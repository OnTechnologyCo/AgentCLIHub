using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentCLIHub;

/// <summary>대화 한 조각의 종류. 두 CLI 의 기록을 이 다섯으로 접는다.</summary>
public enum ItemKind { User, Assistant, ToolCall, ToolResult, Summary }

/// <param name="Tool">도구 이름(<see cref="ItemKind.ToolCall"/> 에서만).</param>
/// <param name="Error">도구가 실패했는가(<see cref="ItemKind.ToolResult"/> 에서만).</param>
public sealed record TranscriptItem(ItemKind Kind, string Text, string Tool = "", bool Error = false);

/// <summary>
/// CLI 와 무관하게 편 대화 하나. **다른 CLI · 다른 계정으로 대화를 옮길 때** 이것을 거친다 —
/// 원본을 읽어 이것으로 펴고(<see cref="TranscriptReader"/>), 받을 CLI 의 기록 형식으로 다시 쓴다
/// (<see cref="TranscriptWriter"/>).
///
/// **도구 호출은 글로 남긴다.** 두 CLI 의 도구는 이름도 인자도 달라 그대로 옮길 수가 없다.
/// codex 가 Claude Code 기록을 가져올 때 쓰는 모양을 그대로 따른다
/// (<c>[external_agent_tool_call: X] … [/external_agent_tool_call]</c> — codex 실행 파일의
/// external-agent-migration 이 이렇게 쓴다). 새 에이전트는 이전 에이전트가 무엇을 했는지 읽을 수 있고,
/// 자기가 부른 도구로 착각하지 않는다.
/// </summary>
public sealed class Transcript
{
    /// <summary>원본 CLI — <c>claude_code</c> · <c>codex_cli</c>.</summary>
    public string Backend { get; init; } = "";

    public string SessionId { get; set; } = "";

    public string Cwd { get; set; } = "";

    /// <summary>원본 기록 파일. 옮긴 대화의 첫 머리에 적어 둔다 — 잘려 나간 부분을 새 에이전트가 찾아 읽을 수 있게.</summary>
    public string SourcePath { get; init; } = "";

    public List<TranscriptItem> Items { get; } = [];

    /// <summary>사람이 한 말이 하나라도 있는가. 없으면 옮길 대화가 없는 것이다(열자마자 /clear 한 탭 등).</summary>
    public bool HasConversation => Items.Any(i => i.Kind is ItemKind.User or ItemKind.Summary);

    public int UserTurns => Items.Count(i => i.Kind == ItemKind.User);

    /// <summary>첫 발화 한 줄. 고르기 창에서 "이 대화를 넘깁니다" 를 보여 줄 때 쓴다.</summary>
    public string Title
    {
        get
        {
            var first = Items.FirstOrDefault(i => i.Kind == ItemKind.User)?.Text
                        ?? Items.FirstOrDefault(i => i.Kind == ItemKind.Summary)?.Text ?? "";
            var line = Regex.Replace(first, @"\s+", " ").Trim();
            return line.Length > 90 ? line[..90] + "…" : line;
        }
    }
}

/// <summary>
/// 두 CLI 의 기록 파일을 <see cref="Transcript"/> 로 편다. **모르는 줄은 건너뛴다** —
/// 기록 형식은 CLI 판마다 조금씩 바뀌고, 한 줄 때문에 대화 전체를 못 옮기면 안 된다.
/// 파일은 CLI 가 쓰는 중일 수 있어 공유 모드로 읽는다.
/// </summary>
public static class TranscriptReader
{
    public static Transcript Read(CliProfile cli, string path) =>
        cli.Kind switch
        {
            CliKind.Claude => ReadClaude(path),
            CliKind.Codex => ReadCodex(path),
            CliKind.Antigravity => AntigravityTransfer.Read(path),
            _ => throw new NotSupportedException("이 CLI 의 대화 변환은 지원하지 않습니다."),
        };

