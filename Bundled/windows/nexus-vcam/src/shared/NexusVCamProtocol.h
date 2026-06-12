// Shared-memory frame ring protocol between the Nexus service (producer)
// and the NexusVCam media source running inside the Windows Frame Server
// (consumer). Plain C ABI so the C# service can mirror it with
// StructLayout(LayoutKind.Sequential) + P/Invoke.
//
// Producer publish sequence per frame:
//   slot = frameCounter % slotCount
//   InterlockedIncrement(slotSeq[slot])        -> odd marks slot busy
//   write pixel data into the slot
//   InterlockedIncrement(slotSeq[slot])        -> even marks slot complete
//   latestSlot = slot, then InterlockedIncrement(frameCounter)
//   producerTickMs = GetTickCount64()
//   SetEvent(frame-ready event)                -> optional wake hint only
//
// Consumer read sequence (MF pull model, called from RequestSample):
//   read frameCounter, latestSlot; seq1 = slotSeq[latestSlot]
//   if seq1 odd -> keep previous frame
//   copy slot; seq2 = slotSeq[latestSlot]
//   if seq1 != seq2 -> torn, keep previous frame
//
// The consumer treats the producer as gone when producerTickMs is older
// than NEXUS_VCAM_STALE_MS and falls back to an internal test pattern.

#ifndef NEXUS_VCAM_PROTOCOL_H
#define NEXUS_VCAM_PROTOCOL_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Global namespace is required: producer may live in session 0 (service)
// while a debug producer runs in the interactive session.
#define NEXUS_VCAM_SHMEM_NAME       L"Global\\NexusVCam.Frames"
#define NEXUS_VCAM_FRAME_EVENT_NAME L"Global\\NexusVCam.FrameReady"

// DACL for both objects: full control for SYSTEM and Administrators,
// read + synchronize for LocalService (the Frame Server identity, which
// hosts the consuming media source).
#define NEXUS_VCAM_SHMEM_SDDL L"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x80100000;;;LS)"

#define NEXUS_VCAM_MAGIC      0x4356584Eu /* "NXVC" little-endian */
#define NEXUS_VCAM_VERSION    1u
#define NEXUS_VCAM_FOURCC_NV12 0x3231564Eu /* "NV12" little-endian */

#define NEXUS_VCAM_MAX_SLOTS  4u
#define NEXUS_VCAM_SLOT_COUNT 3u

#define NEXUS_VCAM_DEFAULT_WIDTH  1920u
#define NEXUS_VCAM_DEFAULT_HEIGHT 1080u
#define NEXUS_VCAM_DEFAULT_FPS    30u

// Producer heartbeat staleness threshold in milliseconds.
#define NEXUS_VCAM_STALE_MS   2000u

// Header occupies the first page; pixel slots follow contiguously.
#define NEXUS_VCAM_HEADER_BYTES 4096u

typedef struct NexusVCamHeader
{
    uint32_t magic;
    uint32_t version;
    uint32_t headerBytes;   // offset of slot 0 from mapping base
    uint32_t width;
    uint32_t height;
    uint32_t strideY;       // Y plane row bytes; NV12 UV plane shares it
    uint32_t fourcc;
    uint32_t slotCount;
    uint32_t slotBytes;
    uint32_t producerPid;
    volatile int64_t frameCounter;  // count of published frames
    volatile int32_t latestSlot;
    uint32_t reserved0;
    volatile int64_t producerTickMs;
    volatile int32_t slotSeq[NEXUS_VCAM_MAX_SLOTS]; // seqlock per slot, odd while writing
} NexusVCamHeader;

#ifdef __cplusplus
} // extern "C"

static_assert(sizeof(NexusVCamHeader) <= NEXUS_VCAM_HEADER_BYTES, "header must fit the reserved page");
static_assert(NEXUS_VCAM_SLOT_COUNT <= NEXUS_VCAM_MAX_SLOTS, "ring larger than seqlock array");

namespace nexusvcam
{
inline uint32_t Nv12FrameBytes(uint32_t /* width */, uint32_t height, uint32_t strideY)
{
    // Y plane plus interleaved half-height UV plane; stride already covers row width.
    return strideY * height + strideY * (height / 2);
}

inline uint64_t MappingBytes(uint32_t slotBytes, uint32_t slotCount)
{
    return static_cast<uint64_t>(NEXUS_VCAM_HEADER_BYTES) + static_cast<uint64_t>(slotBytes) * slotCount;
}

inline uint8_t* SlotData(NexusVCamHeader* header, uint32_t slot)
{
    return reinterpret_cast<uint8_t*>(header) + header->headerBytes + static_cast<size_t>(header->slotBytes) * slot;
}

inline const uint8_t* SlotData(const NexusVCamHeader* header, uint32_t slot)
{
    return reinterpret_cast<const uint8_t*>(header) + header->headerBytes + static_cast<size_t>(header->slotBytes) * slot;
}
} // namespace nexusvcam
#endif // __cplusplus

#endif // NEXUS_VCAM_PROTOCOL_H
