using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentCLIHub;

/// <summary>텔레그램 인라인 단추 하나. <paramref name="Data"/> 는 64바이트를 넘지 않게 짧게 쓴다(Bot API 한도).</summary>
public sealed record TelegramButton(string Text, string Data);

/// <summary>Bot API 가 거절한 호출. <see cref="Message"/> 에 토큰은 들어가지 않는다.</summary>
public sealed class TelegramException(string method, int status, string description, int? retryAfter = null)
    : Exception($"{method}: {description}")
{
    public string Method { get; } = method;
    public int Status { get; } = status;
    public string Description { get; } = description;
    public int? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// 텔레그램 Bot API 의 아주 작은 부분.
///
/// **밖으로 나가는 HTTPS 만 쓴다**(getUpdates 롱 폴링). 이 PC 에 들어오는 포트를 열지 않으므로
/// 공유기·방화벽·웹훅 주소가 필요 없다 — 집 PC 처럼 공인 주소가 없는 곳에서도 그대로 돈다.
///
/// 글은 기본적으로 parse_mode 없이 보낸다. Markdown 으로 보내면 CLI 답에 섞인 `*`·`_`·`[` 하나로
/// 메시지 전체가 거절되고, 그때 사용자에게는 "답이 안 온다" 로만 보인다. 화면 글처럼 고정폭이
/// 필요한 것만 HTML(&lt;pre&gt;)로 보내고, 그때는 반드시 <see cref="Html"/> 로 감싼다.
///
/// **토큰은 예외·기록에 남기지 않는다.** 주소에 토큰이 들어가므로 HttpClient 의 오류 문구에
/// 섞여 나올 수 있다 — <see cref="Scrub"/> 로 지운 뒤에만 밖으로 낸다.
/// </summary>
public sealed class TelegramApi : IDisposable
{
    /// <summary>한 메시지에 담을 글 길이. Bot API 한도(4096)보다 여유를 둔다 — 잘라 붙이는 머리말 몫이다.</summary>
    public const int MaxText = 3900;

    /// <summary>봇이 받을 수 있는 파일 크기 한도(Bot API getFile 은 20MB 까지).</summary>
    public const long MaxDownload = 20L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly string _base;
    private readonly string _fileBase;

    public TelegramApi(string token, HttpMessageHandler? handler = null)
    {
        _token = token;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // 롱 폴링(25초)보다 넉넉해야 한다. 짧으면 기다리는 도중에 끊겨 매번 오류로 보인다.
        _http.Timeout = TimeSpan.FromSeconds(75);
        _base = $"https://api.telegram.org/bot{token}/";
        _fileBase = $"https://api.telegram.org/file/bot{token}/";
    }

    public static bool LooksLikeToken(string? token) =>
        token is { Length: > 20 and < 200 } &&
        System.Text.RegularExpressions.Regex.IsMatch(token, @"^\d{5,15}:[A-Za-z0-9_-]{30,}$");

    /// <summary>화면에 보일 토큰 모양. 봇 번호만 남긴다.</summary>
    public static string Mask(string? token) =>
        token is { Length: > 0 } && token.IndexOf(':') is var at && at > 0 ? token[..at] + ":••••••" : "";

    public string Scrub(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace(_token, Mask(_token), StringComparison.Ordinal);

    /// <summary>HTML 모드로 보낼 글을 감싼다. 텔레그램 HTML 은 &amp; &lt; &gt; 만 막으면 된다.</summary>
    public static string Html(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    public static JsonObject Keyboard(IEnumerable<IEnumerable<TelegramButton>> rows)
    {
        var arr = new JsonArray();
        foreach (var row in rows)
        {
            var r = new JsonArray();
            foreach (var b in row) r.Add(new JsonObject { ["text"] = b.Text, ["callback_data"] = b.Data });
            if (r.Count > 0) arr.Add(r);
        }
        return new JsonObject { ["inline_keyboard"] = arr };
    }

    private async Task<JsonNode?> CallAsync(string method, JsonObject body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                res = await _http.PostAsync(_base + method, content, ct);
            }
            catch (HttpRequestException ex) { throw new HttpRequestException(Scrub(ex.Message), null, ex.StatusCode); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"{method}: 응답 시간이 지났습니다."); }

            using (res)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                JsonNode? node = null;
                try { node = JsonNode.Parse(text); } catch { }
                if (node?["ok"]?.GetValue<bool>() == true) return node["result"];

                var desc = Scrub(node?["description"]?.GetValue<string>() ?? $"HTTP {(int)res.StatusCode}");
                int? retry = node?["parameters"]?["retry_after"] is JsonValue v && v.TryGetValue<int>(out var s) ? s : null;
                // 너무 자주 보냈다(429). **한 번은 기다렸다 다시 보낸다** — 진행 상황 갱신이 몰릴 때 흔하다.
                if ((int)res.StatusCode == 429 && retry is > 0 and <= 30 && attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(retry.Value), ct);
                    continue;
                }
                throw new TelegramException(method, (int)res.StatusCode, desc, retry);
            }
        }
    }

    public async Task<(long Id, string Username, string Name)> GetMeAsync(CancellationToken ct)
    {
        var me = await CallAsync("getMe", new JsonObject(), ct);
        return (me?["id"]?.GetValue<long>() ?? 0, me?["username"]?.GetValue<string>() ?? "", me?["first_name"]?.GetValue<string>() ?? "");
    }

    public async Task<JsonArray> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["timeout"] = timeoutSeconds,
            ["allowed_updates"] = new JsonArray("message", "callback_query"),
        };
        if (offset != 0) body["offset"] = offset;
        return await CallAsync("getUpdates", body, ct) as JsonArray ?? new JsonArray();
    }

    /// <summary>메시지를 보내고 그 번호를 돌려준다(나중에 고쳐 쓰려고).</summary>
    public async Task<long> SendMessageAsync(long chat, string text, JsonObject? markup = null, bool html = false,
                                             CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["chat_id"] = chat,
            ["text"] = text.Length == 0 ? "…" : text,
            ["link_preview_options"] = new JsonObject { ["is_disabled"] = true },
        };
        if (html) body["parse_mode"] = "HTML";
        if (markup is not null) body["reply_markup"] = markup;
        var msg = await CallAsync("sendMessage", body, ct);
        return msg?["message_id"]?.GetValue<long>() ?? 0;
    }

    /// <summary>보낸 메시지를 고쳐 쓴다. 바뀐 것이 없다는 거절은 성공으로 본다.</summary>
    public async Task EditMessageAsync(long chat, long message, string text, JsonObject? markup = null, bool html = false,
                                       CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["chat_id"] = chat,
            ["message_id"] = message,
            ["text"] = text.Length == 0 ? "…" : text,
            ["link_preview_options"] = new JsonObject { ["is_disabled"] = true },
        };
        if (html) body["parse_mode"] = "HTML";
        // 단추를 없애려면 빈 키보드를 줘야 한다. 키를 빼면 이전 단추가 그대로 남는다.
        body["reply_markup"] = markup ?? new JsonObject { ["inline_keyboard"] = new JsonArray() };
        try { await CallAsync("editMessageText", body, ct); }
        catch (TelegramException ex) when (ex.Description.Contains("not modified", StringComparison.OrdinalIgnoreCase)) { }
    }

    public Task AnswerCallbackAsync(string id, string? text = null, CancellationToken ct = default)
    {
        var body = new JsonObject { ["callback_query_id"] = id };
        if (!string.IsNullOrEmpty(text)) body["text"] = text;
        return CallAsync("answerCallbackQuery", body, ct);
    }

    public Task SendChatActionAsync(long chat, string action, CancellationToken ct = default) =>
        CallAsync("sendChatAction", new JsonObject { ["chat_id"] = chat, ["action"] = action }, ct);

    /// <summary>대화창의 명령 메뉴(/ 를 누르면 뜨는 목록)를 건다.</summary>
    public Task SetCommandsAsync(IEnumerable<(string Command, string Description)> commands, CancellationToken ct = default)
    {
        var arr = new JsonArray();
        foreach (var (c, d) in commands) arr.Add(new JsonObject { ["command"] = c, ["description"] = d });
        return CallAsync("setMyCommands", new JsonObject { ["commands"] = arr }, ct);
    }

    /// <summary>
    /// 받은 파일을 <paramref name="destination"/> 에 내려받는다. **크기를 두 번 본다** — 메시지에 적힌 크기와
    /// 실제로 내려오는 바이트. 적힌 크기만 믿으면 큰 파일이 디스크를 채울 수 있다.
    /// </summary>
    public async Task DownloadAsync(string fileId, string destination, CancellationToken ct)
    {
        var file = await CallAsync("getFile", new JsonObject { ["file_id"] = fileId }, ct);
        var path = file?["file_path"]?.GetValue<string>();
        var size = file?["file_size"]?.GetValue<long>() ?? 0;
        if (string.IsNullOrEmpty(path)) throw new IOException("텔레그램이 파일 위치를 주지 않았습니다.");
        if (size > MaxDownload) throw new IOException("파일이 20MB 를 넘습니다.");

        using var res = await _http.GetAsync(_fileBase + path, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new IOException($"파일을 받지 못했습니다(HTTP {(int)res.StatusCode}).");
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        var tmp = destination + ".part";
        try
        {
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > MaxDownload) throw new IOException("파일이 20MB 를 넘습니다.");
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            File.Move(tmp, destination, overwrite: false);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    public void Dispose() => _http.Dispose();
}
