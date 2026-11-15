; UE MM (UEModManager) Windows x64 Installer Script (Inno Setup 6)
; Keeps user data portable in {app}\Data (AppPaths.NextToApp), so per-user writable
; install paths are enforced and existing Data folders are preserved across upgrades/uninstalls.

#ifndef AppVersion
  #define AppVersion "2.2.0"
#endif

#ifndef AppPublisher
  #define AppPublisher "KintaroModding"
#endif

#ifndef AppUrl
  #define AppUrl "https://github.com/HumanMus1c/uemodmanager"
#endif

#ifndef SourceDir
  #define SourceDir "..\..\dist\.build\publish"
#endif

#ifndef OutputDir
  #define OutputDir "..\..\dist\release"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "uemm_" + AppVersion + "_x64_setup"
#endif

#define AppName "UE MM"
#define AppExeName "UEModManager.exe"

[Setup]
; Stable upgrade identifier for HumanMus1c/UEModManager; never reuse from another application.
AppId={{7A9E6B42-8F3D-4C21-B15A-9D4E2F6C8A10}
AppName={#AppName}
AppVerName={#AppName} {#AppVersion}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
DefaultDirName={localappdata}\Programs\UEModManager
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2/max
SolidCompression=yes
SetupIconFile=..\..\src\UEManager.UI\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile=..\..\LICENSE
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UsePreviousAppDir=yes
UsePreviousTasks=yes
UsePreviousLanguage=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "launchAtStartup"; Description: "{cm:AutoStartProgram,{#AppName}}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Only deploy the single-file executable so existing {app}\Data (mods, settings, saves) is never touched.
Source: "{#SourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: launchAtStartup

[Registry]
Root: HKCU; Subkey: "Software\UEModManager"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\UEModManager"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  PrevInstallDetected: Boolean;
  PrevInstallPath: String;
  PrevInstallVersion: String;

function GetPreviousInstallInfo(): Boolean;
begin
  PrevInstallPath := '';
  PrevInstallVersion := '';
  if RegQueryStringValue(HKCU, 'Software\UEModManager', 'InstallPath', PrevInstallPath) and (PrevInstallPath <> '') then
  begin
    RegQueryStringValue(HKCU, 'Software\UEModManager', 'Version', PrevInstallVersion);
    Result := True;
  end
  else
    Result := False;
end;

procedure StopProcessAtPath(const ExePath: String);
var
  Escaped, Command: String;
  ResultCode: Integer;
begin
  if not FileExists(ExePath) then
    exit;
  Escaped := ExePath;
  StringChangeEx(Escaped, '''', '''''', True);
  Command := '-NoProfile -NonInteractive -WindowStyle Hidden -Command "Get-Process -Name UEModManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq ''' + Escaped + ''' } | Stop-Process -Force"';
  Exec('powershell.exe', Command, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup(): Boolean;
begin
  PrevInstallDetected := GetPreviousInstallInfo();
  if PrevInstallDetected then
    Log('Upgrade detected: ' + PrevInstallVersion + ' at ' + PrevInstallPath);
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // Only stop a running instance from the target {app} folder so separate portable instances keep running.
  StopProcessAtPath(ExpandConstant('{app}\{#AppExeName}'));
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopProcessAtPath(ExpandConstant('{app}\{#AppExeName}'));
  Result := True;
end;
