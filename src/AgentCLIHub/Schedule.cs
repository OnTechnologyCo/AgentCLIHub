using System.Globalization;
using System.IO;

namespace AgentCLIHub;

/// <summary>
/// 예약 작업 하나 — "언제" CLI 탭에 "무엇을" 보낼까. settings.json 의 <see cref="Settings.Schedules"/> 에 적힌다.
///
/// <b>보낼 곳은 둘이다.</b> <c>tab</c> = 이미 열려 있는 탭(이름으로 찾는다), <c>new</c> = 실행할 때마다 새 탭을
/// 폴더·CLI·계정을 지정해 연다. 탭 번호(t3 …)로 묶지 않는 이유: 번호는 앱을 껐다 켜면 바뀐다.
///
/// <b>반복</b>은 <see cref="Repeat"/> 로 고른다 — <c>interval</c>(N분·N시간마다) · <c>daily</c>(매일 HH:MM) ·
/// <c>weekly</c>(요일 + HH:MM) · <c>once</c>(한 번) · <c>cron</c>(5칸 식). 시각은 전부 <b>이 PC 의 현지 시각</b>이다.
/// </summary>
public sealed class ScheduledJob
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Prompt { get; set; } = "";

    /// <summary><c>tab</c> · <c>new</c>.</summary>
    public string Target { get; set; } = "new";
    /// <summary><c>tab</c> 일 때 보낼 탭의 이름(탭띠에 보이는 이름 — 붙인 이름 또는 폴더 이름).</summary>
    public string TabName { get; set; } = "";
    /// <summary><c>new</c> 일 때 — 작업 폴더 · CLI · 계정(빈 값 = 기본 계정) · 모델 · 추론 강도(빈 값 = 설정 기본값).</summary>
    public string Folder { get; set; } = "";
    public string Backend { get; set; } = "claude_code";
    public string Account { get; set; } = "";
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    /// <summary><c>new</c> 일 때 — 성공하면 그 탭을 닫는다. 짧은 간격이면 탭이 끝없이 쌓이지 않게 켠다.</summary>
    public bool CloseTab { get; set; }

    /// <summary><c>interval</c> · <c>daily</c> · <c>weekly</c> · <c>once</c> · <c>cron</c>.</summary>
    public string Repeat { get; set; } = "interval";
    public int Every { get; set; } = 60;
    /// <summary><c>m</c>(분) · <c>h</c>(시간).</summary>
    public string Unit { get; set; } = "m";
    /// <summary>HH:mm (daily · weekly).</summary>
    public string Time { get; set; } = "09:00";
    /// <summary>요일 0=일 … 6=토 (weekly).</summary>
    public List<int> Days { get; set; } = [];
    /// <summary>yyyy-MM-ddTHH:mm 현지 시각 (once).</summary>
    public string At { get; set; } = "";
    /// <summary>분 시 일 월 요일 (cron).</summary>
    public string Cron { get; set; } = "";

    /// <summary>끝나면 페어링한 텔레그램 대화로 결과를 보낸다(텔레그램 원격이 연결돼 있을 때).</summary>
    public bool Telegram { get; set; }

    /// <summary>간격 반복의 기준 시각. 저장할 때마다 다시 잡는다 — "10분마다" 가 저장한 순간부터 센다.</summary>
    public DateTime AnchorUtc { get; set; }

    public ScheduledJob Clone()
    {
        var c = (ScheduledJob)MemberwiseClone();
        c.Days = [.. Days];
        return c;
    }

    /// <summary>손으로 고친 settings.json 의 빈 칸(null)을 메운다 — 여기서 던지면 설정 전체가 기본값으로 떨어진다.</summary>
    public void Normalize()
    {
        Id = (Id ?? "").Trim(); Name ??= ""; Prompt ??= ""; Target ??= "new"; TabName ??= ""; Folder ??= "";
        Backend ??= "claude_code"; Account ??= ""; Model ??= ""; Effort ??= "";
        Repeat ??= "interval"; Unit ??= "m"; Time ??= "09:00";
        Days ??= []; At ??= ""; Cron ??= "";
    }

    /// <summary>실행 일정이 바뀌었는가를 가리는 값. 이름·프롬프트만 바꾼 저장은 다음 실행 시각을 흔들지 않는다.</summary>
    public string ScheduleSignature() =>
        string.Join('|', Repeat, Every, Unit, Time, string.Join(',', Days.OrderBy(d => d)), At, Cron, AnchorUtc.Ticks, Enabled);
}

