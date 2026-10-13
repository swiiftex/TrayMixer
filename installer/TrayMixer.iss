; Tray Mixer installer (Inno Setup 6). Build it with scripts\package.ps1, which passes:
;   /DAppVersion=2.0.0 /DNumericVersion=2.0.0.0 /DSourceDir=<app files> /DOutputDir=<folder>
; Installs per user (no admin prompt) into %LOCALAPPDATA%\Programs\TrayMixer.

#ifndef AppVersion
  #error Build the installer with scripts\package.ps1
#endif

[Setup]
AppId={{3F58B924-A272-4B8E-A0EC-4E93B2D906C9}
AppName=Tray Mixer
AppVersion={#AppVersion}
AppPublisher=swiiftex
VersionInfoVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
DefaultDirName={autopf}\TrayMixer
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableDirPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=TrayMixer-{#AppVersion}-setup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\TrayMixer.exe
UninstallDisplayName=Tray Mixer
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
MinVersion=10.0.19041
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start Tray Mixer when I sign in (it lives in the tray)"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Tray Mixer"; Filename: "{app}\TrayMixer.exe"; Comment: "Volume mixer for every app"

[Registry]
; Same entry the app's own "Start with Windows" setting manages.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TrayMixer"; \
    ValueData: """{app}\TrayMixer.exe"" --tray"; Tasks: startup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "TrayMixer"; \
    Flags: deletevalue; Tasks: not startup
; Removed on uninstall even if it was turned on from the app rather than here.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "TrayMixer"; \
    Flags: uninsdeletevalue

[Run]
Filename: "{app}\TrayMixer.exe"; Description: "Open Tray Mixer"; Flags: nowait postinstall skipifsilent
; Silent installs (updates) bring the tray icon back without opening the mixer.
Filename: "{app}\TrayMixer.exe"; Parameters: "--tray"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{app}\TrayMixer.exe"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitTrayMixer"

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  ResultCode: Integer;
begin
  // A running copy is asked to close cleanly (it hands Discord's sound effects their normal volume back) before
  // its files are replaced.
  Exe := ExpandConstant('{app}\TrayMixer.exe');
  if FileExists(Exe) then
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
