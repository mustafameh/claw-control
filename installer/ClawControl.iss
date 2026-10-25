; Setup program for Claw Control (Inno Setup 6). Built by build-release.ps1:
;   ISCC.exe /DAppVersion=0.1.0 /DSourceDir=<app files> installer\ClawControl.iss
; Checks the device and prerequisites before installing anything, installs to
; C:\Program Files\ClawControl, registers the "Claw Control" startup task (task.ps1) and starts it.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\bin\Release\net10.0-windows10.0.19041.0\win-x64"
#endif
#define RepoUrl "https://github.com/mustafameh/claw-control"

[Setup]
AppId={{C4E87B98-FE0C-4A41-BE4A-9A5AAD6985B3}
AppName=Claw Control
AppVersion={#AppVersion}
AppVerName=Claw Control {#AppVersion}
AppPublisher=mustafameh
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Claw Control setup
DefaultDirName={autopf}\ClawControl
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
LicenseFile=..\LICENSE
InfoBeforeFile=before-install.txt
OutputDir=..\dist
OutputBaseFilename=ClawControl-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Claw Control is closed by task.ps1 (the quit signal hands the MSI button back), not by Setup.
CloseApplications=no
UninstallDisplayName=Claw Control
UninstallDisplayIcon={app}\ClawControl.exe
SetupLogging=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "task.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
; Starts Claw Control if it isn't running (after a quit). Through the task, so no admin prompt.
Name: "{autoprograms}\Claw Control"; Filename: "{sys}\schtasks.exe"; Parameters: "/run /tn ""Claw Control"""; \
    IconFilename: "{app}\ClawControl.exe"; Comment: "Start Claw Control"; Flags: runminimized

[Run]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\task.ps1"" -Exe ""{app}\ClawControl.exe"""; \
    StatusMsg: "Setting Claw Control to start with Windows..."; Flags: runhidden waituntilterminated
Filename: "{#RepoUrl}#how-to-use"; Description: "Show how to use Claw Control"; Flags: postinstall shellexec skipifsilent unchecked

[UninstallRun]
; Quits Claw Control (MSI gets its button back) and removes the startup task.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\task.ps1"" -Remove"; \
    Flags: runhidden waituntilterminated; RunOnceId: "RemoveTask"

[UninstallDelete]
; Leftovers of a zip install or an older version.
Type: filesandordirs; Name: "{app}"

[Code]
const
  SupportedBoard = 'MS-1T8K';

function BiosValue(Name: String): String;
begin
  if not RegQueryStringValue(HKLM, 'HARDWARE\DESCRIPTION\System\BIOS', Name, Result) then
    Result := '';
  Result := Trim(Result);
end;

function InitializeSetup(): Boolean;
var
  Board, DeviceName: String;
begin
  Result := True;

  // 1. Made for, and only tested on, the MSI Claw A8: refuse anything else.
  Board := BiosValue('BaseBoardProduct');
  if CompareText(Board, SupportedBoard) <> 0 then
  begin
    DeviceName := Trim(BiosValue('SystemManufacturer') + ' ' + BiosValue('SystemProductName'));
    SuppressibleMsgBox('Claw Control is made only for the MSI Claw A8 (board ' + SupportedBoard + ').' + #13#10#13#10 +
      'This device is ' + DeviceName + ' (' + Board + ').' + #13#10#13#10 +
      'It writes the Claw A8''s firmware and controller settings, which could be wrong for any other ' +
      'device, so nothing will be installed.', mbCriticalError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;

  // 2. MSI Center M: controller modes and the MSI button work together with it.
  if not FileExists(ExpandConstant('{commonpf32}\MSI\MSI Center M\MSI_Center_M_Server.exe')) then
    if SuppressibleMsgBox('MSI Center M isn''t installed.' + #13#10#13#10 +
      'Claw Control runs alongside it: it switches the controller to gamepad mode through MSI Center M ' +
      'while the panel is open, and takes over its button.' + #13#10#13#10 +
      'Install MSI Center M from MSI''s support page for the Claw A8 (or the Microsoft Store), then run ' +
      'this setup again.' + #13#10#13#10 + 'Install anyway?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES then
    begin
      Result := False;
      Exit;
    end;

  // 3. AMD graphics driver, for the Graphics tab.
  if not FileExists(ExpandConstant('{sys}\amdadlx64.dll')) then
    if SuppressibleMsgBox('The AMD graphics driver wasn''t found, so the Graphics tab''s AMD settings won''t work.' + #13#10#13#10 +
      'Install the graphics driver from MSI''s support page for the Claw A8, then run this setup again.' + #13#10#13#10 +
      'Install anyway?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES then
      Result := False;
end;

// Quit a running copy before its files are replaced (it hands the MSI button back first).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  ExtractTemporaryFile('task.ps1');
  Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\task.ps1') + '" -Remove',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{localappdata}\ClawControl');
    if DirExists(Data) then
      if SuppressibleMsgBox('Also delete your Claw Control settings and log?' + #13#10#13#10 + Data,
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
