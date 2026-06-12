// Console test harness for the Nexus virtual camera.
//
//   NexusVCamHost -register [dllPath]    write HKLM CLSID keys via the DLL (admin)
//   NexusVCamHost -unregister [dllPath]  remove them (admin)
//   NexusVCamHost -run [-allusers]       create + start the vcam, pump until Ctrl+C
//   NexusVCamHost -feed                  produce a colored moving pattern into the
//                                        shared-memory ring until Ctrl+C (elevated)
//
// Verification flow: -register once, -run in one console, open the Windows
// Camera app and pick "Nexus Camera". A gray moving gradient means the media
// source is on its internal fallback; start -feed in a second elevated
// console and the image must switch to colored moving bars, proving the
// cross-process shared-memory path end to end.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfvirtualcamera.h>
#include <cstdint>
#include <cstdio>
#include <cstring>

#include "../shared/NexusVCamProtocol.h"
#include "Producer.h"

namespace
{
// Keep in sync with src/dll/Guids.h.
constexpr wchar_t kClsidString[] = L"{85867876-6949-4489-B9DA-7D719F81B50F}";
constexpr wchar_t kFriendlyName[] = L"Nexus Camera";

HANDLE g_stopEvent = nullptr;

BOOL WINAPI ConsoleCtrlHandler(DWORD type)
{
    switch (type)
    {
    case CTRL_C_EVENT:
    case CTRL_BREAK_EVENT:
    case CTRL_CLOSE_EVENT:
        SetEvent(g_stopEvent);
        return TRUE;
    default:
        return FALSE;
    }
}

int RunRegistration(const wchar_t* dllPathArg, bool registerIt)
{
    wchar_t dllPath[MAX_PATH];
    if (dllPathArg)
    {
        wcscpy_s(dllPath, dllPathArg);
    }
    else
    {
        // Default to NexusVCam.dll beside this executable.
        if (!GetModuleFileNameW(nullptr, dllPath, ARRAYSIZE(dllPath)))
        {
            fwprintf(stderr, L"GetModuleFileName failed, error %lu\n", GetLastError());
            return 1;
        }
        wchar_t* lastSlash = wcsrchr(dllPath, L'\\');
        if (!lastSlash)
            return 1;
        *(lastSlash + 1) = L'\0';
        wcscat_s(dllPath, L"NexusVCam.dll");
    }

    HMODULE module = LoadLibraryW(dllPath);
    if (!module)
    {
        fwprintf(stderr, L"LoadLibrary('%s') failed, error %lu\n", dllPath, GetLastError());
        return 1;
    }

    using RegisterFn = HRESULT(__stdcall*)();
    const char* entry = registerIt ? "DllRegisterServer" : "DllUnregisterServer";
    auto fn = reinterpret_cast<RegisterFn>(GetProcAddress(module, entry));
    if (!fn)
    {
        fwprintf(stderr, L"%hs not exported by '%s'\n", entry, dllPath);
        FreeLibrary(module);
        return 1;
    }

    const HRESULT hr = fn();
    FreeLibrary(module);
    if (FAILED(hr))
    {
        fwprintf(stderr, L"%hs failed: 0x%08X (run elevated; keys live under HKLM)\n", entry, hr);
        return 1;
    }
    wprintf(L"%hs ok for '%s'\n", entry, dllPath);
    return 0;
}

int RunCamera(bool allUsers)
{
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr))
    {
        fwprintf(stderr, L"CoInitializeEx failed: 0x%08X\n", hr);
        return 1;
    }

    hr = MFStartup(MF_VERSION);
    if (FAILED(hr))
    {
        fwprintf(stderr, L"MFStartup failed: 0x%08X\n", hr);
        CoUninitialize();
        return 1;
    }

    int exitCode = 0;
    IMFVirtualCamera* camera = nullptr;
    hr = MFCreateVirtualCamera(
        MFVirtualCameraType_SoftwareCameraSource,
        MFVirtualCameraLifetime_Session,
        allUsers ? MFVirtualCameraAccess_AllUsers : MFVirtualCameraAccess_CurrentUser,
        kFriendlyName,
        kClsidString,
        nullptr,
        0,
        &camera);
    if (FAILED(hr))
    {
        fwprintf(stderr, L"MFCreateVirtualCamera failed: 0x%08X\n", hr);
        fwprintf(stderr, L"Checklist: DLL registered under HKLM? webcam privacy allowed? AllUsers needs admin.\n");
        exitCode = 1;
    }
    else
    {
        hr = camera->Start(nullptr);
        if (FAILED(hr))
        {
            fwprintf(stderr, L"IMFVirtualCamera::Start failed: 0x%08X\n", hr);
            fwprintf(stderr, L"The media source loads inside the FrameServer service; check its access to the DLL path.\n");
            exitCode = 1;
        }
        else
        {
            wprintf(L"'%s' is live (%s). Open the Windows Camera app to view it.\n",
                    kFriendlyName, allUsers ? L"all users" : L"current user");
            wprintf(L"Ctrl+C removes the camera and exits.\n");
            WaitForSingleObject(g_stopEvent, INFINITE);
        }

        // Remove, not Shutdown: Shutdown-then-Remove double-shuts the media
        // source and leaves the camera registered.
        const HRESULT removeHr = camera->Remove();
        wprintf(L"Remove: 0x%08X\n", removeHr);
        camera->Release();
    }

    MFShutdown();
    CoUninitialize();
    return exitCode;
}

