; Qos installer (Inno Setup 6)
; Builds Qos-Setup.exe from the AOT publish output.
;
;   Compile: ISCC.exe Qos.iss
;   Output:  installer\output\Qos-Setup.exe
;
; Behaviour:
;   - Single UAC prompt (PrivilegesRequired=admin)
;   - Extracts the AOT payload to %ProgramFiles%\Qos\
;   - Calls Qos.exe --install as one elevated step. That primitive
;     handles all the real work: stop+delete existing service, sc create
;     QosService (LocalSystem, Automatic, depend=PawnIO), grant
;     SERVICE_START to Authenticated Users via DACL, install PawnIO,
;     write Add/Remove Programs reg, open the firewall, start the service.
;   - Drops a Start Menu shortcut to the dashboard.
;
; Uninstall calls Qos.exe --uninstall which mirrors the install: stop
; service, sc delete, remove firewall rule + Add/Remove reg + shortcut.
; Inno then removes the install dir on top of that.

#define MyAppName "Qos"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Nexus Qos"
#define MyAppURL "https://nexusqos.com"
#define MyAppExeName "Qos.exe"
#define PublishDir "..\..\aot"

[Setup]
AppId={{8F2E3A4D-9C5B-4E7A-B1F8-3C2A5E9D0F12}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
; Machine-scope only: Qos runs as a LocalSystem Windows Service, which is
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
OutputBaseFilename=Qos-Setup
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
Filename: "{app}\{#MyAppExeName}"; Parameters: "--install"; Flags: runhidden waituntilterminated; StatusMsg: "Installing Qos service..."
Filename: "http://localhost:9400/"; Flags: shellexec nowait skipifsilent; StatusMsg: "Opening dashboard..."

[UninstallRun]
; Mirrors install: --uninstall stops + deletes the service, removes the
; firewall rule, Add/Remove reg key, and Start Menu shortcut. Inno's
; built-in uninstall then removes the install dir.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "QosUninstall"

[Code]
procedure StopServiceIfRunning();
var
  ResultCode: Integer;
begin
  // Best-effort stop before we overwrite files. The --install step will
  // also stop+delete the service, but doing it here too means we never
  // try to overwrite a locked Qos.exe during the [Files] copy.
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop QosService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Qos.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
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
