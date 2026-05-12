namespace Qos.Service.Models.Panel;

public sealed class PanelPhonePairQrResponse
{
    public string Url { get; set; } = "";
    public string QrDataUrl { get; set; } = "";
    public string MachineName { get; set; } = "";
    public int TtlSeconds { get; set; }
    public long ExpiresAt { get; set; }
    /// <summary>
    /// Plain-HTTP port for the browser fallback. The QR's `port` field carries
    /// the HTTPS port for the native iOS app (which pins SPKI); browsers can't
    /// trust the self-signed LAN cert, so the web path lands here instead.
    /// </summary>
    public int HttpPort { get; set; }
}

public sealed class PanelPhoneClaimBody
{
    public string PairToken { get; set; } = "";
}

public sealed class PanelPhoneClaimResponse
{
    public bool Paired { get; set; }
    public string Token { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class PanelPhoneServiceInfoResponse
{
    public string MachineName { get; set; } = "";
}

public sealed class PanelPhoneSessionNameBody
{
    public string Name { get; set; } = "";
}

public sealed class PanelHostNameBody
{
    /// <summary>Empty / whitespace clears the override and falls back to Environment.MachineName on the next read.</summary>
    public string Name { get; set; } = "";
}

public sealed class PanelHostNameResponse
{
    public string MachineName { get; set; } = "";
}

public sealed class PanelPhoneSessionsResponse
{
    public int ConnectedCount { get; set; }
    public int AuthorizedCount { get; set; }
    public long SessionIdleMs { get; set; }
    public long Now { get; set; }
    public List<PanelPhoneSessionDto> Sessions { get; set; } = new();
}

public sealed class PanelPhoneSessionDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DeviceType { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string RemoteAddress { get; set; } = "";
    public long CreatedAt { get; set; }
    public long LastSeenAt { get; set; }
    public long ExpiresAt { get; set; }
    public bool RecentlyActive { get; set; }
}

public sealed class PanelStatusResponse
{
    public string Msg { get; set; } = "";
    public bool KioskRunning { get; set; }
    public bool PhoneConnected { get; set; }
    public int PhoneSubscribers { get; set; }
}

public sealed class RemoteControlStateResponse
{
    public bool Enabled { get; set; }
}

public sealed class RemoteControlToggleRequest
{
    public bool Enabled { get; set; }
}
