; Inno Setup 6 script for MyLianPC. Built by build\publish.ps1 (passes AppVersion and SourceDir).
#ifndef AppVersion
  #define AppVersion "0.11.2-beta"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\MyLianPC"
#endif

[Setup]
AppId={{6B1E7C1A-7F4E-4B7B-9C5A-2C4E0D8E4F11}
AppName=MyLianPC
AppVersion={#AppVersion}
AppPublisher=MyLianPC contributors
AppPublisherURL=https://github.com/
DefaultDirName={autopf}\MyLianPC
DefaultGroupName=MyLianPC
DisableProgramGroupPage=yes
; The program was renamed from MyPcRgb and both names share one AppId, so Inno Setup would otherwise reuse the old
; MyPcRgb program folder and Start-menu group and install the new build under the old name.
UsePreviousAppDir=no
UsePreviousGroup=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=MyLianPC-Setup-{#AppVersion}
SetupIconFile=..\src\MyLianPC.App\Assets\app.ico
UninstallDisplayIcon={app}\MyLianPC.exe
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "startup"; Description: "Start MyLianPC automatically when Windows starts (stays in the notification area)"
Name: "takeover"; Description: "Take over from L-Connect 3, GIGABYTE Control Center autostart and Corsair Device Control Service (recommended; reversible, nothing is uninstalled)"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; The app was renamed from MyPcRgb: drop the old program folder and shortcuts so nothing launches the old build.
Type: filesandordirs; Name: "{autopf}\MyPcRgb"
Type: filesandordirs; Name: "{commonprograms}\MyPcRgb"
Type: files; Name: "{commonprograms}\MyLianPC.lnk"
Type: files; Name: "{autodesktop}\MyPcRgb.lnk"
; Inno copies the new files but never removes ones a newer version stopped shipping: 0.11.2 dropped the old LCD and
; Cooling screenshots and an upgrade kept them beside the new ones. Clear the folder before the copy recreates it.
Type: filesandordirs; Name: "{app}\docs\images"

[Icons]
Name: "{group}\MyLianPC"; Filename: "{app}\MyLianPC.exe"
Name: "{group}\Uninstall MyLianPC"; Filename: "{uninstallexe}"
Name: "{autodesktop}\MyLianPC"; Filename: "{app}\MyLianPC.exe"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MyLianPC"; ValueData: """{app}\MyLianPC.exe"" --tray"; Flags: uninsdeletevalue; Tasks: startup
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "MyPcRgb"; Flags: deletevalue

[Run]
; The service starts at boot as LocalSystem, owns the hardware for every Windows account and restarts itself after a crash.
Filename: "{sys}\sc.exe"; Parameters: "create MyLianPCService binPath= ""{app}\MyLianPC.Service.exe"" start= auto DisplayName= ""MyLianPC Hardware Service"""; Flags: runhidden waituntilterminated; StatusMsg: "Registering the hardware service..."
Filename: "{sys}\sc.exe"; Parameters: "config MyLianPCService binPath= ""{app}\MyLianPC.Service.exe"" start= auto"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description MyLianPCService ""Controls RGB lighting, the cooler LCD and fan curves for MyLianPC, independent of which Windows account is signed in."""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure MyLianPCService reset= 86400 actions= restart/5000/restart/10000/restart/30000"; Flags: runhidden waituntilterminated
Filename: "{app}\MyLianPC.Service.exe"; Parameters: "--vendor-takeover"; Flags: runhidden waituntilterminated; Tasks: takeover; StatusMsg: "Disabling conflicting vendor software..."
Filename: "{cmd}"; Parameters: "/c winget install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements"; Flags: runhidden waituntilterminated; Check: NeedsPawnIO; StatusMsg: "Installing the PawnIO driver (sensors, fan control, RAM lighting)..."
Filename: "{sys}\sc.exe"; Parameters: "start MyLianPCService"; Flags: runhidden waituntilterminated; StatusMsg: "Starting the hardware service..."
Filename: "{app}\MyLianPC.exe"; Description: "Open MyLianPC"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\net.exe"; Parameters: "stop MyLianPCService"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\timeout.exe"; Parameters: "/t 3 /nobreak"; Flags: runhidden waituntilterminated; RunOnceId: "WaitStop"
Filename: "{sys}\sc.exe"; Parameters: "delete MyLianPCService"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{app}\MyLianPC.Service.exe"; Parameters: "--vendor-restore"; Flags: runhidden waituntilterminated; RunOnceId: "VendorRestore"

[Code]
function NeedsPawnIO: Boolean;
begin
  Result := not FileExists(ExpandConstant('{commonpf64}\PawnIO\PawnIOLib.dll'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  // Stop a running copy so its files can be replaced. net.exe (unlike sc.exe) waits until the service has
  // stopped, which takes several seconds when the cooler is not answering.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM MyLianPC.exe /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\net.exe'), 'stop MyLianPCService', '', SW_HIDE, ewWaitUntilTerminated, Code);
  // The app used to be called MyPcRgb: remove that service, window and start-up entry so two services never
  // drive the same hardware.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM MyPcRgb.exe /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\net.exe'), 'stop MyPcRgbService', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete MyPcRgbService', '', SW_HIDE, ewWaitUntilTerminated, Code);
  RegDeleteValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MyPcRgb');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MyPcRgb');
  Sleep(1000);
  Result := '';
end;
