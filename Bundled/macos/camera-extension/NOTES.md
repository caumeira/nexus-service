# Nexus Camera - CMIO Camera Extension notes

Research + scaffold notes for the phone-as-webcam virtual camera. Everything
below was verified against the macOS 26.5 SDK headers, Apple's "Creating a
camera extension with Core Media I/O" article, and the shipping OBS Studio
`mac-camera-extension` source (plugins/mac-virtualcam).

## 1. Packaging (the verified answer)

Modern camera extensions are **System Extensions**, not app extensions:

- Bundle type: `.systemextension`, Xcode product type
  `com.apple.product-type.system-extension`, `CFBundlePackageType` = `SYSX`.
- There is **no** `NSExtensionPointIdentifier`. The legacy
  `com.apple.cmio-dal-assistant.extension` appex point is the dead DAL path;
  do not use it. The system identifies a camera extension by the
  `CMIOExtension` dictionary in its Info.plist:

  ```xml
  <key>CMIOExtension</key>
  <dict>
      <key>CMIOExtensionMachServiceName</key>
      <string>$(TeamIdentifierPrefix)$(PRODUCT_BUNDLE_IDENTIFIER)</string>
  </dict>
  ```

  The mach service name MUST be prefixed by the team id or by one of the
  extension's app groups, or the DAL assistant refuses to talk to it.
  `$(TeamIdentifierPrefix)` expands only when signing with a real team, so
  `CODE_SIGNING_ALLOWED=NO` builds carry an invalid (unprefixed) name -
  fine for compile checks, not loadable.
- Embed location in the host app: `Nexus.app/Contents/Library/SystemExtensions/
  com.hellonexus.panel.service.camera-extension.systemextension`. The bundle
  directory is conventionally named by its bundle id (OBS and Apple's sample
  both do this; `PRODUCT_NAME` is set to the bundle id for that reason).
- Extension entitlements (required): `com.apple.security.app-sandbox` = true.
  App group `$(TeamIdentifierPrefix)com.hellonexus.panel.service` is included
  so the mach name is valid through either prefix rule and so a future
  XPC/shared-container channel needs no entitlement change.
- Host app entitlement (required to activate):
  `com.apple.developer.system-extension.install` = true. Today `build-app.sh`
  produces an unsigned/ad-hoc bundle and there is no release signing script
  yet (`build-mac` skill stops before signing), so this entitlement work lands
  in the future `release-mac` flow.
- Extension runs as `_cmiodalassistants` user, sandboxed, launched on demand
  by `sysextd`/the CMIO DAL assistant via the mach service. `main.swift` calls
  `CMIOExtensionProvider.startService(provider:)` then `CFRunLoopRun()`.
- Minimum OS: the CMIOExtension framework API is macOS 12.3+; OBS targets
  13.0 and this scaffold matches (sink consume path is solid there).
- Clients see the device through AVFoundation as an external camera. The sink
  stream is invisible to AVFoundation; only CoreMediaIO DAL clients see it.

## 2. Frame ingress design (the OBS pattern, adopted)

The extension publishes TWO streams on the one device:

- `source` stream: what FaceTime/Zoom/etc capture.
- `sink` stream: what the Nexus service feeds.

Extension side (implemented in this scaffold):

- `NexusCameraStreamSink.authorizedToStartStream(for:)` captures the feeding
  `CMIOExtensionClient`. When the sink starts, the device source spins a
  consume timer polling `stream.consumeSampleBuffer(from: client)` at a few
  times the frame rate (CMIOExtensionStream has no push callback; OBS polls
  at 3x frame rate, we mirror that). Each consumed buffer is forwarded to the
  source stream via `stream.send(...)` and acknowledged with
  `notifyScheduledOutputChanged(CMIOExtensionScheduledOutput(sequenceNumber:
  hostTimeInNanoseconds:))`, which fires the client's queueAlteredProc.
- While no sink frames flow, a timer renders the NV12 test pattern
  (scrolling gradient + frame counter) so the camera always produces output.

Client side (what the .NET service P/Invokes - section 5): find the device by
UID, find the sink stream, `CMIOStreamCopyBufferQueue`, `CMIODeviceStartStream`,
then `CMSimpleQueueEnqueue` CMSampleBuffers wrapping NV12 CVPixelBuffers.

## 3. Build (verified working)

```sh
cd nexus-service/Bundled/macos/camera-extension
xcodegen generate
# compile proof, no signing:
xcodebuild -project NexusCameraExtension.xcodeproj -scheme NexusCameraExtension \
    -configuration Release -derivedDataPath build build CODE_SIGNING_ALLOWED=NO
# dev-signed (Apple Development, team 8ZFCKY2SQ9, automatic signing):
xcodebuild -project NexusCameraExtension.xcodeproj -scheme NexusCameraExtension \
    -configuration Release -derivedDataPath build-signed build
```