    // ── claude ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>~/.claude/projects/&lt;폴더&gt;/&lt;세션&gt;.jsonl</c>.
    ///
    /// **파일 순서대로 읽지 않는다.** 대화는 <c>parentUuid</c> 사슬이다 — 되감기(Esc Esc)로 버린 가지도
    /// 파일에는 그대로 남아 있고, 순서대로 읽으면 사용자가 무른 대화까지 딸려 온다.
    /// 그래서 마지막 메시지에서 부모를 따라 거꾸로 올라간다.
    ///
    /// **압축(/compact) 경계에서 멈춘다.** 경계 뒤에는 요약 한 통과 "보존 구간"(요약 직후에 그대로 남긴
    /// 최근 메시지 몇 개)이 오는데, 보존 구간의 머리는 원래 부모(압축 **앞** 메시지)를 가리킨다.
    /// 그대로 따라가면 압축으로 버린 수십만 토큰이 통째로 딸려 온다 — 실제로 한 대화가 90만 토큰이었다.
    /// 경계가 적어 둔 보존 구간 머리에 닿으면 요약으로 건너뛰고 거기서 끝낸다(claude 가 불러올 때와 같다).
    /// </summary>
    public static Transcript ReadClaude(string path)
    {
        var t = new Transcript
        {
            Backend = CliProfile.Claude.Backend,
            SourcePath = path,
            SessionId = Path.GetFileNameWithoutExtension(path),
        };

        var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var posOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var talk = new List<(int Pos, JsonObject O)>();                       // 본 대화의 말(파일 순서)
        var jump = new Dictionary<string, string>(StringComparer.Ordinal);   // 보존 구간 머리 → 요약

        // 도구 결과는 **호출 번호로** 찾는다. 도구를 여럿 한꺼번에 부르면 claude 는 첫 결과를 둘째 호출의
        // 형제로 적는다(부모가 같다) — 사슬만 따라가면 그 결과들이 빠진다(실측: 196개 중 28개).
        var results = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        JsonObject? leaf = null;

        var pos = 0;
        foreach (var o in Lines(path))
        {
            pos++;
            var type = Str(o["type"]);
            var uuid = Str(o["uuid"]);
            if (uuid.Length > 0) { byId[uuid] = o; posOf[uuid] = pos; }   // 같은 줄이 두 번 적히기도 한다 — 뒤의 것이 이긴다
            if (t.Cwd.Length == 0 && Str(o["cwd"]) is { Length: > 0 } cwd) t.Cwd = cwd;

            if (type == "system" && Str(o["subtype"]) == "compact_boundary")
            {
                var seg = o["compactMetadata"]?["preservedSegment"];
                var head = Str(seg?["headUuid"]);
                var anchor = Str(seg?["anchorUuid"]);
                if (head.Length > 0 && anchor.Length > 0) jump[head] = anchor;
            }

            // 곁가지(서브에이전트) 대화는 본 대화가 아니다.
            if (type is "user" or "assistant" && !True(o["isSidechain"]))
            {
                leaf = o;
                talk.Add((pos, o));
                if (type == "user" && o["message"]?["content"] is JsonArray blocks)
                    foreach (var b in blocks.OfType<JsonObject>())
                        if (Str(b["type"]) == "tool_result" && Str(b["tool_use_id"]) is { Length: > 0 } tid) results[tid] = b;
            }
        }
        if (leaf is null) return t;

        var chain = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var cur = leaf; cur is not null;)
        {
            var id = Str(cur["uuid"]);
            if (id.Length > 0 && !seen.Add(id)) break;   // 고리 — 깨진 파일
            chain.Add(cur);

            if (jump.TryGetValue(id, out var anchorId))
            {
                if (byId.TryGetValue(anchorId, out var anchor) && seen.Add(anchorId)) chain.Add(anchor);
                break;
            }

            var parent = Str(cur["parentUuid"]);
            if (parent.Length > 0)
            {
                if (!byId.TryGetValue(parent, out cur)) break;
                continue;
            }

            // **부모가 없는 자리.** 압축 경계면 거기가 진짜 시작이다. 그 밖(이어하기 때 적힌 SessionStart 훅
            // 첨부가 부모 없이 적히는 일이 실제로 있다)은 사슬이 끊긴 것일 뿐 대화는 이어져 있었다 —
            // 그대로 멈추면 앱을 껐다 켠 뒤의 말만 남고 그 앞이 통째로 빠진다. 파일에서 바로 앞의 말로 잇는다.
            if (Str(cur["type"]) == "system" && Str(cur["subtype"]) == "compact_boundary") break;
            var here = id.Length > 0 && posOf.TryGetValue(id, out var p) ? p : int.MaxValue;
            cur = talk.LastOrDefault(x => x.Pos < here && !seen.Contains(Str(x.O["uuid"]))).O;
        }
        chain.Reverse();

