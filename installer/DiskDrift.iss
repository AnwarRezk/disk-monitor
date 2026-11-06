; DiskDrift setup (Inno Setup 6). Build it with installer/build.ps1, which publishes both apps first.
; It does what scripts/install-service.ps1 does: Collector as the LocalSystem service "DiskDrift", Tray started at
; every user's logon, an existing appsettings.json kept. It adds Start menu / desktop shortcuts and an uninstaller.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
; Never change AppId: upgrades and the uninstaller find the installed copy by it.
AppId={{B805704F-2D17-4F87-894B-3D50083B6E50}
AppName=DiskDrift
AppVersion={#AppVersion}
AppVerName=DiskDrift {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=Anwar Rezk
AppPublisherURL=https://github.com/AnwarRezk/disk-monitor
AppSupportURL=https://github.com/AnwarRezk/disk-monitor/issues
; Program Files, so only administrators can replace the binary SYSTEM runs. The Tray finds ..\Collector\appsettings.json.
DefaultDirName={autopf}\DiskDrift
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename=DiskDrift-Setup-{#AppVersion}
SetupIconFile=..\assets\diskdrift.ico
WizardStyle=modern
WizardImageFile=wizard-large-164.bmp,wizard-large-328.bmp
WizardSmallImageFile=wizard-small-55.bmp,wizard-small-110.bmp
UninstallDisplayName=DiskDrift
UninstallDisplayIcon={app}\Tray\DiskDrift.Tray.exe
Compression=lzma2/max
SolidCompression=yes
; service.ps1 stops the service and every Tray itself; the Restart Manager would only ask about the Trays.
CloseApplications=no
RestartApplications=no
#ifdef SignCommand
SignTool=sign
SignedUninstaller=yes
#endif

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[InstallDelete]
; The Tray folder is replaced whole, so files a newer build no longer ships don't linger.
Type: filesandordirs; Name: "{app}\Tray"

[Files]
Source: "{#PublishDir}\collector\*"; DestDir: "{app}\Collector"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
; Local config survives upgrades: the shipped defaults are only copied when there is none yet.
Source: "{#PublishDir}\collector\appsettings.json"; DestDir: "{app}\Collector"; Flags: onlyifdoesntexist
Source: "{#PublishDir}\tray\*"; DestDir: "{app}\Tray"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "service.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; Machine-wide, so the Tray starts at every user's logon.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DiskDriftTray"; ValueData: """{app}\Tray\DiskDrift.Tray.exe"""; Flags: uninsdeletevalue

[Icons]
; --dashboard opens the dashboard, starting the Tray first if it isn't running.
Name: "{autoprograms}\DiskDrift"; Filename: "{app}\Tray\DiskDrift.Tray.exe"; Parameters: "--dashboard"; Comment: "Open the DiskDrift dashboard"
Name: "{autodesktop}\DiskDrift"; Filename: "{app}\Tray\DiskDrift.Tray.exe"; Parameters: "--dashboard"; Comment: "Open the DiskDrift dashboard"; Tasks: desktopicon

[Run]
; The setup runs elevated; runasoriginaluser starts the Tray as the signed-in user, un-elevated.
Filename: "{app}\Tray\DiskDrift.Tray.exe"; Flags: nowait runasoriginaluser; StatusMsg: "Starting the DiskDrift tray icon..."
Filename: "{app}\Tray\DiskDrift.Tray.exe"; Parameters: "--dashboard"; Description: "Open the DiskDrift dashboard"; Flags: nowait postinstall runasoriginaluser skipifsilent

[UninstallDelete]
; Anything left in the program folders (e.g. files from an older build). The data folder is handled in [Code].
Type: filesandordirs; Name: "{app}\Collector"
Type: filesandordirs; Name: "{app}\Tray"

[Code]
// Runs service.ps1 (Stop, Install or Remove). Returns '' on success, otherwise what went wrong.
function RunServiceStep(Script, Action: String): String;
var
  ResultCode: Integer;
  ErrorFile: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := '';
  ErrorFile := ExpandConstant('{tmp}\service-error.txt');
  DeleteFile(ErrorFile);
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + Script + '" -Action ' + Action +
      ' -AppDir "' + ExpandConstant('{app}') + '" -ErrorFile "' + ErrorFile + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := 'Could not run PowerShell: ' + SysErrorMessage(ResultCode)
  else if ResultCode <> 0 then
  begin
    Result := 'The DiskDrift service step "' + Action + '" failed (exit code ' + IntToStr(ResultCode) + ').';
    if LoadStringsFromFile(ErrorFile, Lines) then
      for I := 0 to GetArrayLength(Lines) - 1 do
        Result := Result + #13#10 + Lines[I];
  end;
  Log('service.ps1 ' + Action + ': ' + Result);
end;

// Before any file is replaced: stop the service and every running Tray (an upgrade or a reinstall).
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  ExtractTemporaryFile('service.ps1');
  Result := RunServiceStep(ExpandConstant('{tmp}\service.ps1'), 'Stop');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Error: String;
begin
  if CurStep = ssPostInstall then
  begin
    Error := RunServiceStep(ExpandConstant('{app}\service.ps1'), 'Install');
    if Error <> '' then
      SuppressibleMsgBox(Error + #13#10#13#10 + 'The files are installed. Check the service with "sc.exe query DiskDrift".',
        mbError, MB_OK, IDOK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Error: String;
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    Error := RunServiceStep(ExpandConstant('{app}\service.ps1'), 'Remove');
    if (Error <> '') and not UninstallSilent then
      MsgBox(Error, mbError, MB_OK);
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    // The history and logs are kept by default, so a reinstall continues the same timeline.
    DataDir := ExpandConstant('{commonappdata}\DiskDrift');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox('Also delete DiskDrift''s history and logs in ' + DataDir + '?' + #13#10#13#10 +
          'Choose No to keep them for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