Both succeed on this machine (Xcode 26.5 / macOS 26.5 SDK). Output:
`build*/Build/Products/Release/com.hellonexus.panel.service.camera-extension.systemextension`
(universal arm64 + x86_64). The signed variant was verified to carry the
team-prefixed mach service name and the sandbox + app-group entitlements.

The generated `NexusCameraExtension.xcodeproj` can be committed (nexus-ios
commits its xcodegen output) or regenerated in CI; `build*/` stays ignored.

## 4. Embedding + activation (NOT yet wired into build-app.sh)

### build-app.sh changes needed (do not apply until the feature lands)

```sh
# after the helper builds:
bash "$SCRIPT_DIR/camera-extension/build-extension.sh" "$APP/Contents/Library/SystemExtensions"
# (script to add: xcodegen + xcodebuild into a temp DerivedData, then copy
#  the .systemextension into the target dir)
```

Signing order at release time (inside-out, Developer ID Application):

```sh
codesign --force --options runtime --timestamp \
    --entitlements camera-extension-release.entitlements \
    -s "Developer ID Application" \
    "$APP/Contents/Library/SystemExtensions/com.hellonexus.panel.service.camera-extension.systemextension"
codesign --force --options runtime --timestamp \
    --entitlements nexus-app.entitlements \
    -s "Developer ID Application" "$APP"
# nexus-app.entitlements must add com.apple.developer.system-extension.install
```

Release extension entitlements = the scaffold's entitlements (sandbox + app
group) WITHOUT `get-task-allow` (that key appears only under Apple
Development signing). Then notarize the whole app as usual.

### Who submits the activation request

`OSSystemExtensionRequest` must be submitted by a process whose main bundle
is the app that contains the extension. OBS submits it from a dylib loaded
into the main OBS process. For Nexus the equivalent topology is a small
Swift dylib (e.g. `camera-extension/activator/` compiled with
`swiftc -emit-library`) exporting C symbols the .NET service P/Invokes,
because the .NET AOT `Nexus` binary at `Contents/MacOS/Nexus` IS the main
app executable, so `Bundle.main` resolves to `Nexus.app` and the requirement
is satisfied in-process. A separate helper executable (audio-helper style)
is riskier: those helpers embed their own `__info_plist`, which gives the
process a different main-bundle identity; whether `sysextd` accepts an
activation from such a sidecar is unverified (open question).

Activation snippet for the activator dylib:

```swift
import Foundation
import SystemExtensions

final class NexusCameraActivator: NSObject, OSSystemExtensionRequestDelegate {
    static let shared = NexusCameraActivator()
    static let extensionIdentifier = "com.hellonexus.panel.service.camera-extension"

    func activate() {
        let request = OSSystemExtensionRequest.activationRequest(
            forExtensionWithIdentifier: Self.extensionIdentifier,
            queue: .main
        )
        request.delegate = self
        OSSystemExtensionManager.shared.submitRequest(request)
    }

    func request(_ request: OSSystemExtensionRequest,
                 actionForReplacingExtension existing: OSSystemExtensionProperties,
                 withExtension ext: OSSystemExtensionProperties)
        -> OSSystemExtensionRequest.ReplacementAction
    {
        // Bump CFBundleVersion on every release or replacement is skipped.
        .replace
    }

    func requestNeedsUserApproval(_ request: OSSystemExtensionRequest) {
        // User must approve in System Settings; surface a hint in the Nexus UI.
        // macOS 15+: General > Login Items & Extensions > Camera.
        // macOS 13/14: Privacy & Security > Security.
    }

    func request(_ request: OSSystemExtensionRequest,
                 didFinishWithResult result: OSSystemExtensionRequest.Result) {
        // .completed or .willCompleteAfterReboot
    }

    func request(_ request: OSSystemExtensionRequest, didFailWithError error: Error) {
        // OSSystemExtensionError.unsupportedParentBundleLocation means the
        // app is not in /Applications (and developer mode is off).
    }
}

@_cdecl("nexus_camera_extension_activate")
public func nexus_camera_extension_activate() {
    NexusCameraActivator.shared.activate()
}
```

Deactivation: `OSSystemExtensionRequest.deactivationRequest(forExtensionWithIdentifier:queue:)`,
same delegate shape. CLI inspection: `systemextensionsctl list`.

## 5. Dev install constraints (from research)

- Normal path: app signed Developer ID + notarized, located in
  `/Applications`, user approves the extension in System Settings. Activation
  from anywhere else fails with `unsupportedParentBundleLocation`.
- `systemextensionsctl developer on` lifts the `/Applications` placement
  requirement and lets extensions be replaced/removed freely during
  iteration. Signing must still chain to Apple (Apple Development works for
  local dev with the team profile); fully unsigned or ad-hoc extensions load
  only with SIP/AMFI disabled (use a VM for that, per common guidance).
