; DeskPods installer. Inno Setup 6.
;
; Rules that must not change between releases:
;   * AppId is permanent. It is what lets a new setup replace the previous
;     install instead of creating a second entry in Apps & features.
;   * PrivilegesRequired=lowest, so no UAC prompt and no admin account needed.
;   * The app is installed into %LOCALAPPDATA%\Programs\PodsView, exactly where
;     scripts/install-release.ps1 has always put it, so the old START.cmd route
;     and this installer share one install directory and one shortcut set.
;   * User data (settings.json, logs, signal memory) lives in
;     %LOCALAPPDATA%\PodsView and is never touched by install or uninstall.

#define MyAppName "DeskPods"
#define MyAppVersion "0.8.47"
#define MyAppPublisher "DeskPods"
#define MyAppURL "https://github.com/Stasieps/DeskPods"
#define MyAppExeName "PodsView.exe"

#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{9F41B0C6-27D4-4E0B-9B35-1C6A7E5D2A84}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\PodsView
DefaultGroupName={#MyAppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename=DeskPods_Setup_v{#MyAppVersion}
SetupIconFile=..\src\PodsView\Assets\deskpods.ico
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=force
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.StartupTask=Start DeskPods with Windows
en.StartupGroup=Startup:
uk.StartupTask=Запускати DeskPods разом із Windows
uk.StartupGroup=Автозапуск:
ru.StartupTask=Запускать DeskPods вместе с Windows
ru.StartupGroup=Автозапуск:

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "{cm:StartupTask}"; GroupDescription: "{cm:StartupGroup}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Same value name the PowerShell installer has always used, so the two routes
; can never leave two autostart entries behind.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "PodsView"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; \
    ValueName: "PodsView"; Flags: deletevalue uninsdeletevalue; Tasks: not startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
; 0.8.46: the in-app updater runs this setup with /SILENT; start the new build afterwards.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// The running app holds its own exe open. Inno's file-in-use dialog would put
// that in front of a non-technical user, so the previous instance is closed
// quietly instead. Settings and logs live elsewhere and are untouched.
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/c taskkill /IM PodsView.exe /F', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;
