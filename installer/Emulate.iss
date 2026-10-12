; Inno Setup script - builds Emulate-Setup.exe in CI.
; Installs per-user (no admin prompt) so the in-app updater can replace files without elevation.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6C2F4B7A-3E7D-4C55-9C1B-0E5A1D7E2B91}
AppName=Emulate
AppVersion={#MyAppVersion}
AppPublisher=Emulate
AppPublisherURL=https://github.com/trendlinepros-afk/Emulate
DefaultDirName={localappdata}\Programs\Emulate
DefaultGroupName=Emulate
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=Emulate-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\Emulate.exe
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Emulate"; Filename: "{app}\Emulate.exe"
Name: "{autodesktop}\Emulate"; Filename: "{app}\Emulate.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Emulate.exe"; Description: "Launch Emulate"; Flags: nowait postinstall skipifsilent
