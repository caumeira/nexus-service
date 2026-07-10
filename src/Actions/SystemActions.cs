using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Activity;
using Nexus.Service.Models;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Peripherals.Keeb;
using Nexus.Service.Platform.Clipboard;
using Nexus.Service.Platform.Power;

namespace Nexus.Service.Actions;

/// <summary>
/// Injectable body for the OS-level actions <c>SystemRoutes.cs</c> exposes
/// over REST. Extracted so <c>DeckActionExecutor</c> can drive the same
/// providers headless (no loopback HTTP call) for a physical Stream Deck
/// press. Routes delegate here so both callers share one implementation.
/// </summary>
public sealed class SystemActions
{
    private readonly IInputterProvider _inputter;
    private readonly IClipboardProvider _clipboard;
    private readonly ISystemPowerProvider _power;
    private readonly IAudioDeviceProvider _audio;
    private readonly IVolumeProvider _volume;
    private readonly IShortcutsProvider _shortcuts;
    private readonly IServiceProvider _sp;

    public SystemActions(
        IInputterProvider inputter,
        IClipboardProvider clipboard,
        ISystemPowerProvider power,
        IAudioDeviceProvider audio,
        IVolumeProvider volume,
        IShortcutsProvider shortcuts,
        IServiceProvider sp)
    {
        _inputter = inputter;
        _clipboard = clipboard;
        _power = power;
        _audio = audio;
        _volume = volume;
        _shortcuts = shortcuts;
        _sp = sp;
    }

    public ApiResponse SendKeys(SendKeysBody body)
    {
        var input = BuildKeyStrokes(body);
        if (input.Strokes.Count == 0)
        {
            return ApiResponse.Fail("key or strokes required");
        }
        _inputter.Send(input);
        return ApiResponse.Ok();
    }

    /// <summary>
    /// Clipboard-set then paste - the only Unicode-reliable cross-platform
    /// path. Clobbers the clipboard (restore deferred). Always pastes; there
    /// is no non-paste path (mirrors the pre-extraction route exactly).
    /// </summary>
    public ApiResponse SendText(string text)
    {
        if (text.Length == 0)
        {
            return ApiResponse.Fail("text required");
        }
        if (!_clipboard.SetText(text))
        {
            return ApiResponse.Fail("clipboard unavailable");
        }
        _inputter.Send(PasteChord());
        return ApiResponse.Ok();
    }

    public async Task<ApiResponse> OpenSettingsAsync()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
#if WINDOWS
                var registry = _sp.GetService<Nexus.Service.Helper.HelperRegistry>();
                if (registry?.GetAny() is not null)
                {
                    await Nexus.Service.Helper.Domains.SystemCommands.OpenSettingsAsync(registry).ConfigureAwait(false);
                    return ApiResponse.Ok("opened");
                }
                if (!Environment.UserInteractive)
                {
                    return ApiResponse.Fail("no interactive user session");
                }
#endif
                Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true });
                return ApiResponse.Ok("opened");
            }
            if (OperatingSystem.IsMacOS())
            {
                var exit = Nexus.Service.Platform.ShellExecutor.RunExit("open", 5000, "-b", "com.apple.systempreferences");
                return exit == 0 ? ApiResponse.Ok("opened") : ApiResponse.Fail("failed to open settings");
            }
            string[] linuxCandidates = ["gnome-control-center", "systemsettings5", "systemsettings"];
            foreach (var candidate in linuxCandidates)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(candidate) { UseShellExecute = false });
                    return ApiResponse.Ok("opened");
                }
                catch { /* launcher not installed - try the next */ }
            }
            return ApiResponse.Fail("no settings app found");
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail($"failed to open settings: {ex.Message}");
        }
    }

    public async Task<ApiResponse> OpenUrlAsync(string url)
    {
        var trimmed = url?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed))
        {
            return ApiResponse.Fail("url is required");
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != "http" && parsed.Scheme != "https"))
        {
            return ApiResponse.Fail("invalid url - must be an absolute http or https URL");
        }

        try
        {
#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                var registry = _sp.GetService<Nexus.Service.Helper.HelperRegistry>();
                if (registry?.GetAny() is not null)
                {
                    await Nexus.Service.Helper.Domains.SystemCommands.OpenUrlAsync(registry, parsed.AbsoluteUri).ConfigureAwait(false);
                    return ApiResponse.Ok("opened");
                }
                if (!Environment.UserInteractive)
                {
                    return ApiResponse.Fail("no interactive user session");
                }
            }
