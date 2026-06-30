; Inno Setup script for WinShot
; Builds a per-user installer (no UAC prompt) that registers in
; Add/Remove Programs, adds a Start-menu entry, an optional desktop
; shortcut, and an optional "launch at sign-in" entry. Installs to
; %LOCALAPPDATA%\Programs\WinShot to match tools\install.ps1.

#define MyAppName "WinShot"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Isaiah Calvo"
#define MyAppURL "https://github.com/IsaiahCalvo/WinShot"
#define MyAppExeName "WinShot.exe"

[Setup]
AppId={{8F3C5D2A-7B41-4E96-A2D7-1C9F4B6E0A53}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=WinShot_{#MyAppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\WinShot\Assets\winshot.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
CloseApplications=force
RestartApplications=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "startupentry"; Description: "Launch {#MyAppName} when I sign in to Windows"; GroupDescription: "Startup:"

[Files]
Source: "..\publish\WinShot\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "register-autostart.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Durable autostart: register a per-user logon Scheduled Task instead of the HKCU "Run"
; key. Windows 11 throttles/delays Run-key startup apps, and the in-app updater runs this
; same installer, so a Run key here would silently regress the task-based fix on every update.
; The helper also strips any legacy Run value. runhidden keeps the PowerShell window invisible.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\register-autostart.ps1"" -Exe ""{app}\{#MyAppExeName}"" -TaskName ""WinShot Autostart"" -RunValueName ""WinShot"""; Tasks: startupentry; Flags: runhidden
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: nowait postinstall skipifsilent
; Silent updates (one-click in-app updater runs Setup.exe /SILENT) never fire postinstall
; entries, so the line above can't relaunch the app. This second entry runs ONLY under a
; silent install (skipifnotsilent), giving exactly one launch per mode: interactive uses the
; postinstall+skipifsilent line, silent uses this one.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait skipifnotsilent

[UninstallRun]
Filename: "schtasks.exe"; Parameters: "/delete /tn ""WinShot Autostart"" /f"; Flags: runhidden; RunOnceId: "DeleteWinShotAutostartTask"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
