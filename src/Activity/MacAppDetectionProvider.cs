using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Nexus.Service.Models.Activity;
using Nexus.Service.Platform;

namespace Nexus.Service.Activity;

/// <summary>
/// Real macOS app-detection provider.
/// - GetDetected() returns running GUI applications via `lsappinfo list`
///   (lightweight, no sudo, returns bundle IDs + display names)
/// - Kill() sends SIGTERM via Process.Kill
/// </summary>
public sealed class MacAppDetectionProvider : IAppDetectionProvider
{
    public IReadOnlyList<Detected> GetDetected()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Array.Empty<Detected>();
        }

        try
        {
            var output = ShellExecutor.Run("/usr/bin/lsappinfo", 3000, "list");
            return Parse(output);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[app-detection] failed: {ex.Message}");
            return Array.Empty<Detected>();
        }
    }

    // An entry opens with `N) "Display Name" ASN:0x0-0x184bf4a7:` and its
    // fields follow on indented continuation lines, one of which carries
    // `pid = 70090 type="Foreground"`. Only Foreground entries are GUI
    // applications; UIElement and BackgroundOnly are agents with no user
    // -facing window and outnumber real apps roughly 8:1.
    private static readonly Regex EntryHeader = new(@"^\s*\d+\)\s+""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex PidField = new(@"\bpid\s*=\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex TypeField = new(@"\btype=""([^""]+)""", RegexOptions.Compiled);

    internal static List<Detected> Parse(string output)
    {
        var apps = new List<Detected>();
        string? name = null;
        string? pid = null;
        string? type = null;

        void Flush()
        {
            if (name is not null && pid is not null && string.Equals(type, "Foreground", StringComparison.Ordinal))
            {
                apps.Add(new Detected { Id = pid, Name = name, Type = 0 });
            }
        }

        foreach (var line in output.Split('\n'))
        {
            var header = EntryHeader.Match(line);
            if (header.Success)
            {
                Flush();
                name = header.Groups[1].Value;
                pid = null;
                type = null;
                continue;
            }

            if (name is null)
            {
                continue;
            }
            var pidMatch = PidField.Match(line);
            if (pidMatch.Success)
            {
                pid = pidMatch.Groups[1].Value;
            }
            var typeMatch = TypeField.Match(line);
            if (typeMatch.Success)
            {
                type = typeMatch.Groups[1].Value;
            }
        }
        Flush();

        return apps.DistinctBy(a => a.Name).ToList();
    }

    public bool Kill(string id)
    {
        // macOS app detection uses PIDs as IDs; fall back to name-based kill
        // if the id is not numeric.
        if (int.TryParse(id, out var pid))
        {
            return ProcessKiller.KillByPid(pid);
        }

        return ProcessKiller.Kill(id);
    }
}
