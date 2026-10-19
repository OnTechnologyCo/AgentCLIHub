using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCLIHub;

/// <summary>열려 있던 대화 탭 하나. 앱을 껐다 켜도 그대로 돌아오게 하려고 남긴다.</summary>
public sealed class TabState
{
    /// <summary>
    /// 이 탭이 무엇인가. <c>cli</c> 는 터미널, <c>browser</c> 는 웹 화면이다.
    /// **빠져 있으면 cli 로 본다** — 브라우저가 붙기 전에 저장된 파일에는 이 값이 없다.
    /// 못 읽는다고 탭을 버리면 사용자에게는 앱이 기억을 잃은 것으로 보인다.
    /// </summary>
    public string Kind { get; set; } = "cli";

    /// <summary>브라우저 탭이 마지막으로 보던 주소. cli 탭에서는 빈 값이다.</summary>
    public string Url { get; set; } = "";

    public string Cwd { get; set; } = "";

    /// <summary>
    /// 사용자가 손으로 붙인 이름. **비면 자동 이름을 쓴다**(CLI 는 폴더 이름, 브라우저는 문서 제목).
    ///
    /// 따로 두는 이유: 브라우저 탭 이름은 페이지가 바뀔 때마다 문서 제목으로 덮어써진다.
    /// 한 칸에 같이 담으면 이름을 붙인 다음 링크 한 번만 눌러도 그 이름이 사라진다.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 이 탭이 띄운 CLI. **탭마다 따로 기억한다** — CLI 는 [＋ 새 대화] 를 누를 때마다 고르므로
    /// 탭끼리 다를 수 있고, 되살릴 때도 그 탭이 처음 고른 CLI 로 떠야 한다.
    /// </summary>
    public string Backend { get; set; } = "claude_code";

    /// <summary>
    /// 이 탭이 쓰는 계정(<see cref="Account.Id"/>). **빈 값은 기본 계정**이다 — CLI 가 원래 로그인해 둔
    /// 그 계정(<c>~/.claude</c> · <c>~/.codex</c>). 계정을 바꿔 이어간 탭은 껐다 켜도 그 계정으로 떠야 한다 —
    /// 기본 계정으로 되살아나면 한도가 찬 계정으로 돌아가 곧바로 막힌다.
    /// </summary>
    public string Account { get; set; } = "";
    public string? HandoffPath { get; set; }
}

/// <summary>
/// 기본 계정 말고 **더 넣은 계정** 하나. 한 계정의 한도가 차면 탭을 다른 계정으로 넘겨 이어간다.
///
/// 로그인 자체는 여기 없다. CLI 마다 담는 곳이 다르다(<see cref="Accounts"/> 참조) —
/// claude 는 이 앱이 암호화해 둔 토큰, codex 는 계정마다 따로 둔 홈(<c>CODEX_HOME</c>)의 auth.json.
/// </summary>
public sealed class Account
{
    /// <summary>짧은 고유 번호. 계정 폴더 이름과 탭 기록(<see cref="TabState.Account"/>)에 쓰인다.</summary>
    public string Id { get; set; } = "";

    /// <summary>어느 CLI 의 계정인가 — <c>claude_code</c> · <c>codex_cli</c>.</summary>
    public string Backend { get; set; } = "";

    /// <summary>사용자가 붙인 이름. 탭 배지·사용량 줄·고르기 창에 이것이 찍힌다.</summary>
    public string Name { get; set; } = "";

    /// <summary>로그인에서 읽어 낸 이메일. **보여 주기용**이다 — 무엇을 판단하는 데 쓰지 않는다.</summary>
    public string Email { get; set; } = "";

    /// <summary>
    /// claude 계정의 로그인 방식 — <c>claude auth login</c> 에 붙일 것. 빈 값 = claude.ai 구독
    /// (Pro · Max · Team · Enterprise), <c>sso</c> = 회사 SSO 강제, <c>console</c> = Anthropic Console(API 과금).
    /// [다시 로그인] 이 처음과 같은 방식으로 열리게 남긴다. codex 계정에서는 안 쓴다.
    /// </summary>
    public string Login { get; set; } = "";
}

/// <summary>
/// 머리줄 바로가기 한 칸. 브라우저 북마크와 같은 것이다 — 이름과 주소, 그리고 지금 낼지 말지.
/// </summary>
public sealed class Bookmark
{
    /// <summary>버튼에 찍히는 글. 비면 주소의 호스트를 쓴다(<see cref="MainWindow"/> 에서 채운다).</summary>
    public string Name { get; set; } = "";

