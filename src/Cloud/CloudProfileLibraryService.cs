using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Models.Profiles;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Cloud;

/// <summary>
/// The cross-machine side of cloud profiles. Sync itself is a per-machine
/// backup (see <see cref="CloudProfileSyncService"/>): a machine never pulls
/// another machine's profile on its own. This service is the explicit,
/// user-driven path instead - list what every machine on the account has,
/// show what one of those profiles holds, and overwrite chosen categories of
/// a local profile from it.
///
/// Machines are named by the hostname they report to /account/devices, not by
/// their installId, which is an opaque GUID.
/// </summary>
public sealed class CloudProfileLibraryService
{
    private readonly ICloudApiClient _api;
    private readonly CloudAccountService _accounts;
    private readonly ProfileManager _profiles;

    public CloudProfileLibraryService(ICloudApiClient api, CloudAccountService accounts, ProfileManager profiles)
    {
        _api = api;
        _accounts = accounts;
        _profiles = profiles;
    }

    /// <summary>Every machine on the account with the profiles it has backed up. A machine with no profiles is still listed, so a user can see it was seen.</summary>
    public async Task<CloudActionResult<CloudLibraryResponse>> GetLibraryAsync(CancellationToken ct)
    {
        if (_accounts.ActiveAccountId is not { } accountId)
        {
            return CloudActionResult<CloudLibraryResponse>.Fail("not_signed_in", "Sign in to use cloud profiles.", 401);
        }

        var devicesResult = await _accounts.WithAuthAsync(accountId, token => _api.ListDevicesAsync(token, ct), ct).ConfigureAwait(false);
        if (!devicesResult.Success)
        {
            return CloudActionResult<CloudLibraryResponse>.FromError(devicesResult);
        }
        var profilesResult = await _accounts.WithAuthAsync(accountId, token => _api.ListProfilesAsync(token, ct), ct).ConfigureAwait(false);
        if (!profilesResult.Success)
        {
            return CloudActionResult<CloudLibraryResponse>.FromError(profilesResult);
        }

        var own = _accounts.ResolveStableInstallId();
        var devices = devicesResult.Value ?? new List<CloudDeviceDto>();
        var rows = profilesResult.Value ?? new List<CloudProfileSummaryDto>();

        var machines = new List<CloudLibraryMachineDto>();
        foreach (var device in devices)
        {
            machines.Add(new CloudLibraryMachineDto
            {
                InstallId = device.InstallId,
                Hostname = string.IsNullOrWhiteSpace(device.Hostname) ? device.InstallId : device.Hostname,
                IsThisMachine = string.Equals(device.InstallId, own, StringComparison.Ordinal),
                LastSeenAt = device.LastSeenAt,
                Profiles = ProfilesFor(rows, device.InstallId),
            });
        }

        // A profile row whose machine never registered a device (or whose row
        // predates per-machine keys and carries the 'legacy' sentinel) would
        // otherwise be invisible and unimportable.
        foreach (var orphan in rows.Select(r => r.InstallId).Distinct(StringComparer.Ordinal))
        {
            if (machines.Any(m => string.Equals(m.InstallId, orphan, StringComparison.Ordinal)))
            {
                continue;
            }
            machines.Add(new CloudLibraryMachineDto
            {
                InstallId = orphan,
                Hostname = "",
                IsThisMachine = string.Equals(orphan, own, StringComparison.Ordinal),
                LastSeenAt = "",
                Profiles = ProfilesFor(rows, orphan),
            });
        }

        return CloudActionResult<CloudLibraryResponse>.Ok(new CloudLibraryResponse { Machines = machines });
    }

    private static List<CloudLibraryProfileDto> ProfilesFor(List<CloudProfileSummaryDto> rows, string installId) =>
        rows.Where(r => string.Equals(r.InstallId, installId, StringComparison.Ordinal))
            .Select(r => new CloudLibraryProfileDto
            {
                ProfileId = r.ProfileId,
                Name = r.Name,
                Revision = r.Revision,
                SizeBytes = r.SizeBytes,
                UpdatedAt = r.UpdatedAt,
            })
            .ToList();

