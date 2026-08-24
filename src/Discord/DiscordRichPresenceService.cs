using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Service.Persistence;

namespace Nexus.Service.Discord;

/// <summary>
/// Publishes Nexus' Discord Rich Presence. This is the only part of the
/// Discord integration that needs no OAuth and no approved <c>rpc</c> scope:
/// SET_ACTIVITY is accepted on a bare handshake, so the user's whole setup is
/// one toggle.
///
/// Discord rate-limits SET_ACTIVITY at roughly five calls per twenty seconds,
/// so the loop publishes only on an actual change AND never faster than
/// <see cref="MinPublishInterval"/>.
/// </summary>
public sealed class DiscordRichPresenceService : BackgroundService
{
    /// <summary>
    /// Nexus' own Discord application. Presence deliberately does not fall back
    /// to <c>DiscordSettings.ClientId</c>: that field holds the user's own
    /// application for the OAuth/RPC path, and borrowing it would publish their
    /// application's name as the game being played.
    /// </summary>
    private const string DefaultClientId = "1541547398999048362";

    private static readonly TimeSpan ConnectRetryMin = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectRetryMax = TimeSpan.FromSeconds(60);
    /// <summary>How long an established connection sits before it is re-checked for a Discord quit.</summary>
    private static readonly TimeSpan LivenessPoll = TimeSpan.FromSeconds(15);
    /// <summary>Floor between two SET_ACTIVITY calls. Discord allows ~5 per 20s; this leaves headroom.</summary>
    private static readonly TimeSpan MinPublishInterval = TimeSpan.FromSeconds(5);
    /// <summary>A refused handshake is retried this far out rather than latched off forever.</summary>
    private static readonly TimeSpan RejectedRetry = TimeSpan.FromMinutes(5);
    /// <summary>Bounds the shutdown clear so a wedged pipe cannot stall host shutdown.</summary>
    private static readonly TimeSpan ClearTimeout = TimeSpan.FromSeconds(2);

