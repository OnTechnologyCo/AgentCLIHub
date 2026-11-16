using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCLIHub;

// 텔레그램 원격 검사. 실제 텔레그램·실제 설정 파일·실제 CLI 를 쓰지 않는다 —
// Bot API 는 가짜 HTTP 처리기로, 앱은 가짜 IRemoteHost 로 바꿔 끼운다.
var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL " + message);
    Console.WriteLine("PASS " + message);
    passed++;
}

var root = Path.Combine(Path.GetTempPath(), "agentclihub-telegram-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    // ── 명령 · 빠른 메뉴 바 ──
    Check(TelegramText.Command("/use@MyHubBot 2") == ("use", "2"), "command strips @bot suffix");
    Check(TelegramText.Command("안녕") == ("", "안녕"), "plain text is not a command");
    Check(TelegramText.Command("//help").Command == "" && TelegramText.Unescape("//help") == "/help", "double slash goes to the CLI");
    Check(TelegramText.NewArgs(@"D:\작업 폴더 codex") == (@"D:\작업 폴더", CliProfile.Codex.Backend), "new keeps spaces in folder and reads CLI at end");
    Check(TelegramText.NewArgs("\"D:\\x\"") == (@"D:\x", ""), "new without CLI leaves backend empty");
    Check(TelegramText.NewArgs("claude").Backend == CliProfile.Claude.Backend, "new with only a CLI name");

    foreach (var label in TelegramText.QuickBarRows(false).SelectMany(r => r).Concat(TelegramText.QuickBarRows(true).SelectMany(r => r)))
        Check(TelegramText.QuickBarAction(label) is { Length: > 0 }, $"quick bar button '{label}' maps to an action");
    Check(TelegramText.QuickBarAction("일반 질문입니다") is null, "ordinary prompt text is not treated as quick bar");

    var rk = TelegramApi.ReplyKeyboard(TelegramText.QuickBarRows(false), persistent: true, placeholder: "질문…");
    Check(rk["keyboard"] is JsonArray kbRows && kbRows.Count == 2 && rk["resize_keyboard"]?.GetValue<bool>() == true && rk["is_persistent"]?.GetValue<bool>() == true,
          "reply keyboard markup shape");
    Check(TelegramApi.RemoveReplyKeyboard()["remove_keyboard"]?.GetValue<bool>() == true, "remove reply keyboard markup shape");

    // ── 자르기 ──
    var longText = string.Join("\n", Enumerable.Range(0, 900).Select(i => $"줄 {i} " + new string('가', 15)));
    var parts = TelegramText.Split(longText, 1000);
    Check(parts.Count > 1 && parts.All(p => p.Length <= 1000), "split respects the limit");
    Check(string.Join("\n", parts) == longText, "split on line boundaries keeps all text");
    var emoji = string.Concat(Enumerable.Repeat("😀", 700));
    Check(TelegramText.Split(emoji, 999).All(p => p.Length > 0 && !char.IsHighSurrogate(p[^1])), "split never cuts a surrogate pair");
    Check(TelegramText.Clip("😀😀😀", 2) == "…" || !char.IsHighSurrogate(TelegramText.Clip("😀😀😀", 3)[^2]), "clip keeps surrogate pairs");

    // ── 화면 ──
    var claudePrompt = """
        ● Bash(git push)
        ╭──────────────────────────────────────────────╮
        │ Bash command                                 │
        │   git push origin main                       │
        │ Do you want to proceed?                      │
        │ ❯ 1. Yes                                     │
        │   2. Yes, and don't ask again for git push   │
        │   3. No, and tell Claude what to do (esc)    │
        ╰──────────────────────────────────────────────╯
        """;
    var opts = TelegramText.PromptOptions(claudePrompt);
    Check(opts.Count == 3 && opts[0].Selected && opts[2].Label.StartsWith("No, and tell Claude"), "claude approval box is parsed");
    Check(TelegramText.MoveKeys(opts, 3) == "\u001b[B\u001b[B" && TelegramText.MoveKeys(opts, 1) == "", "arrow keys move from the cursor");

    var codexPrompt = """
        Would you like to run the following command?
        $ git status
          1. Yes, proceed (y)
        › 2. Yes, and don't ask again for this command (a)
          3. No, and tell Codex what to do differently (esc)
        Press enter to confirm or esc to cancel
        """;
    var copts = TelegramText.PromptOptions(codexPrompt);
    Check(copts.Count == 3 && copts[1].Selected, "codex approval list is parsed");
    Check(TelegramText.MoveKeys(copts, 1) == "\u001b[A", "arrow up when target is above cursor");

    var answerList = """
        설치 순서는 다음과 같습니다.
        1. 저장소를 받습니다
        2. 빌드합니다
        3. 실행합니다
        >
        ? for shortcuts
        """;
    Check(TelegramText.PromptOptions(answerList).Count == 0, "numbered answer text without a cursor is not a prompt");
    Check(TelegramText.IsWorking("✻ Thinking… (12s · ↑ 1.2k tokens · esc to interrupt)"), "claude working line");
    Check(TelegramText.IsWorking("• Working (4s • esc to interrupt)"), "codex working line");
    Check(!TelegramText.IsWorking(answerList), "idle screen is not working");
    Check(TelegramText.PromptSignature(claudePrompt, opts) != TelegramText.PromptSignature(claudePrompt.Replace("git push origin main", "rm -rf build"), opts),
          "prompt signature includes the question, not only the options");
    var excerpt = TelegramText.ScreenExcerpt("\n\n\nA\n\n\n\nB\n   \n", 40);
    Check(excerpt == "A\n\nB", "screen excerpt trims and folds blank lines");

    // ── 기록 ──
    var items = new List<TranscriptItem>
    {
        new(ItemKind.User, "이전 질문"),
        new(ItemKind.Assistant, "이전 답"),
        new(ItemKind.User, "빌드가   왜 실패하지?"),
        new(ItemKind.Assistant, "확인해 보겠습니다."),
        new(ItemKind.ToolCall, "description: Run the build\ncommand: dotnet build", "Bash"),
        new(ItemKind.ToolResult, "error CS0103"),
        new(ItemKind.Assistant, "using System.IO 가 빠졌습니다."),
    };
    var turn = TelegramText.TurnItems(items, "빌드가 왜 실패하지?", 2);
    Check(turn is { Count: 4 }, "turn starts after the matching user message");
    Check(TelegramText.FinalAnswer(turn!) == "using System.IO 가 빠졌습니다.", "final answer is the text after the last tool");
    Check(TelegramText.FinalAnswer([new(ItemKind.Assistant, "A"), new(ItemKind.Assistant, "B")]) == "A\n\nB", "answer without tools keeps all text");
    Check(TelegramText.TurnItems(items, "다른 말", 6) is null, "no user message after baseline means not written yet");
    Check(TelegramText.TurnItems(items, "[Pasted text #1]", 1)?.Count == 4, "falls back to the last user message after baseline");
    Check(TelegramText.ToolLine(items[4]) == "• Bash: Run the build", "tool line prefers the description");

    // ── 페어링·토큰 ──
    var code = TelegramText.NewPairingCode();
    Check(System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z2-9]{4}-[A-Z2-9]{4}$"), "pairing code shape: " + code);
    Check(TelegramText.SameCode(code, code.Replace("-", "").ToLowerInvariant()), "pairing code ignores case and dash");
    Check(!TelegramText.SameCode(code, "AAAA-AAAA") && !TelegramText.SameCode("", ""), "wrong or empty code fails");
    Check(TelegramApi.LooksLikeToken("123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw") && !TelegramApi.LooksLikeToken("hello"), "token shape");
    Check(TelegramApi.Mask("123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw") == "123456789:••••••", "token mask keeps only the bot id");
    Check(TelegramApi.Html("<a & b>") == "&lt;a &amp; b&gt;", "html escape");

    // ── 설정 ──
    var legacy = JsonSerializer.Deserialize<Settings>("{\"UiLanguage\":\"ko\"}")!;
    Check(legacy.Telegram is { Enabled: false, ShowQuickBar: true } && legacy.Telegram.Chats.Count == 0, "old settings get Telegram off and quick bar on by default");
    var round = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings
    {
        Telegram = new TelegramSettings { Enabled = true, ShowQuickBar = false, Chats = [new TelegramChat { Id = 42, Name = "@me" }] },
    }))!;
    Check(round.Telegram.Enabled && !round.Telegram.ShowQuickBar && round.Telegram.Chats.Single().Id == 42, "Telegram settings round-trip");
    Check(!JsonSerializer.Serialize(round).Contains("Token", StringComparison.OrdinalIgnoreCase), "settings never hold the bot token");

    // ── 끝에서 끝까지(가짜 텔레그램 + 가짜 앱) ──
    await FileTransferChecks.RunAsync(root, Check);
    await EndToEnd();

    Console.WriteLine($"All {passed} Telegram checks passed.");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
    Console.WriteLine(ex.StackTrace);
    return 1;
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

