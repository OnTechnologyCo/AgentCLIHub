using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AgentCLIHub.Cli;

/// <summary>
/// AgentCLIHub 창의 브라우저 탭을 부르는 도우미.
///
/// **이 프로그램은 아무것도 안 한다** — 창구(<c>BrowserBridge</c>)에 말을 전하고 답을
/// 그대로 찍는다. 판단은 전부 앱 쪽에 있다. 그래야 여기서 무엇을 보내든 앱이 정한 명령
/// 밖으로는 못 나간다.
///
/// 창구 이름은 앱이 환경변수로 심어 준다. 이 CLI 는 앱이 띄운 터미널의 자식이라
/// 그 값을 그대로 물려받는다 — 앱 밖에서 실행하면 이름이 없어 곧바로 실패한다.
/// </summary>
internal static class Program
{
    private const string PipeEnv = "AGENTCLIHUB_BROWSER_PIPE";

    /// <summary>창구가 요청마다 보는 표. 앱이 창구 이름과 같이 심어 준다(BrowserBridge.Ticket).</summary>
    private const string TicketEnv = "AGENTCLIHUB_BROWSER_TICKET";

    /// <summary>계정 로그인 탭에만 앱이 심는 표시(TerminalHost.PrivateBrowserEnv).</summary>
    private const string PrivateEnv = "AGENTCLIHUB_BROWSER_PRIVATE";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--upgrade" && args[2] == "--report")
            return UpgradeCommand.Run(args[3], () => AgentCLIHub.UpgradeMigration.RunForCurrentUser(args[1]));

        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        // **주소 하나만 오면 `open` 으로 본다.** BROWSER 환경변수 규약이 "<명령> <주소>" 라,
        // 이 exe 경로만 BROWSER 에 넣으면 여기로 주소가 그대로 들어온다.
        // 그 덕에 CLI 가 로그인 페이지를 열 때 시스템 브라우저 대신 이 앱의 탭이 열린다.
        if (args.Length == 1 &&
            (args[0].StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             args[0].StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            // 계정 로그인 탭이면 InPrivate 탭으로 — 앱 브라우저에 로그인된 다른 계정이 섞이지 않게(앱이 심는다).
            args = Environment.GetEnvironmentVariable(PrivateEnv) == "1"
                ? ["open", args[0], "--private"]
                : ["open", args[0]];
        }

        var pipe = Environment.GetEnvironmentVariable(PipeEnv);
        if (string.IsNullOrWhiteSpace(pipe))
        {
            // **이유를 정확히 말한다.** 여기서 뭉개면 에이전트는 명령이 틀린 줄 알고
            // 인자만 바꿔 가며 계속 다시 시도한다.
            Console.Error.WriteLine(
                $"{PipeEnv} 가 없습니다 — 이 명령은 AgentCLIHub 창의 터미널 탭 안에서만 씁니다.");
            return 3;
        }

        try
        {
            // 표가 없어도 보낸다 — 거절과 그 이유는 창구가 정한다(판단은 앱 쪽에만 둔다).
            var ticket = Environment.GetEnvironmentVariable(TicketEnv) ?? "";
            var reply = await CallAsync(pipe, ticket, args[0], args[1..]);

            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var o) && o.GetBoolean();

            if (!ok)
            {
                var err = root.TryGetProperty("error", out var e) ? e.GetString() : "알 수 없는 오류";
                Console.Error.WriteLine(err);
                return 1;
            }

            var result = root.TryGetProperty("result", out var r) ? r : default;

            // 글은 글로 찍는다. JSON 으로 싸서 주면 읽는 쪽이 한 번 더 풀어야 하고,
            // 줄바꿈이 \n 으로 뭉개져 사람도 에이전트도 읽기 나빠진다.
            if (result.ValueKind == JsonValueKind.String) Console.WriteLine(result.GetString());
            else Console.WriteLine(result.ValueKind == JsonValueKind.Undefined ? "{}" : result.ToString());

            return 0;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("AgentCLIHub 창이 응답하지 않습니다 — 창이 떠 있는지 확인하세요.");
            return 4;
        }
        catch (UnauthorizedAccessException)
        {
            // 날것의 "Access to the path is denied." 로는 에이전트가 무엇을 바꿔야 할지 모른다.
            // 창구는 사용자 계정과 Codex 샌드박스 그룹에만 열려 있다(BrowserBridge.Security).
            Console.Error.WriteLine(
                "AgentCLIHub 창구에 붙을 권한이 없습니다 — 이 계정(샌드박스)에는 창구가 열려 있지 않습니다. " +
                "샌드박스 밖에서 실행하도록 사용자에게 승인을 받아 다시 실행하세요.");
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<string> CallAsync(string pipe, string ticket, string cmd, string[] args)
    {
        using var cli = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await cli.ConnectAsync(5000); }
        catch (TimeoutException) { throw new TimeoutException(); }

        await cli.WriteAsync(BuildRequest(ticket, cmd, args));
        await cli.FlushAsync();

        // 화면 캡처·페이지 읽기는 몇 초 걸릴 수 있다. 넉넉히 기다리되 무한정은 아니다 —
        // 답이 영영 안 오면 에이전트가 거기서 멈춘 채로 남는다.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var acc = new MemoryStream();
        var buf = new byte[8192];
        while (true)
        {
            int n;
            try { n = await cli.ReadAsync(buf, cts.Token); }
            catch (OperationCanceledException) { throw new TimeoutException(); }
            if (n <= 0) break;

            for (var i = 0; i < n; i++)
            {
                if (buf[i] == (byte)'\n') return Encoding.UTF8.GetString(acc.ToArray());
                acc.WriteByte(buf[i]);
            }
        }
        if (acc.Length == 0) throw new IOException("창구가 답 없이 끊었습니다.");
        return Encoding.UTF8.GetString(acc.ToArray());
    }

