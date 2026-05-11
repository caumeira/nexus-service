using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Qos.Service.Models;
using Qos.Service.Models.Steam;
using Qos.Service.Persistence;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace Qos.Service.Steam;

public sealed class SteamProvider : ISteamProvider
{
    private static readonly TimeSpan PlayerSummaryTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FriendsTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RecentGamesTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OwnedGamesTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LevelTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AchievementsTtl = TimeSpan.FromMinutes(5);
    private const ulong SteamId64Base = 76561197960265728UL;

    private readonly IConfigStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly object _cacheLock = new();
    private CacheEntry<SteamPlayerSummary?>? _profile;
    private CacheEntry<int?>? _level;
    private CacheEntry<List<SteamRecentGame>>? _recentGames;
    private CacheEntry<List<SteamOwnedGame>>? _ownedGames;
    private CacheEntry<List<SteamFriendSummary>>? _friends;
    private readonly Dictionary<int, CacheEntry<List<SteamAchievement>>> _achievements = new();

    public SteamProvider(IConfigStore store, IHttpClientFactory httpClientFactory)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
    }

    public SteamConfigResponse GetConfig()
    {
        var settings = _store.Load().Steam;
        return new SteamConfigResponse
        {
            HasApiKey = !string.IsNullOrWhiteSpace(settings.ApiKey),
            SteamId = settings.SteamId,
            AutoDetectedSteamId = ResolveSteamIdFromLocalClient() ?? "",
        };
    }

    public void SetConfig(SteamConfigBody body)
    {
        _store.Update(s =>
        {
            if (body.SteamId is not null)
            {
                s.Steam.SteamId = body.SteamId.Trim();
            }
            if (body.ClearApiKey == true)
            {
                s.Steam.ApiKey = "";
            }
            else if (body.ApiKey is not null && body.ApiKey.Trim().Length > 0)
            {
                s.Steam.ApiKey = body.ApiKey.Trim();
            }
        });
        ClearCache();
    }

    public SteamStatusResponse GetStatus()
    {
        var config = ResolveConfig();
        return new SteamStatusResponse
        {
            Ready = config.Ready,
            HasApiKey = config.HasApiKey,
            SteamId = config.SteamId,
            Reason = config.Reason,
            Error = !config.Ready,
            Msg = config.Ready ? "Ok" : config.Reason,
        };
    }

    public async Task<SteamProfileResponse> GetProfileAsync(CancellationToken cancellationToken)
    {
        var config = ResolveConfig();
        if (!config.Ready)
        {
            return new SteamProfileResponse
            {
                Error = true,
                Msg = config.Reason,
            };
        }

        return new SteamProfileResponse
        {
            Profile = await GetCachedAsync(
                () => _profile,
                v => _profile = v,
                PlayerSummaryTtl,
                () => CreateClient(config).GetPlayerSummaryAsync(config.SteamId, cancellationToken)).ConfigureAwait(false),
            Level = await GetCachedAsync(
                () => _level,
                v => _level = v,
                LevelTtl,
                () => CreateClient(config).GetSteamLevelAsync(cancellationToken)).ConfigureAwait(false),
        };
    }

    public async Task<List<SteamRecentGame>> GetRecentGamesAsync(CancellationToken cancellationToken)
    {
        var config = EnsureReady();
        return await GetCachedAsync(
            () => _recentGames,
            v => _recentGames = v,
            RecentGamesTtl,
            () => CreateClient(config).GetRecentGamesAsync(cancellationToken)).ConfigureAwait(false);
    }

    public async Task<List<SteamOwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken)
    {
        var config = EnsureReady();
        return await GetCachedAsync(
            () => _ownedGames,
            v => _ownedGames = v,
            OwnedGamesTtl,
            () => CreateClient(config).GetOwnedGamesAsync(cancellationToken)).ConfigureAwait(false);
    }

    public async Task<List<SteamFriendSummary>> GetFriendsAsync(CancellationToken cancellationToken)
    {
        var config = EnsureReady();
        return await GetCachedAsync(
            () => _friends,
            v => _friends = v,
            FriendsTtl,
            () => CreateClient(config).GetFriendSummariesAsync(cancellationToken)).ConfigureAwait(false);
    }

    public async Task<List<SteamAchievement>> GetAchievementsAsync(int appId, CancellationToken cancellationToken)
    {
        var config = EnsureReady();
        lock (_cacheLock)
        {
            if (_achievements.TryGetValue(appId, out var existing) && existing.IsFresh(AchievementsTtl))
            {
                return existing.Value;
            }
        }

        var achievements = await CreateClient(config).GetAchievementsAsync(appId, cancellationToken).ConfigureAwait(false);
        lock (_cacheLock)
        {
            _achievements[appId] = new CacheEntry<List<SteamAchievement>>(achievements);
        }
        return achievements;
    }

    public ApiResponse Launch()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open")
                {
                    UseShellExecute = false,
                    ArgumentList = { "steam://open/main" },
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo("steam://open/main")
                {
                    UseShellExecute = true,
                });
            }
            return ApiResponse.Ok("launched");
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail($"failed to launch Steam: {ex.Message}", ex);
        }
    }

    private SteamRuntimeConfig EnsureReady()
    {
        var config = ResolveConfig();
        if (!config.Ready)
        {
            throw new InvalidOperationException(config.Reason);
        }
        return config;
    }

    private SteamRuntimeConfig ResolveConfig()
    {
        var settings = _store.Load().Steam;
        var apiKey = settings.ApiKey.Trim();
        var steamId = settings.SteamId.Trim();
        if (steamId.Length == 0)
        {
            steamId = ResolveSteamIdFromLocalClient() ?? "";
        }

        if (apiKey.Length == 0)
        {
            return SteamRuntimeConfig.NotReady(false, steamId, "Steam Web API key is not configured");
        }
        if (!IsValidSteamId64(steamId))
        {
            return SteamRuntimeConfig.NotReady(true, steamId, "SteamID64 is not configured");
        }

        return new SteamRuntimeConfig(true, true, apiKey, steamId, "");
    }

    private SteamApiClient CreateClient(SteamRuntimeConfig config) =>
        new(_httpClientFactory.CreateClient(), config.ApiKey, config.SteamId);

    private T GetCached<T>(Func<CacheEntry<T>?> getter, Action<CacheEntry<T>> setter, TimeSpan ttl, Func<T> fetcher)
    {
        lock (_cacheLock)
        {
            var existing = getter();
            if (existing is not null && existing.IsFresh(ttl))
            {
                return existing.Value;
            }
        }

        var value = fetcher();
        lock (_cacheLock)
        {
            setter(new CacheEntry<T>(value));
        }
        return value;
    }

    private async Task<T> GetCachedAsync<T>(
        Func<CacheEntry<T>?> getter,
        Action<CacheEntry<T>> setter,
        TimeSpan ttl,
        Func<Task<T>> fetcher)
    {
        lock (_cacheLock)
        {
            var existing = getter();
            if (existing is not null && existing.IsFresh(ttl))
            {
                return existing.Value;
            }
        }

        var value = await fetcher().ConfigureAwait(false);
        lock (_cacheLock)
        {
            setter(new CacheEntry<T>(value));
        }
        return value;
    }

    private void ClearCache()
    {
        lock (_cacheLock)
        {
            _profile = null;
            _level = null;
            _recentGames = null;
            _ownedGames = null;
            _friends = null;
            _achievements.Clear();
        }
    }

    private static bool IsValidSteamId64(string value) =>
        value.Length >= 16 && value.All(char.IsDigit);

    private static string? ResolveSteamIdFromLocalClient()
    {
#if WINDOWS
        try
        {
            var active = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
            if (active is not null && ulong.TryParse(Convert.ToString(active, CultureInfo.InvariantCulture), out var accountId) && accountId > 0)
            {
                return (SteamId64Base + accountId).ToString(CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // Fall back to loginusers.vdf parsing below.
        }
#endif

        return ResolveSteamIdFromLoginUsers();
    }

    private static string? ResolveSteamIdFromLoginUsers()
    {
        foreach (var path in GetLoginUsersPaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var firstSteamId = "";
                var currentSteamId = "";
                foreach (var rawLine in File.ReadLines(path))
                {
                    var line = rawLine.Trim();
                    var key = ReadFirstQuotedValue(line);
                    if (key.Length >= 16 && key.All(char.IsDigit))
                    {
                        currentSteamId = key;
                        if (firstSteamId.Length == 0)
                        {
                            firstSteamId = key;
                        }
                    }
                    else if (currentSteamId.Length > 0 &&
                        line.Contains("\"MostRecent\"", StringComparison.OrdinalIgnoreCase) &&
                        line.Contains("\"1\"", StringComparison.Ordinal))
                    {
                        return currentSteamId;
                    }
                }

                if (firstSteamId.Length > 0)
                {
                    return firstSteamId;
                }
            }
            catch
            {
                // Try the next known location.
            }
        }

        return null;
    }

    private static IEnumerable<string> GetLoginUsersPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", "Steam", "config", "loginusers.vdf");
        }
        if (OperatingSystem.IsWindows())
        {
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(programFilesX86, "Steam", "config", "loginusers.vdf");
            yield return Path.Combine(programFiles, "Steam", "config", "loginusers.vdf");
        }
        yield return Path.Combine(home, ".steam", "steam", "config", "loginusers.vdf");
    }

    private static string ReadFirstQuotedValue(string line)
    {
        var start = line.IndexOf('"');
        if (start < 0)
        {
            return "";
        }
        var end = line.IndexOf('"', start + 1);
        return end > start ? line.Substring(start + 1, end - start - 1) : "";
    }

    private sealed record CacheEntry<T>(T Value)
    {
        public DateTimeOffset StoredAt { get; } = DateTimeOffset.UtcNow;
        public bool IsFresh(TimeSpan ttl) => DateTimeOffset.UtcNow - StoredAt < ttl;
    }

    private sealed record SteamRuntimeConfig(
        bool Ready,
        bool HasApiKey,
        string ApiKey,
        string SteamId,
        string Reason)
    {
        public static SteamRuntimeConfig NotReady(bool hasApiKey, string steamId, string reason) =>
            new(false, hasApiKey, "", steamId, reason);
    }
}

