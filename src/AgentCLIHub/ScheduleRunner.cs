using System.IO;

namespace AgentCLIHub;

/// <summary>
/// 예약 작업이 앱에 바라는 것. 구현(<see cref="MainWindow"/>)이 UI 스레드로 넘겨 처리한다 —
/// 탭 사전·WebView2·settings.json 쓰기는 UI 스레드에서만 한다.
/// </summary>
public interface IScheduleHost
{
    Task<IReadOnlyList<RemoteTab>> TabsAsync();
    /// <returns>실패 이유. 보냈으면 null.</returns>
    Task<string?> SendAsync(string tabId, string text);
    Task<bool> KeysAsync(string tabId, string data);
    Task<RemoteScreen?> ScreenAsync(string tabId);
    /// <summary>작업에 적힌 폴더·CLI·계정으로 새 탭을 연다(지금 보던 화면을 빼앗지 않는다).</summary>
    Task<(string? TabId, string? Error)> OpenAsync(ScheduledJob job);
    Task CloseAsync(string tabId);
    /// <summary>페어링한 텔레그램 대화로 보낸다. 텔레그램이 꺼져 있으면 아무 일도 안 한다.</summary>
    Task NotifyAsync(string text);
    /// <summary>실행이 시작됐거나 끝났다(사본이 온다). 끝난 것은 실행 기록에 남긴다.</summary>
    void RunChanged(ScheduledJob job, ScheduleRun run);
    /// <summary>"한 번" 작업이 실행됐다 — 꺼 둔다(다시 켜려면 시각을 새로 고른다).</summary>
    void OnceFired(ScheduledJob job);
    bool English { get; }
    void Log(string message, string level);
}

/// <summary>설정 화면에 보일 작업 하나의 지금 상태.</summary>
public sealed record ScheduleStatus(string Id, DateTime? NextLocal, bool Running, bool Pending);

/// <summary>
/// 예약 작업을 돌린다. <b>앱이 떠 있는 동안만</b> 돈다 — 꺼져 있던 동안 지나간 회차는 켜서 몰아 돌리지 않는다
/// (몇 시간 전 지시가 켜자마자 줄줄이 실행되면 안 된다. 텔레그램 원격이 오래된 메시지를 버리는 것과 같은 까닭).
///
/// <b>겹치면 기다린다.</b> 이전 실행이 아직 안 끝났는데 다음 시각이 오면 그 회차를 <i>한 번만</i> 적어 두었다가
/// 끝나는 즉시 이어서 돈다. 그 사이 여러 회차가 지나가도 한 번이다 — 1분 간격 작업이 10분 걸리면 열 번이 줄을
/// 서는 것이 아니라 끝난 뒤 한 번 더 돈다.
///
/// <b>끝을 아는 법</b>은 텔레그램 원격과 같다(<see cref="TelegramRemote"/>): 출력이 잠잠하고, 화면에 작업 중 표시가 없고,
/// 대화 기록의 이번 턴 마지막 조각이 답 글이면 끝이다. 승인 창이 뜨면 사람을 부르고(기록 · 텔레그램) 기다린다.
/// </summary>
public sealed class ScheduleRunner : IDisposable
{
    private sealed class Slot(ScheduledJob job)
    {
        public ScheduledJob Job = job;
        public string Signature = "";
        public DateTime? Next;
        public bool Running;
        public bool Pending;
        public string PendingTrigger = "auto";
    }

