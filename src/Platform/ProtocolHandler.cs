using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Qos.Service.Platform;

/// <summary>
/// Registers the qos:// custom protocol handler so browsers can launch the service.
/// Self-registers on first run — no installer needed, no admin rights.
///
/// Windows: HKCU\Software\Classes\qos → shell\open\command → exe path
/// macOS:   ~/Library/Services/ .plist (or relies on app bundle CFBundleURLTypes)
/// Linux:   ~/.local/share/applications/qos.desktop + xdg-mime
/// </summary>
public static class ProtocolHandler
{
    public static void Register()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                RegisterWindows();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                RegisterMacOS();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                RegisterLinux();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[protocol] failed to register qos:// handler: {ex.Message}");
        }
    }

    private static void RegisterWindows()
    {
        // HKCU doesn't need admin.
        // Detect whether we're running as a native AOT exe or via `dotnet <dll>`.
        // Environment.ProcessPath returns dotnet.exe when run via `dotnet`, so we
        // check for the native exe first, then fall back to a `dotnet <dll>` command.
        var nativeExe = Path.Combine(AppContext.BaseDirectory, "Qos.exe");
        string command;
        string iconPath;

        if (File.Exists(nativeExe))
        {
            // Published AOT binary — register the exe directly.
            command = $"\"{nativeExe}\" \"%1\"";
            iconPath = $"\"{nativeExe}\",0";
        }
        else
        {
            // Dev / framework-dependent — register as `dotnet <dll>`.
            var dllPath = Path.Combine(AppContext.BaseDirectory, "qos-service.dll");
            var dotnetPath = Environment.ProcessPath ?? "dotnet";
            // If ProcessPath is the dotnet host, use it; otherwise find dotnet on PATH.
            if (!dotnetPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                && !dotnetPath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                dotnetPath = "dotnet";
            }
            command = $"\"{dotnetPath}\" \"{dllPath}\" \"%1\"";
            iconPath = $"\"{dllPath}\",0";
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\qos");
        key.SetValue("", "URL:Qos Protocol");
        key.SetValue("URL Protocol", "");

        using var iconKey = key.CreateSubKey("DefaultIcon");
        iconKey.SetValue("", iconPath);

        using var cmdKey = key.CreateSubKey(@"shell\open\command");
        cmdKey.SetValue("", command);

        Console.WriteLine($"[protocol] registered qos:// handler (Windows registry): {command}");
    }

    private static void RegisterMacOS()
    {
        // Create a minimal .app bundle wrapper that registers the URL scheme.
        var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "qos-service");

        // Detect dev mode: ProcessPath is the dotnet host, not our binary.
        // In that case, launch via `dotnet <dll>` instead of the bare exe.
        var isDotnetHost = exePath.EndsWith("/dotnet") || exePath.EndsWith("/dotnet.exe");
        string launchCommand;
        if (isDotnetHost)
        {
            var dllPath = Path.Combine(AppContext.BaseDirectory, "qos-service.dll");
            launchCommand = $@"exec ""{exePath}"" ""{dllPath}"" ""$@""";
        }
        else
        {
            launchCommand = $@"exec ""{exePath}"" ""$@""";
        }

        // Create a minimal .app bundle in ~/Applications/
        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Applications", "Qos.app");
        var contentsDir = Path.Combine(appDir, "Contents");
        var macosDir = Path.Combine(contentsDir, "MacOS");

        Directory.CreateDirectory(macosDir);

        // Info.plist with CFBundleURLTypes
        var plist = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>CFBundleIdentifier</key>
    <string>com.nexusqos.panel.launcher</string>
    <key>CFBundleName</key>
    <string>Qos</string>
    <key>CFBundleExecutable</key>
    <string>qos-launcher</string>
    <key>CFBundleURLTypes</key>
    <array>
        <dict>
            <key>CFBundleURLName</key>
            <string>Qos Protocol</string>
            <key>CFBundleURLSchemes</key>
            <array>
                <string>qos</string>
            </array>
        </dict>
    </array>
</dict>
</plist>";

        File.WriteAllText(Path.Combine(contentsDir, "Info.plist"), plist);

        // Shell script launcher that starts the actual service
        var launcher = $"#!/bin/bash\n{launchCommand}\n";
        var launcherPath = Path.Combine(macosDir, "qos-launcher");
        File.WriteAllText(launcherPath, launcher);
        // chmod +x
        System.Diagnostics.Process.Start("chmod", $"+x \"{launcherPath}\"")?.WaitForExit(3000);

        // Register with Launch Services
        System.Diagnostics.Process.Start("/System/Library/Frameworks/CoreServices.framework/Versions/A/Frameworks/LaunchServices.framework/Versions/A/Support/lsregister",
            $"-R \"{appDir}\"")?.WaitForExit(3000);

        Console.WriteLine("[protocol] registered qos:// handler (macOS app bundle)");
    }

    private static void RegisterLinux()
    {
        var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "qos-service");
        var appsDir = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "applications");

        Directory.CreateDirectory(appsDir);

        var desktop = $@"[Desktop Entry]
Name=Qos
Exec=""{exePath}"" %u
Type=Application
NoDisplay=true
MimeType=x-scheme-handler/qos;
";
        File.WriteAllText(Path.Combine(appsDir, "qos.desktop"), desktop);

        // Register as default handler
        System.Diagnostics.Process.Start("xdg-mime",
            "default qos.desktop x-scheme-handler/qos")?.WaitForExit(3000);

        Console.WriteLine("[protocol] registered qos:// handler (Linux .desktop)");
    }
}
