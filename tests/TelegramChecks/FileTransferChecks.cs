using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using AgentCLIHub;

internal static class FileTransferChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        const string token = "123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw";
        var path = Path.Combine(root, "한글 결과.bin");
        var bytes = new byte[] { 0, 255, 34, 13, 10, 42 };
        await File.WriteAllBytesAsync(path, bytes);
        var uploads = 0;
        using (var api = new TelegramApi(token, new Handler(async (request, ct) =>
        {
            check(request.RequestUri!.AbsolutePath.EndsWith("/sendDocument"), "file uses sendDocument");
            var form = (MultipartFormDataContent)request.Content!;
            var chat = form.Single(c => c.Headers.ContentDisposition?.Name?.Trim('"') == "chat_id");
            var file = form.Single(c => c.Headers.ContentDisposition?.Name?.Trim('"') == "document");
            check(await chat.ReadAsStringAsync(ct) == "777" &&
                (await file.ReadAsByteArrayAsync(ct)).SequenceEqual(bytes) &&
                file.Headers.ContentDisposition!.FileNameStar == Path.GetFileName(path),
                "multipart keeps recipient, Unicode filename and binary bytes on every attempt");
            return ++uploads == 1
                ? Json("{\"ok\":false,\"description\":\"slow down\",\"parameters\":{\"retry_after\":1}}", HttpStatusCode.TooManyRequests)
                : Json("{\"ok\":true,\"result\":{\"message_id\":42}}");
        })))
        {
            check(await api.SendDocumentAsync(777, path) == 42 && uploads == 2,
                "429 retries the complete upload once and returns Telegram message id");
        }

        var called = 0;
        using (var api = new TelegramApi(token, new Handler((_, _) =>
        {
            called++;
            throw new HttpRequestException("failed https://api.telegram.org/bot" + token + "/sendDocument");
        })))
        {
            var missing = await Failure(() => api.SendDocumentAsync(777, path + ".missing"));
            check(missing is FileNotFoundException && called == 0, "missing file fails before HTTP");
            var empty = Path.Combine(root, "empty.bin");
            File.WriteAllBytes(empty, []);
            check(await Failure(() => api.SendDocumentAsync(777, empty)) is IOException && called == 0,
                "empty file fails before HTTP");
            var large = Path.Combine(root, "large.bin");
            using (var f = File.Create(large)) f.SetLength(TelegramApi.MaxUpload + 1);
            check(await Failure(() => api.SendDocumentAsync(777, large)) is IOException && called == 0,
                "file over 50 MB fails before HTTP");
            var failure = await Failure(() => api.SendDocumentAsync(777, path));
            check(failure is HttpRequestException && !failure.ToString().Contains(token) && called == 1,
                "upload network errors hide token and do not retry uncertain delivery");
        }
        using (var api = new TelegramApi(token, new Handler((_, _) =>
            Task.FromResult(Json("{\"ok\":false,\"description\":\"blocked " + token + "\"}", HttpStatusCode.Forbidden)))))
        {
            var failure = await Failure(() => api.SendDocumentAsync(777, path));
            check(failure is TelegramException { Status: 403 } && !failure.Message.Contains(token),
                "Telegram upload rejection propagates without token");
        }
        using (var api = new TelegramApi(token, new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        })))
        {
            using var cancel = new CancellationTokenSource(50);
            check(await Failure(() => api.SendDocumentAsync(777, path, cancel.Token)) is OperationCanceledException,
                "upload respects caller cancellation");
        }
        using (var api = new TelegramApi(token, new Handler((_, _) => Task.FromResult(Json("{\"ok\":true,\"result\":{}}")))))
            check(await Failure(() => api.SendDocumentAsync(777, path)) is IOException,
                "upload does not report success without a Telegram message id");

        foreach (var mode in new[] { "ok", "metadata", "length", "stream", "network", "cancel" })
        {
            var dest = Path.Combine(root, "download-" + mode);
            using var api = new TelegramApi(token, new Handler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/getFile"))
                    return Task.FromResult(Json("{\"ok\":true,\"result\":{\"file_path\":\"files/test.bin\",\"file_size\":" +
                        (mode == "metadata" ? TelegramApi.MaxDownload + 1 : bytes.Length) + "}}"));
                if (mode == "network") throw new HttpRequestException("failed " + request.RequestUri);
                HttpContent content = mode is "length" or "stream"
                    ? new StreamContent(new MemoryStream(new byte[checked((int)TelegramApi.MaxDownload + 1)]))
                    : mode == "cancel" ? new StreamContent(new CancelledStream()) : new ByteArrayContent(bytes);
                if (mode == "length") content.Headers.ContentLength = TelegramApi.MaxDownload + 1;
                if (mode == "stream") content.Headers.ContentLength = bytes.Length; // 크기 헤더가 거짓이어도 실제 바이트로 막는다.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }));
            if (mode == "ok")
            {
                await api.DownloadAsync("file-id", dest, CancellationToken.None);
                check(File.ReadAllBytes(dest).SequenceEqual(bytes), "download preserves binary content");
                File.WriteAllText(dest, "existing file");
                check(await Failure(() => api.DownloadAsync("file-id", dest, CancellationToken.None)) is IOException &&
                    File.ReadAllText(dest) == "existing file" && !File.Exists(dest + ".part"),
                    "download never overwrites an existing destination and cleans temporary data");
            }
            else
            {
                var failure = await Failure(() => api.DownloadAsync("file-id", dest, CancellationToken.None));
                check(!File.Exists(dest) && !File.Exists(dest + ".part") && !failure.ToString().Contains(token),
                    "download " + mode + " failure leaves no partial file or exposed token");
            }
        }
        check(TelegramRemote.FileRecipient([777], null) == 777 && TelegramRemote.FileRecipient([777, 888], 888) == 888,
            "file recipient resolves only unique or explicit paired chat");
        foreach (var (chats, recipient) in new (long[], long?)[] { ([], null), ([777, 888], null), ([777], 999) })
        {
            var failure = await Failure(() => Task.FromResult(TelegramRemote.FileRecipient(chats, recipient)));
            check(failure is InvalidOperationException or UnauthorizedAccessException,
                "missing, ambiguous or unpaired recipient is rejected");
        }
    }

    private static async Task<Exception> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { return ex; }
        throw new Exception("Expected failure");
    }

    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class CancelledStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromException<int>(new OperationCanceledException());
    }
}
