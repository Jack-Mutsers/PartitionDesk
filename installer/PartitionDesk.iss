; Inno Setup script for PartitionDesk.
; Build: dotnet publish (see build-installer.ps1), then compile this file with ISCC.exe.

#define AppName "PartitionDesk"
; The version is passed in by build-installer.ps1 (/DAppVersion=x.y.z); this is only a fallback.
#ifndef AppVersion
  #define AppVersion "0.0.0-local"
#endif
#define AppExe "PartitionDesk.exe"

[Setup]
AppId={{B4E2D9C1-7A3F-4E8B-9C61-2F5A8D0E3B47}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Jack Mutsers
AppPublisherURL=https://github.com/Jack-Mutsers/PartitionDesk
AppCopyright=Copyright (C) 2026 Jack Mutsers. Licensed under GPL-3.0.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\Assets\PartitionDesk.ico
OutputDir=..\dist
OutputBaseFilename=PartitionDesk-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; The app always runs elevated, so it is installed for all users.
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent shellexec
