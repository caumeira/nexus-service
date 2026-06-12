#include "MediaSource.h"
#include "Guids.h"

HRESULT MediaSource::Create(IMFAttributes* activatorAttributes, MediaSource** source) noexcept
{
    NX_RETURN_HR_IF_NULL(E_POINTER, source);
    *source = nullptr;

    ComPtr<MediaSource> instance;
    instance.Attach(new (std::nothrow) MediaSource());
    NX_RETURN_HR_IF(E_OUTOFMEMORY, !instance);
    NX_RETURN_IF_FAILED(instance->Initialize(activatorAttributes));
    *source = instance.Detach();
    return S_OK;
}

HRESULT MediaSource::Initialize(IMFAttributes* activatorAttributes) noexcept
{
    NX_RETURN_IF_FAILED(InitializeAttributes());
    if (activatorAttributes)
    {
        NX_RETURN_IF_FAILED(activatorAttributes->CopyAllItems(_attributes.Get()));
    }

    // Sensor profiles mirror the verified sample; MediaCapture profile
    // queries fail to enumerate the camera without them.
    ComPtr<IMFSensorProfileCollection> collection;
    NX_RETURN_IF_FAILED(MFCreateSensorProfileCollection(collection.Put()));

    const DWORD streamId = 0;
    ComPtr<IMFSensorProfile> profile;
    NX_RETURN_IF_FAILED(MFCreateSensorProfile(KSCAMERAPROFILE_Legacy, 0, nullptr, profile.Put()));
    NX_RETURN_IF_FAILED(profile->AddProfileFilter(streamId, L"((RES==;FRT<=30,1;SUT==))"));
    NX_RETURN_IF_FAILED(collection->AddProfile(profile.Get()));

    NX_RETURN_IF_FAILED(MFCreateSensorProfile(KSCAMERAPROFILE_HighFrameRate, 0, nullptr, profile.Put()));
    NX_RETURN_IF_FAILED(profile->AddProfileFilter(streamId, L"((RES==;FRT>=60,1;SUT==))"));
    NX_RETURN_IF_FAILED(collection->AddProfile(profile.Get()));
    NX_RETURN_IF_FAILED(SetUnknown(MF_DEVICEMFT_SENSORPROFILE_COLLECTION, collection.Get()));

    IMFStreamDescriptor* descriptors[kStreamCount] = {};
    for (DWORD i = 0; i < kStreamCount; i++)
    {
        NX_RETURN_IF_FAILED(MediaStream::Create(this, i, _streams[i].Put()));
        NX_RETURN_IF_FAILED(_streams[i]->GetStreamDescriptor(&descriptors[i]));
    }

    const HRESULT hr = MFCreatePresentationDescriptor(kStreamCount, descriptors, _descriptor.Put());
    for (DWORD i = 0; i < kStreamCount; i++)
    {
        if (descriptors[i])
            descriptors[i]->Release();
    }
    NX_RETURN_IF_FAILED(hr);

    NX_RETURN_IF_FAILED(MFCreateEventQueue(_queue.Put()));
    return S_OK;
}

int MediaSource::GetStreamIndexById(DWORD id) noexcept
{
    for (DWORD i = 0; i < kStreamCount; i++)
    {
        ComPtr<IMFStreamDescriptor> desc;
        if (FAILED(_streams[i]->GetStreamDescriptor(desc.Put())))
            return -1;

        DWORD sid = 0;
        if (FAILED(desc->GetStreamIdentifier(&sid)))
            return -1;

        if (sid == id)
            return static_cast<int>(i);
    }
    return -1;
}

// IUnknown
STDMETHODIMP MediaSource::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv)
        return E_POINTER;
    *ppv = nullptr;

    if (riid == IID_IUnknown || riid == __uuidof(IMFMediaEventGenerator) ||
        riid == __uuidof(IMFMediaSource) || riid == __uuidof(IMFMediaSourceEx))
    {
        *ppv = static_cast<IMFMediaSourceEx*>(this);
    }
    else if (riid == __uuidof(IMFAttributes))
    {
        *ppv = static_cast<IMFAttributes*>(this);
    }
    else if (riid == __uuidof(IMFGetService))
    {
        *ppv = static_cast<IMFGetService*>(this);
    }
    else if (riid == __uuidof(IKsControl))
    {
        *ppv = static_cast<IKsControl*>(this);
    }
    else if (riid == __uuidof(IMFSampleAllocatorControl))
    {
        *ppv = static_cast<IMFSampleAllocatorControl*>(this);
    }
    else
    {
        // The frame server probes several undocumented interfaces here;
        // refusing them is the verified sample behavior.
        return E_NOINTERFACE;
    }

    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) MediaSource::AddRef()
{
    return _refCount.fetch_add(1, std::memory_order_relaxed) + 1;
}

STDMETHODIMP_(ULONG) MediaSource::Release()
{
    const ULONG remaining = _refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
    if (remaining == 0)
        delete this;
    return remaining;
}

