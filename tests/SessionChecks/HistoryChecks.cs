using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AgentCLIHub;

internal static class HistoryChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        // An absolute fixture ID keeps even the encrypted synthetic token under this test's temporary root.
        var tokenProfile = Path.Combine(root, "token-account");
        var token = "sk-ant-fixture-" + new string('x', 40);
        Accounts.SaveClaudeToken(tokenProfile, token);
        using (var host = new TerminalHost("fixture", root, CliProfile.Claude, tokenProfile, _ => { }, (_, _) => { }))
        {
            var env = new Dictionary<string, string?>();
            object?[] arguments = [env, null];
            var configured = (bool)typeof(TerminalHost).GetMethod("AccountEnv", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host, arguments)!;
            check(configured && env["CLAUDE_CONFIG_DIR"] == tokenProfile && env["CLAUDE_CODE_OAUTH_TOKEN"] == token && host.ClaudeHistoryHome == tokenProfile,
                "token accounts keep their native history in their own profile while retaining token authentication");
        }
        var main = Path.Combine(root, "history-main");
        var alpha = Path.Combine(root, "history-alpha");
        var beta = Path.Combine(root, "history-beta");
        var own = Path.Combine(root, "history-private");
        var project = Path.Combine(main, "projects", "workspace");
        var cwd = Path.Combine(root, "history-workspace");
        Directory.CreateDirectory(project); Directory.CreateDirectory(cwd);
        foreach (var profile in new[] { alpha, beta, own }) Directory.CreateDirectory(profile);
        void Link(string profile)
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", Path.Combine(profile, "projects"), Path.Combine(main, "projects") }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("History junction fixture failed");
        }
        void Use(string profile, string id, string title)
        {
            File.AppendAllText(Path.Combine(profile, "history.jsonl"), JsonSerializer.Serialize(new {
                sessionId = id, project = cwd, display = title, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }) + "\n");
        }
        string Session(string folder, string title, int age, bool large = false)
        {
            Directory.CreateDirectory(folder);
            var id = Guid.NewGuid().ToString(); var path = Path.Combine(folder, id + ".jsonl");
            var prefix = large ? JsonSerializer.Serialize(new { type = "file-history-snapshot", data = new string('x', 140000) }) + "\n" : "";
            File.WriteAllText(path, prefix + JsonSerializer.Serialize(new { type = "user", cwd, message = new { content = title } }) + "\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-age)); return id;
        }
        Link(alpha); Link(beta);
        try
        {
            for (var i = 0; i < 130; i++) Use(main, Session(project, "default " + i, i), "default");
            var a = Session(project, "Alpha conversation", 200); Use(alpha, a, "Alpha conversation");
            var b = Session(project, "Beta conversation", 201); Use(beta, b, "Beta conversation");
            var large = Session(project, "late metadata", 1, large: true); Use(beta, large, "Recovered recent title");
            var unknown = Session(project, "Shared without account evidence", 202);
            var saved = Session(project, "Saved open tab", 203);
            var privateId = Session(Path.Combine(own, "projects", "private"), "Private without index", 204);
            var subagent = Session(Path.Combine(project, a, "subagents"), "not a conversation", 0);
            Use(alpha, subagent, "nested agent"); Use(beta, Guid.NewGuid().ToString(), "missing file");
            File.AppendAllText(Path.Combine(beta, "history.jsonl"), "{partial\n{\"sessionId\":4}\n");
            Use(beta, b, "Beta conversation");
            var homes = new[] { (main, ""), (alpha, "alpha"), (beta, "beta"), (own, "private") };
            var tabs = new[] { new TabState { Backend = "claude_code", Account = "alpha", SessionId = saved, Cwd = cwd } };
            var before = File.ReadAllBytes(Path.Combine(project, a + ".jsonl"));
            var entries = SessionStore.ClaudeAccounts(homes, tabs, 500);
            check(entries.Count(e => e.Account == "" && !e.Shared) == 130 &&
                entries.Single(e => e.Id == a).Account == "alpha" && entries.Single(e => e.Id == b).Account == "beta",
                "shared projects retain native account attribution instead of collapsing into default");
            check(entries.Single(e => e.Id == saved).Account == "alpha", "saved open tabs supplement missing account input history");
            check(entries.Single(e => e.Id == unknown).Shared && entries.Single(e => e.Id == privateId).Account == "private",
                "unattributed shared history stays visible without claiming the default account; private history retains its account");
            var recovered = entries.Single(e => e.Id == large);
            check(recovered.Cwd == cwd && recovered.Title == "Recovered recent title" && recovered.Account == "beta",
                "native input metadata recovers a recent session whose header exceeds 128 KiB");
            check(entries.All(e => e.Id != subagent) && entries.Count == 136,
                "malformed history rows, missing files and nested subagents do not hide valid conversations");
            var limited = SessionStore.ClaudeAccounts(homes, tabs, 2);
            check(limited.Count(e => e.Account == "" && !e.Shared) == 2 && limited.Count(e => e.Account == "alpha") == 2 &&
                limited.Count(e => e.Account == "beta") == 2 && limited.Any(e => e.Id == b),
                "limits apply after account attribution; 130 newer default sessions cannot hide another account");
            Use(beta, a, "Continued on Beta");
            entries = SessionStore.ClaudeAccounts(homes, tabs, 500);
            check(entries.Where(e => e.Id == a).Select(e => e.Account).Order().SequenceEqual(new[] { "alpha", "beta" }),
                "a conversation used by two accounts is available under both without duplicate rows within an account");
            var row = entries.First(e => e.Id == a && e.Account == "beta");
            check(SessionFinder.Find(row)?.Path == Path.Combine(project, a + ".jsonl"),
                "account selection resolves the exact physical transcript for shared history");
            check(File.ReadAllBytes(Path.Combine(project, a + ".jsonl")).SequenceEqual(before), "history collection never edits conversation files");
        }
        finally { Directory.Delete(Path.Combine(alpha, "projects")); Directory.Delete(Path.Combine(beta, "projects")); }
    }
}
