; AgentCLIHub 설치 프로그램 (Inno Setup 6)
;
; installer\build.ps1 이 먼저 `dotnet publish` 를 돌리고 그 결과 폴더를 여기로 넘긴다.
; 손으로 컴파일하려면:
;     ISCC.exe /DPublishDir="...\artifacts\publish" installer\AgentCLIHub.iss
;
; **관리자 권한을 안 받는다**(PrivilegesRequired=lowest). 회사 PC 에서 설치하려고
; 사람을 붙잡는 순간 안 쓰이는 도구가 된다. 그래서 사용자 폴더에 깔고, 시작 메뉴도
; 사용자 것만 만든다. 대신 여러 계정이 쓰려면 계정마다 깔아야 한다 — 1인용 도구라 맞다.

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#define AppName "AgentCLIHub"
#define AppExe "AgentCLIHub.exe"
; 버전은 빌드한 실행 파일에서 그대로 읽는다 — 두 군데 적으면 반드시 어긋난다.
#define AppVersion GetVersionNumbersString(PublishDir + "\" + AppExe)
#define AppPublisher "najhna"

[Setup]
; **이 값은 절대 바꾸지 않는다.** 업그레이드·제거가 이 번호로 같은 제품임을 안다 —
; 바꾸면 예전 버전이 안 지워진 채 두 벌이 깔린다.
AppId={{7C1B4E2A-9D3F-4A56-B0E7-6F2C8A1D5E94}
AppName={#AppName}
AppMutex=LlmTools.SingleInstance,AgentCLIHub.Running
UsePreviousAppDir=no
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

; 새 설치는 사용자 폴더를 쓰고, 업그레이드는 기존 설치 권한을 이어받는다.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousPrivileges=yes

OutputDir={#PublishDir}\..\setup
OutputBaseFilename=AgentCLIHub-Setup-{#AppVersion}
SetupIconFile=..\src\AgentCLIHub\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

; 자체 포함 배포라 파일이 400 개가 넘는다. 압축을 세게 걸어야 설치본이 안 커진다.
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

WizardStyle=modern
ShowLanguageDialog=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 바로 가기 만들기"; GroupDescription: "추가 아이콘:"; Flags: unchecked

[Files]
; .NET 설치 없이 실행 가능한 단일 파일 도우미를 설치 전 점검에 사용한다.
Source: "{#PublishDir}\agent\agentclihub-browser.exe"; Flags: dontcopy
; 배포 폴더를 통째로 옮긴다. web\ 과 agent\ 가 같이 들어가야 한다 —
; web\ 이 없으면 창이 빈 화면으로 뜨고(가상 호스트가 404), agent\ 가 없으면
; 탭 안의 CLI 가 브라우저를 못 부른다.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{#AppName} 실행"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 설치 뒤에 생기는 것들. 파일 목록에 없어서 그냥 두면 폴더가 남는다.
Type: filesandordirs; Name: "{app}\agent"
Type: dirifempty; Name: "{app}"

[Code]
const
  ProductKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C1B4E2A-9D3F-4A56-B0E7-6F2C8A1D5E94}_is1';
var
  PreviousDir, PreviousVersion, MovedInstallDir, UninstallerBackup: String;
  HelperReady, Prepared, InstallSucceeded: Boolean;

function GetFileAttributes(Path: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function RunMigration(Operation: String): String;
var
  ResultCode, I: Integer;
  ReportPath: String;
  Lines: TArrayOfString;
begin
  Result := '';
  if not HelperReady then begin
    ExtractTemporaryFile('agentclihub-browser.exe');
    HelperReady := True;
  end;
  ReportPath := ExpandConstant('{tmp}\agentclihub-upgrade-report.txt');
  DeleteFile(ReportPath);
  if not ExecAsOriginalUser(ExpandConstant('{tmp}\agentclihub-browser.exe'),
      '--upgrade ' + Operation + ' --report "' + ReportPath + '"', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode) then begin
    Result := '설정 이전 도우미를 실행하지 못했습니다: ' + SysErrorMessage(ResultCode);
    exit;
  end;
  if LoadStringsFromFile(ReportPath, Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do begin
      Log('Upgrade ' + Operation + ': ' + Lines[I]);
      if ResultCode <> 0 then Result := Result + Lines[I] + #13#10;
    end;
  if (ResultCode <> 0) and (Result = '') then
    Result := '기존 데이터 검사 또는 이전에 실패했습니다. 오류 코드: ' + IntToStr(ResultCode);
end;

function WebView2Installed(): Boolean;
begin
  Result := RegKeyExists(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') or
    RegKeyExists(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}');
end;
function InitializeSetup(): Boolean;
begin
  Result := True;
  if IsAdminInstallMode then begin
    if not RegQueryStringValue(HKLM64, ProductKey, 'Inno Setup: App Path', PreviousDir) then
      RegQueryStringValue(HKLM32, ProductKey, 'Inno Setup: App Path', PreviousDir);
    if not RegQueryStringValue(HKLM64, ProductKey, 'DisplayVersion', PreviousVersion) then
      RegQueryStringValue(HKLM32, ProductKey, 'DisplayVersion', PreviousVersion);
  end else begin
    if not RegQueryStringValue(HKCU64, ProductKey, 'Inno Setup: App Path', PreviousDir) then
      RegQueryStringValue(HKCU32, ProductKey, 'Inno Setup: App Path', PreviousDir);
    if not RegQueryStringValue(HKCU64, ProductKey, 'DisplayVersion', PreviousVersion) then
      RegQueryStringValue(HKCU32, ProductKey, 'DisplayVersion', PreviousVersion);
  end;
  if PreviousDir = '' then begin
    PreviousDir := ExpandConstant('{autopf}\LLM Tools');
    if not (FileExists(PreviousDir + '\LlmTools.exe') and
            FileExists(PreviousDir + '\unins000.dat')) then PreviousDir := '';
  end;
  Log('Previous installation: ' + PreviousDir + ' version ' + PreviousVersion);
  if not WebView2Installed() then
    SuppressibleMsgBox('Microsoft Edge WebView2 런타임이 보이지 않습니다. 실행이 안 되면 https://developer.microsoft.com/microsoft-edge/webview2/ 에서 설치하세요.',
      mbInformation, MB_OK, IDOK);
end;

procedure RestorePreviousUninstaller();
var
  Entry: TFindRec;
  Restored: Boolean;
begin
  if UninstallerBackup = '' then exit;
  Restored := True;
  if FindFirst(UninstallerBackup + '\*', Entry) then begin
    try
      repeat
        if (Entry.Attributes and $10) = 0 then
          if not FileCopy(UninstallerBackup + '\' + Entry.Name,
              MovedInstallDir + '\' + Entry.Name, False) then Restored := False;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
  if Restored then begin
    DelTree(UninstallerBackup, True, True, True);
    UninstallerBackup := '';
  end else Log('Previous uninstaller preserved at: ' + UninstallerBackup);
end;

function StagePreviousUninstaller(): String;
var
  Entry: TFindRec;
  Extension: String;
begin
  Result := '';
  UninstallerBackup := MovedInstallDir + '\.agentclihub-uninstall';
  if DirExists(UninstallerBackup) then begin
    Result := '이전 설치 복구 파일이 있습니다. 원본을 보존하고 설치를 중단했습니다.';
    UninstallerBackup := '';
    exit;
  end;
  if not CreateDir(UninstallerBackup) then begin
    Result := '기존 제거 정보를 보관하지 못했습니다.';
    UninstallerBackup := '';
    exit;
  end;
  { 제거 기록에는 절대 경로가 있다. 새 폴더에서는 제거 정보를 새로 만든다. }
  if FindFirst(MovedInstallDir + '\unins*', Entry) then begin
    try
      repeat
        Extension := Lowercase(ExtractFileExt(Entry.Name));
        if ((Entry.Attributes and $10) = 0) and
            ((Extension = '.exe') or (Extension = '.dat') or (Extension = '.msg')) then
          if not RenameFile(MovedInstallDir + '\' + Entry.Name,
              UninstallerBackup + '\' + Entry.Name) then begin
            Result := '기존 제거 정보를 옮기지 못했습니다. 실행 중인 제거 프로그램을 닫으세요.';
            break;
          end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  TargetDir: String;
begin
  Result := '';
  if Prepared then exit;
  Result := RunMigration('inspect');
  if Result <> '' then exit;

  TargetDir := RemoveBackslash(ExpandConstant('{app}'));
  if (PreviousDir <> '') and DirExists(PreviousDir) and
      (CompareText(RemoveBackslash(PreviousDir), TargetDir) <> 0) then begin
    if (GetFileAttributes(PreviousDir) and $400) <> 0 then begin
      Result := '기존 설치 폴더가 연결 폴더입니다. 원본을 보존하고 설치를 중단했습니다.';
      exit;
    end;
    if not (FileExists(PreviousDir + '\LlmTools.exe') or
            FileExists(PreviousDir + '\AgentCLIHub.exe')) then begin
      Result := '등록된 설치 위치에서 기존 실행 파일을 찾지 못했습니다: ' + PreviousDir;
      exit;
    end;
    if DirExists(TargetDir) and not RemoveDir(TargetDir) then begin
      Result := '새 설치 폴더에 별도 파일이 있습니다. 덮어쓰지 않았습니다: ' + TargetDir;
      exit;
    end;
    if not ForceDirectories(ExtractFileDir(TargetDir)) or
       not RenameFile(PreviousDir, TargetDir) then begin
      Result := '기존 설치 폴더를 옮기지 못했습니다. 실행 중인 앱을 닫고 같은 드라이브의 설치 경로를 사용하세요.';
      exit;
    end;
    MovedInstallDir := TargetDir;
    Result := StagePreviousUninstaller();
    if Result <> '' then begin
      RestorePreviousUninstaller();
      if RenameFile(MovedInstallDir, PreviousDir) then MovedInstallDir := '';
      exit;
    end;
  end;

  Result := RunMigration('migrate');
  if Result <> '' then begin
    if MovedInstallDir <> '' then begin
      RestorePreviousUninstaller();
      if RenameFile(MovedInstallDir, PreviousDir) then MovedInstallDir := ''
      else Result := Result + #13#10 + '설치 파일은 다음 위치에 보존되어 있습니다: ' + MovedInstallDir;
    end;
    exit;
  end;
  Prepared := True;
end;

procedure RemoveLegacyFile(Path: String);
begin
  if FileExists(Path) and not DeleteFile(Path) then
    Log('Could not remove legacy file: ' + Path);
end;

procedure RemoveLegacyShortcut(Path: String);
var
  Files: Variant;
begin
  { 바로가기를 만든 Windows Shell과 같은 파일 시스템 보기를 사용한다. }
  try
    Files := CreateOleObject('Scripting.FileSystemObject');
    if Files.FileExists(Path) then Files.DeleteFile(Path, True);
  except
    Log('Could not remove legacy shortcut: ' + Path);
  end;
end;
procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorText: String;
begin
  if CurStep = ssPostInstall then begin
    InstallSucceeded := True;
    if UninstallerBackup <> '' then begin
      if not DelTree(UninstallerBackup, True, True, True) then
        Log('Could not remove old uninstall backup: ' + UninstallerBackup);
      UninstallerBackup := '';
    end;
    ErrorText := RunMigration('commit');
    if ErrorText <> '' then
      MsgBox('설치는 완료됐지만 이전 기록 정리가 필요합니다:' + #13#10 + ErrorText, mbError, MB_OK);
    RemoveLegacyFile(ExpandConstant('{app}\LlmTools.exe'));
    RemoveLegacyFile(ExpandConstant('{app}\LlmTools.dll'));
    RemoveLegacyFile(ExpandConstant('{app}\LlmTools.deps.json'));
    RemoveLegacyFile(ExpandConstant('{app}\LlmTools.runtimeconfig.json'));
    RemoveLegacyFile(ExpandConstant('{app}\LlmTools.pdb'));
    RemoveLegacyFile(ExpandConstant('{app}\agent\llmtools-browser.exe'));
    RemoveLegacyFile(ExpandConstant('{app}\agent\llmtools-browser.dll'));
    RemoveLegacyFile(ExpandConstant('{app}\agent\llmtools-browser.deps.json'));
    RemoveLegacyFile(ExpandConstant('{app}\agent\llmtools-browser.runtimeconfig.json'));
    RemoveLegacyFile(ExpandConstant('{app}\agent\llmtools-browser.pdb'));
    RemoveLegacyShortcut(ExpandConstant('{autoprograms}\LLM Tools.lnk'));
    RemoveLegacyShortcut(ExpandConstant('{autodesktop}\LLM Tools.lnk'));
    RemoveDir(ExpandConstant('{autoprograms}\LLM Tools'));
    { 1.8.0에서 사용하던 띄어 쓴 제품명도 정리한다. }
    RemoveLegacyShortcut(ExpandConstant('{autoprograms}\Agent CLI Hub.lnk'));
    RemoveLegacyShortcut(ExpandConstant('{autodesktop}\Agent CLI Hub.lnk'));
    RemoveDir(ExpandConstant('{autoprograms}\Agent CLI Hub'));
  end;
end;

procedure DeinitializeSetup();
var
  ErrorText: String;
begin
  if Prepared and not InstallSucceeded then begin
    ErrorText := RunMigration('rollback');
    if ErrorText <> '' then
      MsgBox('설치를 취소했습니다. 데이터는 보존되어 있으나 자동 복원이 완료되지 않았습니다:' + #13#10 + ErrorText, mbError, MB_OK);
  end;
  if not InstallSucceeded then RestorePreviousUninstaller();
  if (MovedInstallDir <> '') and not InstallSucceeded then
    if not RenameFile(MovedInstallDir, PreviousDir) then
      Log('Original installation files preserved at: ' + MovedInstallDir);
end;

{ 이전 설치에서 이동한 빈 하위 폴더도 제거한다. 사용자 파일과 연결 폴더는 보존한다. }
procedure RemoveEmptyInstallDirectories(Path: String);
var
  Entry: TFindRec;
  Child: String;
begin
  if (GetFileAttributes(Path) and $400) <> 0 then exit;
  if FindFirst(AddBackslash(Path) + '*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') and
           ((Entry.Attributes and $10) <> 0) and ((Entry.Attributes and $400) = 0) then begin
          Child := AddBackslash(Path) + Entry.Name;
          RemoveEmptyInstallDirectories(Child);
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
  RemoveDir(Path);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveEmptyInstallDirectories(ExpandConstant('{app}'));
end;
