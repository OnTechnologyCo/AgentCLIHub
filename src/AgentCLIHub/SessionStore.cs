using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCLIHub;

/// <summary>지난 대화 하나. [↻ 이어하기] 목록의 한 줄이 된다.</summary>
/// <param name="Backend">어느 CLI 의 기록인가. 이어갈 때 그 CLI 로 띄워야 한다.</param>
/// <param name="Id">세션 번호. `claude --resume &lt;id&gt;` · `codex resume &lt;id&gt;` 에 그대로 들어간다.</param>
/// <param name="Cwd">그때 일하던 폴더. 같은 폴더에서 이어가야 파일 경로가 맞는다.</param>
/// <param name="When">마지막으로 손댄 시각.</param>
/// <param name="Title">첫 발화 한 줄. 이것이 없으면 목록이 세션 번호 나열이 된다.</param>
/// <param name="Account">
/// 그 기록이 있는 계정(<see cref="AgentCLIHub.Account.Id"/>, 빈 값 = 기본 계정). codex 는 계정마다 홈이 달라
/// 기록도 그 홈에 있다 — 이어갈 때 **그 계정으로** 띄워야 codex 가 기록을 찾는다.
/// claude 는 계정별 입력 기록으로 공유 projects 안의 대화도 분류한다.
/// </param>
public sealed record SessionEntry(string Backend, string Id, string Cwd, DateTime When, string Title, string Account = "",
                                  string? TranscriptPath = null, bool Shared = false);

/// <summary>
/// 지난 대화 목록을 읽는다. **우리가 따로 적어 두지 않는다** — 두 CLI 가 이미
/// 자기 홈에 기록을 남기고, 이어가기도 그 기록으로 하기 때문이다. 우리가 따로 적으면
/// 두 목록이 어긋나고, 어긋난 쪽은 "목록에 있는데 열리지 않는 대화" 로만 보인다.
///
/// **전부 읽지 않는다.** 오래 쓴 PC 에는 파일이 수천 개고 하나가 수 MB 다.
/// 최근 것부터, 파일당 앞부분만 읽는다 — 필요한 것(폴더 · 첫 발화)은 다 앞에 있다.
/// </summary>
public static class SessionStore
{
    /// <summary>파일 하나에서 읽을 최대 바이트. 첫 발화까지 오는 데 이 정도면 넉넉하다.</summary>
    private const int HeadBytes = 128 * 1024;

    /// <summary>훑을 파일 수의 상한. CLI 마다 이만큼씩 본다.</summary>
    private const int ScanFiles = 120;

    /// <summary>목록에 보여 줄 제목 길이.</summary>
    private const int TitleLen = 110;

    /// <summary>
    /// CLI·계정마다 최근 max개를 읽고 시각 내림차순으로 합친다.
    /// </summary>
    public static IReadOnlyList<SessionEntry> Recent(Settings s, int max = 60)
    {
        var all = new List<SessionEntry>();
        var claudeHomes = new[] { (Home: CliProfile.ClaudeHome, Account: "") }
            .Concat(s.Accounts.Where(a => CliProfile.For(a.Backend) == CliProfile.Claude)
                .Select(a => (Home: Accounts.ClaudeHomeOf(a.Id), Account: a.Id)));
        all.AddRange(ClaudeAccounts(claudeHomes, s.Tabs, max));
        try { all.AddRange(Codex(CliProfile.CodexHome, "")); } catch { }
        try { all.AddRange(AntigravitySessions.Recent()); } catch { }
        foreach (var a in s.Accounts.Where(a => CliProfile.For(a.Backend) == CliProfile.Antigravity))
            try { all.AddRange(AntigravitySessions.Recent(AntigravityAccounts.HomeOf(a.Id)).Select(e => e with { Account = a.Id })); } catch { }
        foreach (var a in s.Accounts.Where(a => CliProfile.For(a.Backend) == CliProfile.Codex))
            try { all.AddRange(Codex(Accounts.CodexHomeOf(a.Id), a.Id)); } catch { }
        return all.GroupBy(e => (e.Backend, e.Account, e.Shared))
            .SelectMany(g => g.OrderByDescending(e => e.When).Take(max))
            .OrderByDescending(e => e.When).ToList();
    }

    // ── claude ──────────────────────────────────────────────────────────────

    /// <summary>
    /// `~/.claude/projects/&lt;폴더이름&gt;/&lt;세션번호&gt;.jsonl`.
    ///
    /// **폴더 이름에서 경로를 되돌리지 않는다.** claude 는 `D:\99. Temp\X` 를
    /// `D--99--Temp-X` 로 접는데, 점·공백·역슬래시가 전부 대시가 되어 되돌릴 방법이 없다.
    /// 대신 파일 안의 `cwd` 를 읽는다 — 거기에는 원본이 그대로 들어 있다.
    /// </summary>
    public static IEnumerable<SessionEntry> Claude(string home, string account = "") =>
        ClaudeAccounts([(home, account)], [], ScanFiles);

    private sealed record ClaudeActivity(string Cwd, string Title, DateTime When);

