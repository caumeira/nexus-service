using System;
using System.Collections.Generic;

namespace Nexus.Service.Models.Panel;

/// <summary>
/// Canonical <c>PanelDeviceCapabilities.Surface</c> values. The SPA infers
/// its surface from the viewport for self-registered devices; service-created
/// records (monitor promotion) stamp it explicitly.
/// </summary>
public static class PanelSurfaces
{
    public const string Y70 = "y70";
    public const string Q60 = "q60";
    public const string Phone = "phone";
    public const string Desktop = "desktop";
    /// <summary>A user-promoted OS monitor hosting a fullscreen panel kiosk.</summary>
    public const string Monitor = "monitor";

    /// <summary>
    /// Surfaces that are exactly one physical panel per host. They self-register
    /// over <c>POST /panel/devices</c> (no OS displayId to key on) and rely on
    /// the kiosk WebView's localStorage to reuse their record. The Q-series OEM
    /// shell drops that cache across reconnects, which would accrete a record
    /// every connect, so <see cref="Panel.PanelDeviceRegistry.Allocate"/> reuses
    /// existing record for these instead of minting a new one. Phones are
    /// multi-instance (many pair); promoted monitors are multi-instance and
    /// keyed by displayId.
    /// </summary>
    public static readonly IReadOnlySet<string> SingleInstance =
        new HashSet<string>(StringComparer.Ordinal) { Y70, Q60 };

    public static bool IsSingleInstance(string? surface) =>
        surface is not null && SingleInstance.Contains(surface);
}
