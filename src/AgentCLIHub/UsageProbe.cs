using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCLIHub;

/// <summary>
/// 한도 창 하나. <paramref name="ResetsAtUnix"/> 는 **남은 시간이 아니라 리셋 시각**이다 —
/// 남은 시간을 여기서 계산해 보내면 화면은 그 값이 언제 잰 것인지 모르고, 다음 갱신(몇 분 뒤)
/// 까지 멈춘 숫자를 보여 준다. 시각을 주면 화면이 자기 시계로 초 단위까지 알아서 줄인다.
/// </summary>
/// <param name="Label">사람이 읽는 창 이름 — <c>5h</c> · <c>주</c> · <c>월</c>.</param>
/// <param name="Percent">쓴 비율(0~100).</param>
/// <param name="ResetsAtUnix">리셋 시각(Unix 초). 모르면 0.</param>
public sealed record UsageWindow(string Label, int Percent, long ResetsAtUnix);

/// <summary>
/// 계정 사용량을 읽어 온다. **CLI 마다 출처가 완전히 다르다.**
///
///   claude — 계정 API 를 직접 부른다. 로컬에 남는 것이 없어서 이 방법뿐이다.
///   codex  — 세션 기록에 딸려 온 값을 줍는다. codex 는 매 턴 응답에 한도를 실어 보내고
///            그것을 rollout 파일에 그대로 적어 둔다. 부를 API 가 따로 없다.
///
/// **하나도 못 읽어도 앱은 그대로 돌아간다.** 이 창은 폐쇄망에서도 떠야 하므로 (web/vendor 를
/// 로컬 번들로 두는 것과 같은 이유다) 네트워크가 없는 것은 고장이 아니라 평범한 상태다.
/// 그래서 모든 실패는 <c>null</c> 로 떨어지고, 화면에는 그냥 아무것도 안 나온다.
/// </summary>
public static class UsageProbe
{
    /// <summary>
    /// **한 벌만 쓴다.** 요청마다 새로 만들면 소켓이 TIME_WAIT 로 쌓여, 하루 종일 켜 두는
    /// 이 창에서는 결국 포트가 마른다.
    /// </summary>
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// claude 사용량. <c>~/.claude/.credentials.json</c> 의 OAuth 토큰으로 계정 API 를 부른다.
    ///
    /// **문서화된 엔드포인트가 아니다** — Claude Code 의 <c>/usage</c> 가 쓰는 내부 경로다.
    /// 언젠가 모양이 바뀔 수 있고, 그때는 조용히 <c>null</c> 로 떨어져 표시만 사라진다.
    /// 사용량 표시가 안 되는 것이 앱이 안 뜨는 것보다 훨씬 낫다.
    /// </summary>
    /// <param name="token">
    /// 더 넣은 계정의 setup-token. null 이면 설정 폴더의 <c>.credentials.json</c> 토큰을 본다.
    /// setup-token 은 추론 전용이라 이 조회가 거절될 수 있다 — 그때는 null 로 떨어져 "—" 가 된다.
    /// </param>
    /// <param name="home">
    /// claude 설정 폴더. null 이면 기본 폴더. 계정 폴더에 일반 로그인한 계정은 그 폴더의 토큰으로 읽는다 —
    /// **토큰을 갱신하지 않는다**(갱신은 그 계정의 claude 가 한다. 우리가 하면 갱신 토큰이 바뀌어 로그인이 죽는다).
    /// 한동안 안 쓴 계정은 토큰이 만료돼 "—" 로 보일 수 있고, 그 계정으로 한 번 띄우면 다시 읽힌다.
    /// </param>
    public static async Task<IReadOnlyList<UsageWindow>?> ClaudeAsync(string? token = null, string? home = null)
    {
        try
        {
            if (token is null)
            {
                var cred = Path.Combine(home ?? CliProfile.ClaudeHome, ".credentials.json");
                if (!File.Exists(cred)) return null;

                using var doc = JsonDocument.Parse(ReadShared(cred));
                if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                    !oauth.TryGetProperty("accessToken", out var tok)) return null;
                token = tok.GetString() ?? "";
            }
            if (token.Length == 0) return null;

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var d = JsonDocument.Parse(body);

            var list = new List<UsageWindow>();
            Take(d.RootElement, "five_hour", "5h", list);
            Take(d.RootElement, "seven_day", Week, list);
            return list.Count > 0 ? list : null;
        }
        catch { return null; }

