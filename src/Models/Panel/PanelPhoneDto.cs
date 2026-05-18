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

// Manual pair-code flow (BT-SSP-style numeric comparison). Additive to the
// QR flow for camera-less devices. The phone POSTs the typed code, server
// returns a SAS bound to the leaf SPKI; user visually compares SAS on both
// screens and dual-approves before a session token is issued.

public sealed class PanelPhonePairCodeStartResponse
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Code { get; set; } = "";
    public int TtlSeconds { get; set; }
    public long ExpiresAt { get; set; }
}

public sealed class PanelPhonePairCodeSubmitBody
{
    public string Code { get; set; } = "";
}

public sealed class PanelPhonePairCodeSubmitResponse
{
    public bool Accepted { get; set; }
    public string RequestId { get; set; } = "";
    public string Sas { get; set; } = "";
    public string SpkiFingerprint { get; set; } = "";
    public string MachineName { get; set; } = "";
    public long ExpiresAt { get; set; }
    public string Error { get; set; } = "";
    public int RetryAfterSeconds { get; set; }
}

public sealed class PanelPhonePairCodeConfirmBody
{
    public string RequestId { get; set; } = "";
    public bool Approved { get; set; }
}

public sealed class PanelPhonePairCodeConfirmResponse
{
    /// <summary>One of: waiting-host, approved, denied, expired, unknown.</summary>
    public string Status { get; set; } = "";
    public string Token { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string SpkiFingerprint { get; set; } = "";
}

public sealed class PanelPhonePairCodeHostDecisionBody
{
    public string RequestId { get; set; } = "";
    public bool Approved { get; set; }
}

public sealed class PanelPhonePairCodeHostDecisionResponse
{
    public string Status { get; set; } = "";
}

/// <summary>
/// Multiplex frame for the dashboard. <c>Kind</c> = "request" means a phone
/// just submitted the active code and is awaiting host confirmation.
/// "cancelled" carries a <c>Reason</c> (expired, phone-denied, host-denied,
/// host-started-new-code) so the dashboard can clear its prompt.
/// </summary>
public sealed class PanelPhonePairCodeRequestFrame
{
    public string Kind { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string Sas { get; set; } = "";
    public string DeviceLabel { get; set; } = "";
    public string RemoteAddress { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public long ExpiresAt { get; set; }
    public string Reason { get; set; } = "";
}
