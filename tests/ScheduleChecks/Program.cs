using System.IO;
using System.Text.Json;
using AgentCLIHub;

// 예약 작업 검사. 실제 설정 파일·실제 CLI·실제 시계를 쓰지 않는다 — 앱은 가짜 IScheduleHost, 시각은 손으로 돌리는 시계다.
var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL " + message);
    Console.WriteLine("PASS " + message);
    passed++;
}

var root = Path.Combine(Path.GetTempPath(), "agentclihub-schedule-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    // ── cron ──
    DateTime At(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Local);
    CronExpr? Cron(string s) => CronExpr.TryParse(s, out var c) ? c : null;

    Check(Cron("*/5 * * * *")!.Next(At(2026, 11, 12, 10, 2)) == At(2026, 11, 12, 10, 5), "cron */5 picks the next 5-minute mark");
    Check(Cron("* * * * *")!.Next(At(2026, 11, 12, 10, 2)) == At(2026, 11, 12, 10, 3), "cron every minute is strictly after now");
    Check(Cron("0 9 * * 1-5")!.Next(At(2026, 11, 13, 9, 0)) == At(2026, 11, 16, 9, 0), "cron weekdays 9:00 skips the weekend (Fri 9:00 -> Mon)");
    Check(Cron("0 9 * * MON-FRI")!.Next(At(2026, 11, 14, 12, 0)) == At(2026, 11, 16, 9, 0), "cron accepts weekday names");
    Check(Cron("30 18 * * FRI")!.Next(At(2026, 11, 12, 0, 0)) == At(2026, 11, 13, 18, 30), "cron single weekday name");
    Check(Cron("0 0 * * 7")!.Next(At(2026, 11, 12, 0, 0)) == At(2026, 11, 15, 0, 0), "cron weekday 7 is Sunday");
    Check(Cron("0 0 1 * 1")!.Next(At(2026, 11, 25, 0, 0)) == At(2026, 11, 30, 0, 0), "cron day-of-month and weekday both set match either (Vixie)");
    Check(Cron("15 10 31 * *")!.Next(At(2026, 11, 1, 0, 0)) == At(2026, 12, 31, 10, 15), "cron skips months without the 31st");
    Check(Cron("0 12 1 JAN *")!.Next(At(2026, 11, 12, 0, 0)) == At(2027, 1, 1, 12, 0), "cron month names roll into next year");
    Check(Cron("0,30 8-10/2 * * *")!.Next(At(2026, 11, 12, 8, 30)) == At(2026, 11, 12, 10, 0), "cron lists and stepped ranges");
    Check(Cron("0 0 30 2 *")!.Next(At(2026, 1, 1, 0, 0)) is null, "cron that never matches yields no time");
    Check(new[] { "", "* * * *", "60 * * * *", "* 24 * * *", "* * 0 * *", "* * * 13 *", "* * * * 8", "*/0 * * * *", "5-1 * * * *", "a b c d e" }
              .All(s => Cron(s) is null), "invalid cron expressions are rejected");

    // ── 다음 실행 시각 ──
    var now = At(2026, 11, 12, 10, 7);
    var anchor = At(2026, 11, 12, 10, 0).ToUniversalTime();
    ScheduledJob Job(Action<ScheduledJob> set)
    {
        var j = new ScheduledJob { Id = "j", Name = "test", Prompt = "hello", Target = "tab", TabName = "project", AnchorUtc = anchor };
        set(j);
        return j;
    }
    Check(ScheduleCalc.Next(Job(j => { j.Repeat = "interval"; j.Every = 1; }), now) == At(2026, 11, 12, 10, 8), "1-minute interval fires the next minute");
    Check(ScheduleCalc.Next(Job(j => { j.Repeat = "interval"; j.Every = 5; }), now) == At(2026, 11, 12, 10, 10), "interval counts from its anchor");
    Check(ScheduleCalc.Next(Job(j => { j.Repeat = "interval"; j.Every = 2; j.Unit = "h"; }), now) == At(2026, 11, 12, 12, 0), "hour interval");
    Check(ScheduleCalc.Next(Job(j => { j.Repeat = "interval"; j.Every = 5; j.AnchorUtc = At(2026, 11, 12, 11, 0).ToUniversalTime(); }), now) == At(2026, 11, 12, 11, 0),
        "future anchor is the first run");
    Check(ScheduleCalc.Next(Job(j => { j.Repeat = "daily"; j.Time = "09:30"; }), now) == At(2026, 11, 13, 9, 30), "daily time already passed today runs tomorrow");
    Check(ScheduleCalc.Next(Job(j => { j.Repeat = "daily"; j.Time = "10:08"; }), now) == At(2026, 11, 12, 10, 8), "daily time later today runs today");
    Check(ScheduleCalc.Next(Job(j => { j.Repeat = "weekly"; j.Time = "09:00"; j.Days = [1, 3]; }), now) == At(2026, 11, 16, 9, 0), "weekly picks the next selected day (Thu -> Mon)");
    Check(ScheduleCalc.Next(Job(j => { j.Repeat = "once"; j.At = "2026-11-12T10:30"; }), now) == At(2026, 11, 12, 10, 30), "once in the future");
    Check(ScheduleCalc.Next(Job(j => { j.Repeat = "once"; j.At = "2026-11-12T10:00"; }), now) is null, "once in the past has no next run");
    Check(ScheduleCalc.Next(Job(j => { j.Repeat = "cron"; j.Cron = "*/10 * * * *"; }), now) == At(2026, 11, 12, 10, 10), "cron schedule");
    var upcoming = ScheduleCalc.Upcoming(Job(j => { j.Repeat = "interval"; j.Every = 1; }), now, 3);
    Check(upcoming.SequenceEqual([At(2026, 11, 12, 10, 8), At(2026, 11, 12, 10, 9), At(2026, 11, 12, 10, 10)]), "preview lists consecutive 1-minute runs");

    // ── 검사 ──
    Check(ScheduleCalc.Validate(Job(j => { j.Repeat = "interval"; j.Every = 1; }), now) is null, "1-minute interval is allowed");
    Check(ScheduleCalc.Validate(Job(j => { j.Repeat = "interval"; j.Every = 0; }), now) == "간격은 1분 이상이어야 합니다.", "sub-minute interval is rejected");
    Check(ScheduleCalc.Validate(Job(j => { j.Repeat = "interval"; j.Every = 200; j.Unit = "h"; }), now) == "간격이 너무 깁니다(최대 7일).", "interval over 7 days is rejected");
    Check(ScheduleCalc.Validate(Job(j => j.Prompt = " "), now) == "프롬프트를 입력하세요.", "empty prompt is rejected");
    Check(ScheduleCalc.Validate(Job(j => j.TabName = ""), now) == "보낼 탭 이름을 입력하세요.", "tab mode needs a tab name");
    Check(ScheduleCalc.Validate(Job(j => { j.Target = "new"; j.Folder = Path.Combine(root, "missing"); }), now) == "폴더가 없습니다.", "new-tab mode needs an existing folder");
    Check(ScheduleCalc.Validate(Job(j => { j.Target = "new"; j.Folder = root; j.Backend = "nope"; }), now) == "알 수 없는 CLI 입니다.", "new-tab mode needs a known CLI");
    Check(ScheduleCalc.Validate(Job(j => { j.Repeat = "daily"; j.Time = "25:00"; }), now) == "시각 형식이 올바르지 않습니다(HH:MM).", "invalid daily time is rejected");
    Check(ScheduleCalc.Validate(Job(j => { j.Repeat = "weekly"; j.Days = []; }), now) == "요일을 하나 이상 고르세요.", "weekly needs a day");
    Check(ScheduleCalc.Validate(Job(j => { j.Repeat = "once"; j.At = "2026-11-12T09:00"; }), now) == "이미 지난 시각입니다.", "once in the past is rejected");
    Check(ScheduleCalc.Validate(Job(j => { j.Repeat = "cron"; j.Cron = "* * *"; }), now)!.StartsWith("cron 식이 올바르지 않습니다"), "bad cron is rejected");

    // ── 설정 파일 ──
    var settingsJson = JsonSerializer.Serialize(new Settings { Schedules = [Job(j => { j.Repeat = "weekly"; j.Days = [2, 4]; j.Telegram = true; j.Model = "haiku"; j.Effort = "low"; })] });
    var back = JsonSerializer.Deserialize<Settings>(settingsJson)!;
    Check(back.Schedules.Single().Days.SequenceEqual([2, 4]) && back.Schedules[0].Telegram && back.Schedules[0].TabName == "project"
          && back.Schedules[0].Model == "haiku" && back.Schedules[0].Effort == "low",
        "scheduled tasks round-trip through settings.json");
    var broken = JsonSerializer.Deserialize<ScheduledJob>("{\"Id\":\"x\",\"Name\":null,\"TabName\":null,\"Model\":null,\"Effort\":null,\"Days\":null}")!;
    broken.Normalize();
    Check(broken.Name == "" && broken.TabName == "" && broken.Model == "" && broken.Effort == "" && broken.Days.Count == 0,
        "hand-edited nulls are normalized instead of crashing");
    var historyFile = Path.Combine(root, "history.json");
    ScheduleHistory.Save(Enumerable.Range(0, ScheduleHistory.Max + 20).Select(i => new ScheduleRun { JobId = "j", JobName = "n" + i, Status = "ok" }).ToList(), historyFile);
    var loaded = ScheduleHistory.Load(historyFile);
    Check(loaded.Count == ScheduleHistory.Max && loaded[^1].JobName == "n" + (ScheduleHistory.Max + 19), "run history keeps only the newest entries");

    await RunnerChecks();
    Console.WriteLine($"All {passed} schedule checks passed.");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
    Console.WriteLine(ex.StackTrace);
    return 1;
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

async Task RunnerChecks()
{
    var transcript = Path.Combine(root, "session.jsonl");
    File.WriteAllText(transcript, ClaudeLine("user", "u1", null, "\"이전 질문\"") + ClaudeLine("assistant", "a1", "u1", "[{\"type\":\"text\",\"text\":\"이전 답\"}]"));
    var host = new FakeHost(root, transcript);
    var clock = new DateTime(2026, 11, 12, 10, 0, 30, DateTimeKind.Local);
    using var runner = new ScheduleRunner(host, () => clock)
    {
        Tick = TimeSpan.FromMilliseconds(50), Poll = TimeSpan.FromMilliseconds(100), Quiet = TimeSpan.FromMilliseconds(300),
        NoTranscript = TimeSpan.FromSeconds(30), Stuck = TimeSpan.FromSeconds(30),
    };
    var job = new ScheduledJob
    {
        Id = "every-minute", Name = "점검", Prompt = "빌드 상태를 알려 줘", Target = "tab", TabName = "Project",
        Repeat = "interval", Every = 1, Unit = "m", Telegram = true,
        AnchorUtc = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Local).ToUniversalTime(),
    };
    var answerSeq = 0;
    var lastUuid = "a1";
    host.OnSend = text =>
    {
        // claude 기록은 parentUuid 사슬을 거꾸로 따라 읽는다 — 앞 답에 이어 붙여야 한 대화로 읽힌다.
        var n = Interlocked.Increment(ref answerSeq);
        File.AppendAllText(transcript,
            ClaudeLine("user", "q" + n, lastUuid, JsonSerializer.Serialize(text)) +
            ClaudeLine("assistant", "r" + n, "q" + n, "[{\"type\":\"text\",\"text\":\"빌드 정상 #" + n + "\"}]"));
        lastUuid = "r" + n;
    };
    runner.Update([job]);
    runner.Start();

    var status = runner.Snapshot().Single();
    Check(status.NextLocal == new DateTime(2026, 11, 12, 10, 1, 0), "runner schedules the next minute");
    await Task.Delay(300);
    Check(host.Sent.Count == 0, "nothing runs before the scheduled minute");

    // 1) 1분이 되면 열린 탭(이름은 대소문자 무시)에 보내고, 기록에서 최종 답을 받는다.
    clock = clock.AddMinutes(1);
    await Until(() => host.Finished.Any(r => r.Status == "ok"), "1-minute task runs and finishes", 15);
    var first = host.Finished.First(r => r.Status == "ok");
    Check(host.Sent.Single() == "빌드 상태를 알려 줘", "prompt reached the tab matched by name");
    Check(first.Answer == "빌드 정상 #1" && first.Tab == "project" && first.Trigger == "auto", "run records the final answer and tab");
    Check(host.Notes.Any(n => n.Contains("빌드 정상 #1") && n.Contains("점검")), "Telegram notification carries the answer");
    Check(runner.Snapshot().Single().NextLocal == new DateTime(2026, 11, 12, 10, 2, 0), "next run moves to the following minute");

    // 2) 겹침: 실행이 안 끝난 채 세 번의 회차가 지나가도 끝난 뒤 한 번만 더 돈다.
    host.Hold = true;
    clock = clock.AddMinutes(1);
    await Until(() => host.Sent.Count == 2, "second run starts", 15);
    for (var i = 0; i < 3; i++) { clock = clock.AddMinutes(1); await Task.Delay(200); }
    Check(runner.Snapshot().Single() is { Running: true, Pending: true }, "missed minutes while running collapse into one pending run");
    Check(host.Sent.Count == 2, "no overlapping send while the previous run is busy");
    host.Hold = false;
    host.Release();
    await Until(() => host.Sent.Count == 3, "pending run starts right after the previous one", 15);
    await Until(() => host.Finished.Count(r => r.Status == "ok") == 3, "pending run finishes", 15);
    await Task.Delay(400);
    Check(host.Sent.Count == 3, "only one catch-up run happens");

    // 3) [지금 실행] 은 일정과 무관하게 돈다. 모르는 작업은 false.
    Check(!runner.RunNow("missing"), "unknown task cannot be run");
    Check(runner.RunNow(job.Id), "manual run is accepted");
    await Until(() => host.Finished.Count(r => r.Status == "ok") == 4 && host.Finished.Last().Trigger == "manual", "manual run finishes", 15);

    // 4) 끈 작업은 돌지 않는다.
    runner.Update([Copy(job, j => j.Enabled = false)]);
    var sentBefore = host.Sent.Count;
    clock = clock.AddMinutes(5);
    await Task.Delay(400);
    Check(host.Sent.Count == sentBefore && runner.Snapshot().Single().NextLocal is null, "disabled task never fires");

    // 5) 찾을 수 없는 탭 → 실패로 남는다.
    runner.Update([Copy(job, j => { j.TabName = "없는 탭"; j.Telegram = false; })]);
    Check(runner.RunNow(job.Id), "manual run of a broken task is accepted");
    await Until(() => host.Finished.Any(r => r.Status == "failed" && r.Error.Contains("없는 탭")), "missing tab is reported as a failure", 15);

    // 6) 새 탭 모드: 폴더·CLI·계정·모델·추론 강도로 연 뒤 보내고, 성공하면 닫는다.
    var newJob = Copy(job, j => { j.Id = "new-tab"; j.Target = "new"; j.Folder = root; j.Backend = CliProfile.Claude.Backend;
                                  j.Model = "haiku"; j.Effort = "low"; j.CloseTab = true; j.Telegram = false; });
    runner.Update([newJob]);
    Check(runner.RunNow(newJob.Id), "new-tab task starts");
    await Until(() => host.Closed.Contains("t2"), "new tab is closed after a successful run", 15);
    Check(host.Opened.Single() == (root, CliProfile.Claude.Backend, "haiku", "low") && host.Finished.Last().Status == "ok",
        "new tab opened with the task's folder, model and effort, and finished");

    // 6b) 작업별 모델·추론 강도 커맨드라인 덮어쓰기(비우면 전역 설정 유지).
    var baseCfg = new Settings
    {
        ClaudeModel = "opus", ClaudeEffort = "high", ClaudeCodePermissionMode = "auto",
        CodexModel = "gpt-5", CodexEffort = "medium",
        AntigravityModel = "gemini-2.5-pro", AntigravityEffort = "high",
    };
    var claudeOver = CliProfile.Claude.BuildCommandLine("\"claude\"", CliProfile.Claude.WithOverrides(baseCfg, "haiku", "low"), ResumeMode.New, root);
    Check(claudeOver.Contains("--model haiku") && claudeOver.Contains("--effort low") && claudeOver.Contains("--permission-mode auto"),
        "Claude per-task model and effort override global settings while keeping permission");
    var claudeFallback = CliProfile.Claude.BuildCommandLine("\"claude\"", CliProfile.Claude.WithOverrides(baseCfg, "", "low"), ResumeMode.New, root);
    Check(claudeFallback.Contains("--model opus") && claudeFallback.Contains("--effort low"),
        "empty per-task model keeps global settings model while overriding effort");
    var agOver = CliProfile.Antigravity.BuildCommandLine("\"agy\"", CliProfile.Antigravity.WithOverrides(baseCfg, "gemini-2.5-flash", "max"), ResumeMode.New, root);
    Check(agOver.Contains("--model \"gemini-2.5-flash\"") && agOver.Contains("--effort max"),
        "Antigravity per-task model and effort override global settings");
    Check(ReferenceEquals(CliProfile.Claude.WithOverrides(baseCfg, "", ""), baseCfg) && baseCfg.ClaudeModel == "opus",
        "empty overrides leave the original Settings object untouched");

    // 7) 한 번 작업: 그 시각에 한 번 돌고 꺼진다.
    var onceJob = Copy(job, j => { j.Id = "once"; j.Repeat = "once"; j.At = clock.AddMinutes(2).ToString("yyyy-MM-ddTHH:mm"); j.Telegram = false; });
    runner.Update([onceJob]);
    var sentOnce = host.Sent.Count;
    clock = clock.AddMinutes(2);
    await Until(() => host.OnceFired.Contains("once"), "once task reports that it fired", 15);
    await Until(() => host.Sent.Count == sentOnce + 1, "once task sends exactly once", 15);
    clock = clock.AddMinutes(10);
    await Task.Delay(400);
    Check(host.Sent.Count == sentOnce + 1 && runner.Snapshot().Single().NextLocal is null, "once task never repeats");

    // 8) 승인 창이 뜨면 사람을 부른다(텔레그램 켜진 작업).
    var askJob = Copy(job, j => { j.Id = "ask"; j.Telegram = true; });
    runner.Update([askJob]);
    host.OnSend = _ => host.Screen = "Do you want to proceed?\n❯ 1. Yes\n  2. No";
    host.ClearNotes();
    Check(runner.RunNow(askJob.Id), "approval task starts");
    await Until(() => host.Notes.Any(n => n.Contains("❓") && n.Contains("Do you want to proceed?")), "approval prompt is relayed to Telegram", 15);
}

