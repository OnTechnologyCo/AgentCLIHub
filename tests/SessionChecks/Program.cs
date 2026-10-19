using System.Reflection;
using System.Text.Json;
using AgentCLIHub;

var root = Path.Combine(Path.GetTempPath(), "agentclihub-sessions-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var savedHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
    passed++;
}
try
{
    if (args.Length == 7 && args[0] == "--codex-owners")
    {
        for (var i = 3; i < 7; i += 2)
        {
            using var process = System.Diagnostics.Process.GetProcessById(int.Parse(args[i]));
            var started = process.StartTime.ToUniversalTime();
            var session = SessionFinder.ForProcess(CliProfile.Codex, args[2], args[1], started,
                new HashSet<string>(), NativeFileOwners.ProcessTree(process.Id, started));
            Check(session?.Id == args[i + 1], "real concurrent Codex process owns its exact session " + args[i + 1]);
        }
        return;
    }
    if (args.Contains("--redirection-smoke"))
    {
        RestoreChecks.Run(root, Check, enforceGuard: true);
        Console.WriteLine($"{passed} guarded restore checks passed.");
        return;
    }
    RestoreChecks.Run(root, Check, enforceGuard: false);
    AccountChecks.Run(root, Check);
    TransferMatrixChecks.Run(root, Check);
    if (args.Length == 2 && args[0] == "--agy-smoke") await AccountChecks.NativeSmoke(root, args[1], Check);
    var main = Path.Combine(root, "default-home");
    var sourceHome = Path.Combine(root, "source-home");
    var targetHome = Path.Combine(root, "target-home");
    var cwd = Path.Combine(root, "한글 workspace");
    Directory.CreateDirectory(cwd);
    Directory.CreateDirectory(targetHome);
    Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", main);
    var transcript = new Transcript();
    transcript.Items.Add(new TranscriptItem(ItemKind.User, "fixture question"));
    transcript.Items.Add(new TranscriptItem(ItemKind.Assistant, "fixture answer"));
    var (id, path) = TranscriptWriter.ToClaude(transcript, cwd, "fixture import", sourceHome);
    var original = File.ReadAllBytes(path);
    var source = SessionFinder.FindClaude(id, sourceHome)!;
    Check(source.Path == path && SessionFinder.FindClaude(id, main) is null && SessionFinder.FindClaude(id, targetHome) is null,
        "private Claude account transcript is found in its own home only");
    var read = TranscriptReader.ReadClaude(path);
    var (mode, _) = ConversationTransfer.Prepare(CliProfile.Claude, source, read, cwd, CliProfile.Claude, targetHome, "fixture account");
    Check(mode.SessionId == id && mode.TranscriptPath == path && !mode.Fresh,
        "Claude account transfer keeps the exact UUID and absolute transcript path");
    var line = CliProfile.Claude.BuildCommandLine("claude", new Settings(), mode, cwd, claudeHome: targetHome);
    var split = typeof(CliLocator).GetMethod("SplitCommandLine", BindingFlags.NonPublic | BindingFlags.Static)!;
    var commandArgs = (string[])split.Invoke(null, [line])!;
    Check(commandArgs[Array.IndexOf(commandArgs, "--resume") + 1] == path,
        "Claude resumes the quoted source file even when target history is separate");
    var entries = SessionStore.Claude(sourceHome, "source-account").ToList();
    Check(entries.Count == 1 && entries[0].Id == id && entries[0].Account == "source-account" && entries[0].Cwd == cwd,
        "history entries retain their Claude source account and workspace");
    var agentId = Guid.NewGuid().ToString();
    var nested = Path.Combine(Path.GetDirectoryName(path)!, id, "subagents", agentId + ".jsonl");
    Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
    File.WriteAllBytes(nested, original);
    Check(SessionStore.Claude(sourceHome).Count() == 1 && SessionFinder.FindClaude(agentId, sourceHome) is null,
        "subagent transcripts never appear as resumable top-level conversations");
    Check(SessionFinder.FindClaude("../escape", sourceHome) is null && SessionFinder.FindClaude(Guid.NewGuid().ToString(), sourceHome) is null,
        "missing or invalid session IDs never select a different conversation");
    var current = SessionFinder.Current(CliProfile.Claude, cwd, root, DateTime.UtcNow, id, new HashSet<string>(), sourceHome);
    Check(current?.Path == path, "open-tab transfer searches the active Claude account home");
    Check(SessionFinder.Current(CliProfile.Claude, cwd, root, DateTime.UtcNow.AddMinutes(1), Guid.NewGuid().ToString(), new HashSet<string>(), sourceHome) is null,
        "a missing known session never falls back to an older conversation in the same workspace");
    // Only fixture paths inside our new temporary directory are moved.
    var custom = Path.Combine(sourceHome, "projects", "custom-project-name");
    Directory.CreateDirectory(custom);
    var moved = Path.Combine(custom, id + ".jsonl");
    File.Move(path, moved);
    var found = SessionFinder.FindClaude(id, sourceHome)!;
    Check(found.Path == moved && SessionFinder.Current(CliProfile.Claude, cwd, root, DateTime.UtcNow, id, new HashSet<string>(), sourceHome)?.Path == moved,
        "known sessions survive custom project directory names and renamed workspaces");
    var (converted, _) = ConversationTransfer.Prepare(CliProfile.Codex, null, transcript, cwd, CliProfile.Claude, targetHome, "fixture Codex");
    Check(converted.SessionId != id && converted.TranscriptPath!.StartsWith(targetHome + Path.DirectorySeparatorChar) &&
        File.Exists(converted.TranscriptPath), "cross-CLI import is written into the selected Claude account home");
    Check(TranscriptReader.ReadClaude(converted.TranscriptPath!).Items.Any(i => i.Text.Contains("fixture question")),
        "converted history retains the previous conversation");
    var (codex, _) = ConversationTransfer.Prepare(CliProfile.Claude, found, read, cwd, CliProfile.Codex, Path.Combine(root, "codex-home"), "fixture Claude");
    Check(codex.SessionId != id && codex.TranscriptPath is null,
        "Codex transfers still resume by UUID instead of a Claude file path");
    Check(File.ReadAllBytes(moved).SequenceEqual(original) && !File.Exists(Path.Combine(targetHome, ".credentials.json")),
        "transfers preserve source bytes and never create or copy login credentials");
    var missingFailed = false;
    try { ConversationTransfer.Prepare(CliProfile.Claude, source, read, cwd, CliProfile.Claude, targetHome, "fixture"); }
    catch (FileNotFoundException) { missingFailed = true; }
    Check(missingFailed, "a vanished source file fails before replacing an open terminal");
    Console.WriteLine($"{passed} session checks passed.");
}
finally
{
    Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", savedHome);
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(root).StartsWith("agentclihub-sessions-", StringComparison.Ordinal)) Directory.Delete(root, true);
}
