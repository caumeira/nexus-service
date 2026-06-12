// Virtual camera media source. Interface set and event sequencing mirror
// smourier/VCamSample's MediaSource, which the Windows 11 frame server is
// known to load and stream correctly.

#pragma once

#include "Framework.h"
#include "AttributesBase.h"
#include "MediaStream.h"

class MediaSource final : public AttributesBase<IMFAttributes>,
                          public IMFMediaSourceEx,
                          public IMFGetService,
                          public IKsControl,
                          public IMFSampleAllocatorControl
{
public:
    static HRESULT Create(IMFAttributes* activatorAttributes, MediaSource** source) noexcept;

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IMFMediaEventGenerator
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState) override;
    STDMETHODIMP EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent) override;
    STDMETHODIMP GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent) override;
    STDMETHODIMP QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue) override;

    // IMFMediaSource
    STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** ppPresentationDescriptor) override;
    STDMETHODIMP GetCharacteristics(DWORD* pdwCharacteristics) override;
    STDMETHODIMP Pause() override;
    STDMETHODIMP Shutdown() override;
    STDMETHODIMP Start(IMFPresentationDescriptor* pPresentationDescriptor, const GUID* pguidTimeFormat, const PROPVARIANT* pvarStartPosition) override;
    STDMETHODIMP Stop() override;

    // IMFMediaSourceEx
    STDMETHODIMP GetSourceAttributes(IMFAttributes** ppAttributes) override;
    STDMETHODIMP GetStreamAttributes(DWORD dwStreamIdentifier, IMFAttributes** ppAttributes) override;
    STDMETHODIMP SetD3DManager(IUnknown* pManager) override;

    // IMFGetService
    STDMETHODIMP GetService(REFGUID guidService, REFIID riid, LPVOID* ppvObject) override;

    // IMFSampleAllocatorControl
    STDMETHODIMP SetDefaultAllocator(DWORD dwOutputStreamID, IUnknown* pAllocator) override;
    STDMETHODIMP GetAllocatorUsage(DWORD dwOutputStreamID, DWORD* pdwInputStreamID, MFSampleAllocatorUsage* peUsage) override;

    // IKsControl
    STDMETHODIMP_(NTSTATUS) KsProperty(PKSPROPERTY Property, ULONG PropertyLength, LPVOID PropertyData, ULONG DataLength, ULONG* BytesReturned) override;
    STDMETHODIMP_(NTSTATUS) KsMethod(PKSMETHOD Method, ULONG MethodLength, LPVOID MethodData, ULONG DataLength, ULONG* BytesReturned) override;
    STDMETHODIMP_(NTSTATUS) KsEvent(PKSEVENT Event, ULONG EventLength, LPVOID EventData, ULONG DataLength, ULONG* BytesReturned) override;

private:
    MediaSource() noexcept { NxModuleAddRef(); }
    ~MediaSource() { NxModuleRelease(); }

    HRESULT Initialize(IMFAttributes* activatorAttributes) noexcept;
    int GetStreamIndexById(DWORD id) noexcept;

    static constexpr DWORD kStreamCount = 1;

    std::atomic<ULONG> _refCount{ 1 };
    SrwLock _lock;
    ComPtr<MediaStream> _streams[kStreamCount];
    ComPtr<IMFMediaEventQueue> _queue;
    ComPtr<IMFPresentationDescriptor> _descriptor;
};
