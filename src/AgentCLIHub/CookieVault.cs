using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Web.WebView2.Core;

namespace AgentCLIHub;

/// <summary>
/// 브라우저 탭의 로그인이 앱을 껐다 켜도 남게 하는 곳.
///
/// **왜 필요한가.** WebView2 는 만료 시각이 있는 쿠키(지속 쿠키)만 프로필에 남기고,
/// **세션 쿠키는 브라우저 프로세스가 죽을 때 버린다.** 그래서 프로필을 아무리 잘 붙들고
/// 있어도 세션 쿠키를 쓰는 사이트는 앱을 껐다 켜면 로그아웃된다 —
/// **"Jira 는 유지되는데 어떤 사이트만 자꾸 풀린다" 가 정확히 이 차이다.**
/// 여기서 종료할 때 세션 쿠키만 꺼내 두었다가 켤 때 되돌린다.
///
/// **세션 쿠키는 곧 로그인 토큰이다.** 평문으로 두면 파일 하나 복사로 계정이 넘어간다.
/// 그래서 DPAPI(<see cref="DataProtectionScope.CurrentUser"/>)로 감싼다 — Chromium 이 자기
/// 쿠키 DB 를 지키는 것과 같은 등급이고, **같은 Windows 계정에서만** 풀린다. 파일을 다른
/// PC 로 옮기면 못 읽는다(그때는 조용히 버리고 로그인만 다시 하면 된다).
///
/// 실패는 전부 조용히 넘어간다. 쿠키 하나 못 살린 것으로 앱이 안 뜨면 안 된다 —
/// 최악이라야 "로그인을 다시 해야 한다" 이고, 그것은 이 기능이 없을 때의 상태다.
/// </summary>
public static class CookieVault
{
    private static string FilePath => Path.Combine(Settings.DataDir, "session.dat");

    /// <summary>
    /// DPAPI 에 같이 넣는 고정 소금. 이 앱이 만든 것이 아닌 DPAPI 덩어리를 이 자리에
    /// 갖다 놔도 안 풀리게 한다 — 비밀이 아니라 **꼬리표**다(소스에 있어도 상관없다).
    /// </summary>
    private static readonly byte[] Salt = "LlmTools/session-cookies/v1"u8.ToArray();

    /// <summary>
    /// 파일에 담기는 모양. **이름을 짧게 쓴다** — 쿠키는 수백 개가 되고, 그대로 늘어놓으면
    /// 키 이름이 값보다 커진다. 순서는 <see cref="CoreWebView2Cookie"/> 의 속성과 같다.
    /// </summary>
    private sealed record Jar(
        string N, string V, string D, string P, bool Http, bool Sec, int Same);

    // ── 저장 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 지금 있는 **세션 쿠키만** 꺼내 암호화해 둔다. 창을 닫을 때 부른다.
    ///
    /// 지속 쿠키는 건드리지 않는다 — 그쪽은 WebView2 가 이미 프로필에 제대로 남기고,
    /// 우리가 또 저장하면 두 벌이 되어 어느 쪽이 최신인지 갈린다.
    /// </summary>
    public static async Task SaveAsync(CoreWebView2CookieManager cm, Action<string, string> log)
    {
        try
        {
            var all = await cm.GetCookiesAsync(null).ConfigureAwait(true);
            var jars = all.Where(c => c.IsSession)
                          .Select(c => new Jar(c.Name, c.Value, c.Domain, c.Path,
                                               c.IsHttpOnly, c.IsSecure, (int)c.SameSite))
                          .ToList();

            if (jars.Count == 0) { Clear(); return; }

            var blob = ProtectedData.Protect(
                JsonSerializer.SerializeToUtf8Bytes(jars), Salt, DataProtectionScope.CurrentUser);

            Directory.CreateDirectory(Settings.DataDir);

            // **다 쓴 뒤에 갈아 끼운다.** 쓰다가 죽으면 반쯤 쓴 파일이 남고, 그것은
            // 다음에 켤 때 "로그인이 통째로 날아갔다" 로만 보인다.
            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            log($"로그인 상태를 저장하지 못했습니다 — {ex.Message}", "w");
        }
    }