internal sealed class SteamApiClient
{
    private const string BaseUrl = "https://api.steampowered.com";
    private const int PlayerSummaryBatchSize = 100;
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _steamId;

    public SteamApiClient(HttpClient http, string apiKey, string steamId)
    {
        _http = http;
        _apiKey = apiKey;
        _steamId = steamId;
    }

    public async Task<SteamPlayerSummary?> GetPlayerSummaryAsync(string steamId, CancellationToken cancellationToken)
    {
        var players = await GetPlayerSummariesAsync(new[] { steamId }, cancellationToken).ConfigureAwait(false);
        return players.Count > 0 ? players[0] : null;
    }

    public async Task<List<SteamPlayerSummary>> GetPlayerSummariesAsync(IReadOnlyList<string> steamIds, CancellationToken cancellationToken)
    {
        if (steamIds.Count == 0 || steamIds.Count > PlayerSummaryBatchSize)
        {
            throw new InvalidOperationException("Steam player summary requests require 1 to 100 Steam IDs");
        }

        using var doc = await GetJsonAsync("ISteamUser/GetPlayerSummaries/v2", new Dictionary<string, string>
        {
            ["key"] = _apiKey,
            ["steamids"] = string.Join(",", steamIds),
        }, cancellationToken).ConfigureAwait(false);

        var list = new List<SteamPlayerSummary>();
        if (!TryGetArray(doc.RootElement, "response", "players", out var players))
        {
            return list;
        }

        foreach (var item in players.EnumerateArray())
        {
            list.Add(ReadPlayer(item));
        }
        return list;
    }

