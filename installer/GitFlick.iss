; Inno Setup script for GitFlick — a Windows install wizard that lets the user choose the install
; folder, creates Start-Menu (and optional desktop) shortcuts, and registers an uninstaller.
; The portable zip artifact is unchanged; this is just an alternative to it.
;
; Built by scripts/build-installer.ps1 (locally) and by .github/workflows/release.yml (CI) with:
;   ISCC.exe /DAppVersion=<x.y.z> /DSourceDir=<publish folder> installer\GitFlick.iss
; Requires Inno Setup 6.3+ (for the x64compatible architecture identifier).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  ; Default assumes a `publish` folder at the repo root next to this `installer` folder.
  #define SourceDir "..\publish"
#endif

#define AppName "GitFlick"
#define AppPublisher "HouseAlwaysWin"
#define AppExeName "GitFlick.exe"
#define AppUrl "https://github.com/HouseAlwaysWin/GitFlick"

[Setup]
; Stable app identity — used for upgrades/uninstall. Do NOT change once shipped.
AppId={{D96B1A83-957C-47A2-94E1-93BE6503CE88}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}

; Per-user by default (no UAC prompt); the user may switch to all-users on the first page.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

DefaultDirName={autopf}\{#AppName}
DisableDirPage=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=output
OutputBaseFilename=GitFlick_Setup_{#AppVersion}
SetupIconFile=..\GitFlick\Assets\gitflick.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Close a running GitFlick before overwriting (reinstall / manual upgrade).
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole publish output (the single-file GitFlick.exe).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
