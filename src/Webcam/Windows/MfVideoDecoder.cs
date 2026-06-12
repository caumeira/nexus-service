using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Webcam.Windows;

/// <summary>
/// Media Foundation software decode of H.264 Annex-B or MJPEG to tight NV12.
/// Built-in COM interop is unavailable under AOT, so every COM call goes
/// through manual vtable slots via unmanaged function pointers - the same
/// pattern as WasapiCapturer in src/Activity/WasapiLoopbackBeatsProvider.cs.
/// The decoder MFT is located with MFTEnumEx (no hardcoded decoder CLSIDs)
/// and driven with the standard synchronous ProcessInput/ProcessOutput loop;
/// MF_E_TRANSFORM_STREAM_CHANGE renegotiates the output type so mid-stream
/// resolution changes survive. Output subtypes other than NV12 are repacked
/// by Nv12Convert so the ring always receives NV12.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class MfVideoDecoder : IWindowsVideoDecoder
{
    private nint _transform;
    private nint _inputSample;
    private nint _inputBuffer;
    private uint _inputCapacity;
    private nint _outputSample;
    private nint _outputBuffer;
    private uint _outputCapacity;
    private byte[] _decoded = [];
    private int _frameWidth;
    private int _frameHeight;
    private int _visibleWidth;
    private int _visibleHeight;
    private Guid _outputSubtype;
    private bool _providesSamples;
    private uint _outputStreamSize;
    private bool _disposed;

    private MfVideoDecoder(nint transform)
    {
        _transform = transform;
    }

    public static MfVideoDecoder Create(WebcamCodec codec, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        var subtype = codec switch
        {
            WebcamCodec.H264 => FormatH264,
            WebcamCodec.Mjpeg => FormatMjpg,
            _ => throw new NotSupportedException($"no decoder mapping for codec {codec}"),
        };

        // Multithreaded init tolerates RPC_E_CHANGED_MODE like the WASAPI
        // capturer; decode calls may later land on uninitialized pool threads,
        // which the implicit MTA covers for these free-threaded sync MFTs.
        _ = CoInitializeEx(0, CoinitMultithreaded);
        ThrowIfFailed(MFStartup(MfVersion, MfStartupFull), "MFStartup");

        nint transform;
        try
        {
            transform = ActivateDecoder(subtype, codec);
        }
        catch
        {
            MFShutdown();
            throw;
        }

        // The instance owns both the transform and the MFStartup reference.
        var decoder = new MfVideoDecoder(transform);
        try
        {
            decoder.Configure(subtype, width, height);
            return decoder;
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    public bool TryDecode(ReadOnlySpan<byte> payload, uint timestampMs, out DecodedNv12Frame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = default;
        if (payload.IsEmpty)
            return false;

        PrepareInputSample(payload, timestampMs);
        var got = false;
        var hr = ProcessInput(_inputSample);
        if (hr == MfENotAccepting)
        {
            got |= DrainOutputs();
            hr = ProcessInput(_inputSample);
        }
        ThrowIfFailed(hr, "IMFTransform.ProcessInput");
        got |= DrainOutputs();
        if (!got)
            return false;

        frame = new DecodedNv12Frame(
            _decoded.AsMemory(0, Nv12Convert.TightBytes(_visibleWidth, _visibleHeight)), _visibleWidth, _visibleHeight);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseField(ref _inputBuffer);
        ReleaseField(ref _inputSample);
        ReleaseField(ref _outputBuffer);
        ReleaseField(ref _outputSample);
        ReleaseField(ref _transform);
        MFShutdown();
    }

    private void Configure(Guid subtype, int width, int height)
    {
        TrySetLowLatency();
        SetInputMediaType(subtype, width, height);
        NegotiateOutputType();
        ThrowIfFailed(ProcessMessage(MsgNotifyBeginStreaming), "IMFTransform.ProcessMessage(BeginStreaming)");
        ThrowIfFailed(ProcessMessage(MsgNotifyStartOfStream), "IMFTransform.ProcessMessage(StartOfStream)");
    }

    private static nint ActivateDecoder(Guid subtype, WebcamCodec codec)
    {
        var input = new MftRegisterTypeInfo { Major = MediaTypeVideo, Subtype = subtype };
        // Output unfiltered: the preferred-subtype choice happens during
        // output type negotiation, where non-NV12 layouts are still usable.
        ThrowIfFailed(
            MFTEnumEx(in MftCategoryVideoDecoder, EnumSyncMft | EnumSortAndFilter, in input, 0, out var activates, out var count),
            "MFTEnumEx");
        try
        {
            if (count == 0 || activates == 0)
                throw new NotSupportedException($"no system video decoder found for {WebcamSessionManager.CodecName(codec)}");
            var activate = ((nint*)activates)[0];
            var activateObject = (delegate* unmanaged[Stdcall]<nint, in Guid, out nint, int>)Slot(activate, SlotActivateObject);
            ThrowIfFailed(activateObject(activate, in IidIMFTransform, out var transform), "IMFActivate.ActivateObject");
            return transform;
        }
        finally
        {
            for (uint i = 0; i < count; i++)
                Release(((nint*)activates)[i]);
            if (activates != 0)
                CoTaskMemFree(activates);
        }
    }

    private void TrySetLowLatency()
    {
        // Best-effort: not every decoder exposes attributes or the knob.
        var getAttributes = (delegate* unmanaged[Stdcall]<nint, out nint, int>)Slot(_transform, SlotGetAttributes);
        if (getAttributes(_transform, out var attributes) < 0 || attributes == 0)
            return;
        _ = SetUInt32(attributes, in MfLowLatency, 1);
        Release(attributes);
    }

    private void SetInputMediaType(Guid subtype, int width, int height)
    {
        ThrowIfFailed(MFCreateMediaType(out var type), "MFCreateMediaType");
        try
        {
            ThrowIfFailed(SetGuid(type, in MfMtMajorType, in MediaTypeVideo), "IMFMediaType.SetGUID(MajorType)");
            ThrowIfFailed(SetGuid(type, in MfMtSubtype, in subtype), "IMFMediaType.SetGUID(Subtype)");
            ThrowIfFailed(SetUInt64(type, in MfMtFrameSize, ((ulong)(uint)width << 32) | (uint)height), "IMFMediaType.SetUINT64(FrameSize)");
            ThrowIfFailed(SetUInt32(type, in MfMtInterlaceMode, InterlaceProgressive), "IMFMediaType.SetUINT32(InterlaceMode)");
            ThrowIfFailed(SetUInt64(type, in MfMtFrameRate, ((ulong)NominalFps << 32) | 1), "IMFMediaType.SetUINT64(FrameRate)");
            var setInputType = (delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(_transform, SlotSetInputType);
            ThrowIfFailed(setInputType(_transform, 0, type, 0), "IMFTransform.SetInputType");
        }
        finally
        {
            Release(type);
        }
    }

    private void NegotiateOutputType()
    {
        var getOutputAvailableType = (delegate* unmanaged[Stdcall]<nint, uint, uint, out nint, int>)Slot(_transform, SlotGetOutputAvailableType);
        nint best = 0;
        var bestRank = int.MaxValue;
        var bestSubtype = default(Guid);
        try
        {
            for (uint i = 0; ; i++)
            {
                var hr = getOutputAvailableType(_transform, 0, i, out var type);
                if (hr == MfENoMoreTypes)
                    break;
                ThrowIfFailed(hr, "IMFTransform.GetOutputAvailableType");
                var rank = GetGuid(type, in MfMtSubtype, out var subtype) >= 0 ? Rank(in subtype) : int.MaxValue;
                if (rank < bestRank)
                {
                    if (best != 0)
                        Release(best);
                    best = type;
                    bestRank = rank;
                    bestSubtype = subtype;
                }
                else
                {
                    Release(type);
                }
            }

            if (best == 0)
                throw new NotSupportedException("decoder offers no NV12-compatible output format");

            var setOutputType = (delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(_transform, SlotSetOutputType);
            ThrowIfFailed(setOutputType(_transform, 0, best, 0), "IMFTransform.SetOutputType");

            ThrowIfFailed(GetUInt64(best, in MfMtFrameSize, out var frameSize), "IMFMediaType.GetUINT64(FrameSize)");
            _frameWidth = (int)(frameSize >> 32);
            _frameHeight = (int)(frameSize & uint.MaxValue);
            ReadVisibleArea(best);
            _outputSubtype = bestSubtype;
        }
        finally
        {
            if (best != 0)
                Release(best);
        }

        RefreshOutputStreamInfo();
        // Geometry may have changed; force output sample reallocation.
        ReleaseField(ref _outputBuffer);
        ReleaseField(ref _outputSample);
        _outputCapacity = 0;
    }

    /// <summary>Visible size from the display aperture when present (alignment rows hide below it), else the full frame.</summary>
    private void ReadVisibleArea(nint type)
    {
        var width = _frameWidth;
        var height = _frameHeight;
        Span<byte> area = stackalloc byte[VideoAreaBytes];
        fixed (byte* areaPtr = area)
        {
            if (GetBlob(type, in MfMtMinimumDisplayAperture, areaPtr, VideoAreaBytes, out var got) >= 0 && got == VideoAreaBytes
                && BinaryPrimitives.ReadUInt64LittleEndian(area) == 0)
            {
                var cx = BinaryPrimitives.ReadInt32LittleEndian(area[8..]);
                var cy = BinaryPrimitives.ReadInt32LittleEndian(area[12..]);
                if (cx > 0 && cx <= width && cy > 0 && cy <= height)
                {
                    width = cx;
                    height = cy;
                }
            }
        }
        _visibleWidth = Math.Max(2, width & ~1);
        _visibleHeight = Math.Max(2, height & ~1);
    }

    private void RefreshOutputStreamInfo()
    {
        var getOutputStreamInfo = (delegate* unmanaged[Stdcall]<nint, uint, out MftOutputStreamInfo, int>)Slot(_transform, SlotGetOutputStreamInfo);
        ThrowIfFailed(getOutputStreamInfo(_transform, 0, out var info), "IMFTransform.GetOutputStreamInfo");
        _providesSamples = (info.Flags & (StreamProvidesSamples | StreamCanProvideSamples)) != 0;
        _outputStreamSize = info.Size;
    }

    private void PrepareInputSample(ReadOnlySpan<byte> payload, uint timestampMs)
    {
        if (_inputSample == 0)
            ThrowIfFailed(MFCreateSample(out _inputSample), "MFCreateSample");
        if (_inputBuffer == 0 || _inputCapacity < (uint)payload.Length)
        {
            if (_inputBuffer != 0)
            {
                ThrowIfFailed(RemoveAllBuffers(_inputSample), "IMFSample.RemoveAllBuffers");
                ReleaseField(ref _inputBuffer);
            }
            ThrowIfFailed(MFCreateMemoryBuffer((uint)payload.Length, out _inputBuffer), "MFCreateMemoryBuffer");
            _inputCapacity = (uint)payload.Length;
            ThrowIfFailed(AddBuffer(_inputSample, _inputBuffer), "IMFSample.AddBuffer");
        }

        ThrowIfFailed(LockBuffer(_inputBuffer, out var data, out _, out _), "IMFMediaBuffer.Lock");
        try
        {
            payload.CopyTo(new Span<byte>((void*)data, payload.Length));
        }
        finally
        {
            UnlockBuffer(_inputBuffer);
        }
        ThrowIfFailed(SetCurrentLength(_inputBuffer, (uint)payload.Length), "IMFMediaBuffer.SetCurrentLength");
        ThrowIfFailed(SetSampleTime(_inputSample, timestampMs * TicksPerMs), "IMFSample.SetSampleTime");
        ThrowIfFailed(SetSampleDuration(_inputSample, NominalFrameDurationTicks), "IMFSample.SetSampleDuration");
    }

    /// <summary>Pulls every ready output picture, keeping only the newest; true when at least one landed in the decoded buffer.</summary>
    private bool DrainOutputs()
    {
        var processOutput = (delegate* unmanaged[Stdcall]<nint, uint, uint, ref MftOutputDataBuffer, out uint, int>)Slot(_transform, SlotProcessOutput);
        var got = false;
        while (true)
        {
            EnsureOutputSample();
            var output = new MftOutputDataBuffer
            {
                StreamId = 0,
                Sample = _providesSamples ? 0 : _outputSample,
                Status = 0,
                Events = 0,
            };
            var hr = processOutput(_transform, 0, 1, ref output, out _);
            if (output.Events != 0)
                Release(output.Events);
            if (hr == MfENeedMoreInput)
                return got;
            if (hr == MfEStreamChange || (output.Status & OutputFormatChange) != 0)
            {
                // A provides-samples MFT may still have delivered a sample
                // alongside the format-change status; release it or it leaks.
                if (output.Sample != 0)
                    Release(output.Sample);
                NegotiateOutputType();
                continue;
            }
            ThrowIfFailed(hr, "IMFTransform.ProcessOutput");

            var sample = output.Sample;
            if (sample != 0)
            {
                CopyDecoded(sample);
                got = true;
                if (_providesSamples)
                    Release(sample);
            }
        }
    }

    private void EnsureOutputSample()
    {
        if (_providesSamples)
            return;
        var needed = Math.Max(_outputStreamSize, (uint)ComputedOutputBytes());
        if (_outputSample != 0 && _outputCapacity >= needed)
            return;
        ReleaseField(ref _outputBuffer);
        ReleaseField(ref _outputSample);
        ThrowIfFailed(MFCreateSample(out _outputSample), "MFCreateSample");
        ThrowIfFailed(MFCreateMemoryBuffer(needed, out _outputBuffer), "MFCreateMemoryBuffer");
        ThrowIfFailed(AddBuffer(_outputSample, _outputBuffer), "IMFSample.AddBuffer");
        _outputCapacity = needed;
    }

    private int ComputedOutputBytes() =>
        _outputSubtype == FormatYuy2 ? _frameWidth * _frameHeight * 2 : Nv12Convert.TightBytes(_frameWidth, _frameHeight);

    private void CopyDecoded(nint sample)
    {
        // The contiguous buffer carries the standard packed layout for the
        // negotiated subtype (no row padding), so only the subtype-specific
        // plane repack remains.
        ThrowIfFailed(ConvertToContiguousBuffer(sample, out var buffer), "IMFSample.ConvertToContiguousBuffer");
        try
        {
            ThrowIfFailed(LockBuffer(buffer, out var data, out _, out var currentLength), "IMFMediaBuffer.Lock");
            try
            {
                var src = new ReadOnlySpan<byte>((void*)data, (int)currentLength);
                var tightBytes = Nv12Convert.TightBytes(_visibleWidth, _visibleHeight);
                if (_decoded.Length < tightBytes)
                    _decoded = new byte[tightBytes];
                var dst = _decoded.AsSpan(0, tightBytes);
                if (_outputSubtype == FormatNv12)
                    Nv12Convert.FromNv12(src, _frameWidth, _frameHeight, _visibleWidth, _visibleHeight, dst);
                else if (_outputSubtype == FormatIyuv || _outputSubtype == FormatI420)
                    Nv12Convert.FromPlanar420(src, _frameWidth, _frameHeight, _visibleWidth, _visibleHeight, dst, uFirst: true);
                else if (_outputSubtype == FormatYv12)
                    Nv12Convert.FromPlanar420(src, _frameWidth, _frameHeight, _visibleWidth, _visibleHeight, dst, uFirst: false);
                else
                    Nv12Convert.FromYuy2(src, _frameWidth, _frameHeight, _visibleWidth, _visibleHeight, dst);
            }
            finally
            {
                UnlockBuffer(buffer);
            }
        }
        finally
        {
            Release(buffer);
        }
    }

    private static int Rank(in Guid subtype)
    {
        if (subtype == FormatNv12)
            return 0;
        if (subtype == FormatIyuv || subtype == FormatI420)
            return 1;
        if (subtype == FormatYv12)
            return 2;
        if (subtype == FormatYuy2)
            return 3;
        return int.MaxValue;
    }

    private static void ThrowIfFailed(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{what} failed (hr=0x{hr:X8})");
    }

    // ---- vtable plumbing ----

    private static nint Slot(nint comObject, int index) => (*(nint**)comObject)[index];

    private static void Release(nint unknown)
    {
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)Slot(unknown, SlotRelease);
        release(unknown);
    }

    private static void ReleaseField(ref nint unknown)
    {
        if (unknown == 0)
            return;
        Release(unknown);
        unknown = 0;
    }

    private int ProcessInput(nint sample)
    {
        var processInput = (delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(_transform, SlotProcessInput);
        return processInput(_transform, 0, sample, 0);
    }

    private int ProcessMessage(uint message)
    {
        var processMessage = (delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Slot(_transform, SlotProcessMessage);
        return processMessage(_transform, message, 0);
    }

    private static int SetGuid(nint attributes, in Guid key, in Guid value)
    {
        var setGuid = (delegate* unmanaged[Stdcall]<nint, in Guid, in Guid, int>)Slot(attributes, SlotAttributesSetGuid);
        return setGuid(attributes, in key, in value);
    }

    private static int GetGuid(nint attributes, in Guid key, out Guid value)
    {
        var getGuid = (delegate* unmanaged[Stdcall]<nint, in Guid, out Guid, int>)Slot(attributes, SlotAttributesGetGuid);
        return getGuid(attributes, in key, out value);
    }

    private static int SetUInt32(nint attributes, in Guid key, uint value)
    {
        var setUInt32 = (delegate* unmanaged[Stdcall]<nint, in Guid, uint, int>)Slot(attributes, SlotAttributesSetUInt32);
        return setUInt32(attributes, in key, value);
    }

    private static int SetUInt64(nint attributes, in Guid key, ulong value)
    {
        var setUInt64 = (delegate* unmanaged[Stdcall]<nint, in Guid, ulong, int>)Slot(attributes, SlotAttributesSetUInt64);
        return setUInt64(attributes, in key, value);
    }

    private static int GetUInt64(nint attributes, in Guid key, out ulong value)
    {
        var getUInt64 = (delegate* unmanaged[Stdcall]<nint, in Guid, out ulong, int>)Slot(attributes, SlotAttributesGetUInt64);
        return getUInt64(attributes, in key, out value);
    }

    private static int GetBlob(nint attributes, in Guid key, byte* buffer, uint bufferBytes, out uint blobBytes)
    {
        var getBlob = (delegate* unmanaged[Stdcall]<nint, in Guid, byte*, uint, out uint, int>)Slot(attributes, SlotAttributesGetBlob);
        return getBlob(attributes, in key, buffer, bufferBytes, out blobBytes);
    }

    private static int SetSampleTime(nint sample, long time)
    {
        var setSampleTime = (delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, SlotSampleSetSampleTime);
        return setSampleTime(sample, time);
    }

    private static int SetSampleDuration(nint sample, long duration)
    {
        var setSampleDuration = (delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, SlotSampleSetSampleDuration);
        return setSampleDuration(sample, duration);
    }

    private static int ConvertToContiguousBuffer(nint sample, out nint buffer)
    {
        var convert = (delegate* unmanaged[Stdcall]<nint, out nint, int>)Slot(sample, SlotSampleConvertToContiguousBuffer);
        return convert(sample, out buffer);
    }

    private static int AddBuffer(nint sample, nint buffer)
    {
        var addBuffer = (delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(sample, SlotSampleAddBuffer);
        return addBuffer(sample, buffer);
    }

    private static int RemoveAllBuffers(nint sample)
    {
        var removeAllBuffers = (delegate* unmanaged[Stdcall]<nint, int>)Slot(sample, SlotSampleRemoveAllBuffers);
        return removeAllBuffers(sample);
    }

    private static int LockBuffer(nint buffer, out nint data, out uint maxLength, out uint currentLength)
    {
        var lockBuffer = (delegate* unmanaged[Stdcall]<nint, out nint, out uint, out uint, int>)Slot(buffer, SlotBufferLock);
        return lockBuffer(buffer, out data, out maxLength, out currentLength);
    }

    private static void UnlockBuffer(nint buffer)
    {
        var unlock = (delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, SlotBufferUnlock);
        _ = unlock(buffer);
    }

    private static int SetCurrentLength(nint buffer, uint length)
    {
        var setCurrentLength = (delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(buffer, SlotBufferSetCurrentLength);
        return setCurrentLength(buffer, length);
    }

    // ---- interop surface ----

    [StructLayout(LayoutKind.Sequential)]
    private struct MftRegisterTypeInfo
    {
        public Guid Major;
        public Guid Subtype;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputStreamInfo
    {
        public uint Flags;
        public uint Size;
        public uint Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputDataBuffer
    {
        public uint StreamId;
        public nint Sample;
        public uint Status;
        public nint Events;
    }

    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    private static partial int MFShutdown();

    [LibraryImport("mfplat.dll")]
    private static partial int MFTEnumEx(in Guid category, uint flags, in MftRegisterTypeInfo inputType, nint outputType, out nint activates, out uint count);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateMediaType(out nint mediaType);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateSample(out nint sample);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateMemoryBuffer(uint maxLength, out nint buffer);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint apartmentType);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint memory);

    // IUnknown.
    private const int SlotRelease = 2;

    // IMFAttributes (shared by media types, samples, activates).
    private const int SlotAttributesGetUInt64 = 8;
    private const int SlotAttributesGetGuid = 10;
    private const int SlotAttributesGetBlob = 15;
    private const int SlotAttributesSetUInt32 = 21;
    private const int SlotAttributesSetUInt64 = 22;
    private const int SlotAttributesSetGuid = 24;

    // IMFActivate.
    private const int SlotActivateObject = 33;

    // IMFSample.
    private const int SlotSampleSetSampleTime = 36;
    private const int SlotSampleSetSampleDuration = 38;
    private const int SlotSampleConvertToContiguousBuffer = 41;
    private const int SlotSampleAddBuffer = 42;
    private const int SlotSampleRemoveAllBuffers = 44;

    // IMFMediaBuffer.
    private const int SlotBufferLock = 3;
    private const int SlotBufferUnlock = 4;
    private const int SlotBufferSetCurrentLength = 6;

    // IMFTransform.
    private const int SlotGetOutputStreamInfo = 7;
    private const int SlotGetAttributes = 8;
    private const int SlotGetOutputAvailableType = 14;
    private const int SlotSetInputType = 15;
    private const int SlotSetOutputType = 16;
    private const int SlotProcessMessage = 23;
    private const int SlotProcessInput = 24;
    private const int SlotProcessOutput = 25;

    private const uint MfVersion = 0x00020070;
    private const uint MfStartupFull = 0;
    private const uint CoinitMultithreaded = 0;
    private const uint EnumSyncMft = 0x00000001;
    private const uint EnumSortAndFilter = 0x00000040;
    private const uint StreamProvidesSamples = 0x00000100;
    private const uint StreamCanProvideSamples = 0x00000200;
    private const uint OutputFormatChange = 0x00000100;
    private const uint MsgNotifyBeginStreaming = 0x10000000;
    private const uint MsgNotifyStartOfStream = 0x10000003;
    private const uint InterlaceProgressive = 2;
    private const uint NominalFps = 30;
    private const long TicksPerMs = 10_000;
    private const long NominalFrameDurationTicks = TicksPerMs * 1000 / NominalFps;
    private const int VideoAreaBytes = 16;

    private const int MfENotAccepting = unchecked((int)0xC00D36B5);
    private const int MfENoMoreTypes = unchecked((int)0xC00D36B9);
    private const int MfEStreamChange = unchecked((int)0xC00D6D61);
    private const int MfENeedMoreInput = unchecked((int)0xC00D6D72);

    private static readonly Guid MftCategoryVideoDecoder = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid FormatH264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid FormatMjpg = new("47504a4d-0000-0010-8000-00aa00389b71");
    private static readonly Guid FormatNv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid FormatIyuv = new("56555949-0000-0010-8000-00aa00389b71");
    private static readonly Guid FormatI420 = new("30323449-0000-0010-8000-00aa00389b71");
    private static readonly Guid FormatYv12 = new("32315659-0000-0010-8000-00aa00389b71");
    private static readonly Guid FormatYuy2 = new("32595559-0000-0010-8000-00aa00389b71");
    private static readonly Guid IidIMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MfMtFrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MfMtInterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MfMtMinimumDisplayAperture = new("66758743-7e5f-400d-980a-aa8596c85696");
    private static readonly Guid MfLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
}

/// <summary>Live decoder factory used by the production virtual camera.</summary>
[SupportedOSPlatform("windows")]
internal sealed class MfVideoDecoderFactory : IWindowsVideoDecoderFactory
{
    public IWindowsVideoDecoder Create(WebcamCodec codec, int width, int height) =>
        MfVideoDecoder.Create(codec, width, height);
}
