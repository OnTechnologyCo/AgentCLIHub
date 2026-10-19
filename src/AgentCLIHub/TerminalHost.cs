using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentCLIHub;

/// <summary>
/// 대화 탭 **하나**. ConPTY ↔ xterm.js 다리다.
///
/// **탭마다 한 벌씩 있다.** 폴더도 CLI 도 탭마다 다를 수 있으므로 여기서 들고 있는다 —
/// 하나를 공유하면 탭을 바꿀 때마다 폴더가 흔들리고, 그 흔들림은 "엉뚱한 폴더에서
/// 일을 시켰다" 로만 드러난다(그때는 이미 파일이 고쳐진 뒤다).
///
/// 어느 CLI 를 띄우는지는 <see cref="CliProfile"/> 이 정한다. **여기 있는 것은 CLI 종류와
/// 무관한 부분뿐이다** — ConPTY 로 띄우고, 대괄호 붙여넣기로 한 턴을 보내고, 출력을 모아 올린다.
///
/// 여기서 뜨는 CLI 는 **온전한 대화형 세션**이다 — 슬래시 명령·플랜 모드·승인 프롬프트가
/// 다 살아 있고, 작업 폴더의 지침 파일·스킬·MCP 도 평소처럼 붙는다. 우리는 아무것도 가리지 않는다.
/// </summary>
public sealed class TerminalHost : IDisposable
{
    /// <summary>탭 번호. 화면으로 나가는 **모든** 메시지에 붙는다 — 안 붙이면 옆 탭에 그려진다.</summary>
    public string Id { get; }

    /// <summary>이 탭의 작업 폴더. 열 때 정해지고 바뀌지 않는다(폴더가 바뀌면 새 탭이다).</summary>
    public string Cwd { get; }

    /// <summary>이 탭이 띄우는 CLI. 설정을 나중에 바꿔도 이 탭은 처음 고른 것으로 계속 돈다.</summary>
    public CliProfile Cli { get; }

    /// <summary>
    /// 이 탭의 계정(<see cref="Account.Id"/>, 빈 값 = 기본 계정). 계정을 바꾸면 탭은 그대로 두고
    /// **이 호스트를 새 것으로 갈아 끼운다**(<see cref="MainWindow"/> 의 SwitchTab) — 돌고 있는 프로세스의
    /// 로그인은 바꿀 수 없다.
    /// </summary>
    public string Account { get; }

    /// <summary>codex 가 쓸 홈. 계정마다 다르다 — 기록도 그 홈에 쌓인다. claude 에서는 기본 홈 그대로다.</summary>
    public string CodexHome => Accounts.CodexHomeOf(Account);

    /// <summary>claude 계정의 설정 폴더(<c>CLAUDE_CONFIG_DIR</c>). 기본 계정이면 기본 폴더다.</summary>
    public string ClaudeHome => Accounts.ClaudeHomeOf(Account);

    /// <summary>CLI 가 뜬 시각. 그 뒤에 쓰인 기록이 이 탭의 대화다(<see cref="SessionFinder"/>).</summary>
    public DateTime StartedUtc { get; private set; } = DateTime.MaxValue;

    /// <summary>
    /// 띄울 때 알고 있던 대화 번호 — 새 claude 대화에 준 번호, 이어하기로 고른 번호, 복원 때 그 폴더의
    /// 마지막 대화. 모르면 null(codex 새 대화는 첫 턴이 끝나야 번호가 생긴다).
    /// </summary>
    public string? KnownSessionId { get; private set; }
    public string? HandoffPath { get; init; }

    private readonly Action<object> _toUi;
    private readonly Action<string, string> _log;

    private ConPty? _pty;

    /// <summary>
    /// 로그인 탭인가. 대화 대신 <see cref="CliProfile.LoginCommandLine"/> 을 띄우고, 끝나면 알린다.
    /// 설정에 탭으로 남기지 않는다 — 되살려도 할 일이 없다.
    /// </summary>
    public bool IsLogin { get; }

    /// <summary>로그인 탭이 띄우는 것 — 일반 로그인 · SSO · Console · setup-token · codex login.</summary>
    private readonly LoginKind _loginKind;

    /// <summary>claude 로그인 탭에서 토큰을 읽어 냈을 때. 한 번만 부른다.</summary>
    private readonly Action<string>? _onToken;

    /// <summary>로그인 탭의 CLI 가 끝났을 때(종료 코드).</summary>
    private readonly Action<int>? _onLoginExit;

    /// <summary>
    /// 이 호스트가 물러났다. **그 뒤로는 화면에 아무것도 안 보낸다** — 계정을 바꿀 때 같은 탭 번호로 새
    /// 호스트가 서는데, 옛 프로세스가 늦게 내는 "끝났습니다" 가 새 탭을 죽은 것으로 칠하면 안 된다.
    /// </summary>
    private volatile bool _retired;

    /// <summary>자식이 마지막으로 무언가 그린 시각. 제출 시점을 잡는 데 쓴다.</summary>
    private DateTime _lastOut = DateTime.UtcNow;

