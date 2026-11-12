using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentCLIHub;

/// <summary>원격에서 본 CLI 탭 하나. 로그인 탭은 들어오지 않는다.</summary>
public sealed record RemoteTab(string Id, string Name, string Cli, string Backend, string Cwd, bool Running,
                               string? TranscriptPath, CliProfile Profile, DateTime LastOutputUtc);

/// <summary>터미널에 지금 보이는 글. <paramref name="FocusMode"/> = CLI 가 포커스 알림(?1004h)을 켜 두었는가.</summary>
public sealed record RemoteScreen(string Text, bool FocusMode);

/// <summary>
/// 텔레그램 원격이 앱에 바라는 것. 구현(<see cref="MainWindow"/>)이 UI 스레드로 넘겨 처리한다 —
/// 탭 사전과 WebView2 는 UI 스레드에서만 만질 수 있다.
/// </summary>
public interface IRemoteHost
{
    Task<IReadOnlyList<RemoteTab>> TabsAsync();
    /// <returns>실패 이유. 보냈으면 null.</returns>
    Task<string?> SendAsync(string tabId, string text, IReadOnlyList<string> files);
    Task<bool> KeysAsync(string tabId, string data);
    Task<RemoteScreen?> ScreenAsync(string tabId);
    Task<(string? TabId, string? Error)> OpenAsync(string folder, string backend);
    Task<IReadOnlyList<string>> RecentFoldersAsync(int max);
    /// <summary>페어링을 마친 대화인가. 매번 묻는다 — 설정 화면에서 지우면 바로 막혀야 한다.</summary>
    bool IsAllowed(long chat);
    void AddChat(long chat, string name);
    /// <summary>봇이 쓰는 말. 앱 화면 언어를 따른다.</summary>
    bool English { get; }
    void Log(string message, string level);
    void StateChanged();
}

/// <summary>
/// 텔레그램으로 이 앱의 CLI 탭에 묻고 답을 받는다.
///
/// <b>무엇을 하나.</b> 봇이 받은 글을 고른 탭에 한 턴으로 넣고(화면 입력창과 같은 길 — 대괄호 붙여넣기 + Enter),
/// 그 턴이 끝날 때까지 지켜보다가 최종 답을 돌려보낸다. 진행 중에는 쓴 도구를 한 메시지에 고쳐 써 가며 보이고,
/// CLI 가 승인·고르기 창을 띄우면 그 화면과 항목 단추를 보낸다 — 누른 단추는 화살표 + Enter 로 들어간다.
///
/// <b>어떻게 끝을 아나.</b> 세 가지를 같이 본다. ① 터미널 출력이 잠잠하다 ② 화면에 "esc to interrupt" 가 없다
/// ③ 대화 기록(jsonl)의 이번 턴 마지막 조각이 답 글이다. 출력만 보면 생각하느라 멈춘 순간을 끝으로 착각하고,
/// 기록만 보면 중간 답("확인해 보겠습니다")에서 끝낸다.
///
/// <b>누가 쓸 수 있나.</b> 페어링한 개인 대화만. 그룹·채널은 아무나 끼어들 수 있어 받지 않는다. 앱이 꺼져 있는
/// 동안 쌓인 메시지는 실행하지 않는다 — 몇 시간 전 지시가 켜자마자 돌면 안 된다.
/// </summary>
public sealed class TelegramRemote : IDisposable
{
    private readonly TelegramApi _api;
    private readonly IRemoteHost _host;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly Dictionary<long, ChatState> _chats = [];
    private readonly Dictionary<string, Watch> _watches = new(StringComparer.Ordinal);
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private Task? _loop;
    private long _offset;
    private int _seq;
    private string? _pairCode;
    private DateTime _pairUntil;
    private int _pairMisses;
    private bool _identified;
    private bool _english;
    private DateTime _languageAt;

    public string Token { get; }
    public string BotName { get; private set; } = "";
    public string Error { get; private set; } = "";
    public bool Connected => _identified && Error.Length == 0;

    /// <summary>지금 쓸 수 있는 페어링 코드. 없거나 지났으면 null.</summary>
    public string? PairingCode { get { lock (_lock) return _pairCode is not null && DateTime.UtcNow < _pairUntil ? _pairCode : null; } }
    public DateTime PairingUntilUtc { get { lock (_lock) return _pairUntil; } }

    /// <summary>페어링 코드 유효 시간.</summary>
    public static readonly TimeSpan PairingLife = TimeSpan.FromMinutes(10);

    /// <summary>앱이 뜨기 이만큼 전에 보낸 메시지까지는 받는다(시계 차이·막 켜는 중에 보낸 말).</summary>
    private static readonly TimeSpan StaleGrace = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 봇마다 마지막으로 처리한 update 번호. **껐다 켜도**(토큰 다시 저장 · 사용 끄고 켜기) 이미 처리한 메시지를
    /// 다시 받지 않게 프로세스 안에서 기억한다 — 텔레그램은 다음 getUpdates 가 와야 처리 완료로 보므로,
    /// 마지막 묶음은 끄는 순간 아직 확인되지 않은 채 남아 있다. 그대로 새로 받으면 같은 질문이 CLI 에 두 번 들어간다.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> Offsets = new();

    private string BotKey => Token.Split(':')[0];

    public TelegramRemote(string token, IRemoteHost host, HttpMessageHandler? handler = null)
    {
        Token = token;
        _host = host;
        _api = new TelegramApi(token, handler);
        _offset = Offsets.TryGetValue(BotKey, out var last) ? last : 0;
    }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    public string NewPairingCode()
    {
        string code;
        lock (_lock)
        {
            _pairCode = code = TelegramText.NewPairingCode();
            _pairUntil = DateTime.UtcNow + PairingLife;
            _pairMisses = 0;
        }
        _host.StateChanged();
        return code;
    }

    private string L(string ko, string en)
    {
        if (DateTime.UtcNow - _languageAt > TimeSpan.FromSeconds(15))
        {
            try { _english = _host.English; } catch { }
            _languageAt = DateTime.UtcNow;
        }
        return _english ? en : ko;
    }

