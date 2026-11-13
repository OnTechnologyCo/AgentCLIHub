using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCLIHub;

/// <summary>
/// 에이전트(Claude Code · Codex · Antigravity)가 <c>[⚙ 설정]</c> 화면의 항목을 조회·수정·저장할 때
/// 거치는 호스트 경계. 실제 앱(<see cref="MainWindow"/>)과 자동 검사(<c>HostChecks</c>)가 함께 구현한다 —
/// 검사 코드가 사용자의 실제 <c>settings.json</c> 이나 시작프로그램 레지스트리를 건드리지 않게 한다.
/// </summary>
public interface IAgentConfigHost
{
    string SettingsFilePath { get; }
    string AppVersion { get; }
    bool IsWindowShown { get; }
    Settings LoadSettings();
    void SaveSettings(Settings settings);
    bool IsStartupEnabled();
    void SetStartupEnabled(bool enabled);
    IReadOnlyList<AntigravityModels.Model> AntigravityModels { get; }
    Task<(IReadOnlyList<AntigravityModels.Model> Models, string? Error)> RefreshAntigravityModelsAsync(string path);
    object GetAccountViews(Settings settings);
    bool RenameAccount(Settings settings, string backend, string accountId, string newName, out string error);
    object AddAccountInteractive(string backend, string name, string login);
    void OpenAccountLogin(string accountId);
    void RemoveAccount(string accountId);
    object GetPetStatus(Settings settings);
    void ApplyPet(Settings settings);
    object GetTelegramState();
    void SetTelegramEnabled(bool enabled);
    Task<object> SetTelegramTokenAsync(string token);
    void ClearTelegramToken();
    object CreateTelegramPairCode();
    void RevokeTelegramChat(long chatId);
    object GetScheduleState();
    object SaveSchedule(JsonNode jobNode);
    object SetScheduleEnabled(string id, bool enabled);
    object RunScheduleNow(string id);
    object RemoveSchedule(string id);
    object ClearScheduleHistory();
    void ApplyLiveUi(Settings settings, bool languageChanged, bool signInChanged, bool petChanged);
    void ShowWindow(bool openSettings);
    void HideWindowToTray();
    void ClearSignInData(bool everything);
}

public sealed class AgentConfigService
{
    private readonly IAgentConfigHost _host;

    public AgentConfigService(IAgentConfigHost host) => _host = host;

    public async Task<object> ExecuteAsync(string[] args)
    {
        var sub = args.Length == 0 ? "get" : args[0].Trim().ToLowerInvariant();
        string Arg(int at, string name) =>
            args.Length > at && args[at].Length > 0 ? args[at] : throw new ArgumentException($"{name} 인자가 없습니다.");

        switch (sub)
        {
            case "get":
            case "show":
            case "status":
            {
                var section = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : "all";
                return BuildSnapshot(_host.LoadSettings(), section);
            }

            case "set":
            {
                var key = Arg(1, "항목 이름");
                var value = args.Length > 2 ? string.Join(" ", args.Skip(2)) : "";
                var c = _host.LoadSettings();
                var flags = ApplyKeyValue(c, key, value);
                _host.SaveSettings(c);
                _host.ApplyLiveUi(c, flags.LanguageChanged, flags.SignInChanged, flags.PetChanged);
                return new
                {
                    saved = true,
                    key = flags.CanonicalKey,
                    settings = BuildSettingsView(c),
                };
            }

            case "patch":
            {
                var raw = ReadJsonArgument(args, 1, "JSON 또는 --file <경로>");
                if (JsonNode.Parse(raw) is not JsonObject patch)
                    throw new ArgumentException("settings patch 는 JSON 객체({ ... })를 받아야 합니다.");

                var c = _host.LoadSettings();
                var flags = ApplyPatchObject(c, patch);
                _host.SaveSettings(c);
                _host.ApplyLiveUi(c, flags.LanguageChanged, flags.SignInChanged, flags.PetChanged);
                return new
                {
                    saved = true,
                    updated = flags.UpdatedKeys,
                    settings = BuildSettingsView(c),
                };
            }

            case "open":
                _host.ShowWindow(openSettings: true);
                return new { windowShown = true, view = "settings" };

            case "window":
            {
                var action = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : "status";
                switch (action)
                {
                    case "status":
                    {
                        var c = _host.LoadSettings();
                        return new
                        {
                            shown = _host.IsWindowShown,
                            startInTray = c.StartInTray,
                            closeToTray = c.CloseToTray,
                            runAtStartup = _host.IsStartupEnabled(),
                        };
                    }
                    case "show":
                    case "activate":
                        _host.ShowWindow(openSettings: false);
                        return new { shown = true };
                    case "settings":
                        _host.ShowWindow(openSettings: true);
                        return new { shown = true, view = "settings" };
                    case "hide":
                    case "tray":
                        _host.HideWindowToTray();
                        return new { shown = false, inTray = true };
                    default:
                        throw new ArgumentException("settings window <status|show|hide|settings>");
                }
            }

            case "models-refresh":
            {
                var c = _host.LoadSettings();
                var path = args.Length > 1 ? args[1] : c.AntigravityCliPath;
                var (models, error) = await _host.RefreshAntigravityModelsAsync(path);
                return new
                {
                    path,
                    models = models.Select(m => new { id = m.Id, name = m.Name }).ToList(),
                    error,
                };
            }

            case "bookmark":
            case "bookmarks":
                return HandleBookmarks(args[1..]);

            case "account":
            case "accounts":
                return HandleAccounts(args[1..]);

            case "telegram":
            case "tg":
                return await HandleTelegramAsync(args[1..]);

            case "schedule":
            case "schedules":
            case "sched":
                return HandleSchedules(args[1..]);

            case "signin":
            {
                var action = Arg(1, "동작(clear-passwords | clear-all)");
                if (!args.Skip(2).Any(x => x == "--confirm"))
                    throw new ArgumentException("저장된 비밀번호·로그인 삭제는 되돌릴 수 없습니다. 사용자 확인 후 `--confirm` 을 붙여 실행하세요.");
                if (action.Equals("clear-passwords", StringComparison.OrdinalIgnoreCase))
                {
                    _host.ClearSignInData(everything: false);
                    return new { cleared = "passwords" };
                }
                if (action.Equals("clear-all", StringComparison.OrdinalIgnoreCase))
                {
                    _host.ClearSignInData(everything: true);
                    return new { cleared = "all" };
                }
                throw new ArgumentException("settings signin <clear-passwords|clear-all> --confirm");
            }

            default:
                throw new ArgumentException(
                    $"모르는 settings 하위 명령 '{sub}' — get · set · patch · open · window · bookmark · account · telegram · schedule · models-refresh");
        }
    }