        foreach (var o in chain) AddClaude(t, o, results);
        return t;
    }

    /// <param name="results">호출 번호 → 도구 결과. 결과는 사슬 자리가 아니라 **호출 바로 뒤**에 붙인다.</param>
    private static void AddClaude(Transcript t, JsonObject o, Dictionary<string, JsonObject> results)
    {
        // 한도·오류 알림(claude 가 지어 넣은 "usage limit reached" 따위)은 대화가 아니다.
        // 옮기면 새 에이전트가 자기도 막힌 줄 안다.
        if (True(o["isApiErrorMessage"])) return;

        var type = Str(o["type"]);
        var content = o["message"]?["content"];

        if (type == "user")
        {
            if (True(o["isCompactSummary"]))
            {
                var sum = TextOf(content);
                if (sum.Trim().Length > 0) t.Items.Add(new TranscriptItem(ItemKind.Summary, sum.Trim()));
                return;
            }
            // 스킬 본문·경고문처럼 claude 가 사용자 자리에 끼워 넣은 것.
            if (True(o["isMeta"])) return;

            if (content is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            {
                AddUserText(t, v.GetValue<string>());
                return;
            }
            if (content is not JsonArray arr) return;

            foreach (var b in arr.OfType<JsonObject>())
            {
                switch (Str(b["type"]))
                {
                    case "text": AddUserText(t, Str(b["text"])); break;
                    case "image": t.Items.Add(new TranscriptItem(ItemKind.User, "[이미지 첨부]")); break;
                    case "document": t.Items.Add(new TranscriptItem(ItemKind.User, "[문서 첨부]")); break;
                    // tool_result 는 여기서 안 넣는다 — 그 호출(tool_use) 바로 뒤에 붙는다.
                }
            }
            return;
        }

        if (type != "assistant" || content is not JsonArray blocks) return;

        foreach (var b in blocks.OfType<JsonObject>())
        {
            switch (Str(b["type"]))
            {
                case "text":
                    var text = Str(b["text"]).Trim();
                    if (text.Length > 0) t.Items.Add(new TranscriptItem(ItemKind.Assistant, text));
                    break;

                case "tool_use":
                case "server_tool_use":
                    var name = Str(b["name"]);
                    t.Items.Add(new TranscriptItem(ItemKind.ToolCall, ClaudeInput(b["input"] as JsonObject), name.Length > 0 ? name : "tool"));
                    if (results.Remove(Str(b["id"]), out var r))
                        t.Items.Add(new TranscriptItem(ItemKind.ToolResult, ResultText(r["content"]), Error: True(r["is_error"])));
                    break;

                // 생각(thinking)은 옮기지 않는다 — 서명이 붙은 그 모델만의 것이고, 다른 모델에게는 읽을 거리가 아니다.
            }
        }
    }

    /// <summary>
    /// 사람이 친 말만 남긴다. 슬래시 명령의 껍데기(<c>&lt;command-name&gt;</c>)·로컬 명령 출력·끼워 넣은
    /// 알림(<c>&lt;system-reminder&gt;</c>)은 뺀다 — 옮기면 새 에이전트가 그것을 사용자의 요청으로 읽는다.
    /// </summary>
    private static void AddUserText(Transcript t, string raw)
    {
        var s = Regex.Replace(raw ?? "", @"<system-reminder>[\s\S]*?</system-reminder>", "").Trim();
        if (s.Length == 0) return;
        if (s.StartsWith("<command-", StringComparison.Ordinal) ||
            s.StartsWith("<local-command-", StringComparison.Ordinal) ||
            s.StartsWith("Caveat:", StringComparison.Ordinal) ||
            s.StartsWith("[Request interrupted", StringComparison.Ordinal)) return;
        t.Items.Add(new TranscriptItem(ItemKind.User, s));
    }

    /// <summary>
    /// 도구 인자를 사람이 읽는 몇 줄로. codex 의 가져오기와 같은 이름표(description · command · file · input)를 쓴다.
    /// 긴 값(파일 내용·패치)은 자른다 — 결과는 따로 적히고, 인자 전체를 옮겨도 새 에이전트에게 쓸모가 적다.
    /// </summary>
    private static string ClaudeInput(JsonObject? input)
    {
        if (input is null) return "";
        var sb = new StringBuilder();
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Take(string key, string label)
        {
            if (input[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String &&
                v.GetValue<string>() is { Length: > 0 } s)
            {
                sb.Append(label).Append(": ").Append(Clip(s, 1200)).Append('\n');
                used.Add(key);
            }
        }
        Take("description", "description");
        Take("command", "command");
        Take("file_path", "file");
        Take("notebook_path", "file");
        Take("path", "path");
        Take("pattern", "pattern");
        Take("url", "url");
        Take("query", "query");
        Take("prompt", "prompt");

        var rest = new JsonObject();
        foreach (var (k, v) in input)
            if (!used.Contains(k)) rest[k] = v?.DeepClone();
        if (rest.Count > 0) sb.Append("input: ").Append(Clip(rest.ToJsonString(Json), 1500)).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    // ── codex ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>$CODEX_HOME/sessions/…/rollout-*.jsonl</c>. 모델이 본 것(<c>response_item</c>)만 읽는다 —
    /// <c>event_msg</c> 는 화면용 사본이라 같은 말이 두 번 들어온다.
    ///
    /// **추론(reasoning)과 원격 압축본은 옮기지 않는다.** 둘 다 암호화돼 있어 읽을 수가 없고, 발급한 계정에
    /// 묶여 있을 수 있다 — 다른 계정으로 그대로 보내면 첫 턴에서 거절당한다. 압축본 대신 파일에 남아 있는
    /// 압축 전 원문을 읽는다(rollout 은 덧붙이기만 하므로 원문이 그대로 있다). 글로 된 요약(로컬 압축)이
    /// 있으면 그것부터 시작한다.
    /// </summary>
    public static Transcript ReadCodex(string path)
    {
        var t = new Transcript { Backend = CliProfile.Codex.Backend, SourcePath = path };

        foreach (var o in Lines(path))
        {
            var p = o["payload"] as JsonObject;
            if (p is null) continue;

            switch (Str(o["type"]))
            {
                case "session_meta":
                    if (t.SessionId.Length == 0) t.SessionId = Str(p["id"]) is { Length: > 0 } id ? id : Str(p["session_id"]);
                    if (t.Cwd.Length == 0) t.Cwd = Str(p["cwd"]);
                    break;

                case "compacted":
                    var msg = Str(p["message"]).Trim();
                    if (msg.Length > 0)
                    {
                        t.Items.Clear();
                        t.Items.Add(new TranscriptItem(ItemKind.Summary, msg));
                    }
                    break;

                case "response_item":
                    AddCodex(t, p);
                    break;
            }
        }
        return t;
    }

    private static void AddCodex(Transcript t, JsonObject p)
    {
        switch (Str(p["type"]))
        {
            case "message":
            {
                var role = Str(p["role"]);
                var text = new StringBuilder();
                var images = 0;
                if (p["content"] is JsonArray parts)
                {
                    foreach (var c in parts.OfType<JsonObject>())
                    {
                        var ct = Str(c["type"]);
                        if (ct is "input_image" or "image") { images++; continue; }
                        var s = Str(c["text"]);
                        if (s.Length > 0) text.Append(s).Append('\n');
                    }
                }
                var body = text.ToString().Trim();

                if (role == "user")
                {
                    // codex 가 사용자 자리에 끼워 넣는 것(작업 환경·AGENTS.md·스킬 목록)은 사람의 말이 아니다.
                    if (IsInjected(body)) return;
                    if (images > 0) body = (body + "\n" + string.Join(" ", Enumerable.Repeat("[이미지 첨부]", images))).Trim();
                    if (body.Length > 0) t.Items.Add(new TranscriptItem(ItemKind.User, body));
                }
                else if (role == "assistant" && body.Length > 0)
                {
                    t.Items.Add(new TranscriptItem(ItemKind.Assistant, body));
                }
                return;   // developer · system 은 codex 자신의 지침이다
            }

            case "function_call":
                t.Items.Add(new TranscriptItem(ItemKind.ToolCall, CodexArgs(Str(p["arguments"])), Name(p, "function")));
                return;

            case "custom_tool_call":
                t.Items.Add(new TranscriptItem(ItemKind.ToolCall, "input: " + Clip(Str(p["input"]), 1500), Name(p, "tool")));
                return;

            case "local_shell_call":
            {
                var cmd = p["action"]?["command"];
                var line = cmd is JsonArray a ? string.Join(" ", a.Select(x => x?.ToString())) : Str(cmd);
                t.Items.Add(new TranscriptItem(ItemKind.ToolCall, "command: " + Clip(line, 1200), "shell"));
                return;
            }

            case "web_search_call":
                t.Items.Add(new TranscriptItem(ItemKind.ToolCall, "query: " + Str(p["action"]?["query"]), "web_search"));
                return;

            case "function_call_output":
            case "custom_tool_call_output":
                t.Items.Add(new TranscriptItem(ItemKind.ToolResult, CodexOutput(p["output"])));
                return;
        }
    }

    private static string Name(JsonObject p, string fallback) => Str(p["name"]) is { Length: > 0 } n ? n : fallback;

    private static bool IsInjected(string s) =>
        s.Length == 0 ||
        s.StartsWith("# AGENTS.md instructions", StringComparison.Ordinal) ||
        (s.StartsWith('<') && s.EndsWith('>'));   // <environment_context>…</environment_context> 따위 통째로 태그인 것

    /// <summary>codex 함수 인자(JSON 글). 셸 명령은 배열로 온다 — 한 줄로 편다.</summary>
    private static string CodexArgs(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject o)
            {
                var sb = new StringBuilder();
                var used = new HashSet<string>(StringComparer.Ordinal);
                if (o["command"] is { } cmd)
                {
                    var line = cmd is JsonArray a ? string.Join(" ", a.Select(x => x?.ToString())) : Str(cmd);
                    sb.Append("command: ").Append(Clip(line, 1200)).Append('\n');
                    used.Add("command");
                }
                if (Str(o["workdir"]) is { Length: > 0 } wd) { sb.Append("workdir: ").Append(wd).Append('\n'); used.Add("workdir"); }
                var rest = new JsonObject();
                foreach (var (k, v) in o) if (!used.Contains(k)) rest[k] = v?.DeepClone();
                if (rest.Count > 0) sb.Append("input: ").Append(Clip(rest.ToJsonString(Json), 1500)).Append('\n');
                return sb.ToString().TrimEnd('\n');
            }
        }
        catch { /* JSON 이 아니면 글 그대로 */ }
        return "input: " + Clip(json, 1500);
    }

    /// <summary>
    /// 도구 결과. 판마다 모양이 다르다 — 글 하나, <c>{"output":…,"metadata":…}</c> 를 담은 JSON 글,
    /// <c>[{type:"input_text",text}]</c> 배열.
    /// </summary>
    private static string CodexOutput(JsonNode? output)
    {
        if (output is JsonArray arr) return ResultText(arr);
        if (output is JsonObject obj) return Str(obj["content"]) is { Length: > 0 } c ? c : obj.ToJsonString(Json);

        var s = Str(output);
        if (s.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(s) is JsonObject o && o["output"] is JsonValue v &&
                    v.GetValueKind() == JsonValueKind.String) return v.GetValue<string>();
            }
            catch { }
        }
        return s;
    }

    // ── 공통 ────────────────────────────────────────────────────────────────

    /// <summary>줄마다 JSON 한 덩어리. 잘린 마지막 줄·깨진 줄은 건너뛴다.</summary>
    internal static IEnumerable<JsonObject> Lines(string path)
    {
        using var st = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var rd = new StreamReader(st, Encoding.UTF8);
        for (string? line; (line = rd.ReadLine()) is not null;)
        {
            if (line.Length < 2 || line[0] != '{') continue;
            JsonObject? o;
            try { o = JsonNode.Parse(line) as JsonObject; }
            catch { continue; }
            if (o is not null) yield return o;
        }
    }

    /// <summary>글이거나 <c>[{type:"text",text}]</c> 배열인 내용에서 글만.</summary>
    private static string TextOf(JsonNode? content)
    {
        if (content is JsonValue v && v.GetValueKind() == JsonValueKind.String) return v.GetValue<string>();
        if (content is not JsonArray arr) return "";
        var sb = new StringBuilder();
        foreach (var b in arr.OfType<JsonObject>())
            if (Str(b["text"]) is { Length: > 0 } s) sb.Append(s).Append('\n');
        return sb.ToString();
    }

    /// <summary>도구 결과의 글. 그림은 자리 표시만 남긴다.</summary>
    private static string ResultText(JsonNode? content)
    {
        if (content is JsonValue v && v.GetValueKind() == JsonValueKind.String) return v.GetValue<string>();
        if (content is not JsonArray arr) return content?.ToJsonString(Json) ?? "";
        var sb = new StringBuilder();
        foreach (var b in arr.OfType<JsonObject>())
        {
            var ty = Str(b["type"]);
            if (ty is "image" or "input_image") sb.Append("[이미지]\n");
            else if (Str(b["text"]) is { Length: > 0 } s) sb.Append(s).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    internal static string Str(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";

    private static bool True(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    /// <summary>긴 글을 앞뒤만 남기고 자른다. 가운데를 버리는 이유: 명령 출력은 끝에 결론(오류·요약)이 온다.</summary>
    internal static string Clip(string s, int max)
    {
        if (s.Length <= max) return s;
        var head = max * 2 / 3;
        var tail = max - head;
        return s[..head] + $"\n…({s.Length - max:N0}자 생략)…\n" + s[^tail..];
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// <see cref="Transcript"/> 를 받을 CLI 의 기록 파일로 쓴다. 쓴 파일은 **그 CLI 의 이어하기로 연다**
/// (<c>claude --resume &lt;번호&gt;</c> · <c>codex resume &lt;번호&gt;</c>) — 새 에이전트가 대화를
/// 파일로 읽어 오는 것이 아니라 **자기 대화 기록으로** 받는다.
///
/// **원본은 건드리지 않는다.** 늘 새 번호로 새 파일을 만든다 — 옮기다 잘못돼도 원래 대화는 그대로
/// [↻ 이어하기] 에 남는다.
///
/// 두 형식 모두 실측으로 확인했다(2026-09-21, claude 2.1.278 · codex 0.155.0):
/// claude 는 이 모양의 파일을 <c>--resume</c> 으로 열어 앞 대화 내용을 기억해 답했고,
/// codex 는 상태 DB 에 없는 이 파일을 파일 이름으로 찾아 화면에 앞 대화를 그렸다.
/// </summary>
public static class TranscriptWriter
{
    /// <summary>
    /// 옮길 글의 총량(자). **받는 모델의 문맥 창에 넉넉히 들어가야 한다** — 넘치면 첫 턴부터 문맥 초과로
    /// 막히거나 곧바로 압축이 돈다. 한국어가 많으면 한 자가 토큰 하나 가까이 먹으므로 보수적으로 잡는다.
    /// 넘치면 **앞쪽을 버리고 최근 것을 남긴다** — 이어가는 데 필요한 것은 방금까지 하던 일이다.
    /// </summary>
    public const int Budget = 160_000;

    private const int MaxText = 20_000;
    private const int MaxSummary = 30_000;
    private const int MaxToolResult = 2_500;
    private const int MaxFirstUser = 4_000;

    /// <summary>한 조각을 받을 쪽에 적을 글로. 도구는 codex 가져오기와 같은 표식으로 감싼다.</summary>
    internal static string Render(TranscriptItem i) => i.Kind switch
    {
        ItemKind.User => TranscriptReader.Clip(i.Text, MaxText),
        ItemKind.Assistant => TranscriptReader.Clip(i.Text, MaxText),
        ItemKind.Summary => "[이전 대화 요약]\n" + TranscriptReader.Clip(i.Text, MaxSummary),
        ItemKind.ToolCall => $"[external_agent_tool_call: {i.Tool}]\n{i.Text}\n[/external_agent_tool_call]",
        _ => $"[external_agent_tool_result{(i.Error ? ": error" : "")}]\n" +
             TranscriptReader.Clip(i.Text, MaxToolResult) + "\n[/external_agent_tool_result]",
    };

    /// <summary>
    /// 예산 안으로 줄인 조각들. 맨 앞 요약과 **첫 요청**은 남긴다 — 무엇을 하던 대화인지가 거기 있다.
    /// 나머지는 뒤에서부터 채우고, 빠진 자리에는 몇 개를 뺐는지와 원본 경로를 적는다.
    /// </summary>
    internal static List<(TranscriptItem Item, string Text)> Budgeted(Transcript t)
    {
        var all = t.Items.Select(i => (Item: i, Text: Render(i))).ToList();
        var total = all.Sum(x => x.Text.Length);
        if (total <= Budget) return all;

        var head = new List<(TranscriptItem, string)>();
        var from = 0;
        if (all.Count > 0 && all[0].Item.Kind == ItemKind.Summary) { head.Add(all[0]); from = 1; }
        var firstUser = all.FindIndex(from, x => x.Item.Kind == ItemKind.User);
        if (firstUser >= 0)
        {
            var u = all[firstUser];
            head.Add((u.Item, TranscriptReader.Clip(u.Text, MaxFirstUser)));
            from = firstUser + 1;
        }

        var room = Budget - head.Sum(x => x.Item2.Length) - 600;
        var tail = new List<(TranscriptItem, string)>();
        var i = all.Count - 1;
        for (; i >= from; i--)
        {
            if (all[i].Text.Length > room) break;
            room -= all[i].Text.Length;
            tail.Add(all[i]);
        }
        tail.Reverse();

        var dropped = i - from + 1;
        var droppedChars = dropped > 0 ? all.Skip(from).Take(dropped).Sum(x => x.Text.Length) : 0;
        var result = new List<(TranscriptItem, string)>(head);
        if (dropped > 0)
        {
            var note = $"[external_agent_note]\n옮기는 대화가 길어 중간 기록 {dropped:N0}개 조각(약 {droppedChars:N0}자)을 뺐다.\n" +
                       $"빠진 내용이 필요하면 원본 기록을 읽어라: {t.SourcePath}\n[/external_agent_note]";
            result.Add((new TranscriptItem(ItemKind.Assistant, note), note));
        }
        result.AddRange(tail);
        return result;
    }

    // ── claude 로 ───────────────────────────────────────────────────────────

    /// <summary>
    /// claude 기록 파일로 쓴다 — <c>~/.claude/projects/&lt;폴더&gt;/&lt;새 번호&gt;.jsonl</c>.
    ///
    /// **사용자·어시스턴트가 번갈아 오게 묶는다.** 도구 호출·결과는 모두 어시스턴트 쪽 글로 접는다 —
    /// 그래야 원본에 없던 tool_use/tool_result 짝을 지어낼 필요가 없고, API 가 받는 모양을 벗어나지 않는다.
    /// 어시스턴트 메시지의 모델은 claude 가 자기가 지어낸 메시지에 붙이는 <c>&lt;synthetic&gt;</c> 로 적는다.
    /// </summary>
    /// <returns>새 세션 번호와 파일 경로.</returns>
    public static (string Id, string Path) ToClaude(Transcript t, string cwd, string preface, string? home = null)
    {
        var msgs = Alternate(Budgeted(t), preface);
        var id = Guid.NewGuid().ToString();
        var dir = SessionFinder.ClaudeProjectDir(cwd, home);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, id + ".jsonl");

        var now = DateTime.UtcNow;
        string? parent = null;
        var sb = new StringBuilder();
        for (var n = 0; n < msgs.Count; n++)
        {
            var (user, text) = msgs[n];
            var uuid = Guid.NewGuid().ToString();
            var row = new JsonObject
            {
                ["parentUuid"] = parent,
                ["isSidechain"] = false,
                ["userType"] = "external",
                ["entrypoint"] = "cli",
                ["cwd"] = cwd,
                ["sessionId"] = id,
                ["version"] = ClaudeVersionTag,
                ["gitBranch"] = "",
                ["type"] = user ? "user" : "assistant",
                ["message"] = user
                    ? new JsonObject { ["role"] = "user", ["content"] = text }
                    : new JsonObject
                    {
                        ["id"] = "msg_agentclihub_" + Guid.NewGuid().ToString("N")[..20],
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["model"] = "<synthetic>",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                        ["stop_reason"] = "end_turn",
                        ["stop_sequence"] = null,
                        ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 },
                    },
                ["uuid"] = uuid,
                ["timestamp"] = Stamp(now.AddMilliseconds(n)),
            };
            sb.Append(row.ToJsonString(TranscriptReader.Json)).Append('\n');
            parent = uuid;
        }

        WriteNew(path, sb.ToString());
        return (id, path);
    }

    /// <summary>
    /// claude 기록 줄에 적는 판 표시. 실측에 쓴 판을 적는다 — 이 값으로 claude 가 옛 기록을 옮겨 적는
    /// 일이 있어서, 한참 옛 판을 적으면 쓸데없는 변환을 탈 수 있다.
    /// </summary>
    private const string ClaudeVersionTag = "2.1.278";

    /// <summary>
    /// (사용자?, 글) 줄. 같은 쪽이 잇달아 오면 하나로 합친다. **첫 줄은 늘 사용자다** —
    /// 인계 머리말이 거기 붙는다.
    /// </summary>
    private static List<(bool User, string Text)> Alternate(List<(TranscriptItem Item, string Text)> items, string preface)
    {
        var list = new List<(bool User, string Text)>();
        foreach (var (item, text) in items)
        {
            var user = item.Kind is ItemKind.User or ItemKind.Summary;
            if (list.Count > 0 && list[^1].User == user) list[^1] = (user, list[^1].Text + "\n\n" + text);
            else list.Add((user, text));
        }
        if (list.Count == 0 || !list[0].User) list.Insert(0, (true, preface));
        else list[0] = (true, preface + "\n\n" + list[0].Text);
        return list;
    }

    // ── codex 로 ────────────────────────────────────────────────────────────

    /// <summary>
    /// codex 기록 파일로 쓴다 — <c>&lt;홈&gt;/sessions/YYYY/MM/DD/rollout-…-&lt;새 번호&gt;.jsonl</c>.
    ///
    /// **codex 가 Claude Code 기록을 가져올 때 쓰는 모양(legacy)을 그대로 따른다** — 사용자 말 하나마다 한 턴
    /// (task_started → 사용자 → 어시스턴트 조각들 → task_complete), 조각마다 화면용(event_msg)과
    /// 모델용(response_item)을 한 벌씩. 이 PC 의 codex 가 만든 가져오기 기록이 정확히 이 모양이고,
    /// codex 0.155 가 이 모양의 파일을 이어 연다(실측).
    /// </summary>
    public static (string Id, string Path) ToCodex(Transcript t, string cwd, string home, string preface)
    {
        var items = Budgeted(t);
        var id = Guid.CreateVersion7().ToString();
        var nowUtc = DateTime.UtcNow;
        var local = nowUtc.ToLocalTime();
        var dir = Path.Combine(home, "sessions", local.ToString("yyyy"), local.ToString("MM"), local.ToString("dd"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"rollout-{local:yyyy-MM-dd'T'HH-mm-ss}-{id}.jsonl");

        var sb = new StringBuilder();
        var tick = 0;
        void Row(string type, JsonObject payload) =>
            sb.Append(new JsonObject
            {
                ["timestamp"] = Stamp(nowUtc.AddMilliseconds(tick++)),
                ["type"] = type,
                ["payload"] = payload,
            }.ToJsonString(TranscriptReader.Json)).Append('\n');

        var started = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
        Row("session_meta", new JsonObject
        {
            ["session_id"] = id,
            ["id"] = id,
            ["timestamp"] = Stamp(nowUtc),
            ["cwd"] = cwd,
            ["originator"] = "agentclihub_handoff",
            ["cli_version"] = "0.155.0",
            ["source"] = "cli",
            ["model_provider"] = "openai",
            ["history_mode"] = "legacy",
        });

        // 턴으로 가른다. 사용자 말(요약 포함)이 턴을 연다 — 잇달아 오면 한 말로 합친다.
        var turns = new List<(string User, List<string> Replies)>();
        foreach (var (item, text) in items)
        {
            if (item.Kind is ItemKind.User or ItemKind.Summary)
            {
                if (turns.Count > 0 && turns[^1].Replies.Count == 0)
                    turns[^1] = (turns[^1].User + "\n\n" + text, turns[^1].Replies);
                else turns.Add((text, []));
            }
            else
            {
                if (turns.Count == 0) turns.Add(("", []));
                turns[^1].Replies.Add(text);
            }
        }
        if (turns.Count == 0) turns.Add(("", []));
        turns[0] = ((preface + "\n\n" + turns[0].User).TrimEnd(), turns[0].Replies);

        var n = 0;
        foreach (var (user, replies) in turns)
        {
            var turnId = $"agentclihub-handoff-turn-{++n}";
            Row("event_msg", new JsonObject
            {
                ["type"] = "task_started", ["turn_id"] = turnId, ["started_at"] = started,
                ["model_context_window"] = null, ["collaboration_mode_kind"] = "default",
            });
            Row("event_msg", new JsonObject
            {
                ["type"] = "user_message", ["message"] = user,
                ["local_images"] = new JsonArray(), ["local_audio"] = new JsonArray(), ["text_elements"] = new JsonArray(),
            });
            Row("response_item", new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = user }),
            });
            foreach (var r in replies)
            {
                Row("event_msg", new JsonObject
                {
                    ["type"] = "agent_message", ["message"] = r, ["phase"] = null, ["memory_citation"] = null,
                });
                Row("response_item", new JsonObject
                {
                    ["type"] = "message", ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = r }),
                });
            }
            Row("event_msg", new JsonObject
            {
                ["type"] = "task_complete", ["turn_id"] = turnId, ["last_agent_message"] = null, ["started_at"] = started,
            });
        }

        WriteNew(path, sb.ToString());
        return (id, path);
    }

    // ── 공통 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 받은 쪽 대화의 첫 머리에 붙일 인계문. **어디서 왔는지와 표식의 뜻**을 적는다 — 이것이 없으면
    /// 새 에이전트가 기록 속 도구 호출을 자기가 한 일로 알고, 이미 바뀐 파일을 옛 모습으로 믿는다.
    /// </summary>
    public static string Preface(Transcript t, string from) =>
        $"[AgentCLIHub 인계] 이 대화는 {from} 에서 옮겨 왔다 — 이전 대화의 문맥을 이어간다.\n" +
        "기록 속 [external_agent_tool_call] · [external_agent_tool_result] 는 이전 에이전트가 실제로 실행한 도구와 그 결과다. " +
        "그 뒤로 파일이 바뀌었을 수 있으니 필요하면 다시 확인하라.\n" +
        $"원본 기록: {t.SourcePath}";

    private static string Stamp(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>다 쓴 뒤에 제자리에 놓는다. 반쯤 쓴 파일을 CLI 가 먼저 읽으면 대화가 잘린 채 열린다.</summary>
    private static void WriteNew(string path, string text)
    {
        var tmp = path + ".agentclihub.tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: false);
    }
}