    /// <summary>갈 곳. **비면 버튼을 안 만든다** — 눌러도 아무 데도 안 가는 버튼은 고장으로 보인다.</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// 머리줄에 낼까. **주소와 따로 둔다** — 안 쓰는 동안 지워 버리면 다시 켤 때
    /// 어디였는지 찾아서 또 쳐야 한다. 숨김은 "주소는 남기고 버튼만 치우기" 다.
    /// </summary>
    public bool Show { get; set; } = true;
}

/// <summary>
/// 이 앱이 기억하는 전부. **일부러 얇다** — 우리는 CLI 를 띄우는 껍데기이고,
/// 대화 내용·이력은 CLI 자신이 자기 홈(`~/.claude` · `~/.codex`)에 들고 있다.
/// 여기 있는 것은 "다음에 뜰 때 어떤 인자로, 어떤 폴더에서 띄울까" 뿐이다.
///
/// `%LOCALAPPDATA%` 에 둔다. `%ProgramData%` 는 머신 공용이라 쓰기 권한이 걸리고,
/// 설정은 어차피 사용자별이다(사람마다 다른 모델·다른 폴더를 쓴다).
/// </summary>
public sealed class Settings
{
    public Dictionary<string, string> DefaultAccountNames { get; set; } = [];
    private string _uiLanguage = "ko";
    public string UiLanguage
    {
        get => _uiLanguage;
        set => _uiLanguage = NormalizeLanguage(value);
    }
    public static string NormalizeLanguage(string? value) =>
        string.Equals(value, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ko";

    /// <summary>
    /// 마지막으로 [＋ 새 대화] 에서 고른 CLI. <c>claude_code</c> · <c>codex_cli</c>.
    ///
    /// **이 값으로 대화를 열지는 않는다.** 어느 CLI 로 열지는 새 대화마다 사용자가 고르고,
    /// 이 값은 그 고르기 창에서 먼저 짚어 둘 자리일 뿐이다(Enter 한 번이면 같은 CLI 로 연다).
    /// 설정에 CLI 하나를 박아 두던 때는 다른 CLI 로 한 번 열어 보려면 설정을 바꿨다가 되돌려야
    /// 했고, 되돌리는 것을 잊으면 다음 대화가 엉뚱한 CLI 로 떴다.
    /// </summary>
    public string LastBackend { get; set; } = "claude_code";

    /// <summary>
    /// 마지막으로 [＋ 새 대화] 에서 고른 계정(<see cref="Account.Id"/>, 빈 값 = 기본 계정).
    /// <see cref="LastBackend"/> 와 같이 고르기 창이 먼저 짚어 둘 자리일 뿐이다.
    /// </summary>
    public string LastAccount { get; set; } = "";

    /// <summary>
    /// 더 넣은 계정들. **기본 계정은 여기 없다** — 그것은 CLI 가 원래 로그인해 둔 것이라 따로 적을 게 없다.
    /// 적힌 순서가 곧 화면 순서다(설정 · 고르기 창 · 사용량 줄).
    /// </summary>
    public List<Account> Accounts { get; set; } = [];

    // ── CLI 별 모델 · 추론 강도 ─────────────────────────────────────────────
    // **CLI 마다 따로 둔다.** 새 대화마다 CLI 를 고르므로 둘 다 언제든 쓰인다 — 한 칸을 같이 쓰면
    // claude 에서 고른 `opus` 가 codex 로 넘어가 `codex -m opus` 가 되고, 뜨자마자 죽는다.
    // 빈 값 = 안 건다(= CLI 기본값). 빈 칸과 기본값을 구별하지 않는다.

    /// <summary>claude 의 `--model`. 별칭(opus …)도 전체 모델 이름도 받는다.</summary>
    public string ClaudeModel { get; set; } = "";

    /// <summary>claude 의 `--effort`.</summary>
    public string ClaudeEffort { get; set; } = "";

    /// <summary>codex 의 `-m`. codex 가 캐시해 둔 목록의 슬러그만 받는다(CliCatalog 참조).</summary>
    public string CodexModel { get; set; } = "";

    /// <summary>codex 의 추론 강도. **모델마다 받는 값이 다르다**(CliCatalog 참조).</summary>
    public string CodexEffort { get; set; } = "";

    /// <summary>claude 의 `--permission-mode`. 승인과 접근을 한 값이 함께 정한다.</summary>
    public string ClaudeCodePermissionMode { get; set; } = "";

    /// <summary>codex 의 `-s`. 샌드박스와 승인은 **별개 축이다** — 하나로 접지 않는다.</summary>
    public string CodexSandbox { get; set; } = "";

    /// <summary>codex 의 `-a`.</summary>
    public string CodexApproval { get; set; } = "";

    /// <summary>
    /// 실행 파일 경로 지정. 비어 있으면 자동 탐지한다(<see cref="CliLocator"/>).
    ///
    /// **CLI 마다 따로 둔다.** 하나로 합치면 CLI 를 바꿀 때마다 다시 골라야 하고,
    /// 잘못 남은 경로로 엉뚱한 바이너리가 떠도 화면에는 "그냥 안 뜬다" 로만 보인다.
    /// </summary>
    public string ClaudeCliPath { get; set; } = "";
    public string CodexCliPath { get; set; } = "";
    public string AntigravityCliPath { get; set; } = "";
    public string AntigravityPermission { get; set; } = "";
    public string AntigravityModel { get; set; } = "";
    public string AntigravityEffort { get; set; } = "";

    /// <summary>
    /// 열려 있던 탭들. 앱을 다시 켜면 이대로 되살린다.
    ///
    /// **탭이 있는 앱에서 이것이 없으면 껐다 켤 때마다 처음부터다.** 대화가 CLI 쪽에
    /// 남아 있어도 어느 폴더를 열어 두었는지는 우리만 아는 것이라, 우리가 안 적으면 사라진다.
    /// </summary>
    public List<TabState> Tabs { get; set; } = [];

    /// <summary>폴더 고르기 창을 어디서 열까. 마지막으로 고른 자리를 기억한다.</summary>
    public string LastFolder { get; set; } = "";

    // ── 브라우저 로그인 ─────────────────────────────────────────────────────

    /// <summary>
    /// 브라우저 탭의 로그인을 앱을 껐다 켜도 유지할까.
    ///
    /// **이것은 세션 쿠키를 디스크에 남긴다는 뜻이다.** WebView2 는 만료 시각이 있는 쿠키만
    /// 프로필에 남기고 세션 쿠키는 프로세스가 죽을 때 버린다 — 그래서 지속 쿠키를 쓰는 곳
    /// (Jira 등)은 그냥 유지되는데 **세션 쿠키를 쓰는 사이트만 자꾸 로그아웃된다.**
    /// <see cref="CookieVault"/> 가 종료할 때 그것만 꺼내 두었다가 켤 때 되돌린다.
    ///
    /// 세션 쿠키는 곧 로그인 토큰이다. DPAPI(<c>CurrentUser</c>)로 감싸 두므로 같은 Windows
    /// 계정에서만 풀리지만, **끄면 그 파일 자체가 안 생긴다** — 공용 PC 라면 끄는 쪽이 맞다.
    /// </summary>
    public bool KeepSignedIn { get; set; } = true;

    /// <summary>
    /// Claude Code 의 로그인 상태를 대신 지킬까.
    ///
    /// 켜면 성할 때 사본을 떠 두었다가, 켤 때 <c>~/.claude.json</c> 이 깨져 있으면 되돌린다.
    /// **탭마다 claude 를 하나씩 띄우는 앱이라 이것이 실제로 필요하다** — 여러 프로세스가
    /// 같은 파일을 쓰다 갈아 끼우는 중에 끊기면 그 파일이 잘리고, 화면에는 "로그인이
    /// 풀렸다" 로만 보인다. 자세한 것은 <see cref="ClaudeAuthGuard"/> 참조.
    /// </summary>
    public bool GuardClaudeLogin { get; set; } = true;

    /// <summary>
    /// 사이트 비밀번호를 저장할까(WebView2 프로필의 자동 저장·자동 채움).
    ///
    /// **끈다고 이미 저장된 것이 지워지지는 않는다**(새로 안 저장할 뿐 채워는 준다).
    /// 지우려면 설정 화면의 [저장된 로그인 지우기] 를 쓴다.
    /// </summary>
    public bool SavePasswords { get; set; } = true;

    /// <summary>
    /// 머리줄 바로가기. **순서 그대로 머리줄에 선다** — 목록이 곧 화면이다.
    ///
    /// 이름·주소를 사용자가 직접 넣고 지운다(북마크). 고정 세 칸(Jira · GitHub · GitLab)이었던
    /// 것을 목록으로 편 이유: 사내에는 Jira 가 여럿이거나 GitLab 이 프로젝트마다 다르고,
    /// 그때 고정 칸은 "하나만 골라 넣어라" 가 된다. 넣을 자리가 없으면 그 사이트는 그냥 못 쓴다.
    ///
    /// 로그인·SSO 는 여기 없다. WebView2 가 자기 프로필(<see cref="DataDir"/>/webview2)에
    /// 들고 있어 우리가 알 필요도, 알아서도 안 된다.
    /// </summary>
    public List<Bookmark> Bookmarks { get; set; } =
    [
        new() { Name = "GitHub", Url = "https://github.com" },
        new() { Name = "GitLab", Url = "https://gitlab.com" },
    ];

    // ── 옛 설정(고정 세 칸) ──────────────────────────────────────────────────
    // 바로가기가 목록이 되기 전 모양. **읽기만 한다** — 한 번 옮기고 파일에서 지운다.
    // 남겨 둔 이유: 이것을 안 읽으면 이미 쓰던 사람의 사내 Jira·GitLab 주소가 업데이트
    // 한 번에 사라지고, 화면에는 "버튼이 없어졌다" 로만 보인다. 어디에 적혀 있었는지도 모른다.
    // null 이면 이미 옮긴 파일이다(우리는 다시 쓰지 않는다).

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? JiraUrl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? GithubUrl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? GitlabUrl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowJira { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowGithub { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowGitlab { get; set; }

    /// <summary>
    /// 옛 세 칸을 목록으로 옮긴다. 주소가 빈 칸은 안 옮긴다 — 갈 곳 없는 북마크일 뿐이다.
    ///
    /// **옛 키가 하나라도 있으면 그 파일에는 <c>Bookmarks</c> 가 없다**(우리는 두 모양을 같이
    /// 쓴 적이 없다). 그래서 통째로 갈아 끼워도 잃을 것이 없다.
    /// </summary>
    private void MigrateLegacySites()
    {
        if (JiraUrl is null && GithubUrl is null && GitlabUrl is null) return;

        var moved = new List<Bookmark>();
        void Take(string name, string? url, bool? show)
        {
            if (!string.IsNullOrWhiteSpace(url))
                moved.Add(new Bookmark { Name = name, Url = url.Trim(), Show = show != false });
        }
        Take("Jira", JiraUrl, ShowJira);
        Take("GitHub", GithubUrl, ShowGithub);
        Take("GitLab", GitlabUrl, ShowGitlab);

        Bookmarks = moved;
        JiraUrl = GithubUrl = GitlabUrl = null;
        ShowJira = ShowGithub = ShowGitlab = null;
    }

    // ── 옛 설정(CLI 하나) ────────────────────────────────────────────────────
    // 새 대화의 CLI 를 설정에서 하나 정해 두던 때의 모양. 모델·강도도 그 CLI 몫 한 칸뿐이었다.
    // **읽기만 한다** — 한 번 옮기고 파일에서 지운다. 안 읽으면 쓰던 모델·강도가 업데이트
    // 한 번에 사라지고, 화면에는 "설정이 풀렸다" 로만 보인다.

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Backend { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Model { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Effort { get; set; }

    /// <summary>
    /// 옛 한 칸짜리 모델·강도를 **그때 고른 CLI 의 칸으로** 옮긴다. 그 값은 그 CLI 기준으로
    /// 검증돼 저장된 것이라 다른 CLI 칸에 넣으면 안 된다(위 주석의 `codex -m opus`).
    ///
    /// 새 칸에 이미 값이 있으면 그것이 이긴다 — 두 모양이 한 파일에 같이 있는 것은
    /// 손으로 고친 경우뿐이고, 그때 새로 적은 쪽이 사용자의 뜻이다.
    /// </summary>
    private void MigrateSingleCli()
    {
        if (Backend is null && Model is null && Effort is null) return;

        var cli = CliProfile.ForOrDefault(Backend);
        static string Keep(string now, string? old) => now.Length > 0 ? now : (old ?? "").Trim();

        if (cli.Kind == CliKind.Codex)
        {
            CodexModel = Keep(CodexModel, Model);
            CodexEffort = Keep(CodexEffort, Effort);
        }
        else if (cli.Kind == CliKind.Claude)
        {
            ClaudeModel = Keep(ClaudeModel, Model);
            ClaudeEffort = Keep(ClaudeEffort, Effort);
        }

        // 쓰던 CLI 를 고르기 창이 먼저 짚게 한다 — 업데이트 뒤 첫 [＋ 새 대화] 에서
        // Enter 만 누르면 전과 같은 CLI 로 뜬다.
        LastBackend = cli.Backend;
        Backend = Model = Effort = null;
    }

    // ── 저장 ────────────────────────────────────────────────────────────────

    public static string DataDir => UpgradeMigration.DataDirectory; // 기존 데이터는 설치 단계에서 이전한다.

    public static string FilePath => Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions _opt = new()
    {
        WriteIndented = true,
        // 한글이 \uXXXX 로 깨져 나가면 사람이 파일을 열어 고칠 수가 없다.
        // 이 파일은 손편집을 전제로 한다 — 설정 화면이 안 뜰 때 마지막 수단이다.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>읽는다. 파일이 깨졌으면 기본값으로 떨어진다 — 설정 하나 때문에 앱이 안 뜨면 안 된다.</summary>
    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                s.MigrateLegacySites();
                s.MigrateSingleCli();
                return s;
            }
        }
        catch { /* 손상 — 기본값으로 복구 */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, _opt),
                              new System.Text.UTF8Encoding(false));
        }
        catch { /* 저장 실패가 실행을 막지 않는다 — 이번 세션은 메모리 값으로 계속 돈다 */ }
    }
}
