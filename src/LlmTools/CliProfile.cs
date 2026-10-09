using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LlmTools;

/// <summary>터미널에 띄울 수 있는 CLI 종류.</summary>
public enum CliKind { Claude, Codex, Antigravity }

/// <summary>계정 로그인 탭이 띄우는 것(<see cref="CliProfile.LoginCommandLine"/>).</summary>
public enum LoginKind
{
    /// <summary><c>claude auth login</c> — claude.ai 구독(Pro · Max · Team · Enterprise).</summary>
    Claude,

    /// <summary><c>claude auth login --sso</c> — 회사 SSO 로 강제.</summary>
    ClaudeSso,

    /// <summary><c>claude auth login --console</c> — Anthropic Console(API 과금).</summary>
    ClaudeConsole,

    /// <summary><c>claude setup-token</c> — 1년짜리 추론 전용 토큰을 화면에 찍는다.</summary>
    ClaudeSetupToken,

    /// <summary><c>codex login</c>.</summary>
    Codex,
}

/// <summary>
/// 대화를 어디서부터 시작할까. 세 갈래뿐이고 **셋을 섞으면 안 된다** —
/// 예전에 bool 하나(fresh)로 두었더니 "세션 번호를 줬는데 fresh 라 무시된다" 같은
/// 조합이 표현 가능해져서, 화면에서는 고른 대화가 안 열리는 것으로만 보였다.
/// </summary>
/// <param name="Fresh">처음부터. [＋ 새 대화] 가 이것이다.</param>
/// <param name="SessionId">
/// 콕 집은 대화 하나. [↻ 이어하기] 목록에서 고른 것이 여기로 온다.
/// 비어 있고 <paramref name="Fresh"/> 도 아니면 "그 폴더의 마지막 대화" 를 뜻한다.
/// </param>
public readonly record struct ResumeMode(bool Fresh, string? SessionId, string? HandoffPath = null)
{
    /// <summary>처음부터 시작한다.</summary>
    public static readonly ResumeMode New = new(true, null);

    /// <summary>그 폴더에서 마지막으로 하던 대화를 이어간다.</summary>
    public static readonly ResumeMode Last = new(false, null);

    /// <summary>번호로 콕 집은 대화를 이어간다.</summary>
    public static ResumeMode Session(string id) => new(false, id);
    public static ResumeMode Handoff(string path) => new(true, null, path);
}

/// <summary>
/// 띄울 CLI 한 종류. **CLI 마다 다른 것만 여기 모은다** —
/// 탐지 경로 · 커맨드라인 플래그 · 지침 파일명 · 끊어야 할 부모 환경변수.
///
/// 왜 갈래를 한곳에 모으는가: <see cref="TerminalHost"/> 가 하는 일은 CLI 종류와 무관하다.
/// ConPTY 에 띄우고, 붙여넣기로 한 턴을 보내고, 출력을 화면으로 올린다.
/// 위 네 가지만 다르므로 호스트 여기저기에 `if (claude)` 를 뿌리는 대신 프로파일 하나를 넘긴다 —
/// 세 번째 CLI 가 붙어도 고칠 곳이 이 파일뿐이다.
/// </summary>
public sealed class CliProfile
{
    /// <summary>claude CLI 를 대화형으로 띄운다. 슬래시 명령·플랜 모드·승인 프롬프트가 다 산다.</summary>
    public static readonly CliProfile Claude = new(CliKind.Claude, "claude_code", "Claude Code", "CLAUDE.md");

    /// <summary>OpenAI Codex CLI 를 대화형으로 띄운다. 공식 설치본과 npm 설치본을 모두 찾는다.</summary>
    public static readonly CliProfile Codex = new(CliKind.Codex, "codex_cli", "Codex", "AGENTS.md");

    /// <summary>모든 CLI. 설정 화면 목록과 검증이 같은 곳을 본다.</summary>
    public static readonly CliProfile Antigravity = new(CliKind.Antigravity, "antigravity_cli", "Antigravity", "AGENTS.md / GEMINI.md");

    public static readonly CliProfile[] All = [Claude, Codex, Antigravity];

