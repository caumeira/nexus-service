// COM entry points. The frame server CoCreates CLSID_NexusVCam from the HKLM
// registration, receives the Activator via the class factory, and activates
// the media source through IMFActivate.

#include "Framework.h"
#include "Activator.h"
#include "Guids.h"

std::atomic<long> g_moduleLock{ 0 };
static HMODULE g_module = nullptr;

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    UNREFERENCED_PARAMETER(reserved);
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        g_module = module;
        DisableThreadLibraryCalls(module);
        NxTrace(L"DllMain attach");
        break;

    case DLL_PROCESS_DETACH:
        NxTrace(L"DllMain detach");
        break;
    }
    return TRUE;
}

namespace
{
class ClassFactory final : public IClassFactory
{
public:
    ClassFactory() noexcept { NxModuleAddRef(); }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv)
            return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override
    {
        return _refCount.fetch_add(1, std::memory_order_relaxed) + 1;
    }

    STDMETHODIMP_(ULONG) Release() override
    {
        const ULONG remaining = _refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
        if (remaining == 0)
            delete this;
        return remaining;
    }

    // IClassFactory
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        NX_RETURN_HR_IF_NULL(E_POINTER, ppv);
        *ppv = nullptr;
        NX_RETURN_HR_IF(CLASS_E_NOAGGREGATION, outer != nullptr);

        ComPtr<Activator> activator;
        NX_RETURN_IF_FAILED(Activator::Create(activator.Put()));
        return activator->QueryInterface(riid, ppv);
    }

    STDMETHODIMP LockServer(BOOL lock) override
    {
        if (lock)
            NxModuleAddRef();
        else
            NxModuleRelease();
        return S_OK;
    }

private:
    ~ClassFactory() { NxModuleRelease(); }

    std::atomic<ULONG> _refCount{ 1 };
};

HRESULT WriteRegistryValue(HKEY key, PCWSTR name, PCWSTR value) noexcept
{
    const DWORD bytes = static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t));
    const LSTATUS status = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value), bytes);
    return HRESULT_FROM_WIN32(status);
}
}

__control_entrypoint(DllExport)
STDAPI DllCanUnloadNow()
{
    return g_moduleLock.load(std::memory_order_acquire) == 0 ? S_OK : S_FALSE;
}

_Check_return_
STDAPI DllGetClassObject(_In_ REFCLSID rclsid, _In_ REFIID riid, _Outptr_ LPVOID FAR* ppv)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, ppv);
    *ppv = nullptr;
    NX_RETURN_HR_IF(CLASS_E_CLASSNOTAVAILABLE, rclsid != CLSID_NexusVCam);

    ComPtr<ClassFactory> factory;
    factory.Attach(new (std::nothrow) ClassFactory());
    NX_RETURN_HR_IF(E_OUTOFMEMORY, !factory);
    return factory->QueryInterface(riid, ppv);
}

// HKLM is mandatory: the frame server services resolve the CLSID from the
// machine hive only. HKCU registration is silently ignored.
STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    const DWORD length = GetModuleFileNameW(g_module, path, ARRAYSIZE(path));
    NX_RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER), length == 0 || length == ARRAYSIZE(path));

    wchar_t keyPath[256];
    swprintf_s(keyPath, L"Software\\Classes\\CLSID\\%s\\InprocServer32", NEXUS_VCAM_CLSID_STRING);

    HKEY key = nullptr;
    LSTATUS status = RegCreateKeyExW(HKEY_LOCAL_MACHINE, keyPath, 0, nullptr, REG_OPTION_NON_VOLATILE,
                                     KEY_WRITE, nullptr, &key, nullptr);
    NX_RETURN_HR_IF(HRESULT_FROM_WIN32(status), status != ERROR_SUCCESS);

    HRESULT hr = WriteRegistryValue(key, nullptr, path);
    if (SUCCEEDED(hr))
        hr = WriteRegistryValue(key, L"ThreadingModel", L"Both");

    RegCloseKey(key);
    return hr;
}

STDAPI DllUnregisterServer()
{
    wchar_t keyPath[256];
    swprintf_s(keyPath, L"Software\\Classes\\CLSID\\%s", NEXUS_VCAM_CLSID_STRING);
    const LSTATUS status = RegDeleteTreeW(HKEY_LOCAL_MACHINE, keyPath);
    if (status == ERROR_FILE_NOT_FOUND)
        return S_OK;
    return HRESULT_FROM_WIN32(status);
}
