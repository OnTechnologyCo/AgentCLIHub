using System.Diagnostics;
using System.Text.Json;
using AgentCLIHub;

static class AccountChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        var settings = new Settings();
        foreach (var cli in CliProfile.All)
        {
            check(Accounts.DefaultName(settings, cli) == "기본", cli.Display + " existing default name is preserved");
            check(Accounts.Rename(settings, cli, "", cli.Display + " 회사", out _), cli.Display + " default account can be renamed");
        }
        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        check(CliProfile.All.All(c => Accounts.DefaultName(restored, c) == c.Display + " 회사") && restored.Accounts.Count == 0,
            "default names survive settings roundtrip independently without adding accounts");
        restored.Accounts.Add(new Account { Id = "fixture", Backend = CliProfile.Antigravity.Backend, Name = "Personal" });
        check(!Accounts.Rename(restored, CliProfile.Antigravity, "", "Personal", out _) &&
              !Accounts.Rename(restored, CliProfile.Antigravity, "fixture", "Antigravity 회사", out _) &&
              !Accounts.Rename(restored, CliProfile.Codex, "fixture", "Wrong CLI", out _) &&
              !Accounts.Rename(restored, CliProfile.Codex, "", "   ", out _),
            "blank names, duplicate names and wrong-backend account edits are rejected");
        check(Accounts.Rename(restored, CliProfile.Codex, "", "Personal", out _) &&
              Accounts.DefaultName(restored, CliProfile.Claude) == "Claude Code 회사",
            "same display name across providers is allowed without changing another default");
        var history = JsonSerializer.Serialize(SessionStore.ForUi(restored,
            [new SessionEntry(CliProfile.Codex.Backend, Guid.NewGuid().ToString(), root, DateTime.Now, "fixture")]));
        check(history.Contains("Personal"), "history includes the renamed default account");
        check(CliProfile.Antigravity.SupportsAccounts && CliProfile.LoginCommandLine("agy", LoginKind.Antigravity) == "agy",
            "Antigravity uses the native interactive Google sign-in entrypoint");

        var profile = Path.Combine(root, "agy-account-a");
        var second = Path.Combine(root, "agy-account-b");
        var env = AntigravityAccounts.EnvironmentFor(profile);
        check(env["USERPROFILE"] == profile && env["HOME"] == profile && env["SSH_CONNECTION"] is { Length: > 0 } &&
              env["APPDATA"]!.StartsWith(profile) && env["LOCALAPPDATA"]!.StartsWith(profile) && env["GOOGLE_API_KEY"] is null,
            "extra Antigravity accounts isolate home/config and select native file-based OAuth storage");
        check(!AntigravityAccounts.State(profile).LoggedIn && !AntigravityAccounts.State(second).LoggedIn,
            "empty Antigravity profiles never inherit a default login");
        Directory.CreateDirectory(Path.GetDirectoryName(AntigravityAccounts.TokenFile(profile))!);
        // Synthetic OAuth file in the native wrapped format, never a real user credential.
        File.WriteAllText(AntigravityAccounts.TokenFile(profile), """
            {"Token":{"access_token":"fixture-access","refresh_token":"fixture-refresh"},"Email":"fixture@example.invalid"}
            """);
        check(AntigravityAccounts.State(profile) == (true, "fixture@example.invalid") && !AntigravityAccounts.State(second).LoggedIn,
            "Antigravity login availability is read only from the selected account's own OAuth file");
        File.WriteAllText(AntigravityAccounts.TokenFile(profile), "{\"access_token\":\"expired-fixture\"}");
        check(!AntigravityAccounts.State(profile).LoggedIn, "incomplete OAuth files cannot enable an Antigravity account");
        File.WriteAllText(AntigravityAccounts.TokenFile(profile), "{partial");
        check(!AntigravityAccounts.State(profile).LoggedIn, "partially written OAuth files do not crash account settings");
    }

    public static async Task NativeSmoke(string root, string executable, Action<bool, string> check)
    {
        var profile = Path.Combine(root, "agy-native-empty");
        Directory.CreateDirectory(profile);
        var log = Path.Combine(profile, "fixture.log");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = profile };
        foreach (var (key, value) in AntigravityAccounts.EnvironmentFor(profile))
            if (value is null) start.Environment.Remove(key); else start.Environment[key] = value;
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("AGENTCLIHUB_") || k.StartsWith("LLMTOOLS_")).ToArray())
            start.Environment.Remove(key);
        start.Environment.Remove("BROWSER");
        foreach (var arg in new[] { "--print", "/usage", "--print-timeout", "5s", "--log-file", log }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
        catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
        await Task.WhenAll(output, error);
        check(File.Exists(log) && File.ReadAllText(log).Contains("Using file-based token storage because SSH session detected"),
            "native agy selects file-based OAuth storage with the production account environment");
        check(Directory.Exists(Path.Combine(profile, ".gemini", "antigravity-cli")) && !AntigravityAccounts.State(profile).LoggedIn,
            "native unauthenticated agy writes only its isolated home and does not acquire the default login");
    }
}