/// <summary>탭이 지금 쓰고 있는 대화 하나.</summary>
public sealed record SessionRef(CliProfile Cli, string Id, string Path, DateTime WhenUtc);

/// <summary>
/// **탭 하나가 지금 어느 대화를 쓰고 있는지** 찾는다. 계정을 바꿔 이어갈 때 무엇을 넘길지가 이것이다.
///
/// 띄울 때의 번호만 믿지 않는다. 탭 안에서 <c>/clear</c> 나 <c>/resume</c> 을 치면 CLI 가 다른 대화로
/// 옮겨 가고 번호가 바뀐다. 그래서 **이 탭이 뜬 뒤에 쓰인 기록 중 가장 최근 것**을 본다 — 다만 같은 폴더의
/// 다른 탭이 쥐고 있는 번호는 뺀다. 띄울 때 번호를 모르는 경우(codex 새 대화)도 이것으로 찾는다.
/// </summary>
public static class SessionFinder
{
    /// <summary>
    /// claude 가 폴더 경로를 접는 방식 — 영문자·숫자 말고는 전부 <c>-</c>. <c>D:\99. Temp\X</c> → <c>D--99--Temp-X</c>.
    /// 한글 한 글자도 <c>-</c> 하나다(UTF-16 한 칸이라서). 되돌릴 수는 없다(SessionStore 참조).
    /// </summary>
    public static string ClaudeSlug(string cwd)
    {
        var sb = new StringBuilder(cwd.Length);
        foreach (var c in cwd) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return sb.ToString();
    }

