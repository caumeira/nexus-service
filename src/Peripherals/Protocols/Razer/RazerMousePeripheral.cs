using System.Collections.Generic;
using Nexus.Service.Peripherals.Capabilities;

namespace Nexus.Service.Peripherals.Protocols.Razer;

/// <summary>
/// Generic Razer mouse handler driven by <see cref="RazerMouseProfile"/>. Adding
/// a new mouse is one row in <see cref="RazerMouseProfiles"/>.
///
/// Protocol references:
///   - Framing / CRC: <c>driver/razercommon.c</c>
///   - Command ids:   <c>driver/razerchromacommon.c</c>
///   - transaction_id switch: <c>driver/razermouse_driver.c</c> (set_razer_report + set/get_mouse_polling_rate)
///   - DPI_MAX + capabilities: <c>daemon/openrazer_daemon/hardware/mouse.py</c>
/// </summary>
public sealed class RazerMousePeripheral : IPeripheral, IDpiCapability, IPollingRateCapability, IBatteryCapability, ISleepCapability
{
    private const byte Varstore = 0x01;

    private readonly RazerClient _client;
    private readonly RazerMouseProfile _profile;
    private readonly int _vid;
    private readonly int _pid;
    private readonly string _serial;

    public RazerMousePeripheral(RazerClient client, RazerMouseProfile profile, int vid, int pid, string serial)
    {
        _client = client;
        _profile = profile;
        _vid = vid;
        _pid = pid;
        _serial = serial;
    }

    private RazerReport Cmd(byte commandClass, byte commandId, byte dataSize, byte[] args)
    {
        var r = RazerReport.Command(commandClass, commandId, dataSize, args);
        r.TransactionId = _profile.TransactionId;
        return r;
    }

    // --- IPeripheral ---
    public string Id => $"razer-{Slug(_profile.Name)}-{_serial}";
    public string Name => $"Razer {_profile.Name}";
    public string Vendor => "Razer";
    public string Category => "mouse";
    public int VendorId => _vid;
    public int ProductId => _pid;
    public string Serial => _serial;
    public string FirmwareVersion => "";
    public bool IsWireless => _profile.HasBattery;

    public IReadOnlyList<string> Capabilities
    {
        get
        {
            var caps = new List<string> { "dpi", "polling" };
            if (_profile.HasBattery)
                caps.Add("battery");
            if (_profile.HasSleep)
                caps.Add("sleep");
            return caps;
        }
    }

    public T? GetCapability<T>() where T : class, IPeripheralCapability
    {
        if (typeof(T) == typeof(IDpiCapability))
            return this as T;
        if (typeof(T) == typeof(IPollingRateCapability))
            return this as T;
        if (typeof(T) == typeof(IBatteryCapability) && _profile.HasBattery)
            return this as T;
        if (typeof(T) == typeof(ISleepCapability) && _profile.HasSleep)
            return this as T;
        return null;
    }

    private static string Slug(string s) => s.ToLowerInvariant()
        .Replace(' ', '-').Replace('(', '-').Replace(')', '-').Replace('.', '-').Replace("--", "-").TrimEnd('-');

    // --- IDpiCapability ---
    public int MinDpi => 100;
    public int MaxDpi => _profile.MaxDpi;
    public int Step => 50;
    public int StageCount => 0;
    public int ActiveStage => -1;
    public IReadOnlyList<int> StageDpi => System.Array.Empty<int>();

    public int GetCurrent()
    {
        var req = Cmd(0x04, 0x85, 0x07, new byte[] { Varstore, 0, 0, 0, 0, 0, 0 });
        var reply = _client.Exchange(req);
        if (reply is null)
            return 0;
        return (reply.Arguments[1] << 8) | reply.Arguments[2];
    }

    public bool SetDpi(int dpi)
    {
        if (dpi < MinDpi)
            dpi = MinDpi;
        if (dpi > MaxDpi)
            dpi = MaxDpi;
        var hi = (byte)((dpi >> 8) & 0xFF);
        var lo = (byte)(dpi & 0xFF);
        var args = new byte[] { Varstore, hi, lo, hi, lo, 0, 0 };
        var req = Cmd(0x04, 0x05, 0x07, args);
        return _client.Exchange(req, readResponse: false) is not null;
    }

    public bool SetActiveStage(int index) => false;
    public bool SetStageDpi(int stageIndex, int dpi) => false;

    // --- IPollingRateCapability ---
    public IReadOnlyList<int> SupportedHz => _profile.PollingVariant == RazerPollingVariant.HyperPolling
        ? new[] { 125, 500, 1000, 2000, 4000, 8000 }
        : new[] { 125, 500, 1000 };

