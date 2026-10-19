; ISCC /DHelperPath="...\agentclihub-browser.exe" tests\InstallerChecks\ReportPermissions.iss
; Run the output with /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="..."
; No product registration, shortcuts, user data or application installation is changed.
[Setup]
AppName=AgentCLIHub installer permission checks
AppVersion=1.0
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
OutputDir=..\..\artifacts\installer-checks
OutputBaseFilename=ReportPermissions

[Files]
Source: "{#HelperPath}"; Flags: dontcopy

[Code]
#include "..\..\installer\MigrationReport.iss"

procedure SetTestDirectoryAccess(Path, Access: String);
var
  Descriptor: LongWord;
begin
  if not ConvertStringSecurityDescriptorToSecurityDescriptor(
      'D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;' + Access + ';;;IU)', 1, Descriptor, 0) then
    RaiseException('Cannot create fixture security descriptor');
  try
    if not SetFileSecurity(Path, $80000004, Descriptor) then
      RaiseException('Cannot set fixture directory permissions');
  finally
    LocalFree(Descriptor);
  end;
end;

function InitializeSetup(): Boolean;
var
  Root, ReportPath, MissingPath, Helper: String;
  Code: Integer;
  Lines: TArrayOfString;
begin
  Root := ExpandConstant('{tmp}\report-permission-fixture');
  if not CreateDir(Root) then RaiseException('Cannot create fixture directory');
  ReportPath := Root + '\report.txt';
  MissingPath := Root + '\not-created.txt';
  ExtractTemporaryFile('agentclihub-browser.exe');
  Helper := ExpandConstant('{tmp}\agentclihub-browser.exe');
  if not PrepareMigrationReport(ReportPath) then RaiseException('Cannot prepare report');
  { Reproduce the access that an unelevated helper has under elevated Setup. }
  SetTestDirectoryAccess(Root, 'GRGX');
  try
    if SaveStringToFile(MissingPath, 'unexpected', False) then
      RaiseException('Fixture is not read-only; run this test unelevated');
    Log('PASS report directory prevents ordinary file creation');
    if not Exec(Helper, '--upgrade invalid --report "' + MissingPath + '"', '',
        SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 11) then
      RaiseException('Unwritable report did not return controlled code 11');
    Log('PASS packaged helper rejects an unwritable report without crashing');
    if not Exec(Helper, '--upgrade invalid --report "' + ReportPath + '"', '',
        SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 10) then
      RaiseException('Prepared report was not writable by the packaged helper');
    if not LoadStringsFromFile(ReportPath, Lines) or (GetArrayLength(Lines) = 0) then
      RaiseException('Original migration error did not reach Setup');
    Log('PASS prepared report returns the original migration error from the packaged helper');
    if SaveStringToFile(MissingPath, 'unexpected', False) then
      RaiseException('Parent directory permissions were widened');
    Log('PASS report permission does not allow creating sibling files');
  finally
    SetTestDirectoryAccess(Root, 'FA');
  end;
  Result := True;
end;
