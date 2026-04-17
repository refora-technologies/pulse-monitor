; Pulse Monitor — Inno Setup Installer Script
; Package: com.reforatech.pulse
; © 2025 Refora Technologies

[Setup]
AppId={{B8F3E2A4-9D1C-4F7E-A5B2-3C8D6E9F1A2B}
AppName=Pulse
AppVersion=1.0.0
AppVerName=Pulse v1.0.0
AppPublisher=Refora Technologies
AppPublisherURL=https://reforatech.com
AppSupportURL=https://reforatech.com
AppContact=reforatech@gmail.com
DefaultDirName={autopf}\Refora\Pulse
DefaultGroupName=Refora Technologies
OutputDir=installer
OutputBaseFilename=PulseSetup_v1.0.0
SetupIconFile=Resources\Icons\pulse.ico
UninstallDisplayIcon={app}\Pulse.exe
LicenseFile=LICENSE
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayName=Pulse — System Monitor by Refora Technologies
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}";
Name: "startupentry"; Description: "Start Pulse when Windows starts"; GroupDescription: "System Integration:";

[Files]
Source: "publish\Pulse.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Pulse"; Filename: "{app}\Pulse.exe"; IconFilename: "{app}\Pulse.exe"
Name: "{group}\Uninstall Pulse"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Pulse"; Filename: "{app}\Pulse.exe"; IconFilename: "{app}\Pulse.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PulseMonitor"; ValueData: """{app}\Pulse.exe"""; Flags: uninsdeletevalue; Tasks: startupentry

[Run]
Filename: "{app}\Pulse.exe"; Description: "Launch Pulse"; Flags: nowait postinstall skipifsilent runascurrentuser
