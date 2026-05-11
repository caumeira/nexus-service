; qOS installer (Inno Setup 6)
; Builds qOS-Setup.exe from the AOT publish output.
;
;   Compile: ISCC.exe Qos.iss
;   Output:  installer\output\qOS-Setup.exe
;
; Behaviour:
;   - Single UAC prompt (PrivilegesRequired=admin)
;   - Stops any running qOS.exe and removes any pre-existing
;     scheduled task before laying down the new files
;   - Installs PawnIO kernel driver via pnputil
;   - Registers an "At Logon" scheduled task with elevated privileges so the
;     service starts automatically after every login
;   - Launches the service immediately and offers to open the dashboard

#define MyAppName "qOS"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Nexus qOS"
#define MyAppURL "https://nexusqos.com"
#define MyAppExeName "qOS.exe"
#define MyAppTaskName "QosService"
#define PublishDir "..\..\aot"

[Setup]
AppId={{8F2E3A4D-9C5B-4E7A-B1F8-3C2A5E9D0F12}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
; Default to per-user install (no UAC needed for future binary updates).
; The "Install for all users" checkbox on the directory page flips this to
; {commonpf64}\qOS at runtime.
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=yes
; Dir page kept ON so the user can pick install scope (per-user vs all-users)
; and override the folder via Browse. It is the only wizard page they see.
DisableDirPage=no
DisableReadyPage=yes
DisableFinishedPage=yes
PrivilegesRequired=admin
; Silence "you used per-user areas under admin" warning. Intentional: Inno's
; {localappdata} and {username} correctly resolve to the launching user's
; values even under UAC elevation, which is exactly what we want for
; per-user installs and per-user scheduled task registration.
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=qOS-Setup
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
Filename: "{sys}\pnputil.exe"; Parameters: "/add-driver ""{app}\pawnio\PawnIO.inf"" /install"; Flags: runhidden waituntilterminated; StatusMsg: "Installing PawnIO driver..."
; /RU "{username}" registers the task for the user who launched the installer
; (Inno's {username} returns the original user, not the elevated admin token),
; so the per-user vs all-users install scope still gets a per-user autostart.
Filename: "{sys}\schtasks.exe"; Parameters: "/Create /TN ""{#MyAppTaskName}"" /TR ""\""{app}\{#MyAppExeName}\"""" /SC ONLOGON /RL HIGHEST /F /RU ""{username}"""; Flags: runhidden waituntilterminated; StatusMsg: "Setting up auto-start..."
Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN ""{#MyAppTaskName}"""; Flags: runhidden waituntilterminated; StatusMsg: "Starting {#MyAppName}..."
Filename: "http://localhost:9400/"; Flags: shellexec nowait skipifsilent; StatusMsg: "Opening dashboard..."

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#MyAppTaskName}"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "DelTask"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#MyAppExeName} /F"; Flags: runhidden waituntilterminated; RunOnceId: "KillProc"

[Code]
var
  AllUsersCheckbox: TNewCheckBox;
  AllUsersLabel: TNewStaticText;
  PerUserDir: String;
  AllUsersDir: String;

procedure UpdateInstallDirByScope();
begin
  if AllUsersCheckbox.Checked then
    WizardForm.DirEdit.Text := AllUsersDir
  else
    WizardForm.DirEdit.Text := PerUserDir;
end;

procedure AllUsersCheckboxClick(Sender: TObject);
begin
  UpdateInstallDirByScope();
end;

procedure InitializeWizard();
begin
  PerUserDir  := ExpandConstant('{localappdata}') + '\Programs\{#MyAppName}';
  AllUsersDir := ExpandConstant('{commonpf64}') + '\{#MyAppName}';

  AllUsersCheckbox := TNewCheckBox.Create(WizardForm);
  AllUsersCheckbox.Parent  := WizardForm.SelectDirPage;
  AllUsersCheckbox.Left    := WizardForm.DirEdit.Left;
  AllUsersCheckbox.Top     := WizardForm.DirBrowseButton.Top + WizardForm.DirBrowseButton.Height + ScaleY(16);
  AllUsersCheckbox.Width   := WizardForm.DirEdit.Width;
  AllUsersCheckbox.Caption := 'Install for all users on this PC';
  AllUsersCheckbox.Checked := False;
  AllUsersCheckbox.OnClick := @AllUsersCheckboxClick;

  AllUsersLabel := TNewStaticText.Create(WizardForm);
  AllUsersLabel.Parent  := WizardForm.SelectDirPage;
  AllUsersLabel.Left    := AllUsersCheckbox.Left + ScaleX(20);
  AllUsersLabel.Top     := AllUsersCheckbox.Top + AllUsersCheckbox.Height + ScaleY(2);
  AllUsersLabel.Width   := WizardForm.DirEdit.Width - ScaleX(20);
  AllUsersLabel.AutoSize := False;
  AllUsersLabel.Height  := ScaleY(28);
  AllUsersLabel.Caption := 'Default is single-user (recommended). Switch to all-users if multiple Windows accounts on this PC need it.';
  AllUsersLabel.WordWrap := True;
end;

procedure StopServiceIfRunning();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM qOS.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/End /TN "QosService"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
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
