// Flat C control surface for the Nexus service. The service is .NET AOT and
// avoids COM interop entirely by P/Invoking these exports; the DLL owns the
// IMFVirtualCamera lifetime. These exports run in the caller's process, not
// the frame server.

#include "Framework.h"
#include "Guids.h"

namespace
{
struct VCamHandle
{
    IMFVirtualCamera* camera;
};
}

extern "C" {

// Creates and starts the session-lifetime virtual camera. allUsers nonzero
// maps to MFVirtualCameraAccess_AllUsers and requires an elevated or
// LocalSystem caller. friendlyName may be null to use the default.
HRESULT __stdcall NexusVCamCreate(const wchar_t* friendlyName, int allUsers, void** handle)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, handle);
    *handle = nullptr;

    NX_RETURN_IF_FAILED(MFStartup(MF_VERSION));

    IMFVirtualCamera* camera = nullptr;
    HRESULT hr = MFCreateVirtualCamera(
        MFVirtualCameraType_SoftwareCameraSource,
        MFVirtualCameraLifetime_Session,
        allUsers ? MFVirtualCameraAccess_AllUsers : MFVirtualCameraAccess_CurrentUser,
        friendlyName ? friendlyName : NEXUS_VCAM_FRIENDLY_NAME,
        NEXUS_VCAM_CLSID_STRING,
        nullptr,
        0,
        &camera);
    if (FAILED(hr))
    {
        NxTrace(L"NexusVCamCreate: MFCreateVirtualCamera hr=0x%08X", hr);
        MFShutdown();
        return hr;
    }

    hr = camera->Start(nullptr);
    if (FAILED(hr))
    {
        NxTrace(L"NexusVCamCreate: Start hr=0x%08X", hr);
        camera->Remove();
        camera->Release();
        MFShutdown();
        return hr;
    }

    auto* result = new (std::nothrow) VCamHandle{ camera };
    if (!result)
    {
        camera->Remove();
        camera->Release();
        MFShutdown();
        return E_OUTOFMEMORY;
    }

    *handle = result;
    return S_OK;
}

// Removes the camera from the system and releases it. Remove (not Shutdown)
// is the correct teardown: calling Shutdown first double-shuts the media
// source and the camera fails to unregister.
HRESULT __stdcall NexusVCamDestroy(void* handle)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, handle);
    auto* vcam = static_cast<VCamHandle*>(handle);

    HRESULT hr = S_OK;
    if (vcam->camera)
    {
        hr = vcam->camera->Remove();
        vcam->camera->Release();
    }
    delete vcam;
    MFShutdown();
    return hr;
}

} // extern "C"
