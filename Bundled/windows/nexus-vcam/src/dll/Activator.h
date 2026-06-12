// IMFActivate returned to the frame server by the class factory. The frame
// server activates the media source through this object rather than QIing
// the source directly.

#pragma once

#include "Framework.h"
#include "AttributesBase.h"
#include "MediaSource.h"

class Activator final : public AttributesBase<IMFActivate>
{
public:
    static HRESULT Create(Activator** activator) noexcept;

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IMFActivate
    STDMETHODIMP ActivateObject(REFIID riid, void** ppv) override;
    STDMETHODIMP ShutdownObject() override;
    STDMETHODIMP DetachObject() override;

private:
    Activator() noexcept { NxModuleAddRef(); }
    ~Activator() { NxModuleRelease(); }

    HRESULT Initialize() noexcept;

    std::atomic<ULONG> _refCount{ 1 };
    ComPtr<MediaSource> _source;
};
