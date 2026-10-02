#ifndef PublishDir
 #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
 #define OutputDir "..\dist"
#endif
[Setup]
AppId={{31857247-067C-4DC1-BA47-44F01A87B70D}
AppName=Bebekon VPN
AppVersion=0.1.0
AppPublisher=Bebekon
DefaultDirName={autopf}\Bebekon VPN
DefaultGroupName=Bebekon VPN
UninstallDisplayIcon={app}\Bebekon.App.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=BebekonVPN-Setup-x64
SetupIconFile=..\resources\icons\bebekon.ico
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=Bebekon.App.exe
RestartApplications=no
DisableProgramGroupPage=yes
LicenseFile=..\core\LICENSE

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked
Name: "autostart"; Description: "Запускать вместе с Windows"; Flags: unchecked
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Bebekon VPN"; Filename: "{app}\Bebekon.App.exe"
Name: "{autodesktop}\Bebekon VPN"; Filename: "{app}\Bebekon.App.exe"; Tasks: desktopicon
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "BebekonVPN"; ValueData: """{app}\Bebekon.App.exe"" --tray"; Tasks: autostart; Flags: uninsdeletevalue
[Run]
Filename: "{app}\Bebekon.App.exe"; Description: "Открыть Bebekon VPN"; Flags: nowait postinstall skipifsilent runasoriginaluser
[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\uninstall-service.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[Code]
var OwnerPage: TInputQueryWizardPage;
function OwnerAccount(Param: String): String;
begin Result := OwnerPage.Values[0]; end;
procedure InitializeWizard();
begin
  OwnerPage := CreateInputQueryPage(wpSelectDir, 'Владелец VPN', 'Учётная запись Windows', 'Только эта учётная запись сможет управлять VPN. Если UAC выполняется под другим администратором, укажите имя обычного пользователя Windows.');
  OwnerPage.Add('Пользователь (КОМПЬЮТЕР\имя или ДОМЕН\имя):', False);
  OwnerPage.Values[0] := GetComputerNameString() + '\' + GetUserNameString();
end;
function NextButtonClick(CurPageID: Integer): Boolean;
var I: Integer; C: Char;
begin
  Result := True;
  if CurPageID = OwnerPage.ID then begin
    if Length(OwnerPage.Values[0]) = 0 then Result := False;
    for I := 1 to Length(OwnerPage.Values[0]) do begin
      C := OwnerPage.Values[0][I];
      if (C = '"') or (C = ';') or (C = '&') or (C = '|') or (C = '<') or (C = '>') or (C = #13) or (C = #10) then Result := False;
    end;
    if not Result then MsgBox('Укажите корректное имя учётной записи Windows.', mbError, MB_OK);
  end;
end;
function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\Bebekon.Service.exe')) then begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop BebekonVPN', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(1500);
  end;
end;
procedure CurStepChanged(CurStep: TSetupStep);
var Code: Integer;
begin
  if CurStep = ssPostInstall then
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\scripts\install-service.ps1') + '" -OwnerAccount "' + OwnerAccount('') + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      RaiseException('Не удалось установить службу VPN. Запустите scripts\install-service.ps1 от администратора.');
end;
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Root: String;
begin
  if CurUninstallStep = usPostUninstall then
    if MsgBox('Удалить подписки, правила и настройки текущего пользователя?', mbConfirmation, MB_YESNO) = IDYES then begin
      Root := ExpandConstant('{localappdata}\BebekonVPN');
      DelTree(Root, True, True, True);
    end;
end;
