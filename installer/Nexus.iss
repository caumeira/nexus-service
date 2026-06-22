; Nexus installer (Inno Setup 6)
; Builds Nexus-Setup.exe from the AOT publish output.
;
;   Compile: ISCC.exe Nexus.iss
;   Output:  installer\output\Nexus-Setup.exe
;
; Behaviour:
;   - Single UAC prompt (PrivilegesRequired=admin)
;   - Extracts the AOT payload to %ProgramFiles%\Nexus\
;   - Calls Nexus.exe --install as one elevated step. That primitive
;     handles all the real work: stop+delete existing service, sc create
;     NexusService (LocalSystem, Automatic, depend=PawnIO), grant
;     SERVICE_START to Authenticated Users via DACL, install PawnIO,
;     write Add/Remove Programs reg, open the firewall, start the service.
;   - Drops a Start Menu shortcut to the dashboard.
;
; Uninstall calls Nexus.exe --uninstall which mirrors the install: stop
; service, sc delete, remove firewall rule + Add/Remove reg + shortcut.
; Inno then removes the install dir on top of that.

#define MyAppName "Nexus"
; Versions come from build-installer.ps1 (read from the VERSION file). The
; fallbacks only apply to a bare ISCC run with no /D overrides.
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef MyAppVersionInfo
  #define MyAppVersionInfo "0.0.0.0"
#endif
#define MyAppPublisher "Nexus"
#define MyAppURL "https://hellonexus.com"
#define MyAppExeName "Nexus.exe"
#ifndef PublishDir
  #define PublishDir "..\..\aot"
#endif

[Setup]
AppId={{8F2E3A4D-9C5B-4E7A-B1F8-3C2A5E9D0F12}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersionInfo}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
; Machine-scope only: Nexus runs as a LocalSystem Windows Service, which is
; inherently shared by every account on the PC. The per-user install
; option from the previous schtask era is gone.
DefaultDirName={commonpf64}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableDirPage=no
DisableReadyPage=yes
DisableFinishedPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=Nexus-Setup
OutputDir=output
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardSmallImageFile=logo-small.bmp
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\icon.ico
ShowLanguageDialog=no
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
; Soften Inno's default "Some elements could not be removed" warning.
; The process-stop logic in [Code] should ensure we always reach
; UninstalledAll, but if a stray file is held open we'd rather not
; alarm the user.
UninstalledMost=%1 uninstall complete.%n%nA few files were still in use and will be cleaned up on next sign-in.

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\Open {#MyAppName} Dashboard"; Filename: "http://localhost:9400/"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"

[Run]
; Single canonical install call. The --install primitive registers the
; Windows Service, installs PawnIO, opens the firewall, writes Add/Remove
; Programs, and starts the service. It is idempotent so re-running this
; installer is safe.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--install"; Flags: runhidden waituntilterminated; StatusMsg: "Installing Nexus service..."
; Open the dashboard as the chromeless --app window (overlay WebView2, Edge --app
; fallback), the same as the tray's "Open dashboard". runasoriginaluser drops the
; installer's elevation so it launches in the user session, like the tray's
; schtasks path; without it the window would spawn elevated/in the wrong session.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--open-app"; Flags: nowait skipifsilent runasoriginaluser; StatusMsg: "Opening dashboard..."

[UninstallRun]
; Mirrors install: --uninstall stops + deletes the service, removes the
; firewall rule, Add/Remove reg key, and Start Menu shortcut. Inno's
; built-in uninstall then removes the install dir.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "NexusUninstall"

[Code]
// Win32 imports used to lift the wizard above other windows after an
// elevated relaunch. Without this, Windows' foreground-lock can leave
// the wizard behind the user's existing windows (Explorer, browser,
// etc.) which makes the install look like it stalled.
function SetForegroundWindow(hWnd: Integer): Boolean;
  external 'SetForegroundWindow@user32.dll stdcall';
function ShowWindow(hWnd: Integer; nCmdShow: Integer): Boolean;
  external 'ShowWindow@user32.dll stdcall';
function AllowSetForegroundWindow(dwProcessId: DWORD): Boolean;
  external 'AllowSetForegroundWindow@user32.dll stdcall';

procedure BringWizardToFront();
begin
  if WizardForm <> nil then
  begin
    AllowSetForegroundWindow($FFFFFFFF); // ASFW_ANY
    ShowWindow(WizardForm.Handle, 9);    // SW_RESTORE
    WizardForm.BringToFront();
    SetForegroundWindow(WizardForm.Handle);
  end;
end;

procedure InitializeWizard();
begin
  BringWizardToFront();
end;

procedure StopServiceIfRunning();
var
  ResultCode: Integer;
begin
  // Every process holding a payload file open must be gone before [Files], or
  // Inno reboot-renames the locked file and that pending entry then blocks every
  // later install. Order matters:
  //   1. net stop (not sc stop) blocks until the service reports STOPPED. The
  //      service shuts down in ~1s and reports a clean stop, so no failure-action
  //      restart races us, and its job-owned OpenRGB / overlay are already gone.
  //   2. Nexus.exe: the user-session helper shares this image and keeps
  //      Nexus.exe locked even after the service stops. No /T (it descends into
  //      the matched tree, which could catch Inno's own helper).
  //   3. Sidecar taskkills are a belt in case a kill-job hadn't reaped them yet.
  Exec(ExpandConstant('{sys}\net.exe'), 'stop NexusService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Nexus.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM OpenRGB.exe /F /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM nexus-overlay.exe /F /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopServiceIfRunning();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopServiceIfRunning();
end;
