// Produces the pixels for each RequestSample call. Prefers the latest frame
// published by the Nexus service through the shared-memory ring; falls back
// to an internally generated grayscale test pattern when no producer is
// alive. The fallback pattern is deliberately colorless so end-to-end
// shared-memory feeding (colored pattern from the host -feed mode) is
// visually distinguishable.

#pragma once

#include "Framework.h"
#include "../shared/NexusVCamProtocol.h"

#include <memory>

class FrameSource
{
public:
    FrameSource() = default;
    ~FrameSource() { Shutdown(); }

    FrameSource(const FrameSource&) = delete;
    FrameSource& operator=(const FrameSource&) = delete;

    HRESULT Initialize(uint32_t width, uint32_t height) noexcept;
    void Shutdown() noexcept;

    // Destination described by the locked MF 2D buffer: first scanline plus
    // signed pitch. NV12 destination must have room for the UV plane at
    // scanline0 + pitch * height.
    HRESULT WriteNV12(uint8_t* scanline0, LONG pitch) noexcept;
    HRESULT WriteRGB32(uint8_t* scanline0, LONG pitch) noexcept;

    uint32_t Width() const noexcept { return _width; }
    uint32_t Height() const noexcept { return _height; }

private:
    void RefreshCurrentFrame() noexcept;
    bool TryCopyFromSharedMemory() noexcept;
    void EnsureMappingOpen() noexcept;
    void CloseMapping() noexcept;
    void GenerateTestPattern() noexcept;

    uint32_t _width = 0;
    uint32_t _height = 0;
    uint32_t _strideY = 0;
    size_t _frameBytes = 0;

    // Double buffer: _current is always a complete frame; _incoming receives
    // the seqlock-guarded copy and is swapped in only when the copy is clean.
    std::unique_ptr<uint8_t[]> _current;
    std::unique_ptr<uint8_t[]> _incoming;

    HANDLE _mapping = nullptr;
    const NexusVCamHeader* _view = nullptr;
    uint32_t _openRetryCountdown = 0;
    int64_t _lastConsumedFrame = 0;
    uint64_t _patternFrame = 0;
};
