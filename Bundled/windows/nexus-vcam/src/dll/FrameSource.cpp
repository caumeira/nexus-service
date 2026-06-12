#include "FrameSource.h"

namespace
{
// Mapping reopen attempt cadence, counted in RequestSample calls.
constexpr uint32_t kOpenRetryFrames = 30;

uint8_t ClampToByte(int value) noexcept
{
    if (value < 0) return 0;
    if (value > 255) return 255;
    return static_cast<uint8_t>(value);
}
}

HRESULT FrameSource::Initialize(uint32_t width, uint32_t height) noexcept
{
    NX_RETURN_HR_IF(E_INVALIDARG, width == 0 || height == 0 || (width % 2) || (height % 2));

    _width = width;
    _height = height;
    _strideY = width;
    _frameBytes = nexusvcam::Nv12FrameBytes(width, height, _strideY);

    _current.reset(new (std::nothrow) uint8_t[_frameBytes]);
    _incoming.reset(new (std::nothrow) uint8_t[_frameBytes]);
    NX_RETURN_HR_IF(E_OUTOFMEMORY, !_current || !_incoming);

    _lastConsumedFrame = 0;
    _patternFrame = 0;
    _openRetryCountdown = 0;
    GenerateTestPattern();
    return S_OK;
}

void FrameSource::Shutdown() noexcept
{
    CloseMapping();
    _current.reset();
    _incoming.reset();
}

void FrameSource::EnsureMappingOpen() noexcept
{
    if (_view)
        return;

    if (_openRetryCountdown > 0)
    {
        _openRetryCountdown--;
        return;
    }
    _openRetryCountdown = kOpenRetryFrames;

    HANDLE mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, NEXUS_VCAM_SHMEM_NAME);
    if (!mapping)
        return;

    const void* view = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0);
    if (!view)
    {
        CloseHandle(mapping);
        return;
    }

    const auto* header = static_cast<const NexusVCamHeader*>(view);
    const bool compatible =
        header->magic == NEXUS_VCAM_MAGIC &&
        header->version == NEXUS_VCAM_VERSION &&
        header->fourcc == NEXUS_VCAM_FOURCC_NV12 &&
        header->width == _width &&
        header->height == _height &&
        header->strideY >= _width &&
        header->slotCount >= 1 &&
        header->slotCount <= NEXUS_VCAM_MAX_SLOTS &&
        header->slotBytes >= nexusvcam::Nv12FrameBytes(header->width, header->height, header->strideY);

    if (!compatible)
    {
        NxTrace(L"FrameSource: incompatible shared memory header, ignoring producer");
        UnmapViewOfFile(view);
        CloseHandle(mapping);
        return;
    }

    _mapping = mapping;
    _view = header;
    _lastConsumedFrame = 0;
    NxTrace(L"FrameSource: shared memory producer attached pid:%u", header->producerPid);
}

void FrameSource::CloseMapping() noexcept
{
    if (_view)
    {
        UnmapViewOfFile(_view);
        _view = nullptr;
    }
    if (_mapping)
    {
        CloseHandle(_mapping);
        _mapping = nullptr;
    }
}

bool FrameSource::TryCopyFromSharedMemory() noexcept
{
    if (!_view)
        return false;

    const NexusVCamHeader* h = _view;

    const uint64_t nowMs = GetTickCount64();
    const int64_t beatMs = ReadAcquire64(reinterpret_cast<const volatile LONG64*>(&h->producerTickMs));
    if (beatMs <= 0 || nowMs < static_cast<uint64_t>(beatMs) ||
        nowMs - static_cast<uint64_t>(beatMs) > NEXUS_VCAM_STALE_MS)
    {
        return false;
    }

    const int64_t published = ReadAcquire64(reinterpret_cast<const volatile LONG64*>(&h->frameCounter));
    if (published <= 0)
        return false;

    if (published == _lastConsumedFrame)
        return true; // producer alive but no new frame; repeat _current

    const int32_t slot = ReadAcquire(reinterpret_cast<const volatile LONG*>(&h->latestSlot));
    if (slot < 0 || static_cast<uint32_t>(slot) >= h->slotCount)
        return false;

    const int32_t seqBefore = ReadAcquire(reinterpret_cast<const volatile LONG*>(&h->slotSeq[slot]));
    if (seqBefore & 1)
        return true; // writer mid-frame, keep _current

    const uint8_t* src = nexusvcam::SlotData(h, static_cast<uint32_t>(slot));
    if (h->strideY == _strideY)
    {
        memcpy(_incoming.get(), src, _frameBytes);
    }
    else
    {
        // Producer stride can exceed ours; compact row by row.
        uint8_t* dst = _incoming.get();
        const uint32_t rows = _height + _height / 2;
        for (uint32_t row = 0; row < rows; row++)
        {
            memcpy(dst + static_cast<size_t>(row) * _strideY,
                   src + static_cast<size_t>(row) * h->strideY,
                   _strideY);
        }
    }

    const int32_t seqAfter = ReadAcquire(reinterpret_cast<const volatile LONG*>(&h->slotSeq[slot]));
    if (seqAfter != seqBefore)
        return true; // torn copy, keep _current

    _incoming.swap(_current);
    _lastConsumedFrame = published;
    return true;
}

