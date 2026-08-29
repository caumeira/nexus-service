#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Nexus.Service.Platform;

namespace Nexus.Service.Platform.Windows;

/// <summary>
/// Interactive Windows toast, for the one notice that needs a button the user
/// can press. Runs in the user-session helper - the service sits in session 0
/// and cannot raise UI.
///
/// The tray balloon (<see cref="TrayIcon.ShowNoticeBalloon"/>) stays the
/// default for everything else: it needs no registration and cannot fail. A
/// balloon carries no action buttons though, so a notice that offers one comes
/// through here and falls back to the balloon whenever this path is
/// unavailable.
///
/// Unpackaged apps get no toast unless the process declares an
/// AppUserModelID that a Start-menu shortcut also carries, which is why
/// <see cref="EnsureAppUserModelId"/> must run before the first Show and why
/// the installer stamps the same id onto Nexus.lnk. That shortcut is also
/// where the toast's attribution icon comes from, so an install whose .lnk
/// predates the stamp shows the generic placeholder until an installer run
/// rewrites it. Activation is handled
/// in-process on <see cref="ToastNotification.Activated"/>; no COM activator is
/// registered, so a click only routes while the helper is alive - which it is
/// whenever it raised the toast.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ToastNotifications
{
    /// <summary>
    /// Must match the AppUserModelID stamped on the Start-menu shortcut
    /// (installer/Nexus.iss, [Icons]). A mismatch is silent: the toast is
    /// accepted and never drawn.
    /// </summary>
    public const string AppUserModelId = "HelloNexus.Nexus";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    private static bool _idSet;

    private static readonly object Gate = new();
    // Only one conflict toast is ever in flight, so a single slot is enough.
    private static ToastNotification? _liveToast;
    private static ToastNotifier? _liveNotifier;

    /// <summary>Stamps the process with the toast identity. Safe to call more than once.</summary>
    public static void EnsureAppUserModelId()
    {
        if (_idSet) return;
        try
        {
            // The installer's shortcut is the documented route, but a dev
            // deploy publishes over an install without re-running it, and a
            // user can delete the shortcut. Registering the id under HKCU as
            // well means the toast identity resolves either way; Windows reads
            // the display name from here.
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                       $@"Software\Classes\AppUserModelId\{AppUserModelId}"))
            {
                key?.SetValue("DisplayName", "Nexus", Microsoft.Win32.RegistryValueKind.String);
                // Fallback branding only. Measured on Windows 11: the icon
                // drawn beside the app name comes from the Start-menu shortcut
                // carrying this AppUserModelID (installer/Nexus.iss stamps it),
                // NOT from IconUri - a correct IconUri with an unstamped
                // shortcut still renders the generic placeholder. Kept for the
                // no-shortcut case, where it is the only branding there is.
                var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", "nexus-mark-color.png");
                if (System.IO.File.Exists(icon))
                {
                    var previous = key?.GetValue("IconUri") as string;
                    key?.SetValue("IconUri", icon, Microsoft.Win32.RegistryValueKind.String);
                    // Some Windows builds only render the attribution icon when
                    // a background colour is present alongside it. Opaque black
                    // matches the toast surface in both themes.
                    key?.SetValue("IconBackgroundColor", "FF000000", Microsoft.Win32.RegistryValueKind.String);
                    if (!string.Equals(previous, icon, StringComparison.OrdinalIgnoreCase))
                    {
                        InvalidateBrandingCache();
                    }
                }
                else
                {
                    HelperLog.Write($"[toast] attribution icon missing at {icon}; Windows draws its placeholder");
                }
            }
        }
        catch (Exception ex)
        {
            HelperLog.Write($"[toast] AppUserModelID registration failed: {ex.Message}");
        }

        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            _idSet = true;
        }
        catch (Exception ex)
        {
            HelperLog.Write($"[toast] AppUserModelID failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Windows caches an app's toast name and icon the first time it delivers a
    /// notification for an AppUserModelID, and keeps serving the cached pair
    /// even after the registration changes. Any toast raised before the icon was
    /// registered therefore pins the generic placeholder permanently. Dropping
    /// the platform's per-app settings key makes it re-read the registration on
    /// the next delivery; Windows recreates the key itself.
    /// </summary>
    private static void InvalidateBrandingCache()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{AppUserModelId}",
                throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            HelperLog.Write($"[toast] branding cache reset failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Raises a toast carrying a single action button. Returns false when the
    /// toast could not be handed to Windows, so the caller can fall back to the
    /// balloon; true only means Windows accepted it.
    /// </summary>
    /// <param name="windowPath">SPA path the button opens in the dashboard.</param>
    public static bool TryShowActionToast(string title, string text, string buttonLabel, string windowPath)
    {
        try
        {
            EnsureAppUserModelId();

            var xml = new XmlDocument();
            xml.LoadXml(
                "<toast activationType='foreground'>" +
                  "<visual><binding template='ToastGeneric'>" +
                    $"<text>{Escape(title)}</text>" +
                    $"<text>{Escape(text)}</text>" +
                  "</binding></visual>" +
                  "<actions>" +
                    $"<action content='{Escape(buttonLabel)}' arguments='open' activationType='foreground'/>" +
                  "</actions>" +
                "</toast>");

            var toast = new ToastNotification(xml);
            // Both the button and the toast body land here; either one means
            // "take me to the setting", so the arguments are not inspected.
            toast.Activated += (_, _) =>
            {
                try { TrayIcon.OpenLocalWindow(path: windowPath); }
                catch (Exception ex) { HelperLog.Write($"[toast] activation failed: {ex.Message}"); }
                finally { Release(); }
            };
            toast.Dismissed += (_, _) => Release();
            toast.Failed += (_, _) => Release();

            var notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            // Rooted until the toast resolves. Nothing else references either
            // object after Show returns, and the projected wrapper is what keeps
            // the Activated delegate alive - collect it and the button silently
            // does nothing, with no exception to fall back on.
            lock (Gate)
            {
                _liveToast = toast;
                _liveNotifier = notifier;
            }
            notifier.Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            HelperLog.Write($"[toast] show failed, falling back to balloon: {ex.Message}");
            return false;
        }
    }

    private static void Release()
    {
        lock (Gate)
        {
            _liveToast = null;
            _liveNotifier = null;
        }
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("'", "&apos;")
        .Replace("\"", "&quot;");
}
#endif