    // Antigravity 는 CLI 의 로그인을 쓴다. 대화 인계는 지원하며 별도 계정 격리는 하지 않는다.
    public bool SupportsAccounts => Kind != CliKind.Antigravity;
    public bool SupportsTransfer => true;
    public string CommandName => Kind switch { CliKind.Claude => "claude", CliKind.Codex => "codex", _ => "agy" };
    public string PathKey => Kind switch
    {
        CliKind.Claude => nameof(Settings.ClaudeCliPath),
        CliKind.Codex => nameof(Settings.CodexCliPath),
        _ => nameof(Settings.AntigravityCliPath),
    };

    public CliKind Kind { get; }

    /// <summary>설정 파일에 저장되는 값.</summary>
    public string Backend { get; }

    /// <summary>사람에게 보여 줄 이름.</summary>
    public string Display { get; }

    /// <summary>
    /// 이 CLI 가 작업 폴더에서 자동으로 읽는 지침 파일.
    /// **파일명이 다르다** — claude 는 CLAUDE.md, codex 는 AGENTS.md 다.
    /// 우리가 쓰지는 않지만 어느 파일이 붙는지는 사용자가 알아야 한다 —
    /// 폴더를 바꿨는데 에이전트가 딴소리를 하면 대개 그 폴더에 이 파일이 없는 것이다.
    /// </summary>
    public string MemoryFile { get; }

    private CliProfile(CliKind kind, string backend, string display, string memoryFile)
    {
        Kind = kind; Backend = backend; Display = display; MemoryFile = memoryFile;
    }

    /// <summary>백엔드 값 → 프로파일. 모르는 값이면 <c>null</c>.</summary>
    public static CliProfile? For(string? backend) => (backend ?? "").Trim().ToLowerInvariant() switch
    {
        "claude_code" => Claude,
        "codex_cli" => Codex,
        "antigravity_cli" => Antigravity,
        _ => null,
    };

    /// <summary>모르는 값이면 claude 로 본다 — 이름이 하나 틀리는 것이 아무 말도 안 하는 것보다 낫다.</summary>
    public static CliProfile ForOrDefault(string? backend) => For(backend) ?? Claude;

    // ── 탐지 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// PATH 에서 찾을 실행 파일 이름. **순서에 뜻이 있다.**
    ///   claude — `.cmd` 를 먼저 본다. 그 안에서 node + cli.js 를 뽑아내면 cmd.exe 를 통째로 우회한다.
    ///   codex  — `.exe` 를 먼저 본다. 네이티브 실행 파일이라 래퍼를 거칠 이유가 없다
    ///            (npm 설치본의 `codex.cmd` 도 결국 같은 바이너리를 부른다).
    /// </summary>
    public string[] Exes => Kind switch
    {
        CliKind.Claude => ["claude.cmd", "claude.exe", "claude.ps1", "claude.bat"],
        CliKind.Antigravity => ["agy.exe", "agy.cmd", "agy.ps1", "agy.bat"],
        _ => ["codex.exe", "codex.cmd", "codex.ps1", "codex.bat"],
    };

