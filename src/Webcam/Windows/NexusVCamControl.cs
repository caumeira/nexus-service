using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Webcam.Windows;

/// <summary>
/// P/Invoke wrapper over the flat C exports of the bundled NexusVCam media
/// source DLL (ControlApi.cpp). The flat surface exists precisely so the AOT
/// service avoids COM interop for camera lifetime - the DLL owns the
/// IMFVirtualCamera. HKLM CLSID registration belongs to the installer; this
/// class only verifies it and reports actionable errors, because the Frame
/// Server resolves the media source from the machine hive on its own.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class NexusVCamControl : IVCamControl
{
    /// <summary>Must match Guids.h and the installer registration.</summary>
    internal const string ClsidKeyPath =
        @"HKEY_LOCAL_MACHINE\Software\Classes\CLSID\{85867876-6949-4489-B9DA-7D719F81B50F}\InprocServer32";

    private const string LibraryName = "NexusVCam";

    internal static string BundledDllPath => Path.Combine(AppContext.BaseDirectory, "vcam", "NexusVCam.dll");

    static NexusVCamControl()
    {
        // The DLL ships in a subdirectory of the install root, outside default
        // probing; resolve it explicitly, falling back to the registered copy
        // so a dev build can drive an installer-deployed DLL. Note: only one
        // resolver may be registered per assembly.
        NativeLibrary.SetDllImportResolver(typeof(NexusVCamControl).Assembly, static (name, _, _) =>
        {
            if (name != LibraryName)
                return IntPtr.Zero;
            var path = File.Exists(BundledDllPath) ? BundledDllPath : RegisteredDllPath();
            return path is not null && File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
        });
    }

    public void EnsureAvailable()
    {
        var registered = RegisteredDllPath();
        if (registered is null)
        {
            if (!File.Exists(BundledDllPath))
            {
                throw new InvalidOperationException(
                    $"Nexus Camera media source missing at {BundledDllPath}; reinstall Nexus");
            }
            throw new InvalidOperationException(
                $"Nexus Camera media source is not registered; run elevated: regsvr32 \"{BundledDllPath}\"");
        }
        if (!File.Exists(registered))
        {
            throw new InvalidOperationException(
                $"Nexus Camera media source is registered at {registered} but the file is missing; re-run the installer or regsvr32 \"{BundledDllPath}\"");
        }
    }

    public int Create(string friendlyName, bool allUsers, out nint handle) =>
        NexusVCamCreate(friendlyName, allUsers ? 1 : 0, out handle);

    public int Destroy(nint handle) => NexusVCamDestroy(handle);

    // The Camera Frame Server won't activate a virtual-camera source until the
    // machine-wide "let desktop apps use the camera" consent is DECIDED; an
    // undecided (missing) value makes MFCreateVirtualCamera hang forever from
    // the LocalSystem service. Grant it (Allow) only when undecided — never
    // override an explicit user Deny — and only from the start path, the moment
    // the user turns the webcam on (their opt-in), never at install. The same
    // toggle is what call apps need to see any camera, so it is inherent to
    // using the feature, not an extra grant.
    //
    // Returns true when this call JUST granted consent. The caller must then
    // NOT attempt the create on this pass: the Frame Server already cached the
    // undecided state, so a create now blocks on a consent prompt SYSTEM can't
    // answer (and wedges the Frame Server). The next start sees decided consent
    // and proceeds. Best-effort: a locked-down ACL returns false and the create
    // surfaces its own actionable error.
    private const string WebcamConsentKey =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    public static bool EnsureDesktopCameraConsent()
    {
        var granted = AllowIfUndecided(WebcamConsentKey);
        granted |= AllowIfUndecided(WebcamConsentKey + @"\NonPackaged");
        return granted;
    }

    private static bool AllowIfUndecided(string subKey)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey, writable: true)
                ?? Microsoft.Win32.Registry.LocalMachine.CreateSubKey(subKey);
            if (key is null)
                return false;
            if (key.GetValue("Value") is string existing && existing.Length > 0)
                return false; // user (or policy) already decided — leave it
            key.SetValue("Value", "Allow", Microsoft.Win32.RegistryValueKind.String);
            return true;
        }
        catch
        {
            // Locked-down ACL / non-Windows test host: ignore; the camera
            // create surfaces its own actionable error if consent is the gate.
            return false;
        }
    }

    private static string? RegisteredDllPath() =>
        Microsoft.Win32.Registry.GetValue(ClsidKeyPath, "", null) as string;

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NexusVCamCreate(string? friendlyName, int allUsers, out nint handle);

    [LibraryImport(LibraryName)]
    private static partial int NexusVCamDestroy(nint handle);
}
