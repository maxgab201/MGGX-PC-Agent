#define MyAppName "MGGX PC Agent"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "MGGX Games"
#define MyServiceName "MGGXPCAgent"

[Setup]
AppId={{EFCD36A8-830C-4F8D-BB4A-E6E7F1E2A10F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\MGGX\PC Agent
DefaultGroupName=MGGX PC Agent
OutputDir=..\artifacts
OutputBaseFilename=MGGX-PC-Agent-Setup-x64
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
SetupIconFile=..\assets\icon\mggx-pc-agent.ico
UninstallDisplayIcon={app}\Control\MGGX.PCAgent.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Files]
Source: "..\artifacts\publish\service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\control\*"; DestDir: "{app}\Control"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\MGGX PC Agent"; Filename: "{app}\Control\MGGX.PCAgent.exe"
Name: "{autodesktop}\MGGX PC Agent"; Filename: "{app}\Control\MGGX.PCAgent.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create {#MyServiceName} binPath= ""{app}\Service\MGGX.PCAgent.Service.exe"" start= delayed-auto DisplayName= ""MGGX PC Agent"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description {#MyServiceName} ""Secure local API for MGGX PC Control"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#MyServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failureflag {#MyServiceName} 1"; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""MGGX PC Agent API - Private LAN"" dir=in action=allow protocol=TCP localport=8766 remoteip=LocalSubnet profile=private program=""{app}\Service\MGGX.PCAgent.Service.exe"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""MGGX PC Agent API - Tailscale"" dir=in action=allow protocol=TCP localport=8766 remoteip=100.64.0.0/10 profile=any program=""{app}\Service\MGGX.PCAgent.Service.exe"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""MGGX PC Agent Discovery - Private LAN"" dir=in action=allow protocol=UDP localport=8767 remoteip=LocalSubnet profile=private program=""{app}\Service\MGGX.PCAgent.Service.exe"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#MyServiceName}"; Flags: runhidden waituntilterminated
Filename: "{app}\Control\MGGX.PCAgent.exe"; Description: "Open MGGX PC Agent"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden waituntilterminated skipifdoesntexist
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden waituntilterminated skipifdoesntexist
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""MGGX PC Agent API - Private LAN"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""MGGX PC Agent API - Tailscale"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""MGGX PC Agent Discovery - Private LAN"""; Flags: runhidden waituntilterminated

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
    if MsgBox('Keep MGGX PC Agent configuration, logs, and protected token for a future reinstall?', mbConfirmation, MB_YESNO) = IDNO then
      DelTree(ExpandConstant('{commonappdata}\MGGX\PC-Agent'), True, True, True);
end;
