using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCLIHub;

/// <summary>
/// 예약 작업 실행 기록. settings.json 과 <b>따로 둔다</b> — 1분마다 도는 작업이 결과를 쓸 때마다 설정 파일
/// 전체를 다시 쓰면, 그 사이 설정 화면의 [저장] 과 겹쳐 한쪽이 다른 쪽을 덮는다.
/// </summary>
public static class ScheduleHistory
{
    public const int Max = 100;
    public static string FilePath => Path.Combine(Settings.DataDir, "schedule-history.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static List<ScheduleRun> Load(string? path = null)
    {
        try
        {
            var file = path ?? FilePath;
            if (File.Exists(file))
                return (JsonSerializer.Deserialize<List<ScheduleRun>>(File.ReadAllText(file)) ?? [])
                    .Where(r => r is not null).TakeLast(Max).ToList();
        }
        catch { /* 깨진 기록 — 처음부터 쌓는다 */ }
        return [];
    }

    public static void Save(IReadOnlyList<ScheduleRun> runs, string? path = null)
    {
        try
        {
            var file = path ?? FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(runs.TakeLast(Max).ToList(), Options), new System.Text.UTF8Encoding(false));
            File.Move(tmp, file, overwrite: true);
        }
        catch { /* 기록 실패가 실행을 막지 않는다 */ }
    }
}

/// <summary>
/// 예약 작업의 앱 쪽 절반 — 설정 화면의 [예약 작업] 칸과 <see cref="ScheduleRunner"/> 가 바라는 것(<see cref="IScheduleHost"/>).
/// 설정 칸의 단추는 <b>누르는 즉시 일어난다</b>(아래 [저장] 과 무관하다) — 텔레그램 칸과 같다.
/// </summary>
public partial class MainWindow : IScheduleHost
{
    private ScheduleRunner? _scheduler;
    private List<ScheduleRun> _scheduleRuns = [];
    private string _scheduleNote = "";

    private void StartSchedules()
    {
        if (_scheduler is not null) { PostSchedules(); return; }
        _scheduleRuns = ScheduleHistory.Load();
        // 앱이 꺼지며 끊긴 실행은 "실행 중" 으로 남아 있다 — 영영 도는 것처럼 보이지 않게 닫아 둔다.
        foreach (var r in _scheduleRuns.Where(r => r.Status == "running"))
        {
            r.Status = "failed";
            r.Error = UiText.Pick("앱이 닫혀 실행이 끊겼습니다.", "Interrupted because the app closed.");
            r.FinishedUtc ??= r.StartedUtc;
        }
        _scheduler = new ScheduleRunner(this);
        _scheduler.Update(Settings.Load().Schedules);
        _scheduler.Start();
        PostSchedules();
    }

    private void StopSchedules()
    {
        try { _scheduler?.Dispose(); } catch { }
        _scheduler = null;
    }

