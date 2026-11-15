using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentCLIHub;

/// <summary>원격에서 본 CLI 탭 하나. 로그인 탭은 들어오지 않는다.</summary>
public sealed record RemoteTab(string Id, string Name, string Cli, string Backend, string Cwd, bool Running,
                               string? TranscriptPath, CliProfile Profile, DateTime LastOutputUtc,
                               string Account = "", string AccountName = "");

/// <summary>터미널에 지금 보이는 글. <paramref name="FocusMode"/> = CLI 가 포커스 알림(?1004h)을 켜 두었는가.</summary>
public sealed record RemoteScreen(string Text, bool FocusMode);

/// <summary>새 탭 열기 · 대화 넘기기에서 고를 수 있는 CLI 계정 하나.</summary>
public sealed record RemoteAccountChoice(string Backend, string CliDisplay, string AccountId, string Label,
                                         string Email, bool Usable, string Note = "");

/// <summary>지난 대화([↻ 이어하기]) 항목 하나.</summary>
public sealed record RemoteSessionItem(string Backend, string CliDisplay, string SessionId, string Cwd,
                                       DateTime When, string Title, string AccountId = "", string AccountName = "");

/// <summary>계정별 사용량 한 줄.</summary>
public sealed record RemoteUsageItem(string CliDisplay, string Backend, string AccountLabel, string Email,
                                     IReadOnlyList<UsageWindow>? Windows, string Note = "");

/// <summary>예약 작업 요약 하나.</summary>
public sealed record RemoteScheduleItem(string Id, string Name, bool Enabled, string WhenSummary, string WhereSummary,
                                        bool Running, DateTime? NextLocal, string? LastStatus, DateTime? LastWhenLocal,
                                        string? LastAnswer, string? LastError);

/// <summary>CLI 권한 항목 하나.</summary>
public sealed record RemotePermItem(string Key, string Label, string Value, IReadOnlyList<string> Choices);

/// <summary>CLI 별 모델 · 추론 강도 · 권한 설정.</summary>
public sealed record RemoteCliSetting(string Backend, string Display, string Model, string Effort,
                                      IReadOnlyList<RemotePermItem> Permissions,
                                      IReadOnlyList<string> Models, IReadOnlyList<string> Efforts);

/// <summary>텔레그램 설정 메뉴에서 읽고 바꾸는 앱 상태.</summary>
public sealed record RemoteAppSettings(bool WindowShown, bool PetEnabled, bool CloseToTray, string StartInTray,
                                       bool RunAtStartup, bool ShowQuickBar, string UiLanguage,
                                       IReadOnlyList<RemoteCliSetting> Clis);

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
    Task<(string? TabId, string? Error)> OpenAsync(string folder, string backend, string account) => OpenAsync(folder, backend);
    Task<IReadOnlyList<string>> RecentFoldersAsync(int max);
    Task<IReadOnlyList<RemoteAccountChoice>> ListAccountsAsync(string backend) =>
        Task.FromResult<IReadOnlyList<RemoteAccountChoice>>([]);
    Task<string?> CloseTabAsync(string tabId) => Task.FromResult<string?>("Unsupported");
    Task<string?> RestartTabAsync(string tabId) => Task.FromResult<string?>("Unsupported");
    Task<string?> RenameTabAsync(string tabId, string newName) => Task.FromResult<string?>("Unsupported");
    Task<IReadOnlyList<RemoteAccountChoice>> ListSwitchTargetsAsync(string tabId) =>
        Task.FromResult<IReadOnlyList<RemoteAccountChoice>>([]);
    Task<string?> SwitchTabAsync(string tabId, string backend, string account) => Task.FromResult<string?>("Unsupported");
    Task<IReadOnlyList<RemoteSessionItem>> RecentSessionsAsync(int max, string? backend = null) =>
        Task.FromResult<IReadOnlyList<RemoteSessionItem>>([]);
    Task<(string? TabId, string? Error)> ResumeSessionAsync(string backend, string cwd, string sessionId, string account) =>
        Task.FromResult<(string?, string?)>((null, "Unsupported"));
    Task<IReadOnlyList<RemoteUsageItem>> GetUsageAsync() =>
        Task.FromResult<IReadOnlyList<RemoteUsageItem>>([]);
    Task<IReadOnlyList<RemoteScheduleItem>> GetSchedulesAsync() =>
        Task.FromResult<IReadOnlyList<RemoteScheduleItem>>([]);
    Task<string?> SetScheduleEnabledAsync(string jobId, bool enabled) => Task.FromResult<string?>("Unsupported");
    Task<string?> RunScheduleNowAsync(string jobId) => Task.FromResult<string?>("Unsupported");
    bool ShowQuickBar => true;
    void SetShowQuickBar(bool show) { }
    Task<RemoteAppSettings?> GetAppSettingsAsync() => Task.FromResult<RemoteAppSettings?>(null);
    Task<string?> ApplyAppSettingAsync(string key, string value) => Task.FromResult<string?>("Unsupported");
    /// <summary>페어링을 마친 대화인가. 매번 묻는다 — 설정 화면에서 지우면 바로 막혀야 한다.</summary>
    bool IsAllowed(long chat);
    void AddChat(long chat, string name);
    /// <summary>봇이 쓰는 말. 앱 화면 언어를 따른다.</summary>
    bool English { get; }
    void Log(string message, string level);
    void StateChanged();
}

