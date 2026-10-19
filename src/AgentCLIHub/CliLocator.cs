using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace AgentCLIHub;

/// <summary>
/// CLI 자동 탐지. **설치 경로가 PC 마다 다르다** — npm 전역, 공식 설치 관리자, 사용자 홈이
/// 각각 다른 자리에 놓고, 사용자가 직접 옮겨 두기도 한다. 그래서 한 자리를 박아 두지 않는다.
///
/// **무엇을 찾을지는 <see cref="CliProfile"/> 이 정한다.** 찾는 방법(PATH → 관례 경로 →
/// npm 래퍼 해체)은 CLI 종류와 무관하므로 여기 한 벌만 둔다.
///
/// claude 에서 가장 중요한 결과는 <see cref="Result.NodeExe"/> / <see cref="Result.CliJs"/> 다.
/// .cmd 래퍼를 cmd.exe 로 감싸 실행하면 한국어 인자가 cp949 로 깨진다.
/// node.exe + cli.js 를 직접 부르면 그 경로를 통째로 우회한다.
/// Codex 도 npm 설치본은 node + bin/codex.js 로 실행한다.
/// </summary>
public static class CliLocator
{
    public sealed class Result
    {
        public bool Found => !string.IsNullOrEmpty(Path);
        public string Path = "";
        public string Source = "";
        public string? NodeExe;
        public string? CliJs;
        public string? PowerShellExe;
        public bool UsePowerShellProfile;
        /// <summary>node + cli.js 직접 호출 가능 — cmd.exe 우회. 한글 인코딩 문제 회피.</summary>
        public bool CanBypassCmd => !string.IsNullOrEmpty(NodeExe) && !string.IsNullOrEmpty(CliJs);
        public string? Error;

        /// <summary>
        /// 커맨드라인의 맨 앞 — **이미 따옴표가 붙어 있다.** 뒤에 플래그를 이어 붙이면 된다.
        ///
        /// npm 래퍼를 찾았고 node 로 우회할 수 있으면 `node "…\cli.js"` 를 준다.
        /// cmd.exe 한 겹이 사라지므로 인자가 그 프로세스의 코드페이지를 타지 않는다 —
        /// 한글이 섞인 경로·모델명이 cp949 로 깨지던 자리다.
        /// 네이티브 설치본은 실행 파일 하나뿐이다.
        /// </summary>
        public string CommandHead() =>
            CanBypassCmd ? Quote(NodeExe!) + " " + Quote(CliJs!) : Quote(Path);

        /// <summary>스크립트는 CreateProcessW 로 직접 실행할 수 없다. 인자를 문자열 리터럴로 전달한다.</summary>
        public string LaunchCommand(string commandLine)
        {
            if (!UsePowerShellProfile && (CanBypassCmd || (PowerShellExe is null &&
                !new[] { ".cmd", ".bat", ".ps1" }.Contains(System.IO.Path.GetExtension(Path), StringComparer.OrdinalIgnoreCase))))
                return commandLine;
            var args = SplitCommandLine(commandLine);
            var script = "$ErrorActionPreference='Stop'; $LASTEXITCODE=$null; & " + string.Join(" ", args.Select(PsLiteral)) +
                         "; $ok=$?; if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }; if (-not $ok) { exit 1 }";
            return Quote(PowerShellExe ?? WindowsPowerShell) + " -NoLogo " +
                   (UsePowerShellProfile ? "" : "-NoProfile ") + "-EncodedCommand " +
                   Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }

        private static string Quote(string p) =>
            "\"" + p + "\"";
    }