// BT.601 75 percent color bars, limited range.
struct YuvColor { uint8_t y, u, v; };
constexpr YuvColor kBars[] = {
    { 180, 128, 128 }, // white
    { 162,  44, 142 }, // yellow
    { 131, 156,  44 }, // cyan
    { 112,  72,  58 }, // green
    {  84, 184, 198 }, // magenta
    {  65, 100, 212 }, // red
    {  35, 212, 114 }, // blue
    {  16, 128, 128 }, // black
};

void FillBars(uint8_t* frame, uint32_t width, uint32_t height, uint32_t strideY, uint64_t frameIndex)
{
    uint8_t* yPlane = frame;
    uint8_t* uvPlane = frame + static_cast<size_t>(strideY) * height;

    const uint32_t barCount = ARRAYSIZE(kBars);
    const uint32_t barWidth = width / barCount;
    const uint32_t scroll = static_cast<uint32_t>((frameIndex * 8) % width);

    for (uint32_t row = 0; row < height; row++)
    {
        uint8_t* yLine = yPlane + static_cast<size_t>(row) * strideY;
        for (uint32_t col = 0; col < width; col++)
        {
            const uint32_t shifted = (col + scroll) % width;
            yLine[col] = kBars[(shifted / barWidth) % barCount].y;
        }
    }

    for (uint32_t row = 0; row < height / 2; row++)
    {
        uint8_t* uvLine = uvPlane + static_cast<size_t>(row) * strideY;
        for (uint32_t col = 0; col < width; col += 2)
        {
            const uint32_t shifted = (col + scroll) % width;
            const YuvColor& bar = kBars[(shifted / barWidth) % barCount];
            uvLine[col] = bar.u;
            uvLine[col + 1] = bar.v;
        }
    }
}

int RunFeed()
{
    FrameProducer producer;
    if (!producer.Create(NEXUS_VCAM_DEFAULT_WIDTH, NEXUS_VCAM_DEFAULT_HEIGHT))
        return 1;

    HANDLE timer = CreateWaitableTimerW(nullptr, FALSE, nullptr);
    if (!timer)
    {
        fwprintf(stderr, L"CreateWaitableTimer failed, error %lu\n", GetLastError());
        return 1;
    }

    LARGE_INTEGER due;
    due.QuadPart = 0;
    const LONG periodMs = 1000 / NEXUS_VCAM_DEFAULT_FPS;
    if (!SetWaitableTimer(timer, &due, periodMs, nullptr, nullptr, FALSE))
    {
        fwprintf(stderr, L"SetWaitableTimer failed, error %lu\n", GetLastError());
        CloseHandle(timer);
        return 1;
    }

    wprintf(L"Feeding colored bars into %s. Ctrl+C stops.\n", NEXUS_VCAM_SHMEM_NAME);

    uint64_t frameIndex = 0;
    HANDLE waits[] = { g_stopEvent, timer };
    for (;;)
    {
        const DWORD which = WaitForMultipleObjects(ARRAYSIZE(waits), waits, FALSE, INFINITE);
        if (which != WAIT_OBJECT_0 + 1)
            break;

        uint8_t* slot = producer.BeginFrame();
        if (!slot)
            break;
        FillBars(slot, NEXUS_VCAM_DEFAULT_WIDTH, NEXUS_VCAM_DEFAULT_HEIGHT,
                 NEXUS_VCAM_DEFAULT_WIDTH, frameIndex++);
        producer.EndFrame();
    }

    CloseHandle(timer);
    wprintf(L"Fed %llu frames.\n", static_cast<unsigned long long>(frameIndex));
    return 0;
}

int Usage()
{
    fwprintf(stderr,
             L"usage: NexusVCamHost -register [dllPath] | -unregister [dllPath] | -run [-allusers] | -feed\n");
    return 2;
}
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2)
        return Usage();

    g_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_stopEvent)
        return 1;
    SetConsoleCtrlHandler(ConsoleCtrlHandler, TRUE);

    const wchar_t* command = argv[1];
    if (_wcsicmp(command, L"-register") == 0)
        return RunRegistration(argc > 2 ? argv[2] : nullptr, true);
    if (_wcsicmp(command, L"-unregister") == 0)
        return RunRegistration(argc > 2 ? argv[2] : nullptr, false);
    if (_wcsicmp(command, L"-run") == 0)
        return RunCamera(argc > 2 && _wcsicmp(argv[2], L"-allusers") == 0);
    if (_wcsicmp(command, L"-feed") == 0)
        return RunFeed();
    return Usage();
}
