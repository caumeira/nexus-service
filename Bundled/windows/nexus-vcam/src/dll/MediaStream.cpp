#include "MediaStream.h"
#include "Guids.h"

namespace
{
constexpr UINT32 kWidth = NEXUS_VCAM_DEFAULT_WIDTH;
constexpr UINT32 kHeight = NEXUS_VCAM_DEFAULT_HEIGHT;
constexpr UINT32 kFpsNumerator = NEXUS_VCAM_DEFAULT_FPS;
constexpr UINT32 kFpsDenominator = 1;
constexpr LONGLONG kFrameDuration100ns = 10000000LL * kFpsDenominator / kFpsNumerator;
constexpr DWORD kAllocatorSampleCount = 10;

HRESULT CreateNv12Type(IMFMediaType** type) noexcept
{
    ComPtr<IMFMediaType> mt;
    NX_RETURN_IF_FAILED(MFCreateMediaType(mt.Put()));
    NX_RETURN_IF_FAILED(mt->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    NX_RETURN_IF_FAILED(mt->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
    NX_RETURN_IF_FAILED(MFSetAttributeSize(mt.Get(), MF_MT_FRAME_SIZE, kWidth, kHeight));
    NX_RETURN_IF_FAILED(mt->SetUINT32(MF_MT_DEFAULT_STRIDE, kWidth));
    NX_RETURN_IF_FAILED(mt->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
    NX_RETURN_IF_FAILED(mt->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
    NX_RETURN_IF_FAILED(MFSetAttributeRatio(mt.Get(), MF_MT_FRAME_RATE, kFpsNumerator, kFpsDenominator));
    NX_RETURN_IF_FAILED(MFSetAttributeRatio(mt.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
    const UINT32 bitrate = kWidth * kHeight * 3 / 2 * 8 * kFpsNumerator;
    NX_RETURN_IF_FAILED(mt->SetUINT32(MF_MT_AVG_BITRATE, bitrate));
    *type = mt.Detach();
    return S_OK;
}

HRESULT CreateRgb32Type(IMFMediaType** type) noexcept
{
    ComPtr<IMFMediaType> mt;
    NX_RETURN_IF_FAILED(MFCreateMediaType(mt.Put()));
    NX_RETURN_IF_FAILED(mt->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    NX_RETURN_IF_FAILED(mt->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32));
    NX_RETURN_IF_FAILED(MFSetAttributeSize(mt.Get(), MF_MT_FRAME_SIZE, kWidth, kHeight));
    NX_RETURN_IF_FAILED(mt->SetUINT32(MF_MT_DEFAULT_STRIDE, kWidth * 4));
    NX_RETURN_IF_FAILED(mt->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
    NX_RETURN_IF_FAILED(mt->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
    NX_RETURN_IF_FAILED(MFSetAttributeRatio(mt.Get(), MF_MT_FRAME_RATE, kFpsNumerator, kFpsDenominator));
    NX_RETURN_IF_FAILED(MFSetAttributeRatio(mt.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
    const UINT32 bitrate = kWidth * kHeight * 4 * 8 * kFpsNumerator;
    NX_RETURN_IF_FAILED(mt->SetUINT32(MF_MT_AVG_BITRATE, bitrate));
    *type = mt.Detach();
    return S_OK;
}
}

HRESULT MediaStream::Create(IMFMediaSource* parent, DWORD index, MediaStream** stream) noexcept
{
    NX_RETURN_HR_IF_NULL(E_POINTER, stream);
    *stream = nullptr;

    ComPtr<MediaStream> instance;
    instance.Attach(new (std::nothrow) MediaStream());
    NX_RETURN_HR_IF(E_OUTOFMEMORY, !instance);
    NX_RETURN_IF_FAILED(instance->Initialize(parent, index));
    *stream = instance.Detach();
    return S_OK;
}

HRESULT MediaStream::Initialize(IMFMediaSource* parent, DWORD index) noexcept
{
    NX_RETURN_HR_IF_NULL(E_POINTER, parent);
    _source = parent;
    _index = index;

    NX_RETURN_IF_FAILED(InitializeAttributes());
    NX_RETURN_IF_FAILED(SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE));
    NX_RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_STREAM_ID, index));
    NX_RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1));
    NX_RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color));

    NX_RETURN_IF_FAILED(MFCreateEventQueue(_queue.Put()));

    // NV12 first: it is the native ring-buffer format, so it wins negotiation
    // when the consumer has no preference.
    ComPtr<IMFMediaType> nv12;
    ComPtr<IMFMediaType> rgb32;
    NX_RETURN_IF_FAILED(CreateNv12Type(nv12.Put()));
    NX_RETURN_IF_FAILED(CreateRgb32Type(rgb32.Put()));

    IMFMediaType* types[] = { nv12.Get(), rgb32.Get() };
    NX_RETURN_IF_FAILED(MFCreateStreamDescriptor(_index, ARRAYSIZE(types), types, _descriptor.Put()));

    ComPtr<IMFMediaTypeHandler> handler;
    NX_RETURN_IF_FAILED(_descriptor->GetMediaTypeHandler(handler.Put()));
    NX_RETURN_IF_FAILED(handler->SetCurrentMediaType(nv12.Get()));

    NX_RETURN_IF_FAILED(_frames.Initialize(kWidth, kHeight));
    return S_OK;
}

// IUnknown
STDMETHODIMP MediaStream::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv)
        return E_POINTER;
    *ppv = nullptr;

    if (riid == IID_IUnknown || riid == __uuidof(IMFMediaEventGenerator) ||
        riid == __uuidof(IMFMediaStream) || riid == __uuidof(IMFMediaStream2))
    {
        *ppv = static_cast<IMFMediaStream2*>(this);
    }
    else if (riid == __uuidof(IMFAttributes))
    {
        *ppv = static_cast<IMFAttributes*>(this);
    }
    else if (riid == __uuidof(IKsControl))
    {
        *ppv = static_cast<IKsControl*>(this);
    }
    else
    {
        return E_NOINTERFACE;
    }

    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) MediaStream::AddRef()
{
    return _refCount.fetch_add(1, std::memory_order_relaxed) + 1;
}

STDMETHODIMP_(ULONG) MediaStream::Release()
{
    const ULONG remaining = _refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
    if (remaining == 0)
        delete this;
    return remaining;
}

// IMFMediaEventGenerator
STDMETHODIMP MediaStream::BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState)
{
    SrwGuard guard(_lock);
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
    return _queue->BeginGetEvent(pCallback, punkState);
}

