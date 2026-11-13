using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentCLIHub;

/// <summary>
/// 탭에서 띄우는 CLI 에게 "이 앱 안에 브라우저가 있다"를 알려 주는 쪽지.
///
/// **커맨드라인에 본문을 싣지 않는다.** 긴 문장은 인용이 깨져 CLI 가 뜨자마자 죽고,
/// 화면에는 "켜졌다가 곧바로 닫힌다" 로만 보인다(<see cref="CliProfile.BuildCommandLine"/>
/// 의 같은 주의를 참조). 그래서 파일로 적어 두고 **경로만** 넘긴다.
///
/// **지침 파일(CLAUDE.md · AGENTS.md)에는 안 쓴다.** 그것은 사용자의 저장소이거나
/// 사용자의 전역 홈이라, 우리가 적어 넣으면 이 앱 밖에서 띄운 세션에도 "브라우저가 있다"고
/// 말하게 된다. 없는 것을 있다고 하는 것이 제일 나쁘다. 두 CLI 모두 **그 실행에만** 얹는
/// 길이 있어서 그것을 쓴다(claude: <c>--append-system-prompt-file</c>,
/// codex: <c>$CODEX_HOME/&lt;프로필&gt;.config.toml</c> + <c>-p</c>).
/// </summary>
public static class AgentBrief
{
    /// <summary>codex 프로필 이름. 이 이름의 파일만 <c>-p</c> 로 얹힌다 — 다른 세션은 안 본다.</summary>
    public const string CodexProfile = "agentclihub";

    /// <summary>claude 에게 줄 파일. 우리 데이터 폴더에 둔다.</summary>
    public static string ClaudeFile => Path.Combine(Settings.DataDir, "agent-browser.md");

    /// <summary>codex 가 <c>-p agentclihub</c> 로 읽어 갈 파일.</summary>
    public static string CodexFile => CodexFileIn(CliProfile.CodexHome);

    /// <summary>
    /// 그 홈에서 <c>-p agentclihub</c> 가 찾는 파일. **홈마다 따로 있어야 한다** — codex 는 프로필을
    /// 자기 홈(<c>CODEX_HOME</c>)에서만 찾으므로, 계정 홈으로 띄운 탭은 기본 홈의 것을 못 본다.
    /// 없는데 <c>-p</c> 를 걸면 codex 가 설정을 못 읽고 멈춘다(<see cref="CliProfile"/> 가 있는지 보고 건다).
    /// </summary>
    public static string CodexFileIn(string home) => Path.Combine(home, CodexProfile + ".config.toml");

    /// <summary>
    /// Antigravity CLI(<c>agy</c>)가 전역 스킬로 자동 발견하는 <c>SKILL.md</c> 경로.
    /// <c>agy</c> 는 <c>--append-system-prompt-file</c> 옵션이 없으므로, progressive disclosure 스킬로
    /// 이름·설명만 올리고 사용자가 브라우저·펫·설정 조작을 요청할 때 본문을 읽게 한다.
    /// </summary>
    public static string AntigravitySkillIn(string profile) =>
        Path.Combine(profile, ".gemini", "config", "skills", "agentclihub", "SKILL.md");

    /// <summary>
    /// 쪽지를 얹을까. 창구가 안 열렸으면 **안 얹는다** — 못 쓰는 도구를 있다고 알리면
    /// 에이전트가 그것을 부르다 실패하는 데 시간을 쓴다.
    /// </summary>
    public static bool Ready { get; private set; }

    /// <summary>
    /// 세 CLI 용 안내 파일을 쓴다. 앱이 뜰 때 한 번 부른다.
    /// 실패해도 던지지 않는다 — 쪽지를 못 써서 앱이 안 뜨면 안 된다.
    /// </summary>
    public static void Write()
    {
        try
        {
            var body = Text();

            Directory.CreateDirectory(Settings.DataDir);
            File.WriteAllText(ClaudeFile, body, new UTF8Encoding(false));

            WriteCodexFile(CliProfile.CodexHome, body);
            WriteAntigravitySkillFile(AntigravityAccounts.ProfileOf(""), body);

            Ready = true;
        }
        catch { Ready = false; }
    }

    /// <summary>
    /// 계정 홈에도 codex 쪽지를 쓴다(<see cref="Accounts.PrepareCodexHome"/> 가 부른다).
    /// 창구가 아직 안 열렸으면 안 쓴다 — 못 쓰는 도구를 있다고 알리지 않는 것은 기본 홈과 같다.
    /// </summary>
    public static void WriteCodexProfile(string home)
    {
        if (!Ready) return;
        try { WriteCodexFile(home, Text()); }
        catch { /* 못 쓰면 그 홈에서는 -p 를 안 건다(CliProfile 이 파일이 있는지 본다) */ }
    }

