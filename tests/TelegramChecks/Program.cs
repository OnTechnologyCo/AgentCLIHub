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
    // ── 명령 ──
    Check(TelegramText.Command("/use@MyHubBot 2") == ("use", "2"), "command strips @bot suffix");
    Check(TelegramText.Command("안녕") == ("", "안녕"), "plain text is not a command");
    Check(TelegramText.Command("//help").Command == "" && TelegramText.Unescape("//help") == "/help", "double slash goes to the CLI");
    Check(TelegramText.NewArgs(@"D:\작업 폴더 codex") == (@"D:\작업 폴더", CliProfile.Codex.Backend), "new keeps spaces in folder and reads CLI at end");
    Check(TelegramText.NewArgs("\"D:\\x\"") == (@"D:\x", ""), "new without CLI leaves backend empty");
    Check(TelegramText.NewArgs("claude").Backend == CliProfile.Claude.Backend, "new with only a CLI name");

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
    Check(legacy.Telegram is { Enabled: false } && legacy.Telegram.Chats.Count == 0, "old settings get Telegram off by default");
    var round = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings
    {
        Telegram = new TelegramSettings { Enabled = true, Chats = [new TelegramChat { Id = 42, Name = "@me" }] },
    }))!;
    Check(round.Telegram.Enabled && round.Telegram.Chats.Single().Id == 42, "Telegram settings round-trip");
    Check(!JsonSerializer.Serialize(round).Contains("Token", StringComparison.OrdinalIgnoreCase), "settings never hold the bot token");

    // ── 끝에서 끝까지(가짜 텔레그램 + 가짜 앱) ──
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
    File.WriteAllText(transcript, ClaudeLine("user", "u1", null, "\"이전 질문\"") + ClaudeLine("assistant", "a1", "u1", "[{\"type\":\"text\",\"text\":\"이전 답\"}]"));
    var app = new FakeHost(root, transcript);
    using var remote = new TelegramRemote("123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw", app, tg);
    remote.Start();

    // 1) 모르는 대화 → 안내만, 탭에는 아무것도 안 간다.
    tg.Message(Stranger, "rm -rf /");
    await Until(() => tg.SentTo(Stranger).Any(t => t.Contains("/pair")), "stranger gets a pairing hint");
    Check(app.Sent.Count == 0, "stranger text never reaches a tab");

    // 2) 틀린 코드 → 거절, 맞는 코드 → 허용.
    var code = remote.NewPairingCode();
    tg.Message(Me, "/pair ZZZZ-ZZZZ");
    await Until(() => tg.SentTo(Me).Any(t => t.Contains("맞지 않거나") || t.Contains("wrong")), "wrong code is refused");
    Check(!app.IsAllowedChat(Me), "wrong code does not pair");
    tg.Message(Me, "/pair " + code.ToLowerInvariant());
    await Until(() => app.IsAllowedChat(Me), "right code pairs the chat");
    Check(remote.PairingCode is null, "pairing code is single-use");

    // 3) 질문 → 탭으로 들어가고, 도구 진행 → 최종 답.
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

    // 4) 승인 창 → 단추 → 화살표 + Enter → 끝.
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

    // 5) 오래된 메시지(앱이 꺼져 있을 때 쌓인 것)는 실행하지 않는다.
    var before = app.Sent.Count;
    tg.Message(Me, "옛날 지시", DateTimeOffset.UtcNow.AddHours(-3));
    await Until(() => tg.SentTo(Me).Any(t => t.Contains("실행하지 않았습니다") || t.Contains("were not run")), "stale message is refused");
    Check(app.Sent.Count == before, "stale message never reaches a tab");

    // 6) 지운 대화는 바로 막힌다.
    lock (app.Allowed) app.Allowed.Clear();
    var seen = tg.SentTo(Me).Count;
    tg.Message(Me, "/tabs");
    await Until(() => tg.SentTo(Me).Count > seen, "revoked chat gets only the pairing hint");
    Check(tg.SentTo(Me).Skip(seen).All(t => t.Contains("/pair") && !t.Contains("project · Claude Code")), "revoked chat cannot list tabs");
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
        await Task.Delay(100);
    }
    throw new Exception("FAIL (timeout) " + what);
}

/// <summary>Bot API 흉내. getUpdates 는 넣어 둔 것을 주고, 보낸 메시지·고쳐 쓴 메시지를 모아 둔다.</summary>
sealed class FakeTelegram : HttpMessageHandler
{
    private readonly Queue<JsonObject> _updates = new();
    private readonly object _lock = new();
    private long _updateId = 100, _messageId = 1000;
    private readonly List<(long Chat, string Text)> _sent = [];
    public List<string> Edits { get; } = [];
    public List<string> Markups { get; } = [];