    public static Result Locate(CliProfile profile, string? cachedPath = null)
    {
        if (FileResult(profile, cachedPath, "설정 지정") is { } configured) return configured;

        // Codex 의 공식 설치는 버전 폴더를 연결 경로 뒤에서 교체한다.
        // PowerShell 에서 실행하는 명령을 먼저 찾고, 매번 실제 대상 파일을 확인한다.
        if (profile.Kind == CliKind.Codex && FindWithPowerShell(profile) is { } codex) return codex;

        foreach (var (path, src) in Candidates(profile))
        {
            if (FileResult(profile, path, src) is { } file) return file;
        }

        // PowerShell 프로필에서 PATH 를 더하거나 별칭/함수로 등록한 설치도 찾는다.
        // 빠른 파일 탐색으로 못 찾았을 때만 실행하며, CLI 자체는 실행하지 않는다.
        var shell = profile.Kind == CliKind.Codex ? null : FindWithPowerShell(profile);
        if (shell is not null) return shell;
        return new Result { Error = profile.NotFoundMessage };
    }

    private static Result? FileResult(CliProfile profile, string? path, string source)
    {
        if (NormalizePath(path) is not { } full || !File.Exists(full)) return null;
        var result = new Result { Path = full, Source = source };
        if (profile.Kind == CliKind.Codex && Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            result.Path = ResolveNativePath(full);
        TryResolveNodeDirect(profile, full, result);
        return result;
    }

    private static IEnumerable<(string path, string src)> Candidates(CliProfile p)
    {
        // 이름보다 PATH 의 디렉터리 순서가 먼저다. 앞 디렉터리의 npm 설치를 뒤의 exe 가 가로채면 안 된다.
        foreach (var dir in PathDirectories())
            foreach (var exe in p.Exes)
                yield return (Path.Combine(dir, exe), "PATH:" + exe);

        foreach (var c in p.InstallCandidates()) yield return c;
    }

    /// <summary>PATH 탐색. `where` 프로세스를 띄우지 않고 직접 훑는다(빠르고 조용함).</summary>
    private static IEnumerable<string> Where(string exe)
    {
        foreach (var dir in PathDirectories())
        {
            var full = Path.Combine(dir, exe);
            if (File.Exists(full)) yield return full;
        }
    }

    // 실행 중인 앱/탐색기는 설치 전 PATH 를 갖고 있을 수 있다. 현재 사용자·시스템 값도 매번 읽는다.
    private static IEnumerable<string> PathDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? value;
            try { value = Environment.GetEnvironmentVariable("PATH", target); } catch { continue; }
            foreach (var entry in (value ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                if (NormalizePath(entry) is { } dir && seen.Add(dir)) yield return dir;
        }
    }

    public static string LaunchPath(CliProfile? profile = null)
    {
        var dirs = PathDirectories().ToList();
        if (profile?.Kind == CliKind.Antigravity)
        {
            // Git\\cmd 만 PATH 에 들어 있는 Windows 설치에서도 agy 의 Git Bash 를 찾는다.
            var gitBins = Where("git.exe").Select(p => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(p)!, "..", "bin")))
                .Concat(new[] {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin") });
            var bash = gitBins.FirstOrDefault(p => File.Exists(Path.Combine(p, "bash.exe")));
            if (bash is not null) dirs.Insert(0, bash);
        }
        return string.Join(Path.PathSeparator, dirs.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))); }
        catch { return null; }
    }

    // File.ResolveLinkTarget 는 부모 디렉터리의 junction 을 풀지 않는다.
    // 파일 핸들에서 최종 경로를 읽어 current/bin 같은 연결도 실제 exe 로 바꾼다.
    private static string ResolveNativePath(string path)
    {
        try
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new StringBuilder(1024);
            var size = GetFinalPathNameByHandleW(file, buffer, (uint)buffer.Capacity, 0);
            if (size == 0) return path;
            if (size >= buffer.Capacity)
            {
                buffer.EnsureCapacity(checked((int)size + 1));
                size = GetFinalPathNameByHandleW(file, buffer, (uint)buffer.Capacity, 0);
                if (size == 0 || size >= buffer.Capacity) return path;
            }
            var final = buffer.ToString();
            if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + final[8..];
            return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        }
        catch { return path; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    private static string WindowsPowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                                             "WindowsPowerShell", "v1.0", "powershell.exe");
    private static string PsLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    private static Result? FindWithPowerShell(CliProfile profile)
    {
        const string marker = "AGENTCLIHUB_COMMAND:";
        var name = profile.CommandName;
        foreach (var shell in Where("pwsh.exe").Append(WindowsPowerShell).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(shell)) continue;
            try
            {
                var script = "$ErrorActionPreference='Stop'; $c=Get-Command " + PsLiteral(name) +
                    " -ErrorAction Stop; if ($c.CommandType -eq 'Alias') { $c=$c.ResolvedCommand }; " +
                    "[Console]::WriteLine('" + marker + "' + (@{path=$c.Path; kind=[string]$c.CommandType} | ConvertTo-Json -Compress))";
                var info = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8 };
                info.Environment["PATH"] = string.Join(Path.PathSeparator, PathDirectories());
                info.ArgumentList.Add("-NoLogo");
                info.ArgumentList.Add("-NonInteractive");
                info.ArgumentList.Add("-EncodedCommand");
                info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
                    "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " + script)));
                using var process = Process.Start(info);
                if (process is null) continue;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(3000)) { try { process.Kill(entireProcessTree: true); } catch { } continue; }
                if (!output.Wait(500)) continue;
                var line = output.Result.Split('\n').LastOrDefault(l => l.StartsWith(marker, StringComparison.Ordinal));
                if (line is null) continue;
                using var json = JsonDocument.Parse(line[marker.Length..]);
                var kind = json.RootElement.GetProperty("kind").GetString();
                var path = json.RootElement.GetProperty("path").GetString();
                if (kind == "Function")
                    return new Result { Path = name, Source = "PowerShell 프로필", PowerShellExe = shell, UsePowerShellProfile = true };
                var result = FileResult(profile, path, "PowerShell:Get-Command");
                if (result is null) continue;
                // 프로필이 추가한 환경/모듈이 실행에도 필요할 수 있으므로 같은 셸로 띄운다.
                result.PowerShellExe = shell;
                result.UsePowerShellProfile = true;
                return result;
            }
            catch { /* 다음 셸/설치 안내로 이어진다 */ }
        }
        return null;
    }

    internal static string[] SplitCommandLine(string commandLine)
    {
        var memory = CommandLineToArgvW(commandLine, out var count);
        if (memory == IntPtr.Zero) throw new InvalidOperationException("CLI 인자를 읽지 못했습니다.");
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, i * IntPtr.Size)) ?? "").ToArray(); }
        finally { LocalFree(memory); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>
    /// .cmd 래퍼 안에서 node 경로와 cli.js 경로를 뽑아낸다.
    /// npm 래퍼는 대체로 `node "%~dp0\node_modules\...\cli.js" %*` 형태다.
    /// 우회할 래퍼가 없는 CLI(<see cref="CliProfile.NpmCliJs"/> 가 null)에서는 아무것도 안 한다.
    /// </summary>
    private static void TryResolveNodeDirect(CliProfile p, string cliPath, Result r)
    {
        if (p.NpmCliJs is not { } relative) return;

        try
        {
            if (cliPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;

            var dir = Path.GetDirectoryName(cliPath) ?? "";
            var text = File.ReadAllText(cliPath);

            var m = Regex.Match(text, @"""?%~dp0[\\/]?([^""%\s]*cli\.js)""?", RegexOptions.IgnoreCase);
            var js = m.Success ? Probe(Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value.Replace('/', '\\')))) : null;

            // 관례 경로 폴백
            js ??= Probe(Path.Combine(dir, relative));

            if (js is null) return;

            var node = Probe(Path.Combine(dir, "node.exe")) ?? Where("node.exe").FirstOrDefault()
                       ?? Probe(@"C:\Program Files\nodejs\node.exe");
            if (node is null) return;

            r.NodeExe = node;
            r.CliJs = js;
        }
        catch { /* 실패해도 .cmd 경로로 실행 가능 — 인코딩만 주의 */ }
    }

    private static string? Probe(string p) => File.Exists(p) ? p : null;
}
