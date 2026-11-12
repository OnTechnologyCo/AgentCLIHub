using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentCLIHub;

/// <summary>화면에 뜬 고르기 항목 하나(승인·신뢰 확인 등). <paramref name="Selected"/> = 지금 커서가 있는 항목.</summary>
public sealed record PromptOption(int Index, string Label, bool Selected);

/// <summary>
/// 텔레그램 원격에 쓰는 **순수 계산**만 모았다 — 화면 글 해석, 기록에서 답 고르기, 글 자르기.
/// 네트워크·탭을 안 건드리므로 그대로 검사할 수 있다(tests/TelegramChecks).
/// </summary>
public static class TelegramText
{
    // ── 명령 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// "/use 2" → ("use", "2"). 대화창에서 고른 명령은 "/use@봇이름" 으로 오므로 @ 뒤를 버린다.
    /// 명령이 아니면 ("", 원문).
    /// </summary>
    public static (string Command, string Args) Command(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length < 2 || t[0] != '/' || t[1] == '/') return ("", t);
        var sp = t.IndexOfAny([' ', '\n', '\t']);
        var head = sp < 0 ? t : t[..sp];
        var rest = sp < 0 ? "" : t[(sp + 1)..].Trim();
        var at = head.IndexOf('@');
        if (at > 0) head = head[..at];
        return (head[1..].ToLowerInvariant(), rest);
    }

    /// <summary>"//compact" 처럼 / 를 두 번 쓰면 이 봇의 명령이 아니라 CLI 로 보낼 글이다.</summary>
    public static string Unescape(string text) =>
        text.StartsWith("//", StringComparison.Ordinal) ? text[1..] : text;

    /// <summary>
    /// "/new D:\작업 폴더 codex" → ("D:\작업 폴더", "codex_cli"). CLI 이름은 맨 끝에만 온다 —
    /// 폴더 이름에 빈칸이 있을 수 있어서다. 이름이 없으면 backend 는 빈 값(= 마지막으로 고른 CLI).
    /// </summary>
    public static (string Folder, string Backend) NewArgs(string? args)
    {
        var t = (args ?? "").Trim();
        var backend = "";
        var m = Regex.Match(t, @"(?:^|\s)(claude|codex|antigravity|cc|cx|ag)\s*$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            backend = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "claude" or "cc" => CliProfile.Claude.Backend,
                "codex" or "cx" => CliProfile.Codex.Backend,
                _ => CliProfile.Antigravity.Backend,
            };
            t = t[..m.Index].Trim();
        }
        return (t.Trim('"', '\''), backend);
    }

    // ── 글 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 한 메시지 한도에 맞춰 자른다. 줄 경계 → 빈칸 → 글자 순으로 자를 곳을 찾는다.
    /// 서로게이트 쌍(이모지)을 가르지 않는다 — 갈리면 텔레그램이 메시지를 통째로 거절한다.
    /// </summary>
    public static List<string> Split(string? text, int max = TelegramApi.MaxText)
    {
        var outp = new List<string>();
        var rest = (text ?? "").Replace("\r\n", "\n").Trim('\n');
        while (rest.Length > max)
        {
            var cut = rest.LastIndexOf('\n', max - 1);
            if (cut < max / 2) cut = rest.LastIndexOf(' ', max - 1);
            if (cut < max / 2) cut = max;
            if (cut > 0 && char.IsHighSurrogate(rest[cut - 1])) cut--;
            outp.Add(rest[..cut].TrimEnd());
            rest = rest[cut..].TrimStart('\n', ' ');
        }
        if (rest.Length > 0 || outp.Count == 0) outp.Add(rest);
        return outp;
    }

    public static string Clip(string? s, int max)
    {
        s ??= "";
        if (s.Length <= max) return s;
        var cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut] + "…";
    }

    private static string Squash(string? s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    /// <summary>
    /// 터미널 화면 글에서 보낼 부분. 위아래 빈 줄을 걷고, 빈 줄이 여럿 이어지면 하나로 줄인다.
    /// **아래쪽을 남긴다** — 묻는 말과 고르기 항목은 화면 아래에 있다.
    /// </summary>
    public static string ScreenExcerpt(string? screen, int maxLines = 40, int maxChars = 3500)
    {
        var lines = new List<string>();
        foreach (var raw in (screen ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var l = raw.TrimEnd();
            if (l.Length == 0 && lines.Count > 0 && lines[^1].Length == 0) continue;
            lines.Add(l);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        if (lines.Count > maxLines) lines = lines.Skip(lines.Count - maxLines).ToList();
        var text = string.Join("\n", lines);
        while (text.Length > maxChars && lines.Count > 1)
        {
            lines.RemoveAt(0);
            text = string.Join("\n", lines);
        }
        return text.Length > maxChars ? text[^maxChars..] : text;
    }

    // ── 화면 해석 ───────────────────────────────────────────────────────────

    /// <summary>
    /// CLI 가 일하는 중인가. claude·codex 둘 다 일하는 동안 아래 줄에 "esc to interrupt" 를 띄운다.
    /// 출력이 잠깐 멎어도(생각 중) 이 글이 남아 있으면 끝난 것이 아니다.
    /// </summary>
    public static bool IsWorking(string? screen) =>
        screen is not null && Regex.IsMatch(screen, @"esc to interrupt", RegexOptions.IgnoreCase);

    // 고르기 항목 한 줄: "❯ 1. Yes", "  2. No, and tell Claude …", "│ › 3. No (esc) │".
    private static readonly Regex OptionLine = new(
        @"^\s*(?:[│┃|]\s*)?(?<cur>[❯›>▶➤→▸]\s*)?(?<n>[1-9])[.)]\s+(?<label>\S.*?)\s*(?:[│┃|])?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// 화면 아래쪽의 고르기 목록을 찾는다(승인·폴더 신뢰·설정 고르기 등).
    ///
    /// **커서가 있는 목록만 본다.** 답 본문의 번호 목록("1. 설치 2. 실행")도 같은 모양이라,
    /// 커서 표시(❯ ›)가 정확히 한 항목에 있어야 고르기 창으로 친다. 번호는 1부터 이어져야 하고,
    /// 긴 항목이 줄을 넘겨 접힌 경우를 위해 항목 사이 두 줄까지는 건너뛴다.
    /// </summary>
    public static List<PromptOption> PromptOptions(string? screen)
    {
        var lines = (screen ?? "").Replace("\r\n", "\n").Split('\n');
        var from = Math.Max(0, lines.Length - 40);
        List<PromptOption>? best = null;
        List<PromptOption>? run = null;
        var gap = 0;
        for (var i = from; i < lines.Length; i++)
        {
            var m = OptionLine.Match(lines[i]);
            if (m.Success)
            {
                var n = m.Groups["n"].Value[0] - '0';
                var opt = new PromptOption(n, m.Groups["label"].Value.Trim(), m.Groups["cur"].Success);
                if (run is not null && n == run[^1].Index + 1) { run.Add(opt); gap = 0; continue; }
                if (n == 1) { Close(); run = [opt]; gap = 0; continue; }
                Close();
                continue;
            }
            if (run is not null && ++gap > 2) Close();
        }
        Close();
        return best ?? [];

        void Close()
        {
            if (run is { Count: >= 2 } && run.Count(o => o.Selected) == 1) best = run;
            run = null; gap = 0;
        }
    }

    /// <summary>같은 고르기 창인지 가리는 표. 항목만이 아니라 바로 위 질문까지 넣는다 — 승인 창은 항목이 늘 같다.</summary>
    public static string PromptSignature(string? screen, IReadOnlyList<PromptOption> options) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ScreenExcerpt(screen, 14, 4000) + "|" +
            string.Join("|", options.Select(o => o.Label)))))[..16];

    /// <summary>
    /// <paramref name="target"/> 번 항목을 고르는 키. 커서를 화살표로 옮긴 뒤 Enter 는 따로 보낸다
    /// (같은 덩어리로 보내면 TUI 가 이동을 다 소화하기 전에 Enter 를 받는 일이 있다).
    /// 숫자 키 바로가기는 CLI 마다 달라 쓰지 않는다 — 화살표는 어디서나 같다.
    /// </summary>
    public static string MoveKeys(IReadOnlyList<PromptOption> options, int target)
    {
        var cur = options.FirstOrDefault(o => o.Selected)?.Index ?? 1;
        var step = target > cur ? "\u001b[B" : "\u001b[A";
        return string.Concat(Enumerable.Repeat(step, Math.Abs(target - cur)));
    }

    // ── 대화 기록 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 기록에서 **이번 질문 이후**의 조각들. 보낸 글로 사용자 발화를 찾는다(뒤에서부터).
    /// 못 찾으면 보낼 때의 조각 수(<paramref name="baseline"/>) 뒤에 생긴 마지막 사용자 발화를 쓴다 —
    /// CLI 가 긴 붙여넣기를 줄여 적는 등 글이 조금 달라질 수 있다. 그것도 없으면 null(아직 안 적혔다).
    /// </summary>
    public static List<TranscriptItem>? TurnItems(IReadOnlyList<TranscriptItem> items, string sent, int baseline)
    {
        var key = Squash(sent);
        if (key.Length > 60) key = key[..60];
        var at = -1;
        if (key.Length > 0)
            for (var i = items.Count - 1; i >= 0 && i >= Math.Max(0, baseline); i--)
                if (items[i].Kind == ItemKind.User && Squash(items[i].Text).Contains(key, StringComparison.Ordinal)) { at = i; break; }
        if (at < 0)
            for (var i = items.Count - 1; i >= Math.Max(0, baseline); i--)
                if (items[i].Kind == ItemKind.User) { at = i; break; }
        return at < 0 ? null : items.Skip(at + 1).ToList();
    }

    /// <summary>
    /// 이번 턴의 최종 답. 마지막 도구 호출 **뒤의** 답 글만 모은다 — 앞쪽 "확인해 보겠습니다" 같은
    /// 중간 말은 진행 상황에서 이미 보였다. 도구를 안 썼으면 답 전체다.
    /// </summary>
    public static string FinalAnswer(IReadOnlyList<TranscriptItem> turn)
    {
        var lastTool = -1;
        for (var i = 0; i < turn.Count; i++)
            if (turn[i].Kind is ItemKind.ToolCall or ItemKind.ToolResult) lastTool = i;
        var tail = turn.Skip(lastTool + 1).Where(i => i.Kind == ItemKind.Assistant)
            .Select(i => i.Text.Trim()).Where(s => s.Length > 0).ToList();
        if (tail.Count == 0)
            tail = turn.Where(i => i.Kind == ItemKind.Assistant).Select(i => i.Text.Trim())
                .Where(s => s.Length > 0).TakeLast(1).ToList();
        return string.Join("\n\n", tail);
    }

    /// <summary>도구 호출 한 줄 요약. 기록의 이름표(description · command · file …) 가운데 사람이 알아볼 것을 고른다.</summary>
    public static string ToolLine(TranscriptItem item)
    {
        var lines = item.Text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        string? Value(string label) => lines.FirstOrDefault(l => l.StartsWith(label + ":", StringComparison.Ordinal)) is { } hit
            ? hit[(label.Length + 1)..].Trim() : null;
        var what = Value("description") ?? Value("command") ?? Value("file") ?? Value("path") ?? Value("pattern")
                   ?? Value("url") ?? Value("query") ?? lines.FirstOrDefault() ?? "";
        var name = item.Tool.Length > 0 ? item.Tool : "tool";
        what = Clip(Squash(what), 90);
        return what.Length > 0 ? $"• {name}: {what}" : $"• {name}";
    }

    // ── 페어링 ──────────────────────────────────────────────────────────────

    // 헷갈리는 글자(0·O·1·I)는 뺀다 — 휴대폰으로 옮겨 칠 코드다.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string NewPairingCode()
    {
        Span<byte> b = stackalloc byte[8];
        RandomNumberGenerator.Fill(b);
        var sb = new StringBuilder(9);
        for (var i = 0; i < b.Length; i++)
        {
            if (i == 4) sb.Append('-');
            sb.Append(CodeAlphabet[b[i] % CodeAlphabet.Length]);
        }
        return sb.ToString();
    }

    private static string NormalizeCode(string? s) =>
        new((s ?? "").ToUpperInvariant().Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9').ToArray());

    /// <summary>코드를 비교한다. 빈칸·대시·대소문자는 무시하고, 시간 차로 새지 않게 비교한다.</summary>
    public static bool SameCode(string? expected, string? given)
    {
        var a = Encoding.ASCII.GetBytes(NormalizeCode(expected));
        var b = Encoding.ASCII.GetBytes(NormalizeCode(given));
        return a.Length > 0 && a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
