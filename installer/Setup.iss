#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef AppVersion
  #define AppVersion "4.2.0"
#endif

[Setup]
AppId={{E8D3650B-D8F2-4C73-B313-DB8677A664BA}
AppName=TIA Portal Source Bridge
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\TiaPortalSourceBridge
DefaultGroupName=TIA Portal Source Bridge
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=TiaPortalSourceBridge-{#AppVersion}-Setup
SetupIconFile={#PayloadDir}\app.ico
UninstallDisplayIcon={app}\app.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
InfoBeforeFile={#PayloadDir}\INSTALL.txt

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "config.json,mcp-config.json,mcp-config-v21.json"; Flags: ignoreversion recursesubdirs createallsubdirs
; Configuration is user-owned: retain it on upgrade and uninstall.
Source: "{#PayloadDir}\config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#PayloadDir}\mcp-config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#PayloadDir}\mcp-config-v21.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#PayloadDir}\engines\v21\config.json"; DestDir: "{app}\engines\v21"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#PayloadDir}\engines\v21\mcp-config.json"; DestDir: "{app}\engines\v21"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\TIA Portal Source Bridge"; Filename: "{sys}\wscript.exe"; Parameters: """{app}\LaunchWeb.vbs"""; WorkingDir: "{app}"; IconFilename: "{app}\app.ico"
Name: "{autodesktop}\TIA Portal Source Bridge"; Filename: "{sys}\wscript.exe"; Parameters: """{app}\LaunchWeb.vbs"""; WorkingDir: "{app}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon
Name: "{group}\Uninstall"; Filename: "{uninstallexe}"

[Run]
Filename: "{sys}\wscript.exe"; Parameters: """{app}\LaunchWeb.vbs"""; WorkingDir: "{app}"; Description: "Open TIA Portal Source Bridge"; Flags: postinstall nowait skipifsilent unchecked

[Code]
function InitializeSetup(): Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
  if not Result then
    MsgBox('Microsoft .NET Framework 4.8 or later is required. Install it and run Setup again.', mbError, MB_OK);
end;