    /// <summary>
    /// 이 폴더의 claude 기록이 쌓이는 곳. **이미 있는 것을 먼저 따른다** — 아주 긴 경로는 claude 가 잘라
    /// 뒤에 해시를 붙이는데, 그 해시는 우리가 못 만든다. 기존 기록이 있으면 그 폴더가 답이다.
    /// </summary>
    public static string ClaudeProjectDir(string cwd, string? home = null)
    {
        var root = Path.Combine(home ?? CliProfile.ClaudeHome, "projects");
        var slug = ClaudeSlug(Path.TrimEndingDirectorySeparator(cwd));
        var dir = Path.Combine(root, slug);
        if (Directory.Exists(dir) || slug.Length <= 200) return dir;
        try
        {
            return Directory.EnumerateDirectories(root, slug[..200] + "*").FirstOrDefault() ?? dir;
        }
        catch { return dir; }
    }

    /// <param name="startedUtc">그 탭의 CLI 가 뜬 시각. 그 뒤에 쓰인 기록이 이 탭 것일 가능성이 높다.</param>
    /// <param name="knownId">띄울 때 우리가 준 번호(없으면 null).</param>
    /// <param name="claimed">같은 CLI·같은 홈의 **다른 탭**이 쥐고 있는 번호. 이것들은 이 탭 것이 아니다.</param>
    public static SessionRef? Current(CliProfile cli, string cwd, string codexHome, DateTime startedUtc,
                                      string? knownId, ISet<string> claimed, string? claudeHome = null, string? antigravityHome = null)
    {
        if (!cli.SupportsTransfer) return null;
        try
        {
            return cli.Kind switch
            {
                CliKind.Claude => Claude(cwd, startedUtc, knownId, claimed, claudeHome),
                CliKind.Antigravity => AntigravitySessions.Current(cwd, startedUtc, knownId, claimed, antigravityHome),
                _ => Codex(cwd, codexHome, startedUtc, knownId, claimed),
            };
        }
        catch { return null; }
    }

