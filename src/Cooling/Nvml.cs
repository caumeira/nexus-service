using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Nexus.Service.Cooling;

/// <summary>
/// Minimal NVML (libnvidia-ml) interop for per-fan GPU read + control. Wraps the
/// handful of calls <see cref="LinuxNvidiaFanProvider"/> needs. Every entry point
/// is guarded: a box without the NVIDIA driver throws <c>DllNotFoundException</c>
/// on first call (caught → <see cref="Available"/> false), and an older driver
/// missing a <c>_v2</c> symbol throws <c>EntryPointNotFoundException</c> (also
/// caught). Blittable P/Invoke only — AOT-safe.
/// </summary>
internal static unsafe partial class Nvml
{
    private const string Lib = "libnvidia-ml.so.1";
    private const int Success = 0;          // NVML_SUCCESS
    private const uint TemperatureGpu = 0;  // NVML_TEMPERATURE_GPU
    private const int NameBuf = 96;         // NVML_DEVICE_NAME_V2_BUFFER_SIZE

    private static readonly object Gate = new();
    private static bool? _available;

    public static bool Available
    {
        get
        {
            lock (Gate)
            {
                _available ??= TryInit();
                return _available.Value;
            }
        }
    }

    private static bool TryInit()
    {
        try { return nvmlInit_v2() == Success; }
        catch { return false; } // DllNotFound (no driver) / EntryPointNotFound (old driver)
    }

    public static List<GpuInfo> Read()
    {
        var list = new List<GpuInfo>();
        if (!Available)
            return list;
        try
        {
            if (nvmlDeviceGetCount_v2(out var count) != Success)
                return list;
            for (uint i = 0; i < count; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out var dev) != Success)
                    continue;
                var fans = new List<GpuFan>();
                if (nvmlDeviceGetNumFans(dev, out var nfans) == Success)
                    for (uint f = 0; f < nfans; f++)
                        if (nvmlDeviceGetFanSpeed_v2(dev, f, out var speed) == Success)
                            fans.Add(new GpuFan((int)f, (int)speed));
                float? temp = nvmlDeviceGetTemperature(dev, TemperatureGpu, out var t) == Success ? t : null;
                list.Add(new GpuInfo((int)i, ReadName(dev), temp, fans));
            }
        }
        catch { /* driver removed mid-read — return what we have */ }
        return list;
    }

    /// <summary>Set a fan to a fixed duty %, or null to restore the driver's auto curve. Needs root.</summary>
    public static bool SetFan(int gpu, int fan, int? duty)
    {
        if (!Available)
            return false;
        try
        {
            if (nvmlDeviceGetHandleByIndex_v2((uint)gpu, out var dev) != Success)
                return false;
            var rc = duty is null
                ? nvmlDeviceSetDefaultFanSpeed_v2(dev, (uint)fan)
                : nvmlDeviceSetFanSpeed_v2(dev, (uint)fan, (uint)Math.Clamp(duty.Value, 0, 100));
            return rc == Success;
        }
        catch { return false; }
    }

    private static string ReadName(IntPtr dev)
    {
        Span<byte> buf = stackalloc byte[NameBuf];
        fixed (byte* p = buf)
        {
            if (nvmlDeviceGetName(dev, p, NameBuf) != Success)
                return "NVIDIA GPU";
        }
        var end = buf.IndexOf((byte)0);
        return Encoding.ASCII.GetString(buf[..(end < 0 ? NameBuf : end)]).Trim();
    }

    [LibraryImport(Lib)] private static partial int nvmlInit_v2();
    [LibraryImport(Lib)] private static partial int nvmlDeviceGetCount_v2(out uint count);
    [LibraryImport(Lib)] private static partial int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [LibraryImport(Lib)] private static partial int nvmlDeviceGetName(IntPtr device, byte* name, uint length);
    [LibraryImport(Lib)] private static partial int nvmlDeviceGetNumFans(IntPtr device, out uint numFans);
    [LibraryImport(Lib)] private static partial int nvmlDeviceGetFanSpeed_v2(IntPtr device, uint fan, out uint speed);
    [LibraryImport(Lib)] private static partial int nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temp);
    [LibraryImport(Lib)] private static partial int nvmlDeviceSetFanSpeed_v2(IntPtr device, uint fan, uint speed);
    [LibraryImport(Lib)] private static partial int nvmlDeviceSetDefaultFanSpeed_v2(IntPtr device, uint fan);
}