/// <summary>예약 작업 한 번의 실행 결과. 실행 기록(schedule-history.json)에 쌓인다.</summary>
public sealed class ScheduleRun
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string JobId { get; set; } = "";
    public string JobName { get; set; } = "";
    /// <summary><c>auto</c>(일정) · <c>manual</c>([지금 실행]).</summary>
    public string Trigger { get; set; } = "auto";
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    /// <summary><c>running</c> · <c>ok</c> · <c>failed</c>.</summary>
    public string Status { get; set; } = "running";
    public string Tab { get; set; } = "";
    public string Answer { get; set; } = "";
    public string Error { get; set; } = "";

    public ScheduleRun Clone() => (ScheduleRun)MemberwiseClone();
}

/// <summary>다음 실행 시각 계산과 검증. 상태가 없어 그대로 시험할 수 있다.</summary>
public static class ScheduleCalc
{
    public const int MaxIntervalMinutes = 7 * 24 * 60;

    public static int IntervalMinutes(ScheduledJob j) => j.Unit == "h" ? j.Every * 60 : j.Every;

    /// <summary>저장 전 검사. 문제가 있으면 사람이 읽을 이유(한국어 — 화면이 번역한다), 없으면 null.</summary>
    public static string? Validate(ScheduledJob j, DateTime nowLocal)
    {
        if (string.IsNullOrWhiteSpace(j.Prompt)) return "프롬프트를 입력하세요.";
        if (j.Target == "tab")
        {
            if (string.IsNullOrWhiteSpace(j.TabName)) return "보낼 탭 이름을 입력하세요.";
        }
        else if (j.Target == "new")
        {
            if (string.IsNullOrWhiteSpace(j.Folder) || !Directory.Exists(j.Folder)) return "폴더가 없습니다.";
            if (CliProfile.For(j.Backend) is null) return "알 수 없는 CLI 입니다.";
        }
        else return "보낼 곳을 고르세요.";

        switch (j.Repeat)
        {
            case "interval":
                if (j.Unit is not ("m" or "h")) return "간격 단위가 올바르지 않습니다.";
                if (j.Every < 1) return "간격은 1분 이상이어야 합니다.";
                if (IntervalMinutes(j) > MaxIntervalMinutes) return "간격이 너무 깁니다(최대 7일).";
                break;
            case "daily":
                if (ParseTime(j.Time) is null) return "시각 형식이 올바르지 않습니다(HH:MM).";
                break;
            case "weekly":
                if (ParseTime(j.Time) is null) return "시각 형식이 올바르지 않습니다(HH:MM).";
                if (j.Days.Count == 0 || j.Days.Any(d => d is < 0 or > 6)) return "요일을 하나 이상 고르세요.";
                break;
            case "once":
                if (ParseAt(j.At) is not { } at) return "날짜·시각 형식이 올바르지 않습니다.";
                if (at <= nowLocal) return "이미 지난 시각입니다.";
                break;
            case "cron":
                if (!CronExpr.TryParse(j.Cron, out var cron)) return "cron 식이 올바르지 않습니다 — 분 시 일 월 요일 5칸이어야 합니다.";
                if (cron!.Next(nowLocal) is null) return "cron 식에서 실행할 시각을 찾지 못했습니다.";
                break;
            default:
                return "반복 방식을 고르세요.";
        }
        return null;
    }

    /// <summary>
    /// <paramref name="afterLocal"/> 보다 <b>뒤의</b> 첫 실행 시각(분 단위, 초 = 0). 없으면 null(지난 1회 · 잘못된 식).
    /// </summary>
    public static DateTime? Next(ScheduledJob j, DateTime afterLocal)
    {
        var after = Truncate(afterLocal);
        switch (j.Repeat)
        {
            case "interval":
            {
                var step = IntervalMinutes(j);
                if (step < 1 || step > MaxIntervalMinutes) return null;
                var anchorUtc = j.AnchorUtc == default ? DateTime.UtcNow : j.AnchorUtc;
                var anchor = Truncate(DateTime.SpecifyKind(anchorUtc, DateTimeKind.Utc).ToLocalTime());
                if (anchor > after) return anchor;
                var passed = (long)Math.Floor((after - anchor).TotalMinutes / step) + 1;
                return anchor.AddMinutes(passed * step);
            }
            case "daily":
            {
                if (ParseTime(j.Time) is not { } t) return null;
                var today = after.Date + t;
                return today > after ? today : today.AddDays(1);
            }
            case "weekly":
            {
                if (ParseTime(j.Time) is not { } t || j.Days.Count == 0) return null;
                for (var i = 0; i <= 7; i++)
                {
                    var day = after.Date.AddDays(i);
                    var at = day + t;
                    if (at > after && j.Days.Contains((int)day.DayOfWeek)) return at;
                }
                return null;
            }
            case "once":
                return ParseAt(j.At) is { } once && Truncate(once) > after ? Truncate(once) : null;
            case "cron":
                return CronExpr.TryParse(j.Cron, out var cron) ? cron!.Next(after) : null;
        }
        return null;
    }