    public int GetCurrentHz()
    {
        if (_profile.PollingVariant == RazerPollingVariant.HyperPolling)
        {
            // razer_chroma_misc_get_polling_rate2: class 0x00, id 0xC0, data_size 0x01
            var req = Cmd(0x00, 0xC0, 0x01, new byte[] { 0x00 });
            var reply = _client.Exchange(req);
            if (reply is null)
                return 0;
            return MapHyperPollCode(reply.Arguments[0]);
        }
        else
        {
            // razer_chroma_misc_get_polling_rate: class 0x00, id 0x85, data_size 0x01
            var req = Cmd(0x00, 0x85, 0x01, new byte[] { 0x00 });
            var reply = _client.Exchange(req);
            if (reply is null)
                return 0;
            return MapStdPollCode(reply.Arguments[0]);
        }
    }

    public bool SetHz(int hz)
    {
        if (_profile.PollingVariant == RazerPollingVariant.HyperPolling)
        {
            byte code = InvMapHyperPoll(hz);
            if (code == 0)
                return false;
            // razer_chroma_misc_set_polling_rate2: class 0x00, id 0x40, data_size 0x02; args[0]=varstore, args[1]=code
            var req = Cmd(0x00, 0x40, 0x02, new byte[] { Varstore, code });
            return _client.Exchange(req, readResponse: false) is not null;
        }
        else
        {
            byte code = InvMapStdPoll(hz);
            if (code == 0)
                return false;
            var req = Cmd(0x00, 0x05, 0x01, new byte[] { code });
            return _client.Exchange(req, readResponse: false) is not null;
        }
    }

    private static int MapStdPollCode(byte code) => code switch
    {
        0x01 => 1000,
        0x02 => 500,
        0x08 => 125,
        _ => 0,
    };
    private static byte InvMapStdPoll(int hz) => hz switch
    {
        1000 => 0x01,
        500 => 0x02,
        125 => 0x08,
        _ => (byte)0,
    };
    private static int MapHyperPollCode(byte code) => code switch
    {
        0x01 => 8000,
        0x02 => 4000,
        0x04 => 2000,
        0x08 => 1000,
        0x10 => 500,
        0x20 => 250,
        0x40 => 125,
        _ => 0,
    };
    private static byte InvMapHyperPoll(int hz) => hz switch
    {
        8000 => 0x01,
        4000 => 0x02,
        2000 => 0x04,
        1000 => 0x08,
        500 => 0x10,
        250 => 0x20,
        125 => 0x40,
        _ => (byte)0,
    };

    // --- IBatteryCapability ---
    public int GetPercent()
    {
        var req = Cmd(0x07, 0x80, 0x02, new byte[] { 0x00, 0x00 });
        var reply = _client.Exchange(req);
        if (reply is null)
            return -1;
        var raw = reply.Arguments[1];
        return (int)System.Math.Round(raw * 100.0 / 255.0);
    }

    public bool IsCharging()
    {
        var req = Cmd(0x07, 0x84, 0x02, new byte[] { 0x00, 0x00 });
        var reply = _client.Exchange(req);
        if (reply is null)
            return false;
        return reply.Arguments[1] == 0x01;
    }

    // --- ISleepCapability ---
    public int GetIdleSeconds()
    {
        var req = Cmd(0x07, 0x83, 0x02, new byte[] { 0x00, 0x00 });
        var reply = _client.Exchange(req);
        if (reply is null)
            return -1;
        return (reply.Arguments[0] << 8) | reply.Arguments[1];
    }

    public bool SetIdleSeconds(int seconds)
    {
        if (seconds < 60)
            seconds = 60;
        if (seconds > 900)
            seconds = 900;
        var hi = (byte)((seconds >> 8) & 0xFF);
        var lo = (byte)(seconds & 0xFF);
        var req = Cmd(0x07, 0x03, 0x02, new byte[] { hi, lo });
        return _client.Exchange(req, readResponse: false) is not null;
    }

    public int GetLowBatteryPercent()
    {
        var req = Cmd(0x07, 0x81, 0x01, new byte[] { 0x00 });
        var reply = _client.Exchange(req);
        if (reply is null)
            return -1;
        var raw = reply.Arguments[0];
        return (int)System.Math.Round(raw * 100.0 / 255.0);
    }

    public bool SetLowBatteryPercent(int percent)
    {
        if (percent < 0)
            percent = 0;
        if (percent > 100)
            percent = 100;
        var raw = (byte)System.Math.Round(percent * 255.0 / 100.0);
        var req = Cmd(0x07, 0x01, 0x01, new byte[] { raw });
        return _client.Exchange(req, readResponse: false) is not null;
    }
}
