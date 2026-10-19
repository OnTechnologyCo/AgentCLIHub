using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

using Microsoft.Web.WebView2.Core;

namespace AgentCLIHub;

/// <summary>
/// 창 하나 = WebView2 하나. 여기가 하는 일은 셋뿐이다.
///   1. 화면(web/index.html)을 띄우고 메시지를 주고받는다
///   2. Windows 대화상자를 연다 — 폴더 고르기 · 파일 첨부 · CLI 경로 고르기
///   3. **대화 탭을 관리한다** — 탭 하나에 <see cref="TerminalHost"/> 하나
///
/// **대화 내용·이력·모델 상태는 하나도 안 들고 있다.** 그것은 CLI 자신의 몫이다 —
/// 우리가 흉내 내기 시작하면 CLI 가 아는 것과 우리가 아는 것이 어긋나고,
/// 어긋난 쪽은 화면에 안 보인다. 이 앱은 CLI 를 띄우는 껍데기다.
/// </summary>
public partial class MainWindow : Window
{
    private const string Host = "agentclihub.local";

    /// <summary>
    /// 열려 있는 탭. 키는 화면과 나누는 탭 번호다.
    /// **순서를 지킨다**(Dictionary 대신 List 를 함께 쓰지 않고 삽입 순서를 믿는다) —
    /// .NET 의 Dictionary 는 순서를 보장하지 않지만, 여기서 순서를 쓰는 곳은
    /// 저장(<see cref="SaveTabs"/>)뿐이고 그쪽은 화면이 준 순서를 쓰지 않는다.
    /// </summary>
    private readonly Dictionary<string, TerminalHost> _tabs = new(StringComparer.Ordinal);

    /// <summary>브라우저 탭. CLI 탭과 **같은 번호 공간**을 쓴다 — 화면에서는 한 줄에 섞여 있다.</summary>
    private readonly Dictionary<string, BrowserHost> _browsers = new(StringComparer.Ordinal);

    /// <summary>
    /// 탭이 열린 순서. 두 종류가 사전 둘로 갈려 있어 순서는 여기서만 안다 —
    /// 이것이 없으면 앱을 껐다 켤 때 CLI 탭이 전부 앞으로, 브라우저가 전부 뒤로 몰린다.
    /// </summary>
    private readonly List<string> _order = [];

    /// <summary>탭 번호 발급기. 화면이 만든 번호를 믿지 않는다 — 우리가 주고 화면이 받는다.</summary>
    private int _nextTab;

    /// <summary>
    /// WebView2 프로필. **창과 브라우저 탭이 같은 것을 쓴다** — 프로필이 갈리면
    /// 쿠키도 갈려서 Jira SSO 를 탭마다 다시 해야 한다.
    /// </summary>
    private CoreWebView2Environment? _env;

    /// <summary>
    /// 사용자가 손으로 붙인 탭 이름. **탭 객체가 아니라 여기 둔다** — 브라우저 탭의
    /// 이름은 페이지가 바뀔 때마다 문서 제목으로 덮어써지므로, 같은 칸에 담으면
    /// 이름을 붙인 뒤 링크 한 번에 사라진다. 비어 있으면 자동 이름을 쓴다는 뜻이다.
    /// </summary>
    private readonly Dictionary<string, string> _alias = [];

    /// <summary>탭 안의 CLI 가 브라우저를 부르는 창구. 창이 살아 있는 동안만 열린다.</summary>
    private BrowserBridge? _bridge;

    /// <summary>계정 사용량을 주기적으로 다시 읽는 시계. <c>ui.ready</c> 뒤에만 돈다.</summary>
    private System.Windows.Threading.DispatcherTimer? _usage;

    /// <summary>
    /// 창 제목에 붙는 버전. **어셈블리에서 읽는다** — 어딘가에 손으로 적어 두면 csproj 의
    /// 버전을 올릴 때마다 그 한 곳이 남아 거짓말을 하고, 그 거짓말은 "무슨 버전 쓰세요" 를
    /// 물어보는 자리에서만 드러난다(그때는 이미 엉뚱한 것을 고치고 있다).
    ///
    /// 네 자리 중 끝자리는 우리가 안 쓴다(csproj 는 1.2.0 을 적고 .NET 이 .0 을 붙인다).
    /// 있는 그대로 보여 주면 설치 파일 이름(1.2.0.0)과 창 제목이 달라 보인다.
    /// </summary>
    public static string AppVersion { get; } = ReadVersion();

