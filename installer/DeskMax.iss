#define MyAppName "DeskMax"
#ifndef MyAppVersion
#define MyAppVersion "0.4.1"
#endif
#define MyAppPublisher "DeskMax"
#define MyAppExeName "DeskMax.exe"

[Setup]
AppId={{D4E279B2-E112-4899-9179-D5FF446091EC}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\DeskMax
DefaultGroupName=DeskMax
OutputDir=..\artifacts\installer
OutputBaseFilename=DeskMaxSetup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\artifacts\app\DeskMax.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\DeskMax"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\DeskMax"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Создать значок на рабочем столе"; GroupDescription: "Дополнительные значки:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить DeskMax"; Flags: nowait postinstall skipifsilent
