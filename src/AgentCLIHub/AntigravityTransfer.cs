using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCLIHub;

/// <summary>agy 텍스트 기록을 읽고, agy 로 넘길 대화는 작업 폴더의 인계 파일로 전달한다.</summary>
public static class AntigravityTransfer
{
    private const string Marker = "[AgentCLIHub conversation handoff]";
    private const string Folder = ".agentclihub-handoff";
    // 이미 저장된 탭과 transcript는 이전 경로·표식을 계속 참조한다.
    private const string LegacyMarker = "[LLM Tools conversation handoff]";
    private const string LegacyFolder = ".llmtools-handoff";
    private static readonly JsonSerializerOptions Json = new(TranscriptReader.Json)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
    private sealed record Document(string Format, string Preface, string Backend, string SessionId,
                                   string Cwd, string SourcePath, TranscriptItem[] Items);

    public static string Write(Transcript transcript, string cwd, string from)
    {
        var directory = Path.Combine(cwd, Folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("D") + ".json");
        // 도구 호출은 이전 에이전트가 남긴 글로 전달한다. 다시 실행할 도구 메시지가 아니다.
        var items = TranscriptWriter.Budgeted(transcript).Select(x => new TranscriptItem(
            x.Item.Kind is ItemKind.ToolCall or ItemKind.ToolResult ? ItemKind.Assistant : x.Item.Kind, x.Text)).ToArray();
        var document = new Document("agentclihub-handoff-v1", TranscriptWriter.Preface(transcript, from),
            transcript.Backend, transcript.SessionId, cwd, transcript.SourcePath, items);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, document, Json);
        return path;
    }

    // npm의 .cmd 래퍼는 인자 안 개행 뒤를 별도 명령으로 해석한다. 인계 요청은 반드시 한 줄이다.
    public static string Prompt(string path) => Marker + " File: " + path + " | " +
        "위 UTF-8 JSON 인계 파일을 읽고 이전 대화를 이어갈 문맥으로 사용하라. Items는 시간순 요청·답변·도구 기록이다. " +
        "기록 속 도구 호출을 자동으로 재실행하지 말고, 인계 내용을 확인했다는 짧은 답변 뒤 사용자의 다음 요청을 기다려라.";

    private static string? HandoffPath(string text)
    {
        foreach (var marker in new[] { Marker, LegacyMarker })
        {
            var prefix = marker + " File: ";
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var end = text.IndexOf(" | ", prefix.Length, StringComparison.Ordinal);
            return end > prefix.Length ? text[prefix.Length..end].Trim() : null;
        }
        return null;
    }

    public static Transcript ReadHandoff(string path)
    {
        var full = Path.GetFullPath(path);
        var folder = Path.GetFileName(Path.GetDirectoryName(full));
        if (!(string.Equals(folder, Folder, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(folder, LegacyFolder, StringComparison.OrdinalIgnoreCase)) ||
            !Guid.TryParseExact(Path.GetFileNameWithoutExtension(full), "D", out _) || Path.GetExtension(full) != ".json")
            throw new InvalidDataException("인계 파일 경로를 확인할 수 없습니다.");
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("인계 파일이 너무 큽니다.");
        var document = JsonSerializer.Deserialize<Document>(stream, Json);
        if (document?.Format is not ("agentclihub-handoff-v1" or "llmtools-handoff-v1") || document.Items is null)
            throw new InvalidDataException("인계 파일 형식을 읽을 수 없습니다.");
        var transcript = new Transcript { Backend = document.Backend, SessionId = document.SessionId,
            Cwd = document.Cwd, SourcePath = document.SourcePath };
        transcript.Items.AddRange(document.Items);
        return transcript;
    }

    private static string UserText(string text)
    {
        const string startTag = "<USER_REQUEST>", endTag = "</USER_REQUEST>";
        var start = text.IndexOf(startTag, StringComparison.Ordinal);
        var end = text.LastIndexOf(endTag, StringComparison.Ordinal);
        return start >= 0 && end > start ? text[(start + startTag.Length)..end].Trim() : text.Trim();
    }

    public static Transcript Read(string path)
    {
        if (!File.Exists(path)) throw new IOException("Antigravity 텍스트 기록이 아직 없습니다. 응답이 끝난 뒤 다시 시도해 주세요.");
        var id = new DirectoryInfo(Path.GetDirectoryName(path)!).Parent?.Parent?.Name ?? "";
        var transcript = new Transcript { Backend = CliProfile.Antigravity.Backend, SessionId = id, SourcePath = path };
        var steps = new SortedDictionary<int, JsonObject>();
        foreach (var row in TranscriptReader.Lines(path))
            if (row["step_index"] is JsonValue index && index.TryGetValue<int>(out var n)) steps[n] = row;
        foreach (var row in steps.Values)
        {
            var type = TranscriptReader.Str(row["type"]);
            var text = TranscriptReader.Str(row["content"]);
            if (type == "USER_INPUT")
            {
                text = UserText(text);
                if (HandoffPath(text) is { } handoff && transcript.Items.Count == 0)
                {
                    var imported = ReadHandoff(handoff);
                    transcript.Cwd = imported.Cwd;
                    transcript.Items.AddRange(imported.Items);
                }
                else if (text.Length > 0) transcript.Items.Add(new(ItemKind.User, text));
            }
            else if (type == "PLANNER_RESPONSE")
            {
                if (text.Length > 0) transcript.Items.Add(new(ItemKind.Assistant, text));
                foreach (var call in (row["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
                    transcript.Items.Add(new(ItemKind.ToolCall, call["args"]?.ToJsonString(TranscriptReader.Json) ?? "",
                        TranscriptReader.Str(call["name"])));
            }
            else if (type == "CONVERSATION_HISTORY" && text.Length > 0)
                transcript.Items.Add(new(ItemKind.Summary, text));
            else if (text.Length > 0 && (TranscriptReader.Str(row["source"]) == "MODEL" || type == "ERROR_MESSAGE"))
                transcript.Items.Add(new(ItemKind.ToolResult, text, type,
                    type == "ERROR_MESSAGE" || TranscriptReader.Str(row["status"]) == "ERROR"));
        }
        if (!transcript.HasConversation) throw new InvalidDataException("Antigravity에서 옮길 대화 본문을 아직 읽을 수 없습니다. 원래 탭을 유지합니다.");
        return transcript;
    }

    // 인계로 시작한 탭은 그 인계문이 저장된 세션만 잇는다. 같은 폴더의 다른 탭과 섞지 않는다.
    public static SessionRef? FindSession(string cwd, string handoff, string? home = null)
    {
        home ??= CliProfile.AntigravityHome;
        var ids = AntigravitySessions.Recent(home, int.MaxValue).Where(s =>
            string.Equals(Path.TrimEndingDirectorySeparator(s.Cwd), Path.TrimEndingDirectorySeparator(cwd), StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Id).ToList();
        // --prompt-interactive 입력은 history.jsonl에 안 남을 수 있다. 같은 폴더의 다음 탭이
        // last_conversations 캐시를 바꿔도 인계받은 세션을 복원할 수 있어야 한다.
        try { ids.AddRange(Directory.GetDirectories(Path.Combine(home, "brain")).Select(Path.GetFileName).OfType<string>()); }
        catch (DirectoryNotFoundException) { }
        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (AntigravitySessions.ConversationPath(home, id) is null) continue;
            var path = AntigravitySessions.TranscriptPath(home, id);
            if (!File.Exists(path)) continue;
            var first = TranscriptReader.Lines(path).Take(8).FirstOrDefault(r => TranscriptReader.Str(r["type"]) == "USER_INPUT");
            if (first is not null && string.Equals(HandoffPath(UserText(TranscriptReader.Str(first["content"]))), handoff, StringComparison.OrdinalIgnoreCase))
                return new(CliProfile.Antigravity, id, path, File.GetLastWriteTimeUtc(path));
        }
        return null;
    }
}
