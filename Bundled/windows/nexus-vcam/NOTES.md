# Nexus Camera - Windows 11 MF Virtual Camera

Scaffold for the "Nexus Camera" system camera exposed by the Nexus service.
Media Foundation virtual camera only (Windows 11, `MFCreateVirtualCamera`).
The Win10/DirectShow softcam is a later phase; it should land as a `dshow/`
sibling of this directory.

Reference implementation mirrored: smourier/VCamSample
(https://github.com/smourier/VCamSample) for the COM/MF plumbing (activator,
event-queue protocol, stream descriptor + allocator usage, registration), and
the shared-memory ingress pattern validated by OneLimeStudio/BestCam (Global
section + NV12 + monotonic frame counter into the FrameServer). The Microsoft
Windows-Camera `VirtualCamera` sample documents the same process model.

## Architecture (verified)

```
Nexus service (LocalSystem, session 0)            FrameServer service
  NexusVCamCreate / MFCreateVirtualCamera   --->  svchost.exe (NT AUTHORITY\LocalService)
  produces NV12 frames into shared memory         CoCreates CLSID_NexusVCam from HKLM
                                                  Activator -> MediaSource -> MediaStream
        Global\NexusVCam.Frames (ring)     --->   FrameSource reads latest published slot
                                                  RequestSample -> MEMediaSample @30fps
                                                          |
                                                          v
                                            Camera app / Teams / OBS (MF consumers)
```

Facts verified against current docs and the samples:

- The media source COM object is CoCreated inside the **Windows Camera Frame
  Server** service, not in the process that calls `MFCreateVirtualCamera`.
  At least four processes load the DLL: the control process, FrameServer,
  FrameServerMonitor, and reader apps (attribute probing only; streaming and
  `RequestSample` happen in FrameServer).
- Service identities on Windows 11: **FrameServer = NT AUTHORITY\LocalService**,
  **FrameServerMonitor = LocalSystem**. (Win10 ran FrameServer as LocalSystem,
  irrelevant here since MFCreateVirtualCamera is Win11+.)
- Consequence 1: the DLL file itself must be readable by LocalService and
  LocalSystem. A user-profile directory breaks activation with access denied;
  `C:\Program Files\Nexus\...` is fine.
- Consequence 2 (frame ingress ACL): the shared-memory section must grant
  read to LocalService. SDDL used by both sides
  (`NEXUS_VCAM_SHMEM_SDDL` in `src/shared/NexusVCamProtocol.h`):
  `D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x80100000;;;LS)` - full for SYSTEM and
  Administrators, GENERIC_READ + SYNCHRONIZE for LocalService. BestCam ships
  a NULL DACL for the same channel; ours is the tightened equivalent. If the
  consumer cannot map the section it silently falls back to the internal test
  pattern, so an ACL mistake degrades to "gray pattern", never to a black
  camera.