- `com.apple.developer.system-extension.install` is not a restricted
  entitlement: Xcode automatic signing handles it for development, and for
  Developer ID distribution the Developer ID provisioning profile must
  allowlist it (create a Developer ID profile with the System Extension
  capability once, before the first notarized release).
- macOS 15+ moved approval to System Settings > General > Login Items &
  Extensions; OBS special-cases its guidance text for 15+ because users could
  not find the toggle.
- The extension's CFBundleVersion must increase for `.replace` upgrades to
  take effect; sysextd keeps both copies staged otherwise.
- Uninstall for end users: deactivation request from the app, or the user
  drags the app to Trash (macOS offers to remove the extension).

## 6. Client-side CoreMediaIO surface for the .NET service (P/Invoke)

All constants verified against the macOS 26.5 SDK headers
(CMIOHardwareObject.h / CMIOHardwareSystem.h / CMIOHardwareDevice.h /
CMIOHardwareStream.h / CMSimpleQueue.h).

Libraries:

```
CoreMediaIO: /System/Library/Frameworks/CoreMediaIO.framework/CoreMediaIO
CoreMedia:   /System/Library/Frameworks/CoreMedia.framework/CoreMedia
CoreVideo:   /System/Library/Frameworks/CoreVideo.framework/CoreVideo
CoreFoundation: /System/Library/Frameworks/CoreFoundation.framework/CoreFoundation
```

Constants:

| Constant | Value | Notes |
|---|---|---|
| `kCMIOObjectSystemObject` | `1` | root object id |
| `kCMIOObjectPropertyScopeGlobal` | `'glob'` = `0x676C6F62` | |
| `kCMIOObjectPropertyElementMain` | `0` | |
| `kCMIOHardwarePropertyDevices` | `'dev#'` = `0x64657623` | array of `CMIOObjectID` (UInt32) |
| `kCMIODevicePropertyDeviceUID` | `'uid '` = `0x75696420` | `CFStringRef`; equals the extension's device UUID string `ECDD7A18-3AAE-4968-869D-D345C62F2EA5` |
| `kCMIODevicePropertyStreams` | `'stm#'` = `0x73746D23` | array of `CMIOStreamID` (UInt32); index 0 = source, index 1 = sink (addStream order). OBS hardcodes index 1 for the sink |
| `kCMIOStreamPropertyDirection` | `'sdir'` = `0x73646972` | UInt32: `0` = output (source), `1` = input (sink); use to find the sink robustly instead of index 1 |
| `kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange` | `'420v'` = `0x34323076` | NV12 video range, matches the extension's only format |
| `kCMSimpleQueueError_QueueIsFull` | `-12773` | drop the frame, do not retry-spin |

Struct:

```csharp
[StructLayout(LayoutKind.Sequential)]
struct CMIOObjectPropertyAddress
{
    public uint Selector; // FourCC
    public uint Scope;    // FourCC
    public uint Element;
}
```

Functions (all return OSStatus as int unless noted):

```csharp
// CoreMediaIO
[DllImport(CoreMediaIO)]
static extern int CMIOObjectGetPropertyDataSize(
    uint objectID, in CMIOObjectPropertyAddress address,
    uint qualifierDataSize, IntPtr qualifierData, out uint dataSize);

[DllImport(CoreMediaIO)]
static extern int CMIOObjectGetPropertyData(
    uint objectID, in CMIOObjectPropertyAddress address,
    uint qualifierDataSize, IntPtr qualifierData,
    uint dataSize, out uint dataUsed, IntPtr data);

[DllImport(CoreMediaIO)]
static extern int CMIODeviceStartStream(uint deviceID, uint streamID);

[DllImport(CoreMediaIO)]
static extern int CMIODeviceStopStream(uint deviceID, uint streamID);

// queueAlteredProc: void (*)(CMIOStreamID, void* token, void* refCon).
// Fired on extension consume acks; pass null to just poll queue depth.
[DllImport(CoreMediaIO)]
static extern int CMIOStreamCopyBufferQueue(
    uint streamID, IntPtr queueAlteredProc, IntPtr refCon, out IntPtr queue);

// CoreMedia
[DllImport(CoreMedia)] static extern int CMSimpleQueueEnqueue(IntPtr queue, IntPtr element);
[DllImport(CoreMedia)] static extern int CMSimpleQueueGetCount(IntPtr queue);
[DllImport(CoreMedia)] static extern int CMSimpleQueueGetCapacity(IntPtr queue);

[DllImport(CoreMedia)]
static extern int CMVideoFormatDescriptionCreate(
    IntPtr allocator, uint codecType, int width, int height,
    IntPtr extensions, out IntPtr formatDescriptionOut);

[StructLayout(LayoutKind.Sequential)]
struct CMTime { public long Value; public int Timescale; public uint Flags; public long Epoch; }

[StructLayout(LayoutKind.Sequential)]
struct CMSampleTimingInfo { public CMTime Duration; public CMTime PresentationTimeStamp; public CMTime DecodeTimeStamp; }

[DllImport(CoreMedia)]
static extern int CMSampleBufferCreateForImageBuffer(
    IntPtr allocator, IntPtr imageBuffer, [MarshalAs(UnmanagedType.I1)] bool dataReady,
    IntPtr makeDataReadyCallback, IntPtr refcon,
    IntPtr formatDescription, in CMSampleTimingInfo sampleTiming,
    out IntPtr sampleBufferOut);

// CoreVideo
[DllImport(CoreVideo)]
static extern int CVPixelBufferCreate(
    IntPtr allocator, nuint width, nuint height, uint pixelFormatType,
    IntPtr pixelBufferAttributes, out IntPtr pixelBufferOut);
// plus: CVPixelBufferLockBaseAddress / UnlockBaseAddress,
// CVPixelBufferGetBaseAddressOfPlane / GetBytesPerRowOfPlane, CVPixelBufferRelease.
// Allocate with kCVPixelBufferIOSurfacePropertiesKey (empty dict) so the
// cross-process hand-off is zero-copy; prefer a CVPixelBufferPool.

// CoreFoundation: CFStringGetCString to read the device UID, CFRelease.
```