void FrameSource::RefreshCurrentFrame() noexcept
{
    EnsureMappingOpen();
    if (TryCopyFromSharedMemory())
        return;

    GenerateTestPattern();
}

void FrameSource::GenerateTestPattern() noexcept
{
    // Grayscale diagonal gradient sweeping with a bright moving bar.
    uint8_t* y = _current.get();
    uint8_t* uv = _current.get() + static_cast<size_t>(_strideY) * _height;

    const uint32_t phase = static_cast<uint32_t>(_patternFrame * 4);
    const uint32_t barX = (phase * 2) % _width;
    const uint32_t barWidth = _width / 32;

    for (uint32_t row = 0; row < _height; row++)
    {
        uint8_t* line = y + static_cast<size_t>(row) * _strideY;
        for (uint32_t col = 0; col < _width; col++)
        {
            uint8_t value = static_cast<uint8_t>((col + row + phase) >> 3);
            if (col >= barX && col < barX + barWidth)
                value = 235;
            line[col] = value;
        }
    }

    // Neutral chroma keeps the fallback colorless.
    memset(uv, 128, static_cast<size_t>(_strideY) * (_height / 2));
    _patternFrame++;
}

HRESULT FrameSource::WriteNV12(uint8_t* scanline0, LONG pitch) noexcept
{
    NX_RETURN_HR_IF_NULL(E_POINTER, scanline0);
    NX_RETURN_HR_IF(E_INVALIDARG, pitch == 0);
    NX_RETURN_HR_IF(MF_E_NOT_INITIALIZED, !_current);

    RefreshCurrentFrame();

    const uint8_t* srcY = _current.get();
    const uint8_t* srcUV = _current.get() + static_cast<size_t>(_strideY) * _height;

    // Negative pitch means bottom-up; NV12 is defined top-down only, so the
    // pipeline always hands us a positive pitch here in practice.
    NX_RETURN_HR_IF(E_INVALIDARG, pitch < 0);
    const size_t dstPitch = static_cast<size_t>(pitch);
    NX_RETURN_HR_IF(E_INVALIDARG, dstPitch < _width);

    uint8_t* dstY = scanline0;
    for (uint32_t row = 0; row < _height; row++)
    {
        memcpy(dstY + row * dstPitch, srcY + static_cast<size_t>(row) * _strideY, _width);
    }

    uint8_t* dstUV = scanline0 + dstPitch * _height;
    for (uint32_t row = 0; row < _height / 2; row++)
    {
        memcpy(dstUV + row * dstPitch, srcUV + static_cast<size_t>(row) * _strideY, _width);
    }
    return S_OK;
}

HRESULT FrameSource::WriteRGB32(uint8_t* scanline0, LONG pitch) noexcept
{
    NX_RETURN_HR_IF_NULL(E_POINTER, scanline0);
    NX_RETURN_HR_IF(E_INVALIDARG, pitch == 0);
    NX_RETURN_HR_IF(MF_E_NOT_INITIALIZED, !_current);

    RefreshCurrentFrame();

    const uint8_t* srcY = _current.get();
    const uint8_t* srcUV = _current.get() + static_cast<size_t>(_strideY) * _height;

    // BT.601 limited-range integer conversion, matching the sample's CPU path.
    for (uint32_t row = 0; row < _height; row++)
    {
        uint8_t* dst = scanline0 + static_cast<ptrdiff_t>(row) * pitch;
        const uint8_t* lineY = srcY + static_cast<size_t>(row) * _strideY;
        const uint8_t* lineUV = srcUV + static_cast<size_t>(row / 2) * _strideY;
        for (uint32_t col = 0; col < _width; col++)
        {
            const int c = static_cast<int>(lineY[col]) - 16;
            const int d = static_cast<int>(lineUV[col & ~1u]) - 128;
            const int e = static_cast<int>(lineUV[(col & ~1u) + 1]) - 128;

            const int r = (298 * c + 409 * e + 128) >> 8;
            const int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
            const int b = (298 * c + 516 * d + 128) >> 8;

            uint8_t* px = dst + static_cast<size_t>(col) * 4;
            px[0] = ClampToByte(b);
            px[1] = ClampToByte(g);
            px[2] = ClampToByte(r);
            px[3] = 0xFF;
        }
    }
    return S_OK;
}