    private readonly IConfigStore _store;
    private readonly ILogger<DiscordRichPresenceService> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);

    private DiscordIpcConnection? _connection;
    /// <summary>The preset currently published, used to skip a redundant SET_ACTIVITY.</summary>
    private string? _appliedPreset;
    /// <summary>Start of the elapsed timer. Held across reconnects so a Discord restart does not reset it.</summary>
    private long _sessionStartUnix;
    private DateTime _lastPublishUtc = DateTime.MinValue;
    private TimeSpan _retryDelay = ConnectRetryMin;
    /// <summary>Set when Discord refuses the client id, so the loop backs off hard instead of hammering.</summary>
    private DateTime _rejectedUntilUtc = DateTime.MinValue;

    /// <summary>
    /// Last Discord settings the loop acted on. OnChanged fires for EVERY
    /// settings mutation in the process (hundreds of call sites, including
    /// per-request panel touches), so an unfiltered wake would cut the connect
    /// backoff short on unrelated traffic and re-probe all ten endpoints.
    /// </summary>
    private (bool Enabled, string Preset) _lastSeen;

    public DiscordRichPresenceService(IConfigStore store, ILogger<DiscordRichPresenceService> logger)
    {
        _store = store;
        _logger = logger;
        _lastSeen = ReadDesired();
        _store.OnChanged += Wake;
    }

    /// <summary>True while an RPC connection is live and the presence is published.</summary>
    public bool IsConnected
    {
        get
        {
            var connection = Volatile.Read(ref _connection);
            return connection is { IsClosed: false } && Volatile.Read(ref _appliedPreset) is not null;
        }
    }

    /// <summary>False when no Discord application id is compiled in or configured.</summary>
    public static bool IsAvailable => ResolveClientId().Length > 0;

    private (bool Enabled, string Preset) ReadDesired()
    {
        var settings = _store.Load().Discord;
        return (settings.RichPresenceEnabled, DiscordRichPresence.Normalize(settings.RichPresencePreset));
    }

    private void Wake()
    {
        var desired = ReadDesired();
        if (desired == _lastSeen)
        {
            return;
        }

        // A fresh enable is an explicit user retry: clear a standing refusal.
        if (desired.Enabled && !_lastSeen.Enabled)
        {
            _rejectedUntilUtc = DateTime.MinValue;
        }

        _lastSeen = desired;
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already pending; one wake is enough.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown raced this handler; nothing to wake.
        }
    }

    /// <summary>
    /// The OAuth/RPC path was removed along with its settings UI, so any
    /// client id and secret a user entered for it are now unreachable data
    /// they cannot delete. Clear them once, on the first start that sees them.
    /// </summary>
    private void ClearStrandedOAuthCredentials()
    {
        var settings = _store.Load().Discord;
        if (settings.ClientId.Length == 0 && settings.ClientSecret.Length == 0)
        {
            return;
        }

        _store.Update(s =>
        {
            s.Discord.ClientId = "";
            s.Discord.ClientSecret = "";
        });
        _logger.LogInformation("[discord-presence] cleared stored OAuth credentials (the RPC path was removed)");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ClearStrandedOAuthCredentials();

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await StepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[discord-presence] cycle failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
                DropConnection(resetSession: false);
                wait = Backoff();
            }

            try
            {
                await _wake.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }

        await ClearAndDisconnectAsync().ConfigureAwait(false);
    }

    /// <summary>Runs one reconcile pass and returns how long to idle before the next.</summary>
    private async Task<TimeSpan> StepAsync(CancellationToken cancellationToken)
    {
        var settings = _store.Load().Discord;
        var clientId = ResolveClientId();

        if (!settings.RichPresenceEnabled || clientId.Length == 0)
        {
            await ClearAndDisconnectAsync().ConfigureAwait(false);
            // Nothing to do until a setting changes, which wakes the loop.
            return Timeout.InfiniteTimeSpan;
        }

        var now = DateTime.UtcNow;
        if (now < _rejectedUntilUtc)
        {
            return _rejectedUntilUtc - now;
        }

        if (_connection is { IsClosed: true })
        {
            // Keep the session start: a Discord restart must not reset the
            // elapsed timer the user sees on their profile.
            DropConnection(resetSession: false);
        }

        if (_connection is null)
        {
            try
            {
                _connection = await DiscordIpcConnection
                    .ConnectAsync(clientId, cancellationToken, line => _logger.LogDebug("[discord-presence] {Line}", line))
                    .ConfigureAwait(false);
            }
            catch (DiscordIpcRejectedException ex)
            {
                // Usually a bad client id, but a transient close (Discord still
                // starting, another process on the endpoint) looks identical,
                // so back off hard rather than latching off for the process.
                _rejectedUntilUtc = DateTime.UtcNow + RejectedRetry;
                _logger.LogWarning("[discord-presence] Discord refused the handshake: {Message}", ex.Message);
                return RejectedRetry;
            }

            if (_connection is null)
            {
                // Discord is not running. Ordinary state, not an error.
                return Backoff();
            }

            _retryDelay = ConnectRetryMin;
            Volatile.Write(ref _appliedPreset, null);
            if (_sessionStartUnix == 0)
            {
                _sessionStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            _logger.LogInformation("[discord-presence] connected");
        }

        var preset = DiscordRichPresence.Normalize(settings.RichPresencePreset);
        if (!string.Equals(preset, Volatile.Read(ref _appliedPreset), StringComparison.Ordinal))
        {
            var sinceLast = DateTime.UtcNow - _lastPublishUtc;
            if (sinceLast < MinPublishInterval)
            {
                // Clicking down the preset list must not spend the rate budget.
                return MinPublishInterval - sinceLast;
            }

            await _connection
                .SetActivityAsync(writer => DiscordRichPresence.WriteActivity(writer, preset, _sessionStartUnix), cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _appliedPreset, preset);
            _lastPublishUtc = DateTime.UtcNow;
            _logger.LogDebug("[discord-presence] published {Preset}", preset);
        }

        return LivenessPoll;
    }

    private async Task ClearAndDisconnectAsync()
    {
        if (_connection is null)
        {
            return;
        }

        if (!_connection.IsClosed && Volatile.Read(ref _appliedPreset) is not null)
        {
            try
            {
                // Without an explicit clear the status lingers in Discord until
                // it notices our pid is gone, which outlives a toggle-off.
                using var timeout = new CancellationTokenSource(ClearTimeout);
                await _connection.SetActivityAsync(null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[discord-presence] clear failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
            }
        }

        // A deliberate stop ends the session, so the timer restarts next time.
        DropConnection(resetSession: true);
    }

    private void DropConnection(bool resetSession)
    {
        _connection?.Dispose();
        Volatile.Write(ref _connection, null);
        Volatile.Write(ref _appliedPreset, null);
        if (resetSession)
        {
            _sessionStartUnix = 0;
        }
    }

    private TimeSpan Backoff()
    {
        var current = _retryDelay;
        _retryDelay = TimeSpan.FromTicks(Math.Min(current.Ticks * 2, ConnectRetryMax.Ticks));
        return current;
    }

    private static string ResolveClientId()
    {
        var configured = Environment.GetEnvironmentVariable("NEXUS_DISCORD_PRESENCE_CLIENT_ID");
        return string.IsNullOrWhiteSpace(configured) ? DefaultClientId : configured.Trim();
    }

    public override void Dispose()
    {
        _store.OnChanged -= Wake;
        _connection?.Dispose();
        _wake.Dispose();
        base.Dispose();
    }
}
