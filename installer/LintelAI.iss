; Built by scripts/build-installer.ps1, which passes /DAppVersion=x.y.z
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "Lintel AI"
#define AppExe  "LintelAI.exe"

[Setup]
; Never change this GUID: it is how Windows recognises an upgrade of the same app.
AppId={{7B1F6C2A-4E0D-4B5C-9C1E-5A2D8E6F3B41}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Crane4
AppPublisherURL=https://github.com/Crane4/lintel-ai
AppSupportURL=https://github.com/Crane4/lintel-ai/issues
AppUpdatesURL=https://github.com/Crane4/lintel-ai/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install: no administrator prompt. The user can still pick "all users" at the dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts\installer
OutputBaseFilename=LintelAI-Setup-{#AppVersion}
SetupIconFile=..\src\LintelAI\Assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\win-x64\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

; Settings, history and chats live in %AppData%\LintelAI and are deliberately left alone on uninstall.