    /// <summary>
    /// 요청 한 줄. **직렬화기를 안 쓴다** — 익명 형식 직렬화는 리플렉션을 타서 AOT 로
    /// 못 자른다. 이 도우미는 설치본에 들어가는데, 런타임을 통째로 안고 가면
    /// 75MB 가 그냥 붙는다. 보낼 것이 두 필드뿐이라 직접 쓰는 편이 싸다.
    /// </summary>
    private static byte[] BuildRequest(string ticket, string cmd, string[] args)
    {
        var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("ticket", ticket);
            w.WriteString("cmd", cmd);
            w.WriteStartArray("args");
            foreach (var a in args) w.WriteStringValue(a);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        buf.WriteByte((byte)'\n');
        return buf.ToArray();
    }

    private const string Usage = """
        agentclihub-browser — AgentCLIHub 창의 브라우저 탭을 부린다.

          tabs                      열려 있는 브라우저 탭 목록
          open <주소>               새 탭을 열고 번호를 돌려준다
          goto <번호> <주소>        그 탭을 다른 주소로 보낸다
          back|forward|reload <번호>
          text <번호>               그 탭에 보이는 글
          eval <번호> <스크립트>    그 페이지 안에서 JS 를 돌린다
          shot <번호> <저장경로>    그 탭 화면을 PNG 로 저장
          close <번호>              탭을 닫는다

        주소 하나만 주면 open 으로 봅니다 — BROWSER 환경변수가 이 exe 를 가리키면
        CLI 가 여는 로그인 페이지가 시스템 브라우저 대신 이 앱의 탭으로 열립니다.

        필요한 데이터를 얻었거나 작업을 마쳤으면 직접 연 작업용 탭을 close 로 닫고,
        tabs 로 정리를 확인한 뒤 답변하세요. open 결과의 closeCommand 를 사용하면 됩니다.
        기존 사용자 탭과 유지 요청한 탭, 로그인·다운로드 등이 진행 중인 탭은 보존하세요.

        eval 은 사용자가 로그인해 둔 실제 세션에서 돕니다 — 되돌리기 어려운 것
        (머지·삭제·상태 전환·댓글 게시)은 먼저 사용자에게 확인하세요.
        """;
}