    private object BuildSnapshot(Settings c, string section)
    {
        var settingsView = BuildSettingsView(c);
        var catalog = CliCatalog.ForUi(_host.AntigravityModels);
        var detect = CliProfile.All.ToDictionary(
            p => p.Backend,
            p =>
            {
                var loc = CliLocator.Locate(p, p.CachedPath(c));
                return (object)new
                {
                    found = loc.Found,
                    path = loc.Path,
                    source = loc.Source,
                    bypass = loc.CanBypassCmd,
                    error = loc.Error,
                };
            });

        return section switch
        {
            "all" or "" => new
            {
                file = _host.SettingsFilePath,
                version = _host.AppVersion,
                windowShown = _host.IsWindowShown,
                settings = settingsView,
                choices = new
                {
                    UiLanguage = new[] { "ko", "en" },
                    StartInTray = new[] { "always", "startup", "never" },
                    CloseToTray = new[] { true, false },
                    RunAtStartup = new[] { true, false },
                },
                catalog,
                detect,
                accounts = _host.GetAccountViews(c),
                pet = _host.GetPetStatus(c),
                telegram = _host.GetTelegramState(),
                schedules = _host.GetScheduleState(),
            },
            "ui" or "tray" or "startup" => new
            {
                c.UiLanguage,
                RunAtStartup = _host.IsStartupEnabled(),
                c.StartInTray,
                c.CloseToTray,
                windowShown = _host.IsWindowShown,
            },
            "cli" or "models" => new
            {
                c.ClaudeModel, c.ClaudeEffort, c.ClaudeCodePermissionMode, c.ClaudeCliPath,
                c.CodexModel, c.CodexEffort, c.CodexSandbox, c.CodexApproval, c.CodexCliPath,
                c.AntigravityModel, c.AntigravityEffort, c.AntigravityPermission, c.AntigravityCliPath,
                catalog,
                detect,
            },
            "bookmarks" or "bookmark" => c.Bookmarks.Select((b, index) => new { index, b.Name, b.Url, b.Show }).ToList(),
            "signin" or "login" => new { c.GuardClaudeLogin, c.KeepSignedIn, c.SavePasswords },
            "accounts" or "account" => _host.GetAccountViews(c),
            "pet" => _host.GetPetStatus(c),
            "telegram" or "tg" => _host.GetTelegramState(),
            "schedules" or "schedule" or "sched" => _host.GetScheduleState(),
            _ => throw new ArgumentException(
                $"모르는 설정 구역 '{section}' — all · ui · startup · tray · cli · bookmarks · signin · accounts · pet · telegram · schedules"),
        };
    }

    private object BuildSettingsView(Settings c) => new
    {
        c.UiLanguage,
        RunAtStartup = _host.IsStartupEnabled(),
        c.StartInTray,
        c.CloseToTray,
        c.ClaudeModel,
        c.ClaudeEffort,
        c.ClaudeCodePermissionMode,
        c.ClaudeCliPath,
        c.CodexModel,
        c.CodexEffort,
        c.CodexSandbox,
        c.CodexApproval,
        c.CodexCliPath,
        c.AntigravityModel,
        c.AntigravityEffort,
        c.AntigravityPermission,
        c.AntigravityCliPath,
        Bookmarks = c.Bookmarks.Select(b => new { b.Name, b.Url, b.Show }).ToList(),
        c.GuardClaudeLogin,
        c.KeepSignedIn,
        c.SavePasswords,
        Pet = c.Pet ?? new PetSettings(),
    };

    private sealed class ApplyResult
    {
        public string CanonicalKey { get; set; } = "";
        public List<string> UpdatedKeys { get; } = [];
        public bool LanguageChanged { get; set; }
        public bool SignInChanged { get; set; }
        public bool PetChanged { get; set; }
    }

