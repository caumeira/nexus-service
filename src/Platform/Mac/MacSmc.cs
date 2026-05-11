using System;
using System.Runtime.InteropServices;

namespace Qos.Service.Platform.Mac;

/// <summary>
/// Read-only IOKit AppleSMC client. Works on Apple Silicon and Intel Macs.
/// Reads fan RPM / limits via F{n}Ac / F{n}Mn / F{n}Mx and temperature
/// keys (Tp*, Tg*, Te*, TC*P / TG*P legacy). Writes are not implemented
/// because target-RPM writes (F{n}Tg) require disabling SIP on Apple
/// Silicon.
/// </summary>
internal sealed partial class MacSmc : IDisposable
{
    private const string IOKitFramework = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    private const byte SMC_CMD_READ_BYTES = 5;
    private const byte SMC_CMD_READ_KEYINFO = 9;
    private const uint SMC_SELECTOR = 2;

    private const uint TYPE_FLT = 0x666C7420;   // "flt "
    private const uint TYPE_FPE2 = 0x66706532;  // "fpe2"
    private const uint TYPE_SP78 = 0x73703738;  // "sp78"
    private const uint TYPE_FP78 = 0x66703738;  // "fp78"
    private const uint TYPE_UI8 = 0x75693820;   // "ui8 "
    private const uint TYPE_UI16 = 0x75693136;  // "ui16"
    private const uint TYPE_UI32 = 0x75693332;  // "ui32"

    private readonly object _lock = new();
    private uint _connection;
    private bool _disposed;

    public bool IsOpen => _connection != 0;

    public MacSmc()
    {
        try
        {
            var matching = IOServiceMatching("AppleSMC");
            if (matching == IntPtr.Zero) return;
            var service = IOServiceGetMatchingService(0, matching);
            if (service == 0) return;
            var rc = IOServiceOpen(service, TaskSelfTrap(), 0, out _connection);
            IOObjectRelease(service);
            if (rc != 0) _connection = 0;
        }
        catch
        {
            _connection = 0;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_connection != 0)
            {
                IOServiceClose(_connection);
                _connection = 0;
            }
        }
    }

    public bool TryReadKey(string key, out byte[] data, out uint dataType, out uint dataSize)
    {
        data = Array.Empty<byte>();
        dataType = 0;
        dataSize = 0;
        if (!IsOpen) return false;
        if (key.Length != 4) return false;
        var keyCode = ((uint)key[0] << 24) | ((uint)key[1] << 16) | ((uint)key[2] << 8) | (uint)key[3];

        // Serialise userClient calls. The kernel guarantees per-connection
        // ordering, but holding the lock keeps the two-call pair (key-info +
        // read-bytes) from being preempted by a sensor poll on another thread.
        lock (_lock)
        {
            if (!IsOpen) return false;
            var inSize = (nuint)Marshal.SizeOf<SMCKeyData>();
            var input = default(SMCKeyData);
            input.key = keyCode;
            input.data8 = SMC_CMD_READ_KEYINFO;
            var info = default(SMCKeyData);
            var outSize = inSize;
            var rc = IOConnectCallStructMethod(_connection, SMC_SELECTOR, ref input, inSize, ref info, ref outSize);
            if (rc != 0 || info.result != 0) return false;

            dataType = info.keyInfo.dataType;
            dataSize = info.keyInfo.dataSize;
            if (dataSize == 0 || dataSize > 32) return false;

            var read = default(SMCKeyData);
            read.key = keyCode;
            read.data8 = SMC_CMD_READ_BYTES;
            read.keyInfo.dataSize = dataSize;
            var result = default(SMCKeyData);
            outSize = inSize;
            rc = IOConnectCallStructMethod(_connection, SMC_SELECTOR, ref read, inSize, ref result, ref outSize);
            if (rc != 0 || result.result != 0) return false;

            data = new byte[dataSize];
            unsafe
            {
                for (int i = 0; i < dataSize; i++) data[i] = result.bytes[i];
            }
            return true;
        }
    }

    public int? ReadIntKey(string key)
    {
        var f = ReadFloatKey(key);
        return f is null ? null : (int)f.Value;
    }

    // Apple Silicon SMC returns most numeric keys as "flt " (float32 LE).
    // Intel SMC used fpe2 for fans and sp78/fp78 for temps. Dispatch by
    // declared key type rather than guessing per-key. Integer-typed keys
    // (ui8/16/32) widen losslessly to float for callers reading via this
    // method; ReadIntKey wraps with a truncating cast.

    public float? ReadFloatKey(string key)
    {
        if (!TryReadKey(key, out var d, out var t, out _)) return null;
        return t switch
        {
            TYPE_FLT when d.Length >= 4 => BitConverter.ToSingle(d, 0),
            TYPE_SP78 when d.Length >= 2 => (sbyte)d[0] + d[1] / 256f,
            TYPE_FP78 when d.Length >= 2 => ((d[0] << 8) | d[1]) / 256f,
            // fpe2 has 2 fractional bits; precision floor is 0.25.
            TYPE_FPE2 when d.Length >= 2 => ((d[0] << 8) | d[1]) / 4f,
            TYPE_UI8 when d.Length >= 1 => d[0],
            TYPE_UI16 when d.Length >= 2 => (d[0] << 8) | d[1],
            TYPE_UI32 when d.Length >= 4 => ((uint)d[0] << 24) | ((uint)d[1] << 16) | ((uint)d[2] << 8) | d[3],
            _ => null,
        };
    }

    [LibraryImport(IOKitFramework, EntryPoint = "IOServiceMatching", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr IOServiceMatching(string name);

    [LibraryImport(IOKitFramework, EntryPoint = "IOServiceGetMatchingService")]
    private static partial uint IOServiceGetMatchingService(uint masterPort, IntPtr matching);

    [LibraryImport(IOKitFramework, EntryPoint = "IOServiceOpen")]
    private static partial int IOServiceOpen(uint service, uint owningTask, uint type, out uint connection);

    [LibraryImport(IOKitFramework, EntryPoint = "IOServiceClose")]
    private static partial int IOServiceClose(uint connection);

    [LibraryImport(IOKitFramework, EntryPoint = "IOObjectRelease")]
    private static partial int IOObjectRelease(uint obj);

    [LibraryImport(IOKitFramework, EntryPoint = "IOConnectCallStructMethod")]
    private static partial int IOConnectCallStructMethod(
        uint connection,
        uint selector,
        ref SMCKeyData input,
        nuint inputSize,
        ref SMCKeyData output,
        ref nuint outputSize);

    [LibraryImport(LibSystem, EntryPoint = "task_self_trap")]
    private static partial uint TaskSelfTrap();

    [StructLayout(LayoutKind.Sequential)]
    private struct SMCKeyDataVers
    {
        public byte major;
        public byte minor;
        public byte build;
        public byte reserved;
        public ushort release;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMCKeyDataPLimit
    {
        public ushort version;
        public ushort length;
        public uint cpuPLimit;
        public uint gpuPLimit;
        public uint memPLimit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMCKeyDataKeyInfo
    {
        public uint dataSize;
        public uint dataType;
        public byte dataAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SMCKeyData
    {
        public uint key;
        public SMCKeyDataVers vers;
        public SMCKeyDataPLimit pLimitData;
        public SMCKeyDataKeyInfo keyInfo;
        public byte result;
        public byte status;
        public byte data8;
        public uint data32;
        public fixed byte bytes[32];
    }
}