    private static string ReadVersion()
    {
        try
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "" : (v.Revision > 0 ? v.ToString(4) : v.ToString(3));
        }
        catch { return ""; }   // 버전을 못 읽는다고 창이 안 뜨면 안 된다
    }

    public MainWindow()
    {
        InitializeComponent();

        // XAML 의 제목은 버전 없는 이름이다. 못 읽었으면 그대로 둔다 — 이름 뒤에
        // 빈 칸만 붙은 제목은 뭔가 깨진 것으로 보인다.
        if (AppVersion.Length > 0) Title = $"AgentCLIHub {AppVersion}";

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>
    /// 지난번 탭을 이미 되살렸나. **한 번만 한다** — 껍데기가 다시 뜨면 ui.ready 가
    /// 또 오는데, 그때 다시 되살리면 탭이 한 벌 더 생긴다(ReattachTabs 참조).
    /// </summary>
    private bool _restored;

    /// <summary>닫는 일이 한 번 끝났다는 표식. 아래에서 다시 부른 <c>Close()</c> 를 그냥 통과시킨다.</summary>
    private bool _closingDone;

    /// <summary>
    /// 닫을 때. **한 번 물렸다가 다시 닫는다** — 세션 쿠키를 꺼내는 일이 비동기인데
    /// <c>Closing</c> 은 동기다. 여기서 시작만 하고 넘어가면 창이 먼저 죽어
    /// <c>CoreWebView2</c> 가 사라지고, 저장은 절반에서 끊긴다.
    /// </summary>
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // **페이지가 창을 닫으려는 것이면 거절한다.** WPF WebView2 컨트롤은 페이지의 window.close() 를 받으면
        // 자기를 품은 창(= 이 메인 창)을 닫는다 — 로그인 팝업이 인증을 마치고 스스로 닫히는 순간 앱이 통째로
        // 꺼진다. 탭마다 떼어 두지만(BrowserHost.DetachParentWindowClose) 못 뗐을 때의 울타리다.
        // 그 탭은 우리 처리기가 따로 닫는다.
        if (BrowserHost.IsPageCloseRequest())
        {
            e.Cancel = true;
            return;
        }

        if (_closingDone) return;
        e.Cancel = true;

        try
        {
            _usage?.Stop();
            _modelQuery?.Cancel();
            try { _stallWatch?.Dispose(); } catch { }
            _stallWatch = null;
            SaveTabs();

            if (Settings.Load().KeepSignedIn && View.CoreWebView2?.Profile.CookieManager is { } cm)
                // 저장이 늦어져도 닫히기는 해야 한다. 붙들려 있으면 "창이 안 닫힌다" 가 된다.
                await CookieVault.SaveAsync(cm, Log).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch { /* 저장 실패가 종료를 막지 않는다 */ }

        _closingDone = true;
        _bridge?.Dispose();
        foreach (var t in _tabs.Values) { try { t.Dispose(); } catch { } }

        // **CLI 를 다 끝낸 뒤에 뜬다.** 그래야 마지막으로 쓴 상태가 사본에 들어간다.
        try { if (Settings.Load().GuardClaudeLogin) ClaudeAuthGuard.Guard(mayRestore: false, Log); }
        catch { }
        foreach (var b in _browsers.Values) { try { b.Dispose(); } catch { } }
        _tabs.Clear();
        _browsers.Clear();
        _order.Clear();
        Close();
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            _env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(Settings.DataDir, "webview2"));
            await View.EnsureCoreWebView2Async(_env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                UiText.Pick("WebView2 런타임을 시작하지 못했습니다.\n\nMicrosoft Edge WebView2 Runtime 이 설치되어 있어야 합니다.\n\n",
                    "Could not start WebView2.\n\nMicrosoft Edge WebView2 Runtime must be installed.\n\n") + ex.Message,
                "AgentCLIHub", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // 로컬 번들만 서비스한다. Deny = 다른 origin 에서 이 파일들에 접근 못 한다.
        // 폐쇄망에서도 떠야 하므로 CDN 을 안 쓴다 — xterm.js 는 web/vendor 에 들어 있다.
        View.CoreWebView2.SetVirtualHostNameToFolderMapping(
            Host, Path.Combine(AppContext.BaseDirectory, "web"),
            CoreWebView2HostResourceAccessKind.Deny);

        var s = View.CoreWebView2.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsSwipeNavigationEnabled = false;

        // **F5 · Ctrl+R 로 껍데기가 다시 뜨면 안 된다.** 여기는 웹 페이지가 아니라 앱 화면이고,
        // 다시 뜨면 ui.ready 가 또 와서 탭이 통째로 한 벌 더 생긴다(아래 _restored 참조).
        // 브라우저 탭 쪽은 그대로 켜 둔다 — 거기서는 F5 가 그 페이지만 새로 받는 것이 맞다.
        s.AreBrowserAcceleratorKeysEnabled = false;

        View.CoreWebView2.WebMessageReceived += (_, ev) =>
        {
            try
            {
                if (JsonNode.Parse(ev.WebMessageAsJson) is { } node) OnMessage(node);
            }
            catch { /* 못 읽을 메시지는 무시한다 — 화면 쪽 버그로 호스트가 죽으면 안 된다 */ }
        };

        // 비밀번호 저장. **껍데기 View 의 프로필이 곧 모든 브라우저 탭의 프로필이다**
        // (환경이 하나이므로) — 탭마다 걸 필요가 없고, 걸면 서로 덮어쓴다.
        ApplySignInSettings(Settings.Load());

        View.CoreWebView2.Navigate($"https://{Host}/index.html");
    }

    // ── 메시지 ──────────────────────────────────────────────────────────────

    private void OnMessage(JsonNode m)
    {
        var t = m["t"]?.GetValue<string>() ?? "";

        // 터미널 메시지는 **탭 번호로 갈라 보낸다.** 가장 자주 오고(키 입력마다 온다).
        // 모르는 번호면 아무 탭에나 흘리지 않는다 — 그러면 화면이 뒤섞인다.
        //
        // **버리되 말은 한다.** 조용히 버리면 "탭은 생겼는데 CLI 가 안 뜬다" 가 되고,
        // 그때 화면에는 아무 단서도 없다. 키 입력(term.in)까지 매번 적으면 시끄러우므로
        // 세션을 여는 메시지만 남긴다 — 그것이 안 닿은 것이 곧 안 뜬 이유다.
        if (t.StartsWith("term.", StringComparison.Ordinal))
        {
            var id = m["id"]?.GetValue<string>();
            if (id is not null && _tabs.TryGetValue(id, out var host)) { host.Handle(t, m); return; }
            if (t is "term.start") Log($"탭 '{id ?? "(번호 없음)"}' 을 찾지 못해 CLI 를 띄우지 못했습니다.", "e");
            return;
        }

        // 브라우저 판의 자리는 **탭 하나가 아니라 판 전체의 일이라** 따로 받는다.
        if (t is "browser.layout") { LayoutBrowsers(m); return; }

        if (t.StartsWith("browser.", StringComparison.Ordinal))
        {
            var id = m["id"]?.GetValue<string>();
            if (id is not null && _browsers.TryGetValue(id, out var b)) b.Handle(t, m);
            return;
        }

        switch (t)
        {
            case "ui.ready":
                PostConf();

                // **두 번 올 수 있다.** 껍데기가 다시 뜨면(새로고침·복구) 그대로 또 온다.
                // 그때 RestoreTabs 를 다시 돌리면 설정에 적힌 탭이 **한 벌 더** 생긴다 —
                // 브라우저 탭은 두 개씩 보이고, CLI 탭은 프로세스까지 두 벌이 떠
                // 같은 폴더를 서로 밟다가 접속 오류로 끝난다. 실제로 그렇게 됐다.
                if (_restored)
                {
                    ReattachTabs();
                }
                else
                {
                    _restored = true;
                    // **탭을 살리기 전에 쿠키를 되돌린다.** 페이지가 먼저 요청을 보내 버리면
                    // 그 요청은 쿠키 없이 나가 로그인 화면을 받는다.
                    ApplySignInSettings(Settings.Load(), restoreCookies: true);

                    // **claude 가 하나도 뜨기 전에** 본다. 탭이 뜬 뒤에 고치면 그 프로세스들이
                    // 곧바로 자기 상태로 덮어써서 되돌린 것이 그대로 날아간다.
                    if (Settings.Load().GuardClaudeLogin)
                        ClaudeAuthGuard.Guard(mayRestore: true, Log);

                    RestoreTabs();
                }

                StartUsage();
                StartBridge();
                StartStallWatch();
                break;

            case "signin.clear":
                ClearSignInData(m["all"]?.GetValue<bool>() ?? false);
                break;

            case "conf.get":
                PostConf();
                break;

            case "conf.models.refresh":
                RefreshAntigravityModels(m["path"]?.GetValue<string>() ?? Settings.Load().AntigravityCliPath);
                break;

            case "conf.save":
                SaveConf(m);
                break;

            case "conf.language":
                var languageSettings = Settings.Load();
                languageSettings.UiLanguage = m["language"]?.GetValue<string>() ?? "ko";
                languageSettings.Save();
                Post(new { t = "ui.language", language = languageSettings.UiLanguage });
                break;

            // [＋ 새 대화] — 먼저 CLI 고르기 창에 줄 목록을 보낸다. 고른 뒤에는 tab.new 로 돌아온다.
            case "newchat.list":
                PostNewChat();
                break;

            case "tab.new":
                NewTab(m["backend"]?.GetValue<string>() ?? "", m["account"]?.GetValue<string>() ?? "");
                break;

            // 탭 오른쪽 버튼 [다른 계정으로 이어가기] — 먼저 넘길 곳 목록을 준다. 고른 뒤에는 tab.switch 로 온다.
            case "switch.list":
                PostSwitch(m["id"]?.GetValue<string>() ?? "");
                break;

            case "history.switch.list":
                PostHistorySwitch(m);
                break;

            case "history.switch":
                SwitchHistory(m["id"]?.GetValue<string>() ?? "",
                              m["backend"]?.GetValue<string>() ?? "",
                              m["account"]?.GetValue<string>() ?? "");
                break;

            case "tab.switch":
                SwitchTab(m["id"]?.GetValue<string>() ?? "",
                          m["backend"]?.GetValue<string>() ?? "",
                          m["account"]?.GetValue<string>() ?? "");
                break;

            // ── 계정 (설정 화면) ──
            case "account.add":
                AddAccount(m["backend"]?.GetValue<string>() ?? "", m["name"]?.GetValue<string>() ?? "",
                           m["login"]?.GetValue<string>() ?? "");
                break;

            case "account.login":
                LoginAccount(m["id"]?.GetValue<string>() ?? "");
                break;

            // 대안: claude setup-token 을 열어 1년짜리 토큰을 받는다(화면에서 읽어 저장).
            case "account.setuptoken":
                SetupTokenAccount(m["id"]?.GetValue<string>() ?? "");
                break;

            case "account.token":
                SetClaudeToken(m["id"]?.GetValue<string>() ?? "", m["token"]?.GetValue<string>() ?? "");
                break;

            case "account.rename":
                RenameAccount(m["id"]?.GetValue<string>() ?? "", m["name"]?.GetValue<string>() ?? "",
                              m["backend"]?.GetValue<string>() ?? "");
                break;

            case "account.remove":
                RemoveAccount(m["id"]?.GetValue<string>() ?? "");
                break;

            // 머리줄 바로가기(Jira · GitHub · GitLab)와 링크의 새 창 요청이 모두 여기로 온다.
            case "tab.browser":
                try { OpenBrowserTab(m["url"]?.GetValue<string>() ?? ""); }
                catch (Exception ex) { Log(ex.Message, "w"); }
                break;

            case "tab.close":
                CloseTab(m["id"]?.GetValue<string>() ?? "");
                break;

            case "tab.rename":
                RenameTab(m["id"]?.GetValue<string>() ?? "",
                          m["name"]?.GetValue<string>() ?? "");
                break;

            case "folder.open":
                OpenFolder(m["path"]?.GetValue<string>() ?? "");
                break;

            // 클립보드는 **호스트가 만진다.** 브라우저 쪽 읽기는 권한을 따로 받아야 하고,
            // 거절당하면 화면에는 "붙여넣기를 눌렀는데 아무 일도 없다" 로만 보인다.
            case "clip.copy":
                SetClipboard(m["text"]?.GetValue<string>() ?? "");
                break;

            case "clip.read":
                ReadClipboard(m["to"]?.GetValue<string>() ?? "input",
                              m["id"]?.GetValue<string>() ?? "");
                break;

            // [↻ 이어하기] — 지난 대화 목록을 준다. 고른 뒤에는 tab.resume 으로 돌아온다.
            case "history.list":
                PostHistory();
                break;

            case "tab.resume":
                ResumeTab(m["backend"]?.GetValue<string>() ?? "",
                          m["cwd"]?.GetValue<string>() ?? "",
                          m["sessionId"]?.GetValue<string>() ?? "",
                          m["account"]?.GetValue<string>() ?? "");
                break;

            case "attach.pick":
                PickAttachments();
                break;

            case "capture.region":
                CaptureRegion();
                break;

            case "cli.pick":
                PickCliPath(m["which"]?.GetValue<string>() ?? "");
                break;
        }
    }

    // ── 탭 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// [＋ 새 대화] 의 CLI 고르기 창에 줄 것 — CLI 마다 **찾았는가**와 **무엇을 걸고 뜰 것인가**.
    ///
    /// 찾았는가를 같이 주는 이유: 못 찾은 CLI 를 고르게 두면 폴더까지 고른 뒤에야 빈 탭에
    /// "찾지 못했습니다" 가 뜬다. **누를 때마다 다시 본다** — 그 사이 CLI 를 깔았거나 지웠을 수 있다.
    ///
    /// 거는 값은 **저장된 설정**에서 읽는다. 설정 화면에 고치다 만 값이 있어도 그것은 아직
    /// 안 걸린다 — 화면이 들고 있는 값으로 보여 주면 적힌 것과 실제로 뜨는 것이 갈라진다.
    /// </summary>
    private void PostNewChat()
    {
        var c = Settings.Load();
        var lastCli = CliProfile.ForOrDefault(c.LastBackend);
        Post(new
        {
            t = "newchat",
            last = lastCli.Backend,
            lastAccount = Accounts.Resolve(c, lastCli, c.LastAccount),
            // **CLI × 계정** 마다 한 줄. 계정을 더 넣었으면 새 대화도 그 계정으로 바로 열 수 있어야 한다 —
            // 기본 계정으로 연 뒤에 넘기라고 하면, 한도가 찬 계정으로 한 번은 꼭 막혀야 한다.
            items = CliProfile.All.SelectMany(p =>
            {
                var loc = CliLocator.Locate(p, p.CachedPath(c));
                var opts = p.LaunchOptions(c).Select(o => new { label = o.Label, value = o.Value }).ToList();
                return Accounts.Of(c, p).Select(a => new
                {
                    backend = p.Backend,
                    display = p.Display,
                    account = a.Id,
                    accountName = a.Label,
                    email = a.Email,
                    // 기본 계정은 로그인이 없어도 연다 — 그때는 CLI 가 화면에서 먼저 로그인을 묻는다.
                    // 더 넣은 계정은 로그인이 있어야 한다(claude 는 토큰이 없으면 기본 계정으로 떨어진다).
                    usable = a.IsDefault || a.LoggedIn,
                    note = a.Note,
                    found = loc.Found,
                    path = loc.Path,
                    error = loc.Error,
                    opts,
                });
            }).ToList(),
        });
    }

    /// <summary>
    /// [＋ 새 대화] — 고르기 창에서 고른 CLI 로, 폴더를 고르고 **새 탭**에서 처음부터 시작한다.
    ///
    /// **CLI 는 누를 때마다 고른다.** 설정에 하나를 박아 두고 그것으로만 열면, 다른 CLI 로
    /// 한 번 열어 보려고 설정을 바꿨다가 되돌리는 일이 매번이 되고, 되돌리는 것을 잊으면
    /// 다음 대화가 엉뚱한 CLI 로 뜬다.
    ///
    /// 같은 폴더를 골라도 새 탭이다. 이미 그 폴더에 탭이 있으니 그리로 보내는 것이
    /// 얼핏 친절해 보이지만, 그러면 [＋ 새 대화] 를 눌렀는데 앞 대화가 그대로 남아 있는
    /// 셈이라 버튼이 거짓말을 한다. 하던 이야기로 돌아가려면 [↻ 이어하기] 쪽이다.
    /// </summary>
    private void NewTab(string backend, string account)
    {
        // 모르는 값이면 **열지 않는다.** 고르기 창은 아는 값만 보내므로 이것은 화면 쪽 결함이고,
        // 아무 CLI 로나 열면 고른 것과 다른 CLI 가 떠도 사용자는 탭의 작은 배지로만 알아챈다.
        var cli = CliProfile.For(backend);
        if (cli is null)
        {
            Log($"어느 CLI 로 열지 알 수 없어 새 대화를 열지 않았습니다 — '{backend}'", "e");
            return;
        }

        var cfg = Settings.Load();

        // 계정도 같다 — 모르는 계정(그 사이 지웠다)으로는 열지 않는다. 기본 계정으로 조용히 바꿔 열면
        // 고른 계정과 다른 계정이 한도를 쓴다.
        var acc = Accounts.Resolve(cfg, cli, account);
        if (acc != (account ?? ""))
        {
            Log($"고른 계정을 찾지 못해 새 대화를 열지 않았습니다 — 설정의 [계정] 을 확인하세요.", "e");
            return;
        }
        var start = cfg.LastFolder is { Length: > 0 } last && Directory.Exists(last)
            ? last
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 창 제목에 CLI 를 적는다 — 고르기 창을 닫은 뒤라, 무엇을 골랐는지 확인할 자리가 여기뿐이다.
        var folder = Dialogs.PickFolder(this, UiText.Pick($"{cli.Display} 로 대화할 폴더를 고르세요 — CLI 가 이 폴더에서 실행됩니다",
            $"Choose a working folder for {cli.Display} — the CLI will run here"), start);

        // 취소는 **아무 일도 안 일어나는 것이다.** 여기서 탭을 만들거나 닫으면
        // 폴더 창을 잘못 연 것만으로 화면이 바뀐다.
        if (folder is null) { Post(new { t = "tab.cancel" }); return; }

        // 고른 CLI 는 **폴더까지 고른 뒤에만** 남긴다. 창을 닫았는데 남기면 다음 고르기 창이
        // 끝까지 가지도 않은 선택을 먼저 짚고 있다.
        cfg.LastFolder = folder;
        cfg.LastBackend = cli.Backend;
        cfg.LastAccount = acc;
        cfg.Save();

        OpenTab(folder, cli.Backend, ResumeMode.New, account: acc);
    }

    /// <summary>
    /// [↻ 이어하기] 목록에서 고른 대화를 새 탭에서 이어간다. codex 는 **기록이 있는 계정으로** 연다 —
    /// 계정마다 홈이 달라 다른 계정으로 띄우면 codex 가 그 번호를 못 찾는다.
    /// </summary>
    private void ResumeTab(string backend, string cwd, string sessionId, string account)
    {
        if (cwd.Length == 0 || sessionId.Length == 0) return;
        if (!Directory.Exists(cwd))
        {
            Log($"폴더가 없어 이어갈 수 없습니다 — {cwd}", "e");
            return;
        }
        var cli = CliProfile.ForOrDefault(backend);
        var settings = Settings.Load();
        var resolved = Accounts.Resolve(settings, cli, account);
        if (resolved != account) { Log("이 대화의 계정을 찾지 못했습니다. 다른 계정으로 이어가기를 선택하세요.", "e"); return; }
        OpenTab(cwd, backend, ResumeMode.Session(sessionId), account: resolved);
    }

    /// <summary>
    /// 탭 하나를 연다. **실제 기동은 화면이 시킨다** — 열·행을 화면만 알기 때문이다
    /// (xterm 이 컨테이너 크기를 재서 정한다). 여기서 짐작한 값으로 띄우면 첫 화면이
    /// 어긋난 채 그려지고, TUI 는 그 상태로 계속 그린다.
    /// </summary>
    private void OpenTab(string cwd, string backend, ResumeMode resume, string alias = "", string account = "", string? handoffPath = null)
    {
        var cli = CliProfile.ForOrDefault(backend);
        var id = "t" + (++_nextTab);
        if (alias.Length > 0) _alias[id] = alias;
        var name = TabName(cwd);

        var host = new TerminalHost(id, cwd, cli, account, Post, (msg, lvl) => Log($"[{name}] {msg}", lvl))
            { HandoffPath = handoffPath ?? resume.HandoffPath, ResumeTranscriptPath = resume.TranscriptPath };
        _tabs[id] = host;
        _order.Add(id);

        Post(new
        {
            t = "tab.opened",
            id,
            kind = "cli",
            cwd,
            name,
            backend = cli.Backend,
            cli = cli.Display,
            memoryFile = cli.MemoryFile,
            fresh = resume.Fresh,
            sessionId = resume.SessionId ?? "",
            alias,
            account,
            accountName = AccountLabel(cli, account),
        });
        SaveTabs();
    }

    /// <summary>탭 배지에 붙일 계정 이름. 이름을 바꾸지 않은 기본 계정은 생략한다.</summary>
    private static string AccountLabel(CliProfile cli, string account)
    {
        var label = Accounts.View(Settings.Load(), cli, account).Label;
        return account.Length == 0 && label == "기본" ? "" : label;
    }

    /// <summary>
    /// 브라우저 탭을 연다. **CLI 탭과 같은 탭띠에 나란히 선다** — 따로 창을 띄우면
    /// Jira 를 보다 CLI 로 돌아오는 것이 창 전환이 되고, 그것이 싫어서 이 앱을 쓰는 것이다.
    ///
    /// 자리는 여기서 안 정한다. 화면이 판 사각형을 재서 알려 준다(<see cref="LayoutBrowsers"/>).
    /// </summary>
    /// <param name="inPrivate">
    /// InPrivate 탭으로 연다 — 계정 로그인 탭이 연 로그인 페이지가 이것이다(<see cref="BrowserHost.InPrivate"/>).
    /// </param>
    private string OpenBrowserTab(string url, string alias = "", bool inPrivate = false)
    {
        if (_env is null) throw new InvalidOperationException("브라우저를 아직 쓸 수 없습니다 — WebView2 준비 전입니다.");

        url = BrowserHost.Normalize(url);
        if (url.Length == 0) throw new ArgumentException("주소가 비었습니다.");

        var id = "t" + (++_nextTab);
        if (alias.Length > 0) _alias[id] = alias;

        var host = new BrowserHost(id, url, _env, Post, Log, OpenInNewTabSafe, OpenPopupAsync, inPrivate);
        _browsers[id] = host;
        _order.Add(id);
        Browsers.Children.Add(host.View);

        PostBrowserOpened(host, alias);
        SaveTabs();

        // 기다리지 않는다. 번호는 이미 정해졌고, 부르는 쪽(머리줄 버튼·에이전트)은
        // 페이지가 다 뜰 때까지 잡혀 있을 이유가 없다 — 뜨는 과정은 탭이 스스로 알린다.
        _ = host.InitAsync();
        return id;
    }

    /// <summary>
    /// 페이지가 연 새 창(<c>window.open</c> · <c>target=_blank</c>)을 받을 탭. **opener 와 이어진 채** 연다 —
    /// 아직 아무 데도 안 간 CoreWebView2 를 돌려주면 <see cref="BrowserHost"/> 가 그것을 <c>NewWindow</c> 로 넘긴다.
    /// 로그인 팝업은 그 연결로 결과를 원래 창에 돌려준다(구글 로그인이 이것 없이 흰 화면에서 멈췄다).
    ///
    /// opener 와 **같은 프로필**로 세운다(InPrivate 까지) — 다르면 WebView2 가 넘기기를 거절한다.
    /// </summary>
    private async Task<Microsoft.Web.WebView2.Core.CoreWebView2?> OpenPopupAsync(BrowserHost opener, string url, bool transient)
    {
        if (_env is null) return null;

        var id = "t" + (++_nextTab);
        var host = new BrowserHost(id, url, _env, Post, Log, OpenInNewTabSafe, OpenPopupAsync, opener.InPrivate)
        {
            OpenerId = opener.Id,
            Transient = transient,
        };
        _browsers[id] = host;
        _order.Add(id);
        Browsers.Children.Add(host.View);

        PostBrowserOpened(host, "");
        SaveTabs();

        var core = await host.InitAsync(navigate: false);
        if (core is null)
        {
            // 못 세웠다 — 빈 탭을 남기지 않는다. 부르는 쪽이 주소만 새 탭에서 연다.
            if (_browsers.ContainsKey(id)) Post(new { t = "tab.closed", id });
        }
        return core;
    }

    private void PostBrowserOpened(BrowserHost host, string alias) =>
        Post(new
        {
            t = "tab.opened",
            id = host.Id,
            kind = "browser",
            cwd = "",
            name = host.Title,
            backend = "browser",
            cli = "브라우저",
            url = host.Url,
            alias,
            inPrivate = host.InPrivate,
            opener = host.OpenerId,
        });

    /// <summary>링크가 새 창을 요청했을 때. 실패는 기록만 하고 넘어간다 — 링크 하나가 앱을 죽이면 안 된다.</summary>
    private void OpenInNewTabSafe(string url)
    {
        try { OpenBrowserTab(url); }
        catch (Exception ex) { Log($"새 탭을 열지 못했습니다 — {ex.Message}", "w"); }
    }

    private void CloseTab(string id)
    {
        if (_tabs.Remove(id, out var host)) { try { host.Dispose(); } catch { } }
        else if (_browsers.Remove(id, out var b))
        {
            Browsers.Children.Remove(b.View);
            try { b.Dispose(); } catch { }
        }
        else return;

        _order.Remove(id);
        _alias.Remove(id);
        SaveTabs();
    }

    private string Alias(string id) => _alias.TryGetValue(id, out var a) ? a : "";

    /// <summary>
    /// 탭에 손으로 이름을 붙인다. **빈 이름은 지우는 것이다** — 자동 이름(폴더 이름 · 문서 제목)으로
    /// 돌아간다. 되돌릴 길이 없으면 한 번 붙인 이름을 영영 못 뗀다.
    /// </summary>
    private void RenameTab(string id, string name)
    {
        if (!_tabs.ContainsKey(id) && !_browsers.ContainsKey(id)) return;

        name = name.Trim();
        if (name.Length > 40) name = name[..40];   // 탭 하나가 탭띠를 다 먹지 않게

        if (name.Length == 0) _alias.Remove(id);
        else _alias[id] = name;

        SaveTabs();
        Post(new { t = "tab.renamed", id, alias = name });
    }

    /// <summary>탐색기로 그 폴더를 연다. 경로를 눈으로 확인하거나 파일을 꺼낼 때 쓴다.</summary>
    private void OpenFolder(string path)
    {
        if (path.Length == 0 || !Directory.Exists(path)) { Log($"폴더가 없습니다 — {path}", "w"); return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                // 탐색기에 맡긴다. 이것을 끄면 폴더는 실행 파일이 아니라며 열리지 않는다.
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Log($"폴더를 열지 못했습니다 — {ex.Message}", "w"); }
    }

    // -- 클립보드 -----------------------------------------------------------
    //
    // **호스트가 만진다.** 브라우저 쪽 navigator.clipboard 읽기는 권한을 따로 받아야 하고,
    // 거절당하면 예외도 없이 빈 값이 온다 - 화면에는 "붙여넣기가 안 먹는다" 로만 보인다.
    // WPF 의 클립보드에는 그 문이 없다.
    //
    // 클립보드는 다른 프로그램이 잠깐 잡고 있을 수 있어 **실패가 정상 범위다.** 몇 번 다시 해 본다.

    private static T? ClipboardRetry<T>(Func<T?> read)
    {
        for (var i = 0; ; i++)
        {
            try { return read(); }
            catch when (i < 4) { System.Threading.Thread.Sleep(40); }
        }
    }

    private void SetClipboard(string text)
    {
        if (text.Length == 0) return;
        try { ClipboardRetry<object?>(() => { Clipboard.SetText(text); return null; }); }
        catch (Exception ex) { Log($"복사하지 못했습니다 — {ex.Message}", "w"); }
    }

    /// <summary>
    /// 클립보드를 읽어 화면으로 올린다. <paramref name="to"/> 와 <paramref name="id"/> 는
    /// **그대로 돌려주는 꼬리표**다 - 어디에 넣을 것이었는지(입력창인지 터미널인지)를
    /// 화면이 따로 기억하지 않아도 된다.
    ///
    /// 글자가 없으면 그림을 본다 - 캡처를 오른쪽 버튼으로 붙이는 길이 그것뿐이다.
    /// </summary>
    private void ReadClipboard(string to, string id)
    {
        try
        {
            var text = ClipboardRetry(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
            if (!string.IsNullOrEmpty(text))
            {
                Post(new { t = "clip.text", to, id, text });
                return;
            }

            var img = ClipboardRetry(() => Clipboard.ContainsImage() ? Clipboard.GetImage() : null);
            if (img is not null)
            {
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                using var ms = new MemoryStream();
                enc.Save(ms);
                var bytes = ms.ToArray();
                Post(new
                {
                    t = "clip.image",
                    to,
                    id,
                    dataUrl = "data:image/png;base64," + Convert.ToBase64String(bytes),
                    size = bytes.Length,
                });
                return;
            }

            Log("클립보드가 비어 있습니다.", "i");
        }
        catch (Exception ex) { Log($"클립보드를 읽지 못했습니다 — {ex.Message}", "w"); }
    }

    /// <summary>
    /// 브라우저 판을 화면이 말해 준 사각형에 맞춘다. <paramref name="m"/> 의 <c>id</c> 가
    /// 비어 있으면 **전부 숨긴다** — CLI 탭으로 갔거나, 설정 화면이거나, 화면 안의 덮개
    /// (지난 대화 목록)가 떠 있는 경우다.
    ///
    /// 덮개를 특별히 다루는 이유: WebView2 는 HwndHost 라 HTML 이 그린 것 **위에** 그려진다.
    /// 안 숨기면 [↻ 이어하기] 를 눌러도 목록이 브라우저 뒤에 가려, 화면에는 아무 일도
    /// 안 일어난 것으로 보인다.
    /// </summary>
    private void LayoutBrowsers(JsonNode m)
    {
        var id = m["id"]?.GetValue<string>() ?? "";
        double N(string k) => m[k]?.GetValue<double>() ?? 0;
        double x = N("x"), y = N("y"), w = N("w"), h = N("h");

        foreach (var (bid, b) in _browsers)
        {
            var on = bid == id && w > 0 && h > 0;
            b.View.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) continue;

            Canvas.SetLeft(b.View, x);
            Canvas.SetTop(b.View, y);
            b.View.Width = w;
            b.View.Height = h;

            // **자리만 잡아서는 안 보인다.** 자식 HWND 의 앞뒤는 WPF 가 안 정한다 —
            // 자세한 것은 BrowserHost.BringToFront 참조.
            b.View.UpdateLayout();
            b.BringToFront();
        }
    }

    /// <summary>
    /// 앱을 켤 때 지난번 탭을 되살린다. **이어서 연다** — 강제 종료·전원 차단 뒤에도
    /// 하던 이야기가 남아야 한다. 무엇을 시켰는지부터 다시 설명하게 되면
    /// 그 설명이 매번 조금씩 달라진다.
    ///
    /// 사라진 폴더는 **조용히 건너뛰지 않는다.** 폴더를 옮겼는데 탭만 사라지면
    /// 사용자는 앱이 기억을 잃은 줄 안다.
    /// </summary>
    private void RestoreTabs()
    {
        var cfg = Settings.Load();
        foreach (var tab in cfg.Tabs.ToList())
        {
            if (string.Equals(tab.Kind, "browser", StringComparison.OrdinalIgnoreCase))
            {
                // 주소가 없으면 되살릴 것이 없다. 빈 브라우저 탭을 띄우면 왜 열렸는지 알 수 없다.
                if (tab.Url is { Length: > 0 })
                    try { OpenBrowserTab(tab.Url, tab.Name); }
                    catch (Exception ex) { Log($"브라우저 탭을 되살리지 못했습니다 — {ex.Message}", "w"); }
                continue;
            }
            if (!Directory.Exists(tab.Cwd)) { Log($"폴더가 없어 탭을 되살리지 못했습니다 — {tab.Cwd}", "w"); continue; }

            // **그 탭의 계정으로** 되살린다. 계정을 바꿔 이어가던 탭이 기본 계정으로 돌아오면 한도가 찬 계정으로
            // 다시 막힌다. 그 사이 지운 계정이면 기본 계정으로 — 그때는 말해 둔다.
            var cli = CliProfile.ForOrDefault(tab.Backend);
            var acc = Accounts.Resolve(cfg, cli, tab.Account);
            if (acc != (tab.Account ?? ""))
                Log($"탭이 쓰던 계정을 찾지 못해 기본 계정으로 되살렸습니다 — {tab.Cwd}", "w");
            OpenTab(tab.Cwd, tab.Backend, ResumeMode.Last, tab.Name, acc, tab.HandoffPath);
        }
    }

    /// <summary>
    /// 껍데기가 다시 떴을 때, **이미 살아 있는 탭들을 화면에 다시 붙인다.**
    /// 설정에서 새로 만들지 않는다 — 그것이 탭을 두 벌로 만드는 길이다.
    ///
    /// CLI 탭은 프로세스가 그대로 살아 있다. 화면이 <c>term.start</c> 를 다시 보내도
    /// <see cref="TerminalHost.Start"/> 가 이미 돌고 있는 것을 보고 <c>term.ready</c> 만
    /// 돌려주므로 두 번 뜨지 않는다 — **다만 터미널에 찍혀 있던 글은 사라진다**
    /// (xterm 이 새로 만들어진다). 대화 자체는 CLI 쪽에 남아 있으므로 이어서 쓸 수 있다.
    /// </summary>
    private void ReattachTabs()
    {
        Log("화면이 다시 떠서 열려 있던 탭을 다시 붙였습니다 — 터미널에 찍혀 있던 글은 사라집니다.", "w");

        foreach (var id in _order.ToList())
        {
            if (_tabs.TryGetValue(id, out var h))
            {
                Post(new
                {
                    t = "tab.opened",
                    id,
                    kind = "cli",
                    cwd = h.Cwd,
                    name = TabName(h.Cwd),
                    backend = h.Cli.Backend,
                    cli = h.Cli.Display,
                    memoryFile = h.Cli.MemoryFile,
                    fresh = false,
                    sessionId = "",
                    alias = Alias(id),
                    account = h.Account,
                    accountName = AccountLabel(h.Cli, h.Account),
                    login = h.IsLogin,
                });
            }
            else if (_browsers.TryGetValue(id, out var b))
            {
                PostBrowserOpened(b, Alias(id));
            }
        }
    }

    /// <summary>폴더 경로에서 탭에 찍을 이름. 드라이브 루트(D:\)는 이름이 비어 경로를 그대로 쓴다.</summary>
    private static string TabName(string cwd)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(cwd));
        return string.IsNullOrEmpty(name) ? cwd : name;
    }

    /// <summary>
    /// 지금 열려 있는 탭을 파일에 남긴다. 탭이 생기거나 사라질 때마다 부른다.
    /// <see cref="_order"/> 를 따라 걷는다 — 두 사전을 이어 붙이면 되살릴 때 종류별로 몰린다.
    /// </summary>
    private void SaveTabs()
    {
        var cfg = Settings.Load();
        cfg.Tabs = _order
            // 로그인 탭은 남기지 않는다 — 되살려도 할 일이 없고, 켤 때마다 로그인 창이 뜨게 된다.
            // InPrivate 탭 · 스스로 여닫는 팝업도 남기지 않는다 — 되살려도 그 로그인은 이미 사라졌다.
            .Select(id => _tabs.TryGetValue(id, out var h)
                ? (h.IsLogin ? null : new TabState { Kind = "cli", Cwd = h.Cwd, Backend = h.Cli.Backend, Name = Alias(id), Account = h.Account, HandoffPath = h.HandoffPath })
                : _browsers.TryGetValue(id, out var b)
                    ? (b.InPrivate || b.Transient ? null : new TabState { Kind = "browser", Url = b.Url, Name = Alias(id) })
                    : null)
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();
        cfg.Save();
    }

    // ── 다른 계정으로 이어가기 ──────────────────────────────────────────────
    //
    // 한 계정의 한도가 차면 **그 탭의 대화를 다른 계정(다른 CLI 도 된다)으로 넘겨 이어간다.**
    // 탭은 그대로 두고 안의 CLI 만 갈아 끼운다 — 이름·자리·폴더가 그대로라 무엇을 하던 탭인지 안 헷갈린다.
    //
    //   claude → claude(다른 계정)  실제 대화 파일의 절대 경로로 --resume. 계정별 projects 분리에도 대응한다.
    //   그 밖(→ codex, codex → …)   기록을 받을 CLI 의 형식으로 옮겨 쓴 **새 대화**를 이어하기로 연다
    //                                (Transcript.cs). 원본은 그대로 남는다.
    //
    // codex → codex(다른 계정)도 옮겨 쓴다. 원본 기록의 추론·압축본은 발급 계정에 묶인 암호문이라
    // 다른 계정으로 그대로 보내면 첫 턴에서 거절당할 수 있다.

    /// <summary>
    /// 같은 CLI·같은 홈의 **다른 탭**이 쥐고 있는 대화 번호. 이 탭의 대화를 찾을 때 빼야 한다 —
    /// 같은 폴더에 탭이 둘이면 "가장 최근 기록" 이 옆 탭 것일 수 있다.
    /// </summary>
    private HashSet<string> Claimed(TerminalHost h) =>
        _tabs.Values
            .Where(o => !ReferenceEquals(o, h) && !o.IsLogin && o.Cli == h.Cli &&
                        (h.Cli.Kind == CliKind.Claude || string.Equals(
                            h.Cli.Kind == CliKind.Antigravity ? o.AntigravityHome : o.CodexHome,
                            h.Cli.Kind == CliKind.Antigravity ? h.AntigravityHome : h.CodexHome, StringComparison.OrdinalIgnoreCase)))
            .Select(o => o.KnownSessionId)
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(x => x!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 넘길 곳 목록과 **넘길 대화**를 보낸다. 대화를 찾고 읽는 데 시간이 걸릴 수 있어(큰 기록은 수 MB) 배경에서 한다.
    /// 넘길 대화를 먼저 보여 주는 이유: 같은 폴더에 탭이 여럿이면 엉뚱한 대화를 넘길 수 있다 —
    /// 첫 발화가 보이면 사용자가 고르기 전에 알아챈다.
    /// </summary>
    private void PostSwitch(string id)
    {
        if (!_tabs.TryGetValue(id, out var h) || h.IsLogin) return;
        if (!h.Cli.SupportsTransfer) return;
        var claimed = Claimed(h);
        PostSwitchChoices(id, h.Cli, h.Account, () => TransferSource(h, claimed));
    }

    private void PostSwitchChoices(string id, CliProfile source, string account,
        Func<(SessionRef? Session, Transcript? Transcript)> read, bool history = false)
    {
        var c = Settings.Load();
        var fromView = Accounts.View(c, source, account);
        var items = CliProfile.All.Where(p => p.SupportsTransfer).SelectMany(p =>
        {
            var loc = CliLocator.Locate(p, p.CachedPath(c));
            return Accounts.Of(c, p)
                .Where(a => !(p == source && a.Id == account))
                .Select(a => new
                {
                    backend = p.Backend, display = p.Display, account = a.Id,
                    accountName = a.Label, email = a.Email,
                    usable = loc.Found && (a.IsDefault || a.LoggedIn),
                    why = !loc.Found ? (loc.Error ?? p.NotFoundMessage)
                        : !(a.IsDefault || a.LoggedIn) ? "로그인 필요 — [⚙ 설정] → [계정] 에서 로그인하세요" : "",
                    how = p.Kind == CliKind.Antigravity ? "context" : p == source && p.Kind == CliKind.Claude ? "same" : "copy",
                });
        }).ToList();

        Post(new
        {
            t = "switch",
            id,
            history,
            from = new { backend = source.Backend, display = source.Display, account, accountName = fromView.Label },
            items,
            session = (object?)null,
            loading = true,
        });

        System.Threading.Tasks.Task.Run(() =>
        {
            object? session = null;
            try
            {
                var (src, tr) = read();
                if (tr is not null)
                {
                    session = new
                    {
                        id = src?.Id ?? tr.SessionId,
                        title = tr.Title,
                        turns = tr.UserTurns,
                        items = tr.Items.Count,
                        empty = !tr.HasConversation,
                        when = (src?.WhenUtc ?? DateTime.UtcNow).ToLocalTime().ToString("MM-dd HH:mm"),
                    };
                }
            }
            catch (Exception ex)
            {
                Log($"넘길 대화를 읽지 못했습니다 — {ex.Message}", "w");
                session = new { error = ex.Message };
            }
            Post(new { t = "switch.session", id, session });
        });
    }

    private static (SessionRef? Session, Transcript? Transcript) TransferSource(TerminalHost host, ISet<string> claimed)
    {
        SessionRef? source;
        if (host.Cli.Kind == CliKind.Antigravity && host.HandoffPath is { Length: > 0 } handoff)
        {
            source = AntigravityTransfer.FindSession(host.Cwd, handoff, host.AntigravityHome);
            if (source is null) return (null, AntigravityTransfer.ReadHandoff(handoff));
        }
        else source = SessionFinder.Current(host.Cli, host.Cwd, host.CodexHome, host.StartedUtc, host.KnownSessionId, claimed, host.ClaudeHistoryHome, host.AntigravityHome);
        // 계정별 폴더가 분리되어 있어도, 앞선 전환 때 전달한 원본 파일을 다시 찾는다.
        if (source is null && host.Cli.Kind == CliKind.Claude && host.ResumeTranscriptPath is { } path && File.Exists(path))
            source = new SessionRef(host.Cli, Path.GetFileNameWithoutExtension(path), path, File.GetLastWriteTimeUtc(path));
        return (source, source is null ? null : TranscriptReader.Read(host.Cli, source.Path));
    }

    /// <summary>
    /// 탭의 대화를 다른 CLI·계정으로 넘긴다. **실패하면 탭은 그대로다** — 옮겨 쓰기가 끝난 뒤에야 옛 CLI 를
    /// 끄므로, 도중에 무엇이 터져도 하던 대화는 살아 있다.
    /// </summary>
    private void SwitchTab(string id, string backend, string account)
    {
        if (!_tabs.TryGetValue(id, out var h) || h.IsLogin) return;
        var target = CliProfile.For(backend);
        if (target is null) { Log($"어느 CLI 로 넘길지 알 수 없습니다 — '{backend}'", "e"); return; }

        var c = Settings.Load();
        var acc = Accounts.Resolve(c, target, account);
        if (acc != (account ?? "")) { Log("넘길 계정을 찾지 못했습니다 — 설정의 [계정] 을 확인하세요.", "e"); return; }
        if (target == h.Cli && acc == h.Account) { Log("지금 쓰는 계정입니다 — 넘기지 않았습니다.", "i"); return; }

        var to = Accounts.View(c, target, acc);
        if (!to.IsDefault && !to.LoggedIn)
        {
            Log($"'{to.Label}' 계정은 로그인이 필요합니다 — [⚙ 설정] → [계정] 에서 [로그인] 하세요.", "w");
            Post(new { t = "switch.failed", id });
            return;
        }
        if (!CliLocator.Locate(target, target.CachedPath(c)).Found)
        {
            Log(target.NotFoundMessage, "e");
            Post(new { t = "switch.failed", id });
            return;
        }

        var from = $"{h.Cli.Display}({Accounts.View(c, h.Cli, h.Account).Label} 계정)";
        var dest = $"{target.Display}({to.Label} 계정)";
        var claimed = Claimed(h);
        Post(new { t = "switch.busy", id, to = dest });

        System.Threading.Tasks.Task.Run(() =>
        {
            ResumeMode mode;
            string note;
            try
            {
                var (src, tr) = TransferSource(h, claimed);
                var home = target.Kind == CliKind.Claude ? ClaudeTransferHome(acc) : Accounts.CodexHomeOf(acc);
                (mode, note) = ConversationTransfer.Prepare(h.Cli, src, tr, h.Cwd, target, home, from);
            }
            catch (Exception ex)
            {
                Log($"대화를 넘기지 못했습니다 — {ex.Message}. 이 탭은 그대로 둡니다.", "e");
                Post(new { t = "switch.failed", id });
                return;
            }

            // 옮길 것이 준비된 **뒤에** 옛 CLI 를 끈다. 같은 파일을 잇는 claude 는 옛 프로세스가 완전히
            // 끝나야 한다 — 둘이 한 파일에 덧붙이면 기록이 엉킨다(Stop 은 끝날 때까지 최대 2초 기다린다).
            try { h.Stop($"{dest} 로 넘겼습니다.", retire: true); } catch { }

            Dispatcher.BeginInvoke(() => Swap(id, h, target, acc, mode, $"{from} → {dest}: {note}"));
        });
    }

    /// <summary>같은 탭 번호에 새 호스트를 세우고, 화면에 그 탭을 새 CLI 로 다시 띄우라고 알린다.</summary>
    private void Swap(string id, TerminalHost old, CliProfile target, string account, ResumeMode mode, string note)
    {
        // 그 사이 탭을 닫았으면 할 일이 없다. 옮겨 쓴 기록은 [↻ 이어하기] 에 남아 있다.
        if (!_tabs.TryGetValue(id, out var cur) || !ReferenceEquals(cur, old)) return;

        var name = TabName(old.Cwd);
        var host = new TerminalHost(id, old.Cwd, target, account, Post, (msg, lvl) => Log($"[{name}] {msg}", lvl))
            { HandoffPath = mode.HandoffPath, ResumeTranscriptPath = mode.TranscriptPath };
        _tabs[id] = host;
        SaveTabs();

        Log(note, "i");
        Post(new
        {
            t = "tab.switched",
            id,
            backend = target.Backend,
            cli = target.Display,
            memoryFile = target.MemoryFile,
            account,
            accountName = AccountLabel(target, account),
            fresh = mode.Fresh,
            sessionId = mode.SessionId ?? "",
        });
    }

    // ── 계정 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 계정을 더하고 **곧바로 로그인 탭을 연다.** 로그인 없는 계정은 고를 수 없으므로, 더하는 것과 로그인은
    /// 한 동작이어야 한다. 로그인을 중간에 닫아도 계정은 목록에 남는다 — [로그인] 으로 다시 하면 된다.
    /// </summary>
    /// <param name="login">claude 로그인 방식 — 빈 값(claude.ai 구독) · <c>sso</c> · <c>console</c>.</param>
    private void AddAccount(string backend, string name, string login)
    {
        var cli = CliProfile.For(backend);
        if (cli is null || !cli.SupportsAccounts) return;
        var c = Settings.Load();
        if (!CliLocator.Locate(cli, cli.CachedPath(c)).Found)
        {
            Log(cli.NotFoundMessage + " — 계정을 더하려면 CLI 가 있어야 합니다(로그인을 그 CLI 로 합니다).", "e");
            return;
        }
        var acc = Accounts.Add(c, cli, name);
        if (cli.Kind == CliKind.Claude) acc.Login = login is "sso" or "console" ? login : "";
        c.Save();
        Log($"{cli.Display} 계정 '{acc.Name}' 을 더했습니다 — 열린 탭에서 로그인을 마치세요.", "i");
        PostConf();
        OpenLoginTab(cli, acc, LoginKindOf(cli, acc));
    }

    /// <summary>그 계정의 일반 로그인 — 처음 더할 때 고른 방식 그대로.</summary>
    private static LoginKind LoginKindOf(CliProfile cli, Account acc) =>
        cli.Kind == CliKind.Codex ? LoginKind.Codex
        : cli.Kind == CliKind.Antigravity ? LoginKind.Antigravity
        : acc.Login switch { "sso" => LoginKind.ClaudeSso, "console" => LoginKind.ClaudeConsole, _ => LoginKind.Claude };

    private void LoginAccount(string id)
    {
        var c = Settings.Load();
        var acc = Accounts.Find(c, id);
        var cli = CliProfile.For(acc?.Backend);
        if (acc is null || cli is null || !cli.SupportsAccounts) { Log("그런 계정이 없습니다.", "w"); return; }
        OpenLoginTab(cli, acc, LoginKindOf(cli, acc));
    }

    /// <summary>
    /// 대안 로그인 — <c>claude setup-token</c>. 계정 폴더에 일반 로그인을 둘 수 없을 때(연결을 못 만드는 드라이브 등)
    /// 쓴다. 일반 로그인이 있으면 그것이 먼저라 토큰은 안 쓰인다(<see cref="Accounts.ClaudeModeOf"/>).
    /// </summary>
    private void SetupTokenAccount(string id)
    {
        var acc = Accounts.Find(Settings.Load(), id);
        if (acc is null || CliProfile.For(acc.Backend) != CliProfile.Claude) { Log("그런 Claude 계정이 없습니다.", "w"); return; }
        OpenLoginTab(CliProfile.Claude, acc, LoginKind.ClaudeSetupToken);
    }

    /// <summary>
    /// 로그인 탭. 대화 탭과 같은 터미널이다 — CLI 가 보여 주는 안내(주소·코드)를 사용자가 그대로 보고 따른다.
    ///
    /// 브라우저 로그인 페이지는 이 앱의 브라우저 탭으로 뜬다(BROWSER 를 우리 도우미로 돌려 둔 것 — TerminalHost 참조).
    /// **그 브라우저가 이미 다른 계정으로 로그인돼 있으면 그 계정으로 승인된다** — 그래서 화면에 한 번 적어 준다.
    /// </summary>
    private void OpenLoginTab(CliProfile cli, Account acc, LoginKind kind)
    {
        var id = "t" + (++_nextTab);
        var cwd = cli.Kind == CliKind.Antigravity ? AntigravityAccounts.ProfileOf(acc.Id)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var alias = kind == LoginKind.ClaudeSetupToken ? $"{acc.Name} 토큰" : $"{acc.Name} 로그인";
        _alias[id] = alias;

        var host = new TerminalHost(id, cwd, cli, acc.Id, kind, Post, (msg, lvl) => Log($"[{alias}] {msg}", lvl),
            onToken: token => Dispatcher.BeginInvoke(() => OnClaudeToken(acc.Id, token, id)),
            onLoginExit: code => Dispatcher.BeginInvoke(() => OnLoginExit(cli, acc.Id, kind, code)));
        _tabs[id] = host;
        _order.Add(id);

        Post(new
        {
            t = "tab.opened",
            id,
            kind = "cli",
            cwd,
            name = TabName(cwd),
            backend = cli.Backend,
            cli = cli.Display,
            memoryFile = cli.MemoryFile,
            fresh = true,
            sessionId = "",
            alias,
            account = acc.Id,
            accountName = acc.Name,
            login = true,
        });

        Log(kind switch
        {
            LoginKind.ClaudeSetupToken =>
                $"'{acc.Name}' 토큰 받기: 브라우저가 열리면 **그 계정으로** 로그인해 승인하세요. 이미 다른 계정으로 로그인돼 있으면 " +
                "그 계정의 토큰이 만들어집니다 — 먼저 로그아웃하거나 계정을 바꾸세요. 토큰이 화면에 찍히면 자동으로 저장합니다.",
            LoginKind.Antigravity =>
                $"'{acc.Name}' 로그인: 터미널에 표시된 Google 로그인 안내를 따라 원하는 계정으로 승인하세요. " +
                "코드를 요구하면 이 터미널에 붙여 넣으세요. 로그인 완료 후 /exit로 종료하면 계정 목록이 갱신됩니다.",
            LoginKind.Codex =>
                $"'{acc.Name}' 로그인: 브라우저가 열리면 **그 계정으로** 로그인하세요. 이미 다른 계정으로 로그인돼 있으면 " +
                "로그아웃하거나 다른 계정으로 로그인하세요. 끝나면 이 계정 폴더에 로그인이 저장됩니다.",
            _ =>
                $"'{acc.Name}' 로그인: 브라우저가 열리면 **그 계정으로** 로그인하세요(회사 계정은 SSO · 회사 이메일). 이미 다른 계정으로 " +
                "로그인돼 있으면 로그아웃하거나 계정을 바꾸세요. 승인 뒤 코드가 보이면 복사해서 이 탭의 'Paste code here' 에 붙여 넣으세요. " +
                "기본 계정의 로그인은 그대로 남습니다(이 계정 폴더에만 로그인합니다).",
        }, "i");
    }

    private void OnClaudeToken(string accountId, string token, string tabId)
    {
        try
        {
            Accounts.SaveClaudeToken(accountId, token);
            var name = Accounts.Find(Settings.Load(), accountId)?.Name ?? accountId;
            Log($"'{name}' 계정의 토큰을 저장했습니다(암호화). 이제 그 계정으로 넘기거나 새 대화를 열 수 있습니다.", "i");
            Post(new { t = "term.out", id = tabId, data = "\r\n[32m── AgentCLIHub: 토큰을 저장했습니다. 이 탭은 닫아도 됩니다. ──[0m\r\n" });
            PostConf();
            RefreshUsage();
        }
        catch (Exception ex) { Log($"토큰을 저장하지 못했습니다 — {ex.Message}", "e"); }
    }

    private void OnLoginExit(CliProfile cli, string accountId, LoginKind kind, int code)
    {
        var c = Settings.Load();
        var acc = Accounts.Find(c, accountId);
        if (acc is null) return;

        if (cli.Kind == CliKind.Codex)
        {
            var home = Accounts.CodexHomeOf(accountId);
            if (Accounts.CodexLoggedIn(home))
            {
                acc.Email = Accounts.CodexEmail(home);
                c.Save();
                Log($"'{acc.Name}' Codex 계정에 로그인했습니다{(acc.Email.Length > 0 ? " — " + acc.Email : "")}.", "i");
            }
            else Log($"'{acc.Name}' Codex 로그인이 끝나지 않았습니다(코드 {code}) — [로그인] 으로 다시 하세요.", "w");
        }
        else if (cli.Kind == CliKind.Antigravity)
        {
            var state = AntigravityAccounts.State(AntigravityAccounts.ProfileOf(accountId));
            if (state.LoggedIn)
            {
                acc.Email = state.Email;
                c.Save();
                Log($"'{acc.Name}' Antigravity 계정에 로그인했습니다.", "i");
            }
            else Log($"'{acc.Name}' Antigravity 로그인이 완료되지 않았습니다 — [로그인]으로 다시 진행하세요.", "w");
        }
        else if (kind == LoginKind.ClaudeSetupToken)
        {
            if (Accounts.ClaudeToken(accountId) is null)
                Log($"'{acc.Name}' 의 토큰을 화면에서 찾지 못했습니다. 토큰(sk-ant-…)이 찍혔다면 복사해서 " +
                    "[⚙ 설정] → [계정] 의 [토큰 넣기] 로 붙여 넣으세요.", "w");
        }
        else
        {
            var home = Accounts.ClaudeHomeOf(accountId);
            if (Accounts.ClaudeLoggedIn(home))
            {
                acc.Email = Accounts.ClaudeEmail(home);
                c.Save();
                Log($"'{acc.Name}' Claude 계정에 로그인했습니다{(acc.Email.Length > 0 ? " — " + acc.Email : "")}. " +
                    "이제 탭 오른쪽 버튼 → [다른 계정으로 이어가기] 에서 고를 수 있습니다.", "i");
            }
            else Log($"'{acc.Name}' Claude 로그인이 끝나지 않았습니다(코드 {code}) — [로그인] 으로 다시 하세요.", "w");
        }
        PostConf();
        RefreshUsage();
    }

    /// <summary>화면에서 붙여 넣은 토큰. 자동으로 못 읽었을 때의 길이다.</summary>
    private void SetClaudeToken(string id, string token)
    {
        var acc = Accounts.Find(Settings.Load(), id);
        if (acc is null || CliProfile.For(acc.Backend) != CliProfile.Claude) { Log("그런 Claude 계정이 없습니다.", "w"); return; }
        try
        {
            Accounts.SaveClaudeToken(id, token);
            Log($"'{acc.Name}' 계정의 토큰을 저장했습니다(암호화).", "i");
        }
        catch (Exception ex) { Log($"토큰을 저장하지 않았습니다 — {ex.Message}", "w"); }
        PostConf();
        RefreshUsage();
    }

    private void RenameAccount(string id, string name, string backend)
    {
        var c = Settings.Load();
        var acc = Accounts.Find(c, id);
        var cli = CliProfile.For(acc?.Backend ?? backend);
        if (cli is null) return;
        if (!Accounts.Rename(c, cli, id, name, out var error))
        {
            Log(error, "w");
            PostConf();
            return;
        }
        c.Save();
        PostConf();
        // 열려 있는 탭 배지도 바꾼다.
        foreach (var tid in _tabs.Where(x => x.Value.Account == id && x.Value.Cli == cli).Select(x => x.Key))
            Post(new { t = "tab.account", id = tid, account = id, accountName = AccountLabel(cli, id) });
    }

    /// <summary>
    /// 계정을 지운다(화면에서 한 번 묻고 보낸다). **쓰는 탭이 있으면 안 지운다** — 그 탭은 로그인을 잃고,
    /// 되살릴 때 기본 계정으로 떨어진다.
    /// </summary>
    private void RemoveAccount(string id)
    {
        var busy = _tabs.Values.Where(h => h.Account == id).ToList();
        if (busy.Count > 0)
        {
            Log($"이 계정을 쓰는 탭이 {busy.Count}개 열려 있어 지우지 않았습니다 — 그 탭을 닫거나 다른 계정으로 넘긴 뒤 지우세요.", "w");
            return;
        }
        var c = Settings.Load();
        var name = Accounts.Find(c, id)?.Name ?? id;
        if (!Accounts.Remove(c, id, out var err)) { Log(err, "w"); return; }
        c.Save();
        Log(err.Length > 0 ? err : $"'{name}' 계정을 지웠습니다(로그인과 그 계정 폴더까지).", err.Length > 0 ? "w" : "i");
        PostConf();
    }

    // ── 설정 ────────────────────────────────────────────────────────────────

    private System.Threading.CancellationTokenSource? _modelQuery;
    private int _modelRequest;
    private string? _modelPath;
    private IReadOnlyList<AntigravityModels.Model> _antigravityModels = [];

    private async void RefreshAntigravityModels(string path)
    {
        _modelQuery?.Cancel();
        _modelQuery?.Dispose();
        _modelQuery = new();
        var token = _modelQuery.Token;
        var request = ++_modelRequest;
        Post(new { t = "conf.models.loading", request, path });
        try
        {
            var result = await System.Threading.Tasks.Task.Run(async () =>
            {
                var cli = CliLocator.Locate(CliProfile.Antigravity, path);
                return await AntigravityModels.ReadAsync(cli, token);
            }, token);
            if (token.IsCancellationRequested || request != _modelRequest) return;
            if (result.Error is null)
            {
                _modelPath = path;
                _antigravityModels = result.Models;
            }
            Post(new { t = "conf.models", request,
                models = result.Models.Select(m => m.Id).ToArray(),
                modelLabels = result.Models.ToDictionary(m => m.Id, m => m.Name), error = result.Error });
        }
        catch (OperationCanceledException) { }
        catch { Post(new { t = "conf.models", request, error = "모델 목록을 읽지 못했습니다. 다시 불러오기를 눌러 주세요." }); }
    }

    /// <summary>
    /// 설정 + 표 + **지금 탐지 결과**를 같이 보낸다.
    ///
    /// 탐지 결과를 같이 보내는 이유: 경로를 비워 두면 자동 탐지가 도는데, 그때 **무엇이
    /// 잡혔는지 안 보이면** 설정 화면은 "빈 칸" 만 보여 준다. 사용자는 지정을 해야 하는지
    /// 안 해도 되는지 알 수가 없고, 안 뜰 때 어디를 봐야 할지도 모른다.
    /// </summary>
    private void PostConf()
    {
        var c = Settings.Load();
        Post(new
        {
            t = "conf",
            settings = new
            {
                // 어느 CLI 로 열지는 여기 없다 — [＋ 새 대화] 를 누를 때마다 고른다(PostNewChat).
                c.UiLanguage, c.ClaudeModel, c.ClaudeEffort, c.CodexModel, c.CodexEffort,
                c.ClaudeCodePermissionMode, c.CodexSandbox, c.CodexApproval,
                c.ClaudeCliPath, c.CodexCliPath, c.AntigravityCliPath, c.AntigravityPermission,
                c.AntigravityModel, c.AntigravityEffort,
                // 익명 타입으로 다시 싸는 이유: Bookmark 를 그대로 보내면 나중에 필드를
                // 더할 때 화면이 모르는 값까지 넘어가고, 화면은 그것을 그대로 돌려보낸다.
                Bookmarks = c.Bookmarks.Select(b => new { b.Name, b.Url, b.Show }).ToList(),
                c.KeepSignedIn, c.SavePasswords, c.GuardClaudeLogin,
            },
            catalog = CliCatalog.ForUi(_modelPath == c.AntigravityCliPath ? _antigravityModels : []),
            detect = CliProfile.All.ToDictionary(
                p => p.Backend,
                p =>
                {
                    var loc = CliLocator.Locate(p, p.CachedPath(c));
                    return (object)new
                    {
                        found = loc.Found,
                        path = loc.Path,
                        source = loc.Source,
                        bypass = loc.CanBypassCmd,
                        error = loc.Error,
                    };
                }),
            // 계정. 기본 계정까지 CLI 마다 한 줄씩 — 설정 화면의 [계정] 카드와 맨 아래 사용량 줄이 이것을 그린다.
            accounts = Accounts.All(c).Select(a =>
            {
                var claude = a.Cli.Kind == CliKind.Claude;
                var mode = claude && !a.IsDefault ? Accounts.ClaudeModeOf(a.Id) : ClaudeMode.Login;
                return new
                {
                    backend = a.Cli.Backend,
                    id = a.Id,
                    name = a.Label,
                    email = a.Email,
                    loggedIn = a.LoggedIn,
                    note = a.Note,
                    // [폴더] 단추가 여는 곳 — 그 계정의 설정 폴더(claude) · 홈(codex). 기본 계정은 단추가 없다.
                    home = a.IsDefault ? "" : claude ? Accounts.ClaudeHomeOf(a.Id)
                        : a.Cli.Kind == CliKind.Antigravity ? AntigravityAccounts.ProfileOf(a.Id) : Accounts.CodexHomeOf(a.Id),
                    // claude 로그인 방식(빈 값 · sso · console). [다시 로그인] 이 같은 방식으로 열린다.
                    login = claude ? Accounts.Find(c, a.Id)?.Login ?? "" : "",
                    mode = mode switch { ClaudeMode.Login => "login", ClaudeMode.Token => "token", _ => "" },
                    // setup-token 계정은 사용량 조회가 거절된다(추론 전용 토큰). 줄에서 "—" 의 까닭을 다르게 적는다.
                    tokenOnly = mode == ClaudeMode.Token,
                };
            }).ToList(),
            file = Settings.FilePath,
            version = AppVersion,
        });
        RefreshAntigravityModels(c.AntigravityCliPath);
    }

    /// <summary>
    /// 설정을 저장한다. **여기서 한 번 더 검증한다** — 화면이 이미 목록을 걸러 주지만,
    /// 손으로 고친 settings.json 은 그 문을 안 지나온다. 못 받는 값이 그대로 커맨드라인으로
    /// 나가면 CLI 가 인자를 거절해 터미널이 아예 안 열리고, 화면에는 이유가 안 보인다.
    /// </summary>
    private void SaveConf(JsonNode m)
    {
        var c = Settings.Load();
        var v = m["v"];
        string Get(string k, string fallback) => v?[k]?.GetValue<string>()?.Trim() ?? fallback;
        c.UiLanguage = Get(nameof(Settings.UiLanguage), c.UiLanguage);

        // 모델·강도는 **CLI 마다 따로, 그 CLI 기준으로** 검증한다. 새 대화마다 CLI 를 고르므로
        // 둘 다 언제든 쓰인다 — 한쪽을 건너뛰면 그 CLI 를 고른 날 못 받는 값이 커맨드라인으로 나간다.
        var claude = CliProfile.Claude.Backend;
        var codex = CliProfile.Codex.Backend;
        c.ClaudeModel = CliCatalog.ClampModel(claude, Get(nameof(Settings.ClaudeModel), c.ClaudeModel), Log);
        c.ClaudeEffort = CliCatalog.ClampEffort(claude, c.ClaudeModel,
                                                Get(nameof(Settings.ClaudeEffort), c.ClaudeEffort), Log);
        c.CodexModel = CliCatalog.ClampModel(codex, Get(nameof(Settings.CodexModel), c.CodexModel), Log);
        c.CodexEffort = CliCatalog.ClampEffort(codex, c.CodexModel,
                                               Get(nameof(Settings.CodexEffort), c.CodexEffort), Log);

        // 권한도 CLI 마다 따로다. 같은 까닭으로 둘 다 검증하고 둘 다 남긴다.
        c.ClaudeCodePermissionMode =
            CliCatalog.ClampPermission("ClaudeCodePermissionMode", Get("ClaudeCodePermissionMode", c.ClaudeCodePermissionMode));
        c.CodexSandbox = CliCatalog.ClampPermission("CodexSandbox", Get("CodexSandbox", c.CodexSandbox));
        c.CodexApproval = CliCatalog.ClampPermission("CodexApproval", Get("CodexApproval", c.CodexApproval));

        c.ClaudeCliPath = Get("ClaudeCliPath", c.ClaudeCliPath);
        c.CodexCliPath = Get("CodexCliPath", c.CodexCliPath);
        c.AntigravityCliPath = Get("AntigravityCliPath", c.AntigravityCliPath);
        c.AntigravityPermission = CliCatalog.ClampPermission(nameof(Settings.AntigravityPermission),
            Get(nameof(Settings.AntigravityPermission), c.AntigravityPermission));
        c.AntigravityModel = CliCatalog.ClampModel(CliProfile.Antigravity.Backend, Get(nameof(Settings.AntigravityModel), c.AntigravityModel), Log);
        c.AntigravityEffort = CliCatalog.ClampEffort(CliProfile.Antigravity.Backend, c.AntigravityModel,
            Get(nameof(Settings.AntigravityEffort), c.AntigravityEffort), Log);

        // 바로가기(북마크). **화면이 보낸 순서 그대로 둔다** — 그 순서가 머리줄 순서다.
        // 키가 아예 없으면 건드리지 않는다(옛 화면이 보낸 메시지일 수 있다).
        if (v?["Bookmarks"] is JsonArray arr) c.Bookmarks = ReadBookmarks(arr);

        // 화면은 참/거짓으로 보내지만, 손으로 고친 settings.json 에서는 글자로 올 수 있다.
        bool Flag(JsonNode? n, bool fallback) => n switch
        {
            null => fallback,
            var x when x.GetValueKind() == JsonValueKind.True => true,
            var x when x.GetValueKind() == JsonValueKind.False => false,
            var x => !string.Equals(x.ToString(), "false", StringComparison.OrdinalIgnoreCase),
        };
        c.KeepSignedIn = Flag(v?["KeepSignedIn"], c.KeepSignedIn);
        c.SavePasswords = Flag(v?["SavePasswords"], c.SavePasswords);
        c.GuardClaudeLogin = Flag(v?["GuardClaudeLogin"], c.GuardClaudeLogin);
        c.Save();

        // 새 값을 바로 건다. **쿠키는 안 되돌린다** — 위 주석 참조.
        ApplySignInSettings(c);

        Log("설정을 저장했습니다. **다음에 여는 대화부터** 적용됩니다 — 이미 떠 있는 탭은 " +
            "그 인자로 실행 중이라 바꿀 수 없습니다.", "i");

        PostConf();
    }

    /// <summary>
    /// 화면이 보낸 북마크 목록을 받는다. **여기서 한 번 더 다듬는다** — 손으로 고친
    /// settings.json 과 옛 화면도 이 문을 지나야 하기 때문이다.
    ///
    ///   * 주소는 스킴을 붙여 준다 — 사내 GitLab 을 호스트 이름만 치는 일이 흔하다.
    ///   * 이름을 안 넣었으면 호스트로 채운다 — 이름 없는 버튼은 빈 칸으로 보인다.
    ///   * 이름도 주소도 없는 줄은 버린다 — [추가] 만 눌러 두고 안 채운 줄이다.
    ///   * 개수를 <see cref="MaxBookmarks"/> 로 끊는다. 머리줄은 탭띠와 자리를 나눠 쓴다 —
    ///     수십 개가 들어오면 탭이 밀려 사라지고, 그때는 설정으로 돌아갈 길도 좁아진다.
    /// </summary>
    private const int MaxBookmarks = 30;

    private static List<Bookmark> ReadBookmarks(JsonArray arr)
    {
        var list = new List<Bookmark>();
        foreach (var n in arr)
        {
            if (list.Count >= MaxBookmarks) break;

            // 글자가 아닌 값이 와도 던지지 않는다 — 여기서 던지면 저장이 통째로 조용히 날아간다.
            static string Str(JsonNode? x) =>
                x is null ? ""
                : x.GetValueKind() == JsonValueKind.String ? x.GetValue<string>()
                : x.ToString();

            var name = Str(n?["Name"]).Trim();
            var url = BrowserHost.Normalize(Str(n?["Url"]));
            if (name.Length == 0 && url.Length == 0) continue;
            if (name.Length == 0) name = BrowserHost.HostOf(url);

            list.Add(new Bookmark
            {
                Name = name.Length > 40 ? name[..40] : name,
                Url = url,
                // 화면은 참/거짓으로 보내지만, 손으로 고친 파일에서는 문자열로 올 수 있다.
                Show = n?["Show"] switch
                {
                    null => true,
                    var f when f.GetValueKind() == JsonValueKind.False => false,
                    var f => !string.Equals(f.ToString(), "false", StringComparison.OrdinalIgnoreCase),
                },
            });
        }
        return list;
    }

    // ── 브라우저 로그인 ─────────────────────────────────────────────────────

    /// <summary>
    /// 비밀번호 저장 설정을 프로필에 걸고, 필요하면 세션 쿠키를 되돌린다.
    ///
    /// **쿠키 복원은 시작할 때 딱 한 번만 한다.** 설정을 저장할 때마다 되돌리면 그 사이에
    /// 로그아웃한 것이 되살아난다 — "로그아웃했는데 다시 로그인돼 있다" 가 된다.
    /// </summary>
    private void ApplySignInSettings(Settings c, bool restoreCookies = false)
    {
        if (View.CoreWebView2?.Profile is not { } p) return;

        // 프로필 속성은 WebView2 런타임 판에 따라 없을 수 있다 — 없다고 앱이 죽으면 안 된다.
        try { p.IsPasswordAutosaveEnabled = c.SavePasswords; }
        catch (Exception ex) { Log($"비밀번호 저장 설정을 걸지 못했습니다 — {ex.Message}", "w"); }

        if (!c.KeepSignedIn)
        {
            // 껐으면 남겨 둔 것도 지운다. 파일만 남겨 두면 다시 켜는 순간 옛 로그인이 살아난다.
            CookieVault.Clear();
            return;
        }

        if (restoreCookies) CookieVault.Restore(p.CookieManager, Log);
    }

    /// <summary>
    /// 저장된 로그인을 지운다. **되돌릴 수 없다** — 화면 쪽에서 한 번 묻고 보낸다.
    /// </summary>
    /// <param name="everything">쿠키·사이트 저장소까지. 끄면 저장된 비밀번호만.</param>
    private async void ClearSignInData(bool everything)
    {
        if (View.CoreWebView2?.Profile is not { } p)
        {
            Log("아직 지울 수 없습니다 — WebView2 준비 전입니다.", "w");
            return;
        }

        var kinds = CoreWebView2BrowsingDataKinds.PasswordAutosave;
        if (everything)
            kinds |= CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.AllDomStorage;

        try
        {
            await p.ClearBrowsingDataAsync(kinds);
            if (everything) CookieVault.Clear();
            Log(everything ? "저장된 비밀번호와 로그인을 모두 지웠습니다. 열려 있는 브라우저 탭은 새로고침하세요."
                           : "저장된 비밀번호를 지웠습니다.", "i");
        }
        catch (Exception ex) { Log($"지우지 못했습니다 — {ex.Message}", "w"); }
    }

    // ── 지난 대화 ───────────────────────────────────────────────────────────

    private IReadOnlyList<SessionEntry> _historyEntries = Array.Empty<SessionEntry>();
    private string _historySwitchId = "";
    private SessionEntry? _historySwitchEntry;
    private bool _historySwitchBusy;

    private static string ClaudeTransferHome(string account) =>
        Accounts.ClaudeModeOf(account) == ClaudeMode.Token ? CliProfile.ClaudeHome : Accounts.ClaudeHomeOf(account);

    private static (SessionRef Session, Transcript Transcript) ReadHistorySource(SessionEntry entry)
    {
        var source = SessionFinder.Find(entry) ?? throw new FileNotFoundException("선택한 대화 기록을 찾지 못했습니다. 지난 대화 목록을 새로 열어 확인하세요.");
        var transcript = TranscriptReader.Read(source.Cli, source.Path);
        if (!transcript.HasConversation) throw new IOException("선택한 대화에 이어갈 요청·답변 기록이 없습니다.");
        return (source, transcript);
    }

    private void PostHistorySwitch(JsonNode message)
    {
        if (_historySwitchBusy) return;
        var entry = _historyEntries.FirstOrDefault(e => e.Id == message["sessionId"]?.GetValue<string>() &&
            e.Backend == message["backend"]?.GetValue<string>() && e.Account == (message["account"]?.GetValue<string>() ?? "") &&
            e.Cwd == message["cwd"]?.GetValue<string>());
        if (entry is null) { Log("지난 대화 목록을 새로 열어 대화를 선택하세요.", "w"); return; }
        _historySwitchId = "history-" + Guid.NewGuid().ToString("N");
        _historySwitchEntry = entry;
        PostSwitchChoices(_historySwitchId, CliProfile.ForOrDefault(entry.Backend), entry.Account, () => ReadHistorySource(entry), history: true);
    }

    private void SwitchHistory(string id, string backend, string account)
    {
        if (id != _historySwitchId || _historySwitchEntry is not { } entry || _historySwitchBusy) return;
        void Failed(string error)
        {
            Log(error, "e");
            Post(new { t = "history.switch.failed", id, error });
        }
        var target = CliProfile.For(backend);
        var settings = Settings.Load();
        if (target is null || !target.SupportsTransfer || Accounts.Resolve(settings, target, account) != account)
        { Failed("넘길 CLI·계정을 찾지 못했습니다. 설정의 계정을 확인하세요."); return; }
        if (target.Backend == entry.Backend && account == entry.Account)
        { Failed("다른 계정을 선택하세요."); return; }
        var to = Accounts.View(settings, target, account);
        if (!to.IsDefault && !to.LoggedIn) { Failed("선택한 계정에 먼저 로그인하세요."); return; }
        if (!CliLocator.Locate(target, target.CachedPath(settings)).Found) { Failed(target.NotFoundMessage); return; }
        if (!Directory.Exists(entry.Cwd)) { Failed($"폴더가 없어 이어갈 수 없습니다 — {entry.Cwd}"); return; }
        var source = CliProfile.ForOrDefault(entry.Backend);
        var from = $"{source.Display}({Accounts.View(settings, source, entry.Account).Label} 계정)";
        var home = target.Kind == CliKind.Claude ? ClaudeTransferHome(account) : Accounts.CodexHomeOf(account);
        _historySwitchBusy = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var (session, transcript) = ReadHistorySource(entry);
                var (mode, note) = ConversationTransfer.Prepare(source, session, transcript, entry.Cwd, target, home, from);
                Dispatcher.BeginInvoke(() =>
                {
                    _historySwitchBusy = false;
                    Post(new { t = "history.switched", id });
                    OpenTab(entry.Cwd, target.Backend, mode, account: account);
                    Log($"{from} → {target.Display}({to.Label} 계정): {note}", "i");
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => { _historySwitchBusy = false; Failed("대화를 넘기지 못했습니다 — " + ex.Message); });
            }
        });
    }

    /// <summary>
    /// 지난 대화 목록. **읽는 데 시간이 걸린다**(파일 수백 개를 훑는다) —
    /// UI 스레드에서 하면 목록 창이 뜨기 전에 앱이 멎은 것처럼 보이므로 배경으로 보낸다.
    /// </summary>
    private void PostHistory()
    {
        Post(new { t = "history.loading" });
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var settings = Settings.Load();
                var entries = SessionStore.Recent(settings);
                var items = SessionStore.ForUi(settings, entries);
                Dispatcher.BeginInvoke(() => { _historyEntries = entries; Post(new { t = "history", items }); });
            }
            catch (Exception ex)
            {
                Post(new { t = "history", items = Array.Empty<object>() });
                Log("지난 대화를 읽지 못했습니다: " + ex.Message, "e");
            }
        });
    }

    // ── 사용량 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// UI 스레드가 막히는지 지켜본다.
    ///
    /// **왜 필요한가.** 터미널 키 입력도(WebMessageReceived) 화면 출력도(Post → Dispatcher)
    /// 전부 UI 스레드를 지난다. 그래서 거기가 잠깐이라도 막히면 **입력이 안 먹다가
    /// 한꺼번에 들어가는** 것으로 보인다 — 커서는 xterm 이 혼자 깜빡이므로 멀쩡해 보이고,
    /// 그 탓에 "무엇이 막았는지" 를 짐작할 단서가 화면에 하나도 안 남는다.
    ///
    /// 그래서 재 둔다. 1초마다 UI 스레드에 빈 일을 하나 던지고 돌아오는 데 걸린 시간을 본다.
    /// 오래 걸렸으면 **얼마나** 걸렸는지 기록에 남긴다 — 다음에 그 현상이 나면 그때
    /// 무엇이 얼마나 잡고 있었는지 숫자로 보인다.
    ///
    /// 재는 쪽은 반드시 UI 스레드 **밖**이어야 한다. DispatcherTimer 로 재면 자기도 같이
    /// 밀려 아무것도 못 잡는다.
    /// </summary>
    private void StartStallWatch()
    {
        if (_stallWatch is not null) return;

        _stallWatch = new System.Threading.Timer(_ =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Dispatcher.InvokeAsync(() => { }).Task.Wait(TimeSpan.FromSeconds(20));
            }
            catch { return; }

            var ms = sw.ElapsedMilliseconds;
            if (ms < StallWarnMs) { _stallSeen = false; return; }

            // **한 번만 적는다.** 막혀 있는 동안 1초마다 적으면 기록이 그것으로 덮인다.
            if (_stallSeen) return;
            _stallSeen = true;
            Log($"화면이 {ms}ms 동안 멎었습니다 — 그동안 터미널 입력이 밀립니다. " +
                "되풀이되면 이 줄의 숫자를 알려 주세요.", "w");
        }, null, 1000, 1000);
    }

    /// <summary>이보다 오래 걸리면 사람이 느낀다. 타이핑 반향이 밀리기 시작하는 언저리다.</summary>
    private const int StallWarnMs = 400;

    private System.Threading.Timer? _stallWatch;
    private bool _stallSeen;

    /// <summary>
    /// 계정 사용량을 화면 맨 아래 줄로 밀어 준다.
    ///
    /// **두 CLI 것을 다 보내고, 화면도 둘 다 늘 보여 준다.** CLI 를 새 대화마다 고르므로
    /// 고르기 전에 어느 쪽이 얼마나 남았는지가 보여야 한다. 지금 탭이 어느 것인지는 화면이
    /// 짚는다 — 활성 탭은 화면이 주인이고(탭 전환은 화면 안에서만 일어난다) 두 곳에 두면
    /// 반드시 어긋난다.
    ///
    /// 주기는 넉넉하게 잡는다. 한도는 5시간·1주 단위로 움직이므로 몇 분 늦어도 아무 일이
    /// 없고, claude 쪽은 **네트워크를 타므로** 자주 부를수록 손해다.
    /// </summary>
    private void StartUsage()
    {
        if (_usage is not null) return;
        _usage = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _usage.Tick += (_, _) =>
        {
            RefreshUsage();

            // **배경으로 보낸다.** 여기서 그냥 부르면 UI 스레드가 파일 읽기·JSON·DPAPI 를
            // 붙들고, 그동안 터미널 입력(term.in)과 출력(term.out)이 **둘 다** 그 뒤에 줄을 선다.
            // 화면에는 "타이핑이 안 먹다가 한꺼번에 들어간다" 로만 보인다.
            // 사본만 뜨는 일이라 어느 스레드에서 하든 상관없다.
            System.Threading.Tasks.Task.Run(() =>
            {
                try { if (Settings.Load().GuardClaudeLogin) ClaudeAuthGuard.Guard(mayRestore: false, Log); }
                catch { }
            });
        };
        _usage.Start();
        RefreshUsage();
    }

    /// <summary>
    /// **UI 스레드에서 하지 않는다.** claude 쪽은 HTTP 라 끊긴 망에서는 타임아웃까지 10초를
    /// 잡아먹고, codex 쪽은 세션 파일을 훑는다 — 둘 다 창을 멎게 만들 수 있는 일이다.
    /// 둘을 따로 띄우는 이유도 같다: 한쪽이 느려도 다른 쪽은 제때 올라온다.
    /// </summary>
    private void RefreshUsage()
    {
        var c = Settings.Load();
        var claude = CliProfile.Claude.Backend;
        var codex = CliProfile.Codex.Backend;

        System.Threading.Tasks.Task.Run(async () =>
            PostUsage(claude, "", await UsageProbe.ClaudeAsync()));
        System.Threading.Tasks.Task.Run(async () =>
            PostUsage(codex, "", await UsageProbe.CodexAccountAsync(CliProfile.CodexHome)));

        // 더 넣은 계정마다 따로. **넘길 곳을 고르는 자리에서 그 계정의 잔량이 보여야** 이 기능이 쓸모 있다.
        foreach (var a in c.Accounts)
        {
            var id = a.Id;
            if (CliProfile.For(a.Backend) == CliProfile.Claude)
            {
                // 폴더 로그인은 그 폴더의 토큰으로(권한이 온전해 읽힌다), setup-token 은 그 토큰으로(대개 거절된다).
                var mode = Accounts.ClaudeModeOf(id);
                var home = Accounts.ClaudeHomeOf(id);
                var token = mode == ClaudeMode.Token ? Accounts.ClaudeToken(id) : null;
                System.Threading.Tasks.Task.Run(async () =>
                    PostUsage(claude, id, mode switch
                    {
                        ClaudeMode.Login => await UsageProbe.ClaudeAsync(home: home),
                        ClaudeMode.Token when token is not null => await UsageProbe.ClaudeAsync(token),
                        _ => null,
                    }));
            }
            else if (CliProfile.For(a.Backend) == CliProfile.Codex)
            {
                var home = Accounts.CodexHomeOf(id);
                System.Threading.Tasks.Task.Run(async () =>
                    PostUsage(codex, id, await UsageProbe.CodexAccountAsync(home)));
            }
        }
    }

    /// <summary>
    /// 못 읽었으면 **빈 목록을 보낸다.** 아무것도 안 보내면 화면에는 지난번 값이 그대로 남아,
    /// 로그아웃하거나 망이 끊긴 뒤에도 옛 숫자를 사실인 양 계속 보여 준다.
    /// </summary>
    private void PostUsage(string backend, string account, IReadOnlyList<UsageWindow>? windows) =>
        Post(new
        {
            t = "usage",
            backend,
            account,
            windows = (windows ?? []).Select(w => new { label = w.Label, pct = w.Percent, reset = w.ResetsAtUnix }),
        });

    // ── CLI 가 부르는 브라우저 ──────────────────────────────────────────────

    /// <summary>페이지 글·스크립트 결과를 자를 길이. 넘기면 읽는 쪽 맥락이 통째로 날아간다.</summary>
    private const int MaxAgentChars = 60_000;

    private void StartBridge()
    {
        if (_bridge is not null) return;
        _bridge = new BrowserBridge(HandleAgentCommand, Log);
        _bridge.Start();

        // 창구가 열린 **뒤에** 쪽지를 쓴다 — 못 쓰는 도구를 있다고 알리지 않는다.
        AgentBrief.Write();
        TerminalHost.BrowserTicket = _bridge.Ticket;
        TerminalHost.BrowserPipeName = _bridge.PipeName;
    }

    /// <summary>
    /// 창구는 배경 스레드에서 부른다. 여기서 하는 일은 전부 WebView2 를 만지므로
    /// **UI 스레드로 넘긴다** — 다른 스레드에서 건드리면 예외가 나고, 그 예외는
    /// 배경에서 조용히 사라져 에이전트에게는 "답이 안 온다" 로만 보인다.
    /// </summary>
    private Task<object> HandleAgentCommand(string cmd, string[] a) =>
        Dispatcher.InvokeAsync(() => AgentCommandCore(cmd, a)).Task.Unwrap();

    private async Task<object> AgentCommandCore(string cmd, string[] a)
    {
        // 번호를 받는 명령은 전부 같은 방식으로 찾는다. 없는 번호를 조용히 넘기면
        // 에이전트가 엉뚱한 탭을 봤다고 믿는다.
        BrowserHost Tab(int at = 0)
        {
            var id = a.Length > at ? a[at] : "";
            if (id.Length == 0) throw new ArgumentException("탭 번호가 없습니다. `tabs` 로 확인하세요.");
            if (!_browsers.TryGetValue(id, out var b))
                throw new ArgumentException($"'{id}' 탭이 없습니다. `tabs` 로 확인하세요.");
            return b;
        }
        string Arg(int at, string name) =>
            a.Length > at && a[at].Length > 0 ? a[at] : throw new ArgumentException($"{name} 가 없습니다.");

        switch (cmd)
        {
            case "tabs":
                return _order
                    .Where(_browsers.ContainsKey)
                    .Select(id => _browsers[id])
                    .Select(b => new { id = b.Id, title = b.Title, url = b.Url })
                    .ToList();

            case "open":
            {
                var url = BrowserHost.Normalize(Arg(0, "주소"));
                // `--private` 는 도우미가 붙인다 — 계정 로그인 탭(AGENTCLIHUB_BROWSER_PRIVATE)이 연 로그인 페이지다.
                var priv = a.Skip(1).Any(x => x == "--private");
                var id = OpenBrowserTab(url, inPrivate: priv);
                return new { id, url, inPrivate = priv, closeCommand = $"close {id}" };
            }

            case "goto":
            {
                var b = Tab();
                b.NavigateTo(Arg(1, "주소"));
                return new { id = b.Id, url = b.Url };
            }

            case "back":
            case "forward":
            case "reload":
            {
                var b = Tab();
                if (!b.Go(cmd)) throw new InvalidOperationException($"'{cmd}' 할 곳이 없습니다.");
                return new { id = b.Id };
            }

            case "text":
                return await Tab().ReadTextAsync(MaxAgentChars);

            // **임의의 JS 다.** 여기 열려 있는 것은 사용자가 Jira·GitHub 에 로그인해 둔 바로
            // 그 세션이라, 이 명령은 그 계정으로 할 수 있는 일을 전부 할 수 있다.
            // 그래도 두는 이유: 이것이 없으면 클릭·입력·접힌 영역 펼치기가 전부 막혀
            // 브라우저가 사실상 읽기 전용이 된다.
            case "eval":
                return await Tab().EvalAsync(Arg(1, "스크립트"), MaxAgentChars);

            case "shot":
            {
                var b = Tab();
                var path = System.IO.Path.GetFullPath(Arg(1, "저장 경로"));

                // **먼저 그 탭을 띄운다.** 화면에 없는 탭은 그릴 면이 없어 캡처가 안 끝난다
                // (BrowserHost.CaptureAsync 참조). 탭을 고르는 일은 화면이 하므로 부탁만 하고,
                // 그림이 올라올 짬을 잠깐 준다 — 바로 찍으면 아직 빈 면일 수 있다.
                Post(new { t = "tab.show", id = b.Id });
                await Task.Delay(400);

                await b.CaptureAsync(path);
                return new { id = b.Id, path };
            }

            case "close":
            {
                var b = Tab();
                CloseTab(b.Id);
                // UI의 닫기 버튼과 달리 에이전트 호출은 탭띠를 직접 지우지 않는다.
                // 네이티브 View뿐 아니라 화면의 탭·선택 상태도 함께 정리한다.
                Post(new { t = "tab.closed", id = b.Id });
                return new { id = b.Id, closed = true };
            }

            default:
                throw new ArgumentException(
                    $"모르는 명령 '{cmd}' — tabs · open · goto · back · forward · reload · text · eval · shot · close");
        }
    }

    // ── 대화상자 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 첨부할 파일 고르기. **경로만 화면에 돌려준다** — 내용은 전송할 때 호스트가 다룬다.
    /// 브라우저가 읽어 base64 로 올리면 큰 파일 하나에 메시지가 통째로 부풀고,
    /// 크기 제한·형식 판정이 두 곳에 생긴다.
    /// </summary>
    private void PickAttachments()
    {
        var picked = Dialogs.PickFiles(this, UiText.Pick("대화에 첨부할 파일", "Attach files to the conversation"),
            UiText.Pick("지원 파일", "Supported files") + "|*.txt;*.log;*.csv;*.tsv;*.json;*.xml;*.yaml;*.yml;*.ini;*.md;" +
            "*.c;*.h;*.cpp;*.hpp;*.cs;*.js;*.mjs;*.ts;*.py;*.lua;*.ps1;*.sql;*.diff;" +
            "*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|" + UiText.Pick("모든 파일", "All files") + "|*.*");
        if (picked.Length == 0) return;

        var files = picked.Select(p => new
        {
            path = p,
            name = Path.GetFileName(p),
            size = new FileInfo(p).Length,
        }).ToList();

        Post(new { t = "attach.picked", files });
    }

    /// <summary>
    /// 화면에서 영역을 골라 첨부에 넣는다(캡처 도구의 그 동작).
    ///
    /// **붙여넣기와 같은 모양으로 돌려준다**(<c>kind: paste</c> · data URL). 화면·전송·저장
    /// 어느 쪽도 새 길을 안 타므로, 잘라 낸 그림은 Ctrl+V 로 붙인 것과 똑같이 작업 폴더의
    /// <c>.agentclihub-attachments</c> 로 떨어지고 CLI 가 그 경로를 그대로 읽는다.
    ///
    /// 취소는 조용히 끝낸다 — 안 고른 것을 오류로 적으면 기록만 시끄러워진다.
    /// </summary>
    private void CaptureRegion()
    {
        try
        {
            var png = RegionCapture.Pick(this);
            if (png is null) return;

            Post(new
            {
                t = "attach.captured",
                dataUrl = "data:image/png;base64," + Convert.ToBase64String(png),
                size = png.Length,
            });
        }
        catch (Exception ex)
        {
            Log($"화면을 캡처하지 못했습니다 — {ex.Message}", "w");
        }
    }

    /// <summary>CLI 실행 파일 직접 고르기. 자동 탐지가 엉뚱한 것을 잡았을 때의 탈출구다.</summary>
    private void PickCliPath(string which)
    {
        var cli = CliProfile.ForOrDefault(which);
        var path = Dialogs.PickFile(this, UiText.Pick($"{cli.Display} 실행 파일", $"{cli.Display} executable"),
            UiText.Pick("실행 파일", "Executables") + "|*.exe;*.cmd;*.bat;*.ps1|" + UiText.Pick("모든 파일", "All files") + "|*.*");
        if (path is null) return;

        Post(new { t = "cli.picked", which = cli.Backend, path });
    }

    // ── 화면으로 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 화면에 보낸다. **UI 스레드로 마샬링한다** — pty 읽기 스레드·타이머에서 그대로 부르면
    /// WebView2 가 잘못된 스레드라고 예외를 내고, 그 예외는 배경 스레드에서 조용히 사라진다.
    /// </summary>
    private void Post(object payload)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Post(payload)); return; }
        try { View.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload, _json)); }
        catch { /* 아직 준비 안 됨 / 창이 닫히는 중 */ }
    }

    private static readonly JsonSerializerOptions _json = new()
    {
        // 한글이 \uXXXX 로 나가면 화면에서는 멀쩡히 보이지만 개발자 도구에서 읽을 수가 없다.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>하단 로그 한 줄. level: i(알림) · w(주의) · e(오류).</summary>
    private void Log(string msg, string level) => Post(new { t = "log", msg, level });
}