    /// <summary>띄우기 **전에** 그 폴더에서 이어갈 대화 — <c>--continue</c> · <c>resume --last</c> 가 고를 그것.</summary>
    public static SessionRef? Latest(CliProfile cli, string cwd, string codexHome, string? claudeHome = null, string? antigravityHome = null) =>
        Current(cli, cwd, codexHome, DateTime.MaxValue, null, new HashSet<string>(), claudeHome, antigravityHome);

    private static SessionRef? Claude(string cwd, DateTime startedUtc, string? knownId, ISet<string> claimed, string? home)
    {
        var dir = ClaudeProjectDir(cwd, home);
        var known = FindClaude(knownId, home ?? CliProfile.ClaudeHome);
        if (!Directory.Exists(dir)) return known;

        var files = new DirectoryInfo(dir).GetFiles("*.jsonl")
            .Where(f => Guid.TryParse(Stem(f), out _)).ToArray();
        var since = startedUtc == DateTime.MaxValue ? DateTime.MaxValue : startedUtc.AddSeconds(-5);
        var pool = files.Where(f => f.LastWriteTimeUtc >= since && !claimed.Contains(Stem(f)))
                        .Concat(files.Where(f => knownId is not null && Stem(f) == knownId))
                        .ToList();
        var pick = pool.OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        // 폴더 이름 변경·긴 경로·사용자 지정 프로젝트 폴더에서도 정확한 번호를 보존한다.
        if (known is not null && (pick is null || pick.LastWriteTimeUtc < since)) return known;
        // 번호를 알고 있는 탭에서 파일을 못 찾았다고 오래된 다른 대화를 넘기면 안 된다.
        if (pick is null && knownId is null && startedUtc == DateTime.MaxValue)
            pick = files.Where(f => !claimed.Contains(Stem(f))).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        return pick is null ? known : new SessionRef(CliProfile.Claude, Stem(pick), pick.FullName, pick.LastWriteTimeUtc);
    }

