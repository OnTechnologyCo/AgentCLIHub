using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LlmTools;

/// <summary>agy 의 이력/작업 폴더 캐시에서 이어하기 목록을 읽는다. 네이티브 기록(.pb/.db)은 수정하지 않는다.</summary>
public static class AntigravitySessions
{
    private const int MaxBytes = 4 * 1024 * 1024;

    public static IReadOnlyList<SessionEntry> Recent(string? home = null, int max = 120)
    {
        home ??= CliProfile.AntigravityHome;
        var entries = new Dictionary<string, SessionEntry>(StringComparer.OrdinalIgnoreCase);

        string? Conversation(string id) => ConversationPath(home, id);

        void Add(string id, string cwd, string title, DateTime when)
        {
            if (!Path.IsPathFullyQualified(cwd) || Conversation(id) is not { } file || !File.Exists(file)) return;
            cwd = Path.GetFullPath(cwd);
            if (entries.TryGetValue(id, out var old))
            {
                // 입력 이력은 한 대화에 여러 줄이다. 제목은 첫 질문, 시각은 가장 최근 입력이다.
                if (old.When > when) when = old.When;
                if (!old.Title.StartsWith("(첫 발화 없음)", StringComparison.Ordinal)) title = old.Title;
            }
            if (title.Length == 0) title = "(첫 발화 없음) " + Path.GetFileName(Path.TrimEndingDirectorySeparator(cwd));
            entries[id] = new SessionEntry(CliProfile.Antigravity.Backend, id, cwd, when, title);
        }

        try
        {
            var file = Path.Combine(home, "history.jsonl");
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var offset = Math.Max(0, stream.Length - MaxBytes);
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (offset > 0) reader.ReadLine(); // 잘린 첫 줄은 버린다.
            var remaining = MaxBytes;
            while (remaining > 0 && reader.ReadLine() is { } line)
            {
                remaining -= line.Length + 1;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var o = doc.RootElement;
                    if (!o.TryGetProperty("conversationId", out var id) || !o.TryGetProperty("workspace", out var cwd)) continue;
                    var title = o.TryGetProperty("display", out var display) ? display.GetString() ?? "" : "";
                    title = title.Replace('\r', ' ').Replace('\n', ' ').Trim();
                    if (title.StartsWith('/')) title = "";
                    if (title.Length > 110) title = title[..110] + "…";
                    var when = DateTimeOffset.FromUnixTimeMilliseconds(o.GetProperty("timestamp").GetInt64()).LocalDateTime;
                    Add(id.GetString() ?? "", cwd.GetString() ?? "", title, when);
                }
                catch { /* 쓰는 중인 마지막 줄/예전 형식 한 줄이 전체 목록을 막지 않는다. */ }
            }
        }
        catch { /* 입력 이력이 없어도 CLI 의 작업 폴더 캐시를 읽는다. */ }

        try
        {
            var file = Path.Combine(home, "cache", "last_conversations.json");
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= MaxBytes)
            {
                using var doc = JsonDocument.Parse(stream);
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    try
                    {
                        var id = entry.Value.GetString() ?? "";
                        if (entries.ContainsKey(id) || Conversation(id) is not { } conversation) continue;
                        Add(id, entry.Name, "", File.GetLastWriteTime(conversation));
                    }
                    catch { }
                }
            }
        }
        catch { }
        return entries.Values.OrderByDescending(e => e.When).Take(max).ToArray();
    }

    public static string? ConversationPath(string home, string id)
    {
        if (!Guid.TryParseExact(id, "D", out _)) return null;
        return new[] { ".db", ".pb" }.Select(ext => Path.Combine(home, "conversations", id + ext)).FirstOrDefault(File.Exists);
    }

    public static string TranscriptPath(string home, string id)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new ArgumentException("잘못된 Antigravity 대화 번호입니다.");
        return Path.Combine(home, "brain", id, ".system_generated", "logs", "transcript.jsonl");
    }

    public static SessionRef? Current(string cwd, DateTime startedUtc, string? knownId, ISet<string> claimed, string? home = null)
    {
        home ??= CliProfile.AntigravityHome;
        var want = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        var sessions = Recent(home).Where(s => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(s.Cwd)), want, StringComparison.OrdinalIgnoreCase)).ToList();
        var known = sessions.FirstOrDefault(s => s.Id == knownId);
        var since = startedUtc == DateTime.MaxValue ? DateTime.MinValue : startedUtc.AddSeconds(-5);
        var pick = known ?? sessions.Where(s => !claimed.Contains(s.Id) && s.When.ToUniversalTime() >= since)
                                   .OrderByDescending(s => s.When).FirstOrDefault();
        return pick is null ? null : new SessionRef(CliProfile.Antigravity, pick.Id, TranscriptPath(home, pick.Id), pick.When.ToUniversalTime());
    }
}
