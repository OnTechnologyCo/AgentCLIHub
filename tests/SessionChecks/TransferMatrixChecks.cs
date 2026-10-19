using System.Text.Json;
using AgentCLIHub;

static class TransferMatrixChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        var cwd = Path.Combine(root, "matrix-workspace");
        Directory.CreateDirectory(cwd);
        var transcript = new Transcript();
        transcript.Items.AddRange([
            new(ItemKind.User, "MATRIX_QUESTION"), new(ItemKind.Assistant, "MATRIX_ANSWER"),
            new(ItemKind.ToolCall, "MATRIX_ARGUMENT", "read_file"), new(ItemKind.ToolResult, "MATRIX_RESULT")]);
        var cc = TranscriptWriter.ToClaude(transcript, cwd, "fixture", Path.Combine(root, "matrix-claude-source"));
        var cx = TranscriptWriter.ToCodex(transcript, cwd, Path.Combine(root, "matrix-codex-source"), "fixture");
        var agHome = Path.Combine(root, "matrix-ag-source");
        var agId = Guid.NewGuid().ToString();
        var agPath = AntigravitySessions.TranscriptPath(agHome, agId);
        Directory.CreateDirectory(Path.GetDirectoryName(agPath)!);
        File.WriteAllLines(agPath, new object[] {
            new { step_index = 0, type = "USER_INPUT", source = "USER_EXPLICIT", content = "<USER_REQUEST>MATRIX_QUESTION</USER_REQUEST>" },
            new { step_index = 1, type = "PLANNER_RESPONSE", source = "MODEL", content = "MATRIX_ANSWER",
                tool_calls = new[] { new { name = "read_file", args = new { path = "MATRIX_ARGUMENT" } } } },
            new { step_index = 2, type = "VIEW_FILE", source = "MODEL", content = "MATRIX_RESULT" }
        }.Select(x => JsonSerializer.Serialize(x)));
        Directory.CreateDirectory(Path.Combine(agHome, "conversations"));
        File.WriteAllText(Path.Combine(agHome, "conversations", agId + ".db"), "fixture-placeholder");
        File.WriteAllText(Path.Combine(agHome, "history.jsonl"), JsonSerializer.Serialize(new {
            conversationId = agId, workspace = cwd, display = "MATRIX_QUESTION", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
        check(SessionFinder.Current(CliProfile.Antigravity, cwd, root, DateTime.MaxValue, agId, new HashSet<string>(),
                antigravityHome: agHome)?.Path == agPath &&
              SessionFinder.Current(CliProfile.Antigravity, cwd, root, DateTime.MaxValue, agId, new HashSet<string>(),
                antigravityHome: Path.Combine(root, "different-ag-account")) is null,
            "Antigravity resume discovery uses the selected account's history only");
        var paths = new Dictionary<CliProfile, string> { [CliProfile.Claude] = cc.Path, [CliProfile.Codex] = cx.Path, [CliProfile.Antigravity] = agPath };
        var bytes = paths.ToDictionary(p => p.Key, p => File.ReadAllBytes(p.Value));
        foreach (var source in CliProfile.All)
        foreach (var target in CliProfile.All)
        {
            var read = TranscriptReader.Read(source, paths[source]);
            var sourceRef = new SessionRef(source, read.SessionId, paths[source], DateTime.UtcNow);
            var targetHome = Path.Combine(root, "matrix-target-" + source.Backend + "-" + target.Backend);
            var (mode, _) = ConversationTransfer.Prepare(source, sourceRef, read, cwd, target, targetHome, "matrix fixture");
            Transcript received;
            if (target == CliProfile.Antigravity) received = AntigravityTransfer.ReadHandoff(mode.HandoffPath!);
            else
            {
                var path = target == CliProfile.Claude ? mode.TranscriptPath!
                    : Directory.EnumerateFiles(targetHome, "*.jsonl", SearchOption.AllDirectories).Single();
                if (source != target || target != CliProfile.Claude)
                    check(Path.GetFullPath(path).StartsWith(targetHome + Path.DirectorySeparatorChar),
                        source.Display + " -> " + target.Display + " writes to the destination account only");
                received = TranscriptReader.Read(target, path);
            }
            check(new[] { "MATRIX_QUESTION", "MATRIX_ANSWER", "MATRIX_ARGUMENT", "MATRIX_RESULT" }
                    .All(marker => received.Items.Any(item => item.Text.Contains(marker))),
                source.Display + " -> " + target.Display + " preserves the request, answer and tool history");
        }
        check(paths.All(p => File.ReadAllBytes(p.Value).SequenceEqual(bytes[p.Key])), "all nine CLI/account transfer combinations preserve original source files");
    }
}
