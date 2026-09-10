using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Models;
using Nexus.Service.Models.Devices;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    /// <summary>Ceiling for a hand-typed chain zone count; mirrors the artifact schema's per-zone cap.</summary>
    private const int MaxChainZoneLeds = 4096;

    /// <summary>
    /// Ceiling for a whole chain. Without it six links of the per-link maximum
    /// would ask a header for 24576 LEDs, which is persisted as the port's
    /// count and then sent as a RESIZEZONE.
    /// </summary>
    private const int MaxChainTotalLeds = 4096;

    /// <summary>
    /// Community mapping flow per lighting device: browse the registry
    /// (proxied through the service's disk cache so the SPA never talks to
    /// the cloud directly), apply/revert, publish, and .nexusmap
    /// export/import. The first-seen auto-apply path lives in
    /// <see cref="Nexus.Service.Lighting.Mappings.MappingAutoApplyService"/>.
    /// </summary>
    private static void MapMappingEndpoints(WebApplication app)
    {
        // Ranked community mappings for this device + local apply state.
        app.MapGet("/devices/lighting-devices/{id}/mappings", async (string id, bool? refresh,
            MappingApplyService mappings,
            MappingCloudClient cloud,
            Nexus.Service.Persistence.IConfigStore store,
            CancellationToken ct) =>
        {
            var card = mappings.FindCard(id);
            if (card is null)
            {
                return Results.Json(new DeviceMappingsResponse { Error = true, Msg = "unknown device" },
                    AppJsonContext.Default.DeviceMappingsResponse);
            }

            var settings = store.Load();
            settings.Devices.AppliedMappings.TryGetValue(id, out var applied);
            var response = new DeviceMappingsResponse
            {
                DeviceKey = card.DeviceKey,
                AutoApplyDeclined = settings.Devices.MappingAutoApplyDeclined.Contains(id),
                Applied = applied is null ? null : new AppliedMappingSummary
                {
                    MappingId = applied.MappingId,
                    Name = applied.Name,
                    Source = applied.Source,
                    ContentHash = applied.ContentHash,
                    AutoApplied = applied.AutoApplied,
                    AppliedAtMs = applied.AppliedAt.ToUnixTimeMilliseconds(),
                },
            };
            if (card.DeviceKey.Length > 0)
            {
                var list = await cloud.GetMappingsAsync(card.DeviceKey, refresh == true, ct).ConfigureAwait(false);
                response.Items = list.Items;
                response.Offline = list.Offline;
            }
            return Results.Json(response, AppJsonContext.Default.DeviceMappingsResponse);
        });

        // The pre-built product catalog: everything a user can hang off an ARGB
        // header. Served straight from the embedded resource - no registry, no
        // cache, no network - because a header cannot report what is wired to
        // it and the picker is the only way for the user to say.
        app.MapGet("/devices/lighting-devices/mappings/catalog", (string? q, string? type, int? limit) =>
        {
            var items = BuiltInMappingsCatalog.Search(q, type, limit ?? 50, out var matched);
            var response = new BuiltInMappingsResponse
            {
                Items = items,
                Total = matched,
            };
            return Results.Json(response, AppJsonContext.Default.BuiltInMappingsResponse);
        });

        // Cache-only availability counts for the device-card badge. Never
        // touches the network: counts come from the disk cache the list
        // proxy and auto-apply worker keep warm.
        app.MapGet("/devices/lighting-devices/mappings/available", (
            Nexus.Service.Devices.ILightingDeviceProvider ld,
            MappingCloudClient cloud,
            Nexus.Service.Persistence.IConfigStore store) =>
        {
            var response = new MappingsAvailableResponse();
            var applied = store.Load().Devices.AppliedMappings;
            foreach (var card in ld.GetAll().Devices)
            {
                // The badge advertises layouts the user has not engaged
                // with yet; devices already running a mapping stay quiet.
                if (card.DeviceKey.Length == 0 || applied.ContainsKey(card.Id))
                    continue;
                var count = cloud.CachedMappingCount(card.DeviceKey);
                if (count > 0)
                    response.Counts[card.Id] = count;
            }
            return Results.Json(response, AppJsonContext.Default.MappingsAvailableResponse);
        });

        // Apply a community mapping by registry id (must be in the cached list).
        app.MapPost("/devices/lighting-devices/{id}/mappings/apply", async (string id, ApplyMappingBody body,
            MappingApplyService mappings,
            MappingCloudClient cloud,
            CancellationToken ct) =>
        {
            var card = mappings.FindCard(id);
            if (card is null || card.DeviceKey.Length == 0)
                return ApiResponse.Fail("unknown device");
            var list = await cloud.GetMappingsAsync(card.DeviceKey, forceRefresh: false, ct).ConfigureAwait(false);
            CommunityMapping? item = null;
            foreach (var candidate in list.Items)
            {
                if (candidate.Id == body.MappingId)
                { item = candidate; break; }
            }
            if (item?.Payload is null)
                return ApiResponse.Fail("mapping not found");
            return mappings.Apply(id, item.Payload, item.Id, MappingApplyService.SourceCommunity, auto: false, item.ContentHash) switch
            {
                MappingApplyService.ApplyOutcome.Applied => ApiResponse.Ok(),
                MappingApplyService.ApplyOutcome.Invalid => ApiResponse.Fail("mapping failed validation"),
                _ => ApiResponse.Fail("unknown device"),
            };
        });

        // Assign a pre-built mapping by product key. The user's own edits keep
        // layering on top (DeviceLedOverrides / LedGroups) and are never folded
        // back into the artifact, so re-assigning always restores the shipped
        // layout rather than whatever the last edit left behind.
        app.MapPost("/devices/lighting-devices/{id}/mappings/assign", (string id, AssignMappingBody body,
            MappingApplyService mappings) =>
        {
            var artifact = BuiltInMappingsCatalog.Find(body.Key ?? "");
            if (artifact is null)
                return ApiResponse.Fail("unknown mapping");
            return mappings.Apply(id, artifact, body.Key, MappingApplyService.SourceBuiltIn, auto: false) switch
            {
                MappingApplyService.ApplyOutcome.Applied => ApiResponse.Ok(),
                MappingApplyService.ApplyOutcome.Invalid => ApiResponse.Fail("mapping failed validation"),
                _ => ApiResponse.Fail("unknown device"),
            };
        });

        // Wire an ordered product chain to one ARGB port: three fans in series
        // become three cards, mixed types allowed. The chain owns both the
        // port's LED count (the sum of its products) and its partition (one
        // zone per product), written together so the count can never drift out
        // from under the slices - which is the drift ZonePartitionValidator's
        // rule 2 exists to prevent, and why that rule lifts for a chained port.
        app.MapPost("/devices/lighting-devices/{deviceId}/mappings/chain", (string deviceId, SetChainBody body,
            Nexus.Service.Lighting.Zones.ZoneTopology topology,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.Rgb.RgbBridge? bridge,
            Nexus.Service.Sockets.MultiplexHub hub) =>
        {
            var structure = topology.FindStructure(deviceId);
            if (structure is null)
                return Results.Json(ApiResponse.Fail("unknown device"), AppJsonContext.Default.ApiResponse);
            if (!structure.Partitionable)
                return Results.Json(ApiResponse.Fail("device does not support zone partitions"), AppJsonContext.Default.ApiResponse);
            // A port is one device with one segment, so there is nothing to
            // address: the chain always tiles segment 0. Segment survives on the
            // body only so an older client's payload still parses.
            const int segment = 0;
            if (structure.Segments.Count != 1)
                return Results.Json(ApiResponse.Fail("device is not a single addressable port"), AppJsonContext.Default.ApiResponse);
            if (!structure.Segments[segment].Resizable)
                return Results.Json(ApiResponse.Fail("segment is not an addressable port"), AppJsonContext.Default.ApiResponse);

            // Entries is the current form; Keys is the older all-products body.
            var requested = body.Entries is { Count: > 0 }
                ? body.Entries
                : (body.Keys ?? new List<string>()).ConvertAll(k => new SetChainEntry { Key = k });

            var links = new List<(Nexus.Service.Persistence.ChainEntry Entry, MappingArtifact? Artifact)>(requested.Count);
            var total = 0;
            foreach (var want in requested)
            {
                if (string.IsNullOrEmpty(want.Key))
                    return Results.Json(ApiResponse.Fail("chain entry needs a key"), AppJsonContext.Default.ApiResponse);

                if (GenericChainArtifacts.IsGeneric(want.Key))
                {
                    // A generic's geometry follows the count, so the count is
                    // the client's to set here and only here.
                    if (want.LedCount <= 0 || want.LedCount > MaxChainZoneLeds)
                        return Results.Json(ApiResponse.Fail("generic zone led count out of range"), AppJsonContext.Default.ApiResponse);
                    var generic = GenericChainArtifacts.Build(want.Key, want.LedCount);
                    if (generic is null)
                        return Results.Json(ApiResponse.Fail($"could not build {want.Key}"), AppJsonContext.Default.ApiResponse);
                    links.Add((new Nexus.Service.Persistence.ChainEntry { Key = want.Key, LedCount = want.LedCount }, generic));
                    total += want.LedCount;
                    continue;
                }

                var artifact = BuiltInMappingsCatalog.Find(want.Key);
                if (artifact is null)
                    return Results.Json(ApiResponse.Fail($"unknown mapping {want.Key}"), AppJsonContext.Default.ApiResponse);
                var lint = MappingLint.Validate(artifact);
                if (!lint.Ok)
                    return Results.Json(ApiResponse.Fail($"{want.Key} failed validation"), AppJsonContext.Default.ApiResponse);
                var count = artifact.Zones.Count > 0 ? artifact.Zones[0].LedCount ?? 0 : 0;
                if (count <= 0)
                    return Results.Json(ApiResponse.Fail($"{want.Key} has no LEDs to chain"), AppJsonContext.Default.ApiResponse);
                // The product's own count wins; a client cannot resize a product.
                links.Add((new Nexus.Service.Persistence.ChainEntry { Key = want.Key, LedCount = count }, artifact));
                total += count;
            }

            if (total > MaxChainTotalLeds)
                return Results.Json(ApiResponse.Fail("chain is too long for one port"), AppJsonContext.Default.ApiResponse);

            var chainKey = Nexus.Service.Lighting.Zones.ZoneResolution.ChainKey(deviceId, segment);
            // Resize is keyed by the card that owns the segment. On a port
            // device that is the device id itself; the fallback covers a
            // structure whose defaults were not authored per segment.
            var defaultCardId = structure.DefaultZones.Count > 0
                ? structure.DefaultZones[0].Id
                : deviceId;
            var oldZoneIds = new List<string>();
            foreach (var zone in topology.ZonesFor(structure, store.Load()))
                oldZoneIds.Add(zone.Id);

            var response = new SetChainResponse();
            store.Update(s =>
            {
                Nexus.Service.Lighting.Zones.ZoneStateDrop.Drop(s, oldZoneIds);
                if (links.Count == 0)
                {
                    // Clearing: the port goes back to one whole-segment zone
                    // sized by whatever the user last set.
                    s.Devices.PortChains.Remove(chainKey);
                    s.Devices.ZonePartitions.Remove(deviceId);
                    return;
                }

                s.Devices.PortChains[chainKey] = links.ConvertAll(l => l.Entry);
                s.Devices.ZoneLedCounts[defaultCardId] = total;

                var defs = new List<Nexus.Service.Persistence.ZoneDef>();
                var applied = new List<(int Ordinal, MappingArtifact Artifact)>();
                for (int seg = 0; seg < structure.Segments.Count; seg++)
                {
                    if (seg != segment)
                    {
                        defs.Add(new Nexus.Service.Persistence.ZoneDef
                        {
                            Name = structure.Segments[seg].Name,
                            Slices = { new Nexus.Service.Persistence.ZoneSlice
                                { Segment = seg, Start = 0, Count = structure.Segments[seg].LedCount } },
                        });
                        continue;
                    }
                    var start = 0;
                    for (int i = 0; i < links.Count; i++)
                    {
                        var (entry, artifact) = links[i];
                        var count = entry.LedCount;
                        // No ordinal suffix: three identical fans read as
                        // three "QX Fan" chips, and their position in the chain
                        // is what tells them apart.
                        var name = artifact!.Name;
                        defs.Add(new Nexus.Service.Persistence.ZoneDef
                        {
                            Name = name.Length > Nexus.Service.Lighting.Zones.ZonePartitionValidator.MaxZoneNameLength
                                ? name[..Nexus.Service.Lighting.Zones.ZonePartitionValidator.MaxZoneNameLength]
                                : name,
                            Slices = { new Nexus.Service.Persistence.ZoneSlice
                                { Segment = seg, Start = start, Count = count } },
                        });
                        applied.Add((defs.Count - 1, artifact!));
                        start += count;
                    }
                }
                s.Devices.ZonePartitions[deviceId] = defs;

                // Assign each product to the zone it just created. Written here
                // rather than through MappingApplyService because those cards do
                // not exist until this partition lands.
                foreach (var (ordinal, artifact) in applied)
                {
                    var zoneId = Nexus.Service.Lighting.Zones.ZoneResolution.CustomZoneId(deviceId, ordinal);
                    response.ZoneIds.Add(zoneId);
                    s.Devices.AppliedMappings[zoneId] = new AppliedMappingRef
                    {
                        MappingId = artifact.Device.Key,
                        Source = MappingApplyService.SourceBuiltIn,
                        ContentHash = MappingHash.ContentHash(artifact),
                        Name = artifact.Name,
                        Artifact = artifact,
                        AppliedAt = System.DateTimeOffset.UtcNow,
                        AutoApplied = false,
                    };
                }
            });

            bridge?.RequestTopologyRefresh();
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            response.LedCount = total;
            return Results.Json(response, AppJsonContext.Default.SetChainResponse);
        });

        // Revert the applied mapping. reason: undo (auto-apply veto) | switched | reset.
        app.MapDelete("/devices/lighting-devices/{id}/mapping", (string id, string? reason,
            MappingApplyService mappings) =>
        {
            var normalized = reason is "undo" or "switched" or "reset" ? reason : "reset";
            return mappings.Revert(id, normalized)
                ? ApiResponse.Ok()
                : ApiResponse.Fail("no mapping applied");
        });

        // Import a .nexusmap artifact (validated like any other source).
        app.MapPost("/devices/lighting-devices/{id}/mappings/import", (string id, MappingArtifact artifact,
            MappingApplyService mappings) =>
            mappings.Apply(id, artifact, mappingId: null, source: MappingApplyService.SourceFile, auto: false) switch
            {
                MappingApplyService.ApplyOutcome.Applied => ApiResponse.Ok(),
                MappingApplyService.ApplyOutcome.Invalid => ApiResponse.Fail("artifact failed validation"),
                _ => ApiResponse.Fail("unknown device"),
            });

        // Export the device's current resolved layout as a .nexusmap artifact.
        app.MapGet("/devices/lighting-devices/{id}/mapping/export", (string id,
            MappingApplyService mappings) =>
        {
            var artifact = mappings.Export(id);
            return Results.Json(new ExportMappingResponse
            {
                Error = artifact is null,
                Msg = artifact is null ? "unknown device" : "Ok",
                Artifact = artifact,
            }, AppJsonContext.Default.ExportMappingResponse);
        });

        // Publish the current layout to the registry. Always explicit. No
        // user-authored text travels: the public name is derived from the
        // device itself, so there is nothing to sanitize and nothing to
        // moderate beyond geometry.
        app.MapPost("/devices/lighting-devices/{id}/mappings/publish", async (string id,
            MappingApplyService mappings,
            MappingCloudClient cloud,
            CancellationToken ct) =>
        {
            var card = mappings.FindCard(id);
            if (card is null)
            {
                return Results.Json(new PublishMappingResponse { Error = true, Msg = "unknown device" },
                    AppJsonContext.Default.PublishMappingResponse);
            }
            var name = card.Name.Trim();
            if (name.Length == 0)
                name = card.DeviceKey;
            if (name.Length > MappingSchema.MaxNameLength)
                name = name.Substring(0, MappingSchema.MaxNameLength);
            var artifact = mappings.Export(id, name);
            if (artifact is null || artifact.Device.Key.Length == 0)
            {
                return Results.Json(new PublishMappingResponse { Error = true, Msg = "device cannot be fingerprinted" },
                    AppJsonContext.Default.PublishMappingResponse);
            }
            var lint = MappingLint.Validate(artifact);
            if (!lint.Ok)
            {
                return Results.Json(new PublishMappingResponse { Error = true, Msg = "layout failed validation" },
                    AppJsonContext.Default.PublishMappingResponse);
            }
            var published = await cloud.PublishAsync(artifact, name, description: null, authorName: null, ct).ConfigureAwait(false);
            if (published is null)
            {
                return Results.Json(new PublishMappingResponse { Error = true, Msg = "publish failed or anonymous data is disabled" },
                    AppJsonContext.Default.PublishMappingResponse);
            }
            return Results.Json(published, AppJsonContext.Default.PublishMappingResponse);
        });
    }
}
