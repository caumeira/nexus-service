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
            // lsappinfo list outputs one app per block, with "pid", "bundleID", "name" fields.
            var output = ShellExecutor.Run("/usr/bin/lsappinfo", 3000, "list");
            var apps = new List<Detected>();
            var blocks = output.Split(new[] { "---" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in blocks)
            {
                var pidMatch = Regex.Match(block, @"""pid""\s*=\s*(\d+)");
                var nameMatch = Regex.Match(block, @"""name""\s*=\s*""([^""]+)""");
                var bundleMatch = Regex.Match(block, @"""bundleID""\s*=\s*""([^""]+)""");

                if (!pidMatch.Success || !nameMatch.Success)
                {
                    continue;
                }

                apps.Add(new Detected
                {
                    Id = pidMatch.Groups[1].Value,
                    Name = nameMatch.Groups[1].Value,
                    Type = 0, // Process
                });
            }

            return apps.DistinctBy(a => a.Name).ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[app-detection] failed: {ex.Message}");
            return Array.Empty<Detected>();
        }
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
