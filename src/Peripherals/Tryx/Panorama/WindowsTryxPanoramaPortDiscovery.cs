#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Nexus.Service.Panel;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// SetupAPI-based discovery for Tryx Panorama on Windows. Matches VID 0x18D1
/// against the GUID_DEVCLASS_PORTS device class. AOT-safe; no COM/WMI.
/// Also tries to resolve the ADB serial via <c>adb devices -l</c>, matching
/// lines that report <c>product:cm01</c> or <c>model:cm01</c>.
/// </summary>
public sealed class WindowsTryxPanoramaPortDiscovery : ITryxPanoramaPanelDiscovery
{
    private static readonly Guid PortsClassGuid = new("4d36e978-e325-11ce-bfc1-08002be10318");
    private const string VidFragment = "VID_18D1";

    public IReadOnlyList<TryxPanoramaPortInfo> Discover()
    {
        var result = new List<TryxPanoramaPortInfo>();
        var devInfo = Native.SetupDiGetClassDevs(ref PortsClassGuidLocal(), null, IntPtr.Zero, Native.DIGCF_PRESENT);
        if (devInfo == (IntPtr)(-1)) return result;
        try
        {
            var devData = new Native.SP_DEVINFO_DATA();
            devData.cbSize = Marshal.SizeOf(devData);
            for (uint i = 0; Native.SetupDiEnumDeviceInfo(devInfo, i, ref devData); i++)
            {
                var hardwareId = ReadStringProperty(devInfo, ref devData, Native.SPDRP_HARDWAREID);
                if (string.IsNullOrEmpty(hardwareId)) continue;
                if (hardwareId.IndexOf(VidFragment, StringComparison.OrdinalIgnoreCase) < 0) continue;

                var pid = ParsePid(hardwareId);
                if (pid is null) continue;
                if (Array.IndexOf(TryxPanoramaProtocol.KnownProductIds, pid.Value) < 0) continue;

                var portName = ReadPortName(devInfo, ref devData);
                if (string.IsNullOrEmpty(portName)) continue;

                var serial = ReadInstanceId(devInfo, ref devData);
                var adbSerial = ResolveAdbSerial();

                result.Add(new TryxPanoramaPortInfo
                {
                    PortName = portName,
                    Serial = serial,
                    ProductId = pid.Value,
                    AdbSerial = adbSerial,
                });
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(devInfo);
        }
        return result;
    }

    private static int? ParsePid(string hardwareId)
    {
        var idx = hardwareId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var hex = hardwareId.Substring(idx + 4, Math.Min(4, hardwareId.Length - idx - 4));
        return int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : null;
    }

    private static string ResolveAdbSerial()
    {
        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null) return "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = "devices -l",
                WorkingDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return "";
            p.WaitForExit(5_000);
            var output = p.StandardOutput.ReadToEnd();
            foreach (var line in output.Split('\n'))
            {
                if (line.IndexOf("product:cm01", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("model:cm01", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0) return parts[0];
                }
            }
        }
        catch { /* adb not found or not running */ }
        return "";
    }

    private static ref Guid PortsClassGuidLocal()
        => ref System.Runtime.CompilerServices.Unsafe.AsRef(in PortsClassGuid);

    private static string ReadStringProperty(IntPtr devInfo, ref Native.SP_DEVINFO_DATA devData, uint property)
    {
        var buf = new byte[1024];
        if (!Native.SetupDiGetDeviceRegistryProperty(devInfo, ref devData, property, out _, buf, (uint)buf.Length, out var size))
            return "";
        var raw = Encoding.Unicode.GetString(buf, 0, Math.Max(0, (int)size - 2));
        var nul = raw.IndexOf('\0');
        return nul >= 0 ? raw.Substring(0, nul) : raw;
    }

    private static string ReadPortName(IntPtr devInfo, ref Native.SP_DEVINFO_DATA devData)
    {
        var handle = Native.SetupDiOpenDevRegKey(devInfo, ref devData,
            Native.DICS_FLAG_GLOBAL, 0, Native.DIREG_DEV, Native.KEY_READ);
        if (handle == IntPtr.Zero || handle == (IntPtr)(-1)) return "";
        try
        {
            using var key = RegistryKey.FromHandle(new Microsoft.Win32.SafeHandles.SafeRegistryHandle(handle, ownsHandle: false));
            return key.GetValue("PortName") as string ?? "";
        }
        catch
        {
            return "";
        }
        finally
        {
            Native.RegCloseKey(handle);
        }
    }

    private static string ReadInstanceId(IntPtr devInfo, ref Native.SP_DEVINFO_DATA devData)
    {
        var buf = new char[256];
        if (!Native.SetupDiGetDeviceInstanceId(devInfo, ref devData, buf, (uint)buf.Length, out var needed))
            return "";
        var instanceId = new string(buf, 0, Math.Max(0, (int)needed - 1));
        var lastSlash = instanceId.LastIndexOf('\\');
        if (lastSlash < 0 || lastSlash == instanceId.Length - 1) return instanceId;
        return instanceId.Substring(lastSlash + 1);
    }

    private static class Native
    {
        public const uint DIGCF_PRESENT = 0x02;
        public const uint SPDRP_HARDWAREID = 0x01;
        public const uint DICS_FLAG_GLOBAL = 0x01;
        public const uint DIREG_DEV = 0x01;
        public const uint KEY_READ = 0x20019;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(
            IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceRegistryProperty(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            uint property,
            out uint propertyRegDataType,
            byte[] propertyBuffer,
            uint propertyBufferSize,
            out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiOpenDevRegKey(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            uint scope, uint hwProfile, uint keyType, uint samDesired);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern int RegCloseKey(IntPtr hKey);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "SetupDiGetDeviceInstanceIdW")]
        public static extern bool SetupDiGetDeviceInstanceId(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            [Out] char[] deviceInstanceId,
            uint deviceInstanceIdSize,
            out uint requiredSize);
    }
}
#endif
