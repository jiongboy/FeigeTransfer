#ifndef AppVersion
  #error AppVersion must be supplied by Build-Installer.ps1
#endif
#ifndef TargetArch
  #error TargetArch must be supplied by Build-Installer.ps1
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by Build-Installer.ps1
#endif

#define AppName "飞鸽快传"
#ifndef AppBuild
  #define AppBuild 0
#endif
#ifndef AppBuildSuffix
  #define AppBuildSuffix ""
#endif

[Setup]
AppId={{1BF6149A-D679-44ED-92D0-ACD061BB4707}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Songli
AppCopyright=© Songli
DefaultDirName={localappdata}\Programs\FeigeTransfer
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir={#SetupOutputDir}
OutputBaseFilename=FeigeSetup-{#AppVersion}{#AppBuildSuffix}-{#TargetArch}
SetupIconFile={#SetupIcon}
UninstallDisplayIcon={app}\Feige.App.exe
Compression=lzma2
SolidCompression=yes
#ifdef FeigeSigningEnabled
SignTool=FeigeReleaseSign
SignedUninstaller=yes
SignedUninstallerDir={#SignedUninstallerOutputDir}
#else
SignedUninstaller=no
#endif
WizardStyle=modern
MinVersion=10.0.17763
AppMutex=Local\Songli.FeigeTransfer
CloseApplications=yes
RestartApplications=no
UninstallDisplayName={#AppName}
#if TargetArch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Feige.App.exe"; WorkingDir: "{app}"; AppUserModelID: "Songli.FeigeTransfer"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Feige.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Feige.App.exe"; Description: "Launch {#AppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\Feige.App.exe"; Parameters: "--unregister-notifications"; WorkingDir: "{app}"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveNotificationRegistration"

[Code]
// Prevent side-by-side EXE installs in a different folder. The app identity and
// uninstall registration are user-scoped because this installer runs as lowest privilege.
function FindExistingInstallDir(): string;
var
  UninstallKey: string;
  ExistingDir: string;
begin
  Result := '';
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{1BF6149A-D679-44ED-92D0-ACD061BB4707}_is1';
  if RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', ExistingDir) and (ExistingDir <> '') then
    Result := RemoveBackslash(ExistingDir);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ExistingDir: string;
  RequestedDir: string;
begin
  Result := True;
  if CurPageID <> wpSelectDir then
    exit;

  ExistingDir := FindExistingInstallDir();
  RequestedDir := RemoveBackslash(ExpandConstant(WizardDirValue()));
  if (ExistingDir <> '') and (CompareText(ExistingDir, RequestedDir) <> 0) then
  begin
    MsgBox('检测到飞鸽快传已安装在其他位置：' + #13#10 + ExistingDir + #13#10#13#10 +
      '请使用已有安装位置进行升级，或先从 Windows“已安装的应用”中卸载旧版本，再选择新的安装位置。用户数据不会由本安装程序删除。',
      mbError, MB_OK);
    Result := False;
  end;
end;

