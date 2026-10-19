{ 관리자 설치의 임시 폴더는 일반 사용자에게 읽기/실행만 허용된다.
  폴더/실행 파일의 권한은 유지하고, 설치 프로그램이 미리 만든 결과 파일
  하나에만 대화형 사용자의 읽기/쓰기를 허용한다. 성공 여부는 파일 내용이
  아니라 ExecAsOriginalUser가 돌려준 프로세스 종료 코드로 판단한다. }
function ConvertStringSecurityDescriptorToSecurityDescriptor(
  Descriptor: String; Revision: Cardinal; var SecurityDescriptor: LongWord;
  Size: LongWord): Boolean;
  external 'ConvertStringSecurityDescriptorToSecurityDescriptorW@advapi32.dll stdcall';
function SetFileSecurity(FileName: String; Information: Cardinal;
  SecurityDescriptor: LongWord): Boolean;
  external 'SetFileSecurityW@advapi32.dll stdcall';
function LocalFree(Memory: LongWord): LongWord;
  external 'LocalFree@kernel32.dll stdcall';

function PrepareMigrationReport(Path: String): Boolean;
var
  Descriptor: LongWord;
begin
  Result := False;
  if FileExists(Path) and not DeleteFile(Path) then exit;
  if not SaveStringToFile(Path, '', False) then exit;
  Descriptor := 0;
  { SYSTEM/Administrators는 전체 권한, Interactive Users는 이 파일의 읽기/쓰기만.
    일반 사용자는 삭제/권한 변경 및 상위 폴더의 파일 생성 권한을 받지 않는다. }
  if not ConvertStringSecurityDescriptorToSecurityDescriptor(
      'D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGW;;;IU)', 1, Descriptor, 0) then exit;
  try
    Result := SetFileSecurity(Path, $80000004, Descriptor);
  finally
    LocalFree(Descriptor);
  end;
end;