async Task EndToEnd()
{
    const long Me = 777, Stranger = 999;
    var tg = new FakeTelegram();
    var transcript = Path.Combine(root, "session.jsonl");
    var newDir = Path.Combine(root, "new-work");
    Directory.CreateDirectory(newDir);
    File.WriteAllText(transcript, ClaudeLine("user", "u1", null, "\"이전 질문\"") + ClaudeLine("assistant", "a1", "u1", "[{\"type\":\"text\",\"text\":\"이전 답\"}]"));
    var app = new FakeHost(root, newDir, transcript);
    using var remote = new TelegramRemote("123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw", app, tg)
    {
        RemoteKeyRefreshDelayMs = 10,
    };
    remote.Start();

    // 1) 모르는 대화 → 안내만, 탭에는 아무것도 안 간다.
    tg.Message(Stranger, "rm -rf /");
    await Until(() => tg.SentTo(Stranger).Any(t => t.Contains("/pair")), "stranger gets a pairing hint");
    Check(app.Sent.Count == 0, "stranger text never reaches a tab");

    // 2) 틀린 코드 → 거절, 맞는 코드 → 허용 + 하단 고정 빠른 메뉴 바 + 인라인 대시보드 전송.
    var code = remote.NewPairingCode();
    tg.Message(Me, "/pair ZZZZ-ZZZZ");
    await Until(() => tg.SentTo(Me).Any(t => t.Contains("맞지 않거나") || t.Contains("wrong")), "wrong code is refused");
    Check(!app.IsAllowedChat(Me), "wrong code does not pair");
    tg.Message(Me, "/pair " + code.ToLowerInvariant());
    await Until(() => app.IsAllowedChat(Me) && tg.Markups.Any(m => m.Contains("\"keyboard\"")) && tg.Markups.Any(m => m.Contains("m:tabs")),
                "right code pairs the chat and sends both quick bar and dashboard");
    Check(remote.PairingCode is null, "pairing code is single-use");

    // 3) 하단 고정 빠른 메뉴 바 단추 클릭(한글·영문 모두) → 절대 CLI 프롬프트로 들어가지 않고 메뉴가 열린다.
    tg.Message(Me, "📊 사용량");
    await Until(() => tg.SentTo(Me).Any(t => t.Contains("계정 한도 사용량") && t.Contains("잔여 62%")), "quick bar usage button opens quota report");
    tg.Message(Me, "📋 Menu");
    await Until(() => tg.SentTo(Me).Count(t => t.Contains("원격 메뉴")) >= 2, "English quick bar label also routes cleanly in Korean mode");
    Check(app.Sent.Count == 0, "quick bar button presses never leak into CLI prompts");

    // 4) 빠른 메뉴 바 숨기기/켜기 토글(/bar).
    tg.Message(Me, "/bar off");
    await Until(() => !app.ShowQuickBar && tg.Markups.Any(m => m.Contains("\"remove_keyboard\":true")), "bar off hides persistent reply keyboard");
    tg.Message(Me, "/bar on");
    await Until(() => app.ShowQuickBar && tg.Markups.Count(m => m.Contains("\"keyboard\"")) >= 2, "bar on restores persistent reply keyboard");

    // 5) 질문 → 탭으로 들어가고, 도구 진행 → 최종 답.
    app.OnSend = text =>
    {
        File.AppendAllText(transcript,
            ClaudeLine("user", "u2", "a1", JsonSerializer.Serialize(text)) +
            ClaudeLine("assistant", "a2", "u2", "[{\"type\":\"text\",\"text\":\"확인해 보겠습니다.\"},{\"type\":\"tool_use\",\"id\":\"tu1\",\"name\":\"Bash\",\"input\":{\"command\":\"dotnet build\",\"description\":\"Run the build\"}}]") +
            ClaudeLine("user", "u3", "a2", "[{\"type\":\"tool_result\",\"tool_use_id\":\"tu1\",\"content\":\"error CS0103\"}]") +
            ClaudeLine("assistant", "a3", "u3", "[{\"type\":\"text\",\"text\":\"using System.IO 가 빠졌습니다.\"}]"));
        app.Screen = "> \n? for shortcuts";
    };
    tg.Message(Me, "빌드가 왜 실패하지?");
    await Until(() => tg.SentTo(Me).Any(t => t.Contains("using System.IO 가 빠졌습니다.")), "final answer comes back", 20);
    Check(app.Sent.Single() == "빌드가 왜 실패하지?", "question reached the only open tab");
    Check(!tg.SentTo(Me).Any(t => t.Contains("확인해 보겠습니다.")), "intermediate text is not sent as the answer");
    Check(tg.Edits.Any(e => e.Contains("✅")), "progress message is closed as done");

    // 6) 승인 창 → 단추 → 화살표 + Enter → 끝.
    app.Keys.Clear();
    app.OnSend = _ =>
    {
        app.Screen = "Do you want to proceed?\n❯ 1. Yes\n  2. Yes, and don't ask again\n  3. No, and tell Claude what to do differently (esc)";
    };
    app.OnKeys = keys =>
    {
        if (keys != "\r") return;
        File.AppendAllText(transcript,
            ClaudeLine("user", "u4", "a3", "\"푸시해 줘\"") +
            ClaudeLine("assistant", "a4", "u4", "[{\"type\":\"text\",\"text\":\"푸시했습니다.\"}]"));
        app.Screen = "> \n? for shortcuts";
    };
    tg.Message(Me, "푸시해 줘");
    await Until(() => tg.Markups.Any(m => m.Contains("opt:t1:")), "approval prompt arrives with buttons", 20);
    var data = tg.Markups.Last(m => m.Contains("opt:t1:"));
    var second = JsonNode.Parse(data)!["inline_keyboard"]!.AsArray()[1]![0]!["callback_data"]!.GetValue<string>();
    tg.Callback(Me, second);
    await Until(() => tg.SentTo(Me).Any(t => t.Contains("푸시했습니다.")), "answer after choosing an option", 20);
    Check(app.Keys.Contains("\u001b[B") && app.Keys.Last() == "\r", "option 2 is chosen with one arrow down then Enter");
    app.OnKeys = null;

    // 7) 화면 리모컨(/screen) → Ctrl+C · 방향키 전송 후 제자리 화면 새로고침.
    app.Keys.Clear();
    app.Screen = "line 1\nline 2";
    tg.Message(Me, "🖥 화면");
    await Until(() => tg.Markups.Any(m => m.Contains("rc:cc:t1")), "screen message includes remote control buttons");
    app.Screen = "interrupted!";
    tg.Callback(Me, "rc:cc:t1", messageId: 1050);
    await Until(() => app.Keys.Contains("\u0003") && tg.Edits.Any(e => e.Contains("interrupted!")), "remote Ctrl+C sends \\x03 and refreshes screen in-place");

    // 8) 탭 관리: 이름 변경(인라인 대화식) · 재시작 · 다른 계정 전환.
    var sentBeforeRename = app.Sent.Count;
    tg.Callback(Me, "trnm:t1", messageId: 1051);
    await Until(() => tg.Edits.Any(e => e.Contains("새 탭 이름")), "rename prompt appears");
    tg.Message(Me, "백엔드 서버");
    await Until(() => app.RenamedTo == "백엔드 서버", "next text renames the tab instead of going to CLI");
    Check(app.Sent.Count == sentBeforeRename, "tab rename text does not go to CLI");

    tg.Callback(Me, "trst:t1", messageId: 1051);
    await Until(() => app.Restarted.Contains("t1"), "restart tab callback restarts CLI");

    tg.Callback(Me, "tsw:t1", messageId: 1052);
    await Until(() => tg.Edits.Any(e => e.Contains("어느 CLI·계정으로")), "switch menu lists transferable accounts");
    tg.Callback(Me, "tswi:t1:0", messageId: 1052);
    await Until(() => app.SwitchedTo == ("t1", CliProfile.Codex.Backend, "acc-codex"), "switch callback transfers tab to chosen CLI/account");

    // 9) 새 대화 열기(/new): 최근 폴더 → CLI → 다중 계정 고르기 → 새 탭 열기 + 닫기 확인.
    tg.Message(Me, "➕ 새 탭");
    await Until(() => tg.Markups.Any(m => m.Contains("nf:0")), "new tab shows recent folders");
    tg.Callback(Me, "nf:0", messageId: 1060);
    await Until(() => tg.Edits.Any(e => e.Contains("어느 CLI 로")), "folder selection asks for CLI");
    tg.Callback(Me, "nc:" + CliProfile.Claude.Backend, messageId: 1060);
    await Until(() => tg.Edits.Any(e => e.Contains("어느 계정으로")) && tg.EditMarkups.Any(m => m.Contains("na:1")), "CLI with multiple accounts asks for account");
    tg.Callback(Me, "na:1", messageId: 1060);
    await Until(() => app.OpenedTabs.Any(o => o.Folder == newDir && o.Backend == CliProfile.Claude.Backend && o.Account == "acc-sub"),
                "picking account opens new tab with that account");

    tg.Callback(Me, "tcls:t2", messageId: 1061);
    await Until(() => tg.EditMarkups.Any(m => m.Contains("tclok:t2")), "close tab asks for confirmation first");
    tg.Callback(Me, "tclok:t2", messageId: 1061);
    await Until(() => app.ClosedTabs.Contains("t2"), "confirming close closes the tab");

    // 10) 지난 대화 이어하기(/resume): 목록 조회 및 선택 이어하기.
    tg.Message(Me, "↻ 이어하기");
    await Until(() => tg.Markups.Any(m => m.Contains("rs:0")) && tg.SentTo(Me).Any(t => t.Contains("로그인 버그 수정")), "resume lists recent sessions");
    tg.Callback(Me, "rs:0", messageId: 1070);
    await Until(() => app.ResumedSessionId == "sess-123", "tapping session resumes it in a new tab");

    // 11) 예약 작업(/schedules): 목록 · 켜기/끄기 토글 · 지금 실행 · 최근 결과 조회.
    tg.Message(Me, "⏰ 예약");
    await Until(() => tg.Markups.Any(m => m.Contains("sc:v:0")) && tg.SentTo(Me).Any(t => t.Contains("앱테크 점검")), "schedules menu lists jobs");
    tg.Callback(Me, "sc:tog:0", messageId: 1080);
    await Until(() => !app.ScheduleEnabled, "schedule toggle disables enabled job");
    tg.Callback(Me, "sc:run:0", messageId: 1080);
    await Until(() => app.ScheduleRanNow.Contains("job-1"), "schedule run-now triggers immediate execution");
    tg.Callback(Me, "sc:ans:0", messageId: 1080);
    await Until(() => tg.SentTo(Me).Any(t => t.Contains("모든 점검 통과")), "schedule last answer is sent");

    // 12) 앱 설정·창 제어(/settings): 트레이 숨기기/열기 · 펫 토글 · CLI 기본 모델 변경.
    tg.Message(Me, "⚙ 설정");
    await Until(() => tg.Markups.Any(m => m.Contains("cf:win:hide")), "settings menu opens");
    tg.Callback(Me, "cf:win:hide", messageId: 1090);
    await Until(() => !app.WindowShown, "hide-to-tray hides app window");
    tg.Callback(Me, "cf:pet", messageId: 1090);
    await Until(() => app.PetEnabled, "pet toggle flips pet visibility");
    tg.Callback(Me, "cf:cli:cc", messageId: 1090);
    await Until(() => tg.EditMarkups.Any(m => m.Contains("cf:mdl:cc")), "CLI settings submenu opens");
    tg.Callback(Me, "cf:mdl:cc", messageId: 1090);
    await Until(() => tg.EditMarkups.Any(m => m.Contains("cf:set:1")), "CLI model picker lists models");
    tg.Callback(Me, "cf:set:1", messageId: 1090);
    await Until(() => app.ClaudeModel == "opus", "picking a model updates default CLI model");

    // 파일 받기·보내기: 실제 API 대신 바이트를 검사한다.
    var outputFile = Path.Combine(root, "전송 결과.txt");
    var outputBytes = Encoding.UTF8.GetBytes("한글 파일 내용\n");
    File.WriteAllBytes(outputFile, outputBytes);
    tg.Message(Me, "/use 1");
    var promptsBeforeFiles = app.Sent.Count;
    tg.Message(Me, "/file \"전송 결과.txt\"");
    await Until(() => tg.Documents.Count == 1, "relative /file sends a document to the requesting chat");
    Check(tg.Documents[0].Chat == Me && tg.Documents[0].Bytes.SequenceEqual(outputBytes), "outgoing document bytes match the PC file");
    tg.Message(Me, "/file " + outputFile);
    await Until(() => tg.Documents.Count == 2, "absolute /file accepts spaces without quotes");
    tg.Message(Me, "/file missing-file.txt");
    await Until(() => tg.SentTo(Me).Any(t => t.Contains("파일을 보내지 못했습니다")), "missing PC file reports failure in chat");
    Check(app.Sent.Count == promptsBeforeFiles, "/file commands never become CLI prompts");
    Check(await remote.SendFileAsync(Me, outputFile) > 0 && tg.Documents.Count == 3,
        "agent-facing file sender returns a completed Telegram message id");

    app.OnSend = null;
    app.Screen = "• Working (esc to interrupt)";
    foreach (var kind in new[] { "document", "photo", "video", "audio", "voice", "animation", "video_note" })
    {
        var count = app.Attachments.Count;
        tg.Message(Me, "/close", attachment: new JsonObject
        {
            [kind] = kind == "photo"
                ? new JsonArray(new JsonObject { ["file_id"] = "small" }, new JsonObject { ["file_id"] = "largest" })
                : new JsonObject { ["file_id"] = kind, ["file_name"] = "../../한글 첨부.bin", ["file_size"] = 4 },
        });
        await Until(() => app.Attachments.Count > count, kind + " attachment reaches the CLI");
        var received = app.Attachments.Last().Single();
        Check(Path.GetDirectoryName(received) == Path.Combine(root, ".agentclihub-attachments", "telegram") &&
            File.ReadAllBytes(received).SequenceEqual(FakeTelegram.DownloadBytes) && app.Sent.Last() == "/close",
            kind + " stays inside attachment folder and caption is not executed as a bot command");
    }
    Check(tg.DownloadIds.Contains("largest") && !tg.DownloadIds.Contains("small"), "photo uses the largest image");
    var voiceBefore = app.Attachments.Count;
    tg.Message(Me, "", attachment: new JsonObject
    {
        ["voice"] = new JsonObject { ["file_id"] = "voice-no-name", ["mime_type"] = "audio/mp4" },
    });
    await Until(() => app.Attachments.Count > voiceBefore, "attachment without caption or filename reaches CLI");
    Check(app.Attachments.Last().Single().EndsWith("voice.m4a") && app.Sent.Last() == "",
        "unnamed audio keeps MIME extension and empty caption");
    var filesBeforeOversize = app.Attachments.Count;
    tg.Message(Me, "large", attachment: new JsonObject { ["document"] = new JsonObject { ["file_id"] = "too-large", ["file_size"] = TelegramApi.MaxDownload + 1 } });
    await Until(() => tg.SentTo(Me).Any(t => t.Contains("파일이 20MB")), "oversized incoming file reports its limit");
    Check(app.Attachments.Count == filesBeforeOversize && !tg.DownloadIds.Contains("too-large"), "oversized metadata blocks download and CLI dispatch");

    // 13) 오래된 메시지(앱이 꺼져 있을 때 쌓인 것)는 실행하지 않는다.
    var before = app.Sent.Count;
    tg.Message(Me, "옛날 지시", DateTimeOffset.UtcNow.AddHours(-3));
    await Until(() => tg.SentTo(Me).Any(t => t.Contains("실행하지 않았습니다") || t.Contains("were not run")), "stale message is refused");
    Check(app.Sent.Count == before, "stale message never reaches a tab");
    tg.Message(Me, "/file " + outputFile, DateTimeOffset.UtcNow.AddHours(-3));

    // 14) 지운 대화는 바로 막힌다.
    lock (app.Allowed) app.Allowed.Clear();
    var seen = tg.SentTo(Me).Count;
    tg.Message(Me, "/tabs");
    await Until(() => tg.SentTo(Me).Count > seen, "revoked chat gets only the pairing hint");
    Check(tg.SentTo(Me).Skip(seen).All(t => t.Contains("/pair") && !t.Contains("Claude Code")), "revoked chat cannot list tabs");
    var wrongPairs = tg.SentTo(Me).Count(t => t.Contains("맞지 않거나"));
    tg.Message(Me, "/file " + outputFile);
    tg.Message(Stranger, "attachment", attachment: new JsonObject { ["document"] = new JsonObject { ["file_id"] = "stranger-file" } });
    tg.Message(Me, "/pair wrong");
    await Until(() => tg.SentTo(Me).Count(t => t.Contains("맞지 않거나")) > wrongPairs, "unpaired file updates processed");
    try { await remote.SendFileAsync(Me, outputFile); throw new Exception("revoked upload allowed"); }
    catch (UnauthorizedAccessException) { Check(tg.Documents.Count == 3 && !tg.DownloadIds.Contains("stranger-file"), "revoked and unpaired chats cannot transfer files"); }
}

