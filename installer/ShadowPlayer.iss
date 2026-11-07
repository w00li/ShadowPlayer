; Built by the Release build of M3uPlayer.App, which passes AppVersion and SourceDir.
; The fixed AppId is what makes a newer installer replace the installed version: never change it.

#ifndef AppVersion
  #define AppVersion "0.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\installer\files"
#endif

[Setup]
AppId={{F2E73B56-63E9-4F5A-8A64-D04CE189DF03}
AppName=ShadowPlayer
AppVersion={#AppVersion}
AppVerName=ShadowPlayer
VersionInfoVersion={#AppVersion}
UninstallDisplayIcon={app}\ShadowPlayer.exe
SetupIconFile=..\src\M3uPlayer.App\Assets\ShadowPlayer.ico
DefaultDirName={autopf}\ShadowPlayer
DefaultGroupName=ShadowPlayer
DisableProgramGroupPage=yes
; {autopf} is then Program Files. The first install asks where to go; an update stays in that folder.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\artifacts\installer
OutputBaseFilename=ShadowPlayer-Setup-{#AppVersion}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
; A running copy holds its files, so it is closed for the replacement.
CloseApplications=force
RestartApplications=no

[Languages]
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"

[Tasks]
Name: "desktopicon"; Description: "Utwórz skrót na pulpicie"; GroupDescription: "Skróty:"

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ShadowPlayer"; Filename: "{app}\ShadowPlayer.exe"
Name: "{autodesktop}\ShadowPlayer"; Filename: "{app}\ShadowPlayer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ShadowPlayer.exe"; Description: "Uruchom ShadowPlayer"; Flags: nowait postinstall skipifsilent runasoriginaluser
; An update started by the app itself runs silently with /RELAUNCH=1 and brings the app back when it is done.
Filename: "{app}\ShadowPlayer.exe"; Flags: nowait runasoriginaluser; Check: RelaunchAfterUpdate

; The data in %LocalAppData%\M3uPlayer is deliberately not touched, by an update or by uninstalling.

[Code]
function RelaunchAfterUpdate: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

// The user picks the folder, so it may hold other files: the old version's own uninstaller removes exactly what it installed,
// so files a newer version no longer ships do not stay behind, and nothing else is touched.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Uninstaller: String;
  ResultCode: Integer;
begin
  if (CurStep = ssInstall) and RegQueryStringValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{F2E73B56-63E9-4F5A-8A64-D04CE189DF03}_is1', 'UninstallString', Uninstaller) then
    Exec(RemoveQuotes(Uninstaller), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
