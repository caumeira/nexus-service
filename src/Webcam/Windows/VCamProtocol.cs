namespace Nexus.Service.Webcam.Windows;

/// <summary>
/// C# mirror of Bundled/windows/nexus-vcam/src/shared/NexusVCamProtocol.h -
/// the shared-memory contract between this service (producer) and the
/// NexusVCam media source hosted in the Windows Frame Server (consumer).
/// Field offsets are the MSVC x64 layout of NexusVCamHeader; producer and
/// consumer are both little-endian, so plain little-endian span writes match
/// the C struct bytes. Keep in sync with the header - it is the single
/// source of truth.
/// </summary>
internal static class VCamProtocol
{
    public const string SharedMemoryName = @"Global\NexusVCam.Frames";
    public const string FrameEventName = @"Global\NexusVCam.FrameReady";

    /// <summary>
    /// DACL for both kernel objects: full control for SYSTEM and
    /// Administrators, read + synchronize for LocalService (the Frame Server
    /// identity that hosts the consuming media source).
    /// </summary>
    public const string SharedMemorySddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x80100000;;;LS)";

    public const uint Magic = 0x4356584E;
    public const uint Version = 1;
    public const uint FourccNv12 = 0x3231564E;

    public const int MaxSlots = 4;
    public const int SlotCount = 3;

    /// <summary>
    /// The consumer's media type is pinned to this geometry and it rejects a
    /// ring with any other dimensions, so the producer always creates the
    /// ring at this size and letterboxes arbitrary source frames into it.
    /// </summary>
    public const int DefaultWidth = 1920;
    public const int DefaultHeight = 1080;

    /// <summary>Header page size; slot 0 starts at this offset.</summary>
    public const int HeaderBytes = 4096;

    // NexusVCamHeader field offsets.
    public const int MagicOffset = 0;
    public const int VersionOffset = 4;
    public const int HeaderBytesOffset = 8;
    public const int WidthOffset = 12;
    public const int HeightOffset = 16;
    public const int StrideYOffset = 20;
    public const int FourccOffset = 24;
    public const int SlotCountOffset = 28;
    public const int SlotBytesOffset = 32;
    public const int ProducerPidOffset = 36;
    public const int FrameCounterOffset = 40;
    public const int LatestSlotOffset = 48;
    public const int ProducerTickMsOffset = 56;
    public const int SlotSeqOffset = 64;

    /// <summary>Tight NV12 frame bytes: Y plane plus interleaved half-height UV plane, stride equals width.</summary>
    public static int Nv12FrameBytes(int width, int height) => width * height + width * (height / 2);

    public static long MappingBytes(int slotBytes, int slotCount) => HeaderBytes + (long)slotBytes * slotCount;
}