// IMFMediaEventGenerator
STDMETHODIMP MediaSource::BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState)
{
    SrwGuard guard(_lock);
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
    return _queue->BeginGetEvent(pCallback, punkState);
}

STDMETHODIMP MediaSource::EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, ppEvent);
    *ppEvent = nullptr;
    SrwGuard guard(_lock);
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
    return _queue->EndGetEvent(pResult, ppEvent);
}

STDMETHODIMP MediaSource::GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, ppEvent);
    *ppEvent = nullptr;

    ComPtr<IMFMediaEventQueue> queue;
    {
        SrwGuard guard(_lock);
        NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
        queue = _queue;
    }
    return queue->GetEvent(dwFlags, ppEvent);
}

STDMETHODIMP MediaSource::QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue)
{
    SrwGuard guard(_lock);
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
    return _queue->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue);
}

// IMFMediaSource
STDMETHODIMP MediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** ppPresentationDescriptor)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, ppPresentationDescriptor);
    *ppPresentationDescriptor = nullptr;
    SrwGuard guard(_lock);
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_descriptor);
    return _descriptor->Clone(ppPresentationDescriptor);
}

STDMETHODIMP MediaSource::GetCharacteristics(DWORD* pdwCharacteristics)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, pdwCharacteristics);
    *pdwCharacteristics = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
}

STDMETHODIMP MediaSource::Pause()
{
    return MF_E_INVALID_STATE_TRANSITION;
}

STDMETHODIMP MediaSource::Shutdown()
{
    NxTrace(L"MediaSource::Shutdown");
    SrwGuard guard(_lock);
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

    _queue->Shutdown();
    _queue.Reset();

    for (DWORD i = 0; i < kStreamCount; i++)
    {
        if (_streams[i])
            _streams[i]->Shutdown();
    }

    _descriptor.Reset();
    ResetAttributes();
    return S_OK;
}

STDMETHODIMP MediaSource::Start(IMFPresentationDescriptor* pPresentationDescriptor, const GUID* pguidTimeFormat, const PROPVARIANT* pvarStartPosition)
{
    NxTrace(L"MediaSource::Start");
    NX_RETURN_HR_IF_NULL(E_POINTER, pPresentationDescriptor);
    NX_RETURN_HR_IF_NULL(E_POINTER, pvarStartPosition);
    NX_RETURN_HR_IF(E_INVALIDARG, pguidTimeFormat && *pguidTimeFormat != GUID_NULL);
    SrwGuard guard(_lock);
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_queue || !_descriptor);

    DWORD count = 0;
    NX_RETURN_IF_FAILED(pPresentationDescriptor->GetStreamDescriptorCount(&count));
    NX_RETURN_HR_IF(E_INVALIDARG, count != kStreamCount);

    PROPVARIANT time;
    NX_RETURN_IF_FAILED(InitPropVariantFromInt64(MFGetSystemTime(), &time));

    for (DWORD i = 0; i < count; i++)
    {
        ComPtr<IMFStreamDescriptor> desc;
        BOOL selected = FALSE;
        NX_RETURN_IF_FAILED(pPresentationDescriptor->GetStreamDescriptorByIndex(i, &selected, desc.Put()));

        DWORD id = 0;
        NX_RETURN_IF_FAILED(desc->GetStreamIdentifier(&id));

        const int index = GetStreamIndexById(id);
        NX_RETURN_HR_IF(E_FAIL, index < 0);

        MF_STREAM_STATE state = MF_STREAM_STATE_STOPPED;
        NX_RETURN_IF_FAILED(_streams[index]->GetStreamState(&state));
        const bool running = state != MF_STREAM_STATE_STOPPED;

        if (selected && !running)
        {
            NX_RETURN_IF_FAILED(_descriptor->SelectStream(index));

            // MENewStream must precede the stream's MEStreamStarted.
            ComPtr<IUnknown> unk;
            NX_RETURN_IF_FAILED(_streams[index]->QueryInterface(IID_PPV_ARGS(unk.Put())));
            NX_RETURN_IF_FAILED(_queue->QueueEventParamUnk(MENewStream, GUID_NULL, S_OK, unk.Get()));

            ComPtr<IMFMediaTypeHandler> handler;
            ComPtr<IMFMediaType> type;
            NX_RETURN_IF_FAILED(desc->GetMediaTypeHandler(handler.Put()));
            NX_RETURN_IF_FAILED(handler->GetCurrentMediaType(type.Put()));
            NX_RETURN_IF_FAILED(_streams[index]->Start(type.Get()));
        }
        else if (!selected && running)
        {
            NX_RETURN_IF_FAILED(_descriptor->DeselectStream(index));
            NX_RETURN_IF_FAILED(_streams[index]->Stop());
        }
    }

    const HRESULT hr = _queue->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &time);
    PropVariantClear(&time);
    return hr;
}