STDMETHODIMP MediaStream::EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, ppEvent);
    *ppEvent = nullptr;
    SrwGuard guard(_lock);
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
    return _queue->EndGetEvent(pResult, ppEvent);
}

STDMETHODIMP MediaStream::GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, ppEvent);
    *ppEvent = nullptr;

    // Hold only a queue reference across the potentially blocking call,
    // matching the documented event generator pattern.
    ComPtr<IMFMediaEventQueue> queue;
    {
        SrwGuard guard(_lock);
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
        queue = _queue;
    }
    return queue->GetEvent(dwFlags, ppEvent);
}

STDMETHODIMP MediaStream::QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue)
{
    SrwGuard guard(_lock);
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
    return _queue->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue);
}

// IMFMediaStream
STDMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** ppMediaSource)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, ppMediaSource);
    *ppMediaSource = nullptr;
    SrwGuard guard(_lock);
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_source);
    return _source.CopyTo(ppMediaSource);
}

STDMETHODIMP MediaStream::GetStreamDescriptor(IMFStreamDescriptor** ppStreamDescriptor)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, ppStreamDescriptor);
    *ppStreamDescriptor = nullptr;
    SrwGuard guard(_lock);
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_descriptor);
    return _descriptor.CopyTo(ppStreamDescriptor);
}

