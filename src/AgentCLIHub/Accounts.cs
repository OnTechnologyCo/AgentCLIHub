using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCLIHub;

/// <summary>
/// 계정 하나를 화면·실행에 쓰기 좋게 편 것. **기본 계정(<see cref="Id"/> 빈 값)도 여기 들어간다** —
/// 화면에서는 기본 계정도 더 넣은 계정과 똑같이 한 줄로 서야 고를 수 있다.
/// </summary>
/// <param name="Note">로그인 상태를 사람 말로. 설정 화면과 고르기 창이 그대로 찍는다.</param>
public sealed record AccountView(CliProfile Cli, string Id, string Name, string Email, bool LoggedIn, string Note)
{
    public bool IsDefault => Id.Length == 0;

    /// <summary>탭 배지·사용량 줄에 찍힐 이름. 기본 계정도 사용자가 바꿀 수 있다.</summary>
    public string Label => Name;
}

/// <summary>claude 계정이 무엇으로 로그인돼 있나.</summary>
public enum ClaudeMode
{
    /// <summary>로그인이 없다.</summary>
    None,

    /// <summary>
    /// 계정 폴더(<c>CLAUDE_CONFIG_DIR</c>)에 **일반 로그인**(<c>claude auth login</c>)이 있다. 권한이 온전하다 —
    /// 사용량 조회·Claude in Chrome·Remote Control 이 된다. 회사 계정(Team·Enterprise·SSO·Console)도 이것이다.
    /// </summary>
    Login,

    /// <summary><c>claude setup-token</c> 의 1년짜리 토큰. 추론 전용이라 사용량 조회 등이 안 된다.</summary>
    Token,
}

/// <summary>
/// 계정. **CLI 마다 로그인을 담는 곳이 다르다** — 그래서 여러 계정을 쓰는 방법도 갈린다.
///
///   claude — 더 넣은 계정마다 **설정 폴더를 따로 둔다**(<c>CLAUDE_CONFIG_DIR</c> = 계정 폴더) — 그 폴더 안에서
///            평소 하던 대로 <c>claude auth login</c> 을 한다(일반 로그인 · SSO · Console 모두). 로그인
///            (<c>.credentials.json</c> · <c>.claude.json</c>)만 따로고, **대화 기록(projects)·플러그인·스킬·설정은
///            기본 폴더와 같이 쓴다**(<see cref="PrepareClaudeHome"/>) — 그래서 계정을 바꿔도 같은 대화 파일을
///            <c>--resume</c> 으로 그대로 잇는다. 대안으로 <c>setup-token</c> 토큰을 <c>CLAUDE_CODE_OAUTH_TOKEN</c>
///            으로 심는 길도 남겨 두었다(<see cref="ClaudeMode.Token"/>). 폴더 로그인이 있으면 그쪽이 먼저다.
///
///   codex  — 로그인이 <c>$CODEX_HOME/auth.json</c> 에 있고 환경변수로 대신할 길이 없다.
///            그래서 더 넣은 계정마다 **홈을 따로 둔다**(<see cref="CodexHomeOf"/>). 기본 홈의
///            설정·스킬·플러그인·샌드박스는 그 홈이 따라가게 해 둔다(<see cref="PrepareCodexHome"/>).
///
/// **로그인 파일을 폴더끼리 복사하지 않는다**(둘 다) — 한쪽이 갱신하면 갱신 토큰이 바뀌어 다른 쪽 로그인이 죽는다.
/// 계정 목록은 설정 파일(<see cref="Settings.Accounts"/>)에, 로그인은 계정 폴더(<see cref="DirOf"/>)에 있다.
/// </summary>
public static class Accounts
{
    /// <summary>계정 폴더들의 뿌리. 폴더 이름이 곧 계정 번호다.</summary>
    public static string Root => Path.Combine(Settings.DataDir, "accounts");

    public static string DirOf(string id) => Path.Combine(Root, id);

    /// <summary>
    /// codex 계정의 홈(<c>CODEX_HOME</c>). **기본 계정은 CLI 가 원래 쓰는 홈이다** — 여기서 새로 정하지 않는다.
    /// 더 넣은 계정은 계정 폴더 자체가 홈이다.
    /// </summary>
    public static string CodexHomeOf(string? id) =>
        string.IsNullOrEmpty(id) ? CliProfile.CodexHome : DirOf(id);

    /// <summary>
    /// claude 계정의 설정 폴더(<c>CLAUDE_CONFIG_DIR</c>). 기본 계정은 CLI 가 원래 쓰는 폴더(<c>~/.claude</c>)다.
    /// 더 넣은 계정은 계정 폴더 자체가 설정 폴더다 — 로그인이 거기 쌓인다.
    /// </summary>
    public static string ClaudeHomeOf(string? id) =>
        string.IsNullOrEmpty(id) ? CliProfile.ClaudeHome : DirOf(id);

