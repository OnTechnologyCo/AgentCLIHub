using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentCLIHub;

static class RestoreChecks
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref uint value, nuint size);

    public static void Run(string root, Action<bool, string> check, bool enforceGuard)
    {
        var main = Path.Combine(root, "restore-main");
        var account = Path.Combine(root, "계정 나정호", "967c47");
        var real = Path.Combine(main, "projects");
        var link = Path.Combine(account, "projects");
        Directory.CreateDirectory(real);
        Directory.CreateDirectory(account);
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, real }) start.ArgumentList.Add(arg);
        using (var process = Process.Start(start)!)
        {
            process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit();
            check(process.ExitCode == 0, "fixture creates a real account projects junction with a Korean parent path");
        }
        try
        {
            if (enforceGuard)
            {
                uint flags = 1;
                check(SetProcessMitigationPolicy(16, ref flags, 4), "RedirectionGuard is enabled only in this test process");
                var blocked = false;
                try { Directory.CreateDirectory(Path.Combine(link, "blocked-project")); }
                catch (IOException ex) when ((ex.HResult & 0xffff) == 448) { blocked = true; }
                check(blocked, "native Windows error 448 reproduces the reported untrusted mount point failure");
            }
            check(HistoryPaths.ClaudeProjects(account) == real,
                "shared projects root resolves directly without traversing the junction");
            var cwd = Path.Combine(root, "D--99--Temp-ArgusProject");
            var transcript = new Transcript();
            transcript.Items.Add(new(ItemKind.User, "restore question"));
            transcript.Items.Add(new(ItemKind.Assistant, "restore answer"));
            var first = TranscriptWriter.ToClaude(transcript, cwd, "fixture", account);
            check(first.Path.StartsWith(real + Path.DirectorySeparatorChar) && File.Exists(first.Path),
                "cross-CLI import writes to the physical history path under RedirectionGuard");
            var aliasPath = Path.Combine(link, Path.GetRelativePath(real, first.Path));
            var source = new SessionRef(CliProfile.Claude, first.Id, aliasPath, DateTime.UtcNow);
            var transferred = ConversationTransfer.Prepare(CliProfile.Claude, source, transcript, cwd,
                CliProfile.Claude, account, "fixture").Mode;
            check(transferred.TranscriptPath == first.Path && SessionFinder.FindClaude(first.Id, account)?.Path == first.Path,
                "same-CLI transfer and lookup replace old junction paths with the physical transcript path");
            check(SessionStore.Claude(account, "fixture").Single().Id == first.Id,
                "history lists a junction-backed account under RedirectionGuard");
            var second = TranscriptWriter.ToClaude(transcript, cwd, "fixture", account);
            File.SetLastWriteTimeUtc(second.Path, DateTime.UtcNow.AddMinutes(1));
            var current = SessionFinder.Current(CliProfile.Claude, cwd, root, DateTime.UtcNow.AddMinutes(-1),
                first.Id, new HashSet<string>(), account);
            check(current?.Id == first.Id, "a newer same-folder Claude conversation cannot steal a known tab identity");
            var markers = Path.Combine(account, "sessions");
            Directory.CreateDirectory(markers);
            var marker = Path.Combine(markers, Environment.ProcessId + ".json");
            File.WriteAllText(marker, JsonSerializer.Serialize(new { pid = Environment.ProcessId,
                sessionId = second.Id, cwd, kind = "interactive", startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
            check(SessionFinder.ForProcess(CliProfile.Claude, cwd, account, DateTime.UtcNow,
                new HashSet<string>(), new HashSet<int> { Environment.ProcessId })?.Id == second.Id,
                "Claude native PID markers track an in-terminal session change without selecting another tab's latest history");
            File.WriteAllText(marker, "{\"pid\":");
            check(SessionFinder.ForProcess(CliProfile.Claude, cwd, account, DateTime.UtcNow,
                new HashSet<string>(), new HashSet<int> { Environment.ProcessId }) is null,
                "a partially written native session marker cannot replace the saved conversation");

            var tabs = new[] {
                new TabState { Cwd = cwd, Backend = CliProfile.Claude.Backend, Account = "a", Name = "First",
                    SessionId = first.Id, TranscriptPath = first.Path, Fresh = false },
                new TabState { Cwd = cwd, Backend = CliProfile.Claude.Backend, Account = "b", Name = "Second",
                    SessionId = second.Id, TranscriptPath = second.Path, Fresh = false }
            };
            var restored = JsonSerializer.Deserialize<TabState[]>(JsonSerializer.Serialize(tabs))!;
            check(restored.Select(t => SessionCheckpoint.Restore(t).SessionId).SequenceEqual(new[] { first.Id, second.Id }) &&
                  restored[0].Account == "a" && restored[1].Account == "b" && restored[0].TranscriptPath == first.Path,
                "restart preserves each tab's account, exact session UUID and transcript path in the same workspace");
            check(SessionCheckpoint.Restore(new TabState { Fresh = true }).Fresh &&
                  SessionCheckpoint.Restore(new TabState()).Equals(ResumeMode.Last),
                "new empty tabs and legacy saved tabs retain distinct restore modes");
            var native = new TerminalHost("fixture", cwd, CliProfile.Claude, "fixture-account", _ => { }, (_, _) => { })
                { InitialResume = ResumeMode.Session(first.Id, first.Path), ResumeTranscriptPath = first.Path };
            check(native.SavedState("alias").SessionId == first.Id && native.SavedState("alias").TranscriptPath == first.Path,
                "a restored or transferred session is saved even before its terminal starts");
            var cxHome = Path.Combine(root, "restore-codex");
            var cxA = TranscriptWriter.ToCodex(transcript, cwd, cxHome, "fixture");
            var cxB = TranscriptWriter.ToCodex(transcript, cwd, cxHome, "fixture");
            using (var opened = new FileStream(cxA.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                using var process = Process.GetCurrentProcess();
                var owners = NativeFileOwners.ProcessTree(process.Id, process.StartTime.ToUniversalTime());
                check(NativeFileOwners.IsOpenBy(cxA.Path, owners) && !NativeFileOwners.IsOpenBy(cxB.Path, owners),
                    "native file ownership distinguishes an open session from another conversation without changing processes");
                check(SessionFinder.ForProcess(CliProfile.Codex, cwd, cxHome, DateTime.UtcNow.AddMinutes(-1),
                    new HashSet<string>(), owners)?.Id == cxA.Id,
                    "simultaneous new Codex conversations bind to the owning terminal process instead of the latest file");
                using var otherOpened = new FileStream(cxB.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                check(SessionFinder.ForProcess(CliProfile.Codex, cwd, cxHome, DateTime.UtcNow.AddMinutes(-1),
                    new HashSet<string>(), owners) is null,
                    "ambiguous process ownership never guesses a conversation");
            }
            File.SetLastWriteTimeUtc(cxB.Path, DateTime.UtcNow.AddMinutes(1));
            check(SessionFinder.Current(CliProfile.Codex, cwd, cxHome, DateTime.UtcNow.AddMinutes(-1), cxA.Id, new HashSet<string>())?.Id == cxA.Id,
                "a newer Codex conversation cannot replace a known tab identity");
            check(SessionFinder.Current(CliProfile.Codex, cwd, cxHome, DateTime.UtcNow, Guid.NewGuid().ToString(), new HashSet<string>()) is null &&
                  SessionFinder.Current(CliProfile.Codex, cwd, cxHome, DateTime.UtcNow.AddMinutes(2), null, new HashSet<string>()) is null,
                "missing known and empty new Codex tabs never fall back to unrelated older history");
            for (var i = 0; i < 401; i++)
            {
                var filler = Path.Combine(Path.GetDirectoryName(cxA.Path)!, $"rollout-unrelated-{i}.jsonl");
                File.WriteAllText(filler, "{}");
                File.SetLastWriteTimeUtc(filler, DateTime.UtcNow.AddMinutes(3));
            }
            check(SessionFinder.Current(CliProfile.Codex, cwd, cxHome, DateTime.UtcNow, cxA.Id, new HashSet<string>())?.Id == cxA.Id,
                "an exact saved Codex session is found beyond the recent 400-file discovery limit");

            var agHome = Path.Combine(root, "restore-ag");
            Directory.CreateDirectory(Path.Combine(agHome, "conversations"));
            var agIds = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
            foreach (var agId in agIds) File.WriteAllText(Path.Combine(agHome, "conversations", agId + ".db"), "fixture");
            File.WriteAllLines(Path.Combine(agHome, "history.jsonl"), agIds.Select((agId, i) => JsonSerializer.Serialize(new
            {
                conversationId = agId, workspace = cwd, display = "fixture " + i,
                timestamp = DateTimeOffset.UtcNow.AddSeconds(i).ToUnixTimeMilliseconds()
            })));
            check(AntigravitySessions.Current(cwd, DateTime.UtcNow, agIds[0], new HashSet<string>(), agHome)?.Id == agIds[0] &&
                  AntigravitySessions.Current(cwd, DateTime.UtcNow, Guid.NewGuid().ToString(), new HashSet<string>(), agHome) is null,
                "Antigravity keeps the exact known conversation and never replaces a missing session");
            check(AntigravitySessions.Current(cwd, DateTime.UtcNow, null, new HashSet<string>(), agHome) is null,
                "concurrent unassigned Antigravity histories remain ambiguous until process ownership is known");
            using (var opened = new FileStream(Path.Combine(agHome, "conversations", agIds[0] + ".db"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                check(SessionFinder.ForProcess(CliProfile.Antigravity, cwd, agHome, DateTime.UtcNow,
                    new HashSet<string>(), new HashSet<int> { Environment.ProcessId })?.Id == agIds[0],
                    "Antigravity maps an open native conversation file to the owning terminal");
        }
        finally
        {
            // Remove this fixture's junction itself, never the target or a recursive linked tree.
            if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
        }
    }
}
