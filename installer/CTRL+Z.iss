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
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\{#MyAppName} README"; Filename: "{app}\README.md"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; User data under LocalAppData\CTRL+Z is intentionally preserved on uninstall.
Type: filesandordirs; Name: "{app}\Data"