- The `Global\` namespace is required (producer in session 0, debug feeder in
  the interactive session). Creating `Global\` objects needs
  SeCreateGlobalPrivilege: any service account has it, interactive `-feed`
  must run elevated.

## Frame ingress protocol

`src/shared/NexusVCamProtocol.h` is the single source of truth (C ABI, also
the contract for the future C# producer). Summary:

- One header page + ring of `NEXUS_VCAM_SLOT_COUNT` NV12 slots,
  1920x1080 default, stride = width.
- Per-slot seqlock (`slotSeq[]`, odd while writing) + monotonic
  `frameCounter` + `latestSlot`. Consumer copies the newest complete slot
  into a double buffer and keeps the previous frame on any torn read.
- `producerTickMs` heartbeat; consumer treats the producer as gone after
  `NEXUS_VCAM_STALE_MS` and resumes the fallback pattern. Producer death
  therefore needs no extra cleanup path.
- `Global\NexusVCam.FrameReady` auto-reset event is signaled per publish.
  The MF side does not wait on it (RequestSample is pull at the negotiated
  rate); it exists as a wake hint for a future low-latency path.

## Lifetime (decision: Session, created on demand)

- `MFVirtualCameraLifetime_Session`: camera disappears when the returned
  `IMFVirtualCamera` is shut down or released; `System` would persist across
  reboots. The service creates the camera on demand when phone streaming
  starts and removes it when it stops, so Session is correct and leaves
  nothing behind on uninstall.
- Teardown: call `IMFVirtualCamera::Remove()` then `Release()`. Do NOT call
  `Shutdown()` first - the verified sample documents that Shutdown-then-Remove
  double-shuts the media source and the camera fails to unregister.
- `MFCreateVirtualCamera` is keyed on its parameters: calling it again with
  identical parameters reopens the same camera. On service start, create with
  the same parameters and `Remove()` to clear any stale instance left by a
  crash, then create fresh.
- If the control process dies without `Remove()`, the session camera can
  linger until FrameServer notices or is restarted
  (`net stop/start FrameServer`); the reopen-and-remove dance above is the
  mitigation.

## Registration (decision: installer writes HKLM keys; regsvr32 works too)

FrameServer resolves the CLSID from the machine hive only. Required keys:

```
HKLM\Software\Classes\CLSID\{85867876-6949-4489-B9DA-7D719F81B50F}\InprocServer32
    (Default)       REG_SZ  C:\Program Files\Nexus\vcam\NexusVCam.dll
    ThreadingModel  REG_SZ  Both
```

Nothing else is needed (no Implemented Categories, no AppID). The DLL exports
`DllRegisterServer`/`DllUnregisterServer` writing exactly these keys, so
`regsvr32 NexusVCam.dll` (elevated) and `NexusVCamHost -register` are
equivalent to the installer path.

## Build (VS 2022 Build Tools, x64, offline)

Requires a Windows SDK with `mfvirtualcamera.h` (any Win11 SDK, 22000+; the
26100 SDK installed with current Build Tools is fine). No NuGet/vcpkg; the
COM plumbing is plain C++ with explicit IUnknown implementations.

Visual Studio generator:

```
cmake -G "Visual Studio 17 2022" -A x64 -B build
cmake --build build --config Release
```

Ninja (from a "x64 Native Tools Command Prompt" / after `vcvars64.bat`):

```
cmake -G Ninja -DCMAKE_BUILD_TYPE=Release -B build-ninja
cmake --build build-ninja
```

Artifacts: `NexusVCam.dll`, `NexusVCamHost.exe`. Static CRT, so nothing else
ships into the FrameServer process.

## Run + verify on the test PC

1. Copy both artifacts to a machine-readable path, e.g.
   `C:\Program Files\Nexus\vcam\` (NOT a user-profile path).
2. Elevated: `NexusVCamHost -register`.
3. `NexusVCamHost -run` (keep the console open). Expect "is live".
4. Open the Windows Camera app, switch to "Nexus Camera" (the shell may
   suffix the name with "Windows Virtual Camera"). A moving gray gradient
   with a bright sweeping bar = media source streaming via its internal
   fallback.
5. Second elevated console: `NexusVCamHost -feed`. The Camera app image must
   switch to scrolling colored bars within a couple of seconds = external
   producer -> shared memory -> FrameServer path proven. Ctrl+C the feeder
   and it must fall back to gray within ~2s.
6. Chrome/Edge (getUserMedia test pages) should list "Nexus Camera" as well.
   Check OBS too: its capture source is DirectShow based and only sees the
   camera if the Win11 DShow bridge kicks in - record the result either way,
   it scopes the phase-2 dshow/ work.
7. Ctrl+C in the `-run` console removes the camera.

Debugging: traces go to `OutputDebugString` with a `[NexusVCam pid.tid]`
prefix; capture globally with DebugView (enable Capture Global Win32) or
attach to the FrameServer svchost. If activation fails, check the DLL path
ACL first, then Event Viewer "Applications and Services Logs".

## Service integration (C# AOT) - planned P/Invoke surface

Preferred: skip COM interop entirely and use the flat exports baked into the
DLL (AOT-friendly, the DLL owns the `IMFVirtualCamera`):

```csharp
[LibraryImport("NexusVCam.dll", StringMarshalling = StringMarshalling.Utf16)]
private static partial int NexusVCamCreate(string? friendlyName, int allUsers, out nint handle);