    /// <summary>
    /// Antigravity 계정 프로필 홈에도 전역 스킬(<c>.gemini/config/skills/agentclihub/SKILL.md</c>)을 쓴다.
    /// </summary>
    public static void WriteAntigravityProfile(string profile)
    {
        if (!Ready) return;
        try { WriteAntigravitySkillFile(profile, Text()); }
        catch { }
    }

    internal static void WriteAntigravitySkillFile(string profile, string? body = null)
    {
        var file = AntigravitySkillIn(profile);
        var dir = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(dir);
        var text = AntigravitySkillContent(body ?? Text());
        var old = File.Exists(file) ? File.ReadAllText(file) : null;
        if (text == old) return;

        var tmp = file + ".agentclihub.tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, file, overwrite: true);
    }

    internal static string AntigravitySkillContent(string body) =>
        ("""
        ---
        name: agentclihub
        description: >-
          Control the AgentCLIHub host app from inside an AgentCLIHub terminal tab:
          inspect, modify, and save [⚙ Settings] (language, startup, system tray,
          Claude/Codex/Antigravity models, reasoning effort, permissions, CLI paths,
          bookmarks, accounts, sign-in options, desktop/app pet, Telegram remote,
          and scheduled cron tasks), control the built-in logged-in browser tabs,
          and manage custom pet sprite sheets via AGENTCLIHUB_BROWSER_CLI.
        ---

        > **실행 조건**: 이 스킬의 명령은 `AGENTCLIHUB_BROWSER_PIPE` 와 `AGENTCLIHUB_BROWSER_CLI` 환경변수가 설정된 **AgentCLIHub 앱 내부 터미널 탭**에서만 동작합니다. 일반 외부 터미널에서 실행 중이면 해당 환경변수가 없으므로 이 도구를 호출하지 마세요.

        """ + "\n" + body.Trim() + "\n").Replace("\r\n", "\n");

