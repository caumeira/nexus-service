// Single video stream of the virtual camera. Event-queue protocol, stream
// descriptor layout, and allocator usage mirror smourier/VCamSample's
// MediaStream, with the frame pixels supplied by FrameSource instead of a
// Direct2D generator.

#pragma once

#include "Framework.h"
#include "AttributesBase.h"
#include "FrameSource.h"

class MediaSource;

class MediaStream final : public AttributesBase<IMFAttributes>,
                          public IMFMediaStream2,
                          public IKsControl
{
public:
    static HRESULT Create(IMFMediaSource* parent, DWORD index, MediaStream** stream) noexcept;

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IMFMediaEventGenerator
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState) override;
    STDMETHODIMP EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent) override;
    STDMETHODIMP GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent) override;
    STDMETHODIMP QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue) override;

    // IMFMediaStream
    STDMETHODIMP GetMediaSource(IMFMediaSource** ppMediaSource) override;
    STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** ppStreamDescriptor) override;
    STDMETHODIMP RequestSample(IUnknown* pToken) override;

    // IMFMediaStream2
    STDMETHODIMP SetStreamState(MF_STREAM_STATE value) override;
    STDMETHODIMP GetStreamState(MF_STREAM_STATE* value) override;

    // IKsControl
    STDMETHODIMP_(NTSTATUS) KsProperty(PKSPROPERTY Property, ULONG PropertyLength, LPVOID PropertyData, ULONG DataLength, ULONG* BytesReturned) override;
    STDMETHODIMP_(NTSTATUS) KsMethod(PKSMETHOD Method, ULONG MethodLength, LPVOID MethodData, ULONG DataLength, ULONG* BytesReturned) override;
    STDMETHODIMP_(NTSTATUS) KsEvent(PKSEVENT Event, ULONG EventLength, LPVOID EventData, ULONG DataLength, ULONG* BytesReturned) override;

    // Called by MediaSource with its own lock held.
    HRESULT Start(IMFMediaType* type) noexcept;
    HRESULT Stop() noexcept;
    void Shutdown() noexcept;
    HRESULT SetAllocator(IUnknown* allocator) noexcept;
    MFSampleAllocatorUsage GetAllocatorUsage() const noexcept;
    HRESULT SetD3DManager(IUnknown* manager) noexcept;

private:
    MediaStream() noexcept { NxModuleAddRef(); }
    ~MediaStream() { NxModuleRelease(); }

    HRESULT Initialize(IMFMediaSource* parent, DWORD index) noexcept;
    HRESULT StartLocked(IMFMediaType* type) noexcept;
    HRESULT StopLocked() noexcept;
    HRESULT FillSampleBuffer(IMFSample* sample) noexcept;

    std::atomic<ULONG> _refCount{ 1 };
    SrwLock _lock;
    DWORD _index = 0;
    MF_STREAM_STATE _state = MF_STREAM_STATE_STOPPED;
    GUID _format = GUID_NULL;
    FrameSource _frames;
    ComPtr<IMFStreamDescriptor> _descriptor;
    ComPtr<IMFMediaEventQueue> _queue;
    ComPtr<IMFMediaSource> _source;
    ComPtr<IMFVideoSampleAllocatorEx> _allocator;
};
