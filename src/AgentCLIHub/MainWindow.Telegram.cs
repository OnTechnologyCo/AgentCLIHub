using System.Globalization;
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
            showQuickBar = s.ShowQuickBar,
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
                id = c.Id.ToString(CultureInfo.InvariantCulture),
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

                case "tg.quickbar":
                {
                    var s = Settings.Load();
                    s.Telegram.ShowQuickBar = m["on"]?.GetValue<bool>() != false;
                    s.Save();
                    PostTelegram();
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
                    if (!long.TryParse(m["id"]?.GetValue<string>(), NumberStyles.Integer,
                                       CultureInfo.InvariantCulture, out var id)) return;
                    var s = Settings.Load();
                    var name = s.Telegram.Chats.FirstOrDefault(c => c.Id == id)?.Name ?? id.ToString(CultureInfo.InvariantCulture);
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
            h.Checkpoint.TranscriptPath ?? h.ResumeTranscriptPath, h.Cli, h.LastOutputUtc,
            h.Account, AccountLabel(h.Cli, h.Account));

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
        ((IRemoteHost)this).OpenAsync(folder, backend, "");

    Task<(string? TabId, string? Error)> IRemoteHost.OpenAsync(string folder, string backend, string account) =>
        Dispatcher.InvokeAsync<(string?, string?)>(() =>
        {
            var cfg = Settings.Load();
            var cli = CliProfile.For(backend.Length > 0 ? backend : cfg.LastBackend);
            if (cli is null) return (null, UiText.Pick("알 수 없는 CLI 입니다.", "Unknown CLI."));
            if (!Directory.Exists(folder)) return (null, UiText.Pick("폴더가 없습니다.", "Folder not found."));
            if (!CliLocator.Locate(cli, cli.CachedPath(cfg)).Found) return (null, cli.NotFoundMessage);
            var acc = Accounts.Resolve(cfg, cli, account ?? "");
            if (acc != (account ?? "")) return (null, UiText.Pick("선택한 계정을 찾지 못했습니다.", "Account not found."));
            var to = Accounts.View(cfg, cli, acc);
            if (!to.IsDefault && !to.LoggedIn)
                return (null, UiText.Pick($"'{to.Label}' 계정은 먼저 로그인이 필요합니다.", $"Account '{to.Label}' requires sign-in first."));

            cfg.LastFolder = folder;
            cfg.LastBackend = cli.Backend;
            cfg.LastAccount = acc;
            cfg.Save();

            var id = OpenTab(folder, cli.Backend, ResumeMode.New, account: acc);
            return (id, null);
        }).Task;

    Task<IReadOnlyList<RemoteAccountChoice>> IRemoteHost.ListAccountsAsync(string backend) =>
        Dispatcher.InvokeAsync<IReadOnlyList<RemoteAccountChoice>>(() =>
        {
            var cfg = Settings.Load();
            var cli = CliProfile.For(backend);
            if (cli is null) return [];
            var loc = CliLocator.Locate(cli, cli.CachedPath(cfg));
            return Accounts.Of(cfg, cli)
                .Select(a => new RemoteAccountChoice(
                    cli.Backend, cli.Display, a.Id, a.Label, a.Email,
                    loc.Found && (a.IsDefault || a.LoggedIn), a.Note))
                .ToList();
        }).Task;

    Task<string?> IRemoteHost.CloseTabAsync(string tabId) =>
        Dispatcher.InvokeAsync<string?>(() =>
        {
            if (!_tabs.TryGetValue(tabId, out var h) || h.IsLogin)
                return UiText.Pick("탭이 없습니다.", "No such tab.");
            CloseTab(tabId);
            Post(new { t = "tab.closed", id = tabId });
            return null;
        }).Task;

    async Task<string?> IRemoteHost.RestartTabAsync(string tabId)
    {
        var info = await Dispatcher.InvokeAsync<(TerminalHost? Host, HashSet<string> Claimed, string? Error)>(() =>
        {
            if (!_tabs.TryGetValue(tabId, out var h) || h.IsLogin)
                return (null, [], UiText.Pick("탭이 없습니다.", "No such tab."));
            var cfg = Settings.Load();
            if (!CliLocator.Locate(h.Cli, h.Cli.CachedPath(cfg)).Found)
                return (null, [], h.Cli.NotFoundMessage);
            return (h, Claimed(h), null);
        }).Task;
        if (info.Host is null) return info.Error;

        var host = info.Host;
        var mode = await Task.Run(() =>
        {
            try { host.CaptureSession(info.Claimed, discover: false); } catch { }
            var sid = host.KnownSessionId ?? host.Checkpoint.Id;
            var tp = host.Checkpoint.TranscriptPath ?? host.ResumeTranscriptPath;
            var rm = !string.IsNullOrEmpty(sid) ? ResumeMode.Session(sid, tp)
                   : !string.IsNullOrEmpty(host.HandoffPath) ? ResumeMode.Handoff(host.HandoffPath)
                   : host.Checkpoint.Fresh == true ? ResumeMode.New
                   : ResumeMode.Last;
            try { host.Stop(UiText.Pick("텔레그램에서 다시 시작했습니다.", "Restarted from Telegram."), retire: true); } catch { }
            return rm;
        });

        return await Dispatcher.InvokeAsync<string?>(() =>
        {
            if (!_tabs.TryGetValue(tabId, out var cur) || !ReferenceEquals(cur, host))
                return UiText.Pick("탭이 이미 바뀌었거나 닫혔습니다.", "Tab changed or closed.");
            Swap(tabId, host, host.Cli, host.Account, mode,
                 UiText.Pick($"[Telegram] {TabName(host.Cwd)} 탭의 CLI 를 다시 시작했습니다.",
                             $"[Telegram] Restarted CLI in {TabName(host.Cwd)}."));
            return null;
        }).Task;
    }

    Task<string?> IRemoteHost.RenameTabAsync(string tabId, string newName) =>
        Dispatcher.InvokeAsync<string?>(() =>
        {
            if (!_tabs.TryGetValue(tabId, out var h) || h.IsLogin)
                return UiText.Pick("탭이 없습니다.", "No such tab.");
            RenameTab(tabId, newName);
            return null;
        }).Task;

    Task<IReadOnlyList<RemoteAccountChoice>> IRemoteHost.ListSwitchTargetsAsync(string tabId) =>
        Dispatcher.InvokeAsync<IReadOnlyList<RemoteAccountChoice>>(() =>
        {
            if (!_tabs.TryGetValue(tabId, out var h) || h.IsLogin || !h.Cli.SupportsTransfer)
                return [];
            var c = Settings.Load();
            return CliProfile.All.Where(p => p.SupportsTransfer).SelectMany(p =>
            {
                var loc = CliLocator.Locate(p, p.CachedPath(c));
                return Accounts.Of(c, p)
                    .Where(a => !(p == h.Cli && a.Id == h.Account))
                    .Select(a => new RemoteAccountChoice(
                        p.Backend, p.Display, a.Id, a.Label, a.Email,
                        loc.Found && (a.IsDefault || a.LoggedIn), a.Note));
            }).ToList();
        }).Task;

    async Task<string?> IRemoteHost.SwitchTabAsync(string tabId, string backend, string account)
    {
        var check = await Dispatcher.InvokeAsync<(TerminalHost? Host, CliProfile? Target, string Acc, string From, string Dest, HashSet<string> Claimed, string? Error)>(() =>
        {
            if (!_tabs.TryGetValue(tabId, out var h) || h.IsLogin)
                return (null, null, "", "", "", [], UiText.Pick("탭이 없습니다.", "No such tab."));
            var target = CliProfile.For(backend);
            if (target is null || !target.SupportsTransfer)
                return (null, null, "", "", "", [], UiText.Pick("알 수 없는 CLI 입니다.", "Unknown CLI."));
            var c = Settings.Load();
            var acc = Accounts.Resolve(c, target, account);
            if (acc != (account ?? ""))
                return (null, null, "", "", "", [], UiText.Pick("넘길 계정을 찾지 못했습니다.", "Account not found."));
            if (target == h.Cli && acc == h.Account)
                return (null, null, "", "", "", [], UiText.Pick("이미 사용 중인 계정입니다.", "Already using that account."));
            var to = Accounts.View(c, target, acc);
            if (!to.IsDefault && !to.LoggedIn)
                return (null, null, "", "", "", [], UiText.Pick($"'{to.Label}' 계정은 먼저 로그인이 필요합니다.", $"Account '{to.Label}' requires sign-in."));
            if (!CliLocator.Locate(target, target.CachedPath(c)).Found)
                return (null, null, "", "", "", [], target.NotFoundMessage);

            var from = $"{h.Cli.Display}({Accounts.View(c, h.Cli, h.Account).Label})";
            var dest = $"{target.Display}({to.Label})";
            Post(new { t = "switch.busy", id = tabId, to = dest });
            return (h, target, acc, from, dest, Claimed(h), null);
        }).Task;
        if (check.Host is null || check.Target is null) return check.Error;

        var host = check.Host;
        var targetCli = check.Target;
        var prepared = await Task.Run<(ResumeMode? Mode, string Note, string? Error)>(() =>
        {
            try
            {
                var (src, tr) = TransferSource(host, check.Claimed);
                var home = targetCli.Kind == CliKind.Claude ? ClaudeTransferHome(check.Acc) : Accounts.CodexHomeOf(check.Acc);
                var (mode, note) = ConversationTransfer.Prepare(host.Cli, src, tr, host.Cwd, targetCli, home, check.From);
                try { host.Stop($"{check.Dest} 로 넘겼습니다.", retire: true); } catch { }
                return (mode, note, null);
            }
            catch (Exception ex)
            {
                return (null, "", ex.Message);
            }
        });

        if (prepared.Mode is null)
        {
            Post(new { t = "switch.failed", id = tabId });
            return prepared.Error ?? UiText.Pick("대화를 넘기지 못했습니다.", "Could not transfer conversation.");
        }

        var resolvedMode = prepared.Mode.Value;
        return await Dispatcher.InvokeAsync<string?>(() =>
        {
            Swap(tabId, host, targetCli, check.Acc, resolvedMode, $"{check.From} → {check.Dest}: {prepared.Note}");
            return null;
        }).Task;
    }

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

    Task<IReadOnlyList<RemoteSessionItem>> IRemoteHost.RecentSessionsAsync(int max, string? backend) =>
        Task.Run<IReadOnlyList<RemoteSessionItem>>(() =>
        {
            var s = Settings.Load();
            var entries = SessionStore.Recent(s, Math.Max(max, 40));
            _ = Dispatcher.BeginInvoke(() => _historyEntries = entries);
            IEnumerable<SessionEntry> filtered = entries;
            if (!string.IsNullOrWhiteSpace(backend))
                filtered = filtered.Where(e => string.Equals(e.Backend, backend, StringComparison.OrdinalIgnoreCase));

            return filtered.Take(max).Select(e =>
            {
                var cli = CliProfile.ForOrDefault(e.Backend);
                var accName = Accounts.Resolve(s, cli, e.Account) == e.Account
                    ? Accounts.View(s, cli, e.Account).Label
                    : "";
                if (e.Account.Length == 0 && accName == "기본") accName = "";
                return new RemoteSessionItem(e.Backend, cli.Display, e.Id, e.Cwd, e.When, e.Title, e.Account, accName);
            }).ToList();
        });

    async Task<(string? TabId, string? Error)> IRemoteHost.ResumeSessionAsync(string backend, string cwd, string sessionId, string account)
    {
        if (string.IsNullOrWhiteSpace(cwd) || string.IsNullOrWhiteSpace(sessionId))
            return (null, UiText.Pick("대화 정보가 올바르지 않습니다.", "Invalid session."));
        if (!Directory.Exists(cwd))
            return (null, UiText.Pick($"폴더가 없어 이어갈 수 없습니다 — {cwd}", $"Folder not found — {cwd}"));

        var s = Settings.Load();
        var cli = CliProfile.ForOrDefault(backend);
        if (!CliLocator.Locate(cli, cli.CachedPath(s)).Found)
            return (null, cli.NotFoundMessage);
        var resolved = Accounts.Resolve(s, cli, account ?? "");
        if (resolved != (account ?? ""))
            return (null, UiText.Pick("이 대화의 계정을 찾지 못했습니다.", "Account for this conversation was not found."));

        var cached = await Dispatcher.InvokeAsync(() => _historyEntries).Task;
        var entry = cached.FirstOrDefault(e => e.Backend == backend && e.Id == sessionId && e.Account == resolved && e.Cwd == cwd);
        entry ??= await Task.Run(() => SessionStore.Recent(s, 60)
            .FirstOrDefault(e => e.Backend == backend && e.Id == sessionId && e.Account == resolved && e.Cwd == cwd));

        return await Dispatcher.InvokeAsync<(string?, string?)>(() =>
        {
            var id = OpenTab(cwd, cli.Backend, ResumeMode.Session(sessionId, entry?.TranscriptPath), account: resolved);
            return (id, null);
        }).Task;
    }

    async Task<IReadOnlyList<RemoteUsageItem>> IRemoteHost.GetUsageAsync()
    {
        var c = await Dispatcher.InvokeAsync(Settings.Load).Task;
        var allAccounts = Accounts.All(c).ToList();
        var agyCli = CliLocator.Locate(CliProfile.Antigravity, c.AntigravityCliPath);

        static async Task<IReadOnlyList<UsageWindow>?> WithTimeout(Task<IReadOnlyList<UsageWindow>?> task)
        {
            try { return await task.WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false); }
            catch { return null; }
        }

        var tasks = allAccounts.Select(async a =>
        {
            IReadOnlyList<UsageWindow>? wins = null;
            var note = "";
            if (a.Cli.Kind == CliKind.Claude)
            {
                if (a.IsDefault) wins = await WithTimeout(UsageProbe.ClaudeAsync()).ConfigureAwait(false);
                else
                {
                    var mode = Accounts.ClaudeModeOf(a.Id);
                    if (mode == ClaudeMode.Login) wins = await WithTimeout(UsageProbe.ClaudeAsync(home: Accounts.ClaudeHomeOf(a.Id))).ConfigureAwait(false);
                    else if (mode == ClaudeMode.Token && Accounts.ClaudeToken(a.Id) is { } tok)
                    {
                        wins = await WithTimeout(UsageProbe.ClaudeAsync(tok)).ConfigureAwait(false);
                        if (wins is null) note = UiText.Pick("setup-token 계정(사용량 API 미지원)", "setup-token account (usage API unsupported)");
                    }
                    else note = UiText.Pick("로그인 필요", "sign-in required");
                }
            }
            else if (a.Cli.Kind == CliKind.Codex)
            {
                var home = a.IsDefault ? CliProfile.CodexHome : Accounts.CodexHomeOf(a.Id);
                wins = await WithTimeout(UsageProbe.CodexAccountAsync(home)).ConfigureAwait(false);
            }
            else if (a.Cli.Kind == CliKind.Antigravity)
            {
                if (!agyCli.Found) note = UiText.Pick("CLI 미설치", "CLI not found");
                else wins = await WithTimeout(UsageProbe.AntigravityAsync(agyCli, a.Id)).ConfigureAwait(false);
            }

            PostUsage(a.Cli.Backend, a.Id, wins);
            var label = a.IsDefault && a.Label == "기본" ? UiText.Pick("기본", "Default") : a.Label;
            return new RemoteUsageItem(a.Cli.Display, a.Cli.Backend, label, a.Email, wins, note);
        }).ToArray();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static string DescribeScheduleWhen(ScheduledJob j, bool en)
    {
        string[] koDays = ["일", "월", "화", "수", "목", "금", "토"];
        string[] enDays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
        return j.Repeat switch
        {
            "interval" => j.Unit == "h"
                ? (en ? $"Every {j.Every} h" : $"{j.Every}시간마다")
                : (en ? $"Every {j.Every} min" : $"{j.Every}분마다"),
            "daily" => en ? $"Daily {j.Time}" : $"매일 {j.Time}",
            "weekly" => en
                ? $"Weekly {string.Join(",", j.Days.Where(d => d is >= 0 and <= 6).Select(d => enDays[d]))} {j.Time}"
                : $"매주 {string.Join(",", j.Days.Where(d => d is >= 0 and <= 6).Select(d => koDays[d]))} {j.Time}",
            "once" => (en ? "Once · " : "한 번 · ") + j.At.Replace('T', ' '),
            "cron" => "cron · " + j.Cron,
            _ => j.Repeat,
        };
    }

    private static string DescribeScheduleWhere(ScheduledJob j, Settings cfg, bool en)
    {
        if (j.Target == "tab") return (en ? "Tab: " : "탭: ") + j.TabName;
        var cli = CliProfile.For(j.Backend)?.Display ?? j.Backend;
        var parts = new List<string> { (en ? "New tab: " : "새 탭: ") + j.Folder, cli };
        if (j.Account.Length > 0 && CliProfile.For(j.Backend) is { } p && Accounts.Resolve(cfg, p, j.Account) == j.Account)
            parts.Add(Accounts.View(cfg, p, j.Account).Label);
        if (j.Model.Length > 0) parts.Add(j.Model);
        if (j.Effort.Length > 0) parts.Add(j.Effort);
        if (j.CloseTab) parts.Add(en ? "close when done" : "끝나면 닫기");
        return string.Join(" · ", parts);
    }

    Task<IReadOnlyList<RemoteScheduleItem>> IRemoteHost.GetSchedulesAsync() =>
        Dispatcher.InvokeAsync<IReadOnlyList<RemoteScheduleItem>>(() =>
        {
            var cfg = Settings.Load();
            var en = cfg.UiLanguage == "en";
            var snap = (_scheduler?.Snapshot() ?? []).ToDictionary(s => s.Id, StringComparer.Ordinal);
            return cfg.Schedules.Select(j =>
            {
                snap.TryGetValue(j.Id, out var st);
                var last = _scheduleRuns.LastOrDefault(r => r.JobId == j.Id);
                DateTime? lastLocal = last?.FinishedUtc is { } fu ? DateTime.SpecifyKind(fu, DateTimeKind.Utc).ToLocalTime()
                    : last?.StartedUtc is { } su ? DateTime.SpecifyKind(su, DateTimeKind.Utc).ToLocalTime()
                    : null;
                return new RemoteScheduleItem(
                    j.Id, j.Name, j.Enabled,
                    DescribeScheduleWhen(j, en),
                    DescribeScheduleWhere(j, cfg, en),
                    st?.Running == true,
                    st?.NextLocal,
                    last?.Status,
                    lastLocal,
                    last?.Answer,
                    last?.Error);
            }).ToList();
        }).Task;

    Task<string?> IRemoteHost.SetScheduleEnabledAsync(string jobId, bool enabled) =>
        Dispatcher.InvokeAsync<string?>(() =>
        {
            var s = Settings.Load();
            var job = s.Schedules.FirstOrDefault(j => j.Id == jobId);
            if (job is null) return UiText.Pick("예약 작업을 찾지 못했습니다.", "Scheduled task not found.");
            if (enabled && ValidateJob(job, s) is { } error)
                return error;
            job.Enabled = enabled;
            if (enabled && job.Repeat == "interval") job.AnchorUtc = DateTime.UtcNow;
            s.Save();
            _scheduleNote = "";
            _scheduler?.Update(s.Schedules);
            Log(enabled ? UiText.Pick($"[Telegram] 예약 작업 '{job.Name}' 을 켰습니다.", $"[Telegram] Enabled scheduled task '{job.Name}'.")
                        : UiText.Pick($"[Telegram] 예약 작업 '{job.Name}' 을 껐습니다.", $"[Telegram] Disabled scheduled task '{job.Name}'."), "i");
            PostSchedules();
            return null;
        }).Task;

    Task<string?> IRemoteHost.RunScheduleNowAsync(string jobId) =>
        Dispatcher.InvokeAsync<string?>(() =>
        {
            var s = Settings.Load();
            var job = s.Schedules.FirstOrDefault(j => j.Id == jobId);
            if (job is null || _scheduler is null)
                return UiText.Pick("예약 작업을 찾지 못했습니다.", "Scheduled task not found.");
            if (ValidateJob(job, s) is { } error && !(job.Repeat == "once" && error == "이미 지난 시각입니다."))
                return error;
            _scheduleNote = "";
            _scheduler.RunNow(jobId);
            Log(UiText.Pick($"[Telegram] 예약 작업 '{job.Name}' 을 즉시 실행합니다.", $"[Telegram] Running scheduled task '{job.Name}' now."), "i");
            PostSchedules();
            return null;
        }).Task;

    bool IRemoteHost.ShowQuickBar => Settings.Load().Telegram.ShowQuickBar;

    void IRemoteHost.SetShowQuickBar(bool show) => Dispatcher.Invoke(() =>
    {
        var s = Settings.Load();
        s.Telegram.ShowQuickBar = show;
        s.Save();
        PostTelegram();
    });

    private static string LocalizePermLabel(string label, bool en) => !en ? label : label switch
    {
        "권한" => "Permission",
        "샌드박스" => "Sandbox",
        "승인" => "Approval",
        _ => label,
    };

    private static string GetPermValue(Settings c, string key) => key switch
    {
        "ClaudeCodePermissionMode" => c.ClaudeCodePermissionMode,
        "CodexSandbox" => c.CodexSandbox,
        "CodexApproval" => c.CodexApproval,
        "AntigravityPermission" => c.AntigravityPermission,
        _ => "",
    };

    Task<RemoteAppSettings?> IRemoteHost.GetAppSettingsAsync() =>
        Dispatcher.InvokeAsync<RemoteAppSettings?>(() =>
        {
            var c = Settings.Load();
            var en = c.UiLanguage == "en";
            var clis = CliProfile.All.Select(p =>
            {
                var model = p.ModelOf(c);
                var effort = p.EffortOf(c);
                var models = p.Kind == CliKind.Antigravity
                    ? _antigravityModels.Select(m => m.Id).ToList()
                    : CliCatalog.Models(p.Backend).ToList();
                var efforts = CliCatalog.Efforts(p.Backend, model).ToList();
                var perms = CliCatalog.Permissions(p.Backend)
                    .Select(f => new RemotePermItem(f.Key, LocalizePermLabel(f.Label, en), GetPermValue(c, f.Key), f.Choices))
                    .ToList();
                return new RemoteCliSetting(p.Backend, p.Display, model, effort, perms, models, efforts);
            }).ToList();

            return new RemoteAppSettings(
                IsShownToUser,
                c.Pet?.Enabled == true,
                c.CloseToTray,
                c.StartInTray,
                StartupRegistration.IsEnabled(),
                c.Telegram.ShowQuickBar,
                c.UiLanguage,
                clis);
        }).Task;

    Task<string?> IRemoteHost.ApplyAppSettingAsync(string key, string value) =>
        Dispatcher.InvokeAsync<string?>(() =>
        {
            try
            {
                if (key == "window")
                {
                    switch (value)
                    {
                        case "show":
                            ShowFromTray();
                            return null;
                        case "hide":
                            HideToTray();
                            return null;
                        case "settings":
                            ShowFromTray();
                            Post(new { t = "conf.open" });
                            return null;
                    }
                    return UiText.Pick("알 수 없는 창 동작입니다.", "Unknown window action.");
                }

                if (key == "runAtStartup")
                {
                    SetStartupRegistration(string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
                    PostConf();
                    return null;
                }

                var c = Settings.Load();
                switch (key)
                {
                    case "pet":
                        c.Pet ??= new PetSettings();
                        c.Pet.Enabled = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                        c.Save();
                        ApplyPet();
                        PostConf();
                        return null;

                    case "closeToTray":
                        c.CloseToTray = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                        c.Save();
                        UpdateTrayTooltip();
                        Post(new { t = "conf.tray.state", startInTray = c.StartInTray, closeToTray = c.CloseToTray });
                        PostConf();
                        return null;

                    case "startInTray":
                        c.StartInTray = Settings.NormalizeStartInTray(value);
                        c.Save();
                        UpdateTrayTooltip();
                        Post(new { t = "conf.tray.state", startInTray = c.StartInTray, closeToTray = c.CloseToTray });
                        PostConf();
                        return null;

                    case "model:claude_code":
                        c.ClaudeModel = CliCatalog.ClampModel(CliProfile.Claude.Backend, value, Log);
                        c.ClaudeEffort = CliCatalog.ClampEffort(CliProfile.Claude.Backend, c.ClaudeModel, c.ClaudeEffort, Log);
                        break;
                    case "model:codex_cli":
                        c.CodexModel = CliCatalog.ClampModel(CliProfile.Codex.Backend, value, Log);
                        c.CodexEffort = CliCatalog.ClampEffort(CliProfile.Codex.Backend, c.CodexModel, c.CodexEffort, Log);
                        break;
                    case "model:antigravity_cli":
                        c.AntigravityModel = CliCatalog.ClampModel(CliProfile.Antigravity.Backend, value, Log);
                        c.AntigravityEffort = CliCatalog.ClampEffort(CliProfile.Antigravity.Backend, c.AntigravityModel, c.AntigravityEffort, Log);
                        break;

                    case "effort:claude_code":
                        c.ClaudeEffort = CliCatalog.ClampEffort(CliProfile.Claude.Backend, c.ClaudeModel, value, Log);
                        break;
                    case "effort:codex_cli":
                        c.CodexEffort = CliCatalog.ClampEffort(CliProfile.Codex.Backend, c.CodexModel, value, Log);
                        break;
                    case "effort:antigravity_cli":
                        c.AntigravityEffort = CliCatalog.ClampEffort(CliProfile.Antigravity.Backend, c.AntigravityModel, value, Log);
                        break;

                    case "perm:ClaudeCodePermissionMode":
                        c.ClaudeCodePermissionMode = CliCatalog.ClampPermission("ClaudeCodePermissionMode", value);
                        break;
                    case "perm:CodexSandbox":
                        c.CodexSandbox = CliCatalog.ClampPermission("CodexSandbox", value);
                        break;
                    case "perm:CodexApproval":
                        c.CodexApproval = CliCatalog.ClampPermission("CodexApproval", value);
                        break;
                    case "perm:AntigravityPermission":
                        c.AntigravityPermission = CliCatalog.ClampPermission("AntigravityPermission", value);
                        break;

                    default:
                        return UiText.Pick("지원하지 않는 설정 항목입니다.", "Unsupported setting.");
                }
                c.Save();
                PostConf();
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }).Task;

    bool IRemoteHost.IsAllowed(long chat) => Settings.Load().Telegram.Chats.Any(c => c.Id == chat);

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
