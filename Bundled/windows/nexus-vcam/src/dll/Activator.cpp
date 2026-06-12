#include "Activator.h"
#include "Guids.h"

HRESULT Activator::Create(Activator** activator) noexcept
{
    NX_RETURN_HR_IF_NULL(E_POINTER, activator);
    *activator = nullptr;

    ComPtr<Activator> instance;
    instance.Attach(new (std::nothrow) Activator());
    NX_RETURN_HR_IF(E_OUTOFMEMORY, !instance);
    NX_RETURN_IF_FAILED(instance->Initialize());
    *activator = instance.Detach();
    return S_OK;
}

HRESULT Activator::Initialize() noexcept
{
    NX_RETURN_IF_FAILED(InitializeAttributes());
    NX_RETURN_IF_FAILED(SetUINT32(MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES, 1));
    NX_RETURN_IF_FAILED(SetGUID(MFT_TRANSFORM_CLSID_Attribute, CLSID_NexusVCam));
    NX_RETURN_IF_FAILED(MediaSource::Create(static_cast<IMFAttributes*>(this), _source.Put()));
    return S_OK;
}

// IUnknown
STDMETHODIMP Activator::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv)
        return E_POINTER;
    *ppv = nullptr;

    if (riid == IID_IUnknown || riid == __uuidof(IMFAttributes) || riid == __uuidof(IMFActivate))
    {
        *ppv = static_cast<IMFActivate*>(this);
    }
    else
    {
        return E_NOINTERFACE;
    }

    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) Activator::AddRef()
{
    return _refCount.fetch_add(1, std::memory_order_relaxed) + 1;
}

STDMETHODIMP_(ULONG) Activator::Release()
{
    const ULONG remaining = _refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
    if (remaining == 0)
        delete this;
    return remaining;
}

// IMFActivate
STDMETHODIMP Activator::ActivateObject(REFIID riid, void** ppv)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, ppv);
    *ppv = nullptr;
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_source);
    return _source->QueryInterface(riid, ppv);
}

STDMETHODIMP Activator::ShutdownObject()
{
    return S_OK;
}

STDMETHODIMP Activator::DetachObject()
{
    _source.Reset();
    return S_OK;
}
