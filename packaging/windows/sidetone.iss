; SPDX-License-Identifier: AGPL-3.0-only
;
; Sidetone for Windows: the installer (Inno Setup 6). One setup .exe for x64 and ARM64 PCs: it holds both
; self-contained builds and installs the one the PC needs. Per user, no administrator needed: into
; %LOCALAPPDATA%\Programs\Sidetone, with a Start menu entry. Uninstalling removes the program, not settings,
; recordings or logs (%APPDATA%\sidetone, %LOCALAPPDATA%\sidetone).
;
; Built by CI:  ISCC /DAppVersion=0.35.0 /DSourceX64=out\win-x64 /DSourceArm64=out\win-arm64 packaging\windows\sidetone.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceX64
  #define SourceX64 "..\..\out\win-x64"
#endif
#ifndef SourceArm64
  #define SourceArm64 "..\..\out\win-arm64"
#endif

[Setup]
AppId={{6B7C2A1E-4F3D-4C8B-9A55-51D3E0A2C7F4}
AppName=Sidetone
AppVersion={#AppVersion}
AppVerName=Sidetone {#AppVersion}
AppPublisher=Sidetone
AppPublisherURL=https://github.com/monkey1911/Sidetone
DefaultDirName={autopf}\Sidetone
DefaultGroupName=Sidetone
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible or arm64
ArchitecturesInstallIn64BitMode=x64compatible or arm64
MinVersion=10.0.17763
OutputBaseFilename=Sidetone-{#AppVersion}-windows-setup
SetupIconFile=sidetone.ico
UninstallDisplayIcon={app}\sidetone.exe
UninstallDisplayName=Sidetone
LicenseFile=..\..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ChangesEnvironment=yes
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "addtopath"; Description: "Add sidetone-cli to PATH (the terminal console, and PTT from scripts and hotkey tools)"; Flags: unchecked

[Files]
; the build for this PC: ARM64 on ARM PCs, x64 on the rest
Source: "{#SourceArm64}\*"; DestDir: "{app}"; Check: IsArm64; Flags: recursesubdirs ignoreversion
Source: "{#SourceX64}\*"; DestDir: "{app}"; Check: not IsArm64; Flags: recursesubdirs ignoreversion
Source: "..\..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Sidetone"; Filename: "{app}\sidetone.exe"
Name: "{autodesktop}\Sidetone"; Filename: "{app}\sidetone.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NotOnPath(ExpandConstant('{app}'))

[Run]
Filename: "{app}\sidetone.exe"; Description: "Start Sidetone"; Flags: nowait postinstall skipifsilent

[Code]
function NotOnPath(Dir: string): Boolean;
var
  Path: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    Path := '';
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';') = 0;
end;

{ uninstalling takes the program's folder back off PATH, if it was added }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Path, Dir: string;
  P: Integer;
begin
  if CurUninstallStep <> usPostUninstall then
    exit;
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    exit;
  Dir := ExpandConstant('{app}');
  P := Pos(';' + Uppercase(Dir), Uppercase(Path));
  if P > 0 then
  begin
    Delete(Path, P, Length(Dir) + 1);
    RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Path);
  end;
end;