    /// <summary>
    /// 출력 조립 버퍼. **UTF-8 을 읽기 경계에서 자르면 안 된다** — 한글은 3바이트라
    /// 8KB 경계에 흔히 걸린다. Decoder 가 잘린 바이트를 다음 호출까지 들고 있는다.
    /// </summary>
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();

    public TerminalHost(string id, string cwd, CliProfile cli, string account,
                        Action<object> toUi, Action<string, string> log)
    {
        Id = id; Cwd = cwd; Cli = cli; Account = account ?? "";
        _toUi = toUi;
        _log = log;
    }

    /// <summary>
    /// 계정 로그인 탭. 일반 로그인(<c>claude auth login</c> · <c>codex login</c>)은 **그 계정 폴더 안에서** 하므로
    /// CLI 가 스스로 거기 적는다 — 끝나기만 기다린다. <c>setup-token</c> 은 화면에 찍는 토큰을 읽어
    /// <paramref name="onToken"/> 으로 넘긴다(<see cref="ExtractSetupToken"/>).
    /// </summary>
    public TerminalHost(string id, string cwd, CliProfile cli, string account, LoginKind kind,
                        Action<object> toUi, Action<string, string> log,
                        Action<string>? onToken, Action<int>? onLoginExit)
        : this(id, cwd, cli, account, toUi, log)
    {
        IsLogin = true;
        _loginKind = kind;
        _onToken = onToken;
        _onLoginExit = onLoginExit;
    }

    public bool IsRunning => _pty?.IsRunning == true;

    /// <summary>화면에서 온 터미널 메시지. 처리했으면 true.</summary>
    public bool Handle(string type, JsonNode m)
    {
        switch (type)
        {
            case "term.start":
                Start((short)(m["cols"]?.GetValue<int>() ?? 80),
                      (short)(m["rows"]?.GetValue<int>() ?? 24),
                      Mode(m));
                return true;

            case "term.in":
                _pty?.Write(m["data"]?.GetValue<string>() ?? "");
                return true;

            // 하단 입력창에서 온 한 턴. 키 입력(term.in)과 다르다 —
            // 여러 줄일 수 있고 첨부가 붙는다.
            case "term.send":
                SendTurn(m["text"]?.GetValue<string>() ?? "", m["attachments"] as JsonArray);
                return true;

            case "term.resize":
                _pty?.Resize((short)(m["cols"]?.GetValue<int>() ?? 80),
                             (short)(m["rows"]?.GetValue<int>() ?? 24));
                return true;

            case "term.stop":
                Stop("사용자가 종료했습니다.");
                return true;
        }
        return false;
    }

    private static ResumeMode Mode(JsonNode m)
    {
        if (m["sessionId"]?.GetValue<string>() is { Length: > 0 } sid) return ResumeMode.Session(sid);
        return (m["fresh"]?.GetValue<bool>() ?? false) ? ResumeMode.New : ResumeMode.Last;
    }

    /// <summary>
    /// 대화 한 턴을 CLI 에 보낸다. **두 CLI 가 같은 경로를 쓴다** —
    /// claude 도 codex 도 TUI 라 대괄호 붙여넣기와 CR 을 똑같이 받는다.
    ///
    /// **줄바꿈을 그냥 보내면 안 된다.** 각 줄이 Enter 로 읽혀 문장이 토막난 채 여러 번
    /// 제출된다. 첨부 목록만 붙어도 여러 줄이 되므로 거의 항상 걸린다.
    /// 그래서 대괄호 붙여넣기(bracketed paste)로 감싼다 — TUI 가 `[?2004h` 로 켜 둔 그것이고,
    /// 사람이 터미널에 여러 줄을 붙여넣을 때 쓰이는 바로 그 경로다.
    ///
    /// 첨부는 stdin 으로 못 보낸다(바이트다). 파일로 떨구고 **경로를 본문에 적는다** —
    /// CLI 는 파일을 읽을 수 있으므로 그편이 오히려 온전하다.
    /// </summary>
    private void SendTurn(string text, JsonArray? attachments)
    {
        if (_pty is null || !_pty.IsRunning)
        {
            Fail($"{Cli.Display} 가 떠 있지 않습니다 — 이 탭을 닫고 [＋ 새 대화] 로 다시 여세요.");
            return;
        }

        var body = new StringBuilder(text.Replace("\r\n", "\n").Replace('\r', '\n'));

        var files = SaveAttachments(attachments);
        if (files.Count > 0)
        {
            if (body.Length > 0) body.Append('\n').Append('\n');
            body.AppendLine("[첨부 — 아래 파일을 읽고 답하라]");
            foreach (var f in files) body.Append("- ").AppendLine(f);
        }

        if (body.Length == 0) return;

        // ESC 를 소스에 날바이트(0x1B)로 박지 않는다 — 편집기·diff·복사 도구가 보이지 않는
        // 제어문자를 지우거나 바꿔치기하면 붙여넣기가 통째로 죽는데, 소스를 눈으로 봐서는 알 수가 없다.
        const string ESC = "\u001b";
        _pty.Write(ESC + "[200~" + body.ToString().TrimEnd('\n') + ESC + "[201~");

        // **제출(CR)은 붙여넣기와 같은 덩어리로 보내면 안 된다.** 실측에서 글자는
        // 입력창에 다 들어갔는데 제출이 안 돼 그대로 멈춰 있었다 — 보낸 사람 눈에는
        // "전송을 눌렀는데 아무 반응이 없다" 로만 보인다.
        //
        // 고정 지연도 안 된다. 붙여넣은 것을 TUI 가 다 소화해야 Enter 가 먹는데,
        // 그 시간이 내용에 따라 다르다 — 첨부가 붙으면 `[Image #1]` 로 승격하느라 더 걸리고,
        // 150ms 로는 모자라 그대로 멈춰 있었다.
        //
        // 그래서 **화면이 잠잠해지면 보낸다.** 자식이 그리기를 멈춘 것이 소화가 끝났다는 신호다.
        var pty = _pty;
        System.Threading.Tasks.Task.Run(async () =>
        {
            var giveUp = DateTime.UtcNow.AddSeconds(5);   // 계속 그려대도 언젠가는 보낸다
            while (DateTime.UtcNow < giveUp)
            {
                await System.Threading.Tasks.Task.Delay(60);
                if ((DateTime.UtcNow - _lastOut).TotalMilliseconds >= 300) break;
            }
            try { pty.Write("\r"); } catch { }
        });
    }