    /// <summary>번호로 지정한 Claude 대화만 찾는다. 다른 최신 대화나 subagents 기록으로 대체하지 않는다.</summary>
    public static SessionRef? FindClaude(string? id, string home)
    {
        if (!Guid.TryParse(id, out _)) return null;
        var root = Path.Combine(home, "projects");
        if (!Directory.Exists(root)) return null;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var path = Path.Combine(dir, id + ".jsonl");
            if (File.Exists(path)) return new SessionRef(CliProfile.Claude, id!, path, File.GetLastWriteTimeUtc(path));
        }
        return null;
    }

    /// <summary>지난 대화에서 선택한 항목을 정확하게 찾는다. 없으면 다른 대화를 열지 않는다.</summary>
    public static SessionRef? Find(SessionEntry entry)
    {
        var cli = CliProfile.ForOrDefault(entry.Backend);
        if (cli.Kind == CliKind.Claude) return FindClaude(entry.Id, Accounts.ClaudeHomeOf(entry.Account));
        if (cli.Kind == CliKind.Antigravity)
            return AntigravitySessions.Current(entry.Cwd, DateTime.MaxValue, entry.Id, new HashSet<string>(), AntigravityAccounts.HomeOf(entry.Account)) is { } ag && ag.Id == entry.Id ? ag : null;
        if (!Guid.TryParse(entry.Id, out _)) return null;
        var root = Path.Combine(Accounts.CodexHomeOf(entry.Account), "sessions");
        if (!Directory.Exists(root)) return null;
        foreach (var path in Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories))
            if (path.Contains(entry.Id, StringComparison.OrdinalIgnoreCase) && CodexMeta(path).Id == entry.Id)
                return new SessionRef(cli, entry.Id, path, File.GetLastWriteTimeUtc(path));
        return null;
    }

    private static SessionRef? Codex(string cwd, string home, DateTime startedUtc, string? knownId, ISet<string> claimed)
    {
        var root = Path.Combine(home, "sessions");
        if (!Directory.Exists(root)) return null;

        var want = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        var files = new DirectoryInfo(root).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                                           .OrderByDescending(f => f.LastWriteTimeUtc)
                                           .Take(400)
                                           .ToList();

        (FileInfo File, string Id)? Probe(FileInfo f)
        {
            var (id, fcwd) = CodexMeta(f.FullName);
            if (id.Length == 0 || fcwd.Length == 0) return null;
            var same = string.Equals(Path.TrimEndingDirectorySeparator(fcwd), want, StringComparison.OrdinalIgnoreCase);
            return same ? (f, id) : null;
        }

        var since = startedUtc == DateTime.MaxValue ? DateTime.MaxValue : startedUtc.AddSeconds(-5);
        (FileInfo File, string Id)? best = null;

        // 이 탭이 뜬 뒤에 쓰인 것 · 띄울 때 준 번호.
        foreach (var f in files)
        {
            var fresh = f.LastWriteTimeUtc >= since;
            var known = knownId is not null && f.Name.Contains(knownId, StringComparison.OrdinalIgnoreCase);
            if (!fresh && !known) continue;
            if (Probe(f) is not { } hit) continue;
            if (fresh && claimed.Contains(hit.Id) && !known) continue;
            if (best is null || hit.File.LastWriteTimeUtc > best.Value.File.LastWriteTimeUtc) best = hit;
        }

        // 못 찾았으면 그 폴더의 가장 최근 것(resume --last 가 고를 그것).
        if (best is null)
            foreach (var f in files)
                if (Probe(f) is { } hit && !claimed.Contains(hit.Id)) { best = hit; break; }

        return best is { } b ? new SessionRef(CliProfile.Codex, b.Id, b.File.FullName, b.File.LastWriteTimeUtc) : null;
    }

    /// <summary>rollout 첫 줄(session_meta)의 번호와 폴더. 첫 줄에 지침 본문이 통째로 들어 있어 넉넉히 읽는다.</summary>
    internal static (string Id, string Cwd) CodexMeta(string path)
    {
        try
        {
            using var st = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var rd = new StreamReader(st, Encoding.UTF8);
            var line = rd.ReadLine();
            if (line is null || JsonNode.Parse(line) is not JsonObject o || TranscriptReader.Str(o["type"]) != "session_meta")
                return ("", "");
            var p = o["payload"];
            var id = TranscriptReader.Str(p?["id"]);
            if (id.Length == 0) id = TranscriptReader.Str(p?["session_id"]);
            return (id, TranscriptReader.Str(p?["cwd"]));
        }
        catch { return ("", ""); }
    }

    private static string Stem(FileInfo f) => Path.GetFileNameWithoutExtension(f.Name);
}