STDMETHODIMP MediaSource::Stop()
{
    NxTrace(L"MediaSource::Stop");
    SrwGuard guard(_lock);
    NX_RETURN_HR_IF(MF_E_SHUTDOWN, !_queue || !_descriptor);

    PROPVARIANT time;
    NX_RETURN_IF_FAILED(InitPropVariantFromInt64(MFGetSystemTime(), &time));

    for (DWORD i = 0; i < kStreamCount; i++)
    {
        NX_RETURN_IF_FAILED(_streams[i]->Stop());
        NX_RETURN_IF_FAILED(_descriptor->DeselectStream(i));
    }

    const HRESULT hr = _queue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, &time);
    PropVariantClear(&time);
    return hr;
}

// IMFMediaSourceEx
STDMETHODIMP MediaSource::GetSourceAttributes(IMFAttributes** ppAttributes)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, ppAttributes);
    *ppAttributes = nullptr;
    SrwGuard guard(_lock);
    return QueryInterface(IID_PPV_ARGS(ppAttributes));
}

STDMETHODIMP MediaSource::GetStreamAttributes(DWORD dwStreamIdentifier, IMFAttributes** ppAttributes)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, ppAttributes);
    *ppAttributes = nullptr;
    SrwGuard guard(_lock);
    NX_RETURN_HR_IF(E_FAIL, dwStreamIdentifier >= kStreamCount);
    return _streams[dwStreamIdentifier]->QueryInterface(IID_PPV_ARGS(ppAttributes));
}

STDMETHODIMP MediaSource::SetD3DManager(IUnknown* pManager)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, pManager);
    SrwGuard guard(_lock);
    for (DWORD i = 0; i < kStreamCount; i++)
    {
        NX_RETURN_IF_FAILED(_streams[i]->SetD3DManager(pManager));
    }
    return S_OK;
}

// IMFGetService
STDMETHODIMP MediaSource::GetService(REFGUID guidService, REFIID riid, LPVOID* ppvObject)
{
    UNREFERENCED_PARAMETER(guidService);
    UNREFERENCED_PARAMETER(riid);
    if (ppvObject)
        *ppvObject = nullptr;
    return MF_E_UNSUPPORTED_SERVICE;
}

// IMFSampleAllocatorControl
STDMETHODIMP MediaSource::SetDefaultAllocator(DWORD dwOutputStreamID, IUnknown* pAllocator)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, pAllocator);
    SrwGuard guard(_lock);

    const int index = GetStreamIndexById(dwOutputStreamID);
    NX_RETURN_HR_IF(E_FAIL, index < 0);
    return _streams[index]->SetAllocator(pAllocator);
}

STDMETHODIMP MediaSource::GetAllocatorUsage(DWORD dwOutputStreamID, DWORD* pdwInputStreamID, MFSampleAllocatorUsage* peUsage)
{
    NX_RETURN_HR_IF_NULL(E_POINTER, pdwInputStreamID);
    NX_RETURN_HR_IF_NULL(E_POINTER, peUsage);
    SrwGuard guard(_lock);

    const int index = GetStreamIndexById(dwOutputStreamID);
    NX_RETURN_HR_IF(E_FAIL, index < 0);
    *pdwInputStreamID = dwOutputStreamID;
    *peUsage = _streams[index]->GetAllocatorUsage();
    return S_OK;
}

// IKsControl
STDMETHODIMP_(NTSTATUS) MediaSource::KsProperty(PKSPROPERTY Property, ULONG PropertyLength, LPVOID PropertyData, ULONG DataLength, ULONG* BytesReturned)
{
    UNREFERENCED_PARAMETER(PropertyLength);
    UNREFERENCED_PARAMETER(PropertyData);
    UNREFERENCED_PARAMETER(DataLength);
    NX_RETURN_HR_IF_NULL(E_POINTER, Property);
    NX_RETURN_HR_IF_NULL(E_POINTER, BytesReturned);
    // No camera control properties exposed yet; the pipeline probes
    // KSPROPSETID_Pin and the VIDCAP sets here.
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP_(NTSTATUS) MediaSource::KsMethod(PKSMETHOD Method, ULONG MethodLength, LPVOID MethodData, ULONG DataLength, ULONG* BytesReturned)
{
    UNREFERENCED_PARAMETER(MethodLength);
    UNREFERENCED_PARAMETER(MethodData);
    UNREFERENCED_PARAMETER(DataLength);
    NX_RETURN_HR_IF_NULL(E_POINTER, Method);
    NX_RETURN_HR_IF_NULL(E_POINTER, BytesReturned);
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP_(NTSTATUS) MediaSource::KsEvent(PKSEVENT Event, ULONG EventLength, LPVOID EventData, ULONG DataLength, ULONG* BytesReturned)
{
    UNREFERENCED_PARAMETER(Event);
    UNREFERENCED_PARAMETER(EventLength);
    UNREFERENCED_PARAMETER(EventData);
    UNREFERENCED_PARAMETER(DataLength);
    NX_RETURN_HR_IF_NULL(E_POINTER, BytesReturned);
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}
