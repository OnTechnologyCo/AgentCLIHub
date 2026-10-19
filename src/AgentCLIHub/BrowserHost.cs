using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgentCLIHub;

/// <summary>
/// 브라우저 탭 하나 = WebView2 하나.
///
/// **왜 iframe 이 아닌가.** 노리는 세 사이트가 전부 프레이밍을 막는다 —
/// github.com 은 <c>X-Frame-Options: deny</c>, gitlab.com 은 <c>SAMEORIGIN</c>,
/// atlassian 은 <c>DENY</c>. 화면(index.html) 안에 iframe 으로 넣으면 빈 칸만 뜬다.
/// 그래서 네이티브 컨트롤을 따로 띄우고, 화면은 그 자리만 비워 둔다.
///
/// **환경(<see cref="CoreWebView2Environment"/>)은 창의 것을 그대로 받는다.** 프로필이
/// 같아야 쿠키·로그인이 한 벌로 유지된다 — Jira SSO 를 탭마다 다시 하게 되면 쓸 수가 없다.
/// 새로 만들면 프로필이 갈리고, 갈린 것은 "로그인이 자꾸 풀린다" 로만 보인다.
/// </summary>
public sealed class BrowserHost : IDisposable
{
    private readonly Action<object> _post;
    private readonly Action<string, string> _log;
    private readonly Action<string> _openInNewTab;
    private readonly Func<BrowserHost, string, bool, Task<CoreWebView2?>>? _openPopup;
    private readonly CoreWebView2Environment _env;

    private bool _dead;

    public string Id { get; }

    /// <summary>
    /// InPrivate 탭인가. 쿠키·로그인이 이 탭(과 이 탭이 연 팝업)에만 있고 **탭을 다 닫으면 사라진다.**
    /// 계정 로그인 탭이 이것으로 연다 — 앱 브라우저에 이미 로그인된 다른 claude.ai·Google 계정이 섞여
    /// 엉뚱한 계정이 승인되는 일을 막는다. 앱을 껐다 켜도 되살리지 않는다(되살려도 로그인이 없다).
    /// </summary>
    public bool InPrivate { get; }

    /// <summary>이 탭을 연 탭(<c>window.open</c> 의 opener). 팝업이 닫히면 화면은 그 탭으로 돌아간다.</summary>
    public string OpenerId { get; init; } = "";

    /// <summary>
    /// 창을 스스로 여닫는 팝업인가(<c>window.open</c> 에 크기·위치를 준 것 — 로그인 창이 대개 그렇다).
    /// 앱을 껐다 켜도 되살리지 않는다. <c>target=_blank</c> 링크로 연 탭은 보통 탭이다.
    /// </summary>
    public bool Transient { get; init; }

    /// <summary>지금 보고 있는 주소. 앱을 껐다 켤 때 이 값으로 되살린다.</summary>
    public string Url { get; private set; }

    /// <summary>탭 이름에 쓸 문서 제목. 아직 안 열렸으면 호스트 이름이다.</summary>
    public string Title { get; private set; }

    public WebView2 View { get; }

    /// <param name="openPopup">
    /// <c>window.open</c> 을 받아 줄 곳 — 새 탭을 세우고 **아직 아무 데도 안 간** CoreWebView2 를 돌려준다
    /// (<see cref="OnNewWindow"/>). null 이면 주소만 받아 새 탭에서 따로 연다(opener 가 끊긴다).
    /// </param>
    public BrowserHost(string id, string url, CoreWebView2Environment env,
                       Action<object> post, Action<string, string> log, Action<string> openInNewTab,
                       Func<BrowserHost, string, bool, Task<CoreWebView2?>>? openPopup = null, bool inPrivate = false)
    {
        Id = id;
        Url = Normalize(url);
        Title = HostOf(Url);
        _env = env;
        _post = post;
        _log = log;
        _openInNewTab = openInNewTab;
        _openPopup = openPopup;
        InPrivate = inPrivate;

        // 처음에는 접어 둔다. 자리는 화면이 재서 알려 주고(browser.layout), 그 전에는
        // 어디에 얼마만 하게 둘지 알 수 없다 — 짐작한 크기로 띄우면 한 번 깜빡인다.
        View = new WebView2 { Visibility = Visibility.Collapsed };
    }