    /// <summary>What each category of a remote profile actually holds, so the toggles in the import sheet are not blind.</summary>
    public async Task<CloudActionResult<CloudImportPreviewResponse>> GetPreviewAsync(string installId, string profileId, CancellationToken ct)
    {
        var fetched = await FetchAsync(installId, profileId, ct).ConfigureAwait(false);
        if (!fetched.Success || fetched.Value is not { } dto || dto.Payload?.Settings is not { } settings)
        {
            return fetched.Success
                ? CloudActionResult<CloudImportPreviewResponse>.Fail("not_found", "That profile has no payload.", 404)
                : CloudActionResult<CloudImportPreviewResponse>.FromError(fetched);
        }

        var categories = new List<CloudImportCategoryDto>();
        foreach (var category in ProfileSharing.All)
        {
            categories.Add(Summarize(settings, category));
        }

        return CloudActionResult<CloudImportPreviewResponse>.Ok(new CloudImportPreviewResponse
        {
            InstallId = installId,
            Hostname = await HostnameForAsync(installId, ct).ConfigureAwait(false),
            ProfileId = profileId,
            Name = dto.Name,
            Revision = dto.Revision,
            UpdatedAt = dto.UpdatedAt,
            Categories = categories,
        });
    }

    /// <summary>Overwrites the chosen categories of a local profile from a remote one. Everything not chosen is left exactly as it was.</summary>
    public async Task<CloudActionResult> ImportAsync(CloudImportRequest request, CancellationToken ct)
    {
        var categories = (request.Categories ?? new List<string>())
            .Select(ProfileSharing.Normalize)
            .Where(c => c is not null)
            .Select(c => c!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (categories.Count == 0)
        {
            return CloudActionResult.Fail("no_categories", "Pick at least one thing to import.", 400);
        }

        var fetched = await FetchAsync(request.InstallId, request.ProfileId, ct).ConfigureAwait(false);
        if (!fetched.Success)
        {
            return CloudActionResult.FromError(fetched);
        }
        if (fetched.Value?.Payload?.Settings is not { } settings)
        {
            return CloudActionResult.Fail("not_found", "That profile has no payload.", 404);
        }

        var target = string.IsNullOrWhiteSpace(request.TargetProfileId)
            ? _profiles.ActiveProfileId
            : request.TargetProfileId;
        if (!_profiles.ApplyCategoriesFromImport(target, settings, categories))
        {
            return CloudActionResult.Fail("not_found", "Target profile not found.", 404);
        }
        return CloudActionResult.Ok();
    }

    private async Task<CloudApiResult<CloudProfileDto>> FetchAsync(string installId, string profileId, CancellationToken ct)
    {
        if (_accounts.ActiveAccountId is not { } accountId)
        {
            return CloudApiResult<CloudProfileDto>.Fail(401, "not_signed_in", "Sign in to use cloud profiles.");
        }
        return await _accounts.WithAuthAsync(accountId, token => _api.GetProfileAsync(token, installId, profileId, ct), ct).ConfigureAwait(false);
    }

    private async Task<string> HostnameForAsync(string installId, CancellationToken ct)
    {
        if (_accounts.ActiveAccountId is not { } accountId)
        {
            return "";
        }
        var result = await _accounts.WithAuthAsync(accountId, token => _api.ListDevicesAsync(token, ct), ct).ConfigureAwait(false);
        if (!result.Success || result.Value is null)
        {
            return "";
        }
        return result.Value.FirstOrDefault(d => string.Equals(d.InstallId, installId, StringComparison.Ordinal))?.Hostname ?? "";
    }

    /// <summary>Serialized length of a settings object with nothing populated; the baseline every category size is measured against.</summary>
    private static readonly int EmptySettingsLength =
        JsonSerializer.Serialize(new NexusSettings(), PersistenceJsonContext.Default.NexusSettings).Length;

    /// <summary>
    /// Per-category counts plus what that category ADDS over an empty settings
    /// object. NexusSettings serializes every property, so measuring the
    /// isolated object whole would report the shared skeleton (~8KB) as if it
    /// were the category's own content.
    /// </summary>
    private static CloudImportCategoryDto Summarize(NexusSettings source, string category)
    {
        var isolated = new NexusSettings();
        ProfileSharing.ApplyCategory(isolated, source, category);

        var metrics = new Dictionary<string, int>();
        switch (category)
        {
            case ProfileSharing.Lighting:
                metrics["layoutPresets"] = source.Lighting.LayoutPresets.Count;
                metrics["deviceLayouts"] = source.Lighting.DeviceLayouts.Count;
                break;
            case ProfileSharing.Cooling:
                metrics["curves"] = source.Cooling.Curves.Count;
                metrics["namedFans"] = source.Cooling.FanNames.Count;
                metrics["fixedSpeeds"] = source.Cooling.ManualSpeeds.Count;
                break;
            case ProfileSharing.Device:
                metrics["keebMacros"] = source.Keeb.Macros.Count;
                metrics["keyOverrides"] = source.Keeb.KeyOverrides.Count;
                break;
        }

        return new CloudImportCategoryDto
        {
            Category = category,
            SizeBytes = Math.Max(
                JsonSerializer.Serialize(isolated, PersistenceJsonContext.Default.NexusSettings).Length - EmptySettingsLength,
                0),
            Metrics = metrics,
        };
    }
}
