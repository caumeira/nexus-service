using System;
using System.Collections.Generic;
using System.Text;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Platform;

namespace Nexus.Service.Sensors.Astral;

/// <summary>
/// Per-adapter gate + cache for the Astral 12VHPWR supplement. Detection is
/// list-free: any NVIDIA GPU whose PCI subsystem vendor is ASUS gets probed
/// for the Astral power-monitor IC; a failed probe (non-Astral ASUS card, or
/// any NVAPI error) leaves the adapter with no synthesized sensors. AIB name
/// enrichment (<see cref="AstralAibVendors"/>) applies to every recognized
/// vendor regardless of whether the Astral probe ever succeeds.
///
/// State is keyed by the adapter index LibreHardwareMonitor's NvidiaGroup
/// assigns (parsed out of the LHM hardware Identifier, "/gpu-nvidia/{index}")
/// - the same index this instance's IAstralNvApiClient uses against its own
/// NvAPI_EnumPhysicalGPUs call, since LHM's public IHardware surface exposes
/// no PCI ids to match on directly.
/// </summary>
internal sealed class AstralGpuSupplement
{
    private readonly IAstralNvApiClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _probeInterval;
    private readonly Dictionary<int, GpuState> _states = new();

    public AstralGpuSupplement(IAstralNvApiClient client) : this(client, TimeProvider.System, TimeSpan.FromSeconds(1))
    {
    }

    internal AstralGpuSupplement(IAstralNvApiClient client, TimeProvider timeProvider, TimeSpan probeInterval)
    {
        _client = client;
        _timeProvider = timeProvider;
        _probeInterval = probeInterval;
    }

    private sealed class GpuState
    {
        public int AdapterIndex;
        public bool HasSubSystemId;
        public uint SubSystemId;
        public DateTimeOffset NextProbeUtc;
        public bool LoggedFirstAttempt;
        public AstralTelemetryParser.AstralReadout? LastReadout;
    }

    /// <summary>
    /// AIB-enriched display name for the NVIDIA GPU at <paramref name="hwIdentifier"/>.
    /// Returns <paramref name="lhmName"/> unchanged when the subsystem vendor
    /// can't be read or isn't recognized.
    /// </summary>
    public string EnrichName(string hwIdentifier, string lhmName)
    {
        if (!TryGetState(hwIdentifier, out var state))
        {
            return lhmName;
        }
        return AstralAibVendors.EnrichName(lhmName, state.SubSystemId, state.LastReadout.HasValue);
    }

    /// <summary>
    /// Appends the Astral 12VHPWR sensors to <paramref name="result"/> if this
    /// adapter's subsystem vendor is ASUS and a probe has ever succeeded.
    /// Probes at most once per <see cref="_probeInterval"/> per adapter.
    /// </summary>
    public void AppendSensors(string hwIdentifier, string hwId, string hwName, List<HardwareSensor> result)
    {
        if (!TryGetState(hwIdentifier, out var state))
        {
            return;
        }

        if ((state.SubSystemId & 0xFFFF) != AstralAibVendors.AsusVendorId)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (now >= state.NextProbeUtc)
        {
            state.NextProbeUtc = now + _probeInterval;
            Probe(state, state.AdapterIndex);
        }

        if (state.LastReadout is { } readout)
        {
            AstralSensorBuilder.Append(hwId, hwName, readout, result);
        }
    }

    private bool TryGetState(string hwIdentifier, out GpuState state)
    {
        state = null!;
        if (!TryParseAdapterIndex(hwIdentifier, out var index))
        {
            return false;
        }

        if (!_states.TryGetValue(index, out var existing))
        {
            existing = new GpuState { AdapterIndex = index };
            _states[index] = existing;
        }
        state = existing;

        if (!state.HasSubSystemId && _client.TryGetPciSubsystemId(index, out var subSystemId))
        {
            state.HasSubSystemId = true;
            state.SubSystemId = subSystemId;
        }

        return state.HasSubSystemId;
    }

    private void Probe(GpuState state, int adapterIndex)
    {
        var ok = _client.TryReadAstralBlock(adapterIndex, out var block);
        AstralTelemetryParser.AstralReadout? readout = null;
        if (ok)
        {
            try
            {
                readout = AstralTelemetryParser.Parse(block);
            }
            catch (ArgumentException)
            {
                ok = false;
            }
        }

        if (!state.LoggedFirstAttempt)
        {
            state.LoggedFirstAttempt = true;
            LogFirstAttempt(adapterIndex, ok, block, readout);
        }

        if (ok)
        {
            state.LastReadout = readout;
        }
    }

    private static void LogFirstAttempt(
        int adapterIndex, bool ok, byte[] block, AstralTelemetryParser.AstralReadout? readout)
    {
        var hex = Convert.ToHexString(block);
        if (!ok || readout is not { } r)
        {
            ServiceLog.Info($"[astral] gpu{adapterIndex} first 0x56 read failed, raw={hex}");
            return;
        }

        var summary = new StringBuilder();
        for (var i = 0; i < r.Pins.Length; i++)
        {
            var pin = r.Pins[i];
            summary.Append($"pin{i + 1}={pin.VoltageVolts:F3}V/{pin.CurrentAmps:F3}A ");
        }
        summary.Append($"connector={r.ConnectorCurrentAmps:F3}A/{r.ConnectorPowerWatts:F1}W");

        ServiceLog.Info($"[astral] gpu{adapterIndex} first 0x56 read ok, raw={hex}, {summary}");
    }

    private static bool TryParseAdapterIndex(string hwIdentifier, out int index)
    {
        index = -1;
        if (string.IsNullOrEmpty(hwIdentifier))
        {
            return false;
        }
        var slash = hwIdentifier.LastIndexOf('/');
        var tail = slash >= 0 ? hwIdentifier[(slash + 1)..] : hwIdentifier;
        return int.TryParse(tail, out index);
    }
}
