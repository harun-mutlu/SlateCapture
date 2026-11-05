#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{8C3F2B1E-5A47-4E1B-9C0D-3F7A2E6B9D14}
AppName=SlateCapture
AppVersion={#AppVersion}
AppPublisher=SlateCapture
DefaultDirName={autopf}\SlateCapture
DefaultGroupName=SlateCapture
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=SlateCapture-{#AppVersion}-setup
SetupIconFile=..\desktop\SlateCapture\Assets\icon.ico
UninstallDisplayIcon={app}\SlateCapture.exe
LicenseFile=..\LICENSE
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#if FileExists(AddBackslash(CompilerPath) + "Languages\Turkish.isl")
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\build\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\SlateCapture"; Filename: "{app}\SlateCapture.exe"
Name: "{autodesktop}\SlateCapture"; Filename: "{app}\SlateCapture.exe"; Tasks: desktopicon

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""SlateCapture"""; Flags: runhidden; StatusMsg: "Firewall"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""SlateCapture"" dir=in action=allow program=""{app}\SlateCapture.exe"" enable=yes profile=any"; Flags: runhidden; StatusMsg: "Firewall"
Filename: "{app}\SlateCapture.exe"; Description: "{cm:LaunchProgram,SlateCapture}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""SlateCapture"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"
