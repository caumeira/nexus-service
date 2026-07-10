namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Persisted per-serial state (see <see cref="StreamedPanelStore"/>): keeps
/// the panel record identity stable across restarts and re-attaches so the
/// user's layout/theme survive, plus optional headless config overrides on
/// top of the device kind's <see cref="StreamedPanelProfile"/>.
/// </summary>
public sealed class StreamedPanelRecord
{
    public string PanelDeviceId { get; set; } = "";

    /// <summary>Overrides the discovery's default profile kind when set.</summary>
    public string? ProfileKind { get; set; }

    public int? Fps { get; set; }
    public int? BitrateKbps { get; set; }
}