static ScheduledJob Copy(ScheduledJob j, Action<ScheduledJob> change)
{
    var c = j.Clone();
    change(c);
    return c;
}

static string ClaudeLine(string type, string uuid, string? parent, string contentJson) =>
    "{\"type\":\"" + type + "\",\"uuid\":\"" + uuid + "\",\"parentUuid\":" + (parent is null ? "null" : "\"" + parent + "\"") +
    ",\"message\":{\"role\":\"" + type + "\",\"content\":" + contentJson + "}}\n";

static async Task Until(Func<bool> condition, string what, int seconds = 10)
{
    var end = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < end)
    {
        if (condition()) { Console.WriteLine("PASS " + what); return; }
        await Task.Delay(50);
    }
    throw new Exception("FAIL (timeout) " + what);
}

/// <summary>앱 흉내. 열린 탭 하나(t1 · project), 새 탭은 t2 로 연다.</summary>
sealed class FakeHost(string cwd, string transcript) : IScheduleHost
{
    private readonly object _lock = new();
    private readonly List<RemoteTab> _tabs =
        [new RemoteTab("t1", "project", "Claude Code", CliProfile.Claude.Backend, cwd, true, transcript, CliProfile.Claude, DateTime.UtcNow.AddSeconds(-30))];
    private readonly List<string> _sent = [];
    private readonly List<ScheduleRun> _finished = [];
    private readonly List<string> _notes = [];
    private Action? _pending;