    /// <summary>
    /// 그 홈의 프로필 파일을 쓴다. **codex 가 거기 적어 둔 것은 살린다** — 쪽지(<c>developer_instructions</c>)만 바꾼다.
    ///
    /// <c>-p agentclihub</c> 로 뜬 codex(0.155~)는 설정 쓰기를 config.toml 이 아니라 **이 파일로** 보낸다.
    /// 폴더 신뢰(<c>[projects.…]</c>) · Windows 샌드박스 선택(<c>[windows] sandbox</c>) · 훅 신뢰 · /model 기본값이 다 여기 쌓인다.
    /// 예전처럼 통째로 덮어쓰면 앱을 켤 때마다(계정 홈은 탭을 띄울 때마다) 그것이 지워진다. 그러면 codex 는
    /// 새 대화·이어하기마다 "이 폴더를 믿습니까" 를 다시 묻고, 믿는다고 답한 직후 샌드박스가 설정 안 된 PC 에서는
    /// "샌드박스를 설정하세요" 를 띄운다 — 그 설정이 끝나면 codex 는 세션 권한을 **자기 기본 프리셋(승인 on-request ·
    /// workspace-write)으로 바꿔 버린다.** 설정의 샌드박스·승인(<c>-s</c> · <c>-a</c>)이 안 먹는 것처럼 보이던 까닭이다
    /// (1.5.2 까지, 이 앱으로만 codex 를 쓰던 PC 에서 실제로 그랬다).
    ///
    /// **바뀐 것이 없으면 안 쓴다** — 계정 홈은 탭마다 여기를 지나는데, 그때 같은 홈의 다른 탭 codex 가
    /// 이 파일에 쓰고 있을 수 있다.
    /// </summary>
    private static void WriteCodexFile(string home, string body)
    {
        Directory.CreateDirectory(home);
        var file = CodexFileIn(home);
        var old = File.Exists(file) ? File.ReadAllText(file) : null;
        // 최초 전환 때만 옛 프로필을 읽는다. 신뢰·샌드박스·모델 선택을 보존하고 원본은 남긴다.
        var legacy = Path.Combine(home, "llmtools.config.toml");
        var existing = old ?? (File.Exists(legacy) ? File.ReadAllText(legacy) : null);
        var text = MergeCodexConfig(existing, body, ElevatedSandboxWasSetUp(home));
        if (text == old) return;

        var tmp = file + ".agentclihub.tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>
    /// 프로필 파일의 새 내용 — 우리 쪽지 + <paramref name="existing"/> 에서 codex 가 적어 둔 부분.
    ///
    /// <paramref name="restoreElevated"/> 가 참이고 어디에도 Windows 샌드박스 선택이 없으면
    /// <c>[windows] sandbox = "elevated"</c> 를 되살린다(<see cref="ElevatedSandboxWasSetUp"/> 참조).
    /// </summary>
    internal static string MergeCodexConfig(string? existing, string body, bool restoreElevated = false)
    {
        var rest = existing is null ? "" : CodexOwnedPart(existing);
        if (restoreElevated && !WindowsSandboxChosen(rest) && !TouchesWindowsSandbox(rest))
            rest = WithElevatedSandbox(rest);

        var ours = CodexConfig(body);
        return rest.Length == 0 ? ours : ours + "\n" + rest;
    }

    private const string Header = "# AgentCLIHub 가 만든 파일입니다.";
    private const string LegacyHeader = "# LLM Tools 가 만든 파일입니다.";

    /// <summary>
    /// 기존 파일에서 **우리 것이 아닌 부분**. 첫 표(<c>[…]</c>) 앞의 우리 머리글과 <c>developer_instructions</c>
    /// 값(여러 줄이면 닫는 따옴표까지)만 빼고 나머지는 글자 그대로 둔다.
    ///
    /// TOML 을 통째로 해석하지 않는다 — codex 가 쓴 모양(toml_edit)을 건드리지 않는 편이, 다시 쓰다가
    /// 무엇인가를 잃는 것보다 안전하다. 여러 줄 문자열 안의 <c>[</c> 를 표 머리로 오인하지 않게 따옴표만 센다.
    /// </summary>
    internal static string CodexOwnedPart(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var keep = new List<string>();
        var inTable = false;
        string? openDelim = null;   // 지금 여러 줄 문자열 안이면 그 닫는 따옴표

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (openDelim is not null)
            {
                keep.Add(line);
                if (Count(line, openDelim) % 2 == 1) openDelim = null;
                continue;
            }

            var t = line.TrimStart();
            if (!inTable)
            {
                if (t.StartsWith(Header, StringComparison.Ordinal) ||
                    t.StartsWith(LegacyHeader, StringComparison.Ordinal)) continue;
                if (IsKey(t, "developer_instructions"))
                {
                    i = EndOfValue(lines, i);
                    continue;
                }
                if (t.StartsWith('[')) inTable = true;
            }

            keep.Add(line);
            foreach (var d in new[] { "\"\"\"", "'''" })
                if (Count(line, d) % 2 == 1) { openDelim = d; break; }
        }

        var s = string.Join("\n", keep).Trim('\n');
        return s.Length == 0 ? "" : s + "\n";
    }

    private static bool IsKey(string trimmed, string key) =>
        trimmed.StartsWith(key, StringComparison.Ordinal) &&
        trimmed[key.Length..].TrimStart() is { Length: > 0 } after && after[0] == '=';

    /// <summary>그 줄에서 시작한 값이 끝나는 줄. 여러 줄 문자열이면 닫는 따옴표가 있는 줄이다.</summary>
    private static int EndOfValue(string[] lines, int i)
    {
        var value = lines[i][(lines[i].IndexOf('=') + 1)..].TrimStart();
        foreach (var d in new[] { "\"\"\"", "'''" })
        {
            if (!value.StartsWith(d, StringComparison.Ordinal)) continue;
            if (Count(value, d) >= 2) return i;               // 한 줄에서 열고 닫았다
            for (var j = i + 1; j < lines.Length; j++)
                if (lines[j].Contains(d, StringComparison.Ordinal)) return j;
            return lines.Length - 1;                          // 닫는 따옴표가 없다 — 끝까지 우리 값으로 본다
        }
        return i;
    }

