using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentCLIHub;

var root = Path.Combine(Path.GetTempPath(), "agentclihub-host-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var originalPath = Environment.GetEnvironmentVariable("PATH");
var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
    passed++;
}
string FileAt(string relative, string content = "")
{
    var path = Path.GetFullPath(Path.Combine(root, relative));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content, new UTF8Encoding(false));
    return path;
}
(int Exit, string Output, string Error) Run(string line)
{
    var split = typeof(CliLocator).GetMethod("SplitCommandLine", BindingFlags.NonPublic | BindingFlags.Static)!;
    var args = (string[])split.Invoke(null, [line])!;
    var start = new ProcessStartInfo(args[0]) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
    foreach (var arg in args.Skip(1)) start.ArgumentList.Add(arg);
    start.Environment["PATH"] = CliLocator.LaunchPath();
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(10000)) { process.Kill(true); throw new Exception("command timed out"); }
    return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
}
try
{
    Check(new Settings().UiLanguage == "ko", "existing settings default to Korean");
    var localized = JsonSerializer.Deserialize<Settings>("{\"UiLanguage\":\"EN\",\"CodexModel\":\"keep-model\"}")!;
    Check(localized.UiLanguage == "en" && localized.CodexModel == "keep-model" &&
        JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(localized))!.UiLanguage == "en",
        "UI language persists independently from CLI settings");
    Check(new Settings { UiLanguage = "unknown" }.UiLanguage == "ko", "unsupported UI language falls back to Korean");
    var real = CliLocator.Locate(CliProfile.Codex);
    Check(real.Found, "installed Codex is found without a configured path");
    var version = Run(real.LaunchCommand(real.CommandHead() + " --version"));
    Check(version.Exit == 0 && version.Output.Contains("codex-cli"), "automatically detected Codex actually runs: " + version.Output.Trim());
    Check(real.UsePowerShellProfile ? File.Exists(real.PowerShellExe) : File.Exists(real.Path),
        "automatically detected Codex has a usable launcher: " + real.Source);

    void Junction(string path, string target)
    {
        var script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + path.Replace("'", "''") +
                     "' -Target '" + target.Replace("'", "''") + "' | Out-Null";
        var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe");
        var run = Run("\"" + ps + "\" -NoProfile -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        if (run.Exit != 0) throw new Exception(run.Error);
    }
    var firstExe = FileAt("standalone/releases/0.9.0/bin/codex.exe");
    var nextExe = FileAt("standalone/releases/0.10.0/bin/codex.exe");
    var current = Path.Combine(root, "standalone/current");
    var entry = Path.Combine(root, "stable-bin");
    Junction(current, Path.GetDirectoryName(Path.GetDirectoryName(firstExe)!)!);
    if (Directory.Exists(Path.Combine(current, "bin")))
    {
        Junction(entry, Path.Combine(current, "bin"));
        Environment.SetEnvironmentVariable("PATH", entry);
        Check(CliLocator.Locate(CliProfile.Codex).Path == firstExe,
            "automatic Codex resolves both parent directory junctions to the actual exe");
        Directory.Delete(current); // Only this junction is removed; its target remains in the test directory.
        Junction(current, Path.GetDirectoryName(Path.GetDirectoryName(nextExe)!)!);
        Check(CliLocator.Locate(CliProfile.Codex).Path == nextExe,
            "next automatic launch follows an updated current junction without caching the old release");
    }
    else Console.WriteLine("SKIP 2 junction traversal checks: Windows cannot follow the test junction in this environment");

    var installed = FileAt("fallback-home/packages/standalone/releases/0.10.0-x86_64-pc-windows-msvc/bin/codex.exe");
    var marker = FileAt("fallback-home/packages/standalone/auto-update-version", "0.10.0-x86_64-pc-windows-msvc\n");
    var fallback = typeof(CliProfile).GetMethod("CodexStandaloneCandidates", BindingFlags.NonPublic | BindingFlags.Static)!;
    IEnumerable<(string path, string src)> Standalone() => (IEnumerable<(string path, string src)>)fallback.Invoke(null, [Path.Combine(root, "fallback-home")])!;
    Check(Standalone().FirstOrDefault(c => File.Exists(c.path)).path == installed,
        "missing stable/current junctions fall back to the installer's recorded release");
    File.WriteAllText(marker, "../../outside");
    Check(Standalone().All(c => !File.Exists(c.path)), "invalid standalone version marker cannot select a path outside releases");
    File.Delete(marker);
    Check(Standalone().All(c => !File.Exists(c.path)), "missing version marker does not pick an arbitrary old release");
    Environment.SetEnvironmentVariable("PATH", originalPath);

    using (var failedPty = new ConPty())
    {
        try { failedPty.Start("\"" + Path.Combine(root, "missing.exe") + "\"", root, 80, 24); throw new Exception("missing executable started"); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Check(ex.NativeErrorCode == 2 && ex.Message.Contains("Windows 오류 2"),
                "terminal startup failure retains the native Windows error code and description");
        }
    }

    // This machine has Codex on the registered PATH. Simulate an Explorer process with a stale PATH.
    Environment.SetEnvironmentVariable("PATH", Path.Combine(root, "empty"));
    Check(CliLocator.Locate(CliProfile.Codex).Found, "fresh registered PATH / installation fallback survives a stale process PATH");

    var native = FileAt("native/codex.exe");
    Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(native));
    Check(CliLocator.Locate(CliProfile.Codex).Path == native, "native executable on process PATH");
    Check(CliLocator.Locate(CliProfile.Codex, Path.Combine(root, "removed/codex.exe")).Path == native, "removed version path falls back to auto detection");
    Check(CliLocator.Locate(CliProfile.Codex, "\"" + native + "\"").Path == native, "quoted configured path is accepted");
    Environment.SetEnvironmentVariable("AGENTCLIHUB_TEST_BIN", Path.GetDirectoryName(native));
    Environment.SetEnvironmentVariable("PATH", "\"%AGENTCLIHUB_TEST_BIN%\"");
    Check(CliLocator.Locate(CliProfile.Codex).Path == native, "PATH quotes and environment expansion");

    var npm = FileAt("npm 경로/codex.cmd", "@echo off\r\nnode \"%~dp0node_modules/@openai/codex/bin/codex.js\" %*\r\n");
    var js = FileAt("npm 경로/node_modules/@openai/codex/bin/codex.js", "console.log(JSON.stringify(process.argv.slice(2))); process.exit(7);");
    Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(npm) + ";" + Path.GetDirectoryName(native) + ";" + originalPath);
    var result = CliLocator.Locate(CliProfile.Codex);
    Check(result.Path == npm, "PATH directory priority is preserved across cmd/exe candidates");
    Check(result.CanBypassCmd && result.CliJs == js, "Codex npm launcher resolves to node + bin/codex.js");
    var npmRun = Run(result.LaunchCommand(result.CommandHead() + " resume abc -m \"한글 모델\" -c model_reasoning_effort=high"));
    Check(npmRun.Exit == 7 && JsonSerializer.Deserialize<string[]>(npmRun.Output.Trim())!.SequenceEqual(
        new[] { "resume", "abc", "-m", "한글 모델", "-c", "model_reasoning_effort=high" }), "npm launcher preserves arguments, Korean and exit code");

    var node = result.NodeExe!;
    var cmd = FileAt("custom 폴더/codex.cmd", "@echo off\r\n\"" + node + "\" \"%~dp0args.js\" %*\r\n");
    FileAt("custom 폴더/args.js", "console.log(JSON.stringify(process.argv.slice(2))); process.exit(7);");
    result = CliLocator.Locate(CliProfile.Codex, cmd);
    var cmdRun = Run(result.LaunchCommand(result.CommandHead() + " login \"한글 값\""));
    Check(cmdRun.Exit == 7 && JsonSerializer.Deserialize<string[]>(cmdRun.Output.Trim())!.SequenceEqual(new[] { "login", "한글 값" }), "custom cmd launcher runs through PowerShell");

    var agyCmd = FileAt("custom 폴더/agy.cmd", "@echo off\r\n\"" + node + "\" \"%~dp0args.js\" %*\r\n");
    var agyLauncher = CliLocator.Locate(CliProfile.Antigravity, agyCmd);
    var handoffPath = Path.Combine(root, "한글 작업 폴더", ".agentclihub-handoff", Guid.NewGuid() + ".json");
    var handoffLine = CliProfile.Antigravity.BuildCommandLine(agyLauncher.CommandHead(),
        new Settings { AntigravityModel = "test-model" }, ResumeMode.Handoff(handoffPath), root);
    var handoffRun = Run(agyLauncher.LaunchCommand(handoffLine));
    Check(handoffRun.Exit == 7 && JsonSerializer.Deserialize<string[]>(handoffRun.Output.Trim())!.SequenceEqual(
        new[] { "--prompt-interactive", AntigravityTransfer.Prompt(handoffPath), "--model", "test-model" }),
        "agy cmd wrapper preserves the entire handoff request, Korean path and subsequent model arguments");

    var ps = FileAt("powershell 폴더/codex.ps1", "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); ConvertTo-Json -InputObject @($args) -Compress; exit 7");
    result = CliLocator.Locate(CliProfile.Codex, ps);
    var psRun = Run(result.LaunchCommand(result.CommandHead() + " resume abc \"한글 값\""));
    Check(psRun.Exit == 7 && JsonSerializer.Deserialize<string[]>(psRun.Output.Trim())!.SequenceEqual(new[] { "resume", "abc", "한글 값" }), "PowerShell script preserves arguments and exit code");

    Environment.SetEnvironmentVariable("PATH", originalPath);
    var method = typeof(CliLocator).GetMethod("FindWithPowerShell", BindingFlags.NonPublic | BindingFlags.Static)!;
    // The real installer's junction may be broken. Give this shell-specific test a valid executable directory.
    Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(real.Path) + ";" + originalPath);
    var shellResult = (CliLocator.Result?)method.Invoke(null, [CliProfile.Codex]);
    Check(shellResult?.Found == true && (File.Exists(shellResult.Path) || shellResult.UsePowerShellProfile),
        "PowerShell Get-Command fallback parses the current product marker and resolves installed Codex");
    var shellRun = Run(shellResult!.LaunchCommand(shellResult.CommandHead() + " --version"));
    Check(shellRun.Exit == 0 && shellRun.Output.Contains("codex-cli"), "PowerShell fallback runs in the shell that found Codex");
    Environment.SetEnvironmentVariable("PATH", originalPath);

    var cfg = new Settings();
    foreach (var resume in new[] { ResumeMode.New, ResumeMode.Session("test-session"), ResumeMode.Last })
    {
        var line = CliProfile.Codex.BuildCommandLine("codex", cfg, resume, root, root);
        Check(line.Contains("tui.alternate_screen=always") && line.Contains("tui.fullscreen_transcript=true"), "Codex screen mode applies to " + resume);
    }
    var claudeLine = CliProfile.Claude.BuildCommandLine("claude", cfg, ResumeMode.New, root, root);
    Check(!claudeLine.Contains("tui."), "Codex screen options do not leak into Claude");
    Check(!CliProfile.LoginCommandLine("codex", LoginKind.Codex).Contains("tui."), "login command keeps its own options");

    var ag = CliProfile.Antigravity;
    Check(CliProfile.For("antigravity_cli") == ag && CliProfile.All.Contains(ag), "Antigravity is a distinct selectable CLI");
    var agResult = CliLocator.Locate(ag);
    Check(agResult.Found && Path.GetFileName(agResult.Path).StartsWith("agy.", StringComparison.OrdinalIgnoreCase),
        "installed Antigravity CLI detected automatically: " + agResult.Path + " (" + agResult.Source + ")");
    var agVersion = Run(agResult.LaunchCommand(agResult.CommandHead() + " --version"));
    Check(agVersion.Exit == 0 && agVersion.Output.Trim().Length > 0, "detected Antigravity actually runs: " + agVersion.Output.Trim());
    var agModels = await AntigravityModels.ReadAsync(agResult);
    Check(agModels.Error is null && agModels.Models.Count > 0,
        "installed agy models returns live model choices: " + agModels.Models.Count);
    var modelCatalog = JsonSerializer.SerializeToElement(CliCatalog.ForUi(agModels.Models)).GetProperty(ag.Backend);
    var firstModel = agModels.Models[0];
    Check(modelCatalog.GetProperty("models").EnumerateArray().Any(m => m.GetString() == firstModel.Id) &&
        modelCatalog.GetProperty("modelLabels").GetProperty(firstModel.Id).GetString() == firstModel.Name,
        "model selector receives actual CLI IDs and display names");
    var parsedModels = AntigravityModels.Parse("Fetching available models...\nID\tName\n\u001b[32mtest-id\tTest Model\u001b[0m\r\nTEST-ID\tDuplicate\ninvalid id\tInvalid\nbroken\nother-id\t한글 모델\n");
    Check(parsedModels.Count == 2 && parsedModels[0].Id == "test-id" && parsedModels[1].Name == "한글 모델",
        "model parser excludes status/header/invalid rows, strips color and deduplicates IDs");
    var modelOutput = FileAt("model query/models.txt", "first-id\tFirst Model\n");
    var modelScript = FileAt("model query/agy.ps1", "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); Get-Content -LiteralPath '" + modelOutput.Replace("'", "''") + "'; exit 0");
    var modelCli = CliLocator.Locate(ag, modelScript);
    var beforeUpdate = await AntigravityModels.ReadAsync(modelCli);
    File.WriteAllText(modelOutput, "next-id\tNext Model\n");
    var afterUpdate = await AntigravityModels.ReadAsync(modelCli);
    Check(beforeUpdate.Models.Single().Id == "first-id" && afterUpdate.Models.Single().Id == "next-id",
        "model refresh reads changed CLI output instead of keeping a hardcoded model list");
    File.WriteAllText(modelScript, "[Console]::WriteLine('wrong-id`tDo not use'); exit 7");
    var modelFailure = await AntigravityModels.ReadAsync(modelCli);
    Check(modelFailure.Models.Count == 0 && modelFailure.Error?.Contains("7") == true,
        "failed model query reports failure without exposing partial choices");
    File.WriteAllText(modelScript, "Start-Sleep -Seconds 20");
    using (var cancelModels = new CancellationTokenSource(300))
    {
        var canceled = false;
        var timer = Stopwatch.StartNew();
        try { await AntigravityModels.ReadAsync(modelCli, cancelModels.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled && timer.Elapsed < TimeSpan.FromSeconds(5), "superseded model query cancels without waiting for its CLI");
    }
    Check(CliCatalog.ClampModel(ag.Backend, "saved-custom-model") == "saved-custom-model",
        "model refresh and transient errors never erase a saved custom model");
    Environment.SetEnvironmentVariable("PATH", Path.Combine(root, "empty"));
    Check(CliLocator.Locate(ag).Found, "Antigravity official installation found with stale PATH");
    Environment.SetEnvironmentVariable("PATH", originalPath);
    Check(CliLocator.LaunchPath(ag).Split(';').Any(p => File.Exists(Path.Combine(p, "bash.exe"))), "Antigravity child environment includes Git Bash");
    var isolated = new Settings { ClaudeModel = "opus", CodexModel = "gpt-example", CodexEffort = "high",
        CodexApproval = "never", CodexSandbox = "danger-full-access", CodexCliPath = "old-codex", ClaudeCliPath = "old-claude" };
    Check(ag.BuildCommandLine("agy", isolated, ResumeMode.New, root) == "agy", "new Antigravity starts without another CLI's flags or bypassing approvals");
    Check(ag.CachedPath(isolated) == "" && ag.NpmCliJs is null, "Antigravity detection ignores Codex/Claude paths and npm entrypoints");
    Check(ag.BuildCommandLine("agy", isolated, ResumeMode.Last, root) == "agy --continue", "restored Antigravity tab continues its workspace");
    var agId = Guid.NewGuid().ToString();
    Check(ag.BuildCommandLine("agy", isolated, ResumeMode.Session(agId), root) == "agy --conversation " + agId, "Antigravity resumes selected conversation");
    isolated.AntigravityPermission = "sandbox";
    Check(ag.BuildCommandLine("agy", isolated, ResumeMode.New, root) == "agy --sandbox", "Antigravity sandbox option");
    isolated.AntigravityPermission = "dangerously-skip-permissions";
    Check(ag.BuildCommandLine("agy", isolated, ResumeMode.New, root) == "agy --dangerously-skip-permissions", "Antigravity explicit approval bypass option");
    isolated.AntigravityPermission = "never";
    Check(ag.BuildCommandLine("agy", isolated, ResumeMode.New, root) == "agy", "Antigravity rejects unsupported permission values");
    var agAccount = Accounts.Of(isolated, ag).Single();
    Check(agAccount.IsDefault && agAccount.Email == "" && ag.SupportsAccounts && ag.SupportsTransfer,
        "Antigravity supports isolated extra accounts and conversation transfer using native CLI login");
    Check(SessionFinder.Latest(ag, root, root) is null, "Antigravity is not read as a Codex transcript");
    var agCatalog = JsonSerializer.SerializeToElement(CliCatalog.ForUi()).GetProperty(ag.Backend);
    Check(agCatalog.GetProperty("pathKey").GetString() == "AntigravityCliPath" &&
        agCatalog.GetProperty("modelKey").GetString() == "AntigravityModel" && agCatalog.GetProperty("perm").GetArrayLength() == 1,
        "Antigravity settings expose only supported fields");
    isolated.AntigravityModel = "Gemini model \"quoted\"\\";
    isolated.AntigravityEffort = "high";
    var agLine = ag.BuildCommandLine("agy", isolated, ResumeMode.New, root);
    var splitter = typeof(CliLocator).GetMethod("SplitCommandLine", BindingFlags.NonPublic | BindingFlags.Static)!;
    var agArgs = (string[])splitter.Invoke(null, [agLine])!;
    Check(agArgs.SequenceEqual(new[] { "agy", "--model", isolated.AntigravityModel, "--effort", "high" }),
        "Antigravity model names preserve spaces, quotes and trailing slashes");
    Check(CliCatalog.ClampEffort(ag.Backend, "", "xhigh") == "" && CliCatalog.ClampEffort(ag.Backend, "", "max") == "max",
        "Antigravity reasoning levels follow its CLI");

    var agHome = Path.Combine(root, "agy-home");
    var agFirst = Guid.NewGuid().ToString();
    var agCached = Guid.NewGuid().ToString();
    var agDeleted = Guid.NewGuid().ToString();
    FileAt("agy-home/conversations/" + agFirst + ".pb");
    FileAt("agy-home/conversations/" + agCached + ".pb");
    var agRows = new object[] {
        new { conversationId = agFirst, workspace = root, display = "첫 질문", timestamp = 1000L },
        new { conversationId = agFirst, workspace = root, display = "두 번째 질문", timestamp = 2000L },
        new { conversationId = agDeleted, workspace = root, display = "삭제됨", timestamp = 3000L },
        new { conversationId = "../../outside", workspace = root, display = "invalid", timestamp = 4000L } };
    FileAt("agy-home/history.jsonl", string.Join("\n", agRows.Select(o => JsonSerializer.Serialize(o))) + "\n{partial");
    FileAt("agy-home/cache/last_conversations.json", JsonSerializer.Serialize(new Dictionary<string, string> { [root] = agCached }));
    var agHistory = AntigravitySessions.Recent(agHome);
    Check(agHistory.Count == 2 && agHistory.Any(s => s.Id == agCached), "Antigravity history includes cache-only conversations and excludes deleted/invalid records");
    var first = agHistory.Single(s => s.Id == agFirst);
    Check(first.Title == "첫 질문" && first.When.ToUniversalTime() == DateTimeOffset.FromUnixTimeMilliseconds(2000).UtcDateTime,
        "Antigravity history groups conversation ID with first question and latest timestamp");
    Check(agHistory.All(s => s.Backend == "antigravity_cli" && s.Cwd == root && s.Account == ""), "Antigravity history retains workspace and CLI identity");
    TransferChecks.Run(root, Check);
    Console.WriteLine($"{passed} checks passed");
}
finally
{
    Environment.SetEnvironmentVariable("PATH", originalPath);
    Environment.SetEnvironmentVariable("AGENTCLIHUB_TEST_BIN", null);
    // Remove junctions before their targets; recursive enumeration can otherwise meet a broken junction.
    foreach (var relative in new[] { "stable-bin", "standalone/current" })
    {
        var link = Path.GetFullPath(Path.Combine(root, relative));
        if (!link.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("test cleanup escaped its temporary directory");
        if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
    }
    // Only the uniquely named test directory created above is removed.
    Directory.Delete(root, recursive: true);
}
