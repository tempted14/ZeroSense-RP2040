#ifndef AppVersion
  #define AppVersion "1.6.0"
#endif

[Setup]
AppId={{2E021D34-EDB1-4BD4-9A69-1E01235C7C9C}
AppName=ZeroSense
AppVersion={#AppVersion}
AppPublisher=ZeroSense
AppPublisherURL=https://github.com/tempted14/ZeroSense-RP2040
AppSupportURL=https://github.com/tempted14/ZeroSense-RP2040/issues
AppUpdatesURL=https://github.com/tempted14/ZeroSense-RP2040/releases
DefaultDirName={localappdata}\Programs\ZeroSense
DefaultGroupName=ZeroSense
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=ZeroSense-Setup-{#AppVersion}-win-x64
SetupIconFile=..\WindowsApp\Assets\zerosense.ico
UninstallDisplayIcon={app}\zerosense.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ZeroSense"; Filename: "{app}\zerosense.exe"
Name: "{autodesktop}\ZeroSense"; Filename: "{app}\zerosense.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\zerosense.exe"; Description: "Launch ZeroSense"; Flags: nowait postinstall skipifsilent
