using System;
using System.IO;
using Microsoft.Win32;

namespace AgentCLIHub;

/// <summary>
/// Windows 로그인 시 자동 실행(<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>)을 다룬다.
///
/// <b>설치 파일(Inno Setup)과 작업 관리자도 같은 레지스트리를 만진다.</b>
/// 그래서 설정 파일(<c>settings.json</c>)에 따로 복사해 두지 않고, 설정 화면을 열 때마다
/// 레지스트리에서 직접 읽는다 — 두 군데 적으면 작업 관리자에서 끈 뒤에도 설정 화면은
/// "켜짐" 으로 거짓말을 한다.
///
/// 작업 관리자 · Windows 설정의 [시작 앱] 에서 끄면 <c>Run</c> 값은 그대로 두고
/// <c>StartupApproved\Run</c> 의 첫 바이트를 홀수(<c>0x03</c> 등)로 적는다. 읽을 때는 그것까지
/// 함께 봐야 실제 동작과 일치하고, 앱에서 다시 켤 때는 그 억제 표식을 지워 줘야 실제로 뜬다.
/// </summary>
public static class StartupRegistration
{
    public const string DefaultRunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultApprovedSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string DefaultValueName = "AgentCLIHub";
    private const string ExpectedExeName = "AgentCLIHub.exe";

    public const string StartupArg = "--startup";

    public static string FormatRunCommand(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("실행 파일 경로가 비어 있습니다.", nameof(executablePath));

        var full = Path.GetFullPath(executablePath.Trim().Trim('"'));
        return $"\"{full}\" {StartupArg}";
    }

    public static bool IsEnabled(
        string runSubKey = DefaultRunSubKey,
        string approvedSubKey = DefaultApprovedSubKey,
        string valueName = DefaultValueName)
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(runSubKey, writable: false);
            if (runKey?.GetValue(valueName) is not string raw || string.IsNullOrWhiteSpace(raw))
                return false;

            using var approvedKey = Registry.CurrentUser.OpenSubKey(approvedSubKey, writable: false);
            if (approvedKey?.GetValue(valueName) is byte[] bytes && bytes.Length > 0 && (bytes[0] & 1) != 0)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(
        bool enabled,
        string? executablePath = null,
        string runSubKey = DefaultRunSubKey,
        string approvedSubKey = DefaultApprovedSubKey,
        string valueName = DefaultValueName)
    {
        if (enabled)
        {
            var exe = string.IsNullOrWhiteSpace(executablePath) ? Environment.ProcessPath : executablePath;
            if (string.IsNullOrWhiteSpace(exe))
                throw new InvalidOperationException("현재 실행 파일 경로를 확인할 수 없습니다.");

            var full = Path.GetFullPath(exe.Trim().Trim('"'));
            if (!File.Exists(full))
                throw new FileNotFoundException("자동 실행으로 등록할 실행 파일을 찾지 못했습니다.", full);

            using var runKey = Registry.CurrentUser.CreateSubKey(runSubKey, writable: true)
                ?? throw new InvalidOperationException("시작프로그램 레지스트리 키를 열지 못했습니다.");
            runKey.SetValue(valueName, FormatRunCommand(full), RegistryValueKind.String);

            using var approvedKey = Registry.CurrentUser.OpenSubKey(approvedSubKey, writable: true);
            if (approvedKey?.GetValue(valueName) is byte[] bytes && bytes.Length > 0 && (bytes[0] & 1) != 0)
                approvedKey.DeleteValue(valueName, throwOnMissingValue: false);
            return;
        }

        using (var runKey = Registry.CurrentUser.OpenSubKey(runSubKey, writable: true))
            runKey?.DeleteValue(valueName, throwOnMissingValue: false);

        using (var approvedKey = Registry.CurrentUser.OpenSubKey(approvedSubKey, writable: true))
            approvedKey?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// 자동 실행이 켜져 있을 때 실행 파일 위치가 바뀌었으면(폴더 이동·수동 업데이트 등)
    /// 현재 <c>AgentCLIHub.exe</c> 경로로 맞춰 둔다. 테스트 호스트 등 다른 실행 파일에서는 건드리지 않는다.
    /// </summary>
    public static void SyncExecutableIfEnabled(
        string? executablePath = null,
        string runSubKey = DefaultRunSubKey,
        string approvedSubKey = DefaultApprovedSubKey,
        string valueName = DefaultValueName)
    {
        var exe = string.IsNullOrWhiteSpace(executablePath) ? Environment.ProcessPath : executablePath;
        if (string.IsNullOrWhiteSpace(exe)) return;

        var full = Path.GetFullPath(exe.Trim().Trim('"'));
        if (!string.Equals(Path.GetFileName(full), ExpectedExeName, StringComparison.OrdinalIgnoreCase)) return;
        if (!File.Exists(full)) return;
        if (!IsEnabled(runSubKey, approvedSubKey, valueName)) return;

        var expected = FormatRunCommand(full);
        using var runKey = Registry.CurrentUser.OpenSubKey(runSubKey, writable: true);
        if (runKey is null) return;
        if (runKey.GetValue(valueName) is string current &&
            string.Equals(current.Trim(), expected, StringComparison.OrdinalIgnoreCase))
            return;

        runKey.SetValue(valueName, expected, RegistryValueKind.String);
    }
}