    public volatile string Screen = "> \n? for shortcuts";
    public Action<string>? OnSend;
    public volatile bool Hold;
    public List<(string Folder, string Backend, string Model, string Effort)> Opened { get; } = [];
    public List<string> Closed { get; } = [];
    public List<string> OnceFired { get; } = [];

    public IReadOnlyList<string> Sent { get { lock (_lock) return _sent.ToList(); } }
    public IReadOnlyList<ScheduleRun> Finished { get { lock (_lock) return _finished.ToList(); } }
    public IReadOnlyList<string> Notes { get { lock (_lock) return _notes.ToList(); } }
    public void ClearNotes() { lock (_lock) _notes.Clear(); }

    /// <summary>붙잡아 둔 답을 내보낸다(겹침 시험).</summary>
    public void Release()
    {
        Action? a;
        lock (_lock) { a = _pending; _pending = null; }
        Screen = "> \n? for shortcuts";
        a?.Invoke();
    }

    public Task<IReadOnlyList<RemoteTab>> TabsAsync()
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<RemoteTab>>(_tabs.Select(t => t with { LastOutputUtc = DateTime.UtcNow.AddSeconds(-30) }).ToList());
    }

    public Task<string?> SendAsync(string tabId, string text)
    {
        lock (_lock) _sent.Add(text);
        if (Hold)
        {
            Screen = "✻ Working… (esc to interrupt)";
            lock (_lock) _pending = () => OnSend?.Invoke(text);
        }
        else OnSend?.Invoke(text);
        return Task.FromResult<string?>(null);
    }

    public Task<bool> KeysAsync(string tabId, string data) => Task.FromResult(true);
    public Task<RemoteScreen?> ScreenAsync(string tabId) => Task.FromResult<RemoteScreen?>(new RemoteScreen(Screen, false));

    public Task<(string? TabId, string? Error)> OpenAsync(ScheduledJob job)
    {
        lock (_lock)
        {
            Opened.Add((job.Folder, job.Backend, job.Model, job.Effort));
            _tabs.Add(new RemoteTab("t2", "⏰ " + job.Name, "Claude Code", job.Backend, job.Folder, true, transcript, CliProfile.Claude, DateTime.UtcNow));
        }
        return Task.FromResult<(string?, string?)>(("t2", null));
    }

    public Task CloseAsync(string tabId)
    {
        lock (_lock) { Closed.Add(tabId); _tabs.RemoveAll(t => t.Id == tabId); }
        return Task.CompletedTask;
    }

    public Task NotifyAsync(string text)
    {
        lock (_lock) _notes.Add(text);
        return Task.CompletedTask;
    }

    public void RunChanged(ScheduledJob job, ScheduleRun run)
    {
        if (run.Status != "running") lock (_lock) _finished.Add(run);
    }

    void IScheduleHost.OnceFired(ScheduledJob job) { lock (_lock) OnceFired.Add(job.Id); }
    public bool English => false;
    public void Log(string message, string level) => Console.WriteLine($"  [{level}] {message}");
}
