using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentCLIHub;

internal static class TransferChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        string Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }
        string Rows(params object[] rows) => string.Join("\n", rows.Select(r => JsonSerializer.Serialize(r))) + "\n";
        var cwd = Path.Combine(root, "transfer 한글 workspace");
        var home = Path.Combine(root, "transfer-ag-home");
        var id = Guid.NewGuid().ToString();
        var other = Guid.NewGuid().ToString();
        Write(Path.Combine(home, "conversations", id + ".db"), "native-placeholder");
        Write(Path.Combine(home, "conversations", other + ".pb"), "native-placeholder");
        var now = DateTimeOffset.UtcNow;
        Write(Path.Combine(home, "history.jsonl"), Rows(
            new { conversationId = id, workspace = cwd, display = "인계 요청", timestamp = now.ToUnixTimeMilliseconds() },
            new { conversationId = other, workspace = cwd, display = "다른 탭", timestamp = now.AddSeconds(1).ToUnixTimeMilliseconds() }));
        check(AntigravitySessions.Recent(home).Count == 2, "Antigravity history supports both current SQLite and older protobuf sessions");
        var sourcePath = Write(AntigravitySessions.TranscriptPath(home, id), Rows(
            new { step_index = 0, type = "USER_INPUT", source = "USER_EXPLICIT", content = "<USER_REQUEST>인계 요청</USER_REQUEST><ADDITIONAL_METADATA>do not transfer settings</ADDITIONAL_METADATA>" },
            new { step_index = 1, type = "PLANNER_RESPONSE", source = "MODEL", content = "진행 중 초안" },
            new { step_index = 1, type = "PLANNER_RESPONSE", source = "MODEL", content = "최종 답변", tool_calls = new[] { new { name = "read_file", args = new { path = "sample.txt" } } } },
            new { step_index = 2, type = "VIEW_FILE", source = "MODEL", status = "ERROR", content = "파일 없음" },
            new { step_index = 3, type = "SYSTEM_MESSAGE", source = "SYSTEM", content = "do not transfer settings" }) + "{partial");
        var originalBytes = File.ReadAllBytes(sourcePath);
        var transcript = TranscriptReader.Read(CliProfile.Antigravity, sourcePath);
        check(transcript.UserTurns == 1 && transcript.Title == "인계 요청" && transcript.SessionId == id,
            "Antigravity transcript reads user text and session identity without injected metadata");
        check(transcript.Items.Count == 4 && transcript.Items.Any(i => i.Kind == ItemKind.ToolCall && i.Tool == "read_file") &&
            transcript.Items.Any(i => i.Kind == ItemKind.ToolResult && i.Error), "Antigravity transfer retains answers, tool calls and failed tool results");
        check(transcript.Items.All(i => !i.Text.Contains("진행 중 초안") && !i.Text.Contains("do not transfer")),
            "Antigravity parser keeps the latest step revision and excludes system settings");
        var current = AntigravitySessions.Current(cwd, now.UtcDateTime, id, new HashSet<string> { other }, home);
        check(current?.Id == id, "Antigravity transfer uses its known session instead of another tab in the same folder");
        check(AntigravitySessions.Current(cwd, now.AddMinutes(1).UtcDateTime, null, new HashSet<string>(), home) is null,
            "a new Antigravity tab never transfers an older conversation from the workspace");
        var missing = false;
        try { AntigravityTransfer.Read(Path.Combine(root, "missing-transcript.jsonl")); } catch (IOException) { missing = true; }
        check(missing, "missing Antigravity text transcript fails instead of silently creating an empty conversation");

        var handoff = AntigravityTransfer.Write(transcript, cwd, "Antigravity test");
        var restored = AntigravityTransfer.ReadHandoff(handoff);
        check(restored.UserTurns == 1 && restored.Items.Any(i => i.Text.Contains("최종 답변")) &&
            File.ReadAllText(handoff).Contains("\"Kind\": \"User\""), "handoff JSON preserves conversation and explicit speaker roles");
        var splitter = typeof(CliLocator).GetMethod("SplitCommandLine", BindingFlags.NonPublic | BindingFlags.Static)!;
        var line = CliProfile.Antigravity.BuildCommandLine("agy", new Settings { AntigravityModel = "test-model" }, ResumeMode.Handoff(handoff), cwd);
        var args = (string[])splitter.Invoke(null, [line])!;
        check(args[1] == "--prompt-interactive" && args[2] == AntigravityTransfer.Prompt(handoff) &&
            args.Contains("test-model") && !args.Contains("--continue") && !args.Contains("--conversation") && !args[2].Contains('\n'),
            "Antigravity handoff opens a fresh interactive session with one intact Korean prompt and selected model");
        var roundtripPath = Write(AntigravitySessions.TranscriptPath(home, other), Rows(
            new { step_index = 0, type = "USER_INPUT", source = "USER_EXPLICIT", content = "<USER_REQUEST>" + AntigravityTransfer.Prompt(handoff) + "</USER_REQUEST>" },
            new { step_index = 1, type = "PLANNER_RESPONSE", source = "MODEL", content = "인계 확인" },
            new { step_index = 2, type = "USER_INPUT", source = "USER_EXPLICIT", content = "다음 요청" }));
        var roundtrip = AntigravityTransfer.Read(roundtripPath);
        check(roundtrip.UserTurns == 2 && roundtrip.Items.Any(i => i.Text == "최종 답변") &&
            roundtrip.Items.Any(i => i.Text == "다음 요청"), "switching back out of Antigravity restores imported history plus new conversation");
        check(AntigravityTransfer.FindSession(cwd, handoff, home)?.Id == other,
            "restored handoff tab resumes the session containing its own import file");
        File.WriteAllText(Path.Combine(home, "history.jsonl"), "");
        check(AntigravityTransfer.FindSession(cwd, handoff, home)?.Id == other,
            "handoff session remains discoverable after another tab replaces the workspace cache");
        var savedTab = JsonSerializer.Deserialize<TabState>(JsonSerializer.Serialize(new TabState { Backend = "antigravity_cli", Cwd = cwd, HandoffPath = handoff }));
        check(savedTab?.HandoffPath == handoff, "handoff identity survives saving and restoring tabs");
        var cx = TranscriptWriter.ToCodex(roundtrip, cwd, Path.Combine(root, "transfer-codex-home"), "transfer test");
        var cxRead = TranscriptReader.ReadCodex(cx.Path);
        check(cxRead.Items.Any(i => i.Text.Contains("인계 요청")) && cxRead.Items.Any(i => i.Text.Contains("다음 요청")),
            "Antigravity conversation converts to a resumable Codex transcript");
        // ToClaude deliberately writes to the normal Claude home. Use its serializer in an isolated home.
        var oldClaudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(root, "transfer-claude-home"));
            var cc = TranscriptWriter.ToClaude(roundtrip, cwd, "transfer test");
            var ccRead = TranscriptReader.ReadClaude(cc.Path);
            check(ccRead.Items.Any(i => i.Text.Contains("인계 요청")) && ccRead.Items.Any(i => i.Text.Contains("다음 요청")),
                "Antigravity conversation converts to a resumable Claude transcript");
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", oldClaudeHome); }
        check(File.ReadAllBytes(sourcePath).SequenceEqual(originalBytes), "conversation transfer leaves the original Antigravity transcript unchanged");
    }
}