    /// <summary>
    /// 첨부를 자식이 읽을 수 있는 경로로 바꾼다.
    ///
    /// 붙여넣은 이미지는 클립보드에만 있어 경로가 없다 — 작업 폴더 안에 떨군다.
    /// **작업 폴더 안인 이유**는 자식의 CWD 가 거기라서다. 바깥에 두면 `--add-dir` 없이는
    /// 읽지 못하고, 그러면 붙였는데 못 읽는 상태가 된다.
    /// </summary>
    private List<string> SaveAttachments(JsonArray? attachments)
    {
        var outp = new List<string>();
        if (attachments is null || attachments.Count == 0) return outp;

        string? dir = null;

        foreach (var node in attachments)
        {
            if (node is not JsonObject a) continue;
            var kind = a["kind"]?.GetValue<string>() ?? "";
            var name = a["name"]?.GetValue<string>() ?? "첨부";

            // 파일은 이미 디스크에 있다 — 경로만 넘긴다. 브라우저가 읽어 base64 로 올리면
            // 큰 파일 하나에 메시지가 통째로 부풀고, 크기 제한이 두 곳에 생긴다.
            if (kind == "file")
            {
                var p = a["path"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(p)) outp.Add(p);
                continue;
            }

            var url = a["dataUrl"]?.GetValue<string>() ?? "";
            var comma = url.IndexOf(',');
            if (comma < 0) continue;

            try
            {
                if (dir is null)
                {
                    dir = System.IO.Path.Combine(Cwd, ".agentclihub-attachments");
                    System.IO.Directory.CreateDirectory(dir);
                }

                var ext = url.Contains("image/jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                var safe = string.Concat(name.Where(c => !System.IO.Path.GetInvalidFileNameChars().Contains(c)));
                var file = System.IO.Path.Combine(dir, $"{stamp}-{safe}{ext}");

                System.IO.File.WriteAllBytes(file, Convert.FromBase64String(url[(comma + 1)..]));
                outp.Add(file);
            }
            catch (Exception ex)
            {
                _log($"첨부 '{name}' 을 저장하지 못했습니다: {ex.Message}", "e");
            }
        }
        return outp;
    }

    /// <summary>
    /// 기동이 진행 중인가. **재진입을 막는다** — 켜는 데 몇 초가 걸리는 동안 사용자가
    /// 다시 누르면 이 탭에 CLI 가 두 개 뜨고, 그 둘이 같은 폴더를 서로 밟는다.
    /// </summary>
    private volatile bool _starting;

    // ── 터미널 출력 합치기 ────────────────────────────────────────────
    //
    // pty 읽기 스레드는 청크를 뱉는 대로 올린다. 호출부는 그것을 `Dispatcher.BeginInvoke`
    // 로 UI 스레드에 넘기는데, CLI 는 스피너·스트리밍 때문에 **초당 수백 청크**를 낸다.
    // 탭이 여럿이면 그것이 곱해진다 — UI 큐가 term.out 으로 가득 차 창 전체가 굼떠지고,
    // 보고 있지도 않은 탭이 보고 있는 탭의 입력 반향을 밀어낸다.
    //
    // 그래서 여기서 모았다가 **고정 주기로 한 번씩** 올린다. 수백 개의 디스패처 항목이
    // 초당 ~30개로 줄고, 터미널이 아무리 시끄러워도 창이 반응한다.
    // 화면상 차이는 없다 — xterm 은 어차피 이어 붙여 그린다.
    private readonly StringBuilder _outBuf = new();
    private readonly object _outLock = new();
    private System.Threading.Timer? _outTimer;

    /// 33ms = 30Hz. 더 늦추면 타이핑 반향이 눈에 띄게 밀리고, 더 빠르게 하면 합치는 의미가 준다.
    private const int OutFlushMs = 33;

    private void QueueOut(string s)
    {
        lock (_outLock)
        {
            _outBuf.Append(s);
            // **버퍼에 상한을 둔다.** 폭주하는 출력(예: 큰 파일 cat)을 통째로 들고 있으면
            // 메모리가 늘고, 어차피 사람이 읽을 수 없는 양이다. 오래된 쪽을 버린다.
            const int cap = 512 * 1024;
            if (_outBuf.Length > cap) _outBuf.Remove(0, _outBuf.Length - cap);
            _outTimer ??= new System.Threading.Timer(_ => FlushOut(), null, OutFlushMs, OutFlushMs);
        }
    }

    private void FlushOut()
    {
        string s;
        lock (_outLock)
        {
            if (_outBuf.Length == 0) return;
            s = _outBuf.ToString();
            _outBuf.Clear();
        }
        Send(new { t = "term.out", data = s });
    }

    /// <summary>
    /// 터미널을 연다. **준비 작업은 배경 스레드로 보낸다.**
    ///
    /// 여기서 하는 일(설정 로드 · CLI 탐색 · ConPTY 기동)은 파일시스템과 프로세스 생성을
    /// 건드려 **수 초가 걸릴 수 있다.** 그동안 UI 스레드를 잡고 있으면 창이 통째로
    /// "응답 없음" 이 된다 — 사용자에게는 제품이 죽은 것으로 보인다.
    ///
    /// 그래서 **먼저 말하고 나중에 한다**: `term.starting` 을 즉시 보내 그 탭이
    /// "켜는 중" 을 그리게 하고, 실제 준비는 배경에서 돈다.
    /// </summary>
    private void Start(short cols, short rows, ResumeMode resume)
    {
        if (IsRunning) { Send(new { t = "term.ready", pid = _pty!.ProcessId, cwd = Cwd, cli = Cli.Display, resumed = true }); return; }
        if (_starting) { Send(new { t = "term.starting", again = true, cli = Cli.Display }); return; }

        if (!ConPty.Available)
        {
            Fail("이 Windows 버전은 가짜 콘솔(ConPTY)을 지원하지 않습니다 — Windows 10 1809 이상이 필요합니다.");
            return;
        }

        _starting = true;
        Send(new { t = "term.starting", cli = Cli.Display });
        _log($"{Cli.Display} 를 켜는 중입니다 — 준비되면 터미널이 열립니다.", "i");

        // **예외를 삼키지 않는다.** 배경에서 터지면 아무 일도 안 일어난 것처럼 보이고,
        // 사용자는 "켜는 중" 에서 영원히 기다린다 — 증상이 없는 결함이 되는 자리다.
        System.Threading.Tasks.Task.Run(() =>
        {
            try { StartCore(cols, rows, resume); }
            catch (Exception ex) { Fail("터미널을 열지 못했습니다: " + ex.Message); }
            finally { _starting = false; }
        });
    }

    private void StartCore(short cols, short rows, ResumeMode resume)
    {
        // 모델·강도·권한은 **띄우는 순간의 설정**을 쓴다. 뒤에 설정을 바꿔도 이 탭은
        // 그대로 도는데, 그것이 맞다 — 돌고 있는 프로세스의 인자는 바꿀 수 없다.
        var cfg = Settings.Load();

        if (Cli.Kind == CliKind.Antigravity && HandoffPath is { Length: > 0 } handoff)
        {
            var imported = AntigravityTransfer.FindSession(Cwd, handoff);
            if (imported is not null) resume = ResumeMode.Session(imported.Id);
            else
            {
                AntigravityTransfer.ReadHandoff(handoff); // 파일을 읽을 수 없으면 빈 대화로 바꾸지 않는다.
                resume = ResumeMode.Handoff(handoff);
            }
        }

        var loc = CliLocator.Locate(Cli, Cli.CachedPath(cfg));
        if (!loc.Found)
        {
            Fail(loc.Error ?? Cli.NotFoundMessage);
            return;
        }

        // **고른 폴더에서 띄운다.** 이것이 이 앱의 요점이다 — CLI 가 그 폴더를 자기 작업
        // 공간으로 보고, 지침 파일(CLAUDE.md · AGENTS.md)·스킬·MCP 가 평소처럼 붙는다.
        try { System.IO.Directory.CreateDirectory(Cwd); } catch { }

        // 계정 폴더는 **띄울 때마다** 기본 폴더에 맞춘다 — 그 사이 기본 쪽 설정이 바뀌었을 수 있다.
        // claude 는 폴더 로그인으로 뜰 때(또는 그 로그인을 하러 갈 때)만 — 토큰으로 뜨는 탭은 기본 폴더를 그대로 쓴다.
        var claudeDir = Cli.Kind == CliKind.Claude && Account.Length > 0 &&
                        (IsLogin ? _loginKind != LoginKind.ClaudeSetupToken
                                 : Accounts.ClaudeModeOf(Account) == ClaudeMode.Login);
        if (Cli.Kind == CliKind.Codex && Account.Length > 0) Accounts.PrepareCodexHome(CodexHome, _log);
        if (claudeDir) Accounts.PrepareClaudeHome(ClaudeHome, _log);

        // 계정 환경. **로그인이 없으면 띄우지 않는다** — 기본 계정으로 슬쩍 떨어져 뜨면 사용자는 넘겼다고
        // 믿는 채로 한도가 찬 계정을 계속 쓰게 된다.
        var env = TerminalEnv(Cli);
        if (!IsLogin && !AccountEnv(env, out var why))
        {
            Fail(why);
            return;
        }
        if (IsLogin && Account.Length > 0)
        {
            // 로그인도 **그 계정 폴더 안에서** 한다 — 기본 계정의 로그인은 건드리지 않는다.
            if (Cli.Kind == CliKind.Codex) env["CODEX_HOME"] = CodexHome;
            else if (claudeDir) env["CLAUDE_CONFIG_DIR"] = ClaudeHome;
        }

        // 로그인 페이지는 **InPrivate 탭**으로 연다(도우미가 `open … --private` 로 보낸다). 앱 브라우저에 이미
        // 다른 claude.ai·Google 계정으로 로그인돼 있으면 그 계정이 그대로 승인돼, 더하려던 계정 대신
        // 엉뚱한 계정의 로그인이 이 계정 폴더에 저장된다 — 빈 세션에서 시작하면 고른 계정으로만 로그인된다.
        if (IsLogin && env.ContainsKey("BROWSER")) env[PrivateBrowserEnv] = "1";

        try
        {
            _pty = new ConPty();
            _pty.OnOutput += bytes =>
            {
                _lastOut = DateTime.UtcNow;
                // 잘린 UTF-8 을 이어 붙여 온전한 문자만 올린다.
                var chars = new char[bytes.Length];
                var n = _decoder.GetChars(bytes, 0, bytes.Length, chars, 0);
                if (n <= 0) return;
                var s = new string(chars, 0, n);
                QueueOut(s);
                if (IsLogin && _loginKind == LoginKind.ClaudeSetupToken) WatchForToken(s);
            };
            _pty.OnExit += code =>
            {
                // **남은 출력을 먼저 흘린다.** 자식이 죽으면서 낸 마지막 몇 줄에 대개
                // 이유가 적혀 있다 — 그것이 버퍼에 갇힌 채 사라지면 원인이 안 남는다.
                FlushOut();
                Send(new { t = "term.exit", code });
                if (!_retired) _log($"터미널이 끝났습니다(코드 {code}).", code == 0 ? "i" : "w");
                if (IsLogin) { try { _onLoginExit?.Invoke(code); } catch { } }
            };

            // 도구는 안 묶는다 — 사용자가 직접 모는 CLI 이고, 묶으면 이 앱을 만든 이유가 없어진다.
            // 우리가 거는 것은 **설정에서 고른 것**뿐이다.
            string line;
            if (IsLogin)
            {
                line = CliProfile.LoginCommandLine(loc.CommandHead(), _loginKind);
            }
            else
            {
                // 이 탭이 쓸 대화 번호를 **띄우기 전에** 정해 둔다. 새 claude 대화는 번호를 우리가 준다.
                string? newId = null;
                if (resume.SessionId is { Length: > 0 } sid) KnownSessionId = sid;
                else if (resume.Fresh && Cli.Kind == CliKind.Claude) KnownSessionId = newId = Guid.NewGuid().ToString();
                else if (!resume.Fresh && Cli.SupportsTransfer) KnownSessionId = SessionFinder.Latest(Cli, Cwd, CodexHome)?.Id;

                line = Cli.BuildCommandLine(loc.CommandHead(), cfg, resume, Cwd, CodexHome, newId,
                                            claudeDir ? ClaudeHome : null);
            }

            StartedUtc = DateTime.UtcNow;
            env["PATH"] = CliLocator.LaunchPath(Cli);
            line = loc.LaunchCommand(line);
            _log($"{Cli.Display} 실행 파일: {loc.Path} ({loc.Source})", "i");
            _pty.Start(line, Cwd, cols, rows, env);

            Send(new { t = "term.ready", pid = _pty.ProcessId, cwd = Cwd, cli = Cli.Display });

            var who = Account.Length > 0 ? $" · 계정 {Accounts.View(cfg, Cli, Account).Label}" : "";
            if (IsLogin)
            {
                _log($"{Cli.Display} 계정 로그인을 열었습니다{who} — 터미널의 안내를 따라 브라우저에서 로그인하세요.", "i");
            }
            else
            {
                var how = resume.SessionId is { Length: > 0 } ? "고른 대화를 이어서"
                        : resume.Fresh ? "새 대화로" : "이어서";
                _log($"{Cli.Display} 를 {how} 열었습니다{who} — {Cwd} " +
                     $"(pid {_pty.ProcessId} · {loc.Source} · 지침 파일 {Cli.MemoryFile})", "i");
                if (Cli.Kind == CliKind.Codex) HintFirstSandboxSetup(cfg);
            }
        }
        catch (Exception ex)
        {
            _pty?.Dispose();
            _pty = null;
            Fail("터미널을 열지 못했습니다: " + ex.Message);
        }
    }

    private static bool _sandboxHintShown;

    /// <summary>
    /// 이 홈의 codex 가 Windows 샌드박스를 아직 안 골랐으면 **한 번** 알린다(<see cref="AgentBrief.WindowsSandboxUnset"/>).
    ///
    /// codex(0.155~)는 폴더를 처음 믿는 순간 샌드박스 설정 창을 띄우고, 설정이 끝나면 그 대화의 권한을 자기 기본값
    /// (승인 on-request · workspace-write)으로 바꾼다 — 우리가 건 -s · -a 보다 나중이라 그 대화에서는 설정이 안 먹는다.
    /// 그 선택은 사용자 몫이라 막지 않고, 무슨 일이 일어났고 어떻게 되돌리는지만 적는다.
    /// 설정이 codex 기본값과 같으면 바뀌는 것이 없으므로 말하지 않는다.
    /// </summary>
    private void HintFirstSandboxSetup(Settings cfg)
    {
        if (_sandboxHintShown) return;
        var sandbox = CliCatalog.ClampPermission("CodexSandbox", cfg.CodexSandbox);
        var approval = CliCatalog.ClampPermission("CodexApproval", cfg.CodexApproval);
        if ((sandbox is "" or "workspace-write") && (approval is "" or "on-request")) return;
        if (!AgentBrief.WindowsSandboxUnset(CodexHome)) return;

        _sandboxHintShown = true;
        _log("이 PC 의 codex 는 아직 Windows 샌드박스를 고르지 않았습니다. 폴더를 믿는다고 답하면 codex 가 샌드박스 설정 창을 띄우고, " +
             "설정이 끝나면 그 대화만 권한을 codex 기본값(승인 on-request · workspace-write)으로 바꿉니다. " +
             $"설정의 값(샌드박스 {(sandbox.Length > 0 ? sandbox : "기본")} · 승인 {(approval.Length > 0 ? approval : "기본")})으로 쓰려면 " +
             "그 탭을 닫고 다시 여세요 — 고른 샌드박스는 기억되어 다음부터는 묻지 않습니다.", "w");
    }

    public void Stop(string why) => Stop(why, retire: false);

    /// <param name="retire">
    /// 이 호스트가 물러나는가(계정을 바꿔 같은 탭 번호로 새 호스트가 선다). 참이면 끝났다는 말도 안 보낸다 —
    /// 화면은 그 탭을 곧바로 새 CLI 로 다시 그린다.
    /// </param>
    public void Stop(string why, bool retire)
    {
        // **남은 출력을 먼저 흘린다.** 타이머를 먼저 죽이면 마지막 몇 줄(대개 오류 메시지가
        // 거기 있다)이 버퍼에 갇힌 채 사라지고, 화면에는 아무 이유 없이 끝난 것으로 보인다.
        try { FlushOut(); } catch { }
        if (retire) _retired = true;
        try { _outTimer?.Dispose(); } catch { }
        _outTimer = null;

        if (_pty is null) return;
        try { _pty.Dispose(); } catch { }
        _pty = null;
        Send(new { t = "term.exit", code = 0, why });
        _log("터미널을 닫았습니다 — " + why, "i");
    }

    // ── 계정 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 이 탭의 계정으로 띄우게 환경을 고친다. 기본 계정이면 손대지 않는다(CLI 가 원래 로그인으로 뜬다).
    ///
    ///   claude — 계정 폴더에 일반 로그인이 있으면 <c>CLAUDE_CONFIG_DIR</c> 을 그 폴더로(대화 기록·설정은 연결로
    ///            같이 쓴다). 없고 토큰이 있으면 <c>CLAUDE_CODE_OAUTH_TOKEN</c> 으로(저장된 로그인보다 앞선다 — 실측).
    ///            <b>둘 다 없으면 띄우지 않는다</b> — 그대로 띄우면 기본 계정으로 조용히 떨어진다.
    ///   codex  — <c>CODEX_HOME</c> 을 그 계정 홈으로. 로그인이 없으면 codex 가 화면에서 먼저 묻는다.
    /// </summary>
    private bool AccountEnv(Dictionary<string, string?> env, out string why)
    {
        why = "";
        if (Account.Length == 0) return true;
        if (!Cli.SupportsAccounts)
        {
            why = "Antigravity 는 CLI 에 로그인한 계정을 사용합니다. 기본 계정으로 탭을 여세요.";
            return false;
        }

        if (Cli.Kind == CliKind.Claude)
        {
            switch (Accounts.ClaudeModeOf(Account))
            {
                case ClaudeMode.Login:
                    env["CLAUDE_CONFIG_DIR"] = ClaudeHome;
                    return true;

                case ClaudeMode.Token when Accounts.ClaudeToken(Account) is { } token:
                    env["CLAUDE_CODE_OAUTH_TOKEN"] = token;
                    return true;

                default:
                    why = "이 탭의 Claude 계정이 로그인돼 있지 않습니다 — [⚙ 설정] → [계정] 에서 그 계정의 [로그인] 을 먼저 하세요. " +
                          "(기본 계정으로 바꿔 띄우지는 않습니다 — 한도가 찬 계정으로 조용히 돌아가게 됩니다.)";
                    return false;
            }
        }

        env["CODEX_HOME"] = CodexHome;
        return true;
    }

    /// <summary>로그인 탭 출력의 누적(ANSI 를 걷어 낸 것). 토큰을 찾을 때까지만 모은다.</summary>
    private readonly StringBuilder _loginText = new();
    private bool _tokenFound;

    private void WatchForToken(string chunk)
    {
        if (_tokenFound) return;
        string? token;
        lock (_loginText)
        {
            _loginText.Append(chunk);
            // 토큰 화면은 끝에 온다. 앞쪽(브라우저 안내)은 버려도 된다.
            if (_loginText.Length > 256 * 1024) _loginText.Remove(0, _loginText.Length - 128 * 1024);
            token = ExtractSetupToken(_loginText.ToString());
        }
        if (token is null) return;
        _tokenFound = true;
        try { _onToken?.Invoke(token); } catch { }
    }

    private static readonly Regex AnsiOsc = new("\\][^]*(|\\\\)", RegexOptions.Compiled);
    private static readonly Regex AnsiCsi = new("\\[[0-9;?<>=]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex AnsiEsc = new("[()][0-9A-Za-z]|[=>78DEHMcNO]", RegexOptions.Compiled);

    /// <summary>
    /// <c>claude setup-token</c> 화면에서 토큰을 읽어 낸다. 화면은 이렇다:
    /// <code>
    ///   Your OAuth token (valid for 1 year):
    ///   sk-ant-oat01-……
    ///   Store this token securely. You won't be able to see it again.
    /// </code>
    /// 긴 토큰은 **화면 폭에서 접혀** 여러 줄로 온다(TUI 가 줄바꿈을 직접 넣는다). 그래서 한 줄을 찾지 않고,
    /// 마지막 "Your OAuth token" 과 "Store this token" **사이**에서 <c>sk-ant-</c> 부터 공백·테두리를 걷어 잇는다.
    /// 커서 이동 같은 제어열은 줄바꿈으로 바꿔 걷는다 — 글자 사이에 끼어 있어도 토큰이 이어진다.
    /// 모양이 안 맞으면 null — 사용자가 설정에서 직접 붙여 넣는 길이 남아 있다.
    /// </summary>
    internal static string? ExtractSetupToken(string raw)
    {
        var s = AnsiOsc.Replace(raw, "");
        s = AnsiCsi.Replace(s, "\n");
        s = AnsiEsc.Replace(s, "");

        var a = s.LastIndexOf("Your OAuth token", StringComparison.Ordinal);
        if (a < 0) return null;
        var b = s.IndexOf("Store this token", a, StringComparison.Ordinal);
        if (b < 0) return null;
        var k = s.IndexOf("sk-ant-", a, b - a, StringComparison.Ordinal);
        if (k < 0) return null;

        var sb = new StringBuilder();
        foreach (var c in s.AsSpan(k, b - k))
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
            else if (char.IsWhiteSpace(c) || c is '│' or '|' or '║' or '┃') continue;   // 줄 접힘·테두리
            else break;                                                                // 다른 글이 시작됐다
        }
        var token = sb.ToString();
        return Accounts.LooksLikeClaudeToken(token) ? token : null;
    }

    /// <summary>
    /// 자식에게 줄 환경. 이것이 없으면 TUI 는 자기가 어떤 단말에 붙었는지 몰라
    /// 색·하이퍼링크를 낮춰 잡는다.
    ///
    /// 지우는 쪽도 중요하다. 이 앱을 CLI 터미널 안에서 띄우면 부모의 세션 표식이
    /// 그대로 상속돼 자식이 "이력 저장 꺼짐"으로 뜨거나, 아예 비대화형으로 죽는다.
    /// 여기서 여는 것은 **사용자의 독립된 세션**이므로 표식을 끊는다.
    /// 무엇을 끊을지는 CLI 마다 다르다 — <see cref="CliProfile.StripEnvPrefixes"/>.
    /// </summary>
    /// <summary>
    /// 창구 이름. <see cref="MainWindow"/> 가 창구를 연 뒤 채운다. 비어 있으면
    /// 자식에게 안 심는다 — 없는 창구를 가리키는 변수는 도우미를 헛돌게 만든다.
    /// </summary>
    public static string BrowserPipeName { get; set; } = "";

    /// <summary>
    /// 창구가 요청마다 보는 표(<see cref="BrowserBridge.Ticket"/>). 창구 이름과 같이 채워진다.
    /// **이 앱이 띄운 터미널의 자식만** 물려받는다 — 그것이 창구를 쓸 수 있는 곳의 경계다.
    /// </summary>
    public static string BrowserTicket { get; set; } = "";

    /// <summary>
    /// 로그인 탭에만 심는 표시. 도우미(agentclihub-browser)가 BROWSER 로 불려 주소를 열 때 이것이 있으면
    /// InPrivate 탭으로 열어 달라고 한다(<c>open &lt;주소&gt; --private</c>).
    /// </summary>
    public const string PrivateBrowserEnv = "AGENTCLIHUB_BROWSER_PRIVATE";

    private static Dictionary<string, string?> TerminalEnv(CliProfile cli)
    {
        var ver = typeof(TerminalHost).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["TERM_PROGRAM"] = "AgentCLIHub",
            ["TERM_PROGRAM_VERSION"] = ver,
            ["FORCE_HYPERLINK"] = "1",

            // 색을 **명시적으로 켠다.** TERM·COLORTERM 만으로는 부족하다 —
            // 이 앱을 터미널에서 띄우면 그 셸의 NO_COLOR 가 그대로 상속돼 자식까지 흑백이 된다.
            // 실측에서 정확히 그랬다: SGR 시퀀스가 한 개도 안 나왔다.
            // 여기 터미널은 xterm.js 라 색을 다 그릴 수 있으므로 끌 이유가 없다.
            ["NO_COLOR"] = null,        // null = 지운다
            ["FORCE_COLOR"] = "3",      // 3 = truecolor
        };

        // 브라우저 창구. 도우미(agentclihub-browser)가 이 이름으로 우리를 찾는다.
        // **이 앱이 띄운 터미널의 자식만** 물려받으므로, 밖에서 실행한 CLI 는 못 쓴다.
        if (BrowserPipeName.Length > 0)
        {
            env["AGENTCLIHUB_BROWSER_PIPE"] = BrowserPipeName;
            env[BrowserBridge.TicketEnv] = BrowserTicket;
            env["AGENTCLIHUB_BROWSER_CLI"] = BrowserBridge.HelperPath;

            // **로그인 페이지를 이 앱 안에서 받는다.**
            //
            // CLI 는 로그인 승인 페이지를 시스템 기본 브라우저로 연다(Windows 에서는
            // `rundll32 url,OpenURL`). 그러면 승인은 그 브라우저에서 끝나고, 이 앱의
            // 브라우저 탭은 그 로그인을 모른 채로 남는다 — 같은 계정을 두 군데서 따로
            // 들고 있게 되고, 이 앱 안에서 열어 둔 Jira·GitLab 과도 세션이 갈린다.
            //
            // BROWSER 는 "<명령> <주소>" 규약이라 exe 경로만 넣으면 된다. 도우미는 주소
            // 하나만 받으면 `open` 으로 보고 우리 창구에 넘긴다(AgentCLIHub.Cli.Program 참조).
            //
            // **되돌릴 길을 남긴다.** 이 앱 밖에서 쓰던 값이 있으면 CLI 가 그것을 알아야
            // 하는 경우가 있어, 원래 값은 AGENTCLIHUB_OUTER_BROWSER 로 넘겨 둔다.
            var outer = Environment.GetEnvironmentVariable("BROWSER");
            if (!string.IsNullOrEmpty(outer)) env["AGENTCLIHUB_OUTER_BROWSER"] = outer;
            env["BROWSER"] = BrowserBridge.HelperPath;
        }
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var k = e.Key?.ToString();
            if (k is null) continue;
            foreach (var prefix in cli.StripEnvPrefixes)
                if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    env[k] = null;   // null = 지운다
        }
        return env;
    }

    /// <summary>
    /// 화면으로 보낸다. **탭 번호를 반드시 붙인다** — 안 붙이면 여러 탭이 같은 xterm 에
    /// 그려져 화면이 뒤섞이고, 그때는 어느 CLI 가 무엇을 말했는지 알 수가 없다.
    /// 붙이는 자리를 한 곳으로 모아 두어 빠뜨릴 수 없게 했다.
    /// </summary>
    private void Send(object payload)
    {
        if (_retired) return;   // 물러난 호스트 — 같은 탭 번호의 새 호스트 화면을 건드리지 않는다
        var node = System.Text.Json.JsonSerializer.SerializeToNode(payload) as JsonObject;
        if (node is null) return;
        node["id"] = Id;
        _toUi(node);
    }

    private void Fail(string msg)
    {
        Send(new { t = "term.error", msg });
        _log(msg, "e");
    }

    public void Dispose() => Stop("탭을 닫았습니다.");
}