    private ApplyResult ApplyPatchObject(Settings c, JsonObject patch)
    {
        var result = new ApplyResult();

        // 모델을 먼저 반영해야 같은 패치 안의 추론 강도(Codex 모델별 추론 강도) 검증이 새 모델을 기준으로 돈다.
        var ordered = patch.OrderBy(kv => kv.Key.EndsWith("Model", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        foreach (var (rawKey, node) in ordered)
        {
            var norm = NormKey(rawKey);
            if (norm == "bookmarks")
            {
                if (node is not JsonArray arr)
                    throw new ArgumentException("'Bookmarks' 는 배열([ { \"Name\": ..., \"Url\": ..., \"Show\": true } ])이어야 합니다.");
                c.Bookmarks = MainWindow.ReadBookmarks(arr);
                result.UpdatedKeys.Add("Bookmarks");
                continue;
            }
            if (norm == "pet" && node is JsonObject petObj)
            {
                c.Pet ??= new PetSettings();
                foreach (var (pk, pv) in petObj)
                {
                    var pNorm = NormKey("pet." + pk);
                    var sub = ApplyKeyValue(c, pNorm, NodeToText(pv));
                    result.UpdatedKeys.Add(sub.CanonicalKey);
                    result.PetChanged = true;
                }
                continue;
            }

            var step = ApplyKeyValue(c, rawKey, NodeToText(node));
            result.UpdatedKeys.Add(step.CanonicalKey);
            result.LanguageChanged |= step.LanguageChanged;
            result.SignInChanged |= step.SignInChanged;
            result.PetChanged |= step.PetChanged;
        }
        return result;
    }

    private ApplyResult ApplyKeyValue(Settings c, string rawKey, string rawValue)
    {
        var res = new ApplyResult();
        var v = (rawValue ?? "").Trim();
        var key = NormKey(rawKey);

        switch (key)
        {
            case "uilanguage" or "language" or "lang":
            {
                if (!v.Equals("ko", StringComparison.OrdinalIgnoreCase) &&
                    !v.Equals("en", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"'UiLanguage' 값 '{v}' 는 허용되지 않습니다. 허용 값: ko, en");
                var next = Settings.NormalizeLanguage(v);
                res.LanguageChanged = c.UiLanguage != next;
                c.UiLanguage = next;
                res.CanonicalKey = nameof(Settings.UiLanguage);
                return res;
            }

            case "runatstartup" or "startuprun" or "startup":
            {
                var on = ParseBool(v, "RunAtStartup");
                _host.SetStartupEnabled(on);
                res.CanonicalKey = "RunAtStartup";
                return res;
            }

            case "startintray" or "traystart" or "traymode":
            {
                var low = v.ToLowerInvariant();
                var next = low switch
                {
                    "always" or "true" or "on" => "always",
                    "startup" or "autostart" => "startup",
                    "never" or "false" or "off" or "window" => "never",
                    _ => throw new ArgumentException($"'StartInTray' 값 '{v}' 는 허용되지 않습니다. 허용 값: always, startup, never"),
                };
                c.StartInTray = next;
                res.CanonicalKey = nameof(Settings.StartInTray);
                return res;
            }

            case "closetotray" or "trayclose" or "hidetotrayonclose":
                c.CloseToTray = ParseBool(v, nameof(Settings.CloseToTray));
                res.CanonicalKey = nameof(Settings.CloseToTray);
                return res;

            case "claudemodel":
                c.ClaudeModel = ClearIfDefault(v);
                c.ClaudeEffort = CliCatalog.ClampEffort(CliProfile.Claude.Backend, c.ClaudeModel, c.ClaudeEffort);
                res.CanonicalKey = nameof(Settings.ClaudeModel);
                return res;

            case "claudeeffort":
                c.ClaudeEffort = ValidateEffort(CliProfile.Claude.Backend, c.ClaudeModel, ClearIfDefault(v), nameof(Settings.ClaudeEffort));
                res.CanonicalKey = nameof(Settings.ClaudeEffort);
                return res;

            case "claudecodepermissionmode" or "claudepermission" or "claudepermissionmode":
                c.ClaudeCodePermissionMode = ValidatePermission("ClaudeCodePermissionMode", ClearIfDefault(v));
                res.CanonicalKey = nameof(Settings.ClaudeCodePermissionMode);
                return res;

            case "codexmodel":
            {
                var next = ClearIfDefault(v);
                if (next.Length > 0 && CliCatalog.ModelsAreComplete(CliProfile.Codex.Backend))
                {
                    var allowed = CliCatalog.Models(CliProfile.Codex.Backend);
                    var match = allowed.FirstOrDefault(x => string.Equals(x, next, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"'CodexModel' 값 '{next}' 는 허용되지 않습니다. 허용 값: (빈 값/default), {string.Join(", ", allowed)}");
                    next = match;
                }
                c.CodexModel = next;
                c.CodexEffort = CliCatalog.ClampEffort(CliProfile.Codex.Backend, c.CodexModel, c.CodexEffort);
                res.CanonicalKey = nameof(Settings.CodexModel);
                return res;
            }

            case "codexeffort":
                c.CodexEffort = ValidateEffort(CliProfile.Codex.Backend, c.CodexModel, ClearIfDefault(v), nameof(Settings.CodexEffort));
                res.CanonicalKey = nameof(Settings.CodexEffort);
                return res;

            case "codexsandbox":
                c.CodexSandbox = ValidatePermission("CodexSandbox", ClearIfDefault(v));
                res.CanonicalKey = nameof(Settings.CodexSandbox);
                return res;

            case "codexapproval":
                c.CodexApproval = ValidatePermission("CodexApproval", ClearIfDefault(v));
                res.CanonicalKey = nameof(Settings.CodexApproval);
                return res;

            case "antigravitymodel" or "agymodel":
                c.AntigravityModel = ClearIfDefault(v);
                c.AntigravityEffort = CliCatalog.ClampEffort(CliProfile.Antigravity.Backend, c.AntigravityModel, c.AntigravityEffort);
                res.CanonicalKey = nameof(Settings.AntigravityModel);
                return res;

            case "antigravityeffort" or "agyeffort":
                c.AntigravityEffort = ValidateEffort(CliProfile.Antigravity.Backend, c.AntigravityModel, ClearIfDefault(v), nameof(Settings.AntigravityEffort));
                res.CanonicalKey = nameof(Settings.AntigravityEffort);
                return res;

            case "antigravitypermission" or "agypermission":
                c.AntigravityPermission = ValidatePermission(nameof(Settings.AntigravityPermission), ClearIfDefault(v));
                res.CanonicalKey = nameof(Settings.AntigravityPermission);
                return res;

            case "claudeclipath" or "claudepath":
                c.ClaudeCliPath = ClearIfAuto(v);
                res.CanonicalKey = nameof(Settings.ClaudeCliPath);
                return res;

            case "codexclipath" or "codexpath":
                c.CodexCliPath = ClearIfAuto(v);
                res.CanonicalKey = nameof(Settings.CodexCliPath);
                return res;

            case "antigravityclipath" or "agyclipath" or "agypath":
                c.AntigravityCliPath = ClearIfAuto(v);
                res.CanonicalKey = nameof(Settings.AntigravityCliPath);
                return res;

            case "guardclaudelogin":
                c.GuardClaudeLogin = ParseBool(v, nameof(Settings.GuardClaudeLogin));
                res.CanonicalKey = nameof(Settings.GuardClaudeLogin);
                return res;

            case "keepsignedin":
                c.KeepSignedIn = ParseBool(v, nameof(Settings.KeepSignedIn));
                res.SignInChanged = true;
                res.CanonicalKey = nameof(Settings.KeepSignedIn);
                return res;

            case "savepasswords":
                c.SavePasswords = ParseBool(v, nameof(Settings.SavePasswords));
                res.SignInChanged = true;
                res.CanonicalKey = nameof(Settings.SavePasswords);
                return res;

            case "telegramenabled":
            {
                var on = ParseBool(v, "Telegram.Enabled");
                c.Telegram.Enabled = on;
                _host.SetTelegramEnabled(on);
                res.CanonicalKey = "Telegram.Enabled";
                return res;
            }

            case "petenabled" or "petvisible":
                c.Pet ??= new PetSettings();
                c.Pet.SetOption("visible", ParseBool(v, "Pet.Enabled") ? "on" : "off");
                res.PetChanged = true;
                res.CanonicalKey = "Pet.Enabled";
                return res;

            case "petdesktop" or "petlocation":
                c.Pet ??= new PetSettings();
                if (key == "petdesktop")
                    c.Pet.SetOption("location", ParseBool(v, "Pet.Desktop") ? "desktop" : "app");
                else
                    c.Pet.SetOption("location", v);
                res.PetChanged = true;
                res.CanonicalKey = "Pet.Desktop";
                return res;

            case "petsize":
                c.Pet ??= new PetSettings();
                c.Pet.SetOption("size", v);
                res.PetChanged = true;
                res.CanonicalKey = "Pet.Size";
                return res;

            case "petfollowcursor" or "petcursor":
                c.Pet ??= new PetSettings();
                c.Pet.SetOption("cursor", ParseBool(v, "Pet.FollowCursor") ? "on" : "off");
                res.PetChanged = true;
                res.CanonicalKey = "Pet.FollowCursor";
                return res;

            case "petanimate" or "petanimation":
                c.Pet ??= new PetSettings();
                c.Pet.SetOption("animation", ParseBool(v, "Pet.Animate") ? "on" : "off");
                res.PetChanged = true;
                res.CanonicalKey = "Pet.Animate";
                return res;

            case "petname":
                c.Pet ??= new PetSettings();
                c.Pet.SetOption("name", v);
                res.PetChanged = true;
                res.CanonicalKey = "Pet.Name";
                return res;

            default:
                throw new ArgumentException(
                    $"모르는 설정 항목 '{rawKey}' 입니다. 허용 항목: UiLanguage, RunAtStartup, StartInTray, CloseToTray, " +
                    "ClaudeModel, ClaudeEffort, ClaudeCodePermissionMode, ClaudeCliPath, " +
                    "CodexModel, CodexEffort, CodexSandbox, CodexApproval, CodexCliPath, " +
                    "AntigravityModel, AntigravityEffort, AntigravityPermission, AntigravityCliPath, " +
                    "GuardClaudeLogin, KeepSignedIn, SavePasswords, Telegram.Enabled, " +
                    "Pet.Enabled, Pet.Location, Pet.Size, Pet.FollowCursor, Pet.Animate, Pet.Name");
        }
    }

    private object HandleBookmarks(string[] args)
    {
        var action = args.Length == 0 ? "list" : args[0].Trim().ToLowerInvariant();
        var c = _host.LoadSettings();

        int ParseIndex(int at)
        {
            if (args.Length <= at || !int.TryParse(args[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                throw new ArgumentException("북마크 순번(0부터 시작)이 필요합니다.");
            if (idx < 0 || idx >= c.Bookmarks.Count)
                throw new ArgumentException($"북마크 순번 {idx} 가 범위를 벗어났습니다(현재 {c.Bookmarks.Count}개).");
            return idx;
        }

        switch (action)
        {
            case "list":
                return c.Bookmarks.Select((b, index) => new { index, b.Name, b.Url, b.Show }).ToList();

            case "add":
            {
                if (args.Length < 3)
                    throw new ArgumentException("settings bookmark add <이름> <주소> [show=true|false]");
                if (c.Bookmarks.Count >= 30)
                    throw new InvalidOperationException("브라우저 바로가기는 최대 30개까지 추가할 수 있습니다.");
                var url = BrowserHost.Normalize(args[2]);
                if (url.Length == 0) throw new ArgumentException("주소가 비어 있습니다.");
                var name = args[1].Trim();
                if (name.Length == 0) name = BrowserHost.HostOf(url);
                if (name.Length > 40) name = name[..40];
                var show = args.Length <= 3 || ParseBool(args[3], "Show");
                c.Bookmarks.Add(new Bookmark { Name = name, Url = url, Show = show });
                _host.SaveSettings(c);
                _host.ApplyLiveUi(c, false, false, false);
                return c.Bookmarks.Select((b, index) => new { index, b.Name, b.Url, b.Show }).ToList();
            }

            case "update":
            {
                var idx = ParseIndex(1);
                if (args.Length < 4)
                    throw new ArgumentException("settings bookmark update <순번> <이름> <주소> [show=true|false]");
                var url = BrowserHost.Normalize(args[3]);
                if (url.Length == 0) throw new ArgumentException("주소가 비어 있습니다.");
                var name = args[2].Trim();
                if (name.Length == 0) name = BrowserHost.HostOf(url);
                if (name.Length > 40) name = name[..40];
                var show = args.Length <= 4 ? c.Bookmarks[idx].Show : ParseBool(args[4], "Show");
                c.Bookmarks[idx] = new Bookmark { Name = name, Url = url, Show = show };
                _host.SaveSettings(c);
                _host.ApplyLiveUi(c, false, false, false);
                return c.Bookmarks.Select((b, index) => new { index, b.Name, b.Url, b.Show }).ToList();
            }

            case "move":
            {
                var from = ParseIndex(1);
                if (args.Length <= 2)
                    throw new ArgumentException("settings bookmark move <순번> <목표순번|up|down>");
                var dir = args[2].Trim().ToLowerInvariant();
                int to = dir switch
                {
                    "up" => Math.Max(0, from - 1),
                    "down" => Math.Min(c.Bookmarks.Count - 1, from + 1),
                    _ => ParseIndex(2),
                };
                var item = c.Bookmarks[from];
                c.Bookmarks.RemoveAt(from);
                c.Bookmarks.Insert(to, item);
                _host.SaveSettings(c);
                _host.ApplyLiveUi(c, false, false, false);
                return c.Bookmarks.Select((b, index) => new { index, b.Name, b.Url, b.Show }).ToList();
            }

            case "remove" or "delete":
            {
                var idx = ParseIndex(1);
                c.Bookmarks.RemoveAt(idx);
                _host.SaveSettings(c);
                _host.ApplyLiveUi(c, false, false, false);
                return c.Bookmarks.Select((b, index) => new { index, b.Name, b.Url, b.Show }).ToList();
            }

            default:
                throw new ArgumentException("settings bookmark <list|add|update|move|remove>");
        }
    }

    private object HandleAccounts(string[] args)
    {
        var action = args.Length == 0 ? "list" : args[0].Trim().ToLowerInvariant();
        var c = _host.LoadSettings();

        switch (action)
        {
            case "list":
                return _host.GetAccountViews(c);

            case "rename":
            {
                if (args.Length < 3)
                    throw new ArgumentException("settings account rename <계정ID|default:CLI> <새이름> (또는 settings account rename <CLI> <계정ID|default> <새이름>)");

                string backend;
                string id;
                string newName;

                if (args[1].StartsWith("default:", StringComparison.OrdinalIgnoreCase))
                {
                    backend = NormalizeBackend(args[1]["default:".Length..]);
                    id = "";
                    newName = string.Join(" ", args.Skip(2)).Trim();
                }
                else if (args.Length >= 4 && TryNormalizeBackend(args[1], out var explicitBackend))
                {
                    backend = explicitBackend;
                    id = args[2].Equals("default", StringComparison.OrdinalIgnoreCase) ? "" : args[2].Trim();
                    newName = string.Join(" ", args.Skip(3)).Trim();
                }
                else
                {
                    id = args[1].Trim();
                    var found = Accounts.Find(c, id)
                        ?? throw new ArgumentException($"'{id}' 계정을 찾지 못했습니다. 기본 계정은 'default:claude_code' 처럼 지정하세요.");
                    backend = found.Backend;
                    newName = string.Join(" ", args.Skip(2)).Trim();
                }

                if (!_host.RenameAccount(c, backend, id, newName, out var error))
                    throw new ArgumentException(error);
                return _host.GetAccountViews(_host.LoadSettings());
            }

            case "add":
            {
                if (args.Length < 3)
                    throw new ArgumentException("settings account add <claude_code|codex_cli|antigravity_cli> <이름> [sso|console]");
                var backend = NormalizeBackend(args[1]);
                var name = args[2].Trim();
                var login = args.Length > 3 ? args[3].Trim().ToLowerInvariant() : "";
                return _host.AddAccountInteractive(backend, name, login);
            }

            case "login":
            {
                if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
                    throw new ArgumentException("settings account login <계정ID>");
                _host.OpenAccountLogin(args[1].Trim());
                return new { loginOpened = true, id = args[1].Trim() };
            }

            case "remove" or "delete":
            {
                if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
                    throw new ArgumentException("settings account remove <계정ID>");
                _host.RemoveAccount(args[1].Trim());
                return _host.GetAccountViews(_host.LoadSettings());
            }

            default:
                throw new ArgumentException("settings account <list|rename|add|login|remove>");
        }
    }

    private async Task<object> HandleTelegramAsync(string[] args)
    {
        var action = args.Length == 0 ? "status" : args[0].Trim().ToLowerInvariant();
        switch (action)
        {
            case "status":
            case "get":
                return _host.GetTelegramState();

            case "enable":
            {
                if (args.Length < 2) throw new ArgumentException("settings telegram enable <on|off>");
                _host.SetTelegramEnabled(ParseBool(args[1], "Telegram.Enabled"));
                return _host.GetTelegramState();
            }

            case "set-token":
            {
                if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
                    throw new ArgumentException("settings telegram set-token <BotFather 토큰>");
                return await _host.SetTelegramTokenAsync(args[1].Trim());
            }

            case "clear-token":
                _host.ClearTelegramToken();
                return _host.GetTelegramState();

            case "pair":
                return _host.CreateTelegramPairCode();

            case "revoke":
            {
                if (args.Length < 2 || !long.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatId))
                    throw new ArgumentException("settings telegram revoke <chatId>");
                _host.RevokeTelegramChat(chatId);
                return _host.GetTelegramState();
            }

            default:
                throw new ArgumentException("settings telegram <status|enable|set-token|clear-token|pair|revoke>");
        }
    }

    private object HandleSchedules(string[] args)
    {
        var action = args.Length == 0 ? "list" : args[0].Trim().ToLowerInvariant();
        switch (action)
        {
            case "list" or "status" or "get":
                return _host.GetScheduleState();

            case "save":
            {
                var raw = ReadJsonArgument(args, 1, "예약 작업 JSON 또는 --file <경로>");
                var node = JsonNode.Parse(raw) ?? throw new ArgumentException("유효한 JSON 객체가 아닙니다.");
                return _host.SaveSchedule(node);
            }

            case "enable":
            {
                if (args.Length < 3) throw new ArgumentException("settings schedule enable <id> <on|off>");
                return _host.SetScheduleEnabled(args[1].Trim(), ParseBool(args[2], "Enabled"));
            }

            case "run":
            {
                if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
                    throw new ArgumentException("settings schedule run <id>");
                return _host.RunScheduleNow(args[1].Trim());
            }

            case "remove" or "delete":
            {
                if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
                    throw new ArgumentException("settings schedule remove <id>");
                return _host.RemoveSchedule(args[1].Trim());
            }

            case "clear-history":
                return _host.ClearScheduleHistory();

            default:
                throw new ArgumentException("settings schedule <list|save|enable|run|remove|clear-history>");
        }
    }

    private static string ReadJsonArgument(string[] args, int startIndex, string label)
    {
        if (args.Length <= startIndex)
            throw new ArgumentException($"{label} 가 필요합니다.");
        if (args[startIndex] is "--file" or "-f")
        {
            if (args.Length <= startIndex + 1 || string.IsNullOrWhiteSpace(args[startIndex + 1]))
                throw new ArgumentException("--file 뒤에 JSON 파일 경로가 필요합니다.");
            var path = Path.GetFullPath(args[startIndex + 1]);
            if (!File.Exists(path))
                throw new FileNotFoundException($"파일을 찾지 못했습니다: {path}", path);
            return File.ReadAllText(path);
        }
        return string.Join(" ", args.Skip(startIndex));
    }

    private static string NodeToText(JsonNode? node) => node switch
    {
        null => "",
        JsonValue val when val.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue val when val.TryGetValue<int>(out var i) => i.ToString(CultureInfo.InvariantCulture),
        JsonValue val when val.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    private static string NormKey(string key) =>
        new string((key ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string ClearIfDefault(string v) =>
        v.Equals("default", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("none", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("clear", StringComparison.OrdinalIgnoreCase)
            ? ""
            : v;

    private static string ClearIfAuto(string v) =>
        v.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("default", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("clear", StringComparison.OrdinalIgnoreCase)
            ? ""
            : v;

    private static bool ParseBool(string value, string name) =>
        value.Trim().ToLowerInvariant() switch
        {
            "true" or "on" or "1" or "yes" => true,
            "false" or "off" or "0" or "no" => false,
            _ => throw new ArgumentException($"'{name}' 값 '{value}' 는 허용되지 않습니다. 허용 값: true/false (또는 on/off)"),
        };

    private static string ValidateEffort(string backend, string model, string value, string fieldName)
    {
        if (value.Length == 0) return "";
        var allowed = CliCatalog.Efforts(backend, model);
        var match = allowed.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new ArgumentException($"'{fieldName}' 값 '{value}' 는 허용되지 않습니다. 허용 값: (빈 값/default), {string.Join(", ", allowed)}");
        return match;
    }

    private static string ValidatePermission(string key, string value)
    {
        if (value.Length == 0 || value.Equals("default", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Equals("clear", StringComparison.OrdinalIgnoreCase))
            return "";
        var field = CliCatalog.Backends.SelectMany(CliCatalog.Permissions).FirstOrDefault(x => x.Key == key)
            ?? throw new ArgumentException($"권한 항목 '{key}' 를 찾지 못했습니다.");
        var match = field.Choices.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new ArgumentException($"'{key}' 값 '{value}' 는 허용되지 않습니다. 허용 값: (빈 값/default), {string.Join(", ", field.Choices)}");
        return match;
    }

    private static bool TryNormalizeBackend(string raw, out string backend)
    {
        backend = raw.Trim().ToLowerInvariant() switch
        {
            "claude" or "claude_code" or "cc" => CliProfile.Claude.Backend,
            "codex" or "codex_cli" or "cx" => CliProfile.Codex.Backend,
            "antigravity" or "antigravity_cli" or "agy" or "ag" => CliProfile.Antigravity.Backend,
            _ => "",
        };
        return backend.Length > 0;
    }

    private static string NormalizeBackend(string raw) =>
        TryNormalizeBackend(raw, out var backend)
            ? backend
            : throw new ArgumentException($"모르는 CLI '{raw}' 입니다. 허용 값: claude_code, codex_cli, antigravity_cli");
}

public partial class MainWindow : IAgentConfigHost
{
    private AgentConfigService? _agentConfig;

    private Task<object> AgentConfigCommand(string[] args)
    {
        if (_closingStarted) throw new InvalidOperationException("앱이 종료 중입니다.");
        _agentConfig ??= new AgentConfigService(this);
        return _agentConfig.ExecuteAsync(args);
    }

    string IAgentConfigHost.SettingsFilePath => Settings.FilePath;
    string IAgentConfigHost.AppVersion => AppVersion;
    bool IAgentConfigHost.IsWindowShown => IsShownToUser;
    Settings IAgentConfigHost.LoadSettings() => Settings.Load();
    void IAgentConfigHost.SaveSettings(Settings settings) => settings.Save();
    bool IAgentConfigHost.IsStartupEnabled() => StartupRegistration.IsEnabled();
    void IAgentConfigHost.SetStartupEnabled(bool enabled) => StartupRegistration.SetEnabled(enabled);

    IReadOnlyList<AntigravityModels.Model> IAgentConfigHost.AntigravityModels =>
        _modelPath == Settings.Load().AntigravityCliPath ? _antigravityModels : [];

    async Task<(IReadOnlyList<AntigravityModels.Model> Models, string? Error)> IAgentConfigHost.RefreshAntigravityModelsAsync(string path)
    {
        var (cliPath, result) = await Task.Run(async () =>
        {
            var cli = CliLocator.Locate(CliProfile.Antigravity, path);
            return (cli.Path, await AntigravityModels.ReadAsync(cli, CancellationToken.None));
        });
        if (result.Error is null)
        {
            _modelPath = path;
            _antigravityModels = result.Models;
            UsageProbe.MarkAntigravitySignedIn(cliPath);
        }
        Post(new
        {
            t = "conf.models",
            request = ++_modelRequest,
            models = result.Models.Select(m => m.Id).ToArray(),
            modelLabels = result.Models.ToDictionary(m => m.Id, m => m.Name),
            error = result.Error,
        });
        return (result.Models, result.Error);
    }

    object IAgentConfigHost.GetAccountViews(Settings c) => BuildAccountViews(c);

    bool IAgentConfigHost.RenameAccount(Settings c, string backend, string accountId, string newName, out string error)
    {
        var acc = Accounts.Find(c, accountId);
        var cli = CliProfile.For(acc?.Backend ?? backend);
        if (cli is null) { error = "알 수 없는 CLI 입니다."; return false; }
        if (!Accounts.Rename(c, cli, accountId, newName, out error)) return false;
        c.Save();
        PostConf();
        foreach (var tid in _tabs.Where(x => x.Value.Account == accountId && x.Value.Cli == cli).Select(x => x.Key))
            Post(new { t = "tab.account", id = tid, account = accountId, accountName = AccountLabel(cli, accountId) });
        return true;
    }

    object IAgentConfigHost.AddAccountInteractive(string backend, string name, string login)
    {
        var cli = CliProfile.For(backend) ?? throw new ArgumentException("알 수 없는 CLI 입니다.");
        if (!cli.SupportsAccounts) throw new InvalidOperationException("이 CLI 는 추가 계정을 지원하지 않습니다.");
        var c = Settings.Load();
        if (!CliLocator.Locate(cli, cli.CachedPath(c)).Found)
            throw new InvalidOperationException(cli.NotFoundMessage);
        ShowFromTray();
        AddAccount(backend, name, login);
        var updated = Settings.Load();
        var added = updated.Accounts.LastOrDefault(a => a.Backend == cli.Backend);
        return new { added = added?.Id ?? "", name = added?.Name ?? name, backend = cli.Backend, loginTabOpened = true };
    }

    void IAgentConfigHost.OpenAccountLogin(string accountId)
    {
        var c = Settings.Load();
        var acc = Accounts.Find(c, accountId) ?? throw new ArgumentException($"'{accountId}' 계정이 없습니다.");
        ShowFromTray();
        LoginAccount(acc.Id);
    }

    void IAgentConfigHost.RemoveAccount(string accountId)
    {
        var busy = _tabs.Values.Where(h => h.Account == accountId).ToList();
        if (busy.Count > 0)
            throw new InvalidOperationException($"이 계정을 쓰는 탭이 {busy.Count}개 열려 있어 지울 수 없습니다.");
        var c = Settings.Load();
        if (!Accounts.Remove(c, accountId, out var err))
            throw new ArgumentException(err);
        c.Save();
        PostConf();
    }

    object IAgentConfigHost.GetPetStatus(Settings settings) => PetStatus();

    void IAgentConfigHost.ApplyPet(Settings settings)
    {
        PersistPet(settings);
        ApplyPet();
        PostPet();
    }

    object IAgentConfigHost.GetTelegramState() => TelegramState();

    void IAgentConfigHost.SetTelegramEnabled(bool enabled)
    {
        var s = Settings.Load();
        s.Telegram.Enabled = enabled;
        s.Save();
        _telegramNote = enabled && TelegramVault.Load() is null
            ? UiText.Pick("먼저 봇 토큰을 넣으세요.", "Enter a bot token first.") : "";
        ApplyTelegram();
    }

    async Task<object> IAgentConfigHost.SetTelegramTokenAsync(string token)
    {
        if (!TelegramApi.LooksLikeToken(token))
            throw new ArgumentException("봇 토큰 모양이 아닙니다 — BotFather 가 준 123456:ABC… 전체를 넣으세요.");
        using var api = new TelegramApi(token);
        var me = await api.GetMeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        TelegramVault.Save(token);
        _telegramNote = UiText.Pick($"@{me.Username} 봇 토큰을 저장했습니다.", $"Saved the token for @{me.Username}.");
        Log(UiText.Pick($"텔레그램 봇 @{me.Username} 토큰을 저장했습니다(DPAPI 암호화).",
                        $"Saved the Telegram bot token for @{me.Username} (DPAPI encrypted)."), "i");
        ApplyTelegram();
        return TelegramState();
    }

    void IAgentConfigHost.ClearTelegramToken()
    {
        StopTelegram();
        TelegramVault.Clear();
        _telegramNote = UiText.Pick("토큰을 지웠습니다.", "Token removed.");
        ApplyTelegram();
    }

    object IAgentConfigHost.CreateTelegramPairCode()
    {
        if (_telegram is null)
            throw new InvalidOperationException("텔레그램 봇 토큰을 설정하고 [사용] 을 켠 뒤에 페어링 코드를 만들 수 있습니다.");
        _telegramNote = "";
        _telegram.NewPairingCode();
        return TelegramState();
    }

    void IAgentConfigHost.RevokeTelegramChat(long chatId)
    {
        var s = Settings.Load();
        var removed = s.Telegram.Chats.RemoveAll(c => c.Id == chatId);
        if (removed == 0)
            throw new ArgumentException($"허용된 대화 목록에 '{chatId}' 가 없습니다.");
        s.Save();
        PostTelegram();
    }

    object IAgentConfigHost.GetScheduleState() => ScheduleState();

    object IAgentConfigHost.SaveSchedule(JsonNode jobNode)
    {
        var s = Settings.Load();
        var job = ReadJob(jobNode, s);
        if (ValidateJob(job, s) is { } error)
            throw new ArgumentException(error);

        var old = s.Schedules.FirstOrDefault(j => j.Id == job.Id && job.Id.Length > 0);
        if (old is null)
        {
            job.Id = Guid.NewGuid().ToString("N")[..10];
            job.AnchorUtc = DateTime.UtcNow;
            s.Schedules.Add(job);
        }
        else
        {
            job.AnchorUtc = old.AnchorUtc;
            var probe = job.Clone();
            if (probe.ScheduleSignature() != old.ScheduleSignature() || old.AnchorUtc == default)
                job.AnchorUtc = DateTime.UtcNow;
            s.Schedules[s.Schedules.IndexOf(old)] = job;
        }
        s.Save();
        _scheduleNote = "";
        _scheduler?.Update(s.Schedules);
        Post(new { t = "sched.saved", ok = true, id = job.Id });
        PostSchedules();
        return new
        {
            saved = true,
            id = job.Id,
            schedule = ScheduleState(),
        };
    }

    object IAgentConfigHost.SetScheduleEnabled(string id, bool enabled)
    {
        var s = Settings.Load();
        var job = s.Schedules.FirstOrDefault(j => j.Id == id)
            ?? throw new ArgumentException($"예약 작업 '{id}' 를 찾지 못했습니다.");
        if (enabled && ValidateJob(job, s) is { } error)
            throw new ArgumentException(error);
        job.Enabled = enabled;
        if (enabled && job.Repeat == "interval") job.AnchorUtc = DateTime.UtcNow;
        s.Save();
        _scheduleNote = "";
        _scheduler?.Update(s.Schedules);
        PostSchedules();
        return ScheduleState();
    }

    object IAgentConfigHost.RunScheduleNow(string id)
    {
        var s = Settings.Load();
        var job = s.Schedules.FirstOrDefault(j => j.Id == id)
            ?? throw new ArgumentException($"예약 작업 '{id}' 를 찾지 못했습니다.");
        if (_scheduler is null)
            throw new InvalidOperationException("예약 실행기가 아직 준비되지 않았습니다.");
        if (ValidateJob(job, s) is { } error && !(job.Repeat == "once" && error == "이미 지난 시각입니다."))
            throw new ArgumentException(error);
        _scheduleNote = "";
        _scheduler.RunNow(id);
        PostSchedules();
        return new { triggered = true, id };
    }

    object IAgentConfigHost.RemoveSchedule(string id)
    {
        var s = Settings.Load();
        var job = s.Schedules.FirstOrDefault(j => j.Id == id)
            ?? throw new ArgumentException($"예약 작업 '{id}' 를 찾지 못했습니다.");
        s.Schedules.Remove(job);
        s.Save();
        _scheduler?.Update(s.Schedules);
        PostSchedules();
        return ScheduleState();
    }

    object IAgentConfigHost.ClearScheduleHistory()
    {
        _scheduleRuns = _scheduleRuns.Where(r => r.Status == "running").ToList();
        ScheduleHistory.Save(_scheduleRuns);
        PostSchedules();
        return ScheduleState();
    }

    void IAgentConfigHost.ApplyLiveUi(Settings settings, bool languageChanged, bool signInChanged, bool petChanged)
    {
        if (signInChanged) ApplySignInSettings(settings);
        if (petChanged || languageChanged)
        {
            ApplyPet();
            PostPet();
        }
        if (languageChanged)
            Post(new { t = "ui.language", language = settings.UiLanguage });
        UpdateTrayTooltip();
        PostConf();
    }

    void IAgentConfigHost.ShowWindow(bool openSettings)
    {
        ShowFromTray();
        if (openSettings)
            Post(new { t = "conf.open" });
    }

    void IAgentConfigHost.HideWindowToTray() => HideToTray();

    void IAgentConfigHost.ClearSignInData(bool everything) => ClearSignInData(everything);
}