    /// <summary>PATH 에 없을 때 훑을 관례 설치 경로.</summary>
    public IEnumerable<(string path, string src)> InstallCandidates()
    {
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (Kind == CliKind.Antigravity)
        {
            yield return (Path.Combine(local, "agy", "bin", "agy.exe"), "%LOCALAPPDATA%\\agy\\bin");
            yield return (Path.Combine(user, ".local", "bin", "agy.exe"), "%USERPROFILE%\\.local\\bin");
            yield break;
        }

        if (Kind == CliKind.Claude)
        {
            yield return (Path.Combine(appdata, "npm", "claude.cmd"), "%APPDATA%\\npm");
            yield return (Path.Combine(local, "Programs", "claude", "claude.exe"), "%LOCALAPPDATA%\\Programs");
            yield return (Path.Combine(user, ".claude", "local", "claude.cmd"), "%USERPROFILE%\\.claude\\local");
            yield return (Path.Combine(user, ".local", "bin", "claude.exe"), "%USERPROFILE%\\.local\\bin");
            yield return (@"C:\Program Files\nodejs\claude.cmd", "Program Files\\nodejs");
            yield break;
        }

        // 공식 설치 관리자가 놓는 자리가 먼저다 — npm 래퍼보다 이쪽이 최신인 경우가 많다.
        yield return (Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe"),
                      "%LOCALAPPDATA%\\Programs\\OpenAI\\Codex");
        foreach (var home in new[] { Environment.GetEnvironmentVariable("CODEX_HOME"), Path.Combine(user, ".codex") }
                     .Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var candidate in CodexStandaloneCandidates(home!)) yield return candidate;
        yield return (Path.Combine(appdata, "npm", "codex.cmd"), "%APPDATA%\\npm");
        yield return (Path.Combine(user, ".codex", "bin", "codex.exe"), "%USERPROFILE%\\.codex\\bin");
        yield return (Path.Combine(user, ".local", "bin", "codex.exe"), "%USERPROFILE%\\.local\\bin");
        yield return (Path.Combine(user, ".cargo", "bin", "codex.exe"), "%USERPROFILE%\\.cargo\\bin");
        yield return (Path.Combine(user, "scoop", "shims", "codex.exe"), "Scoop");
        yield return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "codex.cmd"), "Program Files\\nodejs");
    }

    private static IEnumerable<(string path, string src)> CodexStandaloneCandidates(string home)
    {
        var standalone = Path.Combine(home, "packages", "standalone");
        yield return (Path.Combine(standalone, "current", "bin", "codex.exe"), "Codex standalone/current");

        // 설치/업데이트 도중 PATH 쪽 junction 이 없어져도 설치 관리자가 기록한 버전을 찾는다.
        // 버전 폴더를 고정 저장하거나 임의의 구버전을 골라 실행하지 않는다.
        string? version = null;
        try
        {
            var marker = Path.Combine(standalone, "auto-update-version");
            if (new FileInfo(marker).Length <= 256) version = File.ReadAllText(marker).Trim();
        }
        catch { }
        if (version is { Length: > 0 } && version != "." && version != ".." &&
            version.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && Path.GetFileName(version) == version)
            yield return (Path.Combine(standalone, "releases", version, "bin", "codex.exe"), "Codex standalone/auto-update-version");
    }

    /// <summary>
    /// npm 래퍼 안에 숨은 JS 진입점(관례 경로). node 를 직접 부르면 cmd.exe 를 우회한다.
    /// Codex npm 설치도 JS 가 네이티브 실행 파일을 찾아 띄우는 구조다.
    /// </summary>
    public string? NpmCliJs => Kind switch
    {
        CliKind.Claude => Path.Combine("node_modules", "@anthropic-ai", "claude-code", "cli.js"),
        CliKind.Codex => Path.Combine("node_modules", "@openai", "codex", "bin", "codex.js"),
        _ => null,
    };

    /// <summary>못 찾았을 때 사람에게 보여 줄 이유.</summary>
    public string NotFoundMessage => Kind switch
    {
        CliKind.Claude => "claude CLI 를 찾지 못했습니다. 설치 여부를 확인하거나 설정에서 경로를 지정하세요.",
        CliKind.Antigravity => "Antigravity CLI(agy)를 찾지 못했습니다. CLI 설치 여부를 확인하거나 설정에서 agy.exe 경로를 지정하세요.",
        _ => "codex CLI 를 찾지 못했습니다. 설치 여부를 확인하거나 설정에서 경로를 지정하세요.",
    };

    /// <summary>설정에 적힌 경로 지정을 읽는다.</summary>
    public string CachedPath(Settings s) => Kind switch
    {
        CliKind.Claude => s.ClaudeCliPath,
        CliKind.Codex => s.CodexCliPath,
        _ => s.AntigravityCliPath,
    };

    // ── 설정 칸 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 이 CLI 의 모델·추론 강도가 담기는 설정 칸 이름. **CLI 마다 따로다**(<see cref="Settings.ClaudeModel"/> 참조).
    /// 화면은 이 이름으로 읽고 쓴다 — 칸 이름을 화면에 따로 적어 두면 한쪽만 바뀐 날 저장이 헛돈다.
    /// </summary>
    public string ModelKey => Kind switch { CliKind.Claude => nameof(Settings.ClaudeModel), CliKind.Codex => nameof(Settings.CodexModel), _ => nameof(Settings.AntigravityModel) };

    /// <inheritdoc cref="ModelKey"/>
    public string EffortKey => Kind switch { CliKind.Claude => nameof(Settings.ClaudeEffort), CliKind.Codex => nameof(Settings.CodexEffort), _ => nameof(Settings.AntigravityEffort) };

    /// <summary>설정에서 이 CLI 에 걸 모델. 빈 값 = CLI 기본 모델.</summary>
    public string ModelOf(Settings s) => Kind switch { CliKind.Claude => s.ClaudeModel, CliKind.Codex => s.CodexModel, _ => s.AntigravityModel };

    /// <summary>설정에서 이 CLI 에 걸 추론 강도. 빈 값 = CLI 기본값.</summary>
    public string EffortOf(Settings s) => Kind switch { CliKind.Claude => s.ClaudeEffort, CliKind.Codex => s.CodexEffort, _ => s.AntigravityEffort };

    /// <summary>
    /// 이 CLI 를 띄울 때 설정에서 걸리는 것 — (이름, 값). 빈 값은 안 걸리므로(= CLI 기본값) 빠진다.
    ///
    /// [＋ 새 대화] 의 고르기 창이 "이것으로 뜬다" 를 보여 줄 때 쓴다. **커맨드라인이 읽는 칸과
    /// 같은 칸을 읽는다**(<see cref="ClaudeLine"/> · <see cref="CodexLine"/>) — 따로 꾸미면
    /// 화면에 적힌 것과 실제로 걸리는 플래그가 갈라진다.
    /// </summary>
    public IReadOnlyList<(string Label, string Value)> LaunchOptions(Settings s)
    {
        var o = new List<(string Label, string Value)>();
        void Add(string label, string? v)
        {
            if (!string.IsNullOrWhiteSpace(v)) o.Add((label, v.Trim()));
        }

        Add("모델", ModelOf(s));
        Add("추론 강도", EffortOf(s));
        if (Kind == CliKind.Claude)
        {
            Add("권한", Perm("ClaudeCodePermissionMode", s.ClaudeCodePermissionMode));
        }
        else if (Kind == CliKind.Codex)
        {
            Add("샌드박스", Perm("CodexSandbox", s.CodexSandbox));
            Add("승인", Perm("CodexApproval", s.CodexApproval));
        }
        else Add("권한", Perm(nameof(Settings.AntigravityPermission), s.AntigravityPermission));
        return o;
    }

    /// <summary>
    /// 권한 값을 **띄우는 순간에도** 거른다. 저장할 때 이미 거르지만(MainWindow.SaveConf),
    /// 그 문을 안 지난 값이 있다 — 손으로 고친 파일, 그리고 **목록에서 빠진 옛 값**
    /// (codex 가 `-a untrusted` 를 받지 않게 된 뒤에도 전에 저장해 둔 것). 그대로 내보내면 CLI 가
    /// 인자를 거절해 탭이 뜨자마자 죽는데, 설정 화면은 목록에 없는 값을 "(CLI 기본값)" 으로 보여
    /// 주므로 사용자는 까닭을 찾을 수가 없다. 걸러 내면 화면에 보이는 것과 실제로 뜨는 것이 같아진다.
    /// </summary>
    private static string Perm(string key, string? value) => CliCatalog.ClampPermission(key, value);

    // ── 환경 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 자식에게서 **끊어야 할** 부모 환경변수 접두사.
    ///
    /// claude — `CLAUDE_CODE_*` 와 **밑줄 없는 `CLAUDECODE`**.
    ///          앞의 것은 자식 세션 표식이라 상속되면 "이력 저장 꺼짐" 으로 뜬다.
    ///          뒤의 것이 더 고약하다 — 실측에서 이 앱을 Claude Code 안에서 띄웠더니
    ///          자식이 자기를 비대화형으로 보고 TUI 를 아예 안 그린 채 죽었다:
    ///              Error: Input must be provided either through stdin or as a prompt
    ///                     argument when using --print
    ///          화면에는 "터미널이 켜졌다가 곧바로 닫힌다" 로만 보여 원인이 안 드러난다.
    ///          **밑줄 있는 이름만 끊으면 이 하나가 그대로 새어 나간다** — 접두사가 다르다.
    ///
    ///          `CLAUDE_` 를 통째로 끊지는 않는다. `CLAUDE_CONFIG_DIR` 처럼 사용자가 일부러
    ///          옮겨 둔 설정이 거기 섞여 있고, 그것까지 지우면 로그인이 사라진다
    ///          (codex 의 `CODEX_HOME` 을 남기는 것과 같은 이유다).
    ///
    /// codex  — `CODEX_SANDBOX*` 만. codex 는 자기가 띄우는 자식에게 이 표식을 심어
    ///          "이미 샌드박스 안" 을 알리는데, 상속되면 새 세션이 샌드박스를 건너뛴다.
    ///          **`CODEX_HOME` 은 안 지운다** — 사용자가 일부러 옮겨 둔 홈이고,
    ///          지우면 로그인(`auth.json`)까지 같이 사라져 뜨자마자 인증을 다시 물어본다.
    /// </summary>
    public string[] StripEnvPrefixes => Kind switch
    {
        CliKind.Claude => ["CLAUDE_CODE_", "CLAUDECODE", "CLAUDE_EFFORT", "CLAUDE_PID"],
        CliKind.Antigravity => [],
        _ => ["CODEX_SANDBOX"],
    };

    // ── 커맨드라인 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 띄울 커맨드라인을 만든다. **설정에서 고른 것만 싣는다** — 짧고 따옴표가 없는 값들이다.
    ///
    /// 긴 지침·대화 본문은 파일로 전달한다. Antigravity 인계는 파일을 읽으라는 짧은 초기 요청만
    /// WindowsArg로 인용해 --prompt-interactive에 넘긴다.
    /// </summary>
    /// <param name="head">
    /// 커맨드라인의 맨 앞. **이미 따옴표가 붙은 상태로 온다**(<see cref="CliLocator.Result.CommandHead"/>) —
    /// 실행 파일 하나일 수도, `node "…\cli.js"` 두 토막일 수도 있어서 여기서 다시 인용하면 깨진다.
    /// </param>
    /// <param name="resume">
    /// 무엇을 이어갈까. <see cref="ResumeMode.Fresh"/> 면 아무것도 안 붙이고 처음부터 —
    /// 그것이 [＋ 새 대화] 의 뜻이다.
    /// </param>
    /// <param name="cwd">작업 폴더. codex 의 이어하기 판정에 쓴다(세션이 폴더별로 갈린다).</param>
    /// <param name="codexHome">
    /// codex 가 쓸 홈(<c>CODEX_HOME</c>). 계정마다 다르다(<see cref="Accounts.CodexHomeOf"/>) —
    /// 이어하기 판정도 쪽지 프로필도 **그 홈 기준**이어야 한다. claude 에서는 안 쓴다.
    /// </param>
    /// <param name="newSessionId">
    /// claude 새 대화에 미리 정해 줄 세션 번호(<c>--session-id</c>). 번호를 우리가 알고 있어야
    /// 나중에 "이 탭의 대화" 를 다른 계정으로 넘길 때 같은 폴더의 다른 탭 대화와 헷갈리지 않는다.
    /// </param>
    /// <param name="claudeHome">
    /// claude 계정 폴더(계정 탭일 때만). 거기 적어 둔 사용자 범위 MCP 목록을 <c>--mcp-config</c> 로 얹는다
    /// (<see cref="Accounts.PrepareClaudeHome"/>) — 그 목록은 기본 계정의 <c>.claude.json</c> 안에 있어 폴더를 가르면 안 따라온다.
    /// </param>
    public string BuildCommandLine(string head, Settings s, ResumeMode resume, string cwd,
                                   string? codexHome = null, string? newSessionId = null, string? claudeHome = null)
        => Kind switch
        {
            CliKind.Claude => ClaudeLine(head, s, resume, newSessionId, claudeHome),
            CliKind.Codex => CodexLine(head, s, resume, cwd, codexHome ?? CodexHome),
            _ => AntigravityLine(head, s, resume),
        };

    private static string AntigravityLine(string head, Settings s, ResumeMode resume)
    {
        var sb = new StringBuilder(head);
        if (resume.SessionId is { Length: > 0 } sid) sb.Append(" --conversation ").Append(Quote(sid));
        else if (!resume.Fresh) sb.Append(" --continue");
        if (resume.HandoffPath is { Length: > 0 } handoff)
            sb.Append(" --prompt-interactive ").Append(WindowsArg(AntigravityTransfer.Prompt(handoff)));
        if (!string.IsNullOrWhiteSpace(s.AntigravityModel)) sb.Append(" --model ").Append(WindowsArg(s.AntigravityModel.Trim()));
        if (CliCatalog.ClampEffort(Antigravity.Backend, s.AntigravityModel, s.AntigravityEffort) is { Length: > 0 } effort)
            sb.Append(" --effort ").Append(effort);
        var permission = Perm(nameof(Settings.AntigravityPermission), s.AntigravityPermission);
        if (permission == "sandbox") sb.Append(" --sandbox");
        else if (permission == "dangerously-skip-permissions") sb.Append(" --dangerously-skip-permissions");
        return sb.ToString();
    }

    public static string AntigravityHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli");

    // 모델 표시 이름에 공백/따옴표가 들어가도 CreateProcessW 에 인자 하나로 전달한다.
    private static string WindowsArg(string value)
    {
        var sb = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>
    /// 계정 로그인을 띄울 커맨드라인. **대화 인자(모델·권한·쪽지)는 하나도 안 싣는다** — 로그인만 한다.
    /// claude 는 CLI 가 원래 하는 로그인 그대로다(<c>auth login</c> · <c>--sso</c> · <c>--console</c>) — 계정 폴더
    /// (<c>CLAUDE_CONFIG_DIR</c>) 안에서 하므로 기본 계정의 로그인은 그대로 남는다.
    /// </summary>
    public static string LoginCommandLine(string head, LoginKind kind) => head + kind switch
    {
        LoginKind.Claude => " auth login",
        LoginKind.ClaudeSso => " auth login --sso",
        LoginKind.ClaudeConsole => " auth login --console",
        LoginKind.ClaudeSetupToken => " setup-token",
        _ => " login",
    };

    private static string ClaudeLine(string head, Settings s, ResumeMode resume, string? newSessionId, string? claudeHome)
    {
        var sb = new StringBuilder(head);

        // **이어서 한다.** 강제 종료·전원 차단 뒤에도 하던 이야기가 남는다.
        // 대화가 아직 없는 폴더에서도 안전하다 — 실측에서 그냥 새로 시작했다(종료코드 0).
        if (resume.SessionId is { Length: > 0 } sid) sb.Append(" --resume ").Append(Quote(sid));
        else if (!resume.Fresh) sb.Append(" --continue");
        else if (newSessionId is { Length: > 0 } nid) sb.Append(" --session-id ").Append(Quote(nid));

        // 빈 값은 안 건다 = CLI 기본값. **claude 칸만 읽는다** — codex 칸의 슬러그가 여기로 새면
        // claude 가 모르는 모델로 뜬다(Settings.ClaudeModel 참조).
        if (s.ClaudeModel is { Length: > 0 } model) sb.Append(" --model ").Append(Quote(model.Trim()));
        if (s.ClaudeEffort is { Length: > 0 } effort) sb.Append(" --effort ").Append(Quote(effort.Trim()));
        if (Perm("ClaudeCodePermissionMode", s.ClaudeCodePermissionMode) is { Length: > 0 } pm)
            sb.Append(" --permission-mode ").Append(Quote(pm));

        // 브라우저 쪽지. **본문이 아니라 경로만 싣는다** — 위 주의 참조.
        if (AgentBrief.Ready)
            sb.Append(" --append-system-prompt-file ").Append(Quote(AgentBrief.ClaudeFile));

        // 계정 탭: 기본 계정의 사용자 범위 MCP 서버. 파일이 있을 때만(기본 계정에 없으면 안 만든다).
        if (claudeHome is { Length: > 0 } && File.Exists(Accounts.ClaudeMcpFile(claudeHome)))
            sb.Append(" --mcp-config ").Append(Quote(Accounts.ClaudeMcpFile(claudeHome)));

        return sb.ToString();
    }

    private static string CodexLine(string head, Settings s, ResumeMode resume, string cwd, string home)
    {
        var sb = new StringBuilder(head);

        // **서브커맨드는 옵션보다 먼저다.** `codex -m X resume --last` 는 안 먹는다.
        //
        // 그리고 claude 의 `--continue` 와 달리 **아무 때나 걸 수 없다.** codex 의 세션은
        // 폴더별로 갈리고, 그 폴더에 기록이 없으면 `resume --last` 는 이어갈 것을 못 찾는다 —
        // 터미널이 뜨자마자 죽고 화면에는 이유가 안 보인다. 그래서 기록을 먼저 확인한다.
        // 세션 번호를 콕 집어 준 경우는 그 확인이 필요 없다 — 목록에서 고른 것이라 실재한다.
        // 기록은 **이 탭이 쓸 홈**에서 본다 — 계정마다 홈이 달라 기록도 따로 쌓인다.
        if (resume.SessionId is { Length: > 0 } sid) sb.Append(" resume ").Append(Quote(sid));
        else if (!resume.Fresh && HasCodexSession(cwd, home)) sb.Append(" resume --last");

        // **codex 칸만 읽는다** — claude 칸의 `opus` 가 여기로 새면 `codex -m opus` 로 뜨자마자 죽는다.
        if (s.CodexModel is { Length: > 0 } model) sb.Append(" -m ").Append(Quote(model.Trim()));

        // 추론 강도는 전용 플래그가 없다 — 설정 덮어쓰기로 넘긴다.
        // 값 부분은 TOML 로 먼저 파싱되고, 실패하면 문자열 리터럴로 읽힌다(codex --help).
        // low·high 같은 맨낱말은 TOML 값이 아니므로 리터럴 경로로 들어간다 — 따옴표가 필요 없다.
        if (s.CodexEffort is { Length: > 0 } effort)
            sb.Append(" -c model_reasoning_effort=").Append(Quote(effort.Trim()));

        // 승인과 샌드박스는 **별개 축이다**(claude 의 --permission-mode 하나와 다르다).
        // 합쳐서 프리셋으로 만들지 않는다 — codex 가 받는 값을 그대로 보여 주는 편이,
        // 나중에 값이 늘었을 때 여기까지 고쳐야 하는 것보다 낫다.
        if (Perm("CodexSandbox", s.CodexSandbox) is { Length: > 0 } sandbox) sb.Append(" -s ").Append(Quote(sandbox));
        if (Perm("CodexApproval", s.CodexApproval) is { Length: > 0 } approval) sb.Append(" -a ").Append(Quote(approval));

        // 브라우저 쪽지. codex 에는 --append-system-prompt 가 없어 **프로필 파일**로 얹는다
        // ($CODEX_HOME/llmtools.config.toml 의 developer_instructions).
        // 이 이름으로 부를 때만 읽히므로 앱 밖 세션에는 영향이 없다.
        //
        // 대신 이 세션의 설정 쓰기(폴더 신뢰 · Windows 샌드박스 선택 · /model)도 **그 파일로** 간다(codex 0.155).
        // 그것을 지우면 codex 가 매번 다시 묻고, 샌드박스 설정 끝에 -s · -a 를 자기 기본값으로 덮는다 —
        // AgentBrief 가 쪽지만 고쳐 쓰고 나머지는 살린다(AgentBrief.MergeCodexConfig).
        //
        // **그 홈에 파일이 있을 때만 건다.** 계정 홈은 따로 써 넣는데(Accounts.PrepareCodexHome),
        // 못 썼는데 -p 를 걸면 codex 가 프로필을 못 찾고 뜨자마자 멈춘다.
        if (AgentBrief.Ready && File.Exists(AgentBrief.CodexFileIn(home)))
            sb.Append(" -p ").Append(AgentBrief.CodexProfile);

        // 내장 터미널은 전체 화면 대화를 쓴다. 인라인 모드는 창 폭이 바뀔 때 지난 대화를
        // 스크롤백에 처음부터 다시 출력한다. 설정 파일은 바꾸지 않고 이 앱에서만 적용한다.
        sb.Append(" -c tui.alternate_screen=always -c tui.fullscreen_transcript=true");

        return sb.ToString();
    }

    /// <summary>
    /// 이 폴더에 codex 세션 기록이 있는가. <c>$CODEX_HOME/sessions/년/월/일/rollout-*.jsonl</c>
    /// 의 첫 줄(session_meta)에 cwd 가 들어 있다.
    ///
    /// **전부 읽지 않는다.** 오래 쓴 PC 에는 파일이 수천 개다 — 최근 것부터 보고,
    /// 파일당 앞부분만 읽고, 하나라도 맞으면 그만둔다. 실패하면 false 로 떨어진다:
    /// 못 이어가는 것은 대화 하나를 새로 시작하는 것뿐이고, 잘못 이어가려다 죽는 쪽이 나쁘다.
    /// </summary>
    public static bool HasCodexSession(string cwd, string? home = null)
    {
        try
        {
            var dir = Path.Combine(home ?? CodexHome, "sessions");
            if (!Directory.Exists(dir)) return false;

            // JSON 안에서는 역슬래시가 두 번 적힌다. 비교는 그 형태로 한다.
            //
            // **닫는 따옴표까지 넣는다.** 안 넣으면 접두사가 걸린다 — `...\Temp` 로 찾으면
            // `...\Temp\다른폴더` 의 세션이 잡히고, 그 대화를 엉뚱하게 이어 간다.
            // 상위 폴더에서 한 번 쓴 적이 있으면 하위 프로젝트마다 남의 이야기가 딸려 온다.
            var needle = "\"cwd\":\"" +
                         Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)).Replace("\\", "\\\\") + "\"";

            var files = new DirectoryInfo(dir).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                                              .OrderByDescending(f => f.LastWriteTimeUtc)
                                              .Take(300);
            foreach (var f in files)
            {
                string head;
                try
                {
                    using var st = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var buf = new byte[4096];
                    var n = st.Read(buf, 0, buf.Length);
                    head = Encoding.UTF8.GetString(buf, 0, n);
                }
                catch { continue; }

                if (head.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { /* 못 보면 새로 시작한다 — 위 주석 참조 */ }
        return false;
    }

    /// <summary>
    /// claude 의 홈. 대화 기록이 `projects/&lt;폴더를-대시로-바꾼-이름&gt;/&lt;세션번호&gt;.jsonl` 로 쌓인다.
    /// `CLAUDE_CONFIG_DIR` 로 옮길 수 있다 — codex 의 `CODEX_HOME` 과 같은 물건이라 같이 존중한다.
    /// </summary>
    public static string ClaudeHome
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return string.IsNullOrWhiteSpace(home)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : home;
        }
    }

    /// <summary>
    /// codex 의 홈. 세션 기록·모델 캐시·로그인(auth.json)이 다 여기 있다.
    /// **한곳에서만 정한다** — 두 곳에서 각자 풀면 사용자가 CODEX_HOME 을 옮겼을 때
    /// 한쪽만 따라가고, 그 차이는 "이어하기가 안 된다" 로만 보인다.
    /// </summary>
    public static string CodexHome
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            return string.IsNullOrWhiteSpace(home)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : home;
        }
    }

    private static string Quote(string p) =>
        string.IsNullOrEmpty(p) ? "\"\"" : (p.Contains(' ') && !p.StartsWith('"') ? "\"" + p + "\"" : p);
}