    // ── 복원 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 저장해 둔 세션 쿠키를 되돌린다. **탭을 살리기 전에 불러야 한다** — 페이지가
    /// 이미 요청을 보낸 뒤에 넣으면 그 요청은 쿠키 없이 나가 로그인 화면을 받는다.
    /// </summary>
    public static void Restore(CoreWebView2CookieManager cm, Action<string, string> log)
    {
        if (!File.Exists(FilePath)) return;

        List<Jar>? jars;
        try
        {
            var raw = ProtectedData.Unprotect(
                File.ReadAllBytes(FilePath), Salt, DataProtectionScope.CurrentUser);
            jars = JsonSerializer.Deserialize<List<Jar>>(raw);
        }
        catch
        {
            // 다른 계정·다른 PC 이거나 파일이 깨졌다. 되살릴 것이 없으니 치운다 —
            // 남겨 두면 켤 때마다 같은 실패를 반복한다.
            Clear();
            return;
        }

        if (jars is not { Count: > 0 }) return;

        foreach (var j in jars)
        {
            // 쿠키 하나가 이상해도 나머지는 살린다. 도메인 하나 때문에 전부 날리면
            // "어제는 됐는데 오늘은 전부 로그아웃" 이 된다.
            try
            {
                var c = cm.CreateCookie(j.N, j.V, j.D, j.P);
                c.IsHttpOnly = j.Http;
                c.IsSecure = j.Sec;
                if (Enum.IsDefined(typeof(CoreWebView2CookieSameSiteKind), j.Same))
                    c.SameSite = (CoreWebView2CookieSameSiteKind)j.Same;
                // Expires 를 안 건드리면 세션 쿠키 그대로다 — 원래 모습으로 되돌리는 것이 목적이다.
                cm.AddOrUpdateCookie(c);
            }
            catch { /* 이 쿠키만 버린다 */ }
        }

        // **넣었다고 들어간 것이 아니다.** 규칙에 어긋나는 쿠키는 예외 없이 조용히 버려질 수
        // 있다. 그러면 "다 되돌렸다" 로 보이고 화면에는 "이 사이트만 로그인이 안 남는다" 로만
        // 나타난다 — 그것을 찾느라 시간을 쓰게 되므로 다시 읽어 확인하고 이름을 남긴다.
        //
        // **값은 절대 안 적는다.** 그 값이 곧 로그인 토큰이라 기록에 남기면 기록이 비밀이 된다.
        _ = VerifyAsync(cm, jars, log);
    }

    /// <summary>
    /// 되돌린 것이 실제로 저장소에 들어갔는지 확인하고 기록에 남긴다.
    /// **막지 않는다** — 확인이 늦어도 페이지 이동은 이미 제 쿠키를 들고 나갔다.
    /// </summary>
    private static async Task VerifyAsync(
        CoreWebView2CookieManager cm, List<Jar> jars, Action<string, string> log)
    {
        try
        {
            var now = await cm.GetCookiesAsync(null).ConfigureAwait(true);
            var have = new HashSet<string>(
                now.Select(c => Key(c.Name, c.Domain, c.Path)), StringComparer.Ordinal);

            var lost = jars.Where(j => !have.Contains(Key(j.N, j.D, j.P))).ToList();
            if (lost.Count == 0)
            {
                log($"로그인 상태를 되돌렸습니다 (쿠키 {jars.Count}개).", "i");
                return;
            }

            // 사이트별로 묶는다. 쿠키 이름을 낱개로 늘어놓으면 어느 사이트가 문제인지 안 보인다.
            var bySite = lost.GroupBy(j => j.D.TrimStart('.'))
                             .Select(g => $"{g.Key}({string.Join(", ", g.Select(x => x.N))})");

            log($"로그인 상태 {jars.Count - lost.Count}/{jars.Count}개를 되돌렸습니다. " +
                $"못 되돌린 곳: {string.Join(" · ", bySite)}", "w");
        }
        catch (Exception ex)
        {
            log($"되돌린 로그인을 확인하지 못했습니다 — {ex.Message}", "w");
        }
    }

    /// <summary>쿠키 한 개를 가리키는 열쇠. 브라우저가 같은 쿠키로 보는 기준(이름·도메인·경로)과 같다.</summary>
    private static string Key(string name, string domain, string path) =>
        name + " " + domain + " " + path;

    // ── 지우기 ──────────────────────────────────────────────────────────────

    /// <summary>저장해 둔 것을 없앤다. 설정에서 끄거나 [저장된 로그인 지우기] 를 눌렀을 때.</summary>
    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}
