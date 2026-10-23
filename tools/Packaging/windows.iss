#define AppVersion "0.2.0"
[Setup]
AppId={{39A5C82E-32CF-45B7-A8B4-E59FE96BC342}
AppName=Кабельные маршруты
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\CableRoutePlanner
DefaultGroupName=Кабельные маршруты
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\..\artifacts\installer
OutputBaseFilename=CableRoutePlanner-0.2.0-Belarus-win-x64-setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\CableRoutePlanner.Desktop.exe
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
[Files]
Source: "..\..\artifacts\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Кабельные маршруты"; Filename: "{app}\CableRoutePlanner.Desktop.exe"
Name: "{autodesktop}\Кабельные маршруты"; Filename: "{app}\CableRoutePlanner.Desktop.exe"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Создать значок на рабочем столе"; Flags: unchecked
[Run]
Filename: "{app}\CableRoutePlanner.Desktop.exe"; Description: "Запустить приложение"; Flags: nowait postinstall skipifsilent