static string ClaudeLine(string type, string uuid, string? parent, string contentJson) =>
    "{\"type\":\"" + type + "\",\"uuid\":\"" + uuid + "\",\"parentUuid\":" + (parent is null ? "null" : "\"" + parent + "\"") +
    ",\"message\":{\"role\":\"" + type + "\",\"content\":" + contentJson + "}}\n";

static async Task Until(Func<bool> condition, string what, int seconds = 10)
{
    var end = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < end)
    {
        if (condition()) { Console.WriteLine("PASS " + what); return; }
        await Task.Delay(50);
    }
    throw new Exception("FAIL (timeout) " + what);
}

/// <summary>Bot API 흉내. 모든 인라인 callback_data 가 64바이트 한도 안인지도 함께 검사한다.</summary>
sealed class FakeTelegram : HttpMessageHandler
{
    public static readonly byte[] DownloadBytes = [0, 255, 42, 10];
    private readonly List<(long Chat, byte[] Bytes)> _documents = [];
    public IReadOnlyList<(long Chat, byte[] Bytes)> Documents { get { lock (_lock) return _documents.ToArray(); } }
    public List<string> DownloadIds { get; } = [];
    private readonly Queue<JsonObject> _updates = new();
    private readonly object _lock = new();
    private long _updateId = 100, _messageId = 1000;
    private readonly List<(long Chat, string Text)> _sent = [];
    public List<string> Edits { get; } = [];
    public List<string> Markups { get; } = [];
    public List<string> EditMarkups { get; } = [];