[LibraryImport("NexusVCam.dll")]
private static partial int NexusVCamDestroy(nint handle);
```

Raw alternative if the service must own the camera object:

```csharp
// mfsensorgroup.dll, Win11 22000+
[LibraryImport("mfsensorgroup.dll", StringMarshalling = StringMarshalling.Utf16)]
private static partial int MFCreateVirtualCamera(
    int type,       // MFVirtualCameraType_SoftwareCameraSource = 0
    int lifetime,   // Session = 0, System = 1
    int access,     // CurrentUser = 0, AllUsers = 1
    string friendlyName,
    string sourceId, // "{85867876-6949-4489-B9DA-7D719F81B50F}"
    nint categories, // null
    uint categoryCount,
    out nint virtualCamera); // IMFVirtualCamera*, call Start/Remove via vtable
```

`MFStartup`/`MFShutdown` (mfplat.dll) bracket either path. The vtable route
needs hand-rolled slots since built-in COM interop is unavailable under AOT;
that is exactly why the flat exports exist.

Producer side: the service maps `Global\NexusVCam.Frames` with the SDDL above
and mirrors `NexusVCamHeader` via `[StructLayout(LayoutKind.Sequential)]`
(use a fixed int buffer for `slotSeq`). Write protocol = `FrameProducer` in
`src/host/Producer.h`: seqlock odd, pixels, seqlock even, `latestSlot`,
`Interlocked.Increment(frameCounter)`, heartbeat, `SetEvent`.

## Open risks / questions

- **Calling `MFCreateVirtualCamera` from the LocalSystem service (session 0)
  is the biggest unknown.** The API is gated by the Capability Access Manager
  webcam consent; behavior for a session 0 SYSTEM caller is undocumented.
  `CurrentUser` access from SYSTEM would scope the camera to the SYSTEM
  account and likely hide it from the desktop user; `AllUsers` (admin-only,
  SYSTEM qualifies) is the expected correct mode. Must be validated on the
  test PC first (`NexusVCamHost -run -allusers` under `psexec -s` reproduces
  the service context). Fallback if blocked: a tiny per-session helper
  launched in the user session that only calls the create/start API, while
  frames keep flowing from the service via the existing shared memory.
- **DShow-only apps may not see the camera.** MF virtual cameras are visible
  to MF consumers (Camera app, Teams, Chrome/Edge). DirectShow clients (OBS,
  Zoom, older apps) depend on the undocumented Win11 DShow bridge and reports
  are inconsistent; the planned `dshow/` softcam phase covers them properly.
  Verify OBS on the test PC to scope that phase.
- VCamSample reports NV12 negotiation quirks with Teams (preview OK, remote
  side black). We advertise NV12 + RGB32 like the sample; revisit ordering or
  add a converter if Teams misbehaves.
- 1080p over the CPU copy path (ring copy + per-sample copy) is roughly
  150 MB/s at 30fps, fine on the target PC, but the RGB32 conversion path
  doubles that; if profiling shows pressure, move the conversion to the
  producer or to a D3D path later.
- Camera privacy toggle (Settings > Privacy > Camera) denies
  `MFCreateVirtualCamera` with E_ACCESSDENIED; the service must surface that
  state to the UI instead of retrying blindly.
- Resolution is pinned to the protocol default; phone rotation / aspect
  changes should be handled by the service scaling into the fixed ring format
  rather than renegotiating media types mid-stream.
