; Inno Setup script for the Easy FB Soft .exe installer.
;
; Built by .github/workflows/release.yml. One script builds both
; distributions:
;
;   ; .NET bundled
;   iscc /DAppVersion=1.2.3 installer\EasyFbSoft.iss
;
;   ; .NET required on the machine
;   iscc /DAppVersion=1.2.3 /DPublishDir=publish-fx ^
;        /DVariant=-framework /DRequireRuntime=1 installer\EasyFbSoft.iss
;
; PublishDir is named relative to the repo root; the script adds the
; ..\ itself.
;
; AppId identifies the product across every release and both
; distributions, so a machine ends up with one Easy FB Soft rather
; than two competing installs. It must never change.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "publish"
#endif

#ifndef Variant
  #define Variant ""
#endif

#define AppName      "Easy FB Soft"
#define AppPublisher "Easy FB Soft"
#define AppExeName   "EasyFbSoft.exe"
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

; Paths here are relative to this .iss file, not to the directory
; ISCC was launched from, so the repo root is one level up.
SetupIconFile=EasyFbSoft.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

OutputDir=..\artifacts
OutputBaseFilename=EasyFbSoft-{#AppVersion}-x64{#Variant}-setup

; The bundled build is a self-contained .NET tree, so it compresses
; well but is large; solid LZMA2 keeps the download reasonable.
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
Source: "..\{#PublishDir}\*"; \
  DestDir: "{app}"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; \
  Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; \
  Flags: nowait postinstall skipifsilent

#ifdef RequireRuntime
[Code]

{
  This distribution does not carry .NET, so refuse to install when the
  runtime is missing rather than leaving behind an app that exits
  silently on launch.

  The check looks for a real 10.x shared-framework directory instead
  of a registry version string, so it cannot be fooled by a leftover
  key from an uninstalled runtime.
}
function HasDesktopRuntime10(): Boolean;
var
  Rec: TFindRec;
  Base: String;
begin
  Result := False;

  Base := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');

  if FindFirst(Base + '\10.*', Rec) then
  begin
    try
      repeat
        if (Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          Result := True;
      until Result or (not FindNext(Rec));
    finally
      FindClose(Rec);
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;

  if not HasDesktopRuntime10() then
  begin
    if MsgBox('This installer does not include .NET, and the .NET 10 Desktop Runtime (x64) was not found on this computer.'
              + #13#10#13#10
              + 'Either install the runtime, or download the standard installer instead, which bundles .NET and needs no prerequisites.'
              + #13#10#13#10
              + 'Open the .NET download page now?',
              mbError, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0',
                '', '', SW_SHOW, ewNoWait, ErrorCode);
    end;

    Result := False;
  end;
end;
#endif
