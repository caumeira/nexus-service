using System;
using System.Diagnostics;
using System.IO;
using Nexus.Service.Models.Displays;
using Nexus.Service.Platform.Mac;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// macOS monitor rotation. There is no public API, so the overlay helper's
/// one-shot <c>--rotate-display</c> mode drives the MonitorPanel framework
/// (what System Settings uses) and exits 0 only once CGDisplayRotation reads
/// the requested angle. A short-lived process keeps that private framework,
/// and any AppKit it pulls in, out of the service process.
/// </summary>
public sealed class MacDisplayOrientationProvider : IDisplayOrientationProvider
{
    private const int RotateTimeoutMs = 8000;

    private readonly Func<string, uint?> _resolveDisplay;
    private readonly Func<string?> _resolveHelper;
    private readonly object _gate = new();

    public MacDisplayOrientationProvider()
        : this(ResolveCgDisplay, MacOverlayHostLauncher.ResolveHelperPath) { }

    internal MacDisplayOrientationProvider(Func<string, uint?> resolveDisplay, Func<string?> resolveHelper)
    {
        _resolveDisplay = resolveDisplay;
        _resolveHelper = resolveHelper;
    }

    /// <summary>The Y70 is not driven as a macOS display; nothing to rotate.</summary>
    public (bool Ok, string Error) SetY70Orientation(string orientation) => (true, "");

    public (bool Ok, string Error) SetDisplayOrientation(string displayId, string orientation, string coverColorHex)
    {
        if (DisplayOrientations.ToMacDegrees(orientation) is not int degrees)
            return (false, $"unknown orientation '{orientation}'");
        if (_resolveDisplay(displayId) is not uint cgDisplay)
            return (false, "display not attached");
        var helper = _resolveHelper();
        if (helper is null || !File.Exists(helper))
            return (false, "overlay helper not found");

        var psi = new ProcessStartInfo
        {
            FileName = helper,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(helper)!,
            RedirectStandardError = true,
        };
        foreach (var arg in BuildArguments(cgDisplay, degrees)) psi.ArgumentList.Add(arg);

        // The Xeneon sensor worker and the rotation route can both land here.
        lock (_gate)
        {
            try
            {
                using var proc = Process.Start(psi);
                if (proc is null) return (false, "overlay helper failed to start");
                var stderr = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(RotateTimeoutMs))
                {
                    try { proc.Kill(); } catch { }
                    return (false, "rotation timed out");
                }
                if (proc.ExitCode == 0) return (true, "");
                var detail = stderr.Wait(1000) ? StripLogPrefix(stderr.Result.Trim()) : "";
                return (false, string.IsNullOrEmpty(detail) ? $"rotation failed (exit {proc.ExitCode})" : detail);
            }
            catch (Exception ex)
            {
                return (false, $"rotation failed: {ex.Message}");
            }
        }
    }

    private const string HelperLogPrefix = "[overlay-helper] rotate: ";

    /// <summary>The helper's stderr line is also a log line; the route returns the message alone.</summary>
    internal static string StripLogPrefix(string line)
        => line.StartsWith(HelperLogPrefix, StringComparison.Ordinal) ? line[HelperLogPrefix.Length..] : line;

    internal static string[] BuildArguments(uint cgDisplay, int degrees)
        => new[] { $"--rotate-display={cgDisplay}", $"--rotate-degrees={degrees}" };

    private static uint? ResolveCgDisplay(string displayId)
    {
        foreach (var handle in MacDisplayBrightnessProvider.EnumerateDisplayHandles())
        {
            if (string.Equals(handle.Id, displayId, StringComparison.Ordinal)) return handle.DisplayId;
        }
        return null;
    }
}