    public async Task<List<SteamRecentGame>> GetRecentGamesAsync(CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync("IPlayerService/GetRecentlyPlayedGames/v1", new Dictionary<string, string>
        {
            ["key"] = _apiKey,
            ["steamid"] = _steamId,
        }, cancellationToken).ConfigureAwait(false);

        var list = new List<SteamRecentGame>();
        if (!TryGetArray(doc.RootElement, "response", "games", out var games))
        {
            return list;
        }
        foreach (var item in games.EnumerateArray())
        {
            list.Add(new SteamRecentGame
            {
                AppId = ReadInt(item, "appid"),
                Name = ReadString(item, "name"),
                Playtime2Weeks = ReadInt(item, "playtime_2weeks"),
                PlaytimeForever = ReadInt(item, "playtime_forever"),
                IconHash = ReadString(item, "img_icon_url"),
            });
        }
        return list;
    }

    public async Task<List<SteamOwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync("IPlayerService/GetOwnedGames/v1", new Dictionary<string, string>
        {
            ["key"] = _apiKey,
            ["steamid"] = _steamId,
            ["include_appinfo"] = "1",
            ["include_played_free_games"] = "1",
        }, cancellationToken).ConfigureAwait(false);

        var list = new List<SteamOwnedGame>();
        if (!TryGetArray(doc.RootElement, "response", "games", out var games))
        {
            return list;
        }
        foreach (var item in games.EnumerateArray())
        {
            list.Add(new SteamOwnedGame
            {
                AppId = ReadInt(item, "appid"),
                Name = ReadString(item, "name"),
                PlaytimeForever = ReadInt(item, "playtime_forever"),
                Playtime2Weeks = item.TryGetProperty("playtime_2weeks", out var twoWeeks) ? twoWeeks.GetInt32() : null,
                IconHash = ReadString(item, "img_icon_url"),
            });
        }
        return list;
    }

    public async Task<List<SteamFriendSummary>> GetFriendSummariesAsync(CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync("ISteamUser/GetFriendList/v1", new Dictionary<string, string>
        {
            ["key"] = _apiKey,
            ["steamid"] = _steamId,
            ["relationship"] = "friend",
        }, cancellationToken).ConfigureAwait(false);

        if (!TryGetArray(doc.RootElement, "friendslist", "friends", out var friends))
        {
            return new List<SteamFriendSummary>();
        }

        var friendSince = new Dictionary<string, long>();
        var steamIds = new List<string>();
        foreach (var item in friends.EnumerateArray())
        {
            var id = ReadString(item, "steamid");
            if (id.Length == 0)
            {
                continue;
            }
            steamIds.Add(id);
            friendSince[id] = ReadLong(item, "friend_since");
        }

        var summaries = new List<SteamPlayerSummary>();
        for (var i = 0; i < steamIds.Count; i += PlayerSummaryBatchSize)
        {
            var batch = steamIds.Skip(i).Take(PlayerSummaryBatchSize).ToList();
            summaries.AddRange(await GetPlayerSummariesAsync(batch, cancellationToken).ConfigureAwait(false));
        }

        var result = summaries.Select(s => new SteamFriendSummary
        {
            SteamId = s.SteamId,
            PersonaName = s.PersonaName,
            AvatarMedium = s.AvatarMedium,
            PersonaState = s.PersonaState,
            GameExtraInfo = s.GameExtraInfo,
            FriendSince = friendSince.TryGetValue(s.SteamId, out var since) ? since : 0,
        }).ToList();

        result.Sort((a, b) =>
        {
            var diff = OnlineWeight(a.PersonaState) - OnlineWeight(b.PersonaState);
            return diff != 0
                ? diff
                : string.Compare(a.PersonaName, b.PersonaName, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    public async Task<int?> GetSteamLevelAsync(CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync("IPlayerService/GetSteamLevel/v1", new Dictionary<string, string>
        {
            ["key"] = _apiKey,
            ["steamid"] = _steamId,
        }, cancellationToken).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("response", out var response)
            ? ReadInt(response, "player_level")
            : null;
    }

    public async Task<List<SteamAchievement>> GetAchievementsAsync(int appId, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await GetJsonAsync("ISteamUserStats/GetPlayerAchievements/v1", new Dictionary<string, string>
            {
                ["key"] = _apiKey,
                ["steamid"] = _steamId,
                ["appid"] = appId.ToString(CultureInfo.InvariantCulture),
                ["l"] = "english",
            }, cancellationToken).ConfigureAwait(false);

            var list = new List<SteamAchievement>();
            if (!TryGetArray(doc.RootElement, "playerstats", "achievements", out var achievements))
            {
                return list;
            }
            foreach (var item in achievements.EnumerateArray())
            {
                list.Add(new SteamAchievement
                {
                    ApiName = ReadString(item, "apiname"),
                    Achieved = ReadInt(item, "achieved"),
                    UnlockTime = ReadLong(item, "unlocktime"),
                    Name = ReadNullableString(item, "name"),
                    Description = ReadNullableString(item, "description"),
                });
            }
            return list;
        }
        catch (SteamApiException ex) when (ex.StatusCode == 400)
        {
            return new List<SteamAchievement>();
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string endpoint, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var query = string.Join("&", parameters.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
        var url = $"{BaseUrl}/{endpoint}?{query}";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SteamApiException((int)response.StatusCode, $"Steam API {endpoint} returned {(int)response.StatusCode}");
        }
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static SteamPlayerSummary ReadPlayer(JsonElement item) => new()
    {
        SteamId = ReadString(item, "steamid"),
        PersonaName = ReadString(item, "personaname"),
        ProfileUrl = ReadString(item, "profileurl"),
        Avatar = ReadString(item, "avatar"),
        AvatarMedium = ReadString(item, "avatarmedium"),
        AvatarFull = ReadString(item, "avatarfull"),
        PersonaState = (SteamPersonaState)ReadInt(item, "personastate"),
        CommunityVisibilityState = ReadInt(item, "communityvisibilitystate"),
        LastLogoff = item.TryGetProperty("lastlogoff", out var lastLogoff) ? lastLogoff.GetInt64() : null,
        GameExtraInfo = ReadNullableString(item, "gameextrainfo"),
        GameId = ReadNullableString(item, "gameid"),
    };

    private static bool TryGetArray(JsonElement root, string first, string second, out JsonElement array)
    {
        array = default;
        return root.TryGetProperty(first, out var nested) &&
            nested.TryGetProperty(second, out array) &&
            array.ValueKind == JsonValueKind.Array;
    }

    private static string ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? ""
            : "";

    private static string? ReadNullableString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static int ReadInt(JsonElement item, string name) =>
        item.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var value) ? value : 0;

    private static long ReadLong(JsonElement item, string name) =>
        item.TryGetProperty(name, out var prop) && prop.TryGetInt64(out var value) ? value : 0;

    private static int OnlineWeight(SteamPersonaState state) => state switch
    {
        SteamPersonaState.Online or SteamPersonaState.LookingToPlay or SteamPersonaState.LookingToTrade => 0,
        SteamPersonaState.Away or SteamPersonaState.Snooze => 1,
        SteamPersonaState.Busy => 2,
        _ => 3,
    };
}

internal sealed class SteamApiException : Exception
{
    public SteamApiException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
