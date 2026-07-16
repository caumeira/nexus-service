using System;
using System.Collections.Generic;
using Nexus.Service.Platform;

namespace Nexus.Service.Diagnostics.Temperature;

/// <summary>
/// Re-keys legacy enumeration-index GPU temperature rows to the current
/// UUID-based id, so a GPU with prior history does not show as two series in
/// /diagnostics/temperatures. Runs once at TemperatureRollup startup, per
/// resolved GPU.
/// </summary>
public static class GpuComponentIdMigration
{
    public static void Migrate(ITemperatureHistoryStore store, IReadOnlyList<(string Name, string Uuid)> gpus)
    {
        foreach (var (name, uuid) in gpus)
        {
            try
            {
                var legacyIds = store.FindLegacyGpuComponentIds(name);
                if (legacyIds.Count == 0)
                {
                    continue;
                }
                if (legacyIds.Count > 1)
                {
                    ServiceLog.Warn(
                        $"[temp-sampler] ambiguous legacy gpu ids for '{name}': {string.Join(", ", legacyIds)}; skipping migration");
                    continue;
                }

                var newId = $"gpu:{uuid}";
                var moved = store.RekeyComponent(legacyIds[0], newId);
                ServiceLog.Info($"[temp-sampler] migrated {moved} temperature rows from {legacyIds[0]} to {newId}");
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[temp-sampler] gpu id migration failed for '{name}': {ex.Message}");
            }
        }
    }
}