/// <summary>
/// 텔레그램으로 이 앱의 CLI 탭에 묻고 답을 받으며, 탭·화면·사용량·예약 작업·설정을 원격 제어한다.
///
/// <b>무엇을 하나.</b> 봇이 받은 글을 고른 탭에 한 턴으로 넣고(화면 입력창과 같은 길 — 대괄호 붙여넣기 + Enter),
/// 그 턴이 끝날 때까지 지켜보다가 최종 답을 돌려보낸다. 진행 중에는 쓴 도구를 한 메시지에 고쳐 써 가며 보이고,
/// CLI 가 승인·고르기 창을 띄우면 그 화면과 항목 단추를 보낸다 — 누른 단추는 화살표 + Enter 로 들어간다.
/// 또한 하단 고정 빠른 메뉴 바(Reply Keyboard)와 인라인 대시보드(/menu)를 함께 제공해 휴대폰에서도 모든 기능을 단추로 다룬다.
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

    /// <summary>리모컨 키를 보낸 뒤 화면을 다시 읽기 전 기다리는 시간(ms). 테스트에서는 줄일 수 있다.</summary>
    public int RemoteKeyRefreshDelayMs { get; set; } = 250;

    /// <summary>지금 쓸 수 있는 페어링 코드. 없거나 지났으면 null.</summary>
    public string? PairingCode { get { lock (_lock) return _pairCode is not null && DateTime.UtcNow < _pairUntil ? _pairCode : null; } }
    public DateTime PairingUntilUtc { get { lock (_lock) return _pairUntil; } }

    /// <summary>페어링 코드 유효 시간.</summary>
    public static readonly TimeSpan PairingLife = TimeSpan.FromMinutes(10);

    /// <summary>앱이 뜨기 이만큼 전에 보낸 메시지까지는 받는다(시계 차이·막 켜는 중에 보낸 말).</summary>
    private static readonly TimeSpan StaleGrace = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 봇마다 마지막으로 처리한 update 번호. **껐다 켜도**(토큰 다시 저장 · 사용 끄고 켜기) 이미 처리한 메시지를
    /// 다시 받지 않게 프로세스 안에서 기억한다.
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

    private bool IsEnglish()
    {
        if (DateTime.UtcNow - _languageAt > TimeSpan.FromSeconds(5))
        {
            try { _english = _host.English; } catch { }
            _languageAt = DateTime.UtcNow;
        }
        return _english;
    }

    private string L(string ko, string en) => IsEnglish() ? en : ko;

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
        ("menu", L("전체 제어 대시보드 열기", "Open full control dashboard")),
        ("tabs", L("열린 CLI 탭 목록·전환·관리", "List, switch & manage open CLI tabs")),
        ("new", L("새 대화 탭 열기(폴더·CLI·계정)", "Open a new CLI tab (folder/CLI/account)")),
        ("resume", L("지난 대화 목록에서 이어하기", "Resume a recent conversation")),
        ("screen", L("지금 화면 보기 + 터미널 리모컨", "Show current screen + remote keys")),
        ("usage", L("Claude·GPT·Gemini 계정 한도 조회", "Check Claude/GPT/Gemini usage")),
        ("schedules", L("예약 작업 조회·실행·켜기/끄기", "View, run & toggle scheduled tasks")),
        ("settings", L("앱 설정·모델·창/트레이 제어", "App settings, models & window/tray")),
        ("bar", L("하단 고정 빠른 메뉴 바 켜기/숨기기", "Show or hide bottom quick menu bar")),
        ("use", L("질문을 받을 탭 번호 고르기", "Choose the active tab by number")),
        ("rename", L("현재 탭 이름 변경 (/rename 새이름)", "Rename current tab (/rename name)")),
        ("restart", L("현재 탭 CLI 다시 시작", "Restart CLI in current tab")),
        ("close", L("현재 탭 닫기", "Close current tab")),
        ("esc", L("Esc 보내기(중단·취소)", "Send Esc (interrupt/cancel)")),
        ("ctrlc", L("Ctrl+C 보내기(강제 중단)", "Send Ctrl+C")),
        ("enter", L("Enter 보내기", "Send Enter")),
        ("key", L("글자를 그대로 입력(Enter 없이)", "Type text without Enter")),
        ("status", L("지금 연결·탭 상태", "Current connection & tab status")),
        ("help", L("도움말", "Help")),
    ];

    private sealed record SettingChoice(string Key, string Value, string ReturnCode);

    private sealed class ChatState
    {
        public string? Target;
        public List<string> Folders = [];
        public string? NewFolder;
        public string? NewBackend;
        public List<RemoteAccountChoice> NewAccounts = [];
        public List<RemoteAccountChoice> SwitchTargets = [];
        public List<RemoteSessionItem> Sessions = [];
        public List<RemoteScheduleItem> Schedules = [];
        public List<SettingChoice> SettingChoices = [];
        public string? AwaitingRenameTabId;
        public bool QuickBarSynced;
        public bool QuickBarShown;
        public bool QuickBarEnglish;
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
        catch { return false; }
    }

    private JsonObject BuildQuickBarMarkup(bool show, bool english) => show
        ? TelegramApi.ReplyKeyboard(
            TelegramText.QuickBarRows(english),
            persistent: true,
            placeholder: english ? "Send a prompt or tap a menu button…" : "질문을 보내거나 아래 메뉴 단추를 누르세요…")
        : TelegramApi.RemoveReplyKeyboard();

    private JsonObject? QuickBarMarkupIfNeeded(long chat)
    {
        if (!IsAllowed(chat)) return null;
        bool want;
        try { want = _host.ShowQuickBar; } catch { want = true; }
        var eng = IsEnglish();
        var st = State(chat);
        lock (_lock)
        {
            if (st.QuickBarSynced && st.QuickBarShown == want && (!want || st.QuickBarEnglish == eng))
                return null;
            st.QuickBarSynced = true;
            st.QuickBarShown = want;
            st.QuickBarEnglish = eng;
        }
        return BuildQuickBarMarkup(want, eng);
    }

    private async Task EnsureQuickBarAsync(long chat, CancellationToken ct)
    {
        var mk = QuickBarMarkupIfNeeded(chat);
        if (mk is null) return;
        var shown = State(chat).QuickBarShown;
        var note = shown
            ? L("⌨ 하단 빠른 메뉴 바를 켰습니다. 숨기려면 /bar 를 보내세요.",
                "⌨ Bottom quick menu bar enabled. Send /bar to hide it.")
            : L("⌨ 하단 빠른 메뉴 바를 숨겼습니다. 다시 켜려면 /bar 를 보내세요.",
                "⌨ Bottom quick menu bar hidden. Send /bar to show it.");
        await _api.SendMessageAsync(chat, note, mk, ct: ct);
    }

    private async Task HandleUpdateAsync(JsonNode u, CancellationToken ct)
    {
        if (u["callback_query"] is JsonObject cb) { await HandleCallbackAsync(cb, ct); return; }
        if (u["message"] is not JsonObject msg) return;

        var chat = msg["chat"]?["id"]?.GetValue<long>() ?? 0;
        var type = msg["chat"]?["type"]?.GetValue<string>() ?? "";
        if (chat == 0 || type != "private") return;
        var from = msg["from"] as JsonObject;
        if (from?["is_bot"]?.GetValue<bool>() == true) return;

        var text = msg["text"]?.GetValue<string>() ?? msg["caption"]?.GetValue<string>() ?? "";
        var (cmd, args) = TelegramText.Command(text);
        if (cmd.Length == 0 && TelegramText.QuickBarAction(text) is { } qb)
        {
            cmd = qb;
            args = "";
        }

        if (!IsAllowed(chat))
        {
            if (cmd == "pair") { await PairAsync(chat, from, args, ct); return; }
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
            var st = State(chat);
            if (st.StaleNoticed) return;
            st.StaleNoticed = true;
            await Say(chat, L("앱이 꺼져 있는 동안 받은 메시지는 실행하지 않았습니다. 필요하면 다시 보내세요.",
                              "Messages received while the app was closed were not run. Send them again if needed."), ct);
            return;
        }

        if (cmd.Length > 0)
            State(chat).AwaitingRenameTabId = null;

        switch (cmd)
        {
            case "start":
            case "menu":
            case "m":
                await MenuAsync(chat, 0, ct);
                return;
            case "help":
                await HelpAsync(chat, 0, ct);
                return;
            case "pair":
                await Say(chat, L("이미 페어링되어 있습니다.", "This chat is already paired."), ct);
                return;
            case "bar":
            case "quickbar":
                await ToggleQuickBarAsync(chat, args, 0, ct);
                return;
            case "tabs":
                await TabsMenuAsync(chat, null, 0, ct);
                return;
            case "use":
                await UseAsync(chat, args, ct);
                return;
            case "new":
                await NewAsync(chat, args, 0, ct);
                return;
            case "resume":
            case "history":
                await ResumeMenuAsync(chat, ParseBackendFilter(args), 0, 0, ct);
                return;
            case "close":
                await CloseCmdAsync(chat, args, ct);
                return;
            case "restart":
                await RestartCmdAsync(chat, args, ct);
                return;
            case "rename":
                await RenameCmdAsync(chat, args, ct);
                return;
            case "switch":
            case "transfer":
                await SwitchCmdAsync(chat, args, ct);
                return;
            case "screen":
                await ScreenAsync(chat, null, 0, ct);
                return;
            case "usage":
            case "quota":
                await UsageAsync(chat, 0, ct);
                return;
            case "schedules":
            case "schedule":
            case "cron":
                await SchedulesMenuAsync(chat, 0, ct);
                return;
            case "settings":
            case "config":
                await SettingsMenuAsync(chat, 0, ct);
                return;
            case "esc":
                await KeysAsync(chat, "\u001b", L("Esc 를 보냈습니다.", "Sent Esc."), ct);
                return;
            case "ctrlc":
            case "interrupt":
                await KeysAsync(chat, "\u0003", L("Ctrl+C 를 보냈습니다.", "Sent Ctrl+C."), ct);
                return;
            case "enter":
                await KeysAsync(chat, "\r", L("Enter 를 보냈습니다.", "Sent Enter."), ct);
                return;
            case "key":
                if (args.Length == 0) { await Say(chat, L("보낼 글자를 적으세요 — 예: /key y", "Add the text to type — e.g. /key y"), ct); return; }
                await KeysAsync(chat, args, L("입력했습니다.", "Typed."), ct);
                return;
            case "status":
                await StatusAsync(chat, ct);
                return;
            case "cancel":
                await Say(chat, L("취소했습니다.", "Cancelled."), ct);
                return;
        }

        if (State(chat).AwaitingRenameTabId is { } renameTabId)
        {
            State(chat).AwaitingRenameTabId = null;
            var rawName = text.Trim();
            var nextName = rawName == "-" ? "" : rawName;
            var err = await _host.RenameTabAsync(renameTabId, nextName);
            if (err is not null)
                await Say(chat, L("이름을 바꾸지 못했습니다: ", "Could not rename tab: ") + err, ct);
            else
            {
                await Say(chat, nextName.Length == 0
                    ? L("탭 이름을 기본 폴더 이름으로 되돌렸습니다.", "Reset tab name to its folder name.")
                    : L($"탭 이름을 '{nextName}'(으)로 바꿨습니다.", $"Renamed tab to '{nextName}'."), ct);
                await TabDetailAsync(chat, renameTabId, 0, ct);
            }
            return;
        }

        // 나머지는 질문이다. 이 봇이 모르는 /명령(/compact 등)도 CLI 로 그대로 보낸다.
        await AskAsync(chat, TelegramText.Unescape(text), msg, ct);
    }

    private string Help() => L(
        "AgentCLIHub 텔레그램 원격\n\n" +
        "일반 글을 보내면 고른 탭의 CLI 에 질문으로 들어가고, 끝나면 답이 옵니다(사진·파일 첨부 가능).\n" +
        "아래 고정 빠른 메뉴 바 또는 /menu 대시보드에서 모든 기능을 단추로 조작할 수 있습니다.\n\n" +
        "• /menu — 전체 제어 대시보드\n" +
        "• /tabs — 열린 탭 전환 · 재시작 · 이름 변경 · 닫기 · 계정 전환\n" +
        "• /new [폴더] [claude|codex|antigravity] — 새 대화 열기(계정 선택 지원)\n" +
        "• /resume — 지난 대화 목록에서 골라 이어하기\n" +
        "• /screen — 지금 화면 보기 + 터미널 조작 리모컨\n" +
        "• /usage — Claude · GPT · Gemini 계정 한도 잔량 조회\n" +
        "• /schedules — 예약 작업 목록 · 지금 실행 · 켜기/끄기 · 결과 보기\n" +
        "• /settings — 기본 모델·추론 강도·권한 변경, 창 열기/트레이 숨기기, 펫 토글\n" +
        "• /bar [on|off] — 하단 고정 빠른 메뉴 바 켜기/숨기기\n" +
        "• /esc · /ctrlc · /enter · /key 글자 — 터미널 키 직접 전송\n\n" +
        "그 밖의 /명령(/compact 등)은 CLI 로 그대로 갑니다. 이 봇의 명령과 이름이 같은 CLI 명령은 //help 처럼 / 를 두 번 쓰세요.",
        "AgentCLIHub Telegram remote\n\n" +
        "Plain messages go to the selected tab's CLI as a question; the answer comes back when it finishes (photos and files supported).\n" +
        "Use the persistent bottom quick bar or /menu dashboard to control everything with buttons.\n\n" +
        "• /menu — full control dashboard\n" +
        "• /tabs — switch, restart, rename, close, or transfer open tabs\n" +
        "• /new [folder] [claude|codex|antigravity] — open a new tab (with account picker)\n" +
        "• /resume — resume from recent conversations\n" +
        "• /screen — view current terminal screen + remote control keys\n" +
        "• /usage — check Claude / GPT / Gemini account quota\n" +
        "• /schedules — view, run now, toggle, and inspect scheduled tasks\n" +
        "• /settings — change default models/effort/permissions, window/tray, pet\n" +
        "• /bar [on|off] — show or hide the bottom quick menu bar\n" +
        "• /esc · /ctrlc · /enter · /key text — send terminal keys directly\n\n" +
        "Other /commands (/compact, …) go straight to the CLI. Use two slashes (e.g. //help) for a CLI command that shares a bot command name.");

    private async Task HelpAsync(long chat, long editMsg, CancellationToken ct)
    {
        await EnsureQuickBarAsync(chat, ct);
        var rows = new[]
        {
            new[] { new TelegramButton(L("📋 전체 메뉴 열기", "📋 Open main menu"), "m:main") },
        };
        await DeliverAsync(chat, editMsg, Help(), TelegramApi.Keyboard(rows), html: false, ct);
    }

    // ── 대시보드 (/menu) · 빠른 메뉴 바 (/bar) ───────────────────────────────

    private async Task MenuAsync(long chat, long editMsg, CancellationToken ct)
    {
        if (editMsg == 0) await EnsureQuickBarAsync(chat, ct);

        var tabs = await _host.TabsAsync();
        var st = State(chat);
        RemoteTab? active = null;
        if (st.Target is { } tid) active = tabs.FirstOrDefault(t => t.Id == tid);
        if (active is null && tabs.Count == 1) { active = tabs[0]; st.Target = active.Id; }

        var sb = new StringBuilder();
        sb.Append(L("📋 AgentCLIHub 원격 메뉴", "📋 AgentCLIHub Remote Menu")).Append('\n');
        sb.Append(L($"열린 CLI 탭: {tabs.Count}개", $"Open CLI tabs: {tabs.Count}")).Append('\n');
        if (active is not null)
        {
            var runState = active.Running ? L("🟢 실행 중", "🟢 running") : L("⚪ 꺼짐", "⚪ stopped");
            sb.Append(L("현재 탭: ", "Active tab: ")).Append(Label(active)).Append(" · ").Append(runState)
              .Append("\n📁 ").Append(active.Cwd);
        }
        else if (tabs.Count > 0)
        {
            sb.Append(L("현재 탭: 미선택 — [🗂 탭 관리] 에서 고르세요", "Active tab: none — pick one in [🗂 Tabs]"));
        }
        else
        {
            sb.Append(L("현재 탭: 없음 — [➕ 새 대화] 또는 [↻ 이어하기] 로 여세요", "Active tab: none — open with [➕ New] or [↻ Resume]"));
        }

        bool barOn;
        try { barOn = _host.ShowQuickBar; } catch { barOn = true; }
        var barLabel = barOn ? L("⌨ 빠른바 숨기기", "⌨ Hide quick bar") : L("⌨ 빠른바 켜기", "⌨ Show quick bar");

        var rows = new List<TelegramButton[]>
        {
            new[]
            {
                new TelegramButton(L($"🗂 탭 관리 ({tabs.Count})", $"🗂 Tabs ({tabs.Count})"), "m:tabs"),
                new TelegramButton(L("➕ 새 대화", "➕ New chat"), "m:new"),
                new TelegramButton(L("↻ 이어하기", "↻ Resume"), "m:res:all:0"),
            },
            new[]
            {
                new TelegramButton(L("🖥 화면·리모컨", "🖥 Screen & keys"), "m:scr"),
                new TelegramButton(L("📊 사용량", "📊 Usage"), "m:use"),
                new TelegramButton(L("⏰ 예약 작업", "⏰ Schedules"), "m:sch"),
            },
            new[]
            {
                new TelegramButton(L("⚙ 설정·창 제어", "⚙ Settings & window"), "m:cfg"),
                new TelegramButton(barLabel, "m:bar"),
                new TelegramButton(L("❓ 도움말", "❓ Help"), "m:hlp"),
            },
        };
        await DeliverAsync(chat, editMsg, sb.ToString(), TelegramApi.Keyboard(rows), html: false, ct);
    }

    private async Task<string> ToggleQuickBarAsync(long chat, string args, long editMsg, CancellationToken ct)
    {
        bool cur;
        try { cur = _host.ShowQuickBar; } catch { cur = true; }
        var a = args.Trim().ToLowerInvariant();
        var next = a switch
        {
            "on" or "true" or "1" or "show" or "켜기" or "표시" => true,
            "off" or "false" or "0" or "hide" or "숨기기" or "숨김" => false,
            _ => !cur,
        };
        _host.SetShowQuickBar(next);
        var eng = IsEnglish();
        var st = State(chat);
        lock (_lock)
        {
            st.QuickBarSynced = true;
            st.QuickBarShown = next;
            st.QuickBarEnglish = eng;
        }
        var note = next
            ? L("⌨ 하단 빠른 메뉴 바를 켰습니다.", "⌨ Bottom quick menu bar enabled.")
            : L("⌨ 하단 빠른 메뉴 바를 숨겼습니다. 다시 켜려면 /bar 를 보내세요.", "⌨ Bottom quick menu bar hidden. Send /bar to show it.");
        await _api.SendMessageAsync(chat, note, BuildQuickBarMarkup(next, eng), ct: ct);
        if (editMsg > 0) await MenuAsync(chat, editMsg, ct);
        return next ? L("빠른바 켜짐", "Quick bar on") : L("빠른바 숨김", "Quick bar hidden");
    }

    // ── 페어링 ──────────────────────────────────────────────────────────────

    private async Task PairAsync(long chat, JsonObject? from, string code, CancellationToken ct)
    {
        bool ok;
        lock (_lock)
        {
            var live = _pairCode is not null && DateTime.UtcNow < _pairUntil;
            ok = live && TelegramText.SameCode(_pairCode, code);
            if (ok) _pairCode = null;
            else if (live && ++_pairMisses >= 5) _pairCode = null;
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
        await MenuAsync(chat, 0, ct);
    }

    // ── 탭 관리 (/tabs · 상세 · 재시작 · 이름 · 전환 · 닫기) ────────────────

    private static string Label(RemoteTab t) =>
        t.AccountName.Length > 0 ? $"{t.Name} · {t.Cli}({t.AccountName})" : $"{t.Name} · {t.Cli}";

    private async Task<RemoteTab?> FindAsync(string? id) =>
        id is null ? null : (await _host.TabsAsync()).FirstOrDefault(t => t.Id == id);

    private async Task<RemoteTab?> ResolveTabArgAsync(long chat, string args, CancellationToken ct)
    {
        var key = args.Trim();
        if (key.Length == 0) return await TargetAsync(chat, false, ct);
        var tabs = await _host.TabsAsync();
        var hit = tabs.FirstOrDefault(t => string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase));
        if (hit is null && int.TryParse(key, out var n) && n >= 1 && n <= tabs.Count) hit = tabs[n - 1];
        if (hit is null) await TabsMenuAsync(chat, L("탭 번호를 확인하세요:", "Check the tab number:"), 0, ct);
        return hit;
    }

    private async Task<RemoteTab?> TargetAsync(long chat, bool quiet, CancellationToken ct)
    {
        var tabs = await _host.TabsAsync();
        var st = State(chat);
        if (st.Target is { } id && tabs.FirstOrDefault(t => t.Id == id) is { } hit) return hit;
        if (tabs.Count == 1) { st.Target = tabs[0].Id; return tabs[0]; }
        if (quiet) return null;
        if (tabs.Count == 0)
        {
            var rows = new[]
            {
                new[]
                {
                    new TelegramButton(L("➕ 새 대화", "➕ New chat"), "m:new"),
                    new TelegramButton(L("↻ 이어하기", "↻ Resume"), "m:res:all:0"),
                    new TelegramButton(L("📋 메뉴", "📋 Menu"), "m:main"),
                },
            };
            await _api.SendMessageAsync(chat,
                L("열린 CLI 탭이 없습니다. 새 대화를 열거나 지난 대화를 이어가세요.",
                  "No CLI tab is open. Open a new chat or resume a recent one."),
                TelegramApi.Keyboard(rows), ct: ct);
        }
        else
        {
            await TabsMenuAsync(chat, L("어느 탭으로 보낼까요?", "Which tab should get this?"), 0, ct);
        }
        return null;
    }

    private async Task TabsMenuAsync(long chat, string? title, long editMsg, CancellationToken ct)
    {
        if (editMsg == 0) await EnsureQuickBarAsync(chat, ct);
        var tabs = await _host.TabsAsync();
        if (tabs.Count == 0)
        {
            var emptyRows = new[]
            {
                new[]
                {
                    new TelegramButton(L("➕ 새 대화 열기", "➕ Open new chat"), "m:new"),
                    new TelegramButton(L("↻ 지난 대화 이어하기", "↻ Resume chat"), "m:res:all:0"),
                },
                new[] { new TelegramButton(L("⬅ 메뉴", "⬅ Menu"), "m:main") },
            };
            await DeliverAsync(chat, editMsg,
                L("열린 CLI 탭이 없습니다.", "No CLI tabs are open."),
                TelegramApi.Keyboard(emptyRows), html: false, ct);
            return;
        }
        var target = State(chat).Target;
        if (target is null && tabs.Count == 1) State(chat).Target = target = tabs[0].Id;

        var sb = new StringBuilder(title ?? L("🗂 열린 CLI 탭 (탭 선택 또는 ⚙ 관리):", "🗂 Open CLI tabs (select or ⚙ manage):")).Append('\n');
        var rows = new List<TelegramButton[]>();
        for (var i = 0; i < tabs.Count; i++)
        {
            var t = tabs[i];
            var mark = t.Id == target ? "▶ " : "";
            var state = t.Running ? L("🟢 실행 중", "🟢 running") : L("⚪ 꺼짐", "⚪ stopped");
            sb.Append('\n').Append(i + 1).Append(") ").Append(mark).Append(Label(t)).Append(" · ").Append(state)
              .Append("\n   ").Append(t.Cwd);
            rows.Add([
                new TelegramButton(TelegramText.Clip($"{i + 1}) {mark}{Label(t)}", 48), "use:" + t.Id),
                new TelegramButton(L("⚙ 관리", "⚙ Manage"), "tm:" + t.Id),
            ]);
        }
        rows.Add([
            new TelegramButton(L("➕ 새 대화", "➕ New"), "m:new"),
            new TelegramButton(L("↻ 이어하기", "↻ Resume"), "m:res:all:0"),
            new TelegramButton(L("⬅ 메뉴", "⬅ Menu"), "m:main"),
        ]);
        await DeliverAsync(chat, editMsg, sb.ToString(), TelegramApi.Keyboard(rows), html: false, ct);
    }

    private async Task TabDetailAsync(long chat, string tabId, long editMsg, CancellationToken ct)
    {
        var tab = await FindAsync(tabId);
        if (tab is null)
        {
            await TabsMenuAsync(chat, L("이미 닫힌 탭입니다. 남은 탭 목록:", "That tab was closed. Open tabs:"), editMsg, ct);
            return;
        }
        var isTarget = State(chat).Target == tab.Id;
        var sb = new StringBuilder();
        sb.Append("🗂 ").Append(Label(tab)).Append('\n');
        sb.Append(L("상태: ", "Status: ")).Append(tab.Running ? L("🟢 실행 중", "🟢 Running") : L("⚪ 꺼짐", "⚪ Stopped"));
        if (isTarget) sb.Append(L(" · ▶ 현재 선택된 탭", " · ▶ Active tab"));
        sb.Append("\n📁 ").Append(tab.Cwd);

        var rows = new List<TelegramButton[]>
        {
            new[]
            {
                new TelegramButton(isTarget ? L("✔ 현재 질문 탭", "✔ Active tab") : L("▶ 이 탭으로 선택", "▶ Use this tab"), "use:" + tab.Id),
                new TelegramButton(L("🖥 화면·리모컨", "🖥 Screen & keys"), "scr:" + tab.Id),
            },
            new[]
            {
                new TelegramButton(L("🔄 CLI 다시 시작", "🔄 Restart CLI"), "trst:" + tab.Id),
                new TelegramButton(L("🔀 다른 CLI·계정으로", "🔀 Switch CLI/account"), "tsw:" + tab.Id),
            },
            new[]
            {
                new TelegramButton(L("✏ 이름 변경", "✏ Rename"), "trnm:" + tab.Id),
                new TelegramButton(L("🗑 탭 닫기", "🗑 Close tab"), "tcls:" + tab.Id),
            },
            new[]
            {
                new TelegramButton(L("⬅ 탭 목록", "⬅ Tabs"), "m:tabs"),
                new TelegramButton(L("📋 메뉴", "📋 Menu"), "m:main"),
            },
        };
        await DeliverAsync(chat, editMsg, sb.ToString(), TelegramApi.Keyboard(rows), html: false, ct);
    }

    private async Task UseAsync(long chat, string args, CancellationToken ct)
    {
        var tabs = await _host.TabsAsync();
        var key = args.Trim();
        var hit = tabs.FirstOrDefault(t => string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase));
        if (hit is null && int.TryParse(key, out var n) && n >= 1 && n <= tabs.Count) hit = tabs[n - 1];
        if (hit is null) { await TabsMenuAsync(chat, L("고를 탭 번호를 확인하세요:", "Check the tab number:"), 0, ct); return; }
        await SelectAsync(chat, hit, ct);
    }

    private async Task SelectAsync(long chat, RemoteTab tab, CancellationToken ct)
    {
        var st = State(chat);
        st.Target = tab.Id;
        var rows = new[]
        {
            new[]
            {
                new TelegramButton(L("🖥 화면·리모컨", "🖥 Screen & keys"), "scr:" + tab.Id),
                new TelegramButton(L("⚙ 탭 관리", "⚙ Manage tab"), "tm:" + tab.Id),
                new TelegramButton(L("📋 메뉴", "📋 Menu"), "m:main"),
            },
        };
        await _api.SendMessageAsync(chat,
            L($"▶ 이제 질문은 {Label(tab)} 탭으로 갑니다.\n{tab.Cwd}",
              $"▶ Questions now go to {Label(tab)}.\n{tab.Cwd}"),
            TelegramApi.Keyboard(rows), ct: ct);

        var text = st.PendingText;
        var msg = st.PendingMessage;
        st.PendingText = null; st.PendingMessage = null;
        if (text is not null && msg is not null) await AskAsync(chat, text, msg, ct);
    }

    private async Task CloseCmdAsync(long chat, string args, CancellationToken ct)
    {
        var tab = await ResolveTabArgAsync(chat, args, ct);
        if (tab is null) return;
        await CloseConfirmAsync(chat, tab.Id, 0, ct);
    }

    private async Task CloseConfirmAsync(long chat, string tabId, long editMsg, CancellationToken ct)
    {
        var tab = await FindAsync(tabId);
        if (tab is null) { await TabsMenuAsync(chat, L("이미 닫힌 탭입니다.", "That tab is already closed."), editMsg, ct); return; }
        var rows = new[]
        {
            new[]
            {
                new TelegramButton(L("🗑 예, 탭 닫기", "🗑 Yes, close tab"), "tclok:" + tab.Id),
                new TelegramButton(L("취소", "Cancel"), "tm:" + tab.Id),
            },
        };
        await DeliverAsync(chat, editMsg,
            L($"⚠ '{Label(tab)}' 탭을 닫을까요?\n실행 중인 CLI 프로세스가 종료됩니다(대화 기록은 이어하기에 남습니다).",
              $"⚠ Close '{Label(tab)}'?\nThe running CLI process will stop (conversation history stays in Resume)."),
            TelegramApi.Keyboard(rows), html: false, ct);
    }

    private async Task<string> DoCloseTabAsync(long chat, string tabId, long editMsg, CancellationToken ct)
    {
        var tab = await FindAsync(tabId);
        var label = tab is null ? tabId : Label(tab);
        var err = await _host.CloseTabAsync(tabId);
        if (err is not null)
        {
            await Say(chat, L("탭을 닫지 못했습니다: ", "Could not close tab: ") + err, ct);
            return err;
        }
        var st = State(chat);
        if (st.Target == tabId) st.Target = null;
        await TabsMenuAsync(chat, L($"🗑 '{label}' 탭을 닫았습니다.", $"🗑 Closed '{label}'."), editMsg, ct);
        return L("탭을 닫았습니다.", "Closed tab.");
    }

    private async Task RestartCmdAsync(long chat, string args, CancellationToken ct)
    {
        var tab = await ResolveTabArgAsync(chat, args, ct);
        if (tab is null) return;
        await DoRestartTabAsync(chat, tab.Id, 0, ct);
    }

    private async Task<string> DoRestartTabAsync(long chat, string tabId, long editMsg, CancellationToken ct)
    {
        var tab = await FindAsync(tabId);
        if (tab is null) return L("탭이 없습니다.", "No such tab.");
        var err = await _host.RestartTabAsync(tabId);
        if (err is not null)
        {
            await Say(chat, L("다시 시작하지 못했습니다: ", "Could not restart: ") + err, ct);
            return err;
        }
        State(chat).Target = tabId;
        if (editMsg > 0)
            await Edit(chat, editMsg, L($"🔄 '{Label(tab)}' 탭을 다시 시작합니다…", $"🔄 Restarting '{Label(tab)}'…"), null, ct);
        await StartWatchAsync(chat, tabId, tab.Name, "", -1, WatchKind.Startup, ct);
        return L("다시 시작합니다.", "Restarting.");
    }

    private async Task RenameCmdAsync(long chat, string args, CancellationToken ct)
    {
        var tab = await TargetAsync(chat, false, ct);
        if (tab is null) return;
        if (args.Trim().Length == 0)
        {
            await BeginRenameAsync(chat, tab.Id, 0, ct);
            return;
        }
        var next = args.Trim() == "-" ? "" : args.Trim();
        var err = await _host.RenameTabAsync(tab.Id, next);
        if (err is not null) await Say(chat, L("이름을 바꾸지 못했습니다: ", "Could not rename: ") + err, ct);
        else await Say(chat, next.Length == 0
            ? L("탭 이름을 기본 폴더 이름으로 되돌렸습니다.", "Reset tab name to its folder name.")
            : L($"탭 이름을 '{next}'(으)로 바꿨습니다.", $"Renamed tab to '{next}'."), ct);
    }

    private async Task BeginRenameAsync(long chat, string tabId, long editMsg, CancellationToken ct)
    {
        var tab = await FindAsync(tabId);
        if (tab is null) return;
        State(chat).AwaitingRenameTabId = tabId;
        var rows = new[]
        {
            new[] { new TelegramButton(L("취소", "Cancel"), "trnc:" + tabId) },
        };
        await DeliverAsync(chat, editMsg,
            L($"✏ '{Label(tab)}' 의 새 탭 이름을 메시지로 보내세요.\n(원래 폴더 이름으로 되돌리려면 '-' 전송)",
              $"✏ Send the new name for '{Label(tab)}'.\n(Send '-' to reset to the folder name)"),
            TelegramApi.Keyboard(rows), html: false, ct);
    }

    private async Task SwitchCmdAsync(long chat, string args, CancellationToken ct)
    {
        var tab = await ResolveTabArgAsync(chat, args, ct);
        if (tab is null) return;
        await SwitchMenuAsync(chat, tab.Id, 0, ct);
    }

    private async Task SwitchMenuAsync(long chat, string tabId, long editMsg, CancellationToken ct)
    {
        var tab = await FindAsync(tabId);
        if (tab is null) { await TabsMenuAsync(chat, null, editMsg, ct); return; }
        var targets = (await _host.ListSwitchTargetsAsync(tabId)).Where(t => t.Usable).ToList();
        var st = State(chat);
        st.SwitchTargets = targets;
        if (targets.Count == 0)
        {
            var back = new[] { new[] { new TelegramButton(L("⬅ 탭 관리로", "⬅ Back to tab"), "tm:" + tabId) } };
            await DeliverAsync(chat, editMsg,
                L($"'{Label(tab)}' 에서 넘길 수 있는 다른 로그인된 CLI·계정이 없습니다.\n앱의 [⚙ 설정 → 계정] 에서 계정을 추가·로그인하세요.",
                  $"No other signed-in CLI/account is available for '{Label(tab)}'.\nAdd or sign in to an account in [⚙ Settings → Accounts]."),
                TelegramApi.Keyboard(back), html: false, ct);
            return;
        }
        var rows = new List<TelegramButton[]>();
        for (var i = 0; i < targets.Count; i++)
        {
            var a = targets[i];
            var who = a.Email.Length > 0 ? $"{a.CliDisplay} · {a.Label} ({a.Email})" : $"{a.CliDisplay} · {a.Label}";
            rows.Add([new TelegramButton(TelegramText.Clip(who, 58), $"tswi:{tabId}:{i}")]);
        }
        rows.Add([new TelegramButton(L("⬅ 취소", "⬅ Cancel"), "tm:" + tabId)]);
        await DeliverAsync(chat, editMsg,
            L($"🔀 '{Label(tab)}' 의 대화를 어느 CLI·계정으로 이어갈까요?",
              $"🔀 Continue '{Label(tab)}' with which CLI / account?"),
            TelegramApi.Keyboard(rows), html: false, ct);
    }

    private async Task<string> DoSwitchTabAsync(long chat, string tabId, int idx, long editMsg, CancellationToken ct)
    {
        var st = State(chat);
        if (idx < 0 || idx >= st.SwitchTargets.Count)
            return L("목록이 지났습니다. 다시 고르세요.", "List expired. Pick again.");
        var choice = st.SwitchTargets[idx];
        if (editMsg > 0)
            await Edit(chat, editMsg,
                L($"⏳ {choice.CliDisplay} ({choice.Label}) 로 대화를 넘기는 중…",
                  $"⏳ Transferring conversation to {choice.CliDisplay} ({choice.Label})…"), null, ct);
        var err = await _host.SwitchTabAsync(tabId, choice.Backend, choice.AccountId);
        if (err is not null)
        {
            await Say(chat, L("대화를 넘기지 못했습니다: ", "Could not transfer conversation: ") + err, ct);
            return err;
        }
        st.Target = tabId;
        var tab = await FindAsync(tabId);
        await StartWatchAsync(chat, tabId, tab?.Name ?? tabId, "", -1, WatchKind.Startup, ct);
        return $"{choice.CliDisplay} ({choice.Label})";
    }

    // ── 새 대화 (/new · 폴더 · CLI · 계정) ───────────────────────────────────

    private async Task NewAsync(long chat, string args, long editMsg, CancellationToken ct)
    {
        if (editMsg == 0) await EnsureQuickBarAsync(chat, ct);
        var (folder, backend) = TelegramText.NewArgs(args);
        if (folder.Length == 0)
        {
            var folders = await _host.RecentFoldersAsync(8);
            var st = State(chat);
            st.Folders = folders.ToList();
            if (folders.Count == 0)
            {
                var back = new[] { new[] { new TelegramButton(L("⬅ 메뉴", "⬅ Menu"), "m:main") } };
                await DeliverAsync(chat, editMsg,
                    L("최근 폴더가 없습니다. /new D:\\작업폴더 claude 처럼 경로를 적으세요.",
                      "No recent folders. Give a path, e.g. /new D:\\work claude"),
                    TelegramApi.Keyboard(back), html: false, ct);
                return;
            }
            var rows = folders.Select((f, i) => new[] { new TelegramButton("📁 " + TelegramText.Clip(f, 56), "nf:" + i) }).ToList();
            rows.Add([new TelegramButton(L("⬅ 메뉴", "⬅ Menu"), "m:main")]);
            await DeliverAsync(chat, editMsg,
                L("➕ 어느 폴더에서 새 대화를 열까요?\n(직접 입력: /new 경로 [claude|codex|antigravity])",
                  "➕ Open a new chat in which folder?\n(Custom path: /new path [claude|codex|antigravity])"),
                TelegramApi.Keyboard(rows), html: false, ct);
            return;
        }
        if (backend.Length == 0) { State(chat).NewFolder = folder; await CliMenuAsync(chat, folder, editMsg, ct); return; }
        await PickAccountOrOpenAsync(chat, folder, backend, editMsg, ct);
    }

    private async Task CliMenuAsync(long chat, string folder, long editMsg, CancellationToken ct)
    {
        var rows = new[]
        {
            new[]
            {
                new TelegramButton("Claude Code", "nc:" + CliProfile.Claude.Backend),
                new TelegramButton("Codex", "nc:" + CliProfile.Codex.Backend),
                new TelegramButton("Antigravity", "nc:" + CliProfile.Antigravity.Backend),
            },
            new[]
            {
                new TelegramButton(L("⬅ 폴더 다시 고르기", "⬅ Folders"), "m:new"),
                new TelegramButton(L("📋 메뉴", "📋 Menu"), "m:main"),
            },
        };
        await DeliverAsync(chat, editMsg,
            L($"📁 {folder}\n어느 CLI 로 열까요?", $"📁 {folder}\nWhich CLI?"),
            TelegramApi.Keyboard(rows), html: false, ct);
    }

    private async Task PickAccountOrOpenAsync(long chat, string folder, string backend, long editMsg, CancellationToken ct)
    {
        var accounts = (await _host.ListAccountsAsync(backend)).Where(a => a.Usable).ToList();
        if (accounts.Count <= 1)
        {
            var accId = accounts.Count == 1 ? accounts[0].AccountId : "";
            await OpenAsync(chat, folder, backend, accId, editMsg, ct);
            return;
        }
        var st = State(chat);
        st.NewFolder = folder;
        st.NewBackend = backend;
        st.NewAccounts = accounts;
        var rows = new List<TelegramButton[]>();
        for (var i = 0; i < accounts.Count; i++)
        {
            var a = accounts[i];
            var txt = a.Email.Length > 0 ? $"{a.Label} ({a.Email})" : a.Label;
            rows.Add([new TelegramButton("👤 " + TelegramText.Clip(txt, 54), "na:" + i)]);
        }
        rows.Add([new TelegramButton(L("⬅ CLI 다시 고르기", "⬅ Back"), "m:new")]);
        var cliName = CliProfile.For(backend)?.Display ?? backend;
        await DeliverAsync(chat, editMsg,
            L($"📁 {folder}\n🤖 {cliName} — 어느 계정으로 열까요?",
              $"📁 {folder}\n🤖 {cliName} — Which account?"),
            TelegramApi.Keyboard(rows), html: false, ct);
    }

    private async Task OpenAsync(long chat, string folder, string backend, string account, long editMsg, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
        {
            await Say(chat, L($"폴더가 없습니다 — {folder}\n이 PC 의 전체 경로를 적으세요(예: D:\\Src\\프로젝트).",
                              $"Folder not found — {folder}\nUse a full path on this PC (e.g. D:\\Src\\project)."), ct);
            return;
        }
        var (id, error) = await _host.OpenAsync(Path.GetFullPath(folder), backend, account);
        if (id is null) { await Say(chat, L("탭을 열지 못했습니다: ", "Could not open the tab: ") + error, ct); return; }
        State(chat).Target = id;
        var tab = await FindAsync(id);
        var name = tab is null ? id : Label(tab);
        if (editMsg > 0)
            await Edit(chat, editMsg, L($"➕ {name} 탭을 열었습니다.", $"➕ Opened {name}."), null, ct);
        _host.Log(L($"[Telegram] {name} 탭을 열었습니다.", $"[Telegram] Opened tab {name}."), "i");
        await StartWatchAsync(chat, id, tab?.Name ?? id, "", -1, WatchKind.Startup, ct);
    }

    // ── 지난 대화 이어하기 (/resume) ─────────────────────────────────────────

    private static string ParseBackendFilter(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "claude" or "cc" or "claude_code" => "cc",
        "codex" or "cx" or "codex_cli" => "cx",
        "antigravity" or "ag" or "agy" or "antigravity_cli" => "ag",
        _ => "all",
    };

    private static string? FilterToBackend(string code) => code switch
    {
        "cc" => CliProfile.Claude.Backend,
        "cx" => CliProfile.Codex.Backend,
        "ag" => CliProfile.Antigravity.Backend,
        _ => null,
    };

    private async Task ResumeMenuAsync(long chat, string filterCode, int page, long editMsg, CancellationToken ct)
    {
        if (editMsg == 0) await EnsureQuickBarAsync(chat, ct);
        const int pageSize = 6;
        var backend = FilterToBackend(filterCode);
        var all = await _host.RecentSessionsAsync(36, backend);
        var st = State(chat);
        st.Sessions = all.ToList();

        var totalPages = Math.Max(1, (all.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 0, totalPages - 1);
        var slice = all.Skip(page * pageSize).Take(pageSize).ToList();

        var sb = new StringBuilder();
        sb.Append(L("↻ 지난 대화 이어하기", "↻ Resume a recent conversation"));
        if (all.Count > 0) sb.Append($" ({page + 1}/{totalPages})");
        sb.Append('\n');

        var rows = new List<TelegramButton[]>
        {
            new[]
            {
                new TelegramButton((filterCode == "all" ? "● " : "") + L("전체", "All"), "m:res:all:0"),
                new TelegramButton((filterCode == "cc" ? "● " : "") + "Claude", "m:res:cc:0"),
                new TelegramButton((filterCode == "cx" ? "● " : "") + "Codex", "m:res:cx:0"),
                new TelegramButton((filterCode == "ag" ? "● " : "") + "AGY", "m:res:ag:0"),
            },
        };

        if (slice.Count == 0)
        {
            sb.Append('\n').Append(L("이어갈 지난 대화가 없습니다.", "No recent conversations found."));
        }
        else
        {
            for (var i = 0; i < slice.Count; i++)
            {
                var idx = page * pageSize + i;
                var s = slice[i];
                var when = s.When.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
                var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(s.Cwd));
                if (string.IsNullOrEmpty(folderName)) folderName = s.Cwd;
                var acc = s.AccountName.Length > 0 ? $"({s.AccountName})" : "";
                sb.Append('\n').Append(i + 1).Append(") [").Append(s.CliDisplay).Append(acc).Append("] ")
                  .Append(TelegramText.Clip(s.Title, 60))
                  .Append("\n   📁 ").Append(folderName).Append(" · ").Append(when);
                rows.Add([
                    new TelegramButton(
                        TelegramText.Clip($"{i + 1}) {folderName} · {s.Title}", 56),
                        "rs:" + idx),
                ]);
            }
        }

        if (totalPages > 1)
        {
            var nav = new List<TelegramButton>();
            if (page > 0) nav.Add(new TelegramButton(L("⬅ 이전", "⬅ Prev"), $"m:res:{filterCode}:{page - 1}"));
            if (page + 1 < totalPages) nav.Add(new TelegramButton(L("다음 ➡", "Next ➡"), $"m:res:{filterCode}:{page + 1}"));
            if (nav.Count > 0) rows.Add(nav.ToArray());
        }
        rows.Add([
            new TelegramButton(L("🔄 새로고침", "🔄 Refresh"), $"m:res:{filterCode}:{page}"),
            new TelegramButton(L("⬅ 메뉴", "⬅ Menu"), "m:main"),
        ]);

        await DeliverAsync(chat, editMsg, sb.ToString(), TelegramApi.Keyboard(rows), html: false, ct);
    }

    private async Task<string> DoResumeSessionAsync(long chat, int idx, long editMsg, CancellationToken ct)
    {
        var st = State(chat);
        if (idx < 0 || idx >= st.Sessions.Count)
            return L("목록이 지났습니다. /resume 로 다시 여세요.", "List expired. Open /resume again.");
        var item = st.Sessions[idx];
        var (tabId, err) = await _host.ResumeSessionAsync(item.Backend, item.Cwd, item.SessionId, item.AccountId);
        if (tabId is null)
        {
            await Say(chat, L("이어가지 못했습니다: ", "Could not resume: ") + err, ct);
            return err ?? L("실패", "Failed");
        }
        st.Target = tabId;
        var tab = await FindAsync(tabId);
        var name = tab is null ? tabId : Label(tab);
        if (editMsg > 0)
            await Edit(chat, editMsg, L($"↻ '{name}' 대화를 이어서 열었습니다.", $"↻ Resumed '{name}'."), null, ct);
        _host.Log(L($"[Telegram] 지난 대화를 이어서 열었습니다 — {name}", $"[Telegram] Resumed conversation — {name}"), "i");
        await StartWatchAsync(chat, tabId, tab?.Name ?? tabId, "", -1, WatchKind.Startup, ct);
        return name;
    }

    // ── 화면 · 터미널 리모컨 (/screen) ───────────────────────────────────────

    private JsonObject ScreenRemoteKeyboard(string tabId, bool running)
    {
        var rows = new List<TelegramButton[]>
        {
            new[]
            {
                new TelegramButton(L("🔄 새로고침", "🔄 Refresh"), "rc:rf:" + tabId),
                new TelegramButton("⏹ Esc", "rc:esc:" + tabId),
                new TelegramButton("⚡ Ctrl+C", "rc:cc:" + tabId),
                new TelegramButton("⏎ Enter", "rc:cr:" + tabId),
            },
            new[]
            {
                new TelegramButton("↑", "rc:up:" + tabId),
                new TelegramButton("↓", "rc:dn:" + tabId),
                new TelegramButton("⇥ Tab", "rc:tb:" + tabId),
                new TelegramButton("Space", "rc:sp:" + tabId),
            },
            new[]
            {
                new TelegramButton("y⏎", "rc:y:" + tabId),
                new TelegramButton("n⏎", "rc:n:" + tabId),
                new TelegramButton("1", "rc:1:" + tabId),
                new TelegramButton("2", "rc:2:" + tabId),
                new TelegramButton("3", "rc:3:" + tabId),
            },
        };
        if (!running)
            rows.Insert(0, [new TelegramButton(L("🔄 꺼진 CLI 다시 시작", "🔄 Restart stopped CLI"), "trst:" + tabId)]);
        rows.Add([
            new TelegramButton(L("⚙ 탭 관리", "⚙ Tab"), "tm:" + tabId),
            new TelegramButton(L("🗂 탭 목록", "🗂 Tabs"), "m:tabs"),
            new TelegramButton(L("📋 메뉴", "📋 Menu"), "m:main"),
        ]);
        return TelegramApi.Keyboard(rows);
    }

    private string BuildScreenHtml(string title, string screen)
    {
        var body = TelegramText.ScreenExcerpt(screen, 45, 3000);
        if (body.Length == 0) body = L("(빈 화면)", "(empty screen)");
        return TelegramApi.Html(title) + "\n<pre>" + TelegramApi.Html(body) + "</pre>";
    }

    private async Task ScreenAsync(long chat, string? tabId, long editMsg, CancellationToken ct)
    {
        if (editMsg == 0 && tabId is null) await EnsureQuickBarAsync(chat, ct);
        var tab = tabId is null ? await TargetAsync(chat, false, ct) : await FindAsync(tabId);
        if (tab is null) return;
        var screen = await _host.ScreenAsync(tab.Id);
        if (screen is null) { await Say(chat, L("화면을 읽지 못했습니다.", "Could not read the screen."), ct); return; }
        var state = tab.Running ? "" : L(" · 꺼짐", " · stopped");
        var html = BuildScreenHtml($"🖥 {Label(tab)}{state}", screen.Text);
        await DeliverAsync(chat, editMsg, html, ScreenRemoteKeyboard(tab.Id, tab.Running), html: true, ct);
    }

    private async Task SendScreenAsync(long chat, string title, string screen, CancellationToken ct, string? tabId = null, bool running = true)
    {
        var html = BuildScreenHtml(title, screen);
        var markup = tabId is not null ? ScreenRemoteKeyboard(tabId, running) : null;
        await _api.SendMessageAsync(chat, html, markup, html: true, ct: ct);
    }

    private async Task<string> HandleRemoteKeyAsync(long chat, string code, string tabId, long editMsg, CancellationToken ct)
    {
        if (code == "rf")
        {
            await ScreenAsync(chat, tabId, editMsg, ct);
            return L("새로고침", "Refreshed");
        }
        var (keys, label) = code switch
        {
            "esc" => ("\u001b", "Esc"),
            "cc" => ("\u0003", "Ctrl+C"),
            "cr" => ("\r", "Enter"),
            "up" => ("\u001b[A", "↑"),
            "dn" => ("\u001b[B", "↓"),
            "tb" => ("\t", "Tab"),
            "sp" => (" ", "Space"),
            "y" => ("y\r", "y + Enter"),
            "n" => ("n\r", "n + Enter"),
            "1" => ("1", "1"),
            "2" => ("2", "2"),
            "3" => ("3", "3"),
            _ => ("", ""),
        };
        if (keys.Length == 0) return "";
        var ok = await _host.KeysAsync(tabId, keys);
        if (!ok) return L("CLI 가 실행 중이 아닙니다.", "The CLI is not running.");
        if (RemoteKeyRefreshDelayMs > 0)
            try { await Task.Delay(RemoteKeyRefreshDelayMs, ct); } catch (OperationCanceledException) { }
        await ScreenAsync(chat, tabId, editMsg, ct);
        return L($"{label} 전송", $"Sent {label}");
    }

    private async Task KeysAsync(long chat, string data, string done, CancellationToken ct)
    {
        var tab = await TargetAsync(chat, false, ct);
        if (tab is null) return;
        var ok = await _host.KeysAsync(tab.Id, data);
        await Say(chat, ok ? $"{done} ({Label(tab)})" : L("CLI 가 실행 중이 아닙니다.", "The CLI is not running."), ct);
    }

    // ── 사용량 (/usage) ─────────────────────────────────────────────────────

    private string FormatReset(long unixSeconds)
    {
        if (unixSeconds <= 0) return "";
        var left = DateTimeOffset.FromUnixTimeSeconds(unixSeconds) - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero) return L("곧 리셋", "resetting");
        if (left.TotalDays >= 1) return L($"{(int)left.TotalDays}일 {left.Hours}시간 남음", $"{(int)left.TotalDays}d {left.Hours}h left");
        if (left.TotalHours >= 1) return L($"{(int)left.TotalHours}시간 {left.Minutes}분 남음", $"{(int)left.TotalHours}h {left.Minutes}m left");
        return L($"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}분 남음", $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m left");
    }

    private string LocalizeWindowLabel(string label) => IsEnglish()
        ? label switch { "주" => "wk", "월" => "mo", "1일" => "1d", _ => label }
        : label;

    private async Task UsageAsync(long chat, long editMsg, CancellationToken ct)
    {
        if (editMsg == 0) await EnsureQuickBarAsync(chat, ct);
        var items = await _host.GetUsageAsync();
        var sb = new StringBuilder();
        sb.Append(L("📊 계정 한도 사용량", "📊 Account Quota Usage")).Append('\n');
        if (items.Count == 0)
        {
            sb.Append('\n').Append(L("조회된 계정 정보가 없습니다.", "No account usage information available."));
        }
        else
        {
            foreach (var item in items)
            {
                var who = item.Email.Length > 0 ? $"{item.AccountLabel} ({item.Email})" : item.AccountLabel;
                sb.Append("\n• ").Append(item.CliDisplay).Append(" — ").Append(who).Append('\n');
                if (item.Windows is { Count: > 0 })
                {
                    foreach (var w in item.Windows)
                    {
                        var remain = Math.Clamp(100 - w.Percent, 0, 100);
                        var rst = FormatReset(w.ResetsAtUnix);
                        sb.Append("   ")
                          .Append(LocalizeWindowLabel(w.Label)).Append(": ")
                          .Append(L($"사용 {w.Percent}% (잔여 {remain}%)", $"{w.Percent}% used ({remain}% left)"));
                        if (rst.Length > 0) sb.Append(" · ").Append(rst);
                        sb.Append('\n');
                    }
                }
                else
                {
                    var why = item.Note.Length > 0 ? item.Note : L("조회 안 됨(미로그인 또는 기록 없음)", "unavailable (not signed in or no recent activity)");
                    sb.Append("   — ").Append(why).Append('\n');
                }
            }
        }
        var rows = new[]
        {
            new[]
            {
                new TelegramButton(L("🔄 새로고침", "🔄 Refresh"), "m:use"),
                new TelegramButton(L("⬅ 메뉴", "⬅ Menu"), "m:main"),
            },
        };
        await DeliverAsync(chat, editMsg, sb.ToString().TrimEnd(), TelegramApi.Keyboard(rows), html: false, ct);
    }

    // ── 예약 작업 (/schedules) ───────────────────────────────────────────────

    private async Task SchedulesMenuAsync(long chat, long editMsg, CancellationToken ct)
    {
        if (editMsg == 0) await EnsureQuickBarAsync(chat, ct);
        var jobs = await _host.GetSchedulesAsync();
        State(chat).Schedules = jobs.ToList();

        var sb = new StringBuilder();
        sb.Append(L($"⏰ 예약 작업 ({jobs.Count}개)", $"⏰ Scheduled Tasks ({jobs.Count})")).Append('\n');
        var rows = new List<TelegramButton[]>();

        if (jobs.Count == 0)
        {
            sb.Append('\n').Append(L("등록된 예약 작업이 없습니다. 앱의 [⚙ 설정 → 예약 작업] 에서 추가할 수 있습니다.",
                                     "No scheduled tasks. Add one in the app under [⚙ Settings → Scheduled tasks]."));
        }
        else
        {
            for (var i = 0; i < jobs.Count; i++)
            {
                var j = jobs[i];
                var icon = j.Running ? "🔄" : j.Enabled ? "⏰" : "⏸";
                var nextTxt = j.Running ? L("실행 중…", "running…")
                    : !j.Enabled ? L("꺼짐", "off")
                    : j.NextLocal is { } nx ? L($"다음 {nx:MM-dd HH:mm}", $"next {nx:MM-dd HH:mm}")
                    : L("다음 실행 없음", "no upcoming run");
                sb.Append('\n').Append(i + 1).Append(") ").Append(icon).Append(' ').Append(j.Name)
                  .Append("\n   ").Append(j.WhenSummary).Append(" · ").Append(nextTxt);
                rows.Add([
                    new TelegramButton(TelegramText.Clip($"{icon} {j.Name}", 38), "sc:v:" + i),
                    new TelegramButton(L("▶ 지금 실행", "▶ Run"), "sc:run:" + i),
                    new TelegramButton(j.Enabled ? L("끄기", "Off") : L("켜기", "On"), "sc:tog:" + i),
                ]);
            }
        }
        rows.Add([
            new TelegramButton(L("🔄 새로고침", "🔄 Refresh"), "m:sch"),
            new TelegramButton(L("⬅ 메뉴", "⬅ Menu"), "m:main"),
        ]);
        await DeliverAsync(chat, editMsg, sb.ToString(), TelegramApi.Keyboard(rows), html: false, ct);
    }

    private async Task ScheduleDetailAsync(long chat, int idx, long editMsg, CancellationToken ct)
    {
        var jobs = await _host.GetSchedulesAsync();
        State(chat).Schedules = jobs.ToList();
        if (idx < 0 || idx >= jobs.Count) { await SchedulesMenuAsync(chat, editMsg, ct); return; }
        var j = jobs[idx];

        var sb = new StringBuilder();
        sb.Append(j.Enabled ? "⏰ " : "⏸ ").Append(j.Name).Append('\n');
        sb.Append(L("일정: ", "Schedule: ")).Append(j.WhenSummary).Append('\n');
        sb.Append(L("대상: ", "Target: ")).Append(j.WhereSummary).Append('\n');
        sb.Append(L("상태: ", "State: "));
        if (j.Running) sb.Append(L("🔄 실행 중…", "🔄 Running…"));
        else if (!j.Enabled) sb.Append(L("꺼짐", "Disabled"));
        else sb.Append(j.NextLocal is { } nx ? L($"켜짐 · 다음 실행 {nx:yyyy-MM-dd HH:mm}", $"Enabled · next {nx:yyyy-MM-dd HH:mm}") : L("켜짐 · 다음 실행 없음", "Enabled · no upcoming run"));

        if (j.LastStatus is { Length: > 0 } st)
        {
            var when = j.LastWhenLocal is { } lw ? lw.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
            sb.Append('\n').Append(L("최근 실행: ", "Last run: "))
              .Append(st == "ok" ? L("✅ 성공", "✅ Success") : st == "running" ? L("🔄 실행 중", "🔄 Running") : L("❌ 실패", "❌ Failed"));
            if (when.Length > 0) sb.Append(" · ").Append(when);
            if (!string.IsNullOrWhiteSpace(j.LastError)) sb.Append("\n⚠ ").Append(j.LastError);
        }

        var rows = new List<TelegramButton[]>
        {
            new[]
            {
                new TelegramButton(L("▶ 지금 실행", "▶ Run now"), "sc:run:" + idx),
                new TelegramButton(j.Enabled ? L("🔕 끄기", "🔕 Disable") : L("🔔 켜기", "🔔 Enable"), "sc:tog:" + idx),
            },
        };
        if (!string.IsNullOrWhiteSpace(j.LastAnswer))
            rows.Add([new TelegramButton(L("📄 최근 실행 결과 보기", "📄 View last answer"), "sc:ans:" + idx)]);
        rows.Add([
            new TelegramButton(L("🔄 새로고침", "🔄 Refresh"), "sc:v:" + idx),
            new TelegramButton(L("⬅ 예약 목록", "⬅ Schedules"), "m:sch"),
        ]);
        await DeliverAsync(chat, editMsg, sb.ToString(), TelegramApi.Keyboard(rows), html: false, ct);
    }

    private async Task<string> HandleScheduleActionAsync(long chat, string act, int idx, long editMsg, CancellationToken ct)
    {
        var st = State(chat);
        if (st.Schedules.Count == 0) st.Schedules = (await _host.GetSchedulesAsync()).ToList();
        if (idx < 0 || idx >= st.Schedules.Count)
        {
            await SchedulesMenuAsync(chat, editMsg, ct);
            return L("목록을 갱신했습니다.", "Refreshed list.");
        }
        var job = st.Schedules[idx];
        switch (act)
        {
            case "v":
                await ScheduleDetailAsync(chat, idx, editMsg, ct);
                return "";
            case "tog":
            {
                var err = await _host.SetScheduleEnabledAsync(job.Id, !job.Enabled);
                if (err is not null) return err;
                await ScheduleDetailAsync(chat, idx, editMsg, ct);
                return !job.Enabled ? L("켰습니다.", "Enabled.") : L("껐습니다.", "Disabled.");
            }
            case "run":
            {
                var err = await _host.RunScheduleNowAsync(job.Id);
                if (err is not null) return err;
                await ScheduleDetailAsync(chat, idx, editMsg, ct);
                return L($"'{job.Name}' 실행을 시작했습니다.", $"Started '{job.Name}'.");
            }
            case "ans":
            {
                var fresh = (await _host.GetSchedulesAsync()).FirstOrDefault(x => x.Id == job.Id) ?? job;
                if (string.IsNullOrWhiteSpace(fresh.LastAnswer))
                    return L("최근 실행 결과가 없습니다.", "No recent answer.");
                await Say(chat, $"⏰ {fresh.Name}\n\n{fresh.LastAnswer}", ct);
                return L("결과를 보냈습니다.", "Sent result.");
            }
        }
        return "";
    }

    // ── 앱 설정 · 창 제어 (/settings) ────────────────────────────────────────

    private static string BackendShort(string backend) => backend switch
    {
        "claude_code" => "cc",
        "codex_cli" => "cx",
        _ => "ag",
    };

    private static string ShortToBackend(string code) => code switch
    {
        "cc" => CliProfile.Claude.Backend,
        "cx" => CliProfile.Codex.Backend,
        _ => CliProfile.Antigravity.Backend,
    };

    private string StartInTrayLabel(string v) => Settings.NormalizeStartInTray(v) switch
    {
        "always" => L("항상 트레이로", "Always in tray"),
        "startup" => L("자동 실행 때만", "On startup only"),
        _ => L("창으로 시작", "Normal window"),
    };

    private static string NextStartInTray(string v) => Settings.NormalizeStartInTray(v) switch
    {
        "never" => "startup",
        "startup" => "always",
        _ => "never",
    };

    private async Task SettingsMenuAsync(long chat, long editMsg, CancellationToken ct)
    {
        if (editMsg == 0) await EnsureQuickBarAsync(chat, ct);
        var cfg = await _host.GetAppSettingsAsync();
        if (cfg is null)
        {
            await Say(chat, L("설정을 읽지 못했습니다.", "Could not read app settings."), ct);
            return;
        }

        var onTxt = L("켜짐", "ON");
        var offTxt = L("꺼짐", "OFF");
        var sb = new StringBuilder();
        sb.Append(L("⚙ 앱 설정 · 창 제어", "⚙ App Settings & Window Control")).Append('\n');
        sb.Append(L("• 앱 창: ", "• Window: ")).Append(cfg.WindowShown ? L("화면에 표시 중", "Shown on desktop") : L("시스템 트레이에 숨김", "Hidden in system tray")).Append('\n');
        sb.Append(L("• 데스크톱 펫: ", "• Pet: ")).Append(cfg.PetEnabled ? onTxt : offTxt).Append('\n');
        sb.Append(L("• 닫기(×) 트레이 숨김: ", "• Close(×) to tray: ")).Append(cfg.CloseToTray ? onTxt : offTxt).Append('\n');
        sb.Append(L("• 시작 시 트레이: ", "• Start in tray: ")).Append(StartInTrayLabel(cfg.StartInTray)).Append('\n');
        sb.Append(L("• Windows 시작프로그램: ", "• Run at startup: ")).Append(cfg.RunAtStartup ? onTxt : offTxt).Append('\n');
        sb.Append(L("• 하단 빠른 메뉴 바: ", "• Bottom quick bar: ")).Append(cfg.ShowQuickBar ? onTxt : offTxt).Append('\n');

        foreach (var cli in cfg.Clis)
        {
            var m = cli.Model.Length > 0 ? cli.Model : L("기본", "default");
            var e = cli.Effort.Length > 0 ? cli.Effort : L("기본", "default");
            var perms = string.Join(", ", cli.Permissions.Select(p => $"{p.Label}: {(p.Value.Length > 0 ? p.Value : L("기본", "default"))}"));
            sb.Append("\n🤖 ").Append(cli.Display).Append(": ")
              .Append(L($"모델={m} · 추론={e}", $"model={m} · effort={e}"));
            if (perms.Length > 0) sb.Append(" · ").Append(perms);
        }

        var rows = new List<TelegramButton[]>
        {
            new[]
            {
                new TelegramButton(L("🪟 창 열기", "🪟 Show window"), "cf:win:show"),
                new TelegramButton(L("📥 트레이로 숨기기", "📥 Hide to tray"), "cf:win:hide"),
            },
            new[]
            {
                new TelegramButton(L($"🐾 펫: {(cfg.PetEnabled ? onTxt : offTxt)}", $"🐾 Pet: {(cfg.PetEnabled ? onTxt : offTxt)}"), "cf:pet"),
                new TelegramButton(L($"× 트레이 숨김: {(cfg.CloseToTray ? onTxt : offTxt)}", $"× Close→Tray: {(cfg.CloseToTray ? onTxt : offTxt)}"), "cf:ctt"),
            },
            new[]
            {
                new TelegramButton(L($"🚀 시작프로그램: {(cfg.RunAtStartup ? onTxt : offTxt)}", $"🚀 Startup: {(cfg.RunAtStartup ? onTxt : offTxt)}"), "cf:run"),
                new TelegramButton(L($"트레이 시작: {StartInTrayLabel(cfg.StartInTray)}", $"Tray start: {StartInTrayLabel(cfg.StartInTray)}"), "cf:sit"),
            },
            new[]
            {
                new TelegramButton("🤖 Claude", "cf:cli:cc"),
                new TelegramButton("🤖 Codex", "cf:cli:cx"),
                new TelegramButton("🤖 Antigravity", "cf:cli:ag"),
            },
            new[]
            {
                new TelegramButton(cfg.ShowQuickBar ? L("⌨ 빠른바 숨기기", "⌨ Hide quick bar") : L("⌨ 빠른바 켜기", "⌨ Show quick bar"), "m:bar"),
                new TelegramButton(L("⬅ 메뉴", "⬅ Menu"), "m:main"),
            },
        };
        await DeliverAsync(chat, editMsg, sb.ToString(), TelegramApi.Keyboard(rows), html: false, ct);
    }

    private async Task CliSettingsMenuAsync(long chat, string shortCode, long editMsg, CancellationToken ct)
    {
        var cfg = await _host.GetAppSettingsAsync();
        var backend = ShortToBackend(shortCode);
        var cli = cfg?.Clis.FirstOrDefault(c => c.Backend == backend);
        if (cli is null) { await SettingsMenuAsync(chat, editMsg, ct); return; }

        var def = L("(기본값)", "(default)");
        var sb = new StringBuilder();
        sb.Append("🤖 ").Append(cli.Display).Append(L(" 기본 설정\n(다음에 새로 여는 탭부터 적용됩니다)\n", " Default Settings\n(Applies to newly opened tabs)\n"));
        sb.Append('\n').Append(L("• 모델: ", "• Model: ")).Append(cli.Model.Length > 0 ? cli.Model : def);
        sb.Append('\n').Append(L("• 추론 강도: ", "• Reasoning effort: ")).Append(cli.Effort.Length > 0 ? cli.Effort : def);
        foreach (var p in cli.Permissions)
            sb.Append('\n').Append($"• {p.Label}: ").Append(p.Value.Length > 0 ? p.Value : def);

        var rows = new List<TelegramButton[]>
        {
            new[]
            {
                new TelegramButton(L("🧠 모델 변경", "🧠 Model"), "cf:mdl:" + shortCode),
                new TelegramButton(L("⚡ 추론 강도 변경", "⚡ Effort"), "cf:eff:" + shortCode),
            },
        };
        for (var i = 0; i < cli.Permissions.Count; i++)
        {
            var p = cli.Permissions[i];
            rows.Add([new TelegramButton(L($"🛡 {p.Label} 변경 ({(p.Value.Length > 0 ? p.Value : "기본")})",
                                           $"🛡 {p.Label} ({(p.Value.Length > 0 ? p.Value : "default")})"),
                                         $"cf:prm:{shortCode}:{i}")]);
        }
        rows.Add([new TelegramButton(L("⬅ 설정으로", "⬅ Back to Settings"), "m:cfg")]);
        await DeliverAsync(chat, editMsg, sb.ToString(), TelegramApi.Keyboard(rows), html: false, ct);
    }

    private async Task CliChoicePickerAsync(long chat, string kind, string shortCode, int permIdx, long editMsg, CancellationToken ct)
    {
        var cfg = await _host.GetAppSettingsAsync();
        var backend = ShortToBackend(shortCode);
        var cli = cfg?.Clis.FirstOrDefault(c => c.Backend == backend);
        if (cli is null) { await SettingsMenuAsync(chat, editMsg, ct); return; }

        string settingKey;
        string current;
        string heading;
        IReadOnlyList<string> values;

        if (kind == "mdl")
        {
            settingKey = "model:" + backend;
            current = cli.Model;
            heading = L($"🧠 {cli.Display} 기본 모델 선택:", $"🧠 Choose {cli.Display} default model:");
            values = cli.Models;
        }
        else if (kind == "eff")
        {
            settingKey = "effort:" + backend;
            current = cli.Effort;
            heading = L($"⚡ {cli.Display} 추론 강도 선택:", $"⚡ Choose {cli.Display} reasoning effort:");
            values = cli.Efforts;
        }
        else
        {
            if (permIdx < 0 || permIdx >= cli.Permissions.Count) { await CliSettingsMenuAsync(chat, shortCode, editMsg, ct); return; }
            var p = cli.Permissions[permIdx];
            settingKey = "perm:" + p.Key;
            current = p.Value;
            heading = L($"🛡 {cli.Display} · {p.Label} 선택:", $"🛡 Choose {cli.Display} · {p.Label}:");
            values = p.Choices;
        }

        var choices = new List<SettingChoice> { new(settingKey, "", shortCode) };
        var rows = new List<TelegramButton[]>
        {
            new[] { new TelegramButton((current.Length == 0 ? "● " : "") + L("(CLI 기본값)", "(CLI default)"), "cf:set:0") },
        };
        foreach (var val in values.Take(24))
        {
            var idx = choices.Count;
            choices.Add(new SettingChoice(settingKey, val, shortCode));
            var mark = string.Equals(current, val, StringComparison.OrdinalIgnoreCase) ? "● " : "";
            rows.Add([new TelegramButton(TelegramText.Clip(mark + val, 58), "cf:set:" + idx)]);
        }
        if (values.Count == 0 && kind == "mdl")
        {
            heading += "\n\n" + L("목록을 아직 불러오지 못했습니다(앱 설정 화면에서 모델 목록을 새로고침할 수 있습니다).",
                                  "No model list cached yet (refresh models in the app's Settings screen).");
        }
        State(chat).SettingChoices = choices;
        rows.Add([new TelegramButton(L("⬅ 뒤로", "⬅ Back"), "cf:cli:" + shortCode)]);
        await DeliverAsync(chat, editMsg, heading, TelegramApi.Keyboard(rows), html: false, ct);
    }

    private async Task<string> HandleSettingCallbackAsync(long chat, string[] parts, long editMsg, CancellationToken ct)
    {
        if (parts.Length >= 3 && parts[1] == "win")
        {
            var err = await _host.ApplyAppSettingAsync("window", parts[2]);
            await SettingsMenuAsync(chat, editMsg, ct);
            return err ?? (parts[2] == "show" ? L("창을 열었습니다.", "Window shown.") : L("트레이로 숨겼습니다.", "Hidden to tray."));
        }
        if (parts.Length == 2 && parts[1] is "pet" or "ctt" or "run" or "sit")
        {
            var cfg = await _host.GetAppSettingsAsync();
            if (cfg is null) return "";
            var (key, val) = parts[1] switch
            {
                "pet" => ("pet", (!cfg.PetEnabled).ToString().ToLowerInvariant()),
                "ctt" => ("closeToTray", (!cfg.CloseToTray).ToString().ToLowerInvariant()),
                "run" => ("runAtStartup", (!cfg.RunAtStartup).ToString().ToLowerInvariant()),
                _ => ("startInTray", NextStartInTray(cfg.StartInTray)),
            };
            var err = await _host.ApplyAppSettingAsync(key, val);
            await SettingsMenuAsync(chat, editMsg, ct);
            return err ?? L("적용했습니다.", "Updated.");
        }
        if (parts.Length == 3 && parts[1] == "cli")
        {
            await CliSettingsMenuAsync(chat, parts[2], editMsg, ct);
            return "";
        }
        if (parts.Length == 3 && parts[1] is "mdl" or "eff")
        {
            await CliChoicePickerAsync(chat, parts[1], parts[2], 0, editMsg, ct);
            return "";
        }
        if (parts.Length == 4 && parts[1] == "prm" && int.TryParse(parts[3], out var pIdx))
        {
            await CliChoicePickerAsync(chat, "prm", parts[2], pIdx, editMsg, ct);
            return "";
        }
        if (parts.Length == 3 && parts[1] == "set" && int.TryParse(parts[2], out var cIdx))
        {
            var st = State(chat);
            if (cIdx < 0 || cIdx >= st.SettingChoices.Count)
            {
                await SettingsMenuAsync(chat, editMsg, ct);
                return L("목록이 지났습니다.", "List expired.");
            }
            var pick = st.SettingChoices[cIdx];
            var err = await _host.ApplyAppSettingAsync(pick.Key, pick.Value);
            await CliSettingsMenuAsync(chat, pick.ReturnCode, editMsg, ct);
            return err ?? L("저장했습니다.", "Saved.");
        }
        return "";
    }

    // ── 상태 (/status) ───────────────────────────────────────────────────────

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
        var rows = new[]
        {
            new[]
            {
                new TelegramButton(L("📋 전체 메뉴", "📋 Menu"), "m:main"),
                new TelegramButton(L("🗂 탭 관리", "🗂 Tabs"), "m:tabs"),
            },
        };
        await EnsureQuickBarAsync(chat, ct);
        await _api.SendMessageAsync(chat, sb.ToString(), TelegramApi.Keyboard(rows), ct: ct);
    }

    // ── 질문 ────────────────────────────────────────────────────────────────

    private async Task AskAsync(long chat, string text, JsonObject msg, CancellationToken ct)
    {
        var tab = await TargetAsync(chat, false, ct);
        if (tab is null)
        {
            var st = State(chat);
            st.PendingText = text; st.PendingMessage = msg;
            return;
        }
        if (!tab.Running)
        {
            var rows = new[]
            {
                new[]
                {
                    new TelegramButton(L("🔄 이 탭 다시 시작", "🔄 Restart this tab"), "trst:" + tab.Id),
                    new TelegramButton(L("🗂 다른 탭 고르기", "🗂 Switch tab"), "m:tabs"),
                    new TelegramButton(L("➕ 새 대화", "➕ New chat"), "m:new"),
                },
            };
            await _api.SendMessageAsync(chat,
                L($"{Label(tab)} 의 CLI 가 실행 중이 아닙니다 — 아래 단추로 다시 시작하거나 다른 탭을 고르세요.",
                  $"The CLI in {Label(tab)} is not running — restart it below or pick another tab."),
                TelegramApi.Keyboard(rows), ct: ct);
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
        [
            new TelegramButton(L("🖥 화면·리모컨", "🖥 Screen"), "scr:" + tabId),
            new TelegramButton(L("⏹ 중단(Esc)", "⏹ Stop (Esc)"), "esc:" + tabId),
            new TelegramButton("⚡ Ctrl+C", "ctrlc:" + tabId),
        ],
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

                if (w.Kind == WatchKind.Turn && tab.TranscriptPath is { Length: > 0 } path && File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    var sig = path + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                    if (sig != fileSig)
                    {
                        fileSig = sig;
                        try { turn = TelegramText.TurnItems(TranscriptReader.Read(tab.Profile, path).Items, w.Sent, w.Baseline) ?? turn; }
                        catch { }
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

                var quiet = now - tab.LastOutputUtc;
                if (quiet < TimeSpan.FromSeconds(1.2)) continue;

                var screen = await _host.ScreenAsync(tab.Id);
                if (screen is null) continue;

                if (screen.FocusMode && tab.Backend == CliProfile.Codex.Backend &&
                    quiet > TimeSpan.FromSeconds(2) && now - w.LastWake > TimeSpan.FromSeconds(2))
                {
                    w.LastWake = now;
                    await _host.KeysAsync(tab.Id, "\u001b[I");
                }

                var options = TelegramText.PromptOptions(screen.Text);
                if (options.Count >= 2)
                {
                    if (now - w.AnsweredUtc < TimeSpan.FromSeconds(2)) continue;
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
                    await FinishAsync(w, L("ℹ 대화 기록에서 답을 찾지 못해 화면을 보냅니다.", "ℹ No answer found in the transcript; sending the screen."),
                                      null, screen.Text, ct);
                    return;
                }
                if (quiet > TimeSpan.FromSeconds(20) && !w.IdleNoticed)
                {
                    w.IdleNoticed = true;
                    await SendScreenAsync(w.Chat, L($"⏸ {w.TabName} — 입력을 기다리는 것 같습니다:", $"⏸ {w.TabName} seems to be waiting for input:"),
                                          screen.Text, ct, w.TabId, tab.Running);
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
        if (screen is not null) await SendScreenAsync(w.Chat, "🖥 " + w.TabName, screen, ct, w.TabId);
    }

    // ── 인라인 단추 콜백 ────────────────────────────────────────────────────

    private async Task HandleCallbackAsync(JsonObject cb, CancellationToken ct)
    {
        var id = cb["id"]?.GetValue<string>() ?? "";
        var chat = cb["message"]?["chat"]?["id"]?.GetValue<long>() ?? 0;
        var msgId = cb["message"]?["message_id"]?.GetValue<long>() ?? 0;
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
            if (parts[0] != "trnc") State(chat).AwaitingRenameTabId = null;

            switch (parts[0])
            {
                case "m" when parts.Length >= 2:
                    switch (parts[1])
                    {
                        case "main": await MenuAsync(chat, msgId, ct); break;
                        case "tabs": await TabsMenuAsync(chat, null, msgId, ct); break;
                        case "new": await NewAsync(chat, "", msgId, ct); break;
                        case "res":
                        {
                            var f = parts.Length >= 3 ? parts[2] : "all";
                            var p = parts.Length >= 4 && int.TryParse(parts[3], out var pg) ? pg : 0;
                            await ResumeMenuAsync(chat, f, p, msgId, ct);
                            break;
                        }
                        case "scr":
                            // TargetAsync 가 목록을 새로 띄울 수 있으므로 탭이 있을 때만 제자리 편집한다.
                            if (await TargetAsync(chat, true, ct) is { } curTab) await ScreenAsync(chat, curTab.Id, msgId, ct);
                            else await TargetAsync(chat, false, ct);
                            break;
                        case "use": await UsageAsync(chat, msgId, ct); break;
                        case "sch": await SchedulesMenuAsync(chat, msgId, ct); break;
                        case "cfg": await SettingsMenuAsync(chat, msgId, ct); break;
                        case "bar": answer = await ToggleQuickBarAsync(chat, "", msgId, ct); break;
                        case "hlp": await HelpAsync(chat, msgId, ct); break;
                    }
                    break;

                case "use" when parts.Length == 2:
                    if (await FindAsync(parts[1]) is { } tab) { answer = Label(tab); await SelectAsync(chat, tab, ct); }
                    else answer = L("이미 닫힌 탭입니다.", "That tab is closed.");
                    break;

                case "tm" when parts.Length == 2:
                    await TabDetailAsync(chat, parts[1], msgId, ct);
                    break;

                case "trst" when parts.Length == 2:
                    answer = await DoRestartTabAsync(chat, parts[1], msgId, ct);
                    break;

                case "trnm" when parts.Length == 2:
                    await BeginRenameAsync(chat, parts[1], msgId, ct);
                    break;

                case "trnc":
                    State(chat).AwaitingRenameTabId = null;
                    if (parts.Length >= 2 && parts[1].Length > 0) await TabDetailAsync(chat, parts[1], msgId, ct);
                    else await TabsMenuAsync(chat, null, msgId, ct);
                    answer = L("취소했습니다.", "Cancelled.");
                    break;

                case "tcls" when parts.Length == 2:
                    await CloseConfirmAsync(chat, parts[1], msgId, ct);
                    break;

                case "tclok" when parts.Length == 2:
                    answer = await DoCloseTabAsync(chat, parts[1], msgId, ct);
                    break;

                case "tsw" when parts.Length == 2:
                    await SwitchMenuAsync(chat, parts[1], msgId, ct);
                    break;

                case "tswi" when parts.Length == 3 && int.TryParse(parts[2], out var swIdx):
                    answer = await DoSwitchTabAsync(chat, parts[1], swIdx, msgId, ct);
                    break;

                case "scr" when parts.Length == 2:
                    await ScreenAsync(chat, parts[1], 0, ct);
                    break;

                case "rc" when parts.Length == 3:
                    answer = await HandleRemoteKeyAsync(chat, parts[1], parts[2], msgId, ct);
                    break;

                case "esc" when parts.Length == 2:
                    answer = await _host.KeysAsync(parts[1], "\u001b") ? L("Esc 를 보냈습니다.", "Sent Esc.") : L("CLI 가 실행 중이 아닙니다.", "The CLI is not running.");
                    break;

                case "ctrlc" when parts.Length == 2:
                    answer = await _host.KeysAsync(parts[1], "\u0003") ? L("Ctrl+C 를 보냈습니다.", "Sent Ctrl+C.") : L("CLI 가 실행 중이 아닙니다.", "The CLI is not running.");
                    break;

                case "opt" when parts.Length == 5:
                    answer = await ChooseAsync(parts[1], parts[2], parts[3], parts[4], ct);
                    break;

                case "nf" when parts.Length == 2:
                {
                    var st = State(chat);
                    if (int.TryParse(parts[1], out var i) && i >= 0 && i < st.Folders.Count)
                    {
                        st.NewFolder = st.Folders[i];
                        await CliMenuAsync(chat, st.NewFolder, msgId, ct);
                    }
                    else answer = L("목록이 지났습니다. /new 를 다시 보내세요.", "That list expired. Send /new again.");
                    break;
                }

                case "nc" when parts.Length == 2:
                {
                    var folder = State(chat).NewFolder;
                    if (folder is null) answer = L("폴더를 먼저 고르세요 — /new", "Choose a folder first — /new");
                    else await PickAccountOrOpenAsync(chat, folder, parts[1], msgId, ct);
                    break;
                }

                case "na" when parts.Length == 2 && int.TryParse(parts[1], out var accIdx):
                {
                    var st = State(chat);
                    if (st.NewFolder is { } nf && st.NewBackend is { } nb && accIdx >= 0 && accIdx < st.NewAccounts.Count)
                    {
                        var accId = st.NewAccounts[accIdx].AccountId;
                        st.NewFolder = null; st.NewBackend = null;
                        await OpenAsync(chat, nf, nb, accId, msgId, ct);
                    }
                    else answer = L("목록이 지났습니다. /new 를 다시 보내세요.", "That list expired. Send /new again.");
                    break;
                }

                case "rs" when parts.Length == 2 && int.TryParse(parts[1], out var rsIdx):
                    answer = await DoResumeSessionAsync(chat, rsIdx, msgId, ct);
                    break;

                case "sc" when parts.Length == 3 && int.TryParse(parts[2], out var scIdx):
                    answer = await HandleScheduleActionAsync(chat, parts[1], scIdx, msgId, ct);
                    break;

                case "cf":
                    answer = await HandleSettingCallbackAsync(chat, parts, msgId, ct);
                    break;
            }
        }
        finally
        {
            try { await _api.AnswerCallbackAsync(id, answer, ct); } catch { }
        }
    }

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

    // ── 보내기 · 제자리 고쳐 쓰기 ───────────────────────────────────────────

    private async Task DeliverAsync(long chat, long editMessage, string text, JsonObject? markup, bool html, CancellationToken ct)
    {
        if (editMessage > 0)
        {
            try
            {
                await _api.EditMessageAsync(chat, editMessage, text, markup, html: html, ct: ct);
                return;
            }
            catch (TelegramException) { /* 지워졌거나 고쳐 쓸 수 없는 메시지면 새로 보낸다 */ }
        }
        await _api.SendMessageAsync(chat, text, markup, html: html, ct: ct);
    }

    private async Task Say(long chat, string text, CancellationToken ct)
    {
        var parts = TelegramText.Split(text);
        var qb = QuickBarMarkupIfNeeded(chat);
        for (var i = 0; i < parts.Count; i++)
        {
            var mk = i == parts.Count - 1 ? qb : null;
            await _api.SendMessageAsync(chat, parts[i], mk, ct: ct);
        }
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

    private async Task Edit(long chat, long message, string text, JsonObject? markup, CancellationToken ct, bool html = false)
    {
        if (message == 0) return;
        try { await _api.EditMessageAsync(chat, message, text, markup, html: html, ct: ct); }
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