    public IReadOnlyList<string> SentTo(long chat) { lock (_lock) return _sent.Where(s => s.Chat == chat).Select(s => s.Text).ToList(); }

    public void Message(long chat, string text, DateTimeOffset? when = null)
    {
        lock (_lock) _updates.Enqueue(new JsonObject
        {
            ["update_id"] = ++_updateId,
            ["message"] = new JsonObject
            {
                ["message_id"] = ++_messageId,
                ["date"] = (when ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
                ["chat"] = new JsonObject { ["id"] = chat, ["type"] = "private" },
                ["from"] = new JsonObject { ["id"] = chat, ["is_bot"] = false, ["username"] = "tester" },
                ["text"] = text,
            },
        });
    }

    public void Callback(long chat, string data)
    {
        lock (_lock) _updates.Enqueue(new JsonObject
        {
            ["update_id"] = ++_updateId,
            ["callback_query"] = new JsonObject
            {
                ["id"] = "cb" + _updateId,
                ["data"] = data,
                ["message"] = new JsonObject { ["message_id"] = 1, ["chat"] = new JsonObject { ["id"] = chat, ["type"] = "private" } },
            },
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var method = request.RequestUri!.AbsolutePath.Split('/').Last();
        var body = request.Content is null ? new JsonObject() : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct)) as JsonObject ?? new JsonObject();
        JsonNode? result = true;
        switch (method)
        {
            case "getMe":
                result = new JsonObject { ["id"] = 1, ["username"] = "hub_test_bot", ["first_name"] = "Hub" };
                break;
            case "getUpdates":
                JsonArray arr;
                lock (_lock) { arr = new JsonArray(_updates.Select(u => (JsonNode)u.DeepClone()).ToArray()); _updates.Clear(); }
                if (arr.Count == 0) await Task.Delay(50, ct);
                result = arr;
                break;
            case "sendMessage":
                lock (_lock)
                {
                    _sent.Add((body["chat_id"]!.GetValue<long>(), body["text"]!.GetValue<string>()));
                    if (body["reply_markup"] is JsonNode mk) Markups.Add(mk.ToJsonString());
                }
                result = new JsonObject { ["message_id"] = Interlocked.Increment(ref _messageId) };
                break;
            case "editMessageText":
                lock (_lock) Edits.Add(body["text"]!.GetValue<string>());
                break;
        }
        var json = new JsonObject { ["ok"] = true, ["result"] = result }.ToJsonString();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

/// <summary>앱 흉내. 탭 하나(t1), 화면 글과 입력을 손으로 바꾼다.</summary>
sealed class FakeHost(string cwd, string transcript) : IRemoteHost
{
    public List<string> Sent { get; } = [];
    public List<string> Keys { get; } = [];
    public HashSet<long> Allowed { get; } = [];
    public volatile string Screen = "> \n? for shortcuts";
    public Action<string>? OnSend;
    public Action<string>? OnKeys;
    public bool IsAllowedChat(long chat) { lock (Allowed) return Allowed.Contains(chat); }

    public Task<IReadOnlyList<RemoteTab>> TabsAsync() => Task.FromResult<IReadOnlyList<RemoteTab>>(
        [new RemoteTab("t1", "project", "Claude Code", CliProfile.Claude.Backend, cwd, true, transcript, CliProfile.Claude,
                       DateTime.UtcNow.AddSeconds(-30))]);

    public Task<string?> SendAsync(string tabId, string text, IReadOnlyList<string> files)
    {
        lock (Sent) Sent.Add(text);
        OnSend?.Invoke(text);
        return Task.FromResult<string?>(null);
    }

    public Task<bool> KeysAsync(string tabId, string data)
    {
        lock (Keys) Keys.Add(data);
        OnKeys?.Invoke(data);
        return Task.FromResult(true);
    }

    public Task<RemoteScreen?> ScreenAsync(string tabId) => Task.FromResult<RemoteScreen?>(new RemoteScreen(Screen, false));
    public Task<(string? TabId, string? Error)> OpenAsync(string folder, string backend) => Task.FromResult<(string?, string?)>((null, "not in tests"));
    public Task<IReadOnlyList<string>> RecentFoldersAsync(int max) => Task.FromResult<IReadOnlyList<string>>([]);
    public bool IsAllowed(long chat) => IsAllowedChat(chat);
    public void AddChat(long chat, string name) { lock (Allowed) Allowed.Add(chat); }
    public bool English => false;
    public void Log(string message, string level) => Console.WriteLine($"  [{level}] {message}");
    public void StateChanged() { }
}
