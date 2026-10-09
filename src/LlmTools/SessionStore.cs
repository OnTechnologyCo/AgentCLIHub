using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmTools;

/// <summary>지난 대화 하나. [↻ 이어하기] 목록의 한 줄이 된다.</summary>
/// <param name="Backend">어느 CLI 의 기록인가. 이어갈 때 그 CLI 로 띄워야 한다.</param>
/// <param name="Id">세션 번호. `claude --resume &lt;id&gt;` · `codex resume &lt;id&gt;` 에 그대로 들어간다.</param>
/// <param name="Cwd">그때 일하던 폴더. 같은 폴더에서 이어가야 파일 경로가 맞는다.</param>
/// <param name="When">마지막으로 손댄 시각.</param>
/// <param name="Title">첫 발화 한 줄. 이것이 없으면 목록이 세션 번호 나열이 된다.</param>
/// <param name="Account">
/// 그 기록이 있는 계정(<see cref="LlmTools.Account.Id"/>, 빈 값 = 기본 계정). codex 는 계정마다 홈이 달라
/// 기록도 그 홈에 있다 — 이어갈 때 **그 계정으로** 띄워야 codex 가 기록을 찾는다.
/// claude 는 계정과 무관하게 한 곳(<c>~/.claude</c>)에 쌓이므로 늘 빈 값이다.
/// </param>
public sealed record SessionEntry(string Backend, string Id, string Cwd, DateTime When, string Title, string Account = "");

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
    /// 최근 대화. 두 CLI 를 합쳐 시각 내림차순으로 준다. codex 는 **계정 홈마다** 본다 —
    /// 다른 계정으로 넘겨 이어간 대화는 그 계정 홈에만 있다.
    /// </summary>
    public static IReadOnlyList<SessionEntry> Recent(Settings s, int max = 60)
    {
        var all = new List<SessionEntry>();
        try { all.AddRange(Claude()); } catch { /* 한쪽을 못 읽어도 다른 쪽은 보여 준다 */ }
        try { all.AddRange(Codex(CliProfile.CodexHome, "")); } catch { }
        try { all.AddRange(AntigravitySessions.Recent()); } catch { }
        foreach (var a in s.Accounts.Where(a => CliProfile.For(a.Backend) == CliProfile.Codex))
            try { all.AddRange(Codex(Accounts.CodexHomeOf(a.Id), a.Id)); } catch { }
        return all.OrderByDescending(e => e.When).Take(max).ToList();
    }

    // ── claude ──────────────────────────────────────────────────────────────

    /// <summary>
    /// `~/.claude/projects/&lt;폴더이름&gt;/&lt;세션번호&gt;.jsonl`.
    ///
    /// **폴더 이름에서 경로를 되돌리지 않는다.** claude 는 `D:\99. Temp\X` 를
    /// `D--99--Temp-X` 로 접는데, 점·공백·역슬래시가 전부 대시가 되어 되돌릴 방법이 없다.
    /// 대신 파일 안의 `cwd` 를 읽는다 — 거기에는 원본이 그대로 들어 있다.
    /// </summary>
    private static IEnumerable<SessionEntry> Claude()
    {
        var root = Path.Combine(CliProfile.ClaudeHome, "projects");
        if (!Directory.Exists(root)) yield break;

        var files = new DirectoryInfo(root)
            .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(ScanFiles);

        foreach (var f in files)
        {
            string head;
            try { head = Head(f.FullName); } catch { continue; }

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

            if (cwd.Length == 0) continue;   // 폴더를 모르면 이어갈 수가 없다
            yield return new SessionEntry("claude_code", Path.GetFileNameWithoutExtension(f.Name),
                                          cwd, f.LastWriteTime, Fallback(title, cwd));
        }
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
    public static object[] ForUi(Settings s, int max = 60) => Recent(s, max).Select(e => (object)new
    {
        backend = e.Backend,
        cli = CliProfile.ForOrDefault(e.Backend).Display,
        id = e.Id,
        cwd = e.Cwd,
        folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(e.Cwd)),
        when = e.When.ToString("yyyy-MM-dd HH:mm"),
        title = e.Title,
        account = e.Account,
        accountName = e.Account.Length > 0 ? Accounts.View(s, CliProfile.ForOrDefault(e.Backend), e.Account).Label : "",
    }).ToArray();
}