    /// <summary>다음 실행 시각 몇 개 — 설정 화면의 미리보기.</summary>
    public static List<DateTime> Upcoming(ScheduledJob j, DateTime afterLocal, int count)
    {
        var list = new List<DateTime>();
        var cur = afterLocal;
        while (list.Count < count && Next(j, cur) is { } n)
        {
            list.Add(n);
            if (j.Repeat == "once") break;
            cur = n;
        }
        return list;
    }

    public static TimeSpan? ParseTime(string? s) =>
        TimeSpan.TryParseExact((s ?? "").Trim(), [@"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out var t) &&
        t >= TimeSpan.Zero && t < TimeSpan.FromDays(1) ? t : null;

    public static DateTime? ParseAt(string? s) =>
        DateTime.TryParseExact((s ?? "").Trim(), ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? DateTime.SpecifyKind(d, DateTimeKind.Local) : null;

    private static DateTime Truncate(DateTime d) => new(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0, d.Kind);
}

/// <summary>
/// 표준 5칸 cron 식(분 시 일 월 요일). <c>*</c> · <c>*/n</c> · <c>a-b</c> · <c>a-b/n</c> · <c>a,b,c</c> ·
/// 요일·달 영문 이름(MON · JAN …)을 받는다. 요일 7 은 일요일이다.
/// 일과 요일을 둘 다 좁히면 <b>둘 중 하나만 맞아도</b> 돈다(Vixie cron 과 같다).
/// </summary>
public sealed class CronExpr
{
    private readonly bool[] _minute = new bool[60], _hour = new bool[24], _dom = new bool[32], _month = new bool[13], _dow = new bool[7];
    private bool _domAny, _dowAny;

    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] Weekdays = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    public static bool TryParse(string? text, out CronExpr? expr)
    {
        expr = null;
        var parts = (text ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return false;
        var e = new CronExpr();
        if (!Field(parts[0], 0, 59, null, e._minute, out _)) return false;
        if (!Field(parts[1], 0, 23, null, e._hour, out _)) return false;
        if (!Field(parts[2], 1, 31, null, e._dom, out e._domAny)) return false;
        if (!Field(parts[3], 1, 12, Months, e._month, out _)) return false;
        var dow = new bool[8];
        if (!Field(parts[4], 0, 7, Weekdays, dow, out e._dowAny)) return false;
        for (var i = 0; i < 7; i++) e._dow[i] = dow[i];
        if (dow[7]) e._dow[0] = true;
        expr = e;
        return true;
    }

    private static bool Field(string text, int min, int max, string[]? names, bool[] set, out bool any)
    {
        any = text == "*" || text == "?";
        foreach (var piece in text.Split(','))
        {
            if (piece.Length == 0) return false;
            var body = piece;
            var step = 1;
            var slash = piece.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(piece[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1) return false;
                body = piece[..slash];
            }
            int lo, hi;
            if (body is "*" or "?") { lo = min; hi = max; }
            else
            {
                var dash = body.IndexOf('-');
                if (dash > 0)
                {
                    if (!Value(body[..dash], names, min, out lo) || !Value(body[(dash + 1)..], names, min, out hi)) return false;
                }
                else
                {
                    if (!Value(body, names, min, out lo)) return false;
                    hi = slash >= 0 ? max : lo;
                }
            }
            if (lo < min || hi > max || lo > hi) return false;
            for (var v = lo; v <= hi; v += step) set[v] = true;
        }
        return true;
    }

    private static bool Value(string s, string[]? names, int min, out int v)
    {
        if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v)) return true;
        if (names is not null)
        {
            var i = Array.FindIndex(names, n => n.Equals(s, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) { v = i + (min == 1 ? 1 : 0); return true; }
        }
        return false;
    }

    private bool DayMatches(DateTime d)
    {
        var dom = _dom[d.Day];
        var dow = _dow[(int)d.DayOfWeek];
        if (_domAny && _dowAny) return true;
        if (_domAny) return dow;
        if (_dowAny) return dom;
        return dom || dow;
    }

    /// <summary><paramref name="afterLocal"/> 보다 뒤의 첫 시각. 5년 안에 없으면 null(2월 30일 같은 식).</summary>
    public DateTime? Next(DateTime afterLocal)
    {
        var t = new DateTime(afterLocal.Year, afterLocal.Month, afterLocal.Day, afterLocal.Hour, afterLocal.Minute, 0, afterLocal.Kind).AddMinutes(1);
        var end = t.AddYears(5);
        while (t < end)
        {
            if (!_month[t.Month]) { t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, t.Kind).AddMonths(1); continue; }
            if (!DayMatches(t)) { t = t.Date.AddDays(1); continue; }
            if (!_hour[t.Hour]) { t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind).AddHours(1); continue; }
            if (!_minute[t.Minute]) { t = t.AddMinutes(1); continue; }
            return t;
        }
        return null;
    }
}
