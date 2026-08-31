using Microsoft.Extensions.Hosting;
using Nexus.Service.Obs;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.FocusModes;

/// <summary>
/// Feeds the obs trigger by polling OBS for stream/record state.
///
/// Polls only while some mode actually uses the trigger, so an install with no
/// streaming mode never opens the OBS socket. GetStatusAsync connects on demand
/// and reports Connected=false when OBS is closed, which reads as "not live".
/// </summary>
public sealed class FocusObsTrigger : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly FocusModeState _state;
    private readonly IObsProvider _obs;
    private readonly IConfigStore _config;

    public FocusObsTrigger(FocusModeState state, IObsProvider obs, IConfigStore config)
    {
        _state = state;
        _obs = obs;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await PollAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { ServiceLog.Warn($"[focus] obs poll failed: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        if (!AnyModeUsesObs())
        {
            _state.SetObsLive(false);
            return;
        }

        var status = await _obs.GetStatusAsync(ct).ConfigureAwait(false);
        _state.SetObsLive(status.Connected && (status.Streaming || status.Recording));
    }

    private bool AnyModeUsesObs()
    {
        try
        {
            var focus = _config.Load().Focus;
            return focus is not null
                && focus.Modes.Any(m => m.Trigger == FocusTriggers.Obs && m.AutoActivate);
        }
        catch
        {
            return false;
        }
    }
}
