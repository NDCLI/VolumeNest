#define MyAppName "VolumeNest"
#define MyAppVersion "1.0.1"
#define MyAppPublisher "NDCLI"
#define MyAppExeName "VolumeNest.exe"

[Setup]
AppId={{DDAF0E6E-9D4F-4E20-9B8A-2B8CC7D8A001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\VolumeNest
DefaultGroupName={#MyAppName}
OutputDir=publish
OutputBaseFilename=VolumeNest-Setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
CloseApplications=yes
CloseApplicationsFilter=*VolumeNest*
RestartApplications=no
SetupIconFile=VolumeNest\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\icon.ico"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; IconFilename: "{app}\icon.ico"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VolumeNest"; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletevalue; Tasks: startup

[Tasks]
Name: "startup"; Description: "Khởi động VolumeNest cùng Windows"; GroupDescription: "Tùy chọn khởi động:"
Name: "desktopicon"; Description: "Tạo shortcut ngoài Desktop"; GroupDescription: "Shortcut bổ sung:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Mở {#MyAppName}"; Flags: nowait postinstall skipifsilent