    // projects can be shared, but native history.jsonl remains local to the account that used the session.
    public static IReadOnlyList<SessionEntry> ClaudeAccounts(IEnumerable<(string Home, string Account)> homes,
        IEnumerable<TabState> tabs, int max = 60)
    {
        var profiles = new List<(string Root, string Account, Dictionary<string, ClaudeActivity> Used)>();
        var roots = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (home, account) in homes)
        {
            try
            {
                var root = HistoryPaths.ClaudeProjects(home);
                if (!roots.ContainsKey(root)) roots[root] = ClaudeFiles(root);
                var used = ClaudeHistory(home);
                foreach (var tab in tabs.Where(t => t.Backend == "claude_code" && t.Account == account && Guid.TryParse(t.SessionId, out _)))
                    used.TryAdd(tab.SessionId!, new(tab.Cwd, "", DateTime.MinValue));
                profiles.Add((root, account, used));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var files = roots.SelectMany(r => r.Value).GroupBy(f => Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.LastWriteTimeUtc).ToArray(), StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(FileInfo File, string Account, ClaudeActivity? Activity, bool Shared)>();
        foreach (var profile in profiles)
            foreach (var (id, activity) in profile.Used)
                if (files.TryGetValue(id, out var copies))
                {
                    var file = copies.FirstOrDefault(f => Path.GetRelativePath(profile.Root, f.FullName) is var relative &&
                        relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative)) ?? copies[0];
                    candidates.Add((file, profile.Account, activity, false)); claimed.Add(id);
                }
        foreach (var (root, entries) in roots)
        {
            var owners = profiles.Where(p => string.Equals(p.Root, root, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (owners.Length == 0) continue;
            foreach (var file in entries.Where(f => !claimed.Contains(Path.GetFileNameWithoutExtension(f.Name))))
                candidates.Add((file, owners[0].Account, null, owners.Length > 1));
        }
        var result = new List<SessionEntry>();
        var parsed = new Dictionary<string, SessionEntry?>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.GroupBy(c => (c.Account, c.Shared)))
        {
            var count = 0;
            foreach (var candidate in group.OrderByDescending(c => c.File.LastWriteTimeUtc)
                .DistinctBy(c => Path.GetFileNameWithoutExtension(c.File.Name), StringComparer.OrdinalIgnoreCase))
            {
                if (count >= max) break;
                if (!parsed.TryGetValue(candidate.File.FullName, out var entry))
                    parsed[candidate.File.FullName] = entry = ReadClaude(candidate.File);
                var cwd = entry?.Cwd ?? candidate.Activity?.Cwd ?? "";
                if (cwd.Length == 0) continue;
                var title = entry?.Title is { Length: > 0 } text ? text : candidate.Activity?.Title ?? "";
                result.Add(new("claude_code", Path.GetFileNameWithoutExtension(candidate.File.Name), cwd,
                    candidate.File.LastWriteTime, Fallback(title, cwd), candidate.Account, candidate.File.FullName, candidate.Shared));
                count++;
            }
        }
        return result.OrderByDescending(e => e.When).ToList();
    }

    private static List<FileInfo> ClaudeFiles(string root)
    {
        var files = new List<FileInfo>();
        if (!Directory.Exists(root)) return files;
        foreach (var dir in Directory.EnumerateDirectories(root))
            try
            {
                files.AddRange(new DirectoryInfo(HistoryPaths.PhysicalDirectory(dir)).EnumerateFiles("*.jsonl")
                    .Where(f => Guid.TryParse(Path.GetFileNameWithoutExtension(f.Name), out _)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        return files;
    }

    private static Dictionary<string, ClaudeActivity> ClaudeHistory(string home)
    {
        var result = new Dictionary<string, ClaudeActivity>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = new FileStream(Path.Combine(home, "history.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var node = doc.RootElement;
                    var id = node.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String ? sid.GetString() : null;
                    if (!Guid.TryParse(id, out _)) continue;
                    var cwd = node.TryGetProperty("project", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
                    var title = node.TryGetProperty("display", out var d) && d.ValueKind == JsonValueKind.String ? Clean(d.GetString() ?? "") : "";
                    var when = node.TryGetProperty("timestamp", out var t) && t.TryGetInt64(out var ms)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : DateTime.MinValue;
                    if (!result.TryGetValue(id!, out var old) || when >= old.When) result[id!] = new(cwd, title, when);
                }
                catch (JsonException) { }
                catch (InvalidOperationException) { }
                catch (ArgumentOutOfRangeException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    private static SessionEntry? ReadClaude(FileInfo f)
    {
        try
        {
            var head = Head(f.FullName);

            string cwd = "", title = "";
            foreach (var line in head.Split('\n'))
            {
                if (line.Length < 2) continue;
                JsonObject? o;
                try { o = JsonNode.Parse(line) as JsonObject; } catch { continue; }   // 잘린 마지막 줄
                if (o is null) continue;

                if (cwd.Length == 0 && o["cwd"]?.GetValue<string>() is { Length: > 0 } c) cwd = c;

                if (title.Length == 0 && o["type"]?.GetValue<string>() == "user")
                    title = Clean(TextOf(o["message"]?["content"]));

                if (cwd.Length > 0 && title.Length > 0) break;
            }

            if (cwd.Length == 0) return null;
            return new SessionEntry("claude_code", Path.GetFileNameWithoutExtension(f.Name), cwd, f.LastWriteTime, title);
        }
        catch { return null; }
    }

    // ── codex ───────────────────────────────────────────────────────────────

    /// <summary>
    /// `$CODEX_HOME/sessions/&lt;년&gt;/&lt;월&gt;/&lt;일&gt;/rollout-*.jsonl`.
    /// 첫 줄(session_meta)에 세션 번호와 폴더가 같이 들어 있다.
    /// </summary>
    private static IEnumerable<SessionEntry> Codex(string home, string account)
    {
        var root = Path.Combine(home, "sessions");
        if (!Directory.Exists(root)) yield break;

        var files = new DirectoryInfo(root)
            .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(ScanFiles);

        foreach (var f in files)
        {
            string head;
            try { head = Head(f.FullName); } catch { continue; }

            string id = "", cwd = "", title = "";
            foreach (var line in head.Split('\n'))
            {
                if (line.Length < 2) continue;
                JsonObject? o;
                try { o = JsonNode.Parse(line) as JsonObject; } catch { continue; }
                var p = o?["payload"] as JsonObject;
                if (p is null) continue;

                if (o!["type"]?.GetValue<string>() == "session_meta")
                {
                    id = p["session_id"]?.GetValue<string>() ?? p["id"]?.GetValue<string>() ?? "";
                    cwd = p["cwd"]?.GetValue<string>() ?? "";
                }
                else if (title.Length == 0 &&
                         p["type"]?.GetValue<string>() == "message" &&
                         p["role"]?.GetValue<string>() == "user")
                {
                    title = Clean(TextOf(p["content"]));
                }

                if (id.Length > 0 && cwd.Length > 0 && title.Length > 0) break;
            }

            if (id.Length == 0 || cwd.Length == 0) continue;
            yield return new SessionEntry("codex_cli", id, cwd, f.LastWriteTime, Fallback(title, cwd), account);
        }
    }

    // ── 공통 ────────────────────────────────────────────────────────────────

    private static string Head(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[Math.Min(HeadBytes, (int)Math.Min(s.Length, int.MaxValue))];
        var n = s.Read(buf, 0, buf.Length);
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    /// <summary>
    /// 발화 본문을 뽑는다. 두 CLI 모두 문자열이거나 `[{type,text}]` 배열이다 —
    /// 도구 결과·이미지 조각은 글자가 없으므로 자연히 빠진다.
    /// </summary>
    private static string TextOf(JsonNode? content)
    {
        if (content is null) return "";
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (content is not JsonArray arr) return "";

        var sb = new StringBuilder();
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            var t = o["text"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(t)) sb.Append(t).Append(' ');
            if (sb.Length > TitleLen * 4) break;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 사람이 실제로 친 말만 남긴다.
    ///
    /// **첫 발화가 곧 사람의 말인 것은 아니다.** 두 CLI 모두 세션을 열면서 지침 파일과
    /// 시스템 안내를 사용자 발화처럼 밀어 넣는다(`&lt;system-reminder&gt;` · `# AGENTS.md` ·
    /// `Caveat:` · `&lt;command-name&gt;`). 그대로 쓰면 목록이 전부 같은 문구로 보여
    /// 어느 대화가 무엇이었는지 구별이 안 된다. 그래서 그런 줄은 버리고 다음을 본다.
    /// </summary>
    private static string Clean(string raw)
    {
        var t = (raw ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        if (t.Length == 0) return "";

        if (t.StartsWith('<') || t.StartsWith("# AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("# CLAUDE.md", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("Caveat:", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("[Request interrupted", StringComparison.OrdinalIgnoreCase))
            return "";

        return t.Length > TitleLen ? t[..TitleLen] + "…" : t;
    }

    /// <summary>제목을 못 건졌으면 폴더 이름이라도 준다 — 빈 줄보다는 낫다.</summary>
    private static string Fallback(string title, string cwd) =>
        title.Length > 0 ? title
                         : "(첫 발화 없음) " + (Path.GetFileName(Path.TrimEndingDirectorySeparator(cwd)) ?? cwd);

    /// <summary>화면으로 보낼 형태.</summary>
    public static object[] ForUi(Settings s, int max = 60) => ForUi(s, Recent(s, max));

    public static object[] ForUi(Settings s, IEnumerable<SessionEntry> entries) => entries.Select(e => (object)new
    {
        backend = e.Backend,
        cli = CliProfile.ForOrDefault(e.Backend).Display,
        id = e.Id,
        cwd = e.Cwd,
        folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(e.Cwd)),
        when = e.When.ToString("yyyy-MM-dd HH:mm"),
        title = e.Title,
        account = e.Account,
        shared = e.Shared,
        accountName = Accounts.View(s, CliProfile.ForOrDefault(e.Backend), e.Account).Label,
    }).ToArray();
}
