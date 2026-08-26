using System.Runtime.InteropServices;

namespace Nexus.Service.Sensors;

/// <summary>
/// Whether a physical drive has spinning platters, from IOCTL_STORAGE_QUERY_PROPERTY's
/// seek-penalty descriptor. Only a rotational drive parks its heads, so this is what
/// separates a drive that needs a slow SMART cadence from one that does not.
/// </summary>
internal static partial class DriveRotationProbe
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;
    private const uint OpenExisting = 3;
    private const uint FileShareReadWrite = 0x00000003;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        // byte, not bool: LibraryImport cannot marshal a non-blittable field without
        // DisableRuntimeMarshalling on the whole assembly.
        public byte IncursSeekPenalty;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateFile(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(nint device, uint code, ref StoragePropertyQuery inBuffer, int inSize,
        ref DeviceSeekPenaltyDescriptor outBuffer, int outSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>Null when the drive cannot be opened or does not answer the query.</summary>
    internal static bool? IsRotational(uint physicalDriveNumber)
    {
        // Zero desired access is enough for a property query and avoids needing
        // the volume to be openable for read.
        var handle = CreateFile($@"\\.\PhysicalDrive{physicalDriveNumber}", 0, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle == -1 || handle == 0) return null;

        try
        {
            var query = new StoragePropertyQuery
            {
                PropertyId = StorageDeviceSeekPenaltyProperty,
                QueryType = PropertyStandardQuery,
            };
            var descriptor = new DeviceSeekPenaltyDescriptor();
            var ok = DeviceIoControl(handle, IoctlStorageQueryProperty,
                ref query, Marshal.SizeOf<StoragePropertyQuery>(),
                ref descriptor, Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(), out _, 0);
            return ok ? descriptor.IncursSeekPenalty != 0 : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