#endif
            await Task.CompletedTask.ConfigureAwait(false);
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
            return ApiResponse.Ok("opened");
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail($"failed to open url: {ex.Message}");
        }
    }

    /// <summary>LAN-only in the route (denied on the relay) - opens arbitrary local files.</summary>
    public async Task<ApiResponse> OpenPathAsync(string path)
    {
        var trimmed = path?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed))
        {
            return ApiResponse.Fail("path is required");
        }
        if (!File.Exists(trimmed) && !Directory.Exists(trimmed))
        {
            return ApiResponse.Fail("path does not exist");
        }

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var registry = _sp.GetService<Nexus.Service.Helper.HelperRegistry>();
            var helperConnected = registry?.GetAny() is not null;
            if (helperConnected)
            {
                if (Directory.Exists(trimmed))
                {
                    if (await Nexus.Service.Helper.Domains.FileDialogCommands.OpenFolderAsync(registry!, trimmed).ConfigureAwait(false))
                    {
                        return ApiResponse.Ok("opened");
                    }
                    return ApiResponse.Fail("failed to open folder");
                }

                await Nexus.Service.Helper.Domains.SystemCommands.OpenFileAsync(registry!, trimmed).ConfigureAwait(false);
                return ApiResponse.Ok("opened");
            }

            if (!Environment.UserInteractive)
            {
                return ApiResponse.Fail("no interactive user session");
            }
        }
#endif
        await Task.CompletedTask.ConfigureAwait(false);
        try
        {
            Process.Start(new ProcessStartInfo(trimmed) { UseShellExecute = true });
            return ApiResponse.Ok("opened");
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail($"failed to open path: {ex.Message}");
        }
    }

    public bool Lock() => _power.Lock();
    public bool Sleep() => _power.Sleep();
    public bool Shutdown() => _power.Shutdown();
    public bool Restart() => _power.Restart();
    public bool Logout() => _power.Logout();

    public bool SetDefaultOutput(string deviceId) => _audio.SetDefaultOutput(deviceId);
    public bool SetDefaultInput(string deviceId) => _audio.SetDefaultInput(deviceId);

    public VolumeState GetVolume() => _volume.GetState();
    public void SetVolume(double volume) => _volume.SetVolume(volume);
    public void SetMuted(bool muted) => _volume.SetMuted(muted);

    public bool LaunchShortcut(string targetId) => _shortcuts.Launch(targetId);

    /// <summary>
    /// Builds the inputter strokes for a key request. An explicit Strokes list
    /// wins; otherwise a single chord is expanded to a key-down then key-up
    /// (both carrying the modifier flags) so the combo presses and releases.
    /// </summary>
    private static InputterBody BuildKeyStrokes(SendKeysBody body)
    {
        if (body.Strokes is { Count: > 0 })
        {
            return new InputterBody { Strokes = body.Strokes };
        }
        if (string.IsNullOrEmpty(body.Key))
        {
            return new InputterBody();
        }
        return new InputterBody
        {
            Strokes =
            {
                new MacroStroke { Key = body.Key, Meta = body.Meta, Ctrl = body.Ctrl, Alt = body.Alt, Shift = body.Shift, Type = "keydown" },
                new MacroStroke { Key = body.Key, Meta = body.Meta, Ctrl = body.Ctrl, Alt = body.Alt, Shift = body.Shift, Type = "keyup" },
            },
        };
    }

    private static InputterBody PasteChord()
    {
        var mac = OperatingSystem.IsMacOS();
        MacroStroke V(string type) => new()
        {
            Key = "KeyV",
            Meta = mac,
            Ctrl = !mac,
            Type = type,
        };
        return new InputterBody { Strokes = { V("keydown"), V("keyup") } };
    }
}
