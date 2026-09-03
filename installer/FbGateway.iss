; Inno Setup script for the FbGateway .exe installer.
;
; Built by .github/workflows/release.yml, which passes the version
; from the git tag:
;
;   iscc /DAppVersion=1.2.3 installer\FbGateway.iss
;
; AppId identifies the product across every release and must never
; change; changing it would make new versions install beside the old
; ones instead of replacing them.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName      "FbGateway"
#define AppPublisher "FbGateway"
#define AppExeName   "FbGateway.exe"
#define AppUrl       "https://github.com/kenanwahbeh/FbGateway"

[Setup]
AppId={{4DF9ABE1-3644-4B2B-9A42-B4742A9C6DB4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no

; Per-machine install into Program Files; asks for elevation once.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Matches the MSI's floor and the app's own requirements.
MinVersion=6.3

SetupIconFile=installer\FbGateway.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

OutputDir=artifacts
OutputBaseFilename=FbGateway-{#AppVersion}-x64-setup

; The payload is a self-contained .NET build, so it compresses well
; but is large; solid LZMA2 keeps the download reasonable.
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; \
  Description: "{cm:CreateDesktopIcon}"; \
  GroupDescription: "{cm:AdditionalIcons}"; \
  Flags: unchecked

[Files]
Source: "publish\*"; \
  DestDir: "{app}"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; \
  Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; \
  Flags: nowait postinstall skipifsilent