STDMETHODIMP MediaStream::RequestSample(IUnknown* pToken)
{
    SrwGuard guard(_lock);
    // Only shutdown gates the request, mirroring the verified sample; the
    // pipeline may race RequestSample with state transitions.
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_queue || !_allocator);

    ComPtr<IMFSample> sample;
    NX_RETURN_IF_FAILED(_allocator->AllocateSample(sample.Put()));
    NX_RETURN_IF_FAILED(sample->SetSampleTime(MFGetSystemTime()));
    NX_RETURN_IF_FAILED(sample->SetSampleDuration(kFrameDuration100ns));
    NX_RETURN_IF_FAILED(FillSampleBuffer(sample.Get()));

    if (pToken)
    {
        NX_RETURN_IF_FAILED(sample->SetUnknown(MFSampleExtension_Token, pToken));
    }
    NX_RETURN_IF_FAILED(_queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get()));
    return S_OK;
}

HRESULT MediaStream::FillSampleBuffer(IMFSample* sample) noexcept
{
    ComPtr<IMFMediaBuffer> buffer;
    NX_RETURN_IF_FAILED(sample->GetBufferByIndex(0, buffer.Put()));

    ComPtr<IMF2DBuffer2> buffer2D;
    if (SUCCEEDED(buffer.As(buffer2D.Put())))
    {
        BYTE* scanline0 = nullptr;
        LONG pitch = 0;
        BYTE* start = nullptr;
        DWORD length = 0;
        NX_RETURN_IF_FAILED(buffer2D->Lock2DSize(MF2DBuffer_LockFlags_Write, &scanline0, &pitch, &start, &length));

        const HRESULT hr = (_format == MFVideoFormat_NV12)
            ? _frames.WriteNV12(scanline0, pitch)
            : _frames.WriteRGB32(scanline0, pitch);
        buffer2D->Unlock2D();
        return hr;
    }

    // System-memory buffer without 2D support: tightly packed rows.
    BYTE* data = nullptr;
    DWORD maxLength = 0;
    NX_RETURN_IF_FAILED(buffer->Lock(&data, &maxLength, nullptr));

    HRESULT hr;
    DWORD written;
    if (_format == MFVideoFormat_NV12)
    {
        written = kWidth * kHeight * 3 / 2;
        hr = maxLength >= written ? _frames.WriteNV12(data, kWidth) : E_INVALIDARG;
    }
    else
    {
        written = kWidth * kHeight * 4;
        hr = maxLength >= written ? _frames.WriteRGB32(data, kWidth * 4) : E_INVALIDARG;
    }
    buffer->Unlock();
    NX_RETURN_IF_FAILED(hr);
    return buffer->SetCurrentLength(written);
}

// IMFMediaStream2
STDMETHODIMP MediaStream::SetStreamState(MF_STREAM_STATE value)
{
    SrwGuard guard(_lock);
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
    if (_state == value)
        return S_OK;

    switch (value)
    {
    case MF_STREAM_STATE_PAUSED:
        NX_RETURN_HR_IF(MF_E_INVALID_STATE_TRANSITION, _state != MF_STREAM_STATE_RUNNING);
        _state = value;
        return S_OK;

    case MF_STREAM_STATE_RUNNING:
        return StartLocked(nullptr);

    case MF_STREAM_STATE_STOPPED:
        return StopLocked();

    default:
        return MF_E_INVALID_STATE_TRANSITION;
    }
}

STDMETHODIMP MediaStream::GetStreamState(MF_STREAM_STATE* value)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, value);
    SrwGuard guard(_lock);
    *value = _state;
    return S_OK;
}

// Source-driven control. The parent source serializes these against its own
// lock; the stream lock still guards against pipeline calls on other threads.
HRESULT MediaStream::Start(IMFMediaType* type) noexcept
{
    SrwGuard guard(_lock);
    return StartLocked(type);
}

HRESULT MediaStream::Stop() noexcept
{
    SrwGuard guard(_lock);
    return StopLocked();
}

