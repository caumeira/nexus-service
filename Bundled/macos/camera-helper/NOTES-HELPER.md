# Nexus Camera helper + activator notes

Two artifacts feed the CMIO camera extension scaffolded in
`../camera-extension/` (see its NOTES.md for the verified research):

- `nexus-camera-helper` - sidecar binary. Decodes encoded phone frames from
  stdin (VideoToolbox) and enqueues NV12 pixel buffers on the extension's
  sink stream via the CoreMediaIO DAL client API.
- `nexus-camera-activator.dylib` - loaded into the Nexus process via P/Invoke
  to submit OSSystemExtension activation / status requests.

## Build

```sh
./build.sh <output-dir>          # both artifacts; arm64, macOS 13.0 minimum
```

Mirrors the audio/overlay helper builds: plain `swiftc`, ad-hoc codesign, no
Xcode project. The helper embeds `Info.plist` as `__TEXT,__info_plist` for a
stable bundle identity; the activator deliberately does NOT (it must inherit
the Nexus.app main-bundle identity, see below). Verified clean (zero
warnings) with Swift 6.3 / macOS 26.5 SDK targeting `arm64-apple-macos13.0`.

## nexus-camera-helper

### stdin wire protocol

One record per frame, all integers little-endian. This is the WS stream
header from `src/Webcam/WebcamSession.cs` plus an explicit payload length
(byte streams have no message boundaries):

| offset | size | field |
|---|---|---|
| 0 | 1 | version, must be `1` |
| 1 | 1 | flags: bit0 keyframe, bits1-2 codec (`0` h264-annexb, `1` mjpeg) |
| 2 | 2 | width |
| 4 | 2 | height |
| 6 | 4 | timestamp ms |
| 10 | 4 | payload length |
| 14 | n | payload (H.264 Annex-B access unit, or one JPEG) |

Payload length is capped (desync guard); zero-length records are skipped.
The keyframe bit is informational - H.264 keyframes are detected from the
SPS/PPS NALs themselves.

### Behavior

- Startup: locate the extension device by UID (`NexusCameraDeviceUUID` in
  the extension Info.plist), find the stream whose direction property marks
  it as a sink, `CMIOStreamCopyBufferQueue`, `CMIODeviceStartStream`, then
  write a single `READY` line to stdout. The service must wait for `READY`
  before piping frames.
- Decode: H.264 Annex-B is repacked to length-prefixed NALs and decoded with
  a `VTDecompressionSession` built from in-band SPS/PPS (sessions rebuild on
  parameter/dimension change and after media-services resets). MJPEG decodes
  through the same session machinery with a JPEG format description. Both
  paths request NV12 video-range (`420v`) IOSurface-backed output, matching
  the extension's only advertised format. Decoding is synchronous, so stdin
  backpressure paces the pipeline.
- Enqueue: pixel buffers are wrapped in CMSampleBuffers with host-clock PTS
  (the extension streams run on the host time clock) and enqueued; ownership
  transfers to the queue on success. When the queue is full the frame is
  dropped and a rate-limited `FRAME-DROP` line goes to stderr.
- Dimensions pass through from the encoded stream; the extension forwards
  sink sample buffers to its source stream as-is. Its advertised source
  format is fixed (see `nexusCameraWidth`/`Height` in the extension), so the
  service should arm the phone at that resolution; behavior of capture apps
  when fed other dimensions is untested.
- Shutdown: EOF on stdin at a record boundary, SIGTERM, or SIGINT all stop
  the sink stream and exit cleanly.

### Exit codes

| code | meaning |
|---|---|
| 0 | clean EOF / SIGTERM / SIGINT |
| 2 | extension device not found (not installed / not approved) |
| 3 | sink stream not found on the device |
| 4 | sink queue copy or stream start failed |
| 5 | stdin protocol violation (version, codec bits, truncation, oversize) |
| 6 | decoder session creation failed |

## nexus-camera-activator.dylib

C exports (P/Invoke from a worker thread; both BLOCK the calling thread
until a terminal callback, needs-approval, or timeout):

```c
int32_t nexus_camera_extension_activate(void); // ~60s timeout
int32_t nexus_camera_extension_status(void);   // ~15s timeout
```

`activate` return codes: `0` activated, `1` will complete after reboot,
`2` awaiting user approval in System Settings (request stays pending in
sysextd; poll `status` afterwards), `90` timed out, `91` busy (another
request in flight), negative = `OSSystemExtensionError.Code` negated (e.g.
`-3` unsupportedParentBundleLocation means the app is not in /Applications
and developer mode is off, `-4` extension not found in the app bundle),
`-100` non-sysext error.

`status` return codes: `0` enabled, `1` installed awaiting approval,
`2` installed but disabled, `3` not installed, `90` timed out, `91` busy,
negative as above.

### Required bundle layout

`OSSystemExtensionRequest.activationRequest` only succeeds when the calling
process's MAIN bundle contains the extension. The dylib therefore assumes:

```
Nexus.app/
  Contents/MacOS/Nexus                          <- process loading the dylib
  Contents/MacOS/nexus-camera-activator.dylib
  Contents/MacOS/nexus-camera-helper
  Contents/Library/SystemExtensions/
    com.hellonexus.panel.service.camera-extension.systemextension
```

The .NET AOT `Nexus` binary IS the app executable, so `Bundle.main` resolves
to `Nexus.app` and the requirement holds. Never load the activator from a
sidecar helper: helpers embed their own `__info_plist`, which changes the
main-bundle identity (open question in `../camera-extension/NOTES.md`).

## build-app.sh changes needed (NOT yet applied)

When the feature lands, after the existing helper builds:

```sh
bash "$SCRIPT_DIR/camera-helper/build.sh" "$APP/Contents/MacOS"
# plus the extension embed + signing steps from camera-extension/NOTES.md
# (xcodegen/xcodebuild the .systemextension into
#  Contents/Library/SystemExtensions, sign inside-out at release time,
#  add com.apple.developer.system-extension.install to the app entitlements)
```

## Verified / unverified

Verified on this machine (no extension installed):

- Both artifacts compile warning-free; activator exports both symbols.
- Helper exits with the device-not-found code and message when the
  extension is absent.
- Decode path (harness build with the CMIO bottom half stubbed): H.264
  Annex-B and MJPEG streams decode to `420v` IOSurface-backed buffers,
  including mid-stream codec and dimension changes; all protocol-violation
  records exit with the protocol code; clean EOF exits zero.
- `nexus_camera_extension_status` returns not-installed via the real
  OSSystemExtensions request round-trip.

Unverifiable until the extension is signed, installed, and approved:

- Sink discovery/start against a live extension device, real enqueue
  consumption, queue-full drop behavior, and end-to-end latency.
- `nexus_camera_extension_activate` success paths (needs Developer ID or
  `systemextensionsctl developer on` + Apple Development signing).
- Capture-app behavior when sink frame dimensions differ from the
  extension's advertised format.