    private void SetError(string message)
    {
        if (Error == message) return;
        Error = message;
        if (message.Length > 0) _host.Log("[Telegram] " + message, "w");
        _host.StateChanged();
    }

    // ── 받기 ────────────────────────────────────────────────────────────────

    private async Task LoopAsync(CancellationToken ct)
    {
        var delay = 2;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_identified)
                {
                    var me = await _api.GetMeAsync(ct);
                    BotName = me.Username;
                    _identified = true;
                    _host.Log(L($"텔레그램 봇 @{BotName} 에 연결했습니다.", $"Connected to Telegram bot @{BotName}."), "i");
                    try { await _api.SetCommandsAsync(CommandMenu(), ct); } catch { /* 메뉴는 없어도 된다 */ }
                    _host.StateChanged();
                }

                var updates = await _api.GetUpdatesAsync(_offset, 25, ct);
                delay = 2;
                if (Error.Length > 0) SetError("");
                foreach (var u in updates)
                {
                    if (u is null) continue;
                    _offset = Math.Max(_offset, (u["update_id"]?.GetValue<long>() ?? 0) + 1);
                    Offsets[BotKey] = _offset;
                    try { await HandleUpdateAsync(u, ct); }
                    catch (Exception) when (ct.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        _host.Log("[Telegram] " + L("메시지를 처리하지 못했습니다: ", "Could not handle a message: ") + _api.Scrub(ex.Message), "w");
                    }
                }
            }
            // 끄는 중이다(취소·해제된 HttpClient). 오류로 적지 않는다.
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (TelegramException ex) when (ex.Status is 401 or 404)
            {
                SetError(L("봇 토큰이 거절되었습니다 — 설정에서 토큰을 다시 넣으세요.",
                           "The bot token was rejected — enter it again in Settings."));
                return;
            }
            catch (TelegramException ex) when (ex.Status == 409)
            {
                SetError(L("같은 봇을 다른 곳(다른 PC·웹훅)에서 받고 있습니다 — 한 곳에서만 켜 두세요.",
                           "This bot is being polled elsewhere (another PC or a webhook). Keep it on in one place only."));
                await Pause(30, ct);
            }
            catch (Exception ex)
            {
                SetError(L("텔레그램에 연결하지 못했습니다: ", "Could not reach Telegram: ") + _api.Scrub(ex.Message));
                await Pause(delay, ct);
                delay = Math.Min(delay * 2, 60);
            }
        }
    }

    private static async Task Pause(int seconds, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); } catch (OperationCanceledException) { }
    }

    private IEnumerable<(string, string)> CommandMenu() =>
    [
        ("tabs", L("열린 CLI 탭 목록", "List open CLI tabs")),
        ("use", L("질문을 받을 탭 고르기", "Choose the tab for questions")),
        ("new", L("새 탭 열기 — /new 폴더 claude|codex", "Open a tab — /new folder claude|codex")),
        ("screen", L("지금 화면 보기", "Show the current screen")),
        ("esc", L("Esc 보내기(중단·취소)", "Send Esc (interrupt/cancel)")),
        ("enter", L("Enter 보내기", "Send Enter")),
        ("key", L("글자를 그대로 입력(Enter 없이)", "Type text without Enter")),
        ("status", L("지금 상태", "Current status")),
        ("help", L("도움말", "Help")),
    ];

    private sealed class ChatState
    {
        public string? Target;
        public List<string> Folders = [];
        public string? NewFolder;
        public DateTime HintedUtc;
        public bool StaleNoticed;
        public string? PendingText;
        public JsonObject? PendingMessage;
    }

    private ChatState State(long chat)
    {
        lock (_lock)
        {
            if (!_chats.TryGetValue(chat, out var s)) _chats[chat] = s = new ChatState();
            return s;
        }
    }

    private bool IsAllowed(long chat)
    {
        try { return _host.IsAllowed(chat); }
        catch { return false; }   // 모르면 막는다
    }

    private async Task HandleUpdateAsync(JsonNode u, CancellationToken ct)
    {
        if (u["callback_query"] is JsonObject cb) { await HandleCallbackAsync(cb, ct); return; }
        if (u["message"] is not JsonObject msg) return;

        var chat = msg["chat"]?["id"]?.GetValue<long>() ?? 0;
        var type = msg["chat"]?["type"]?.GetValue<string>() ?? "";
        // **개인 대화만.** 그룹은 누구나 들어올 수 있고, 거기서 한 말이 이 PC 의 CLI 로 가면 안 된다.
        if (chat == 0 || type != "private") return;
        var from = msg["from"] as JsonObject;
        if (from?["is_bot"]?.GetValue<bool>() == true) return;

        var text = msg["text"]?.GetValue<string>() ?? msg["caption"]?.GetValue<string>() ?? "";
        var (cmd, args) = TelegramText.Command(text);

        if (!IsAllowed(chat))
        {
            if (cmd == "pair") { await PairAsync(chat, from, args, ct); return; }
            // 모르는 사람에게는 무엇이 돌고 있는지 말하지 않는다. 페어링 안내만 10분에 한 번.
            var st = State(chat);
            if (DateTime.UtcNow - st.HintedUtc < TimeSpan.FromMinutes(10)) return;
            st.HintedUtc = DateTime.UtcNow;
            await Say(chat, L("이 봇은 페어링한 대화만 씁니다. 앱의 [설정 → 텔레그램 원격] 에서 페어링 코드를 만든 뒤 /pair 코드 를 보내세요.",
                              "This bot only answers paired chats. Create a pairing code in the app (Settings → Telegram remote) and send /pair CODE."), ct);
            return;
        }

        var date = DateTimeOffset.FromUnixTimeSeconds(msg["date"]?.GetValue<long>() ?? 0).UtcDateTime;
        if (date < _startedUtc - StaleGrace)
        {
            // 앱이 꺼져 있는 동안 쌓인 메시지. 실행하지 않고, 한 번만 알린다.
            var st = State(chat);
            if (st.StaleNoticed) return;
            st.StaleNoticed = true;
            await Say(chat, L("앱이 꺼져 있는 동안 받은 메시지는 실행하지 않았습니다. 필요하면 다시 보내세요.",
                              "Messages received while the app was closed were not run. Send them again if needed."), ct);
            return;
        }

        switch (cmd)
        {
            case "start":
            case "help": await Say(chat, Help(), ct); return;
            case "pair": await Say(chat, L("이미 페어링되어 있습니다.", "This chat is already paired."), ct); return;
            case "tabs": await TabsMenuAsync(chat, null, ct); return;
            case "use": await UseAsync(chat, args, ct); return;
            case "new": await NewAsync(chat, args, ct); return;
            case "screen": await ScreenAsync(chat, null, ct); return;
            case "esc": await KeysAsync(chat, "\u001b", L("Esc 를 보냈습니다.", "Sent Esc."), ct); return;
            case "enter": await KeysAsync(chat, "\r", L("Enter 를 보냈습니다.", "Sent Enter."), ct); return;
            case "key":
                if (args.Length == 0) { await Say(chat, L("보낼 글자를 적으세요 — 예: /key y", "Add the text to type — e.g. /key y"), ct); return; }
                await KeysAsync(chat, args, L("입력했습니다.", "Typed."), ct);
                return;
            case "status": await StatusAsync(chat, ct); return;
        }

        // 나머지는 질문이다. 이 봇이 모르는 /명령(/compact 등)도 CLI 로 그대로 보낸다.
        await AskAsync(chat, TelegramText.Unescape(text), msg, ct);
    }

    private string Help() => L(
        "AgentCLIHub 텔레그램 원격\n\n" +
        "글을 보내면 고른 탭의 CLI 에 질문으로 들어가고, 끝나면 답이 옵니다. 사진·파일도 함께 보낼 수 있습니다.\n" +
        "작업 중에는 쓴 도구가 한 메시지에 이어서 보이고, 승인·고르기 창이 뜨면 단추로 고를 수 있습니다.\n\n" +
        "/tabs — 열린 CLI 탭 목록과 고르기\n" +
        "/use 번호 — 질문을 받을 탭 고르기\n" +
        "/new [폴더] [claude|codex|antigravity] — 새 탭 열기(폴더를 빼면 최근 폴더에서 고름)\n" +
        "/screen — 지금 화면 보기\n" +
        "/esc — Esc 보내기(작업 중단·취소)\n" +
        "/enter — Enter 보내기\n" +
        "/key 글자 — 글자를 그대로 입력(Enter 없이)\n" +
        "/status — 지금 상태\n\n" +
        "그 밖의 /명령(/compact 등)은 CLI 로 그대로 갑니다. 이 봇의 명령과 이름이 같은 CLI 명령은 //help 처럼 / 를 두 번 쓰세요.",
        "AgentCLIHub Telegram remote\n\n" +
        "Send text and it goes to the selected tab's CLI as a question; the answer comes back when it finishes. Photos and files can be attached.\n" +
        "While it works, tool use is shown in one updating message, and approval or choice prompts arrive with buttons.\n\n" +
        "/tabs — list and choose open CLI tabs\n" +
        "/use N — choose the tab for questions\n" +
        "/new [folder] [claude|codex|antigravity] — open a tab (omit the folder to pick a recent one)\n" +
        "/screen — show the current screen\n" +
        "/esc — send Esc (interrupt/cancel)\n" +
        "/enter — send Enter\n" +
        "/key text — type text without Enter\n" +
        "/status — current status\n\n" +
        "Other /commands (/compact, …) go to the CLI as-is. For a CLI command with the same name as one of these, use two slashes, e.g. //help.");

    // ── 페어링 ──────────────────────────────────────────────────────────────

    private async Task PairAsync(long chat, JsonObject? from, string code, CancellationToken ct)
    {
        bool ok;
        lock (_lock)
        {
            var live = _pairCode is not null && DateTime.UtcNow < _pairUntil;
            ok = live && TelegramText.SameCode(_pairCode, code);
            if (ok) _pairCode = null;
            else if (live && ++_pairMisses >= 5) _pairCode = null;   // 맞혀 보기를 막는다
        }
        if (!ok)
        {
            await Say(chat, L("페어링 코드가 맞지 않거나 시간이 지났습니다. 앱에서 새 코드를 만드세요.",
                              "The pairing code is wrong or expired. Create a new code in the app."), ct);
            return;
        }

        var name = from?["username"]?.GetValue<string>() is { Length: > 0 } u ? "@" + u
                 : string.Join(" ", new[] { from?["first_name"]?.GetValue<string>(), from?["last_name"]?.GetValue<string>() }
                       .Where(s => !string.IsNullOrWhiteSpace(s)));
        _host.AddChat(chat, name);
        _host.Log(L($"[Telegram] {name} 대화를 페어링했습니다.", $"[Telegram] Paired chat {name}."), "i");
        _host.StateChanged();
        await Say(chat, L("페어링했습니다. 이제 이 대화에서 이 PC 의 CLI 탭을 쓸 수 있습니다.\n\n",
                          "Paired. This chat can now use the CLI tabs on this PC.\n\n") + Help(), ct);
    }

    // ── 탭 ──────────────────────────────────────────────────────────────────

    private string Label(RemoteTab t) => $"{t.Name} · {t.Cli}";

    private async Task<RemoteTab?> FindAsync(string? id) =>
        id is null ? null : (await _host.TabsAsync()).FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// 질문을 받을 탭. 고른 것이 없거나 닫혔으면 — 탭이 하나뿐이면 그것, 여럿이면 고르게 한다.
    /// <paramref name="quiet"/> 이면 고르기를 띄우지 않고 null 만 돌려준다.
    /// </summary>
    private async Task<RemoteTab?> TargetAsync(long chat, bool quiet, CancellationToken ct)
    {
        var tabs = await _host.TabsAsync();
        var st = State(chat);
        if (st.Target is { } id && tabs.FirstOrDefault(t => t.Id == id) is { } hit) return hit;
        if (tabs.Count == 1) { st.Target = tabs[0].Id; return tabs[0]; }
        if (quiet) return null;
        if (tabs.Count == 0)
            await Say(chat, L("열린 CLI 탭이 없습니다. /new 로 새 탭을 여세요.", "No CLI tab is open. Open one with /new."), ct);
        else
            await TabsMenuAsync(chat, L("어느 탭으로 보낼까요?", "Which tab should get this?"), ct);
        return null;
    }

    private async Task TabsMenuAsync(long chat, string? title, CancellationToken ct)
    {
        var tabs = await _host.TabsAsync();
        if (tabs.Count == 0)
        {
            await Say(chat, L("열린 CLI 탭이 없습니다. /new 로 새 탭을 여세요.", "No CLI tab is open. Open one with /new."), ct);
            return;
        }
        var target = State(chat).Target;
        var sb = new StringBuilder(title ?? L("열린 CLI 탭:", "Open CLI tabs:")).Append('\n');
        var rows = new List<TelegramButton[]>();
        for (var i = 0; i < tabs.Count; i++)
        {
            var t = tabs[i];
            var mark = t.Id == target ? "▶ " : "";
            var state = t.Running ? L("실행 중", "running") : L("꺼짐", "stopped");
            sb.Append('\n').Append(i + 1).Append(") ").Append(mark).Append(Label(t)).Append(" · ").Append(state)
              .Append("\n   ").Append(t.Cwd);
            rows.Add([new TelegramButton(TelegramText.Clip($"{i + 1}) {mark}{Label(t)}", 60), "use:" + t.Id)]);
        }
        await _api.SendMessageAsync(chat, sb.ToString(), TelegramApi.Keyboard(rows), ct: ct);
    }

    private async Task UseAsync(long chat, string args, CancellationToken ct)
    {
        var tabs = await _host.TabsAsync();
        var key = args.Trim();
        var hit = tabs.FirstOrDefault(t => string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase));
        if (hit is null && int.TryParse(key, out var n) && n >= 1 && n <= tabs.Count) hit = tabs[n - 1];
        if (hit is null) { await TabsMenuAsync(chat, L("고를 탭 번호를 확인하세요:", "Check the tab number:"), ct); return; }
        await SelectAsync(chat, hit, ct);
    }

    private async Task SelectAsync(long chat, RemoteTab tab, CancellationToken ct)
    {
        var st = State(chat);
        st.Target = tab.Id;
        await Say(chat, L($"이제 질문은 {Label(tab)} 탭으로 갑니다.\n{tab.Cwd}", $"Questions now go to {Label(tab)}.\n{tab.Cwd}"), ct);

        // 탭을 고르느라 못 보낸 질문이 있으면 이어서 보낸다.
        var text = st.PendingText;
        var msg = st.PendingMessage;
        st.PendingText = null; st.PendingMessage = null;
        if (text is not null && msg is not null) await AskAsync(chat, text, msg, ct);
    }

    private async Task NewAsync(long chat, string args, CancellationToken ct)
    {
        var (folder, backend) = TelegramText.NewArgs(args);
        if (folder.Length == 0)
        {
            var folders = await _host.RecentFoldersAsync(8);
            var st = State(chat);
            st.Folders = folders.ToList();
            if (folders.Count == 0)
            {
                await Say(chat, L("최근 폴더가 없습니다. /new D:\\작업폴더 claude 처럼 경로를 적으세요.",
                                  "No recent folders. Give a path, e.g. /new D:\\work claude"), ct);
                return;
            }
            var rows = folders.Select((f, i) => new[] { new TelegramButton(TelegramText.Clip(f, 60), "nf:" + i) }).ToList();
            await _api.SendMessageAsync(chat, L("어느 폴더에서 열까요? (다른 폴더는 /new 경로 [claude|codex])",
                                                "Which folder? (for another, /new path [claude|codex])"),
                                        TelegramApi.Keyboard(rows), ct: ct);
            return;
        }
        if (backend.Length == 0) { State(chat).NewFolder = folder; await CliMenuAsync(chat, folder, ct); return; }
        await OpenAsync(chat, folder, backend, ct);
    }

    private async Task CliMenuAsync(long chat, string folder, CancellationToken ct)
    {
        var rows = new[]
        {
            new[] { new TelegramButton("Claude Code", "nc:" + CliProfile.Claude.Backend),
                    new TelegramButton("Codex", "nc:" + CliProfile.Codex.Backend),
                    new TelegramButton("Antigravity", "nc:" + CliProfile.Antigravity.Backend) },
        };
        await _api.SendMessageAsync(chat, L($"{folder}\n어느 CLI 로 열까요?", $"{folder}\nWhich CLI?"), TelegramApi.Keyboard(rows), ct: ct);
    }

    private async Task OpenAsync(long chat, string folder, string backend, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
        {
            await Say(chat, L($"폴더가 없습니다 — {folder}\n이 PC 의 전체 경로를 적으세요(예: D:\\Src\\프로젝트).",
                              $"Folder not found — {folder}\nUse a full path on this PC (e.g. D:\\Src\\project)."), ct);
            return;
        }
        var (id, error) = await _host.OpenAsync(Path.GetFullPath(folder), backend);
        if (id is null) { await Say(chat, L("탭을 열지 못했습니다: ", "Could not open the tab: ") + error, ct); return; }
        State(chat).Target = id;
        var tab = await FindAsync(id);
        var name = tab is null ? id : Label(tab);
        _host.Log(L($"[Telegram] {name} 탭을 열었습니다.", $"[Telegram] Opened tab {name}."), "i");
        await StartWatchAsync(chat, id, tab?.Name ?? id, "", -1, WatchKind.Startup, ct);
    }

    private async Task ScreenAsync(long chat, string? tabId, CancellationToken ct)
    {
        var tab = tabId is null ? await TargetAsync(chat, false, ct) : await FindAsync(tabId);
        if (tab is null) return;
        var screen = await _host.ScreenAsync(tab.Id);
        if (screen is null) { await Say(chat, L("화면을 읽지 못했습니다.", "Could not read the screen."), ct); return; }
        await SendScreenAsync(chat, $"🖥 {Label(tab)}", screen.Text, ct);
    }

    private async Task SendScreenAsync(long chat, string title, string screen, CancellationToken ct)
    {
        // HTML 로 감싸면 & < > 가 늘어난다 — 한도(4096)보다 넉넉히 줄여 둔다.
        var body = TelegramText.ScreenExcerpt(screen, 45, 3000);
        if (body.Length == 0) body = L("(빈 화면)", "(empty screen)");
        await _api.SendMessageAsync(chat, TelegramApi.Html(title) + "\n<pre>" + TelegramApi.Html(body) + "</pre>", html: true, ct: ct);
    }

    private async Task KeysAsync(long chat, string data, string done, CancellationToken ct)
    {
        var tab = await TargetAsync(chat, false, ct);
        if (tab is null) return;
        var ok = await _host.KeysAsync(tab.Id, data);
        await Say(chat, ok ? $"{done} ({Label(tab)})" : L("CLI 가 실행 중이 아닙니다.", "The CLI is not running."), ct);
    }

    private async Task StatusAsync(long chat, CancellationToken ct)
    {
        var tab = await TargetAsync(chat, true, ct);
        Watch? w = null;
        if (tab is not null) lock (_lock) _watches.TryGetValue(tab.Id, out w);
        var sb = new StringBuilder();
        sb.Append(L("봇: @", "Bot: @")).Append(BotName).Append('\n');
        sb.Append(L("고른 탭: ", "Selected tab: ")).Append(tab is null ? L("없음 — /tabs 로 고르세요", "none — choose with /tabs") : Label(tab));
        if (tab is not null)
        {
            sb.Append('\n').Append(tab.Cwd);
            sb.Append('\n').Append(tab.Running ? L("CLI 실행 중", "CLI running") : L("CLI 꺼짐", "CLI stopped"));
            if (w is not null) sb.Append('\n').Append(L($"지켜보는 중 · {Elapsed(w)}", $"Watching · {Elapsed(w)}"));
        }
        await Say(chat, sb.ToString(), ct);
    }

    // ── 질문 ────────────────────────────────────────────────────────────────

    private async Task AskAsync(long chat, string text, JsonObject msg, CancellationToken ct)
    {
        var tab = await TargetAsync(chat, false, ct);
        if (tab is null)
        {
            // 탭을 고르면 이어서 보낸다(SelectAsync).
            var st = State(chat);
            st.PendingText = text; st.PendingMessage = msg;
            return;
        }
        if (!tab.Running)
        {
            await Say(chat, L($"{Label(tab)} 의 CLI 가 실행 중이 아닙니다 — 앱에서 탭을 다시 열거나 /new 로 새 탭을 여세요.",
                              $"The CLI in {Label(tab)} is not running — reopen it in the app or open a new tab with /new."), ct);
            return;
        }

        var files = await DownloadAsync(chat, tab, msg, ct);
        if (files is null) return;
        if (text.Trim().Length == 0 && files.Count == 0) return;

        var baseline = -1;
        if (tab.TranscriptPath is { Length: > 0 } path && File.Exists(path))
            try { baseline = TranscriptReader.Read(tab.Profile, path).Items.Count; } catch { }

        var error = await _host.SendAsync(tab.Id, text, files);
        if (error is not null) { await Say(chat, L("보내지 못했습니다: ", "Could not send: ") + error, ct); return; }
        _host.Log(L($"[Telegram] {tab.Name} 탭에 질문을 보냈습니다.", $"[Telegram] Sent a question to {tab.Name}."), "i");
        await StartWatchAsync(chat, tab.Id, tab.Name, text, baseline, WatchKind.Turn, ct);
    }

    /// <summary>
    /// 사진·파일을 그 탭의 작업 폴더 안(<c>.agentclihub-attachments\telegram</c>)에 내려받는다 —
    /// 화면에 붙인 첨부와 같은 자리다. CLI 의 작업 폴더 밖에 두면 CLI 가 못 읽을 수 있다.
    /// 실패하면 이유를 말하고 null(질문도 안 보낸다 — 첨부 없는 질문은 엉뚱한 답을 부른다).
    /// </summary>
    private async Task<List<string>?> DownloadAsync(long chat, RemoteTab tab, JsonObject msg, CancellationToken ct)
    {
        var files = new List<string>();
        string? id = null, name = null;
        long size = 0;
        if (msg["document"] is JsonObject doc)
        {
            id = doc["file_id"]?.GetValue<string>();
            name = doc["file_name"]?.GetValue<string>() ?? "file";
            size = doc["file_size"]?.GetValue<long>() ?? 0;
        }
        else if (msg["photo"] is JsonArray photos && photos.Count > 0 && photos[^1] is JsonObject big)
        {
            id = big["file_id"]?.GetValue<string>();
            name = "photo.jpg";
            size = big["file_size"]?.GetValue<long>() ?? 0;
        }
        if (id is null) return files;
        if (size > TelegramApi.MaxDownload)
        {
            await Say(chat, L("파일이 20MB 를 넘어 받을 수 없습니다(텔레그램 봇 한도).", "The file is over 20 MB (Telegram bot limit)."), ct);
            return null;
        }
        try
        {
            var dir = Path.Combine(tab.Cwd, ".agentclihub-attachments", "telegram");
            Directory.CreateDirectory(dir);
            var safe = string.Concat(Path.GetFileName(name ?? "file").Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
            if (safe.Length == 0) safe = "file";
            var dest = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{safe}");
            await _api.DownloadAsync(id, dest, ct);
            files.Add(dest);
            return files;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Say(chat, L("첨부를 받지 못했습니다: ", "Could not download the attachment: ") + _api.Scrub(ex.Message), ct);
            return null;
        }
    }

    // ── 지켜보기 ────────────────────────────────────────────────────────────

    private enum WatchKind { Turn, Startup }

    private sealed class Watch(int seq, long chat, string tabId, string tabName, string sent, int baseline, WatchKind kind)
    {
        public int Seq { get; } = seq;
        public long Chat { get; } = chat;
        public string TabId { get; } = tabId;
        public string TabName { get; } = tabName;
        public string Sent { get; } = sent;
        public int Baseline { get; } = baseline;
        public WatchKind Kind { get; } = kind;
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public CancellationTokenSource Cts { get; } = new();
        public long ProgressMessage;
        public int Tools;
        public int ToolsShown;
        public DateTime LastEdit;
        public long PromptMessage;
        public string? PromptSignature;
        public int PromptSeq;
        public DateTime AnsweredUtc;
        public bool IdleNoticed;
        public DateTime LastWake;
        public bool Done;
    }

    private string Elapsed(Watch w)
    {
        var s = (int)(DateTime.UtcNow - w.StartedUtc).TotalSeconds;
        return s < 60 ? L($"{s}초", $"{s}s") : L($"{s / 60}분 {s % 60}초", $"{s / 60}m {s % 60}s");
    }

    private JsonObject WorkingKeyboard(string tabId) => TelegramApi.Keyboard([
        [new TelegramButton(L("화면", "Screen"), "scr:" + tabId), new TelegramButton(L("중단(Esc)", "Stop (Esc)"), "esc:" + tabId)],
    ]);

    private async Task StartWatchAsync(long chat, string tabId, string tabName, string sent, int baseline, WatchKind kind, CancellationToken ct)
    {
        var w = new Watch(Interlocked.Increment(ref _seq), chat, tabId, tabName, sent, baseline, kind);
        Watch? old;
        lock (_lock)
        {
            _watches.TryGetValue(tabId, out old);
            _watches[tabId] = w;
        }
        if (old is not null)
        {
            old.Cts.Cancel();
            if (old.ProgressMessage != 0)
                await Edit(old.Chat, old.ProgressMessage, L($"↪ {old.TabName} — 다음 질문으로 이어집니다.", $"↪ {old.TabName} — continued by the next question."), null, ct);
            if (old.PromptMessage != 0) await Edit(old.Chat, old.PromptMessage, L("↪ 다음 질문으로 넘어갔습니다.", "↪ Superseded by the next question."), null, ct);
        }

        var head = kind == WatchKind.Turn
            ? L($"⏳ {tabName} 에 보냈습니다 — 작업 중…", $"⏳ Sent to {tabName} — working…")
            : L($"⏳ {tabName} 탭을 여는 중…", $"⏳ Opening {tabName}…");
        w.ProgressMessage = await _api.SendMessageAsync(chat, head, WorkingKeyboard(tabId), ct: ct);
        w.LastEdit = DateTime.UtcNow;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, w.Cts.Token);
        _ = Task.Run(async () =>
        {
            try { await RunWatchAsync(w, linked.Token); }
            finally { linked.Dispose(); }
        });
    }

    private async Task RunWatchAsync(Watch w, CancellationToken ct)
    {
        string? fileSig = null;
        List<TranscriptItem>? turn = null;
        var typingAt = DateTime.MinValue;
        var limit = w.StartedUtc.AddHours(3);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1500, ct);
                var now = DateTime.UtcNow;
                var tab = await FindAsync(w.TabId);
                if (tab is null) { await FinishAsync(w, L($"⚠ {w.TabName} 탭이 닫혔습니다.", $"⚠ {w.TabName} was closed."), null, null, ct); return; }
                if (now > limit)
                {
                    await FinishAsync(w, L("⌛ 3시간이 지나 지켜보기를 멈춥니다. /screen 으로 화면을 확인하세요.",
                                           "⌛ Stopped watching after 3 hours. Check with /screen."), null, null, ct);
                    return;
                }

                // ① 기록 — 바뀌었을 때만 다시 읽는다(수 MB 일 수 있다).
                if (w.Kind == WatchKind.Turn && tab.TranscriptPath is { Length: > 0 } path && File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    var sig = path + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                    if (sig != fileSig)
                    {
                        fileSig = sig;
                        try { turn = TelegramText.TurnItems(TranscriptReader.Read(tab.Profile, path).Items, w.Sent, w.Baseline) ?? turn; }
                        catch { /* 쓰는 중이라 못 읽었으면 다음 주기에 */ }
                    }
                }
                if (turn is not null) await ProgressAsync(w, turn, ct);

                if (!tab.Running)
                {
                    var answer = turn is null ? null : TelegramText.FinalAnswer(turn);
                    await FinishAsync(w, L($"⚠ {w.TabName} 의 CLI 가 끝났습니다.", $"⚠ The CLI in {w.TabName} exited."),
                                      string.IsNullOrEmpty(answer) ? null : answer, null, ct);
                    return;
                }

                if (now - typingAt > TimeSpan.FromSeconds(5))
                {
                    typingAt = now;
                    try { await _api.SendChatActionAsync(w.Chat, "typing", ct); } catch { }
                }

                // ② 출력이 잠잠해질 때까지는 화면을 안 읽는다 — 그리는 도중의 화면은 반쪽이다.
                var quiet = now - tab.LastOutputUtc;
                if (quiet < TimeSpan.FromSeconds(1.2)) continue;

                var screen = await _host.ScreenAsync(tab.Id);
                if (screen is null) continue;

                // codex 는 입력이 없으면 새 출력을 멈추는 때가 있다(화면의 wakeIdleCodex 와 같은 문제).
                // 포커스 알림을 켜 둔 경우에만 FocusIn 을 보낸다 — 키 입력이 아니라 화면을 다시 그리게 할 뿐이다.
                if (screen.FocusMode && tab.Backend == CliProfile.Codex.Backend &&
                    quiet > TimeSpan.FromSeconds(2) && now - w.LastWake > TimeSpan.FromSeconds(2))
                {
                    w.LastWake = now;
                    await _host.KeysAsync(tab.Id, "\u001b[I");
                }

                // ③ 고르기 창(승인 등).
                var options = TelegramText.PromptOptions(screen.Text);
                if (options.Count >= 2)
                {
                    if (now - w.AnsweredUtc < TimeSpan.FromSeconds(2)) continue;   // 방금 고른 창이 아직 남아 있다
                    var sig = TelegramText.PromptSignature(screen.Text, options);
                    if (sig != w.PromptSignature) await PromptAsync(w, screen.Text, options, sig, ct);
                    continue;
                }
                if (w.PromptMessage != 0)
                {
                    await Edit(w.Chat, w.PromptMessage, L("✔ 화면에서 처리되었습니다.", "✔ Handled on screen."), null, ct);
                    w.PromptMessage = 0; w.PromptSignature = null;
                }

                if (TelegramText.IsWorking(screen.Text)) { w.IdleNoticed = false; continue; }
                if (quiet < TimeSpan.FromSeconds(2.5)) continue;

                // ④ 끝.
                if (w.Kind == WatchKind.Startup)
                {
                    await FinishAsync(w, L($"✅ {w.TabName} 탭이 준비되었습니다. 이제 글을 보내면 이 탭으로 갑니다.",
                                           $"✅ {w.TabName} is ready. Messages now go to this tab."), null, null, ct);
                    return;
                }
                if (turn is { Count: > 0 } && turn[^1].Kind == ItemKind.Assistant)
                {
                    await FinishAsync(w, null, TelegramText.FinalAnswer(turn), null, ct);
                    return;
                }
                if (turn is null && quiet > TimeSpan.FromSeconds(15))
                {
                    // 기록을 못 찾았다(아직 대화 번호를 못 잡았거나 지원하지 않는 기록). 화면으로 대신한다.
                    await FinishAsync(w, L("ℹ 대화 기록에서 답을 찾지 못해 화면을 보냅니다.", "ℹ No answer found in the transcript; sending the screen."),
                                      null, screen.Text, ct);
                    return;
                }
                if (quiet > TimeSpan.FromSeconds(20) && !w.IdleNoticed)
                {
                    w.IdleNoticed = true;
                    await SendScreenAsync(w.Chat, L($"⏸ {w.TabName} — 입력을 기다리는 것 같습니다:", $"⏸ {w.TabName} seems to be waiting for input:"), screen.Text, ct);
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _host.Log("[Telegram] " + L("지켜보기 오류: ", "Watch error: ") + _api.Scrub(ex.Message), "w");
            try { await Say(w.Chat, L("⚠ 진행을 지켜보다 오류가 났습니다. /screen 으로 화면을 확인하세요.", "⚠ Watching failed. Check with /screen."), CancellationToken.None); } catch { }
        }
        finally
        {
            lock (_lock)
                if (_watches.TryGetValue(w.TabId, out var cur) && ReferenceEquals(cur, w)) _watches.Remove(w.TabId);
        }
    }

    private async Task ProgressAsync(Watch w, List<TranscriptItem> turn, CancellationToken ct)
    {
        var tools = turn.Where(i => i.Kind == ItemKind.ToolCall).ToList();
        w.Tools = tools.Count;
        if (tools.Count <= w.ToolsShown || DateTime.UtcNow - w.LastEdit < TimeSpan.FromSeconds(3)) return;
        w.ToolsShown = tools.Count;
        w.LastEdit = DateTime.UtcNow;
        const int show = 8;
        var sb = new StringBuilder(L($"⏳ {w.TabName} — 작업 중 · 도구 {tools.Count}회 · {Elapsed(w)}",
                                     $"⏳ {w.TabName} — working · {tools.Count} tool calls · {Elapsed(w)}"));
        if (tools.Count > show) sb.Append('\n').Append(L($"… 앞의 {tools.Count - show}개 생략", $"… {tools.Count - show} earlier omitted"));
        foreach (var t in tools.TakeLast(show)) sb.Append('\n').Append(TelegramText.ToolLine(t));
        await Edit(w.Chat, w.ProgressMessage, TelegramText.Clip(sb.ToString(), TelegramApi.MaxText), WorkingKeyboard(w.TabId), ct);
    }

    private async Task PromptAsync(Watch w, string screen, List<PromptOption> options, string sig, CancellationToken ct)
    {
        if (w.PromptMessage != 0)
            await Edit(w.Chat, w.PromptMessage, L("↪ 다른 창으로 바뀌었습니다.", "↪ Replaced by another prompt."), null, ct);
        w.PromptSignature = sig;
        w.PromptSeq++;
        var rows = options.Select(o => new[]
        {
            new TelegramButton(TelegramText.Clip($"{o.Index}. {o.Label}", 60), $"opt:{w.TabId}:{w.Seq}:{w.PromptSeq}:{o.Index}"),
        }).ToList();
        rows.Add([new TelegramButton(L("취소(Esc)", "Cancel (Esc)"), "esc:" + w.TabId), new TelegramButton(L("화면", "Screen"), "scr:" + w.TabId)]);
        var excerpt = TelegramText.ScreenExcerpt(screen, 25, 2500);
        var text = TelegramApi.Html(L($"❓ {w.TabName} — 확인이 필요합니다", $"❓ {w.TabName} needs your choice")) +
                   "\n<pre>" + TelegramApi.Html(excerpt) + "</pre>";
        w.PromptMessage = await _api.SendMessageAsync(w.Chat, text, TelegramApi.Keyboard(rows), html: true, ct: ct);
        _host.Log(L($"[Telegram] {w.TabName} 의 확인 창을 텔레그램으로 보냈습니다.", $"[Telegram] Sent {w.TabName}'s prompt to Telegram."), "i");
    }

    private async Task FinishAsync(Watch w, string? note, string? answer, string? screen, CancellationToken ct)
    {
        if (w.Done) return;
        w.Done = true;
        var ok = note is null && answer is not null;
        if (w.ProgressMessage != 0)
        {
            var tools = w.Tools > 0 ? L($" · 도구 {w.Tools}회", $" · {w.Tools} tool calls") : "";
            await Edit(w.Chat, w.ProgressMessage, (ok ? "✅ " : "■ ") + w.TabName + " — " + Elapsed(w) + tools, null, ct);
        }
        if (w.PromptMessage != 0) { await Edit(w.Chat, w.PromptMessage, L("■ 끝났습니다.", "■ Finished."), null, ct); w.PromptMessage = 0; }
        if (note is not null) await Say(w.Chat, note, ct);
        if (!string.IsNullOrWhiteSpace(answer))
        {
            var parts = TelegramText.Split(answer, TelegramApi.MaxText - 80);
            for (var i = 0; i < parts.Count; i++)
            {
                var head = i == 0 ? $"💬 {w.TabName}" + (parts.Count > 1 ? $" (1/{parts.Count})" : "") + "\n\n"
                                  : $"({i + 1}/{parts.Count})\n";
                await Say(w.Chat, head + parts[i], ct);
            }
        }
        if (screen is not null) await SendScreenAsync(w.Chat, "🖥 " + w.TabName, screen, ct);
    }

    // ── 단추 ────────────────────────────────────────────────────────────────

    private async Task HandleCallbackAsync(JsonObject cb, CancellationToken ct)
    {
        var id = cb["id"]?.GetValue<string>() ?? "";
        var chat = cb["message"]?["chat"]?["id"]?.GetValue<long>() ?? 0;
        var type = cb["message"]?["chat"]?["type"]?.GetValue<string>() ?? "";
        var data = cb["data"]?.GetValue<string>() ?? "";
        if (chat == 0 || type != "private" || !IsAllowed(chat))
        {
            try { await _api.AnswerCallbackAsync(id, null, ct); } catch { }
            return;
        }

        string? answer = null;
        try
        {
            var parts = data.Split(':');
            switch (parts[0])
            {
                case "use" when parts.Length == 2:
                    if (await FindAsync(parts[1]) is { } tab) { answer = Label(tab); await SelectAsync(chat, tab, ct); }
                    else answer = L("이미 닫힌 탭입니다.", "That tab is closed.");
                    break;
                case "scr" when parts.Length == 2:
                    await ScreenAsync(chat, parts[1], ct);
                    break;
                case "esc" when parts.Length == 2:
                    answer = await _host.KeysAsync(parts[1], "\u001b") ? L("Esc 를 보냈습니다.", "Sent Esc.") : L("CLI 가 실행 중이 아닙니다.", "The CLI is not running.");
                    break;
                case "opt" when parts.Length == 5:
                    answer = await ChooseAsync(parts[1], parts[2], parts[3], parts[4], ct);
                    break;
                case "nf" when parts.Length == 2:
                    var st = State(chat);
                    if (int.TryParse(parts[1], out var i) && i >= 0 && i < st.Folders.Count)
                    {
                        st.NewFolder = st.Folders[i];
                        await CliMenuAsync(chat, st.NewFolder, ct);
                    }
                    else answer = L("목록이 지났습니다. /new 를 다시 보내세요.", "That list expired. Send /new again.");
                    break;
                case "nc" when parts.Length == 2:
                    var folder = State(chat).NewFolder;
                    if (folder is null) answer = L("폴더를 먼저 고르세요 — /new", "Choose a folder first — /new");
                    else { State(chat).NewFolder = null; await OpenAsync(chat, folder, parts[1], ct); }
                    break;
            }
        }
        finally
        {
            try { await _api.AnswerCallbackAsync(id, answer, ct); } catch { }
        }
    }

    /// <summary>
    /// 고르기 단추. **누른 그때 화면이 그 창 그대로인지 다시 본다** — 단추는 메시지에 남아 있어 한참 뒤에
    /// 누를 수도 있고, 그 사이 다른 창이 떴다면 같은 번호가 전혀 다른 뜻이다.
    /// </summary>
    private async Task<string> ChooseAsync(string tabId, string seqText, string promptText, string indexText, CancellationToken ct)
    {
        Watch? w;
        lock (_lock) _watches.TryGetValue(tabId, out w);
        if (w is null || w.Seq.ToString() != seqText || w.PromptSeq.ToString() != promptText || !int.TryParse(indexText, out var index))
            return L("지난 질문입니다 — /screen 으로 지금 화면을 보세요.", "That prompt has passed — check /screen.");

        var screen = await _host.ScreenAsync(tabId);
        List<PromptOption> options = screen is null ? [] : TelegramText.PromptOptions(screen.Text);
        if (screen is null || options.Count < 2 || TelegramText.PromptSignature(screen.Text, options) != w.PromptSignature)
            return L("화면이 바뀌었습니다 — /screen 으로 확인하세요.", "The screen changed — check /screen.");
        var choice = options.FirstOrDefault(o => o.Index == index);
        if (choice is null) return L("없는 항목입니다.", "No such option.");

        var move = TelegramText.MoveKeys(options, index);
        if (move.Length > 0)
        {
            await _host.KeysAsync(tabId, move);
            await Task.Delay(200, ct);
        }
        await _host.KeysAsync(tabId, "\r");
        w.AnsweredUtc = DateTime.UtcNow;
        w.PromptSignature = null;
        var msg = w.PromptMessage;
        w.PromptMessage = 0;
        if (msg != 0) await Edit(w.Chat, msg, L($"☑ {w.TabName} — 고름: {choice.Index}. {choice.Label}", $"☑ {w.TabName} — chose {choice.Index}. {choice.Label}"), null, ct);
        _host.Log(L($"[Telegram] {w.TabName} 의 확인 창에서 {choice.Index}번을 골랐습니다.", $"[Telegram] Chose option {choice.Index} in {w.TabName}."), "i");
        return $"{choice.Index}. {TelegramText.Clip(choice.Label, 150)}";
    }

    // ── 보내기 ──────────────────────────────────────────────────────────────

    private async Task Say(long chat, string text, CancellationToken ct)
    {
        foreach (var part in TelegramText.Split(text)) await _api.SendMessageAsync(chat, part, ct: ct);
    }

    /// <summary>
    /// 앱이 먼저 보내는 알림(예약 작업 결과 등). 받을 대화는 부르는 쪽이 준다 — 페어링한 대화만 넘겨야 한다.
    /// 한 대화가 실패해도 나머지에는 보낸다.
    /// </summary>
    public async Task NotifyAsync(IEnumerable<long> chats, string text)
    {
        if (!Connected || string.IsNullOrWhiteSpace(text)) return;
        foreach (var chat in chats.Distinct())
        {
            if (!_host.IsAllowed(chat)) continue;
            try { await Say(chat, text, _cts.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _host.Log("[Telegram] " + L("알림을 보내지 못했습니다: ", "Could not send a notification: ") + _api.Scrub(ex.Message), "w");
            }
        }
    }

    private async Task Edit(long chat, long message, string text, JsonObject? markup, CancellationToken ct)
    {
        if (message == 0) return;
        try { await _api.EditMessageAsync(chat, message, text, markup, ct: ct); }
        catch (TelegramException) { /* 지워진 메시지 등 — 진행 표시가 못 바뀔 뿐이다 */ }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        lock (_lock)
        {
            foreach (var w in _watches.Values) try { w.Cts.Cancel(); } catch { }
            _watches.Clear();
        }
        _api.Dispose();
    }
}