        static void Take(JsonElement root, string key, string label, List<UsageWindow> into)
        {
            if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return;
            if (!w.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) return;

            long reset = 0;
            if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(r.GetString(), out var at)) reset = at.ToUnixTimeSeconds();

            into.Add(new UsageWindow(label, (int)Math.Round(u.GetDouble()), reset));
        }
    }

    /// <summary>
    /// codex 사용량. 최근 rollout 파일을 **뒤에서부터** 훑어 마지막 <c>rate_limits</c> 를 줍는다.
    ///
    /// 최근 파일 몇 개만 본다: 오래 쓴 PC 에는 세션이 수천 개고(<see cref="CliProfile.HasCodexSession"/>
    /// 도 같은 이유로 300 개에서 끊는다), 우리가 원하는 것은 **가장 최근 값 하나**뿐이다.
    /// 지금 도는 codex 가 파일을 쥐고 있을 수 있으므로 공유 모드로 연다.
    ///
    /// 무료 요금제는 <c>secondary</c> 가 늘 <c>null</c> 이라 창이 하나(30일)만 온다 — 빈 자리는
    /// 우리가 지어내지 않는다. 요금제가 바뀌면 codex 가 알아서 더 보내 주고 여기는 그대로 둔다.
    /// </summary>
    public static IReadOnlyList<UsageWindow>? Codex(string? home = null)
    {
        try
        {
            var dir = Path.Combine(home ?? CliProfile.CodexHome, "sessions");
            if (!Directory.Exists(dir)) return null;

            var files = new DirectoryInfo(dir)
                .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(5);

            foreach (var f in files)
            {
                string[] lines;
                try { lines = ReadShared(f.FullName).Split('\n'); }
                catch { continue; }

                for (var i = lines.Length - 1; i >= 0; i--)
                {
                    // 줄마다 JSON 을 파싱하면 큰 파일에서 눈에 띄게 느리다 — 먼저 글자로 거른다.
                    if (!lines[i].Contains("\"rate_limits\"", StringComparison.Ordinal)) continue;

                    var got = Parse(lines[i]);
                    if (got is { Count: > 0 }) return got;
                }
            }
        }
        catch { /* 못 읽으면 안 보여 준다 — 위 주석 참조 */ }
        return null;

        static List<UsageWindow>? Parse(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("payload", out var p) ||
                    !p.TryGetProperty("rate_limits", out var rl) ||
                    rl.ValueKind != JsonValueKind.Object) return null;

                var list = new List<UsageWindow>();
                foreach (var slot in new[] { "primary", "secondary" })
                {
                    if (!rl.TryGetProperty(slot, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                    if (!w.TryGetProperty("used_percent", out var u) || u.ValueKind != JsonValueKind.Number) continue;

                    var minutes = w.TryGetProperty("window_minutes", out var m) && m.ValueKind == JsonValueKind.Number
                        ? m.GetInt32() : 0;
                    var reset = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number
                        ? r.GetInt64() : 0;

                    list.Add(new UsageWindow(Label(minutes), (int)Math.Round(u.GetDouble()), reset));
                }
                // 짧은 창이 앞에 온다. 어느 요금제든 "곧 풀리는 것"부터 보는 편이 읽기 쉽다.
                list.Sort((a, b) => a.ResetsAtUnix.CompareTo(b.ResetsAtUnix));
                return list;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// codex 계정 사용량 — 기록에 남은 값이 먼저, **없으면 계정 API 에 묻는다.**
    ///
    /// 기록은 그 계정으로 한 턴이라도 주고받아야 생긴다. 막 로그인한 계정은 기록이 없어 늘 "—" 인데,
    /// 한도가 찬 계정에서 넘어갈 곳을 고르는 자리에서는 바로 그 계정의 잔량이 보여야 한다.
    /// 그래서 codex 클라이언트가 쓰는 조회 경로(<c>chatgpt.com/backend-api/wham/usage</c>)를 부른다 —
    /// **공개 API 가 아니다.** 모양이 바뀌면 조용히 null 로 떨어진다(claude 쪽 조회와 같은 처지다).
    /// 한도를 쓰지 않는 읽기 전용 조회다. 토큰은 발급한 곳(chatgpt.com)으로만 간다.
    /// </summary>
    public static async Task<IReadOnlyList<UsageWindow>?> CodexAccountAsync(string home)
    {
        var fromLog = Codex(home);
        if (fromLog is { Count: > 0 }) return fromLog;

        try
        {
            if (Accounts.CodexAuth(home) is not { } auth) return null;

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + auth.AccessToken);
            req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", auth.AccountId);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            using var d = JsonDocument.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = d.RootElement;
            if (!root.TryGetProperty("rate_limit", out var rl) && !root.TryGetProperty("rate_limits", out rl)) return null;
            if (rl.ValueKind != JsonValueKind.Object) return null;

            // 창 이름(primary_window …)에 기대지 않는다 — 창마다 길이(초)와 쓴 비율이 적혀 있다.
            var list = new List<UsageWindow>();
            foreach (var w in rl.EnumerateObject().Select(p => p.Value))
            {
                if (w.ValueKind != JsonValueKind.Object) continue;
                if (!w.TryGetProperty("used_percent", out var u) || u.ValueKind != JsonValueKind.Number) continue;
                var secs = w.TryGetProperty("limit_window_seconds", out var ws) && ws.ValueKind == JsonValueKind.Number
                    ? ws.GetInt64() : 0;
                var reset = w.TryGetProperty("reset_at", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt64() : 0;
                list.Add(new UsageWindow(Label((int)(secs / 60)), (int)Math.Round(u.GetDouble()), reset));
            }
            list.Sort((a, b) => a.ResetsAtUnix.CompareTo(b.ResetsAtUnix));
            return list.Count > 0 ? list : null;
        }
        catch { return null; }
    }

    private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<UsageWindow>?>> _antigravityInFlight =
        new(StringComparer.OrdinalIgnoreCase);

    private static string _verifiedAntigravityPath = "";

    /// <summary>
    /// <c>agy models</c> 조회가 성공한 실행 경로를 기록한다. 기본 계정의 미로그인 상태에서
    /// <c>agy --print /usage</c> 가 OAuth 브라우저를 띄우는 것을 막기 위해 첫 조회 전에
    /// 로그인 여부를 확인하는데, 이미 모델 목록 조회로 확인됐다면 중복 실행을 건너뛴다.
    /// </summary>
    public static void MarkAntigravitySignedIn(string? cliPath)
    {
        if (!string.IsNullOrWhiteSpace(cliPath))
            Volatile.Write(ref _verifiedAntigravityPath, cliPath);
    }

    /// <summary>
    /// Antigravity(Gemini) 사용량. 공식 비대화형 읽기 전용 명령
    /// <c>agy --print "/usage" --output-format json</c> 의 출력에서 Gemini 모델군의
    /// 5시간·주간 한도를 읽는다(턴·토큰·대화 생성 없음).
    /// </summary>
    public static Task<IReadOnlyList<UsageWindow>?> AntigravityAsync(
        CliLocator.Result cli, string? accountId = null, CancellationToken cancellationToken = default)
    {
        if (!cli.Found) return Task.FromResult<IReadOnlyList<UsageWindow>?>(null);
        var id = accountId ?? "";
        if (cancellationToken.CanBeCanceled)
            return QueryAntigravityCoreAsync(cli, id, cancellationToken);

        var key = cli.Path + "\0" + id;
        return _antigravityInFlight.GetOrAdd(key, _ => RunSharedAsync(key, cli, id));

        async Task<IReadOnlyList<UsageWindow>?> RunSharedAsync(string flightKey, CliLocator.Result target, string acct)
        {
            try { return await QueryAntigravityCoreAsync(target, acct, CancellationToken.None).ConfigureAwait(false); }
            finally { _antigravityInFlight.TryRemove(flightKey, out _); }
        }
    }

    private static async Task<IReadOnlyList<UsageWindow>?> QueryAntigravityCoreAsync(
        CliLocator.Result cli, string accountId, CancellationToken cancellationToken)
    {
        var profile = "";
        if (accountId.Length > 0)
        {
            profile = AntigravityAccounts.ProfileOf(accountId);
            if (!AntigravityAccounts.State(profile).LoggedIn) return null;
        }
        else if (!string.Equals(Volatile.Read(ref _verifiedAntigravityPath), cli.Path, StringComparison.OrdinalIgnoreCase))
        {
            // 기본 계정이 로그인 전일 때 --print /usage 를 바로 부르면 agy 가 브라우저 인증을 시작한다.
            // agy models 는 미로그인 시 브라우저를 열지 않고 즉시 종료 코드 1 로 끝나므로 먼저 확인한다.
            var check = await AntigravityModels.ReadAsync(cli, cancellationToken).ConfigureAwait(false);
            if (check.Error is not null || check.Models.Count == 0) return null;
            Volatile.Write(ref _verifiedAntigravityPath, cli.Path);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        Process? process = null;
        try
        {
            var args = CliLocator.SplitCommandLine(
                cli.LaunchCommand(cli.CommandHead() + " --print \"/usage\" --output-format json --print-timeout 15s"));
            var workDir = accountId.Length > 0 ? profile : Path.GetTempPath();
            try { Directory.CreateDirectory(workDir); } catch { }
            var info = new ProcessStartInfo(args[0])
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in args.Skip(1)) info.ArgumentList.Add(arg);
            info.Environment["PATH"] = CliLocator.LaunchPath(CliProfile.Antigravity);
            info.Environment["NO_COLOR"] = "1";
            info.Environment.Remove("FORCE_COLOR");
            info.Environment.Remove("BROWSER");
            foreach (var k in info.Environment.Keys
                .Where(k => k.StartsWith("AGENTCLIHUB_", StringComparison.OrdinalIgnoreCase) ||
                            k.StartsWith("LLMTOOLS_", StringComparison.OrdinalIgnoreCase))
                .ToArray())
            {
                info.Environment.Remove(k);
            }
            if (accountId.Length > 0)
            {
                foreach (var (k, v) in AntigravityAccounts.EnvironmentFor(profile))
                    if (v is null) info.Environment.Remove(k); else info.Environment[k] = v;
            }

            process = Process.Start(info);
            if (process is null) return null;
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errWatch = WatchAuthFailureAsync(process.StandardError, timeout);
            await Task.WhenAll(output, errWatch, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                if (accountId.Length == 0) Volatile.Write(ref _verifiedAntigravityPath, "");
                return null;
            }
            var parsed = ParseAntigravity(await output.ConfigureAwait(false));
            if (parsed is null && accountId.Length == 0) Volatile.Write(ref _verifiedAntigravityPath, "");
            return parsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            if (accountId.Length == 0) Volatile.Write(ref _verifiedAntigravityPath, "");
            return null;
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
        }

        static async Task WatchAuthFailureAsync(StreamReader err, CancellationTokenSource cancel)
        {
            try
            {
                while (await err.ReadLineAsync(cancel.Token).ConfigureAwait(false) is { } line)
                {
                    if (line.Contains("Authentication required", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Please sign in", StringComparison.OrdinalIgnoreCase))
                    {
                        cancel.Cancel();
                        return;
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// <c>agy --print "/usage" --output-format json</c> 출력에서 Gemini 모델군의 한도 창을 파싱한다.
    /// <c>remaining_fraction</c>(남은 비율 0~1)을 다른 CLI 와 같은 <b>사용한 비율(0~100%)</b>로 바꾸고,
    /// 짧은 창(<c>5h</c>)이 앞, 긴 창(<c>주</c>)이 뒤에 오도록 정렬한다.
    /// </summary>
    public static IReadOnlyList<UsageWindow>? ParseAntigravity(string output)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(output)) return null;
            var clean = Regex.Replace(output, @"\x1b\[[0-?]*[ -/]*[@-~]", "").Trim();
            var start = clean.IndexOf('{');
            var end = clean.LastIndexOf('}');
            if (start < 0 || end <= start) return null;

            using var doc = JsonDocument.Parse(clean[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            JsonElement groups;
            if (root.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.Object)
            {
                if (cmd.TryGetProperty("name", out var cname) && cname.ValueKind == JsonValueKind.String &&
                    !string.Equals(cname.GetString(), "usage", StringComparison.OrdinalIgnoreCase))
                    return null;
                if (!cmd.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                    !data.TryGetProperty("groups", out groups) || groups.ValueKind != JsonValueKind.Array)
                    return null;
            }
            else if (!root.TryGetProperty("groups", out groups) || groups.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            JsonElement? selected = null;
            JsonElement? fallback = null;
            foreach (var g in groups.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) continue;
                if (!g.TryGetProperty("buckets", out var b) || b.ValueKind != JsonValueKind.Array || b.GetArrayLength() == 0) continue;
                fallback ??= g;
                var gname = g.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                var isGemini = gname.Contains("Gemini", StringComparison.OrdinalIgnoreCase) ||
                    b.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Object &&
                        x.TryGetProperty("id", out var bid) && bid.ValueKind == JsonValueKind.String &&
                        (bid.GetString() ?? "").StartsWith("gemini-", StringComparison.OrdinalIgnoreCase));
                if (isGemini) { selected = g; break; }
            }

            var target = selected ?? fallback;
            if (target is null || !target.Value.TryGetProperty("buckets", out var buckets)) return null;

            var list = new List<UsageWindow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in buckets.EnumerateArray())
            {
                if (b.ValueKind != JsonValueKind.Object) continue;
                if (b.TryGetProperty("disabled", out var dis) && dis.ValueKind == JsonValueKind.True) continue;
                if (!b.TryGetProperty("remaining_fraction", out var remEl) || remEl.ValueKind != JsonValueKind.Number) continue;

                var rem = remEl.GetDouble();
                if (double.IsNaN(rem) || double.IsInfinity(rem)) continue;
                var pct = Math.Clamp((int)Math.Round((1.0 - rem) * 100.0), 0, 100);

                long reset = 0;
                if (b.TryGetProperty("reset_time", out var rt) && rt.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(rt.GetString(), out var at))
                    reset = at.ToUnixTimeSeconds();

                var win = b.TryGetProperty("window", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? "" : "";
                var bid = b.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
                var bname = b.TryGetProperty("name", out var nmEl) && nmEl.ValueKind == JsonValueKind.String ? nmEl.GetString() ?? "" : "";
                var label = AntigravityWindowLabel(win, bid, bname);
                if (!seen.Add(label)) continue;

                list.Add(new UsageWindow(label, pct, reset));
            }

            list.Sort((a, b) =>
            {
                var cmp = WindowOrder(a.Label).CompareTo(WindowOrder(b.Label));
                return cmp != 0 ? cmp : a.ResetsAtUnix.CompareTo(b.ResetsAtUnix);
            });
            return list.Count > 0 ? list : null;
        }
        catch { return null; }

        static string AntigravityWindowLabel(string window, string id, string name)
        {
            switch (window.Trim().ToLowerInvariant())
            {
                case "5h": return "5h";
                case "weekly" or "7d" or "1w": return Week;
                case "monthly" or "30d" or "1m": return Month;
                case "daily" or "24h" or "1d": return "1일";
            }
            if (window.EndsWith('h') && int.TryParse(window[..^1], out var hours) && hours > 0) return Label(hours * 60);
            if (window.EndsWith('d') && int.TryParse(window[..^1], out var days) && days > 0) return Label(days * 1440);
            if (id.Contains("5h", StringComparison.OrdinalIgnoreCase) || name.Contains("Five Hour", StringComparison.OrdinalIgnoreCase)) return "5h";
            if (id.Contains("week", StringComparison.OrdinalIgnoreCase) || name.Contains("Week", StringComparison.OrdinalIgnoreCase)) return Week;
            if (id.Contains("month", StringComparison.OrdinalIgnoreCase) || name.Contains("Month", StringComparison.OrdinalIgnoreCase)) return Month;
            return window.Length > 0 ? window : "?";
        }

        static int WindowOrder(string label) => label switch
        {
            "5h" => 1,
            "1일" => 2,
            Week => 3,
            Month => 4,
            _ => 5,
        };
    }

    private const string Week = "주";
    private const string Month = "월";

    /// <summary>창 길이(분) → 사람이 읽는 이름.</summary>
    private static string Label(int minutes) => minutes switch
    {
        <= 0 => "?",
        300 => "5h",
        10080 => Week,
        43200 => Month,
        _ when minutes % 1440 == 0 => (minutes / 1440) + "일",
        _ when minutes % 60 == 0 => (minutes / 60) + "h",
        _ => minutes + "분",
    };

    /// <summary>
    /// 쓰는 중일 수 있는 파일을 읽는다. codex 도 claude 도 자기 파일을 열어 둔 채 돌기 때문에
    /// <see cref="File.ReadAllText(string)"/> 는 공유 위반으로 터진다.
    /// </summary>
    private static string ReadShared(string path)
    {
        using var st = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var rd = new StreamReader(st);
        return rd.ReadToEnd();
    }
}
