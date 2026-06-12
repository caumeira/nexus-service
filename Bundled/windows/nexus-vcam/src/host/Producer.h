// Shared-memory ring producer. Reference implementation of the protocol's
// write side; the Nexus service reimplements this in C# against the same
// header layout. Creating objects in the Global namespace requires
// SeCreateGlobalPrivilege (elevated process, or any service account).

#pragma once

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <sddl.h>
#include <cstdint>
#include <cstdio>

#include "../shared/NexusVCamProtocol.h"

class FrameProducer
{
public:
    FrameProducer() = default;
    ~FrameProducer() { Close(); }

    FrameProducer(const FrameProducer&) = delete;
    FrameProducer& operator=(const FrameProducer&) = delete;

    bool Create(uint32_t width, uint32_t height) noexcept
    {
        Close();

        const uint32_t strideY = width;
        const uint32_t slotBytes = nexusvcam::Nv12FrameBytes(width, height, strideY);
        const uint64_t totalBytes = nexusvcam::MappingBytes(slotBytes, NEXUS_VCAM_SLOT_COUNT);

        PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                NEXUS_VCAM_SHMEM_SDDL, SDDL_REVISION_1, &descriptor, nullptr))
        {
            fwprintf(stderr, L"producer: bad SDDL, error %lu\n", GetLastError());
            return false;
        }

        SECURITY_ATTRIBUTES sa{ sizeof(sa), descriptor, FALSE };
        _mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE,
                                      static_cast<DWORD>(totalBytes >> 32),
                                      static_cast<DWORD>(totalBytes & 0xFFFFFFFF),
                                      NEXUS_VCAM_SHMEM_NAME);
        const DWORD mappingError = GetLastError();

        _frameEvent = CreateEventW(&sa, FALSE, FALSE, NEXUS_VCAM_FRAME_EVENT_NAME);
        LocalFree(descriptor);

        if (!_mapping)
        {
            fwprintf(stderr, L"producer: CreateFileMapping failed, error %lu (Global namespace needs elevation)\n", mappingError);
            Close();
            return false;
        }

        _header = static_cast<NexusVCamHeader*>(
            MapViewOfFile(_mapping, FILE_MAP_ALL_ACCESS, 0, 0, 0));
        if (!_header)
        {
            fwprintf(stderr, L"producer: MapViewOfFile failed, error %lu\n", GetLastError());
            Close();
            return false;
        }

        // The consumer validates magic last, so publish it after the rest of
        // the header is in place.
        _header->version = NEXUS_VCAM_VERSION;
        _header->headerBytes = NEXUS_VCAM_HEADER_BYTES;
        _header->width = width;
        _header->height = height;
        _header->strideY = strideY;
        _header->fourcc = NEXUS_VCAM_FOURCC_NV12;
        _header->slotCount = NEXUS_VCAM_SLOT_COUNT;
        _header->slotBytes = slotBytes;
        _header->producerPid = GetCurrentProcessId();
        _header->frameCounter = 0;
        _header->latestSlot = 0;
        _header->producerTickMs = static_cast<int64_t>(GetTickCount64());
        for (uint32_t i = 0; i < NEXUS_VCAM_MAX_SLOTS; i++)
            _header->slotSeq[i] = 0;
        MemoryBarrier();
        _header->magic = NEXUS_VCAM_MAGIC;
        return true;
    }

    void Close() noexcept
    {
        if (_header)
        {
            UnmapViewOfFile(_header);
            _header = nullptr;
        }
        if (_mapping)
        {
            CloseHandle(_mapping);
            _mapping = nullptr;
        }
        if (_frameEvent)
        {
            CloseHandle(_frameEvent);
            _frameEvent = nullptr;
        }
    }

    bool IsOpen() const noexcept { return _header != nullptr; }
    NexusVCamHeader* Header() const noexcept { return _header; }

    // Seqlock write: slot goes odd, pixels land, slot goes even, then the
    // publish indices move. Consumers retry or reuse their previous frame on
    // any overlap.
    uint8_t* BeginFrame() noexcept
    {
        if (!_header)
            return nullptr;
        _writeSlot = static_cast<uint32_t>(_header->frameCounter % _header->slotCount);
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(&_header->slotSeq[_writeSlot]));
        return nexusvcam::SlotData(_header, _writeSlot);
    }

    void EndFrame() noexcept
    {
        if (!_header)
            return;
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(&_header->slotSeq[_writeSlot]));
        WriteRelease(reinterpret_cast<volatile LONG*>(&_header->latestSlot), static_cast<LONG>(_writeSlot));
        InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&_header->frameCounter));
        WriteRelease64(reinterpret_cast<volatile LONG64*>(&_header->producerTickMs),
                       static_cast<LONG64>(GetTickCount64()));
        if (_frameEvent)
            SetEvent(_frameEvent);
    }

private:
    HANDLE _mapping = nullptr;
    HANDLE _frameEvent = nullptr;
    NexusVCamHeader* _header = nullptr;
    uint32_t _writeSlot = 0;
};
