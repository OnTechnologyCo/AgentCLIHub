; LLM Tools 설치 프로그램 (Inno Setup 6)
;
; installer\build.ps1 이 먼저 `dotnet publish` 를 돌리고 그 결과 폴더를 여기로 넘긴다.
; 손으로 컴파일하려면:
;     ISCC.exe /DPublishDir="...\artifacts\publish" installer\LlmTools.iss
;
; **관리자 권한을 안 받는다**(PrivilegesRequired=lowest). 회사 PC 에서 설치하려고
; 사람을 붙잡는 순간 안 쓰이는 도구가 된다. 그래서 사용자 폴더에 깔고, 시작 메뉴도
; 사용자 것만 만든다. 대신 여러 계정이 쓰려면 계정마다 깔아야 한다 — 1인용 도구라 맞다.

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#define AppName "LLM Tools"
#define AppExe "LlmTools.exe"
; 버전은 빌드한 실행 파일에서 그대로 읽는다 — 두 군데 적으면 반드시 어긋난다.
#define AppVersion GetVersionNumbersString(PublishDir + "\" + AppExe)
#define AppPublisher "najhna"

[Setup]
; **이 값은 절대 바꾸지 않는다.** 업그레이드·제거가 이 번호로 같은 제품임을 안다 —
; 바꾸면 예전 버전이 안 지워진 채 두 벌이 깔린다.
AppId={{7C1B4E2A-9D3F-4A56-B0E7-6F2C8A1D5E94}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

; 사용자 권한으로만 깐다 — {autopf} 가 %LOCALAPPDATA%\Programs 로 풀린다.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir={#PublishDir}\..\setup
OutputBaseFilename=LlmTools-Setup-{#AppVersion}
SetupIconFile=..\src\LlmTools\app.ico
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
{ WebView2 런타임이 없으면 창이 안 뜬다. 설치를 막지는 않고 **미리 알려만 준다** —
  Windows 11 에는 기본으로 들어 있고, 없는 PC 는 관리자가 따로 깔아야 하는 경우가 많다.
  설치 자체를 막으면 "왜 안 깔리는지" 를 사용자가 알 수 없다. }
function WebView2Installed(): Boolean;
var
  Key: String;
begin
  Key := 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  Result := RegKeyExists(HKLM, Key)
         or RegKeyExists(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}');
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not WebView2Installed() then
    MsgBox('Microsoft Edge WebView2 런타임이 안 보입니다.' + #13#10#13#10 +
           '이 프로그램의 화면은 WebView2 로 그려집니다 — 없으면 창이 뜨지 않습니다.' + #13#10 +
           '설치는 그대로 진행하고, 실행이 안 되면 아래에서 받아 설치하세요:' + #13#10 +
           'https://developer.microsoft.com/microsoft-edge/webview2/',
           mbInformation, MB_OK);
end;

{ 설치·제거 중에 앱이 떠 있으면 파일이 잠긴다. 사용자에게 닫으라고 말해 준다. }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    if CheckForMutexes('LlmTools.SingleInstance') then
      MsgBox('LLM Tools 가 실행 중입니다. 닫은 뒤 [확인] 을 누르세요.', mbInformation, MB_OK);
end;