    private static long? Ms(DateTime? local) =>
        local is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Local)).ToUnixTimeMilliseconds() : null;

    private static long? MsUtc(DateTime? utc) =>
        utc is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : null;

    private static object RunView(ScheduleRun r) => new
    {
        runId = r.RunId,
        jobId = r.JobId,
        jobName = r.JobName,
        trigger = r.Trigger,
        started = MsUtc(r.StartedUtc),
        finished = MsUtc(r.FinishedUtc),
        status = r.Status,
        tab = r.Tab,
        answer = r.Answer,
        error = r.Error,
    };

    private object ScheduleState()
    {
        var jobs = Settings.Load().Schedules;
        var status = (_scheduler?.Snapshot() ?? []).ToDictionary(s => s.Id, StringComparer.Ordinal);
        return new
        {
            jobs = jobs.Select(j =>
            {
                status.TryGetValue(j.Id, out var st);
                var last = _scheduleRuns.LastOrDefault(r => r.JobId == j.Id);
                return new
                {
                    j.Id, j.Name, j.Enabled, j.Prompt, j.Target, j.TabName, j.Folder, j.Backend, j.Account, j.Model, j.Effort, j.CloseTab,
                    j.Repeat, j.Every, j.Unit, j.Time, j.Days, j.At, j.Cron, j.Telegram,
                    next = Ms(st?.NextLocal),
                    running = st?.Running == true,
                    pending = st?.Pending == true,
                    last = last is null ? null : RunView(last),
                };
            }).ToList(),
            history = _scheduleRuns.AsEnumerable().Reverse().Take(30).Select(RunView).ToList(),
            telegram = _telegram?.Connected == true && Settings.Load().Telegram.Chats.Count > 0,
            note = _scheduleNote,
        };
    }

    private void PostSchedules() => Post(new { t = "sched.state", state = ScheduleState() });

    /// <summary>화면이 보낸 작업을 읽는다. 모르는 칸·틀린 모양은 기본값으로 둔다(여기서 던지면 저장이 조용히 사라진다).</summary>
    private static ScheduledJob ReadJob(JsonNode? n)
    {
        ScheduledJob job;
        try { job = n?.Deserialize<ScheduledJob>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
        catch { job = new(); }
        job.Normalize();
        job.Name = job.Name.Trim();
        job.TabName = job.TabName.Trim();
        job.Folder = job.Folder.Trim().Trim('"');
        job.Model = job.Model.Trim();
        job.Effort = job.Effort.Trim();
        if (job.Target == "new" && CliProfile.For(job.Backend) is { } cli)
        {
            var cfg = Settings.Load();
            job.Model = CliCatalog.ClampModel(cli.Backend, job.Model);
            var effModel = job.Model.Length > 0 ? job.Model : cli.ModelOf(cfg);
            job.Effort = CliCatalog.ClampEffort(cli.Backend, effModel, job.Effort);
        }
        else
        {
            job.Model = "";
            job.Effort = "";
        }
        job.Time = job.Time.Trim();
        job.Cron = job.Cron.Trim();
        job.Days = job.Days.Where(d => d is >= 0 and <= 6).Distinct().Order().ToList();
        if (job.Name.Length == 0)
        {
            var first = job.Prompt.Trim().Split('\n')[0].Trim();
            job.Name = first.Length > 30 ? first[..30] + "…" : first;
        }
        if (job.Name.Length > 40) job.Name = job.Name[..40];
        return job;
    }

    /// <summary>계정까지 본 검사. 일정·탭 검사는 <see cref="ScheduleCalc.Validate"/>.</summary>
    private static string? ValidateJob(ScheduledJob job)
    {
        if (ScheduleCalc.Validate(job, DateTime.Now) is { } error) return error;
        if (job.Target == "new")
        {
            var cli = CliProfile.For(job.Backend)!;
            if (Accounts.Resolve(Settings.Load(), cli, job.Account) != job.Account) return "계정을 찾지 못했습니다.";
        }
        return null;
    }

    private void HandleScheduleMessage(JsonNode m)
    {
        try
        {
            switch (m["t"]?.GetValue<string>())
            {
                case "sched.get":
                    PostSchedules();
                    return;

                case "sched.save":
                {
                    var job = ReadJob(m["job"]);
                    if (ValidateJob(job) is { } error)
                    {
                        Post(new { t = "sched.saved", ok = false, error });
                        return;
                    }
                    var s = Settings.Load();
                    var old = s.Schedules.FirstOrDefault(j => j.Id == job.Id && job.Id.Length > 0);
                    if (old is null)
                    {
                        job.Id = Guid.NewGuid().ToString("N")[..10];
                        job.AnchorUtc = DateTime.UtcNow;
                        s.Schedules.Add(job);
                    }
                    else
                    {
                        // 일정이 바뀌었을 때만 기준 시각을 다시 잡는다 — 이름·프롬프트만 고쳤는데 "10분마다" 가 처음부터
                        // 다시 세면 회차가 밀린다.
                        job.AnchorUtc = old.AnchorUtc;
                        var probe = job.Clone();
                        if (probe.ScheduleSignature() != old.ScheduleSignature() || old.AnchorUtc == default)
                            job.AnchorUtc = DateTime.UtcNow;
                        s.Schedules[s.Schedules.IndexOf(old)] = job;
                    }
                    s.Save();
                    _scheduleNote = "";
                    _scheduler?.Update(s.Schedules);
                    Log(UiText.Pick($"예약 작업 '{job.Name}' 을 저장했습니다.", $"Saved scheduled task '{job.Name}'."), "i");
                    Post(new { t = "sched.saved", ok = true, id = job.Id });
                    PostSchedules();
                    return;
                }

                case "sched.remove":
                {
                    var id = m["id"]?.GetValue<string>() ?? "";
                    var s = Settings.Load();
                    var job = s.Schedules.FirstOrDefault(j => j.Id == id);
                    if (job is null) return;
                    s.Schedules.Remove(job);
                    s.Save();
                    _scheduler?.Update(s.Schedules);
                    Log(UiText.Pick($"예약 작업 '{job.Name}' 을 지웠습니다.", $"Deleted scheduled task '{job.Name}'."), "i");
                    PostSchedules();
                    return;
                }

                case "sched.enable":
                {
                    var id = m["id"]?.GetValue<string>() ?? "";
                    var on = m["on"]?.GetValue<bool>() == true;
                    var s = Settings.Load();
                    var job = s.Schedules.FirstOrDefault(j => j.Id == id);
                    if (job is null) return;
                    if (on && ValidateJob(job) is { } error)
                    {
                        _scheduleNote = error;
                        PostSchedules();
                        return;
                    }
                    job.Enabled = on;
                    if (on && job.Repeat == "interval") job.AnchorUtc = DateTime.UtcNow;
                    s.Save();
                    _scheduleNote = "";
                    _scheduler?.Update(s.Schedules);
                    Log(on ? UiText.Pick($"예약 작업 '{job.Name}' 을 켰습니다.", $"Enabled scheduled task '{job.Name}'.")
                           : UiText.Pick($"예약 작업 '{job.Name}' 을 껐습니다.", $"Disabled scheduled task '{job.Name}'."), "i");
                    PostSchedules();
                    return;
                }

                case "sched.run":
                {
                    var id = m["id"]?.GetValue<string>() ?? "";
                    var job = Settings.Load().Schedules.FirstOrDefault(j => j.Id == id);
                    if (job is null || _scheduler is null) return;
                    if (ValidateJob(job) is { } error && !(job.Repeat == "once" && error == "이미 지난 시각입니다."))
                    {
                        _scheduleNote = error;
                        PostSchedules();
                        return;
                    }
                    _scheduleNote = "";
                    _scheduler.RunNow(id);
                    PostSchedules();
                    return;
                }

                case "sched.preview":
                {
                    // 일정 칸만 본다 — 프롬프트·폴더를 아직 안 채웠다고 미리보기가 막히면 안 된다.
                    var job = ReadJob(m["job"]);
                    job.Prompt = "preview"; job.Target = "tab"; job.TabName = "preview";
                    if (job.AnchorUtc == default) job.AnchorUtc = DateTime.UtcNow;
                    var error = ScheduleCalc.Validate(job, DateTime.Now);
                    var times = error is null ? ScheduleCalc.Upcoming(job, DateTime.Now, 3).Select(d => Ms(d)).ToList() : [];
                    Post(new { t = "sched.preview", seq = m["seq"]?.GetValue<int>() ?? 0, times, error });
                    return;
                }

                case "sched.pick":
                {
                    var start = m["path"]?.GetValue<string>() is { Length: > 0 } p && Directory.Exists(p) ? p
                        : Settings.Load().LastFolder is { Length: > 0 } last && Directory.Exists(last) ? last
                        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var folder = Dialogs.PickFolder(this, UiText.Pick("예약 작업이 새 탭을 열 폴더를 고르세요", "Choose the folder for the scheduled task's new tab"), start);
                    if (folder is not null) Post(new { t = "sched.picked", path = folder });
                    return;
                }

                case "sched.history.clear":
                    _scheduleRuns = _scheduleRuns.Where(r => r.Status == "running").ToList();
                    ScheduleHistory.Save(_scheduleRuns);
                    PostSchedules();
                    return;
            }
        }
        catch (Exception ex)
        {
            _scheduleNote = UiText.Pick("예약 작업을 바꾸지 못했습니다: ", "Could not change the scheduled task: ") + ex.Message;
            PostSchedules();
        }
    }

    // ── IScheduleHost ───────────────────────────────────────────────────────

    Task<IReadOnlyList<RemoteTab>> IScheduleHost.TabsAsync() => ((IRemoteHost)this).TabsAsync();

    Task<string?> IScheduleHost.SendAsync(string tabId, string text) => ((IRemoteHost)this).SendAsync(tabId, text, []);

    Task<bool> IScheduleHost.KeysAsync(string tabId, string data) => ((IRemoteHost)this).KeysAsync(tabId, data);

    Task<RemoteScreen?> IScheduleHost.ScreenAsync(string tabId) => ((IRemoteHost)this).ScreenAsync(tabId);

    Task<(string? TabId, string? Error)> IScheduleHost.OpenAsync(ScheduledJob job) =>
        Dispatcher.InvokeAsync<(string?, string?)>(() =>
        {
            if (_closingStarted) return (null, UiText.Pick("앱이 닫히는 중입니다.", "The app is closing."));
            var cli = CliProfile.For(job.Backend);
            if (cli is null) return (null, UiText.Pick("알 수 없는 CLI 입니다.", "Unknown CLI."));
            if (!Directory.Exists(job.Folder)) return (null, UiText.Pick("폴더가 없습니다.", "Folder not found."));
            if (!CliLocator.Locate(cli, cli.CachedPath(Settings.Load())).Found) return (null, cli.NotFoundMessage);
            var acc = Accounts.Resolve(Settings.Load(), cli, job.Account);
            if (acc != job.Account) return (null, UiText.Pick("계정을 찾지 못했습니다.", "Account not found."));
            var alias = ("⏰ " + job.Name).Trim();
            if (alias.Length > 40) alias = alias[..40];
            return (OpenTab(job.Folder, cli.Backend, ResumeMode.New, alias: alias, account: acc, background: true,
                            model: job.Model, effort: job.Effort), null);
        }).Task;

    Task IScheduleHost.CloseAsync(string tabId) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (!_tabs.ContainsKey(tabId)) return;
            CloseTab(tabId);
            Post(new { t = "tab.closed", id = tabId });
        }).Task;

    Task IScheduleHost.NotifyAsync(string text)
    {
        var r = _telegram;
        if (r is not { Connected: true }) return Task.CompletedTask;
        var chats = Settings.Load().Telegram.Chats.Select(c => c.Id).ToList();
        return chats.Count == 0 ? Task.CompletedTask : r.NotifyAsync(chats, text);
    }

    void IScheduleHost.RunChanged(ScheduledJob job, ScheduleRun run) => Dispatcher.InvokeAsync(() =>
    {
        var i = _scheduleRuns.FindIndex(r => r.RunId == run.RunId);
        if (i >= 0) _scheduleRuns[i] = run;
        else _scheduleRuns.Add(run);
        if (_scheduleRuns.Count > ScheduleHistory.Max) _scheduleRuns.RemoveRange(0, _scheduleRuns.Count - ScheduleHistory.Max);
        if (run.Status != "running") ScheduleHistory.Save(_scheduleRuns);
        PostSchedules();
    });

    void IScheduleHost.OnceFired(ScheduledJob job) => Dispatcher.InvokeAsync(() =>
    {
        var s = Settings.Load();
        var saved = s.Schedules.FirstOrDefault(j => j.Id == job.Id);
        if (saved is null) return;
        saved.Enabled = false;
        s.Save();
        _scheduler?.Update(s.Schedules);
        PostSchedules();
    });

    bool IScheduleHost.English => Settings.Load().UiLanguage == "en";

    void IScheduleHost.Log(string message, string level) => Dispatcher.InvokeAsync(() => Log(message, level));
}
