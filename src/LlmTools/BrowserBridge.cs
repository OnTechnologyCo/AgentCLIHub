using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LlmTools;

/// <summary>
/// 탭 안에서 도는 CLI 가 브라우저 탭을 부릴 수 있게 열어 두는 창구.
///
/// **왜 파이프인가.** CLI 는 ConPTY 안에서 돌고, 그 안에서 밖으로 말할 수 있는 것은
/// 셸 명령뿐이다. 그래서 작은 도우미(<c>llmtools-browser.exe</c>)를 하나 두고,
/// 그것이 이 파이프로 우리에게 말한다. MCP 로도 되지만 프로토콜을 한 벌 더 지고 가야 하고,
/// 파이프 쪽은 **사람이 터미널에서 직접 쳐 볼 수 있어** 고장 났을 때 원인이 드러난다.
///
/// **JS 를 받지 않는다.** 명령은 정해진 몇 개뿐이고, 페이지 글을 뽑는 스크립트도 우리가
/// 들고 있는 고정된 것이다. 여기 열려 있는 것은 사용자가 Jira·GitHub 에 로그인해 둔
/// 바로 그 세션이라, 임의 코드를 받으면 그 계정으로 무엇이든 할 수 있게 된다.
///
/// 이름은 앱 프로세스마다 다르다 — 창을 두 개 띄워도 서로의 탭을 건드리지 않는다.
/// </summary>
public sealed class BrowserBridge : IDisposable
{
    /// <summary>한 줄 요청 = 한 줄 응답. 줄이 길어질 수 있어 넉넉히 잡는다.</summary>
    private const int MaxRequestBytes = 64 * 1024;

    /// <summary>
    /// 명령 하나가 물고 있을 수 있는 최대 시간.
    ///
    /// **이것이 없으면 명령 하나가 창구를 통째로 죽인다.** 손님을 한 번에 하나만 받으므로,
    /// 안 끝나는 명령이 하나 생기면 그 뒤의 모든 명령이 "창이 응답하지 않습니다" 로만 보이고
    /// 앱을 껐다 켜기 전에는 안 풀린다. 실제로 그랬다 — 화면에 없는 탭에 `shot` 을 걸면
    /// WebView2 의 <c>CapturePreviewAsync</c> 는 영영 안 끝난다.
    ///
    /// 시간이 지나면 **이유를 적어 돌려주고 다음 손님을 받는다.** 늦은 작업은 배경에서 저 혼자
    /// 끝나거나 말거나 하게 둔다 — 창구를 살려 두는 쪽이 언제나 낫다.
    /// </summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<string, string[], Task<object>> _handle;
    private readonly Action<string, string> _log;
    private readonly CancellationTokenSource _stop = new();

    public string PipeName { get; }