Feeding sequence:

1. Enumerate `kCMIOHardwarePropertyDevices` on `kCMIOObjectSystemObject`;
   for each device read `kCMIODevicePropertyDeviceUID` and match
   `ECDD7A18-3AAE-4968-869D-D345C62F2EA5` (case-insensitive compare; the
   string form of the extension's deviceID UUID).
2. Read `kCMIODevicePropertyStreams`; pick the stream whose
   `kCMIOStreamPropertyDirection` is `1` (or index 1, OBS-style).
3. `CMIOStreamCopyBufferQueue(sinkStream, proc, refCon, out queue)` - caller
   owns the returned queue ref.
4. `CMIODeviceStartStream(device, sinkStream)` - this triggers the
   extension's sink `startStream` and the consume pump.
5. Per frame: NV12 CVPixelBuffer (IOSurface-backed) -> copy planes ->
   `CMSampleBufferCreateForImageBuffer` -> if
   `CMSimpleQueueGetCount < CMSimpleQueueGetCapacity` then
   `CMSimpleQueueEnqueue`. **Ownership transfers on successful enqueue: do
   not CFRelease the sample buffer afterwards** (OBS releases only the pixel
   buffer). On `kCMSimpleQueueError_QueueIsFull` release the buffer and drop
   the frame.
6. Teardown: `CMIODeviceStopStream`, `CFRelease(queue)`.

Note: the feeding process needs no camera TCC permission (it produces, not
captures). The device only renders the test pattern while a viewer app has
the source stream open and no sink frames arrive.

## 7. Scaffold inventory

```
camera-extension/
  project.yml                    xcodegen spec (mirrors nexus-ios conventions)
  NexusCameraExtension/
    Info.plist                   CMIOExtension dict + stable device/stream UUIDs
    NexusCameraExtension.entitlements   generated by xcodegen from project.yml
    main.swift                   startService entry
    NexusCameraProviderSource.swift
    NexusCameraDeviceSource.swift       device + format + pattern/sink pumps
    NexusCameraStreamSource.swift       source stream (clients capture this)
    NexusCameraStreamSink.swift         sink stream (service feeds this)
    NexusTestPattern.swift              NV12 gradient + frame counter renderer
  NOTES.md
```

Format: single NV12 (`420v`) 1920x1080 at 30 fps, IOSurface-backed pool,
hostTime clock on both streams (CMIOExtensionStreamClockType.hostTime; the
other clock types are linkedCoreAudioDeviceUID for audio-synced devices and
custom, neither applies here).

## 8. Open questions

- Whether sysextd accepts an activation request from a sidecar helper binary
  whose `__info_plist` gives it a non-app main-bundle identity (section 4);
  the activator-dylib-in-main-process route avoids the question entirely.
- Whether the Developer ID provisioning profile for
  `com.hellonexus.panel.service` needs regenerating with the System
  Extension capability before the first notarized release (expected: yes,
  one-time portal step).
- macOS 26 specifics: the OBS pattern is verified shipping through macOS 15;
  no behavior changes for CMIO extensions were found for 26.x, but first
  activation on this machine should be smoke-tested with developer mode
  before wiring the release path.
- Frame pacing: the extension forwards sink buffers at whatever rate they
  arrive; the service should pace enqueues to the advertised frame duration
  or viewers see judder.