    /// <summary>
    /// 실제 기동. 생성자에서 못 한다(<c>EnsureCoreWebView2Async</c> 가 비동기다).
    /// **실패해도 예외를 안 던진다** — 탭 하나가 안 열리는 것으로 앱이 죽으면 안 된다(그때는 null).
    /// </summary>
    /// <param name="navigate">
    /// 끝에 <see cref="Url"/> 로 갈까. **팝업은 안 간다** — WebView2 가 opener 와 이어 준 채 스스로 그 주소로 보낸다.
    /// 먼저 한 번이라도 가 버리면 <c>NewWindow</c> 로 못 넘긴다.
    /// </param>
    public async Task<CoreWebView2?> InitAsync(bool navigate = true)
    {
        try
        {
            CoreWebView2ControllerOptions? opts = null;
            if (InPrivate)
            {
                opts = _env.CreateCoreWebView2ControllerOptions();
                opts.IsInPrivateModeEnabled = true;
            }
            await View.EnsureCoreWebView2Async(_env, opts);
        }
        catch (Exception ex)
        {
            _log($"브라우저 탭을 열지 못했습니다: {ex.Message}", "e");
            return null;
        }

        var c = View.CoreWebView2;

        // 여기는 **브라우저다.** 터미널 쪽(MainWindow 의 View)과 달리 오른쪽 클릭·상태줄·
        // 브라우저 단축키를 다 켠다 — Ctrl+F 찾기와 Ctrl+± 확대가 그것으로 그냥 된다.
        var s = c.Settings;
        s.AreDefaultContextMenusEnabled = true;
        s.IsStatusBarEnabled = true;
        s.AreBrowserAcceleratorKeysEnabled = true;
        s.IsZoomControlEnabled = true;

        // **가상 호스트를 걸지 않는다.** 창 쪽 View 는 web/ 폴더를 agentclihub.local 로 걸어 두지만,
        // 이쪽은 바깥 웹을 띄우는 자리다. 같은 매핑을 걸면 바깥 페이지가 로컬 파일에 닿는다.

        c.SourceChanged += (_, _) => Report();
        c.HistoryChanged += (_, _) => Report();
        c.DocumentTitleChanged += (_, _) => { Title = c.DocumentTitle; Report(); };

        c.NavigationStarting += (_, e) => Report(loading: true, url: e.Uri);
        c.NavigationCompleted += (_, e) =>
        {
            Report();
            // 취소는 사용자가 멈춘 것이다 — 말할 것이 없다.
            if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                _log($"열지 못했습니다 ({e.WebErrorStatus}) — {Url}", "w");
        };

        // 링크의 target=_blank · window.open. **새 탭으로 돌린다** — 막아 두면 Jira 에서
        // 이슈를 새 창으로 여는 링크가 아무 반응이 없고, 그대로 두면 크롬 없는 맨 창이 뜬다.
        c.NewWindowRequested += OnNewWindow;

        // 페이지가 스스로 닫으려는 경우(window.close). **이 탭만** 닫는다.
        // 로그인 팝업은 인증을 마치면 이것으로 스스로 닫힌다.
        DetachParentWindowClose(c);
        c.WindowCloseRequested += (_, _) => _post(new { t = "tab.closed", id = Id });

        // 배치는 이미 왔을 수 있다(화면이 tab.opened 를 받자마자 보낸다). 그때는 아직
        // HWND 가 없어 앞으로 못 올렸으므로 여기서 한 번 더 올린다.
        BringToFront();

        if (navigate) Navigate(Url);
        return c;
    }

