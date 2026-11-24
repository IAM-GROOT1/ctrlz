; SPDX-License-Identifier: AGPL-3.0-only
; Copyright (c) 2026 iam-groot1

; CTRL+Z installer — build with build\build-installer.ps1
; The app itself is a single self-contained executable.

#ifndef MyAppVersion
  #define MyAppVersion "1.1.3"
#endif

; VersionInfoVersion must be numeric (x.y.z[.w]), so pre-release tags pass a suffix-free
; value here. AppVersion and the output file name keep the full version string.
#ifndef MyAppVersionNumeric
  #define MyAppVersionNumeric "1.1.3"
#endif

#define MyAppName "CTRL+Z"
#define MyAppPublisher "IAM-GROOT1"
#define MyAppExeName "CTRL+Z.exe"

[Setup]
AppId={{0941C8F1-8BBA-4F3E-822A-7DDB6598644A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/IAM-GROOT1/ctrlz
AppSupportURL=https://github.com/IAM-GROOT1/ctrlz/issues
AppUpdatesURL=https://github.com/IAM-GROOT1/ctrlz/releases
VersionInfoVersion={#MyAppVersionNumeric}.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} setup
DefaultDirName={autopf}\CTRL+Z
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\artifacts\installer
OutputBaseFilename=CTRL+Z-Setup-{#MyAppVersion}
SetupIconFile=..\src\CtrlZ.App\Assets\ctrlz.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\portable\CTRL+Z.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\publish\portable\ctrlz.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\{#MyAppName} README"; Filename: "{app}\README.md"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; User data under LocalAppData\CTRL+Z is kept unless the user chooses to delete it (see [Code]).
Type: filesandordirs; Name: "{app}\Data"
; Rollback copies an update keeps beside the app until the new version has started twice.
Type: files; Name: "{app}\*.old"
Type: files; Name: "{app}\*.failed"

[Code]
var
  DeleteUserData: Boolean;

function UserDataFolder(): String;
begin
  Result := ExpandConstant('{localappdata}\CTRL+Z');
end;

function PortableShortcutsFolder(): String;
begin
  Result := ExpandConstant('{userprograms}\CTRL+Z apps');
end;

{ Guards DelTree: only a folder that really is CTRL+Z's, never a root or an empty path. }
function IsSafeToDelete(Path: String): Boolean;
begin
  Result := (Length(Path) > 12) and
            ((Pos('\CTRL+Z', Path) = Length(Path) - 6) or (Pos('\CTRL+Z apps', Path) = Length(Path) - 11)) and
            DirExists(Path);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    DeleteUserData := False;
    { A silent uninstall always keeps the data. }
    if (not UninstallSilent()) and DirExists(UserDataFolder()) then
      DeleteUserData := MsgBox(
        'Also delete your CTRL+Z data?' + #13#10 + #13#10 +
        UserDataFolder() + #13#10 + #13#10 +
        'It holds your app lists (profiles), settings, logs and install state, and any portable apps ' +
        'CTRL+Z installed for you. Browser backups and exported profiles saved elsewhere are not touched.' + #13#10 + #13#10 +
        'Choose No to keep it, for example if you plan to install CTRL+Z again.',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end;

  if (CurUninstallStep = usPostUninstall) and DeleteUserData then
  begin
    if IsSafeToDelete(UserDataFolder()) and not DelTree(UserDataFolder(), True, True, True) then
      MsgBox('Some CTRL+Z data could not be deleted. You can remove ' + UserDataFolder() + ' yourself.',
        mbInformation, MB_OK);
    { The portable apps are gone with the data, so their Start menu shortcuts go too. }
    if IsSafeToDelete(PortableShortcutsFolder()) then
      DelTree(PortableShortcutsFolder(), True, True, True);
  end;
end;
