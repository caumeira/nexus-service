using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Models;
using Nexus.Service.Models.Devices;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
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
            return mappings.Apply(id, item.Payload, item.Id, "community", auto: false, item.ContentHash) switch
            {
                MappingApplyService.ApplyOutcome.Applied => ApiResponse.Ok(),
                MappingApplyService.ApplyOutcome.Invalid => ApiResponse.Fail("mapping failed validation"),
                _ => ApiResponse.Fail("unknown device"),
            };
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
            mappings.Apply(id, artifact, mappingId: null, source: "file", auto: false) switch
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

        // Publish the current layout to the registry. Always explicit; the
        // registry enforces provenance, throttle, and dedup.
        app.MapPost("/devices/lighting-devices/{id}/mappings/publish", async (string id, PublishMappingBody body,
            MappingApplyService mappings,
            MappingCloudClient cloud,
            CancellationToken ct) =>
        {
            var name = body.Name.Trim();
            if (name.Length == 0 || name.Length > MappingSchema.MaxNameLength)
            {
                return Results.Json(new PublishMappingResponse { Error = true, Msg = "invalid name" },
                    AppJsonContext.Default.PublishMappingResponse);
            }
            var artifact = mappings.Export(id, name, body.Description);
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
            var published = await cloud.PublishAsync(artifact, name, body.Description, body.AuthorName, ct).ConfigureAwait(false);
            if (published is null)
            {
                return Results.Json(new PublishMappingResponse { Error = true, Msg = "publish failed or anonymous data is disabled" },
                    AppJsonContext.Default.PublishMappingResponse);
            }
            return Results.Json(published, AppJsonContext.Default.PublishMappingResponse);
        });
    }
}
