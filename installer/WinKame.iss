; WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
; SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.
; WinKame installer (Inno Setup 6). Build with scripts\build-installer.ps1.
; Per-user install: no administrator rights, everything under the user's profile and HKCU.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{8C6B2E3A-5D1F-4F7B-9E2A-3B6D7C1A9F40}
AppName=WinKame
AppVersion={#AppVersion}
AppVerName=WinKame {#AppVersion}
AppPublisher=morandi.cloud
AppPublisherURL=https://morandi.cloud
AppSupportURL=https://github.com/thalesmorandi/WinKame/issues
AppUpdatesURL=https://winkame.morandi.cloud
AppCopyright=Copyright (C) 2026 Thales de Oliveira Morandi (morandi.cloud)
VersionInfoVersion={#AppVersion}
VersionInfoCompany=morandi.cloud
VersionInfoCopyright=Copyright (C) 2026 Thales de Oliveira Morandi
VersionInfoDescription=WinKame Setup
LicenseFile=..\LICENSE
DefaultDirName={autopf}\WinKame
DefaultGroupName=WinKame
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=WinKame-Setup-{#AppVersion}
SetupIconFile=..\src\WinKame\Assets\WinKame.ico
UninstallDisplayIcon={app}\WinKame.exe
UninstallDisplayName=WinKame
Compression=lzma2/ultra64
SolidCompression=yes
; Compress in a separate 64-bit process: the 32-bit compiler runs out of memory otherwise.
LZMAUseSeparateProcess=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=force
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[CustomMessages]
en.GroupIntegration=Windows integration:
ptbr.GroupIntegration=Integração com o Windows:
en.TaskStartup=Start WinKame with Windows (needed for the drag wheel)
ptbr.TaskStartup=Iniciar o WinKame com o Windows (necessário para a roda ao arrastar)
en.TaskContextMenu=Add "Open in WinKame" to the Explorer right-click menu
ptbr.TaskContextMenu=Adicionar "Abrir no WinKame" ao menu do botão direito do Explorer
en.TaskSendTo=Add WinKame to the "Send to" menu
ptbr.TaskSendTo=Adicionar o WinKame ao menu "Enviar para"
en.MenuVerb=Open in WinKame
ptbr.MenuVerb=Abrir no WinKame
en.SendToDescription=Open files in the WinKame wheel
ptbr.SendToDescription=Abrir arquivos na roda do WinKame
en.LaunchNow=Start WinKame now
ptbr.LaunchNow=Iniciar o WinKame agora

[Tasks]
Name: "startup"; Description: "{cm:TaskStartup}"; GroupDescription: "{cm:GroupIntegration}"
Name: "contextmenu"; Description: "{cm:TaskContextMenu}"; GroupDescription: "{cm:GroupIntegration}"
Name: "sendto"; Description: "{cm:TaskSendTo}"; GroupDescription: "{cm:GroupIntegration}"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Upgrades start from a clean folder so files removed from the app don't linger.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\WinKame"; Filename: "{app}\WinKame.exe"
Name: "{autodesktop}\WinKame"; Filename: "{app}\WinKame.exe"; Tasks: desktopicon
Name: "{usersendto}\WinKame"; Filename: "{app}\WinKame.exe"; Parameters: "--wheel"; Comment: "{cm:SendToDescription}"; Tasks: sendto

[Registry]
; Same keys the app writes from Settings › Integration, so both stay in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WinKame"; ValueData: """{app}\WinKame.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\*\shell\WinKame"; ValueType: string; ValueName: "MUIVerb"; ValueData: "{cm:MenuVerb}"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\WinKame"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\WinKame.exe"",0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\WinKame"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\WinKame\command"; ValueType: string; ValueName: ""; ValueData: """{app}\WinKame.exe"" --wheel ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\WinKame"; ValueType: string; ValueName: "MUIVerb"; ValueData: "{cm:MenuVerb}"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\WinKame"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\WinKame.exe"",0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\WinKame"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\WinKame\command"; ValueType: string; ValueName: ""; ValueData: """{app}\WinKame.exe"" --wheel ""%1"""; Tasks: contextmenu

[Run]
Filename: "{app}\WinKame.exe"; Parameters: "--background"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent

[Code]
procedure StopWinKame;
var
  ResultCode: Integer;
begin
  { The app lives in the tray; close it so its files can be replaced or removed. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM WinKame.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopWinKame;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopWinKame;
    { The user may have turned these on later from Settings; remove them either way. }
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\*\shell\WinKame');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\shell\WinKame');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'WinKame');
    DeleteFile(ExpandConstant('{usersendto}\WinKame.lnk'));
  end;
end;
