{ Windows 시작프로그램(HKCU\...\Run) 및 작업 관리자 억제 표식(StartupApproved\Run) 처리.
  AgentCLIHub.iss 와 tests\InstallerChecks\StartupLifecycle.iss 가 함께 포함해 검증한다. }
function IsStartupEnabledIn(RunSubKey, ApprovedSubKey, ValueName: String): Boolean;
var
  Cmd: String;
  Approved: AnsiString;
begin
  Result := False;
  if not RegQueryStringValue(HKCU, RunSubKey, ValueName, Cmd) or (Trim(Cmd) = '') then
    if not RegQueryStringValue(HKCU, RunSubKey, 'LLM Tools', Cmd) or (Trim(Cmd) = '') then
      if not RegQueryStringValue(HKCU, RunSubKey, 'Agent CLI Hub', Cmd) or (Trim(Cmd) = '') then
        exit;
  if RegQueryBinaryValue(HKCU, ApprovedSubKey, ValueName, Approved) and
     (Length(Approved) > 0) and ((Ord(Approved[1]) and 1) <> 0) then
    exit;
  Result := True;
end;

procedure ApplyStartupPostInstall(
  RunSubKey, ApprovedSubKey, ValueName, ExePath: String;
  TaskSelected, SilentWithoutTasksParam, HadBefore: Boolean);
begin
  RegDeleteValue(HKCU, RunSubKey, 'LLM Tools');
  RegDeleteValue(HKCU, RunSubKey, 'Agent CLI Hub');
  if TaskSelected then begin
    RegWriteStringValue(HKCU, RunSubKey, ValueName, '"' + ExePath + '"');
    RegDeleteValue(HKCU, ApprovedSubKey, ValueName);
  end else if SilentWithoutTasksParam and HadBefore then
    RegWriteStringValue(HKCU, RunSubKey, ValueName, '"' + ExePath + '"')
  else
    RegDeleteValue(HKCU, RunSubKey, ValueName);
end;

procedure CleanupStartupOnUninstall(RunSubKey, ApprovedSubKey, ValueName: String);
begin
  RegDeleteValue(HKCU, RunSubKey, ValueName);
  RegDeleteValue(HKCU, ApprovedSubKey, ValueName);
  RegDeleteValue(HKCU, RunSubKey, 'LLM Tools');
  RegDeleteValue(HKCU, RunSubKey, 'Agent CLI Hub');
end;