    /// <summary>
    /// WPF <see cref="WebView2"/> 컨트롤이 **내장한** 닫기 처리기를 뗀다.
    ///
    /// 그 처리기는 페이지가 <c>window.close()</c> 를 부르면 **컨트롤을 품은 WPF 창을 닫는다**
    /// (<c>WebView2Base.CoreWebView2_WindowCloseRequested</c> → <c>_parentWindow.Close()</c>) — 창 하나에
    /// WebView2 하나를 두는 앱을 가정한 것이다. 이 앱은 탭이 전부 메인 창 안에 있어서, 로그인 팝업이 인증을
    /// 마치고 스스로 닫히는 순간 **AgentCLIHub 창 전체가 닫힌다**(실측: 창의 Closing 이 우리 처리기보다 먼저 온다 —
    /// 컨트롤이 먼저 구독하기 때문이다). 그래서 떼어 내고, 닫기는 우리 처리기(그 탭만 닫기)가 받게 한다.
    ///
    /// 비공개 멤버를 찾아 떼므로 SDK 가 바뀌면 못 찾을 수 있다 — 그때는 기록만 남긴다. 메인 창의
    /// <see cref="IsPageCloseRequest"/> 검사가 두 번째 울타리다(창이 닫히기 직전에 거절한다).
    /// </summary>
    private void DetachParentWindowClose(CoreWebView2 c)
    {
        try
        {
            var impl = typeof(WebView2).GetField("m_webview2Base",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(View);
            var method = impl?.GetType().GetMethod(WpfCloseHandler,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (impl is null || method is null)
            {
                _log("브라우저 탭의 창 닫기 처리기를 못 찾았습니다 — 페이지가 창을 닫으려 하면 앱이 대신 거절합니다.", "w");
                return;
            }
            c.WindowCloseRequested -= (EventHandler<object>)Delegate.CreateDelegate(typeof(EventHandler<object>), impl, method);
        }
        catch (Exception ex)
        {
            _log($"브라우저 탭의 창 닫기 처리기를 떼지 못했습니다 — {ex.Message}", "w");
        }
    }

    /// <summary>WPF WebView2 컨트롤 안의 그 처리기 이름(Microsoft.Web.WebView2.Wpf 1.0.4078 에서 확인).</summary>
    private const string WpfCloseHandler = "CoreWebView2_WindowCloseRequested";

    /// <summary>
    /// 지금 창을 닫으려는 것이 **페이지의 <c>window.close()</c>** 인가 — 호출 경로에 WPF WebView2 컨트롤의
    /// 닫기 처리기가 있으면 그렇다. 메인 창의 Closing 이 이것으로 거절한다(<see cref="DetachParentWindowClose"/> 를 못 했을 때).
    /// </summary>
    public static bool IsPageCloseRequest() =>
        new System.Diagnostics.StackTrace(false).GetFrames()
            .Any(f => f.GetMethod() is { } m && m.Name == WpfCloseHandler &&
                      m.DeclaringType?.Namespace == "Microsoft.Web.WebView2.Wpf");

    /// <summary>
    /// 새 창 요청. **opener 를 이은 채 새 탭으로 연다** — 새 탭을 세우고 그 CoreWebView2 를
    /// <see cref="CoreWebView2NewWindowRequestedEventArgs.NewWindow"/> 로 넘긴다.
    ///
    /// **주소만 받아 새 탭에서 따로 열면 안 된다.** 로그인 팝업(구글 · 마이크로소프트 · 회사 SSO)은 인증을 마치면
    /// 결과를 <c>window.opener</c> 로 원래 창에 넘기고 스스로 닫힌다. 따로 열면 그 연결이 끊겨 팝업은 흰 화면으로
    /// 멈추고, 원래 창은 로그인을 영영 모른다 — claude.ai 의 [Google로 계속하기] 가 정확히 그랬다(1.5.1 까지).
    ///
    /// 새 탭은 **opener 와 같은 프로필**(InPrivate 까지)이어야 한다 — 다르면 WebView2 가 거절한다.
    /// 못 세우면 예전처럼 주소만 새 탭에서 연다(적어도 페이지는 보인다).
    /// </summary>
    private async void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            e.Handled = true;
            var popup = e.WindowFeatures is { } f && (f.HasSize || f.HasPosition);
            var child = _openPopup is null ? null : await _openPopup(this, e.Uri, popup);
            if (child is not null) e.NewWindow = child;
            else _openInNewTab(e.Uri);
        }
        catch (Exception ex)
        {
            _log($"새 창을 이어 열지 못해 주소만 새 탭에서 엽니다 — {ex.Message}", "w");
            try { _openInNewTab(e.Uri); } catch { }
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ── 화면이 시키는 것 ────────────────────────────────────────────────────

    public void Handle(string t, System.Text.Json.Nodes.JsonNode m)
    {
        if (_dead || View.CoreWebView2 is not { } c) return;

        switch (t)
        {
            case "browser.nav":
                switch (m["action"]?.GetValue<string>())
                {
                    case "back": if (c.CanGoBack) c.GoBack(); break;
                    case "forward": if (c.CanGoForward) c.GoForward(); break;
                    case "reload": c.Reload(); break;
                    case "stop": c.Stop(); break;
                    case "go": Navigate(m["url"]?.GetValue<string>() ?? ""); break;
                }
                break;
        }
    }

    // ── CLI 가 시키는 것 ────────────────────────────────────────────────────

    /// <summary>
    /// 페이지에 보이는 글. **스크립트는 우리가 들고 있는 고정된 것이다** — 에이전트가
    /// JS 를 넘길 수 있으면 사용자가 로그인해 둔 Jira·GitHub 세션에서 무엇이든 하게 된다.
    ///
    /// HTML 이 아니라 innerText 를 준다. 태그까지 주면 한 페이지가 수십만 자라
    /// 읽는 쪽 맥락이 통째로 날아가고, 정작 필요한 것은 사람이 보는 글이다.
    /// </summary>
    public async Task<string> ReadTextAsync(int maxChars)
    {
        if (View.CoreWebView2 is not { } c) return "";

        // 반환값은 **JSON 으로 인코딩된 문자열**이다(따옴표째 온다). 그대로 쓰면 안 된다.
        var raw = await c.ExecuteScriptAsync(
            "document.body ? document.body.innerText : ''").ConfigureAwait(true);

        string text;
        try { text = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? ""; }
        catch { text = raw; }

        if (text.Length > maxChars)
            text = text[..maxChars] + $"\n\n… ({text.Length - maxChars}자 잘림)";
        return text;
    }

    /// <summary>
    /// 페이지 안에서 스크립트를 돌린다. 클릭·입력·접힌 영역 펼치기가 전부 이것으로 된다.
    ///
    /// **여기 열려 있는 것은 사용자가 로그인해 둔 그 세션이다.** 이 명령은 그 계정으로
    /// 할 수 있는 일을 전부 할 수 있다 — 티켓 상태 변경도, 코멘트 작성도, 머지도.
    /// 그것을 알고 여는 문이다(없으면 브라우저가 사실상 읽기 전용이 된다).
    ///
    /// 돌려주는 것은 **JSON 으로 인코딩된 값**이다. 문자열이면 따옴표째 온다 —
    /// 부르는 쪽이 그대로 파싱하면 된다. 우리가 한 번 풀어 주면 숫자·객체와 구별이 안 된다.
    /// </summary>
    public async Task<string> EvalAsync(string script, int maxChars)
    {
        if (View.CoreWebView2 is not { } c)
            throw new InvalidOperationException("탭이 아직 준비되지 않았습니다.");

        var raw = await c.ExecuteScriptAsync(script).ConfigureAwait(true) ?? "null";
        return raw.Length > maxChars
            ? raw[..maxChars] + $"\n… ({raw.Length - maxChars}자 잘림)"
            : raw;
    }

    /// <summary>
    /// 지금 보이는 화면을 PNG 로 저장한다.
    ///
    /// **화면에 안 떠 있는 탭은 못 찍는다.** WebView2 의 <c>CapturePreviewAsync</c> 는 그릴 면이
    /// 없으면 오류를 내는 것이 아니라 **영영 안 끝난다** — 부르는 쪽은 답이 없는 것으로만 보이고,
    /// 창구는 그 하나에 물려 통째로 멈춘다. 그래서 여기서 두 겹으로 막는다.
    ///   1. 보이지 않으면 애초에 부르지 않고 이유를 말한다(부르는 쪽이 탭을 먼저 띄우면 된다).
    ///   2. 그래도 안 끝나면 시간으로 끊는다 — 보인다고 판정해도 그리기 전일 수 있다.
    ///
    /// **파일은 다 받은 뒤에 쓴다.** 스트림을 바로 물리면 늦은 캡처가 그 파일을 계속 잡고 있어,
    /// 다음 시도가 "다른 프로세스가 사용 중" 으로 막힌다.
    /// </summary>
    public async Task CaptureAsync(string path)
    {
        if (View.CoreWebView2 is not { } c) throw new InvalidOperationException("탭이 아직 준비되지 않았습니다.");
        if (!View.IsVisible || View.ActualWidth < 1 || View.ActualHeight < 1)
            throw new InvalidOperationException(
                $"'{Id}' 탭이 화면에 없어 캡처할 수 없습니다 — 그 탭을 먼저 띄우세요.");

        var buf = new MemoryStream();
        await c.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, buf)
               .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(path, buf.ToArray()).ConfigureAwait(true);
    }

    /// <summary>주소창 없이 프로그램이 시키는 이동. 사람이 친 것과 같은 길로 보낸다.</summary>
    public void NavigateTo(string url) => Navigate(url);

    /// <summary>뒤로·앞으로·새로고침. 못 가면 <c>false</c> — 조용히 성공한 척하지 않는다.</summary>
    public bool Go(string action)
    {
        if (View.CoreWebView2 is not { } c) return false;
        switch (action)
        {
            case "back": if (!c.CanGoBack) return false; c.GoBack(); return true;
            case "forward": if (!c.CanGoForward) return false; c.GoForward(); return true;
            case "reload": c.Reload(); return true;
            default: return false;
        }
    }

    private void Navigate(string url)
    {
        url = Normalize(url);
        if (url.Length == 0) return;
        try { View.CoreWebView2?.Navigate(url); }
        catch (Exception ex) { _log($"주소가 올바르지 않습니다 — {url} ({ex.Message})", "w"); }
    }

    /// <summary>
    /// 지금 상태를 화면으로 올린다. 주소·제목·뒤로/앞으로 가능 여부.
    /// **화면이 따로 캐시하지 않게 매번 전부 보낸다** — 일부만 보내면 어느 값이 최신인지
    /// 양쪽이 다르게 알게 되고, 그 차이는 "뒤로 버튼이 안 살아난다" 로만 보인다.
    /// </summary>
    private void Report(bool loading = false, string? url = null)
    {
        if (_dead || View.CoreWebView2 is not { } c) return;

        // NavigationStarting 은 아직 Source 가 안 바뀐 시점이라 넘겨받은 주소를 쓴다.
        Url = url ?? (string.IsNullOrEmpty(c.Source) ? Url : c.Source);
        if (string.IsNullOrWhiteSpace(Title)) Title = HostOf(Url);

        _post(new
        {
            t = "browser.state",
            id = Id,
            url = Url,
            title = string.IsNullOrWhiteSpace(c.DocumentTitle) ? HostOf(Url) : c.DocumentTitle,
            canBack = c.CanGoBack,
            canForward = c.CanGoForward,
            loading,
        });
    }

    // ── 주소 다듬기 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 사람이 친 것을 주소로 만든다. <c>github.com/foo</c> 처럼 스킴을 빼고 치는 것이
    /// 보통이라 그대로 넘기면 파일 경로로 읽혀 엉뚱한 곳으로 간다.
    ///
    /// **검색으로 넘기지 않는다.** 검색 엔진을 하나 고르는 순간 그것이 설정이 되고,
    /// 이 앱이 노리는 것은 정해진 세 사이트를 오가는 일이다.
    /// </summary>
    public static string Normalize(string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)) return u.ToString();
        return "https://" + url.TrimStart('/');
    }

    /// <summary>주소에서 호스트만. 이름을 안 붙인 북마크의 이름표로도 쓴다.</summary>
    public static string HostOf(string url)
    {
        try { return new Uri(url).Host; } catch { return url; }
    }

    // ── z-order ─────────────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
                                            int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// 이 판을 형제 자식 창들 중 맨 앞으로 올린다.
    ///
    /// **이것이 없으면 화면이 통째로 빈칸이다.** <see cref="WebView2"/>(WPF)는 HwndHost 라
    /// 자식 HWND 로 그려지고, 자식 HWND 의 앞뒤는 **WPF 의 z-order 를 안 따른다** — 창을
    /// 채우고 있는 쪽(index.html 을 그리는 View)이 나중에 얹은 이 판을 덮어 버린다.
    /// 재현해 보면 붙기는 다 붙어서(제목·주소가 갱신된다) 그림만 안 나온다.
    ///
    /// 배치가 바뀔 때마다 다시 부른다. WPF 가 레이아웃을 다시 돌리면서 앞뒤를 되돌릴 수 있다.
    /// </summary>
    public void BringToFront()
    {
        var hwnd = View.Handle;                       // HwndHost 가 아직 안 지어졌으면 0 이다
        if (_dead || hwnd == IntPtr.Zero) return;
        const uint TopNoMoveNoSizeNoActivate = 0x0001 | 0x0002 | 0x0010;
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, TopNoMoveNoSizeNoActivate);
    }

    public void Dispose()
    {
        if (_dead) return;
        _dead = true;
        try { View.Dispose(); } catch { }
    }
}
