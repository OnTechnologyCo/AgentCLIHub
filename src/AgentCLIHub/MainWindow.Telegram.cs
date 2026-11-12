using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCLIHub;

/// <summary>
/// 텔레그램 원격의 앱 쪽 절반. <see cref="TelegramRemote"/> 가 바라는 것(<see cref="IRemoteHost"/>)을
/// **UI 스레드로 넘겨** 처리한다 — 탭 사전·WebView2 는 UI 스레드에서만 만질 수 있고, 다른 스레드에서
/// 건드린 예외는 배경에서 조용히 사라져 텔레그램 쪽에는 "답이 안 온다" 로만 보인다.
/// </summary>
public partial class MainWindow : IRemoteHost
{
    private TelegramRemote? _telegram;

    /// <summary>설정 화면에 보일 한 줄(토큰 확인 결과 등). 상태(연결·오류)와 따로 둔다.</summary>
    private string _telegramNote = "";

    private void StartTelegram() => ApplyTelegram();

    private void StopTelegram()
    {
        try { _telegram?.Dispose(); } catch { }
        _telegram = null;
    }

    /// <summary>설정과 토큰대로 켜거나 끈다. 토큰이 바뀌었으면 새로 띄운다(같은 봇을 두 곳에서 받으면 서로 밀어낸다).</summary>
    private void ApplyTelegram()
    {
        var token = Settings.Load().Telegram.Enabled ? TelegramVault.Load() : null;
        if (token is null) { StopTelegram(); PostTelegram(); return; }
        if (_telegram is not null && _telegram.Token == token) { PostTelegram(); return; }
        StopTelegram();
        _telegram = new TelegramRemote(token, this);
        _telegram.Start();
        PostTelegram();
    }