    /// <summary>
    /// 그 설정 폴더의 전역 설정 파일. **기본 폴더만 자리가 다르다** — <c>CLAUDE_CONFIG_DIR</c> 이 없으면
    /// <c>~/.claude</c> 안이 아니라 홈의 <c>~/.claude.json</c> 이다. 폴더를 지정하면 그 안이다(실측).
    /// </summary>
    public static string ClaudeConfigJson(string home)
    {
        var main = CliProfile.ClaudeHome;
        var isMain = SamePath(home, main);
        if (isMain && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        return Path.Combine(home, ".claude.json");
    }

    /// <summary>
    /// 이 설정 폴더에 일반 로그인이 있는가. claude.ai 로그인은 <c>.credentials.json</c> 의 <c>claudeAiOauth</c>,
    /// Console 로그인은 <c>.claude.json</c> 의 <c>primaryApiKey</c> 에 남는다(Windows).
    /// </summary>
    public static bool ClaudeLoggedIn(string home)
    {
        try
        {
            var cred = Path.Combine(home, ".credentials.json");
            if (File.Exists(cred) && JsonNode.Parse(ReadShared(cred))?["claudeAiOauth"]?["refreshToken"] is JsonValue) return true;
            var cfg = ClaudeConfigJson(home);
            return File.Exists(cfg) && JsonNode.Parse(ReadShared(cfg))?["primaryApiKey"] is JsonValue v &&
                   v.GetValueKind() == JsonValueKind.String;
        }
        catch { return false; }
    }

    /// <summary>
    /// 이 claude 계정으로 띄울 때 무엇을 쓰나. **폴더 로그인이 먼저다** — 권한이 온전하고 갱신도 claude 가 한다.
    /// 둘 다 있으면 토큰은 안 쓴다(같이 심으면 토큰이 로그인을 가린다).
    /// </summary>
    public static ClaudeMode ClaudeModeOf(string? id)
    {
        if (string.IsNullOrEmpty(id)) return ClaudeMode.Login;
        if (ClaudeLoggedIn(ClaudeHomeOf(id))) return ClaudeMode.Login;
        return ClaudeToken(id) is not null ? ClaudeMode.Token : ClaudeMode.None;
    }

    /// <summary>그 설정 폴더에 로그인한 계정의 이메일(<c>oauthAccount.emailAddress</c>).</summary>
    public static string ClaudeEmail(string home)
    {
        try
        {
            var path = ClaudeConfigJson(home);
            if (!File.Exists(path)) return "";
            using var doc = JsonDocument.Parse(ReadShared(path));
            return doc.RootElement.TryGetProperty("oauthAccount", out var acc) &&
                   acc.TryGetProperty("emailAddress", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static Account? Find(Settings s, string? id) =>
        string.IsNullOrEmpty(id) ? null : s.Accounts.FirstOrDefault(a => a.Id == id);

    /// <summary>
    /// 이 탭 기록의 계정이 아직 있는가. 지운 계정을 가리키는 탭은 **기본 계정으로 떨어진다** —
    /// 없는 계정으로 띄우면 로그인이 없어 뜨자마자 막히고, 화면에는 이유가 안 보인다.
    /// </summary>
    public static string Resolve(Settings s, CliProfile cli, string? id)
    {
        if (!cli.SupportsAccounts) return "";
        var a = Find(s, id);
        return a is not null && CliProfile.For(a.Backend) == cli ? a.Id : "";
    }

    // ── 목록 ────────────────────────────────────────────────────────────────

    /// <summary>이 CLI 의 계정들. **기본 계정이 맨 앞이다** — 그 뒤는 넣은 순서.</summary>
    public static List<AccountView> Of(Settings s, CliProfile cli)
    {
        var list = new List<AccountView> { DefaultView(s, cli) };
        if (!cli.SupportsAccounts) return list;
        foreach (var a in s.Accounts.Where(a => CliProfile.For(a.Backend) == cli))
            list.Add(ExtraView(cli, a));
        return list;
    }

    public static List<AccountView> All(Settings s) => CliProfile.All.SelectMany(c => Of(s, c)).ToList();

    public static AccountView View(Settings s, CliProfile cli, string? id)
    {
        if (!cli.SupportsAccounts) return DefaultView(s, cli);
        var a = Find(s, id);
        return a is not null && CliProfile.For(a.Backend) == cli ? ExtraView(cli, a) : DefaultView(s, cli);
    }

    public static string DefaultName(Settings settings, CliProfile cli) =>
        settings.DefaultAccountNames is not null && settings.DefaultAccountNames.TryGetValue(cli.Backend, out var name) &&
        !string.IsNullOrWhiteSpace(name) ? name.Trim() : "기본";

    private static AccountView DefaultView(Settings settings, CliProfile cli) =>
        DefaultView(cli) with { Name = DefaultName(settings, cli) };

    public static bool Rename(Settings settings, CliProfile cli, string id, string name, out string error)
    {
        error = "";
        name = (name ?? "").Trim();
        if (name.Length > 20) name = name[..20];
        var account = Find(settings, id);
        if (id.Length > 0 && (account is null || account.Backend != cli.Backend))
        { error = "그런 계정이 없습니다."; return false; }
        if (name.Length == 0) { error = "계정 이름을 입력하세요."; return false; }
        if (settings.Accounts.Any(a => a.Id != id && a.Backend == cli.Backend && a.Name == name) ||
            (id.Length > 0 && DefaultName(settings, cli) == name))
        { error = $"'{name}' 은 이미 있는 계정 이름입니다."; return false; }
        if (account is not null) account.Name = name;
        else
        {
            settings.DefaultAccountNames ??= [];
            settings.DefaultAccountNames[cli.Backend] = name;
        }
        return true;
    }

    private static AccountView DefaultView(CliProfile cli)
    {
        if (cli.Kind == CliKind.Antigravity)
            return new AccountView(cli, "", "기본", "", false,
                "Antigravity CLI 의 기존 로그인을 사용합니다. 로그인은 열린 CLI 에서 진행하세요.");
        if (cli.Kind == CliKind.Claude)
        {
            var main = CliProfile.ClaudeHome;
            var ok = ClaudeLoggedIn(main);
            return new AccountView(cli, "", "기본", ClaudeEmail(main), ok,
                                   ok ? "Claude Code 에 로그인해 둔 계정" : "Claude Code 에 로그인돼 있지 않습니다");
        }

        var home = CliProfile.CodexHome;
        var logged = CodexLoggedIn(home);
        return new AccountView(cli, "", "기본", CodexEmail(home), logged,
                               logged ? "Codex 에 로그인해 둔 계정" : "Codex 에 로그인돼 있지 않습니다");
    }

    private static AccountView ExtraView(CliProfile cli, Account a)
    {
        if (cli.Kind == CliKind.Antigravity)
        {
            var state = AntigravityAccounts.State(AntigravityAccounts.ProfileOf(a.Id));
            return new AccountView(cli, a.Id, a.Name, state.Email.Length > 0 ? state.Email : a.Email, state.LoggedIn,
                state.LoggedIn ? "이 계정 폴더에 로그인해 둔 Antigravity" : "로그인 필요 — [로그인] 을 누르세요");
        }
        if (cli.Kind == CliKind.Claude)
        {
            var mode = ClaudeModeOf(a.Id);
            var mail = mode == ClaudeMode.Login ? ClaudeEmail(ClaudeHomeOf(a.Id)) : "";
            return new AccountView(cli, a.Id, a.Name, mail.Length > 0 ? mail : a.Email, mode != ClaudeMode.None,
                                   mode switch
                                   {
                                       ClaudeMode.Login => a.Login == "console"
                                           ? "Console(API 과금) 로그인 — 대화 기록·플러그인·설정은 기본 계정과 같이 씁니다"
                                           : "로그인됨 — 대화 기록·플러그인·설정은 기본 계정과 같이 씁니다",
                                       ClaudeMode.Token => "setup-token 로그인 — 추론 전용이라 사용량은 못 읽습니다",
                                       _ => "로그인 필요 — [로그인] 을 누르세요",
                                   });
        }

        var home = CodexHomeOf(a.Id);
        var logged = CodexLoggedIn(home);
        var email = logged ? CodexEmail(home) : "";
        return new AccountView(cli, a.Id, a.Name, email.Length > 0 ? email : a.Email, logged,
                               logged ? "이 계정 폴더에 로그인해 둔 Codex" : "로그인 필요 — [로그인] 을 누르세요");
    }

    // ── 더하기 · 빼기 ───────────────────────────────────────────────────────

    /// <summary>
    /// 계정을 하나 더한다. **로그인은 여기서 안 한다** — 로그인 탭을 띄우는 것은 부르는 쪽이다
    /// (로그인이 끝나야 계정이 쓸모 있지만, 목록에 먼저 서 있어야 다시 로그인할 자리가 생긴다).
    /// </summary>
    public static Account Add(Settings s, CliProfile cli, string name)
    {
        if (!cli.SupportsAccounts) throw new NotSupportedException("이 CLI 의 계정은 CLI 에서 관리합니다.");
        name = (name ?? "").Trim();
        if (name.Length > 20) name = name[..20];
        if (name.Length == 0 || name == "기본")
            name = $"{cli.Display} {s.Accounts.Count(a => CliProfile.For(a.Backend) == cli) + 2}";

        // 같은 CLI 안에서 이름이 겹치면 고르기 창에서 구별이 안 된다.
        var taken = s.Accounts.Where(a => CliProfile.For(a.Backend) == cli).Select(a => a.Name).ToHashSet();
        taken.Add(DefaultName(s, cli));
        var baseName = name;
        for (var i = 2; taken.Contains(name); i++) name = $"{baseName} {i}";

        var acc = new Account { Id = NewId(s), Backend = cli.Backend, Name = name };
        s.Accounts.Add(acc);
        Directory.CreateDirectory(DirOf(acc.Id));
        return acc;
    }

    private static string NewId(Settings s)
    {
        for (; ; )
        {
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
            if (s.Accounts.All(a => a.Id != id) && !Directory.Exists(DirOf(id))) return id;
        }
    }

    /// <summary>
    /// 계정을 지운다 — **목록과 계정 폴더(로그인·그 계정에서 이어간 codex 기록)를 같이** 지운다.
    /// 로그인만 남겨 두면 "지웠는데 토큰이 디스크에 남아 있다" 가 된다.
    ///
    /// codex 계정 폴더에는 **기본 홈을 가리키는 연결(junction)** 이 있다(<see cref="PrepareCodexHome"/>).
    /// 그 속으로 따라 들어가 지우면 기본 홈의 스킬·플러그인·샌드박스 비밀까지 날아간다 —
    /// 그래서 연결은 연결 자체만 끊고, 나머지도 연결을 절대 따라가지 않는 방식으로 지운다.
    /// </summary>
    public static bool Remove(Settings s, string id, out string error)
    {
        error = "";
        var acc = Find(s, id);
        if (acc is null) { error = "그런 계정이 없습니다."; return false; }

        var dir = DirOf(acc.Id);
        // 계정 폴더 **바깥**을 지우는 일이 없게 한 번 더 본다. 번호가 이상하면 폴더는 두고 목록만 뺀다.
        var inRoot = acc.Id.Length > 0 && acc.Id.All(char.IsAsciiLetterOrDigit) &&
                     Path.GetFullPath(dir).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar,
                                                      StringComparison.OrdinalIgnoreCase);
        try
        {
            if (inRoot && Directory.Exists(dir)) DeleteTree(new DirectoryInfo(dir));
        }
        catch (Exception ex)
        {
            error = $"계정 폴더를 다 지우지 못했습니다 — {ex.Message} ({dir})";
        }

        s.Accounts.Remove(acc);
        return true;
    }

    /// <summary>연결(reparse point)은 연결만 끊는다. 그 속으로는 절대 안 들어간다.</summary>
    private static void DeleteTree(DirectoryInfo d)
    {
        foreach (var e in d.EnumerateFileSystemInfos())
        {
            if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // 디렉터리 연결에 RemoveDirectory 를 걸면 연결만 사라지고 대상은 그대로다.
                if (e is DirectoryInfo link) link.Delete(recursive: false);
                else e.Delete();
                continue;
            }
            if (e is DirectoryInfo sub) { DeleteTree(sub); continue; }
            try { e.Attributes = FileAttributes.Normal; } catch { }
            e.Delete();
        }
        d.Delete(recursive: false);
    }

    // ── claude: 토큰 ────────────────────────────────────────────────────────

    private static readonly byte[] TokenSalt = "LlmTools/claude-token/v1"u8.ToArray();

    private static string TokenPath(string id) => Path.Combine(DirOf(id), "claude-token.dat");

    /// <summary>
    /// <c>setup-token</c> 이 준 토큰을 모양만 본다. 이 앱이 아는 것은 이 모양뿐이다 —
    /// 실제로 맞는 토큰인지는 그 계정으로 띄워 봐야 안다(틀리면 CLI 가 401 을 말한다).
    /// </summary>
    public static bool LooksLikeClaudeToken(string? t) =>
        t is { Length: >= 40 and <= 400 } && t.StartsWith("sk-ant-", StringComparison.Ordinal) &&
        t.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// 토큰을 남긴다. **평문으로 두지 않는다** — 이 토큰 하나로 1년 동안 그 계정을 쓸 수 있다.
    /// DPAPI(<see cref="DataProtectionScope.CurrentUser"/>)라 같은 Windows 계정에서만 풀린다.
    /// </summary>
    public static void SaveClaudeToken(string id, string token)
    {
        token = token.Trim();
        if (!LooksLikeClaudeToken(token)) throw new ArgumentException("setup-token 이 준 토큰(sk-ant-…) 모양이 아닙니다.");

        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), TokenSalt, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(DirOf(id));
        var path = TokenPath(id);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>풀어 낸 토큰. 없거나 못 풀면(다른 Windows 계정·깨진 파일) null.</summary>
    public static string? ClaudeToken(string id)
    {
        try
        {
            var path = TokenPath(id);
            if (!File.Exists(path)) return null;
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(path), TokenSalt, DataProtectionScope.CurrentUser);
            var t = Encoding.UTF8.GetString(raw);
            return LooksLikeClaudeToken(t) ? t : null;
        }
        catch { return null; }
    }

    // ── claude: 계정 폴더 ───────────────────────────────────────────────────

    /// <summary>
    /// 기본 폴더와 **같이 쓸** 폴더(연결). <c>projects</c> 가 가장 중요하다 — 대화 기록과 자동 메모리가 거기 있어,
    /// 이것을 같이 써야 계정을 바꿔도 같은 대화를 <c>--resume</c> 으로 잇는다. <c>file-history</c> 는 되감기(체크포인트)
    /// 사본이라 같은 대화를 이어 쓰면 같이 가야 한다. 나머지는 사용자가 깔아 둔 것(플러그인·스킬·에이전트·명령·말투).
    ///
    /// <c>sessions</c>(떠 있는 프로세스 표식) · <c>history.jsonl</c>(입력 기록) · 로그인 파일은 **같이 쓰지 않는다.**
    /// </summary>
    private static readonly string[] ClaudeSharedDirs =
        ["projects", "file-history", "plugins", "skills", "agents", "commands", "output-styles", "todos", "plans"];

    /// <summary>띄울 때마다 기본 폴더 것으로 덮어쓰는 파일 — 기본 계정의 설정이 기준이다.</summary>
    private static readonly string[] ClaudeSyncFiles = ["settings.json", "settings.local.json", "CLAUDE.md", "keybindings.json"];

    /// <summary>계정 탭에 <c>--mcp-config</c> 로 얹는 사용자 범위 MCP 서버(기본 계정 <c>.claude.json</c> 의 것).</summary>
    public static string ClaudeMcpFile(string home) => Path.Combine(home, "agentclihub-mcp.json");

    /// <summary>
    /// claude 계정 폴더를 **띄울 때마다**(로그인 전에도) 기본 폴더에 맞춘다.
    ///
    ///   * 폴더 — 연결(junction)로 같이 쓴다(<see cref="ClaudeSharedDirs"/>). 기본 쪽에 없으면 만들어 두고 잇는다 —
    ///            <c>projects</c> 가 따로 생기면 계정을 바꿀 때 같은 대화를 못 찾는다.
    ///   * 설정 — settings.json · CLAUDE.md 따위를 기본 폴더 것으로 덮어쓴다.
    ///   * MCP  — 사용자 범위 MCP 서버는 <c>.claude.json</c> 안에 있어 연결로 못 나눈다. 그 목록만 따로 적어
    ///            <c>--mcp-config</c> 로 얹는다(<see cref="CliProfile"/>). 계정 폴더의 <c>.claude.json</c> 은 손대지 않는다 —
    ///            떠 있는 claude 가 쓰는 파일이라, 겹쳐 쓰면 기본 폴더에서 겪은 잘림 사고가 이쪽에서 난다.
    ///   * 첫 준비 — <c>.claude.json</c> 이 아직 없을 때만(= 그 계정의 claude 가 한 번도 안 떴을 때) 온보딩 완료 표시와
    ///            기본 계정이 믿은 폴더 목록을 적어 둔다. 안 그러면 첫 탭에서 테마 고르기부터 다시 묻는다.
    ///
    /// **로그인 파일은 절대 건드리지 않는다.** 실패는 기록만 한다.
    /// </summary>
    public static void PrepareClaudeHome(string home, Action<string, string>? log = null)
    {
        var main = CliProfile.ClaudeHome;
        if (SamePath(main, home)) return;
        lock (_prepLock) PrepareClaudeCore(main, home, log);
    }

    private static void PrepareClaudeCore(string main, string home, Action<string, string>? log)
    {
        Directory.CreateDirectory(home);

        foreach (var name in ClaudeSharedDirs)
        {
            var src = Path.Combine(main, name);
            var dst = Path.Combine(home, name);
            if (Directory.Exists(dst) || File.Exists(dst)) continue;
            // 대화 기록 폴더는 **반드시** 같이 써야 한다 — 없으면 만들어서라도 잇는다.
            if (!Directory.Exists(src))
            {
                if (name != "projects") continue;
                try { Directory.CreateDirectory(src); } catch { continue; }
            }
            try { Junction(dst, src); }
            catch (Exception ex) { log?.Invoke($"계정 폴더에 {name} 를 잇지 못했습니다 — {ex.Message}", "w"); }
        }

        foreach (var name in ClaudeSyncFiles)
        {
            var src = Path.Combine(main, name);
            var dst = Path.Combine(home, name);
            try
            {
                if (File.Exists(src)) CopyIfChanged(src, dst);
            }
            catch (Exception ex) { log?.Invoke($"계정 폴더에 {name} 를 맞추지 못했습니다 — {ex.Message}", "w"); }
        }

        JsonObject? mainCfg = null;
        try
        {
            var p = ClaudeConfigJson(main);
            if (File.Exists(p)) mainCfg = JsonNode.Parse(ReadShared(p)) as JsonObject;
        }
        catch { /* 못 읽으면 MCP·첫 준비만 건너뛴다 */ }

        try
        {
            var mcpFile = ClaudeMcpFile(home);
            if (mainCfg?["mcpServers"] is JsonObject servers && servers.Count > 0)
                WriteIfChanged(mcpFile, new JsonObject { ["mcpServers"] = servers.DeepClone() }.ToJsonString(Indented));
            else if (File.Exists(mcpFile)) File.Delete(mcpFile);
        }
        catch (Exception ex) { log?.Invoke($"계정 폴더에 MCP 목록을 적지 못했습니다 — {ex.Message}", "w"); }

        var cfg = ClaudeConfigJson(home);
        if (!File.Exists(cfg))
        {
            try
            {
                var seed = new JsonObject { ["hasCompletedOnboarding"] = true };
                foreach (var key in new[] { "lastOnboardingVersion", "lastReleaseNotesSeen", "installMethod", "autoUpdates" })
                    if (mainCfg?[key] is { } v) seed[key] = v.DeepClone();

                // 기본 계정이 이미 믿은 폴더는 이 계정도 믿는다 — 넘기자마자 "이 폴더를 믿습니까" 가 뜨지 않게.
                if (mainCfg?["projects"] is JsonObject projects)
                {
                    var trusted = new JsonObject();
                    foreach (var (path, node) in projects)
                        if (node?["hasTrustDialogAccepted"] is JsonValue t && t.GetValueKind() == JsonValueKind.True)
                            trusted[path] = new JsonObject { ["hasTrustDialogAccepted"] = true };
                    if (trusted.Count > 0) seed["projects"] = trusted;
                }
                WriteIfChanged(cfg, seed.ToJsonString(Indented));
            }
            catch (Exception ex) { log?.Invoke($"계정 폴더의 첫 설정을 적지 못했습니다 — {ex.Message}", "w"); }
        }
    }

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void CopyIfChanged(string src, string dst)
    {
        var bytes = File.ReadAllBytes(src);
        if (File.Exists(dst) && File.ReadAllBytes(dst).AsSpan().SequenceEqual(bytes)) return;
        var tmp = dst + ".agentclihub.tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, dst, overwrite: true);
    }

    private static void WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        var tmp = path + ".agentclihub.tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    // ── codex: 홈 ───────────────────────────────────────────────────────────

    /// <summary>이 홈에 로그인이 있는가. auth.json 에 토큰이나 API 키가 있으면 있다고 본다.</summary>
    public static bool CodexLoggedIn(string home)
    {
        try
        {
            var path = Path.Combine(home, "auth.json");
            if (!File.Exists(path)) return false;
            var o = JsonNode.Parse(ReadShared(path)) as JsonObject;
            return o?["tokens"] is JsonObject || o?["OPENAI_API_KEY"]?.GetValueKind() == JsonValueKind.String;
        }
        catch { return false; }
    }

    /// <summary>
    /// 이 홈의 로그인 이메일. id_token(JWT) 안의 것을 **검증 없이** 읽는다 — 이름표로만 쓰기 때문이다.
    /// </summary>
    public static string CodexEmail(string home) => CodexClaims(home)?["email"]?.ToString() ?? "";

    /// <summary>
    /// 사용량 조회에 쓸 것 — 접근 토큰과 ChatGPT 계정 번호. 로그인이 없으면 null.
    /// **토큰을 갱신하지 않는다.** 갱신은 codex 자신의 일이다 — 우리가 하면 갱신 토큰이 바뀌어
    /// codex 가 들고 있는 것이 무효가 되고, 그 계정이 로그아웃된다.
    /// </summary>
    public static (string AccessToken, string AccountId)? CodexAuth(string home)
    {
        try
        {
            var o = JsonNode.Parse(ReadShared(Path.Combine(home, "auth.json"))) as JsonObject;
            var t = o?["tokens"] as JsonObject;
            var access = t?["access_token"]?.ToString() ?? "";
            var acct = t?["account_id"]?.ToString() ?? "";
            if (acct.Length == 0)
                acct = CodexClaims(home)?["https://api.openai.com/auth"]?["chatgpt_account_id"]?.ToString() ?? "";
            return access.Length > 0 && acct.Length > 0 ? (access, acct) : null;
        }
        catch { return null; }
    }

    private static JsonObject? CodexClaims(string home)
    {
        try
        {
            var o = JsonNode.Parse(ReadShared(Path.Combine(home, "auth.json"))) as JsonObject;
            var jwt = o?["tokens"]?["id_token"]?.ToString() ?? "";
            var parts = jwt.Split('.');
            if (parts.Length != 3) return null;
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64))) as JsonObject;
        }
        catch { return null; }
    }

    /// <summary>
    /// 기본 홈과 **같이 쓸** 폴더. 계정 홈에서 이것들이 비어 있으면 그 계정의 codex 는 다른 프로그램처럼 뜬다
    /// (스킬·플러그인이 없다). 샌드박스 셋은 **반드시** 같이 써야 한다 — 따로 두면 그 계정의 codex 가
    /// 샌드박스를 새로 세우면서 같은 Windows 사용자(CodexSandbox*)의 비밀번호를 바꾸고, 그러면 기본 홈에
    /// 적힌 비밀번호가 틀려져 기본 계정 쪽 명령 실행이 막힌다. 다시 세우면 이번엔 반대쪽이 막힌다.
    /// </summary>
    private static readonly string[] SharedDirs =
        ["skills", "plugins", "prompts", "rules", ".sandbox", ".sandbox-bin", ".sandbox-secrets"];

    /// <summary>없을 때만 한 번 떠 오는 파일. 계정 홈에서 바뀌어도 기본 홈을 건드리지 않는다.</summary>
    private static readonly string[] SeedFiles = ["cap_sid", ".sandbox_migration", "models_cache.json"];

    /// <summary>
    /// codex 계정 홈을 **띄울 때마다** 맞춘다(로그인 전에도). 기본 홈이 기준이다.
    ///
    ///   * config.toml — 기본 홈 것을 그대로 따른다. 이 계정에서만 믿은 폴더(<c>[projects.…]</c>)는
    ///                   살려 둔다 — 안 그러면 띄울 때마다 "이 폴더를 믿습니까" 를 다시 묻는다.
    ///   * AGENTS.md   — 기본 홈 것을 그대로 따른다.
    ///   * 폴더        — 연결(junction)로 같이 쓴다(<see cref="SharedDirs"/>).
    ///   * 쪽지        — <c>-p agentclihub</c> 가 이 홈에서 찾으므로 여기에도 쓴다(<see cref="AgentBrief"/>).
    ///                   <c>-p</c> 로 뜬 codex 는 폴더 신뢰·샌드박스 선택을 config.toml 이 아니라 **이 파일에** 적는다 —
    ///                   쪽지만 고쳐 쓰고 그것들은 살린다(<see cref="AgentBrief.MergeCodexConfig"/>).
    ///
    /// **auth.json 은 절대 건드리지 않는다.** 그것이 곧 그 계정이다.
    /// 실패는 기록만 하고 넘어간다 — 준비가 덜 됐어도 로그인과 대화는 된다.
    /// </summary>
    public static void PrepareCodexHome(string home, Action<string, string>? log = null)
    {
        var main = CliProfile.CodexHome;
        if (string.Equals(Path.GetFullPath(main).TrimEnd('\\'), Path.GetFullPath(home).TrimEnd('\\'),
                          StringComparison.OrdinalIgnoreCase)) return;   // 기본 홈은 손대지 않는다

        // 같은 계정의 탭 둘이 동시에 뜨면(복원 · 넘기기) 연결을 둘이 같이 만들려다 한쪽이 실패한다.
        lock (_prepLock) PrepareCore(main, home, log);
    }

    private static readonly object _prepLock = new();

    private static void PrepareCore(string main, string home, Action<string, string>? log)
    {
        Directory.CreateDirectory(home);

        try { SyncConfig(main, home); }
        catch (Exception ex) { log?.Invoke($"계정 홈의 config.toml 을 맞추지 못했습니다 — {ex.Message}", "w"); }

        try
        {
            var agents = Path.Combine(main, "AGENTS.md");
            if (File.Exists(agents)) File.Copy(agents, Path.Combine(home, "AGENTS.md"), overwrite: true);
        }
        catch (Exception ex) { log?.Invoke($"계정 홈에 AGENTS.md 를 옮기지 못했습니다 — {ex.Message}", "w"); }

        foreach (var name in SharedDirs)
        {
            var src = Path.Combine(main, name);
            var dst = Path.Combine(home, name);
            if (!Directory.Exists(src) || Directory.Exists(dst) || File.Exists(dst)) continue;
            try { Junction(dst, src); }
            catch (Exception ex) { log?.Invoke($"계정 홈에 {name} 를 잇지 못했습니다 — {ex.Message}", "w"); }
        }

        foreach (var name in SeedFiles)
        {
            var src = Path.Combine(main, name);
            var dst = Path.Combine(home, name);
            if (!File.Exists(src) || File.Exists(dst)) continue;
            try { File.Copy(src, dst); } catch { /* 없으면 codex 가 스스로 만든다 */ }
        }

        AgentBrief.WriteCodexProfile(home);
    }

    /// <summary>
    /// config.toml 을 기본 홈 것으로 맞추되, **이 계정 홈에만 있는 <c>[projects.…]</c> 표는 뒤에 붙여 살린다.**
    /// TOML 을 통째로 해석하지 않는다 — 표 머리(<c>[</c> 로 시작하는 줄) 단위로만 자른다.
    /// 머리가 기본 홈에 이미 있으면 기본 홈 쪽이 이긴다(같은 표를 두 번 적으면 codex 가 설정을 못 읽는다).
    /// </summary>
    internal static void SyncConfig(string main, string home)
    {
        var src = Path.Combine(main, "config.toml");
        var dst = Path.Combine(home, "config.toml");
        if (!File.Exists(src)) return;

        var mainText = File.ReadAllText(src).Replace("\r\n", "\n");
        var mine = File.Exists(dst) ? File.ReadAllText(dst).Replace("\r\n", "\n") : "";

        var mainHeads = TableHeads(mainText);
        var extra = ProjectTables(mine).Where(t => !mainHeads.Contains(t.Head)).ToList();

        var sb = new StringBuilder(mainText.TrimEnd('\n')).Append('\n');
        if (extra.Count > 0)
        {
            sb.Append('\n').Append(ExtraMarker).Append('\n');
            foreach (var t in extra) sb.Append('\n').Append(t.Text.TrimEnd('\n')).Append('\n');
        }

        var text = sb.ToString();
        if (text == mine) return;
        var tmp = dst + ".agentclihub.tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, dst, overwrite: true);
    }

    private const string ExtraMarker = "# ── AgentCLIHub: 이 계정에서만 믿은 폴더(기본 홈 config.toml 뒤에 붙임) ──";
    private const string LegacyExtraMarker = "# ── LLM Tools: 이 계정에서만 믿은 폴더(기본 홈 config.toml 뒤에 붙임) ──";

    private static HashSet<string> TableHeads(string toml) =>
        toml.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('[')).ToHashSet(StringComparer.Ordinal);

    private static List<(string Head, string Text)> ProjectTables(string toml)
    {
        var list = new List<(string, string)>();
        string? head = null;
        var cur = new StringBuilder();
        void Flush()
        {
            if (head is not null && head.StartsWith("[projects.", StringComparison.Ordinal))
                list.Add((head, cur.ToString()));
            head = null;
            cur.Clear();
        }
        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                Flush();
                head = line;
            }
            // 우리가 붙인 표식 줄은 표 내용이 아니다 — 다시 붙일 때 두 번 쌓이지 않게 뺀다.
            if (head is not null && line != ExtraMarker && line != LegacyExtraMarker) cur.Append(raw).Append('\n');
        }
        Flush();
        return list;
    }

    /// <summary>
    /// 디렉터리 연결(junction). 심볼릭 링크와 달리 **관리자 권한이 필요 없다.**
    /// .NET 에는 junction 을 만드는 API 가 없어 <c>mklink /J</c> 를 부른다.
    /// </summary>
    private static void Junction(string link, string target)
    {
        // **역슬래시로 편다.** mklink 는 `/` 로 시작하는 토막을 옵션으로 읽는다 — `D:/tmp/x` 를 주면
        // "구문이 틀립니다 - tmp" 로 실패한다(실측). 사용자가 CLAUDE_CONFIG_DIR · CODEX_HOME 을 슬래시로 적어 두면 그렇게 온다.
        link = Path.GetFullPath(link);
        target = Path.GetFullPath(target);

        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("mklink 를 띄우지 못했습니다.");
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } throw new TimeoutException("mklink 가 끝나지 않았습니다."); }
        if (p.ExitCode != 0 || !Directory.Exists(link))
            throw new IOException(err.Trim().Length > 0 ? err.Trim() : $"mklink 실패(코드 {p.ExitCode})");
    }

    /// <summary>CLI 가 쥐고 있을 수 있는 파일을 읽는다(공유 모드).</summary>
    private static string ReadShared(string path)
    {
        using var st = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var rd = new StreamReader(st);
        return rd.ReadToEnd();
    }
}