    public IReadOnlyList<string> SentTo(long chat) { lock (_lock) return _sent.Where(s => s.Chat == chat).Select(s => s.Text).ToList(); }

    public void Message(long chat, string text, DateTimeOffset? when = null, JsonObject? attachment = null)
    {
        var media = attachment?.DeepClone().AsObject() ?? new JsonObject();
        media[attachment is null ? "text" : "caption"] = text;
        media["message_id"] = Interlocked.Increment(ref _messageId);
        media["date"] = (when ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        media["chat"] = new JsonObject { ["id"] = chat, ["type"] = "private" };
        media["from"] = new JsonObject { ["id"] = chat, ["is_bot"] = false, ["username"] = "tester" };
        lock (_lock) _updates.Enqueue(new JsonObject
        {
            ["update_id"] = ++_updateId,
            ["message"] = media,
        });
    }

    public void Callback(long chat, string data, long messageId = 1)
    {
        lock (_lock) _updates.Enqueue(new JsonObject
        {
            ["update_id"] = ++_updateId,
            ["callback_query"] = new JsonObject
            {
                ["id"] = "cb" + _updateId,
                ["data"] = data,
                ["message"] = new JsonObject { ["message_id"] = messageId, ["chat"] = new JsonObject { ["id"] = chat, ["type"] = "private" } },
            },
        });
    }

    private static void ValidateMarkup(JsonNode? markup)
    {
        if (markup is not JsonObject obj) return;
        if (obj["inline_keyboard"] is not JsonArray rows) return;
        foreach (var row in rows)
        {
            if (row is not JsonArray arr) continue;
            foreach (var btn in arr)
            {
                var cd = btn?["callback_data"]?.GetValue<string>() ?? "";
                var bytes = Encoding.UTF8.GetByteCount(cd);
                if (bytes is 0 or > 64)
                    throw new Exception($"Invalid callback_data byte length ({bytes}): '{cd}'");
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var method = request.RequestUri!.AbsolutePath.Split('/').Last();
        if (request.Method == HttpMethod.Get)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(DownloadBytes) };
        var body = request.Content is null or MultipartFormDataContent ? new JsonObject() : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct)) as JsonObject ?? new JsonObject();
        JsonNode? result = true;
        switch (method)
        {
            case "sendDocument":
                var form = (MultipartFormDataContent)request.Content!;
                var chatPart = form.Single(c => c.Headers.ContentDisposition?.Name?.Trim('"') == "chat_id");
                var filePart = form.Single(c => c.Headers.ContentDisposition?.Name?.Trim('"') == "document");
                var chatId = long.Parse(await chatPart.ReadAsStringAsync(ct));
                var data = await filePart.ReadAsByteArrayAsync(ct);
                lock (_lock) _documents.Add((chatId, data));
                result = new JsonObject { ["message_id"] = Interlocked.Increment(ref _messageId) };
                break;
            case "getFile":
                DownloadIds.Add(body["file_id"]!.GetValue<string>());
                result = new JsonObject { ["file_path"] = "files/test.bin", ["file_size"] = DownloadBytes.Length };
                break;
            case "getMe":
                result = new JsonObject { ["id"] = 1, ["username"] = "hub_test_bot", ["first_name"] = "Hub" };
                break;
            case "getUpdates":
                JsonArray arr;
                lock (_lock) { arr = new JsonArray(_updates.Select(u => (JsonNode)u.DeepClone()).ToArray()); _updates.Clear(); }
                if (arr.Count == 0) await Task.Delay(40, ct);
                result = arr;
                break;
            case "sendMessage":
                ValidateMarkup(body["reply_markup"]);
                lock (_lock)
                {
                    _sent.Add((body["chat_id"]!.GetValue<long>(), body["text"]!.GetValue<string>()));
                    if (body["reply_markup"] is JsonNode mk) Markups.Add(mk.ToJsonString());
                }
                result = new JsonObject { ["message_id"] = Interlocked.Increment(ref _messageId) };
                break;
            case "editMessageText":
                ValidateMarkup(body["reply_markup"]);
                lock (_lock)
                {
                    Edits.Add(body["text"]!.GetValue<string>());
                    if (body["reply_markup"] is JsonNode emk) EditMarkups.Add(emk.ToJsonString());
                }
                break;
        }
        var json = new JsonObject { ["ok"] = true, ["result"] = result }.ToJsonString();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

/// <summary>앱 흉내. 확장된 IRemoteHost 전 기능을 메모리 안에서 검증한다.</summary>
sealed class FakeHost : IRemoteHost
{
    private readonly string _cwd;
    private readonly string _newDir;
    private readonly string _transcript;
    private readonly List<RemoteTab> _tabs = [];

    public List<string> Sent { get; } = [];
    public List<string[]> Attachments { get; } = [];
    public List<string> Keys { get; } = [];
    public HashSet<long> Allowed { get; } = [];
    public List<string> Restarted { get; } = [];
    public List<string> ClosedTabs { get; } = [];
    public List<(string Folder, string Backend, string Account)> OpenedTabs { get; } = [];
    public List<string> ScheduleRanNow { get; } = [];
    public string RenamedTo { get; private set; } = "";
    public (string TabId, string Backend, string Account) SwitchedTo { get; private set; }
    public string ResumedSessionId { get; private set; } = "";
    public bool ScheduleEnabled { get; private set; } = true;
    public bool WindowShown { get; private set; } = true;
    public bool PetEnabled { get; private set; }
    public string ClaudeModel { get; private set; } = "sonnet";
    public bool ShowQuickBar { get; private set; } = true;

    public volatile string Screen = "> \n? for shortcuts";
    public Action<string>? OnSend;
    public Action<string>? OnKeys;

    public FakeHost(string cwd, string newDir, string transcript)
    {
        _cwd = cwd;
        _newDir = newDir;
        _transcript = transcript;
        _tabs.Add(new RemoteTab("t1", "project", "Claude Code", CliProfile.Claude.Backend, cwd, true, transcript, CliProfile.Claude,
                                DateTime.UtcNow.AddSeconds(-30)));
    }

    public bool IsAllowedChat(long chat) { lock (Allowed) return Allowed.Contains(chat); }

    public Task<IReadOnlyList<RemoteTab>> TabsAsync()
    {
        lock (_tabs) return Task.FromResult<IReadOnlyList<RemoteTab>>(_tabs.ToList());
    }

    public Task<string?> SendAsync(string tabId, string text, IReadOnlyList<string> files)
    {
        lock (Sent) Sent.Add(text);
        OnSend?.Invoke(text);
        lock (Attachments) Attachments.Add(files.ToArray());
        return Task.FromResult<string?>(null);
    }

    public Task<bool> KeysAsync(string tabId, string data)
    {
        lock (Keys) Keys.Add(data);
        OnKeys?.Invoke(data);
        return Task.FromResult(true);
    }

    public Task<RemoteScreen?> ScreenAsync(string tabId) => Task.FromResult<RemoteScreen?>(new RemoteScreen(Screen, false));

    public Task<(string? TabId, string? Error)> OpenAsync(string folder, string backend) => OpenAsync(folder, backend, "");

    public Task<(string? TabId, string? Error)> OpenAsync(string folder, string backend, string account)
    {
        lock (_tabs)
        {
            OpenedTabs.Add((folder, backend, account));
            var cli = CliProfile.ForOrDefault(backend);
            var id = "t" + (_tabs.Count + 1);
            _tabs.Add(new RemoteTab(id, Path.GetFileName(folder), cli.Display, cli.Backend, folder, true, _transcript, cli,
                                    DateTime.UtcNow.AddSeconds(-10), account, account.Length > 0 ? "부계정" : ""));
            return Task.FromResult<(string?, string?)>((id, null));
        }
    }

    public Task<IReadOnlyList<string>> RecentFoldersAsync(int max) =>
        Task.FromResult<IReadOnlyList<string>>([_newDir, _cwd]);

    public Task<IReadOnlyList<RemoteAccountChoice>> ListAccountsAsync(string backend) =>
        Task.FromResult<IReadOnlyList<RemoteAccountChoice>>(
        [
            new(backend, "Claude Code", "", "기본", "main@example.com", true),
            new(backend, "Claude Code", "acc-sub", "부계정", "sub@example.com", true),
        ]);

    public Task<string?> CloseTabAsync(string tabId)
    {
        lock (_tabs)
        {
            ClosedTabs.Add(tabId);
            _tabs.RemoveAll(t => t.Id == tabId);
        }
        return Task.FromResult<string?>(null);
    }

    public Task<string?> RestartTabAsync(string tabId)
    {
        lock (_tabs) Restarted.Add(tabId);
        return Task.FromResult<string?>(null);
    }

    public Task<string?> RenameTabAsync(string tabId, string newName)
    {
        lock (_tabs)
        {
            RenamedTo = newName;
            var idx = _tabs.FindIndex(t => t.Id == tabId);
            if (idx >= 0) _tabs[idx] = _tabs[idx] with { Name = newName.Length > 0 ? newName : "project" };
        }
        return Task.FromResult<string?>(null);
    }

    public Task<IReadOnlyList<RemoteAccountChoice>> ListSwitchTargetsAsync(string tabId) =>
        Task.FromResult<IReadOnlyList<RemoteAccountChoice>>(
        [
            new(CliProfile.Codex.Backend, "Codex", "acc-codex", "Codex 프로", "codex@example.com", true),
        ]);

    public Task<string?> SwitchTabAsync(string tabId, string backend, string account)
    {
        SwitchedTo = (tabId, backend, account);
        return Task.FromResult<string?>(null);
    }

    public Task<IReadOnlyList<RemoteSessionItem>> RecentSessionsAsync(int max, string? backend = null) =>
        Task.FromResult<IReadOnlyList<RemoteSessionItem>>(
        [
            new(CliProfile.Claude.Backend, "Claude Code", "sess-123", _cwd, DateTime.Now.AddMinutes(-15), "로그인 버그 수정"),
        ]);

    public Task<(string? TabId, string? Error)> ResumeSessionAsync(string backend, string cwd, string sessionId, string account)
    {
        ResumedSessionId = sessionId;
        lock (_tabs)
        {
            var id = "t" + (_tabs.Count + 1);
            _tabs.Add(new RemoteTab(id, "resumed", "Claude Code", backend, cwd, true, _transcript, CliProfile.Claude, DateTime.UtcNow.AddSeconds(-10)));
            return Task.FromResult<(string?, string?)>((id, null));
        }
    }

    public Task<IReadOnlyList<RemoteUsageItem>> GetUsageAsync() =>
        Task.FromResult<IReadOnlyList<RemoteUsageItem>>(
        [
            new("Claude Code", CliProfile.Claude.Backend, "기본", "main@example.com",
                [new UsageWindow("5h", 38, DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()),
                 new UsageWindow("주", 55, DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds())]),
            new("Antigravity", CliProfile.Antigravity.Backend, "기본", "",
                [new UsageWindow("5h", 12, DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeSeconds())]),
        ]);

    public Task<IReadOnlyList<RemoteScheduleItem>> GetSchedulesAsync() =>
        Task.FromResult<IReadOnlyList<RemoteScheduleItem>>(
        [
            new("job-1", "앱테크 점검", ScheduleEnabled, "매일 09:00", "새 탭: D:\\Work · Antigravity",
                false, DateTime.Now.AddHours(3), "ok", DateTime.Now.AddHours(-1), "모든 점검 통과", null),
        ]);

    public Task<string?> SetScheduleEnabledAsync(string jobId, bool enabled)
    {
        ScheduleEnabled = enabled;
        return Task.FromResult<string?>(null);
    }

    public Task<string?> RunScheduleNowAsync(string jobId)
    {
        ScheduleRanNow.Add(jobId);
        return Task.FromResult<string?>(null);
    }

    public void SetShowQuickBar(bool show) => ShowQuickBar = show;

    public Task<RemoteAppSettings?> GetAppSettingsAsync() =>
        Task.FromResult<RemoteAppSettings?>(new RemoteAppSettings(
            WindowShown, PetEnabled, true, "startup", true, ShowQuickBar, "ko",
            [
                new RemoteCliSetting(CliProfile.Claude.Backend, "Claude Code", ClaudeModel, "high",
                    [new RemotePermItem("ClaudeCodePermissionMode", "권한", "acceptEdits", ["manual", "acceptEdits", "bypassPermissions"])],
                    ["opus", "sonnet", "haiku"], ["low", "medium", "high", "max"]),
            ]));

    public Task<string?> ApplyAppSettingAsync(string key, string value)
    {
        switch (key)
        {
            case "window": WindowShown = value == "show"; break;
            case "pet": PetEnabled = value == "true"; break;
            case "model:claude_code": ClaudeModel = value; break;
        }
        return Task.FromResult<string?>(null);
    }

    public bool IsAllowed(long chat) => IsAllowedChat(chat);
    public void AddChat(long chat, string name) { lock (Allowed) Allowed.Add(chat); }
    public bool English => false;
    public void Log(string message, string level) => Console.WriteLine($"  [{level}] {message}");
    public void StateChanged() { }
}