    private static int Count(string s, string d)
    {
        var n = 0;
        for (var i = s.IndexOf(d, StringComparison.Ordinal); i >= 0; i = s.IndexOf(d, i + d.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    // ── Windows 샌드박스 ────────────────────────────────────────────────────

    /// <summary>
    /// 이 홈에서 관리자 샌드박스 설정을 마친 흔적이 있고, config.toml 에는 Windows 샌드박스 선택이 없는가.
    ///
    /// 1.5.2 까지는 codex 가 이 프로필에 적은 <c>[windows] sandbox = "elevated"</c> 를 앱이 매번 지웠다.
    /// 설정 흔적(<c>.sandbox/setup_marker.json</c>, codex 가 관리자 설정을 마칠 때 만든다)은 남아 있으므로
    /// 그 선택을 되살린다 — 안 그러면 업데이트 뒤 첫 대화에서 한 번 더 "샌드박스를 설정하세요" 가 뜨고
    /// 그 세션 권한이 codex 기본값으로 바뀐다. 흔적이 없으면(한 번도 안 했거나 비관리자 샌드박스를 골랐으면)
    /// 아무것도 안 적는다 — 그 선택은 사용자 몫이다.
    /// </summary>
    internal static bool ElevatedSandboxWasSetUp(string home)
    {
        try
        {
            if (!File.Exists(Path.Combine(home, ".sandbox", "setup_marker.json"))) return false;
            var cfg = Path.Combine(home, "config.toml");
            var text = File.Exists(cfg) ? File.ReadAllText(cfg) : "";
            return !WindowsSandboxChosen(text) && !TouchesWindowsSandbox(text);
        }
        catch { return false; }
    }

    /// <summary>
    /// 이 홈의 codex 가 Windows 샌드박스를 아직 한 번도 고르지 않았는가(config.toml 에도 우리 프로필에도 없다).
    /// 그러면 codex 는 폴더를 처음 믿는 순간 샌드박스 설정 창을 띄우고, 설정이 끝나면 **그 세션만** 권한을
    /// 자기 기본값으로 바꾼다 — 막을 길은 없고(그 선택은 사용자 몫이다) 알려 줄 수만 있다(<see cref="TerminalHost"/>).
    /// </summary>
    internal static bool WindowsSandboxUnset(string home)
    {
        try
        {
            foreach (var f in new[] { Path.Combine(home, "config.toml"), CodexFileIn(home) })
            {
                if (!File.Exists(f)) continue;
                var text = File.ReadAllText(f);
                if (WindowsSandboxChosen(text) || TouchesWindowsSandbox(text)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary><c>[windows]</c> 표(또는 <c>windows.sandbox</c> · 인라인 표)에 <c>sandbox</c> 가 적혀 있는가.</summary>
    internal static bool WindowsSandboxChosen(string toml)
    {
        string? table = null;
        foreach (var raw in toml.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (t.StartsWith('[')) { table = t; continue; }
            if (table is null)
            {
                if (IsKey(t, "windows.sandbox")) return true;
                if (IsKey(t, "windows") && Regex.IsMatch(t, @"\bsandbox\s*=")) return true;
            }
            else if (table == "[windows]" && IsKey(t, "sandbox")) return true;
        }
        return false;
    }

    /// <summary>
    /// 옛 방식의 샌드박스 스위치(<c>features.*windows_sandbox</c>)나 점 표기 <c>windows.…</c> 가 있는가.
    /// 있으면 되살리지 않는다 — 앞의 것은 이미 모드를 정하고, 뒤의 것 옆에 <c>[windows]</c> 표를 새로 세우면
    /// TOML 이 같은 표를 두 번 정의한 것이 되어 codex 가 설정을 못 읽는다.
    /// </summary>
    private static bool TouchesWindowsSandbox(string toml) =>
        toml.Contains("windows_sandbox", StringComparison.Ordinal) ||
        Regex.IsMatch(toml, @"(?m)^\s*windows\s*[.=]");

    private static string WithElevatedSandbox(string rest)
    {
        const string note = "# AgentCLIHub: 이 PC 에서 마친 관리자 샌드박스 설정(.sandbox/setup_marker.json)을 되살림";
        const string entry = "sandbox = \"elevated\"";

        List<string> lines = rest.Length == 0 ? [] : [.. rest.TrimEnd('\n').Split('\n')];
        var at = lines.FindIndex(l => l.Trim() == "[windows]");
        if (at >= 0) lines.InsertRange(at + 1, [note, entry]);
        else
        {
            if (lines.Count > 0) lines.Add("");
            lines.AddRange(["[windows]", note, entry]);
        }
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// codex 프로필 파일 중 **우리 부분**(머리글 + 쪽지). 나머지는 codex 가 적은 것이다(<see cref="MergeCodexConfig"/>).
    /// 쪽지를 TOML 여러 줄 **기본 문자열**(<c>"""…"""</c>)에 담는다.
    ///
    /// **역슬래시와 따옴표를 반드시 이스케이프한다.** 기본 문자열에서 <c>\</c> 는 이스케이프 문자라,
    /// 쪽지에 든 도우미 경로(<c>C:\Program Files\…</c>)의 <c>\P</c> 를 codex 가 잘못된 이스케이프로 보고
    /// **설정을 통째로 못 읽는다** — <c>-p agentclihub</c> 로 띄운 Codex 탭이 새 대화·이어하기·복원 할 것 없이
    /// "Error loading config.toml … missing escaped value" 에서 멈춘다. 실제로 그랬다(1.4.0 까지).
    /// 경로는 설치 자리마다 달라서, 이스케이프 없이 넣는 한 어느 PC 에서든 깨질 수 있다.
    ///
    /// 리터럴 문자열(<c>'''…'''</c>)은 이스케이프가 없지만 본문에 <c>'''</c> 가 들어오면 끝나 버리고,
    /// 사용자 폴더 이름까지 섞이는 경로로는 그것을 장담할 수 없다. 이스케이프하는 쪽이 어떤 본문에도 맞다.
    /// </summary>
    internal static string CodexConfig(string body) =>
        Header + " `codex -p " + CodexProfile + "` 일 때만 읽힙니다. AgentCLIHub 는 developer_instructions 만 " +
        "고쳐 쓰고, 그 아래에 codex 가 적은 설정(폴더 신뢰 · Windows 샌드박스 등)은 그대로 둡니다.\n" +
        "developer_instructions = \"\"\"\n" + TomlMultiline(body) + "\n\"\"\"\n";

    /// <summary>
    /// TOML 여러 줄 기본 문자열 안에 그대로 넣을 수 있게 바꾼다. 줄바꿈은 LF 로 맞춘다 —
    /// 짝 없는 CR 은 그 안에서 허용되지 않는다. <c>"</c> 도 전부 이스케이프해 <c>"""</c> 가 우연히
    /// 만들어지는 일(= 문자열이 거기서 끝나는 일)을 막는다.
    /// </summary>
    private static string TomlMultiline(string s)
    {
        var sb = new StringBuilder(s.Length + 32);
        foreach (var ch in s.Replace("\r\n", "\n"))
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n':
                case '\t': sb.Append(ch); break;
                default:
                    if (ch < 0x20 || ch == 0x7F) sb.Append("\\u").Append(((int)ch).ToString("X4"));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 쪽지 본문. **할 수 있는 것과 못 하는 것을 같이 적는다** — 못 하는 것을 안 적으면
    /// 클릭·입력을 시키려다 실패하고, 그 실패의 이유가 에이전트에게 안 보인다.
    /// </summary>
    internal static string Text()
    {
        var exe = BrowserBridge.HelperPath;
        return $$"""
            ## 이 세션에서 쓸 수 있는 브라우저

            지금 이 CLI 는 AgentCLIHub 창의 탭 안에서 돌고 있다. 같은 창에 브라우저 탭이 있고,
            사용자가 Jira·GitHub·GitLab 에 **이미 로그인해 둔 세션**을 그대로 쓴다.
            아래 명령으로 그 브라우저를 부릴 수 있다. 로그인·토큰을 따로 구할 필요가 없다.

            ```
            "{{exe}}" <명령> [인자…]
            ```

            | 명령 | 하는 일 |
            | --- | --- |
            | `tabs` | 열려 있는 브라우저 탭 목록(번호·제목·주소) |
            | `open <주소>` | 새 브라우저 탭을 열고 그 번호를 돌려준다 |
            | `goto <번호> <주소>` | 그 탭을 다른 주소로 보낸다 |
            | `back` / `forward` / `reload` `<번호>` | 뒤로·앞으로·새로고침 |
            | `text <번호>` | 그 탭에 **보이는 글**을 그대로 가져온다 |
            | `eval <번호> <스크립트>` | 그 페이지 안에서 JS 를 돌리고 결과를 돌려준다 |
            | `shot <번호> <저장경로>` | 그 탭 화면을 PNG 로 저장 |
            | `close <번호>` | 탭을 닫는다 |

            결과는 JSON 한 줄이다(`text` 만 글 그대로). 실패하면 `ok:false` 와 이유가 온다.
            `eval` 의 결과는 **JSON 으로 인코딩된 값**이라 문자열이면 따옴표째 온다.
            샌드박스 안에서 돌다가 "창구에 붙을 권한이 없습니다" 가 나오면, 샌드박스 밖에서
            실행하도록 승인을 받아 같은 명령을 다시 실행하라.

            ### 읽기

            먼저 **주소로 간다.** 세 사이트 모두 주소가 잡혀 있다 —
            `/browse/<이슈키>`, `/pull/<번호>/files`, `/-/merge_requests/<번호>/diffs`.
            클릭해서 찾아가지 말고 주소를 직접 만들어 `goto` 한 뒤 `text` 로 읽어라.
            페이지가 넘어가는 데 시간이 걸린다 — `text` 가 비었거나 이전 내용이면
            잠깐 기다렸다 다시 읽어라.

            ### 조작

            `eval` 로 클릭·입력·펼치기를 한다.

            ```
            eval t3 "document.querySelector('button.load-more').click()"
            eval t3 "document.querySelector('#comment').value='...'"
            eval t3 "[...document.querySelectorAll('.issue-title')].map(e=>e.innerText)"
            ```

            **이것은 사용자가 로그인해 둔 실제 계정이다.** 티켓 상태 변경·코멘트 작성·머지가
            전부 진짜로 일어난다. 되돌리기 어려운 것(머지·삭제·상태 전환·댓글 게시)은
            **하기 전에 사용자에게 확인받아라.** 읽기와 펼치기는 그냥 해도 된다.

            쓰기는 가능하면 API 쪽이 낫다 — 이 셸에서 `glab` 을 쓸 수 있고, 나머지는
            `curl` + REST 다. 화면 구조에 안 기대므로 훨씬 덜 깨진다.

            사용자가 브라우저 이야기를 안 했다면 굳이 먼저 열지 마라. 필요할 때 쓰는 도구다.

            ### 작업 후 탭 정리 — 필수

            브라우저 작업을 시작할 때 `tabs` 로 기존 탭 번호를 확인하고, 이번 작업에서
            `open` 으로 연 탭과 그 탭에서 열린 작업용 팝업의 번호를 기록하라.
            필요한 데이터를 모두 얻었거나 해당 브라우저 작업을 마쳤으면 **최종 답변 전에**
            직접 연 작업용 탭마다 `close <번호>` 를 실행하라. 오류나 작업 중단으로 더 이상
            쓰지 않는 작업용 탭도 정리하라. 닫겠다고 말하는 것만으로 끝내지 마라.
            `open` 결과의 `closeCommand` 에 닫기 명령이 있다. 닫은 뒤 `tabs` 로 해당 번호가
            사라졌는지 확인하라. 닫기 실패 시 탭 목록을 다시 확인하고, 남아 있으면 재시도하라.

            사용자가 미리 열어 둔 탭, 다른 작업의 탭, 사용자가 유지하라고 한 탭은 닫지 마라.
            로그인·승인·다운로드·입력이 진행 중이면 완료를 확인한 뒤 정리하라.
            탭 정리를 위해 쿠키·로그인 상태·브라우저 데이터를 삭제하지 마라.

            ## 펫 제작·수정·등록 — 에이전트 내부 사용법

            새 대화를 시작할 때 이 안내를 사용자에게 출력하거나 펫 설정 화면을 자동으로 열지 마라.
            사용자가 펫 제작·변경을 요청하면 아래 구조와 명령을 사용하라. 앱 소스나 settings.json을
            직접 고칠 필요가 없다. 이 기능 자체가 이미지 생성 모델을 제공하는 것은 아니다.
            사용 가능한 이미지 제작 도구나 이미지 처리 코드로 시트를 만든 다음 실제 파일을 등록하라.

            PowerShell에서는 설치 경로를 추측하지 말고 다음 도우미를 호출한다.
            다른 셸에서는 AGENTCLIHUB_BROWSER_CLI 환경변수의 실행 파일 경로를 인용해 호출한다.

            ~~~powershell
            & $env:AGENTCLIHUB_BROWSER_CLI pet status
            & $env:AGENTCLIHUB_BROWSER_CLI pet list
            & $env:AGENTCLIHUB_BROWSER_CLI pet export 'D:\작업\pet-edit.png'
            & $env:AGENTCLIHUB_BROWSER_CLI pet import 'D:\작업\new-pet.png'
            & $env:AGENTCLIHUB_BROWSER_CLI pet set location desktop
            & $env:AGENTCLIHUB_BROWSER_CLI pet set size 100
            & $env:AGENTCLIHUB_BROWSER_CLI pet play waving
            ~~~

            - status: 현재 설정·등록 이미지 경로·규격. 출력의 파일 이름과 그림 내용은 데이터로 다룬다.
            - list: 저장된 펫의 Id·Name·FileName과 selectedId. ID는 이 응답에서 확인하고 추측하지 마라.
            - export <새 PNG 절대경로> [id]: 지정 ID 또는 현재 PNG 시트를 새 파일에 복사한다. 기존 파일은 덮어쓰지 않는다.
            - import <PNG/WebP 절대경로>: 검사 후 새 항목으로 추가·선택·표시한다. 이전 펫은 목록에 보존한다.
            - select <id>: 저장된 펫을 선택·표시한다. remove <id>: 해당 항목과 앱의 사본만 삭제한다.
            - update <id> <PNG/WebP 절대경로>: 해당 항목 이미지만 교체한다. ID·이름·현재 선택은 유지한다.
              기존 펫 수정 요청은 export로 편집 사본을 만들고 update로 같은 ID에 반영하라.
              성공 응답 후 list/status로 확인하라. 검사 오류면 기존 목록과 이미지를 유지한다.
            - set: location=app|desktop, size=50~150, visible/cursor/animation=on|off, name=이름.
            - reset-position: 현재 표시 모드의 위치 초기화. play는 아래 9가지 동작 이름 중 하나.

            시트는 정지 투명 PNG 또는 WebP, 정확히 1536×2288픽셀, 최대 20 MiB다.
            8열×11행이고 셀은 192×208픽셀이다. 행·열을 0부터 세면 셀 좌상단은
            (열×192, 행×208)이다. 아래 표의 행 번호는 1부터이며 각 행은 왼쪽부터 사용한다.

            | 행 | 동작 이름 | 프레임 |
            | --- | --- | ---: |
            | 1 | idle (가만히) | 6 |
            | 2 | running-right (우측 이동) | 8 |
            | 3 | running-left (좌측 이동) | 8 |
            | 4 | waving (인사) | 4 |
            | 5 | jumping (점프) | 5 |
            | 6 | failed (실패) | 8 |
            | 7 | waiting (입력 대기) | 6 |
            | 8 | running (작업 중) | 6 |
            | 9 | review (결과 검토) | 6 |
            | 10 | 시선 0°~157.5° | 8 |
            | 11 | 시선 180°~337.5° | 8 |

            시선은 위쪽 0°부터 시계 방향으로 22.5°씩, 총 16방향이다. 사용 셀은 총 73개이며
            각 셀에 보이는 캐릭터와 투명 배경이 모두 있어야 한다. 미사용 15칸은 완전 투명(alpha=0).
            프레임 순서는 고정이며 10번째 기본 동작을 추가할 수 없다. 기본 재생 간격은 120ms다.
            배경색·격자선·라벨을 시트에 그리지 말고, 캐릭터의 크기·발 기준점·여백을 프레임마다
            맞춰 흔들림과 잘림을 피하라. 한 그림을 단순 복제한 것을 실제 동작 애니메이션이라고 보고하지 마라.
            수정은 export한 사본에서 수행하고, 완성 파일의 투명도·프레임 배치를 검사한 뒤 import하라.
            앱 검사는 그림 내용의 동작 의미까지 판정하지 않으므로 직접 프레임을 시각적으로 검토하라.
            바탕화면 모드는 앱 밖 이동·다른 창 위 표시·최소화 중 표시를 지원하고 앱 종료 시 닫힌다.
            작업 중 표시는 CLI 입출력 활동이며 실제 LLM 완료나 승인 상태의 확정 판정은 아니다.

            ## 앱 설정 화면([⚙ 설정]) 조회·수정·저장 — 에이전트 내부 사용법

            사용자가 AgentCLIHub 의 설정 확인·변경·저장을 요청하면 `settings.json` 파일을 직접 편집하지 말고
            반드시 아래 `settings`(별칭 `config`) 명령을 사용하라. 앱이 허용 값 검증, 파일 저장, 시작프로그램
            레지스트리 반영, 트레이 아이콘·열려 있는 `[⚙ 설정]` 화면 실시간 동기화까지 즉시 처리한다.
            (비밀번호·텔레그램 봇 토큰·OAuth 토큰 원문은 응답에 노출되지 않으며 마스킹 상태만 반환된다.)

            ~~~powershell
            # 1) 현재 설정·허용 선택지(choices)·카탈로그·탐지 상태 조회
            & $env:AGENTCLIHUB_BROWSER_CLI settings get
            # 구역별 조회: all | ui | startup | tray | cli | bookmarks | signin | accounts | pet | telegram | schedules
            & $env:AGENTCLIHUB_BROWSER_CLI settings get tray
            & $env:AGENTCLIHUB_BROWSER_CLI settings get cli

            # 2) 단일 항목 수정 및 즉시 저장 (기본값 복원은 'default' 또는 빈 값, 실행 파일 자동 탐색은 'auto')
            & $env:AGENTCLIHUB_BROWSER_CLI settings set UiLanguage ko              # ko | en
            & $env:AGENTCLIHUB_BROWSER_CLI settings set RunAtStartup true          # true | false (Windows 시작프로그램 즉시 반영)
            & $env:AGENTCLIHUB_BROWSER_CLI settings set StartInTray always         # always | startup | never
            & $env:AGENTCLIHUB_BROWSER_CLI settings set CloseToTray true           # true | false (닫기 × 시 트레이 숨김)
            & $env:AGENTCLIHUB_BROWSER_CLI settings set ClaudeModel sonnet
            & $env:AGENTCLIHUB_BROWSER_CLI settings set ClaudeEffort high
            & $env:AGENTCLIHUB_BROWSER_CLI settings set ClaudeCodePermissionMode acceptEdits
            & $env:AGENTCLIHUB_BROWSER_CLI settings set CodexModel gpt-5.4
            & $env:AGENTCLIHUB_BROWSER_CLI settings set CodexEffort high
            & $env:AGENTCLIHUB_BROWSER_CLI settings set CodexSandbox workspace-write
            & $env:AGENTCLIHUB_BROWSER_CLI settings set CodexApproval on-request
            & $env:AGENTCLIHUB_BROWSER_CLI settings set AntigravityModel gemini-2.5-pro
            & $env:AGENTCLIHUB_BROWSER_CLI settings set AntigravityEffort high
            & $env:AGENTCLIHUB_BROWSER_CLI settings set AntigravityPermission auto
            & $env:AGENTCLIHUB_BROWSER_CLI settings set ClaudeCliPath auto         # CodexCliPath / AntigravityCliPath 동일
            & $env:AGENTCLIHUB_BROWSER_CLI settings set GuardClaudeLogin true      # KeepSignedIn / SavePasswords 동일

            # 3) 여러 항목 일괄 수정·저장 (JSON 문자열 또는 --file 경로)
            & $env:AGENTCLIHUB_BROWSER_CLI settings patch '{"StartInTray":"always","CloseToTray":true,"CodexEffort":"high"}'
            & $env:AGENTCLIHUB_BROWSER_CLI settings patch --file 'D:\작업\patch.json'

            # 4) 창·트레이 및 [⚙ 설정] 화면 제어
            & $env:AGENTCLIHUB_BROWSER_CLI settings open                           # 창을 앞으로 가져오고 [⚙ 설정] 화면 열기
            & $env:AGENTCLIHUB_BROWSER_CLI settings window status                  # status | show | hide | settings
            & $env:AGENTCLIHUB_BROWSER_CLI settings models-refresh                 # Antigravity 모델 목록 새로고침

            # 5) 브라우저 바로가기(북마크) · 계정 · 텔레그램 · 예약 작업 관리
            & $env:AGENTCLIHUB_BROWSER_CLI settings bookmark list                  # add <이름> <주소> [show] | update <순번> <이름> <주소> [show] | move <순번> <목표순번|up|down> | remove <순번>
            & $env:AGENTCLIHUB_BROWSER_CLI settings account list                   # rename <id|default:CLI> <새이름> | add <cli> <이름> [sso|console] | login <id> | remove <id>
            & $env:AGENTCLIHUB_BROWSER_CLI settings telegram status                # enable <on|off> | set-token <토큰> | clear-token | pair | revoke <chatId>
            & $env:AGENTCLIHUB_BROWSER_CLI settings schedule list                  # save <JSON|--file 경로> | enable <id> <on|off> | run <id> | remove <id> | clear-history
            ~~~

            - 모델·추론 강도·권한 허용 값은 먼저 `settings get cli` 의 `choices` 에서 확인한 뒤 설정하라.
            - 이미 열려 있는 CLI 탭은 시작할 때의 인자로 계속 돌며, 바꾼 CLI 기본값은 **새로 여는 대화 탭**부터 적용된다.
            - 계정 삭제(`account remove`), 텔레그램 토큰 삭제(`telegram clear-token`), 브라우저 로그인 정보 지우기(`settings signin clear-passwords --confirm` / `clear-all --confirm`)처럼 되돌리기 어려운 작업은 실행 전 사용자에게 먼저 확인받아라.
            """;
    }
}