    private object TelegramState()
    {
        var s = Settings.Load().Telegram;
        var token = TelegramVault.Load();
        var r = _telegram;
        var code = r?.PairingCode;
        return new
        {
            enabled = s.Enabled,
            hasToken = token is not null,
            token = TelegramApi.Mask(token),       // 봇 번호만. 토큰 자체는 화면에 다시 보내지 않는다
            running = r is not null,
            connected = r?.Connected == true,
            bot = r?.BotName ?? "",
            error = r?.Error ?? "",
            note = _telegramNote,
            pairCode = code ?? "",
            pairUntil = code is null ? 0 : new DateTimeOffset(DateTime.SpecifyKind(r!.PairingUntilUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            chats = s.Chats.Select(c => new
            {
                id = c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                name = c.Name,
            }).ToList(),
        };
    }

    private void PostTelegram() => Post(new { t = "tg.state", state = TelegramState() });

    /// <summary>설정 화면의 텔레그램 칸. **누르는 즉시 일어난다**([저장] 과 무관하다).</summary>
    private async void HandleTelegramMessage(JsonNode m)
    {
        try
        {
            switch (m["t"]?.GetValue<string>())
            {
                case "tg.get":
                    PostTelegram();
                    return;

                case "tg.token":
                {
                    var token = (m["token"]?.GetValue<string>() ?? "").Trim();
                    if (!TelegramApi.LooksLikeToken(token))
                    {
                        _telegramNote = UiText.Pick("봇 토큰 모양이 아닙니다 — BotFather 가 준 123456:ABC… 전체를 넣으세요.",
                                                    "That is not a bot token — paste the whole 123456:ABC… value from BotFather.");
                        PostTelegram();
                        return;
                    }
                    _telegramNote = UiText.Pick("토큰을 확인하는 중…", "Checking the token…");
                    PostTelegram();
                    // **저장하기 전에 한 번 불러 본다.** 틀린 토큰을 저장해 두면 켤 때마다 거절만 쌓인다.
                    using (var api = new TelegramApi(token))
                    {
                        var me = await api.GetMeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
                        TelegramVault.Save(token);
                        _telegramNote = UiText.Pick($"@{me.Username} 봇 토큰을 저장했습니다.", $"Saved the token for @{me.Username}.");
                        Log(UiText.Pick($"텔레그램 봇 @{me.Username} 토큰을 저장했습니다(DPAPI 암호화).",
                                        $"Saved the Telegram bot token for @{me.Username} (DPAPI encrypted)."), "i");
                    }
                    ApplyTelegram();
                    return;
                }

                case "tg.token.clear":
                    StopTelegram();
                    TelegramVault.Clear();
                    _telegramNote = UiText.Pick("토큰을 지웠습니다.", "Token removed.");
                    ApplyTelegram();
                    return;

                case "tg.enable":
                {
                    var s = Settings.Load();
                    s.Telegram.Enabled = m["on"]?.GetValue<bool>() == true;
                    s.Save();
                    _telegramNote = s.Telegram.Enabled && TelegramVault.Load() is null
                        ? UiText.Pick("먼저 봇 토큰을 넣으세요.", "Enter a bot token first.") : "";
                    Log(s.Telegram.Enabled ? UiText.Pick("텔레그램 원격을 켰습니다.", "Telegram remote turned on.")
                                           : UiText.Pick("텔레그램 원격을 껐습니다.", "Telegram remote turned off."), "i");
                    ApplyTelegram();
                    return;
                }

                case "tg.pair":
                    if (_telegram is null)
                    {
                        _telegramNote = UiText.Pick("토큰을 넣고 [사용] 을 켠 뒤에 만들 수 있습니다.", "Enter a token and turn it on first.");
                        PostTelegram();
                        return;
                    }
                    _telegramNote = "";
                    _telegram.NewPairingCode();   // StateChanged → PostTelegram
                    return;

                case "tg.chat.remove":
                {
                    if (!long.TryParse(m["id"]?.GetValue<string>(), System.Globalization.NumberStyles.Integer,
                                       System.Globalization.CultureInfo.InvariantCulture, out var id)) return;
                    var s = Settings.Load();
                    var name = s.Telegram.Chats.FirstOrDefault(c => c.Id == id)?.Name ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    s.Telegram.Chats.RemoveAll(c => c.Id == id);
                    s.Save();
                    Log(UiText.Pick($"텔레그램 대화 {name} 의 허용을 거두었습니다.", $"Revoked Telegram chat {name}."), "i");
                    PostTelegram();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            var why = ex switch
            {
                TelegramException te => te.Description,
                TimeoutException => UiText.Pick("응답이 없습니다", "no response"),
                _ => ex.Message,
            };
            _telegramNote = UiText.Pick("텔레그램 설정을 바꾸지 못했습니다: ", "Could not change Telegram settings: ") + why;
            PostTelegram();
        }
    }

    // ── IRemoteHost ─────────────────────────────────────────────────────────

    private RemoteTab RemoteTabOf(string id, TerminalHost h) =>
        new(id, Alias(id) is { Length: > 0 } a ? a : TabName(h.Cwd), h.Cli.Display, h.Cli.Backend, h.Cwd, h.IsRunning,
            h.Checkpoint.TranscriptPath ?? h.ResumeTranscriptPath, h.Cli, h.LastOutputUtc);

    Task<IReadOnlyList<RemoteTab>> IRemoteHost.TabsAsync() =>
        Dispatcher.InvokeAsync<IReadOnlyList<RemoteTab>>(() =>
            _order.Where(id => _tabs.TryGetValue(id, out var h) && !h.IsLogin)
                  .Select(id => RemoteTabOf(id, _tabs[id]))
                  .ToList()).Task;

    Task<string?> IRemoteHost.SendAsync(string tabId, string text, IReadOnlyList<string> files) =>
        Dispatcher.InvokeAsync<string?>(() =>
        {
            if (!_tabs.TryGetValue(tabId, out var h) || h.IsLogin) return UiText.Pick("탭이 없습니다.", "No such tab.");
            return h.SendRemoteTurn(text, files, out var error) ? null : error;
        }).Task;

    Task<bool> IRemoteHost.KeysAsync(string tabId, string data) =>
        Dispatcher.InvokeAsync(() => _tabs.TryGetValue(tabId, out var h) && !h.IsLogin && h.WriteKeys(data)).Task;

    /// <summary>
    /// 화면(xterm)에 보이는 글. 터미널 출력은 날것의 제어 문자열이라 그대로는 읽을 수 없다 —
    /// 화면이 이미 해석해 둔 버퍼를 읽는다(index.html 의 window.agentRemote).
    /// </summary>
    async Task<RemoteScreen?> IRemoteHost.ScreenAsync(string tabId)
    {
        try
        {
            var script = "(window.agentRemote ? window.agentRemote.screen(" + JsonSerializer.Serialize(tabId) + ") : null)";
            var json = await Dispatcher.InvokeAsync(() =>
                View.CoreWebView2 is { } core ? core.ExecuteScriptAsync(script) : Task.FromResult("null")).Task.Unwrap()
                .WaitAsync(TimeSpan.FromSeconds(10));
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            return new RemoteScreen(o["text"]?.GetValue<string>() ?? "", o["focus"]?.GetValue<bool>() == true);
        }
        catch { return null; }
    }

    Task<(string? TabId, string? Error)> IRemoteHost.OpenAsync(string folder, string backend) =>
        Dispatcher.InvokeAsync<(string?, string?)>(() =>
        {
            var cfg = Settings.Load();
            var cli = CliProfile.For(backend.Length > 0 ? backend : cfg.LastBackend);
            if (cli is null) return (null, UiText.Pick("알 수 없는 CLI 입니다.", "Unknown CLI."));
            if (!Directory.Exists(folder)) return (null, UiText.Pick("폴더가 없습니다.", "Folder not found."));
            // 기본 계정으로 연다. 계정을 고르는 일은 앱 화면의 몫이다 — 원격에서 엉뚱한 계정의 한도를 쓰지 않게.
            OpenTab(folder, cli.Backend, ResumeMode.New);
            return ("t" + _nextTab, null);
        }).Task;

    Task<IReadOnlyList<string>> IRemoteHost.RecentFoldersAsync(int max) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var s = Settings.Load();
            var list = new List<string>();
            void Add(string? f)
            {
                if (list.Count >= max || string.IsNullOrWhiteSpace(f) || !Directory.Exists(f)) return;
                if (!list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
            }
            Add(s.LastFolder);
            try { foreach (var e in SessionStore.Recent(s, 40)) Add(e.Cwd); } catch { }
            return list;
        });

    bool IRemoteHost.IsAllowed(long chat) => Settings.Load().Telegram.Chats.Any(c => c.Id == chat);

    /// <summary>
    /// 페어링한 대화를 적는다. **UI 스레드에서** 한다 — 설정 화면의 [저장] 도 settings.json 을 통째로 다시 쓰므로,
    /// 두 스레드가 동시에 쓰면 한쪽이 다른 쪽을 덮는다.
    /// </summary>
    void IRemoteHost.AddChat(long chat, string name) => Dispatcher.Invoke(() =>
    {
        var s = Settings.Load();
        s.Telegram.Chats.RemoveAll(c => c.Id == chat);
        s.Telegram.Chats.Add(new TelegramChat { Id = chat, Name = name, AddedUtc = DateTime.UtcNow });
        s.Save();
    });

    bool IRemoteHost.English => Settings.Load().UiLanguage == "en";

    void IRemoteHost.Log(string message, string level) => Dispatcher.InvokeAsync(() => Log(message, level));

    void IRemoteHost.StateChanged() => Dispatcher.InvokeAsync(PostTelegram);
}