    private readonly IScheduleHost _host;
    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>일정을 보는 간격. 1분 단위 작업이 몇 초 안에 돌게 짧게 둔다(하는 일은 시각 비교뿐이다).</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>실행 중 탭을 들여다보는 간격.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(1500);
    /// <summary>출력이 이만큼 잠잠해야 끝·준비로 본다.</summary>
    public TimeSpan Quiet { get; init; } = TimeSpan.FromSeconds(2.5);
    /// <summary>새 탭이 뜨기를 기다리는 시간.</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>열린 탭이 하던 일을 끝내기를(또는 사람이 승인 창을 처리하기를) 기다리는 시간.</summary>
    public TimeSpan IdleWait { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>한 번의 실행을 지켜보는 최대 시간.</summary>
    public TimeSpan Limit { get; init; } = TimeSpan.FromHours(3);
    /// <summary>기록에서 답을 못 찾을 때 화면으로 대신하기까지.</summary>
    public TimeSpan NoTranscript { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>작업 중 표시도 승인 창도 없이 이만큼 멈춰 있으면 끝난 것으로 본다(뒤 회차가 무한정 막히지 않게).</summary>
    public TimeSpan Stuck { get; init; } = TimeSpan.FromSeconds(60);

    public const int MaxAnswer = 8000;

    public ScheduleRunner(IScheduleHost host, Func<DateTime>? clock = null)
    {
        _host = host;
        _clock = clock ?? (() => DateTime.Now);
    }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    private string L(string ko, string en)
    {
        try { return _host.English ? en : ko; } catch { return ko; }
    }

    /// <summary>
    /// 작업 목록을 바꿔 끼운다(설정을 저장할 때마다). 돌고 있는 실행은 그대로 둔다.
    /// 일정이 안 바뀐 작업은 다음 실행 시각을 다시 계산하지 않는다 — 이름만 고쳤는데 회차가 밀리면 안 된다.
    /// </summary>
    public void Update(IEnumerable<ScheduledJob> jobs)
    {
        var now = _clock();
        lock (_lock)
        {
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var j in jobs)
            {
                if (string.IsNullOrEmpty(j.Id)) continue;
                keep.Add(j.Id);
                var sig = j.ScheduleSignature();
                if (!_slots.TryGetValue(j.Id, out var slot)) _slots[j.Id] = slot = new Slot(j.Clone());
                slot.Job = j.Clone();
                if (slot.Signature != sig || (slot.Next is null && j.Enabled && j.Repeat != "once"))
                {
                    slot.Signature = sig;
                    slot.Next = j.Enabled ? ScheduleCalc.Next(j, now) : null;
                }
                if (!j.Enabled) { slot.Next = null; slot.Pending = false; }
            }
            foreach (var id in _slots.Keys.Where(k => !keep.Contains(k)).ToList())
                _slots.Remove(id);
        }
    }

    public IReadOnlyList<ScheduleStatus> Snapshot()
    {
        lock (_lock)
            return _slots.Values.Select(s => new ScheduleStatus(s.Job.Id, s.Next, s.Running, s.Pending)).ToList();
    }

    /// <summary>[지금 실행]. 돌고 있으면 끝난 뒤 한 번 더 돈다. 모르는 작업이면 false.</summary>
    public bool RunNow(string id)
    {
        Slot? start = null;
        lock (_lock)
        {
            if (!_slots.TryGetValue(id, out var slot)) return false;
            if (slot.Running) { slot.Pending = true; slot.PendingTrigger = "manual"; }
            else { slot.Running = true; start = slot; }
        }
        if (start is not null) _ = Task.Run(() => ExecuteAsync(start, "manual"));
        return true;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Tick, ct); } catch (OperationCanceledException) { return; }
            var now = _clock();
            var fire = new List<Slot>();
            var once = new List<ScheduledJob>();
            lock (_lock)
            {
                foreach (var s in _slots.Values)
                {
                    if (!s.Job.Enabled || s.Next is not { } due || now < due) continue;
                    s.Next = s.Job.Repeat == "once" ? null : ScheduleCalc.Next(s.Job, now);
                    if (s.Job.Repeat == "once") once.Add(s.Job.Clone());
                    if (s.Running) { s.Pending = true; s.PendingTrigger = "auto"; }
                    else { s.Running = true; fire.Add(s); }
                }
            }
            foreach (var s in fire) _ = Task.Run(() => ExecuteAsync(s, "auto"));
            foreach (var j in once) { try { _host.OnceFired(j); } catch { } }
        }
    }

    private async Task ExecuteAsync(Slot slot, string trigger)
    {
        ScheduledJob job;
        lock (_lock) job = slot.Job.Clone();
        var run = new ScheduleRun { JobId = job.Id, JobName = job.Name, Trigger = trigger, StartedUtc = DateTime.UtcNow };
        var ct = _cts.Token;
        try { _host.RunChanged(job, run.Clone()); } catch { }
        _host.Log(L($"[예약] '{job.Name}' 실행을 시작합니다.", $"[Schedule] Starting '{job.Name}'."), "i");
        try
        {
            await RunOnceAsync(job, run, ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            run.Status = "failed";
            run.Error = L("앱이 닫혀 실행을 멈췄습니다.", "Stopped because the app closed.");
        }
        catch (Exception ex)
        {
            run.Status = "failed";
            run.Error = ex.Message;
        }
        if (run.Status == "running") run.Status = run.Error.Length > 0 ? "failed" : "ok";
        run.FinishedUtc = DateTime.UtcNow;
        try { _host.RunChanged(job, run.Clone()); } catch { }
        _host.Log(run.Status == "ok"
            ? L($"[예약] '{job.Name}' 실행을 마쳤습니다.", $"[Schedule] '{job.Name}' finished.")
            : L($"[예약] '{job.Name}' 실행 실패 — {run.Error}", $"[Schedule] '{job.Name}' failed — {run.Error}"),
            run.Status == "ok" ? "i" : "w");
        if (job.Telegram && !ct.IsCancellationRequested)
            try { await _host.NotifyAsync(FinishText(job, run)); } catch { }

        var again = false;
        var nextTrigger = "auto";
        lock (_lock)
        {
            slot.Running = false;
            if (slot.Pending && _slots.TryGetValue(job.Id, out var cur) && ReferenceEquals(cur, slot) &&
                (slot.Job.Enabled || slot.PendingTrigger == "manual") && !ct.IsCancellationRequested)
            {
                slot.Pending = false;
                slot.Running = true;
                nextTrigger = slot.PendingTrigger;
                again = true;
            }
            else slot.Pending = false;
        }
        if (again) _ = Task.Run(() => ExecuteAsync(slot, nextTrigger));
    }

    private string Elapsed(ScheduleRun run)
    {
        var s = (int)((run.FinishedUtc ?? DateTime.UtcNow) - run.StartedUtc).TotalSeconds;
        return s < 60 ? L($"{s}초", $"{s}s") : L($"{s / 60}분 {s % 60}초", $"{s / 60}m {s % 60}s");
    }

    private string FinishText(ScheduledJob job, ScheduleRun run)
    {
        var head = run.Status == "ok"
            ? L($"⏰ {job.Name} — ✅ 완료 · {Elapsed(run)}", $"⏰ {job.Name} — ✅ done · {Elapsed(run)}")
            : L($"⏰ {job.Name} — ⚠ 실패: {run.Error}", $"⏰ {job.Name} — ⚠ failed: {run.Error}");
        if (run.Tab.Length > 0) head += "\n" + L("탭: ", "Tab: ") + run.Tab;
        return run.Answer.Length > 0 ? head + "\n\n" + run.Answer : head;
    }

    private async Task<RemoteTab?> FindAsync(string id) =>
        (await _host.TabsAsync()).FirstOrDefault(t => t.Id == id);

    private static void Fail(ScheduleRun run, string error)
    {
        run.Status = "failed";
        run.Error = error;
    }

    private async Task RunOnceAsync(ScheduledJob job, ScheduleRun run, CancellationToken ct)
    {
        string tabId;
        if (job.Target == "tab")
        {
            var want = job.TabName.Trim();
            var tab = (await _host.TabsAsync()).FirstOrDefault(t => string.Equals(t.Name.Trim(), want, StringComparison.OrdinalIgnoreCase));
            if (tab is null) { Fail(run, L($"'{want}' 탭을 찾지 못했습니다.", $"Tab '{want}' was not found.")); return; }
            run.Tab = tab.Name;
            if (!tab.Running) { Fail(run, L($"'{tab.Name}' 탭의 CLI 가 실행 중이 아닙니다.", $"The CLI in '{tab.Name}' is not running.")); return; }
            tabId = tab.Id;
            var busy = await WaitReadyAsync(job, run, tabId, IdleWait, isNew: false, ct);
            if (busy is not null) { Fail(run, busy); return; }
        }
        else
        {
            var (id, error) = await _host.OpenAsync(job);
            if (id is null) { Fail(run, error ?? L("새 탭을 열지 못했습니다.", "Could not open a new tab.")); return; }
            tabId = id;
            run.Tab = (await FindAsync(id))?.Name ?? job.Name;
            var notReady = await WaitReadyAsync(job, run, tabId, ReadyTimeout, isNew: true, ct);
            if (notReady is not null) { Fail(run, notReady); return; }
        }

        var baseline = -1;
        var before = await FindAsync(tabId);
        if (before?.TranscriptPath is { Length: > 0 } path && File.Exists(path))
            try { baseline = TranscriptReader.Read(before.Profile, path).Items.Count; } catch { }

        var sendError = await _host.SendAsync(tabId, job.Prompt);
        if (sendError is not null) { Fail(run, L("보내지 못했습니다: ", "Could not send: ") + sendError); return; }

        await WatchAsync(job, run, tabId, baseline, ct);

        if (run.Status == "ok" && job.Target == "new" && job.CloseTab)
            try { await _host.CloseAsync(tabId); } catch { }
    }

    /// <summary>
    /// 보내도 되는 때까지 기다린다 — 탭이 준비됐고(새 탭), 하던 일이 없고, 승인 창이 없다.
    /// 승인 창(폴더 신뢰 묻기 등)이 뜨면 사람을 부르고 <see cref="IdleWait"/> 만큼 더 기다린다.
    /// </summary>
    /// <returns>못 보낼 이유. 보내도 되면 null.</returns>
    private async Task<string?> WaitReadyAsync(ScheduledJob job, ScheduleRun run, string tabId, TimeSpan timeout, bool isNew, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? promptSig = null;
        var lastWake = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(Poll, ct);
            var tab = await FindAsync(tabId);
            if (tab is null) return L("탭이 닫혔습니다.", "The tab was closed.");
            if (!tab.Running)
            {
                if (isNew) continue;     // 아직 뜨는 중
                return L("CLI 가 실행 중이 아닙니다.", "The CLI is not running.");
            }
            var now = DateTime.UtcNow;
            var quiet = now - tab.LastOutputUtc;
            if (quiet < Quiet) continue;
            var screen = await _host.ScreenAsync(tabId);
            if (screen is null || string.IsNullOrWhiteSpace(screen.Text)) continue;
            if (screen.FocusMode && tab.Backend == CliProfile.Codex.Backend && now - lastWake > TimeSpan.FromSeconds(2))
            {
                lastWake = now;
                await _host.KeysAsync(tabId, "\u001b[I");
            }
            var options = TelegramText.PromptOptions(screen.Text);
            if (options.Count >= 2)
            {
                var sig = TelegramText.PromptSignature(screen.Text, options);
                if (sig != promptSig)
                {
                    promptSig = sig;
                    await NeedsInputAsync(job, run, screen.Text);
                    var extended = DateTime.UtcNow + IdleWait;
                    if (extended > deadline) deadline = extended;
                }
                continue;
            }
            if (TelegramText.IsWorking(screen.Text)) continue;
            return null;
        }
        return isNew
            ? L("새 탭이 준비되지 않아 보내지 못했습니다.", "The new tab did not become ready, so nothing was sent.")
            : L("탭이 계속 작업 중이라 보내지 못했습니다.", "The tab stayed busy, so nothing was sent.");
    }

    private async Task NeedsInputAsync(ScheduledJob job, ScheduleRun run, string screen)
    {
        _host.Log(L($"[예약] '{job.Name}' — {run.Tab} 탭에서 확인이 필요합니다.", $"[Schedule] '{job.Name}' — {run.Tab} needs your input."), "w");
        if (!job.Telegram) return;
        var text = L($"⏰ {job.Name} — ❓ {run.Tab} 탭에서 확인이 필요합니다. 앱 화면이나 텔레그램 /screen 으로 처리하세요.",
                     $"⏰ {job.Name} — ❓ {run.Tab} needs your input. Handle it in the app or with /screen in Telegram.");
        try { await _host.NotifyAsync(text + "\n\n" + TelegramText.ScreenExcerpt(screen, 25, 2500)); } catch { }
    }

    private async Task WatchAsync(ScheduledJob job, ScheduleRun run, string tabId, int baseline, CancellationToken ct)
    {
        string? fileSig = null, promptSig = null;
        List<TranscriptItem>? turn = null;
        var started = DateTime.UtcNow;
        var lastWake = DateTime.MinValue;
        while (true)
        {
            await Task.Delay(Poll, ct);
            var now = DateTime.UtcNow;
            var tab = await FindAsync(tabId);
            if (tab is null) { Fail(run, L("탭이 닫혔습니다.", "The tab was closed.")); return; }
            if (now - started > Limit)
            {
                Fail(run, L("3시간이 지나도 끝나지 않아 지켜보기를 멈췄습니다.", "Stopped watching after 3 hours without finishing."));
                return;
            }

            if (tab.TranscriptPath is { Length: > 0 } path && File.Exists(path))
            {
                var fi = new FileInfo(path);
                var sig = path + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                if (sig != fileSig)
                {
                    fileSig = sig;
                    try { turn = TelegramText.TurnItems(TranscriptReader.Read(tab.Profile, path).Items, job.Prompt, baseline) ?? turn; }
                    catch { }
                }
            }

            if (!tab.Running)
            {
                var answer = turn is null ? "" : TelegramText.FinalAnswer(turn);
                if (answer.Length > 0) { Succeed(run, answer); return; }
                Fail(run, L("CLI 가 끝났습니다.", "The CLI exited."));
                return;
            }

            var quiet = now - tab.LastOutputUtc;
            if (quiet < TimeSpan.FromSeconds(1.2)) continue;
            var screen = await _host.ScreenAsync(tabId);
            if (screen is null) continue;

            if (screen.FocusMode && tab.Backend == CliProfile.Codex.Backend &&
                quiet > TimeSpan.FromSeconds(2) && now - lastWake > TimeSpan.FromSeconds(2))
            {
                lastWake = now;
                await _host.KeysAsync(tabId, "\u001b[I");
            }

            var options = TelegramText.PromptOptions(screen.Text);
            if (options.Count >= 2)
            {
                var sig = TelegramText.PromptSignature(screen.Text, options);
                if (sig != promptSig) { promptSig = sig; await NeedsInputAsync(job, run, screen.Text); }
                continue;
            }
            promptSig = null;

            if (TelegramText.IsWorking(screen.Text)) continue;
            if (quiet < Quiet) continue;

            if (turn is { Count: > 0 } && turn[^1].Kind == ItemKind.Assistant)
            {
                Succeed(run, TelegramText.FinalAnswer(turn));
                return;
            }
            if (turn is null && quiet > NoTranscript)
            {
                Succeed(run, TelegramText.ScreenExcerpt(screen.Text, 40, 3500));
                return;
            }
            if (quiet > Stuck)
            {
                var answer = turn is null ? "" : TelegramText.FinalAnswer(turn);
                Succeed(run, answer.Length > 0 ? answer : TelegramText.ScreenExcerpt(screen.Text, 40, 3500));
                return;
            }
        }
    }

    private static void Succeed(ScheduleRun run, string answer)
    {
        run.Status = "ok";
        run.Answer = TelegramText.Clip(answer.Trim(), MaxAnswer);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
    }
}