    /// <summary>
    /// 요청마다 같이 와야 하는 표. **앱을 켤 때마다 새로 뽑는다.**
    ///
    /// 왜 필요한가: Codex 가 브라우저를 쓰려면 창구를 Codex 샌드박스 계정에도 열어야 한다
    /// (<see cref="Security"/>). 그런데 그 계정은 이 앱과 상관없이 **이 PC 의 모든 Codex 세션**이
    /// 같이 쓴다 — 표가 없으면 앱 밖에서 돌던 Codex 가 (저장소에 심긴 지시 한 줄에 넘어가) 파이프를
    /// 찾아 붙어, 사용자가 로그인해 둔 Jira·GitLab 에서 JS 를 돌릴 수 있게 된다. 파이프 이름은 누구나
    /// 훑어볼 수 있어 비밀이 못 된다. 표는 이 앱이 띄운 터미널의 자식에게만 환경변수로 건너가므로
    /// (<see cref="TerminalHost"/>), 창구를 쓸 수 있는 것이 다시 "이 앱 안의 탭" 으로 좁혀진다.
    /// </summary>
    public string Ticket { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    /// <summary>
    /// 표를 싣는 환경변수. 이름에 KEY·SECRET·TOKEN 을 **넣지 않는다** — Codex 는 기본값으로 그런 이름의
    /// 변수를 자기가 돌리는 명령에서 지우므로(shell_environment_policy), 넣으면 Codex 에서만 표가 사라진다.
    /// </summary>
    public const string TicketEnv = "LLMTOOLS_BROWSER_TICKET";

    /// <summary>도우미 실행 파일. 지침에 절대 경로로 적어 준다 — PATH 를 건드리지 않는다.</summary>
    public static string HelperPath =>
        Path.Combine(AppContext.BaseDirectory, "agent", "llmtools-browser.exe");

    public BrowserBridge(Func<string, string[], Task<object>> handle, Action<string, string> log)
    {
        _handle = handle;
        _log = log;
        PipeName = "llmtools-browser-" + Environment.ProcessId;
    }

    public void Start() => _ = Task.Run(AcceptLoop);

    /// <summary>
    /// 한 번에 한 손님만 받는다. 명령이 드물고(에이전트가 가끔 부른다) 전부 UI 스레드로
    /// 넘어가므로 줄을 세우는 편이 낫다 — 동시에 받아도 결국 같은 곳에서 한 줄로 선다.
    /// </summary>
    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? srv = null;
            try
            {
                srv = NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());

                await srv.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                await ServeAsync(srv).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // 손님 하나가 끊어졌다고 창구를 닫지 않는다.
                _log("브라우저 창구 오류: " + ex.Message, "w");
                await Task.Delay(200).ConfigureAwait(false);
            }
            finally { try { srv?.Dispose(); } catch { } }
        }
    }

    /// <summary>
    /// 파이프 권한. Codex 샌드박스 안에서 도는 도우미도 붙을 수 있게 **딱 그만큼만** 연다.
    ///
    /// 기본 권한으로 두면 Codex 가 돌리는 명령은 읽기만 되고 쓰기가 막혀, 도우미가
    /// "Access to the path is denied" 로 끝난다(실측). Codex 의 Windows 샌드박스는 명령을
    /// **쓰기 제한 토큰**(write-restricted)으로 돌리기 때문이다 — 쓰기는 평소 검사에 더해
    /// "제한 목록" 의 SID 로도 한 번 더 통과해야 한다. 토큰을 찍어 보니(codex 0.154):
    ///
    ///   elevated   계정 CodexSandboxOffline/Online(그룹 CodexSandboxUsers) ·
    ///              제한 목록 = { Codex 능력 SID, 그 계정, **부른 쪽의 로그온 SID**, Everyone }
    ///   unelevated 계정 = 사용자 자신 · 제한 목록 = { Codex 능력 SID, **부른 쪽의 로그온 SID**, Everyone }
    ///
    /// 그래서 셋을 연다: 나(전부) · CodexSandboxUsers(elevated 의 평소 검사) · **내 로그온 SID**
    /// (두 방식 공통의 제한 검사). 로그온 SID 는 이 로그온 세션의 프로세스에만 있는 것이라 다른
    /// 사용자 계정에는 안 열린다. Everyone 은 열지 않는다 — 제한 목록에 있어 쉬워 보이지만 그러면
    /// PC 의 모든 계정에 열린다. 기본 권한에 있던 "Everyone 읽기" 도 뺀다 — 쓸 곳이 없다.
    /// 여는 대신 요청마다 <see cref="Ticket"/> 을 본다(앱 밖에서 돈 Codex 도 같은 SID 를 갖기 때문이다).
    ///
    /// 손님을 받을 때마다 새로 만든다 — 앱을 켠 뒤에 Codex 샌드박스를 처음 설정했을 수 있다.
    /// </summary>
    private static PipeSecurity Security()
    {
        const PipeAccessRights rw = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
                                            PipeAccessRights.FullControl, AccessControlType.Allow));
        if (CodexSandboxGroup() is { } g) ps.AddAccessRule(new PipeAccessRule(g, rw, AccessControlType.Allow));
        if (LogonSid() is { } l) ps.AddAccessRule(new PipeAccessRule(l, rw, AccessControlType.Allow));
        return ps;
    }

    /// <summary>
    /// 이 프로세스의 로그온 SID(S-1-5-5-X-Y). **토큰에서 직접 읽는다** —
    /// <see cref="WindowsIdentity.Groups"/> 는 이것을 빼고 준다.
    /// </summary>
    private static SecurityIdentifier? LogonSid()
    {
        const int TokenLogonSid = 28;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            GetTokenInformation(id.Token, TokenLogonSid, IntPtr.Zero, 0, out var len);
            if (len <= 0) return null;

            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetTokenInformation(id.Token, TokenLogonSid, buf, len, out _)) return null;
                // TOKEN_GROUPS: DWORD GroupCount, 그리고 포인터 정렬된 SID_AND_ATTRIBUTES 배열.
                if (Marshal.ReadInt32(buf) < 1) return null;
                return new SecurityIdentifier(Marshal.ReadIntPtr(buf, IntPtr.Size));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    /// <summary>Codex 가 샌드박스 계정을 묶어 두는 로컬 그룹. 없으면(Codex 샌드박스를 안 쓰는 PC) null.</summary>
    private static SecurityIdentifier? CodexSandboxGroup()
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(Environment.MachineName, "CodexSandboxUsers")
                .Translate(typeof(SecurityIdentifier));
        }
        catch { return null; }
    }

    private async Task ServeAsync(NamedPipeServerStream srv)
    {
        var line = await ReadLineAsync(srv).ConfigureAwait(false);
        if (line is null) return;

        object payload;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var cmd = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";

            // **표부터 본다.** 명령을 하나라도 돌린 뒤에 보면 늦다 — 탭 목록만 흘려도 무엇을 열어
            // 두었는지가 새어 나간다. 틀린 표는 기록에 남긴다: 앱 밖의 무엇인가가 창구를 두드렸다는 뜻이다.
            var ticket = doc.RootElement.TryGetProperty("ticket", out var tk) && tk.ValueKind == JsonValueKind.String
                ? tk.GetString() ?? "" : "";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(ticket), Encoding.UTF8.GetBytes(Ticket)))
            {
                _log($"브라우저 창구가 표가 맞지 않는 요청('{cmd}')을 거절했습니다 — 이 앱이 띄운 탭 밖에서 온 것입니다.", "w");
                throw new UnauthorizedAccessException(
                    $"표가 맞지 않습니다 — 이 명령은 LLM Tools 가 띄운 터미널 탭 안에서만 씁니다({TicketEnv}).");
            }

            var args = new List<string>();
            if (doc.RootElement.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var v in a.EnumerateArray()) args.Add(v.GetString() ?? "");

            var result = await _handle(cmd, args.ToArray())
                .WaitAsync(CommandTimeout).ConfigureAwait(false);
            payload = new { ok = true, result };
        }
        catch (TimeoutException)
        {
            payload = new
            {
                ok = false,
                error = $"명령이 {CommandTimeout.TotalSeconds:0} 초 안에 안 끝났습니다. " +
                        "창구는 계속 열려 있으니 다음 명령을 보내도 됩니다.",
            };
        }
        catch (Exception ex)
        {
            // **이유를 그대로 돌려준다.** 에이전트가 읽고 스스로 고칠 유일한 단서다.
            payload = new { ok = false, error = ex.Message };
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, _json) + "\n");
        try
        {
            await srv.WriteAsync(bytes).ConfigureAwait(false);
            await srv.FlushAsync().ConfigureAwait(false);
        }
        catch { /* 도우미가 먼저 끊었다 */ }
    }

    private static async Task<string?> ReadLineAsync(Stream s)
    {
        var buf = new byte[4096];
        var acc = new MemoryStream();
        while (acc.Length < MaxRequestBytes)
        {
            var n = await s.ReadAsync(buf).ConfigureAwait(false);
            if (n <= 0) break;
            for (var i = 0; i < n; i++)
            {
                if (buf[i] == (byte)'\n') return Encoding.UTF8.GetString(acc.ToArray());
                acc.WriteByte(buf[i]);
            }
        }
        return acc.Length > 0 ? Encoding.UTF8.GetString(acc.ToArray()) : null;
    }

    private static readonly JsonSerializerOptions _json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Dispose()
    {
        try { _stop.Cancel(); } catch { }
        try { _stop.Dispose(); } catch { }
    }
}
