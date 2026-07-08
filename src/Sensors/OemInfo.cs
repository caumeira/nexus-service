using System;
using System.Collections.Generic;
#if WINDOWS
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
#endif

namespace Nexus.Service.Sensors;

/// <summary>
/// SMBIOS Type 1 (System Information) identity fields, read once via
/// GetSystemFirmwareTable ('RSMB' provider) rather than WMI/PowerShell so the
/// lookup stays AOT-safe and process-spawn-free. Backed by a thread-safe
/// Lazy so any caller - including one racing <see cref="OemInfoPrewarmService"/> -
/// gets the same, fully-resolved fields; the native read + parse happens
/// exactly once. Windows-only; null everywhere else.
/// </summary>
public sealed class OemInfo
{
    private readonly Lazy<Type1Fields> _fields = new(Read, isThreadSafe: true);

    public string? Manufacturer => _fields.Value.Manufacturer;
    public string? Model => _fields.Value.Model;
    public string? Serial => _fields.Value.Serial;
    public string? Family => _fields.Value.Family;

    /// <summary>Case-insensitive, trimmed match against the detected manufacturer.</summary>
    public bool Matches(IEnumerable<string> candidates)
    {
        var m = Manufacturer;
        if (string.IsNullOrWhiteSpace(m)) return false;
        var trimmed = m.Trim();
        foreach (var candidate in candidates)
        {
            if (string.Equals(trimmed, candidate?.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static Type1Fields Read()
    {
#if WINDOWS
        try
        {
            return ReadWindows();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[oem-info] SMBIOS read failed: {ex.Message}");
            return default;
        }
#else
        return default;
#endif
    }

#if WINDOWS
    // 'RSMB' - raw SMBIOS firmware table provider signature for GetSystemFirmwareTable.
    private const uint RsmbProvider = 0x52534D42;

    // Type 1 formatted-area offsets, SMBIOS spec section 7.2.
    private const int OffsetManufacturer = 0x04;
    private const int OffsetProductName = 0x05;
    private const int OffsetSerialNumber = 0x07;
    private const int OffsetFamily = 0x1A;

    [SupportedOSPlatform("windows")]
    private static Type1Fields ReadWindows()
    {
        var size = OemInfoNativeMethods.GetSystemFirmwareTable(RsmbProvider, 0, null, 0);
        if (size == 0) return default;

        var buffer = new byte[size];
        var written = OemInfoNativeMethods.GetSystemFirmwareTable(RsmbProvider, 0, buffer, size);
        if (written == 0 || written > buffer.Length) return default;

        return ParseType1(buffer);
    }

    /// <summary>
    /// Walks the RawSMBIOSData buffer (8-byte header: Used20CallingMethod,
    /// SMBIOSMajorVersion, SMBIOSMinorVersion, DmiRevision, Length) then its
    /// DMI structures until Type 1 (System Information). Each structure is a
    /// formatted area (Length bytes, starting with type/length/handle) followed
    /// by its string set, which ends at a double NUL. Each field is a 1-based
    /// string-number index into that set at a fixed formatted-area offset.
    /// </summary>
    private static Type1Fields ParseType1(byte[] buffer)
    {
        if (buffer.Length < 8) return default;
        var tableLength = BitConverter.ToInt32(buffer, 4);
        var offset = 8;
        var end = Math.Min(buffer.Length, 8 + tableLength);

        while (offset + 4 <= end)
        {
            var type = buffer[offset];
            var length = buffer[offset + 1];
            if (length < 4) break;

            var formattedEnd = offset + length;
            if (formattedEnd > end) break;

            var pos = formattedEnd;
            while (pos + 1 < end && !(buffer[pos] == 0 && buffer[pos + 1] == 0)) pos++;
            var structEnd = Math.Min(end, pos + 2);

            if (type == 1)
            {
                var strings = SplitStrings(buffer, formattedEnd, structEnd);
                return new Type1Fields
                {
                    Manufacturer = ResolveString(buffer, offset, length, OffsetManufacturer, strings),
                    Model = ResolveString(buffer, offset, length, OffsetProductName, strings),
                    Serial = ResolveString(buffer, offset, length, OffsetSerialNumber, strings),
                    Family = ResolveString(buffer, offset, length, OffsetFamily, strings),
                };
            }
            if (type == 127) break; // end-of-table marker

            offset = structEnd;
        }
        return default;
    }

    private static string? ResolveString(byte[] buffer, int structOffset, int structLength, int fieldOffset, List<string> strings)
    {
        if (fieldOffset >= structLength) return null;
        var strNum = buffer[structOffset + fieldOffset];
        if (strNum == 0 || strNum > strings.Count) return null;
        return strings[strNum - 1].Trim();
    }

    private static List<string> SplitStrings(byte[] buffer, int start, int end)
    {
        var result = new List<string>();
        var strStart = start;
        for (var i = start; i < end; i++)
        {
            if (buffer[i] != 0) continue;
            if (i == strStart) break; // empty string is the set terminator
            result.Add(Encoding.UTF8.GetString(buffer, strStart, i - strStart));
            strStart = i + 1;
        }
        return result;
    }
#endif
}

/// <summary>SMBIOS Type 1 fields resolved from a single parse pass.</summary>
internal readonly struct Type1Fields
{
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? Serial { get; init; }
    public string? Family { get; init; }
}

#if WINDOWS
internal static partial class OemInfoNativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetSystemFirmwareTable(
        uint firmwareTableProviderSignature,
        uint firmwareTableId,
        byte[]? buffer,
        uint bufferSize);
}
#endif