HRESULT MediaStream::StartLocked(IMFMediaType* type) noexcept
{
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_queue || !_descriptor);

    ComPtr<IMFMediaType> effective(type);
    if (!effective)
    {
        ComPtr<IMFMediaTypeHandler> handler;
        NX_RETURN_IF_FAILED(_descriptor->GetMediaTypeHandler(handler.Put()));
        NX_RETURN_IF_FAILED(handler->GetCurrentMediaType(effective.Put()));
    }
    NX_RETURN_IF_FAILED(effective->GetGUID(MF_MT_SUBTYPE, &_format));

    if (!_allocator)
    {
        // The frame server normally provides one through
        // IMFSampleAllocatorControl before starting; this covers direct
        // in-process activation during development.
        NX_RETURN_IF_FAILED(MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(_allocator.Put())));
    }
    NX_RETURN_IF_FAILED(_allocator->InitializeSampleAllocator(kAllocatorSampleCount, effective.Get()));
    NX_RETURN_IF_FAILED(_queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr));
    _state = MF_STREAM_STATE_RUNNING;
    return S_OK;
}

HRESULT MediaStream::StopLocked() noexcept
{
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

    if (_allocator)
    {
        NX_RETURN_IF_FAILED(_allocator->UninitializeSampleAllocator());
    }
    NX_RETURN_IF_FAILED(_queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr));
    _state = MF_STREAM_STATE_STOPPED;
    return S_OK;
}

void MediaStream::Shutdown() noexcept
{
    SrwGuard guard(_lock);
    if (_queue)
    {
        _queue->Shutdown();
        _queue.Reset();
    }
    _descriptor.Reset();
    _source.Reset();
    _allocator.Reset();
    ResetAttributes();
    _frames.Shutdown();
    _state = MF_STREAM_STATE_STOPPED;
}

HRESULT MediaStream::SetAllocator(IUnknown* allocator) noexcept
{
    NX_RETURN_HR_IF_NULL(E_POINTER, allocator);
    SrwGuard guard(_lock);
    _allocator.Reset();
    return allocator->QueryInterface(IID_PPV_ARGS(_allocator.Put()));
}

MFSampleAllocatorUsage MediaStream::GetAllocatorUsage() const noexcept
{
    return MFSampleAllocatorUsage_UsesProvidedAllocator;
}

HRESULT MediaStream::SetD3DManager(IUnknown* manager) noexcept
{
    NX_RETURN_HR_IF_NULL(E_POINTER, manager);
    // CPU-only source: accept the manager but keep the allocator on system
    // memory so FillSampleBuffer can lock and write directly.
    return S_OK;
}

// IKsControl
STDMETHODIMP_(NTSTATUS) MediaStream::KsProperty(PKSPROPERTY Property, ULONG PropertyLength, LPVOID PropertyData, ULONG DataLength, ULONG* BytesReturned)
{
    UNREFERENCED_PARAMETER(PropertyLength);
    UNREFERENCED_PARAMETER(PropertyData);
    UNREFERENCED_PARAMETER(DataLength);
    NX_RETURN_HR_IF_NULL(E_POINTER, Property);
    NX_RETURN_HR_IF_NULL(E_POINTER, BytesReturned);
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP_(NTSTATUS) MediaStream::KsMethod(PKSMETHOD Method, ULONG MethodLength, LPVOID MethodData, ULONG DataLength, ULONG* BytesReturned)
{
    UNREFERENCED_PARAMETER(MethodLength);
    UNREFERENCED_PARAMETER(MethodData);
    UNREFERENCED_PARAMETER(DataLength);
    NX_RETURN_HR_IF_NULL(E_POINTER, Method);
    NX_RETURN_HR_IF_NULL(E_POINTER, BytesReturned);
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP_(NTSTATUS) MediaStream::KsEvent(PKSEVENT Event, ULONG EventLength, LPVOID EventData, ULONG DataLength, ULONG* BytesReturned)
{
    UNREFERENCED_PARAMETER(Event);
    UNREFERENCED_PARAMETER(EventLength);
    UNREFERENCED_PARAMETER(EventData);
    UNREFERENCED_PARAMETER(DataLength);
    NX_RETURN_HR_IF_NULL(E_POINTER, BytesReturned);
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}
