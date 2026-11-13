using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCLIHub;

internal static class AgentConfigChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        // ── 1. Tray & Settings defaults + normalization + isolated persistence ──
        var defaults = new Settings();
        check(defaults.StartInTray == "always" && defaults.CloseToTray,
            "settings default to StartInTray=always and CloseToTray=true");
        check(Settings.NormalizeStartInTray("STARTUP") == "startup" &&
              Settings.NormalizeStartInTray("never") == "never" &&
              Settings.NormalizeStartInTray("unexpected") == "always",
            "StartInTray normalizes supported modes and falls back to always");

        check(MainWindow.ShouldStartInTray(new Settings { StartInTray = "always" }, false) &&
              MainWindow.ShouldStartInTray(new Settings { StartInTray = "always" }, true) &&
              !MainWindow.ShouldStartInTray(new Settings { StartInTray = "startup" }, false) &&
              MainWindow.ShouldStartInTray(new Settings { StartInTray = "startup" }, true) &&
              !MainWindow.ShouldStartInTray(new Settings { StartInTray = "never" }, false) &&
              !MainWindow.ShouldStartInTray(new Settings { StartInTray = "never" }, true),
            "ShouldStartInTray honors always / startup / never across normal and --startup launches");

        var settingsFile = Path.Combine(root, "isolated-config", "settings.json");
        var roundTrip = new Settings { UiLanguage = "en", StartInTray = "startup", CloseToTray = false, CodexModel = "gpt-5.4" };
        roundTrip.SaveTo(settingsFile);
        var reloaded = Settings.LoadFrom(settingsFile);
        check(reloaded.UiLanguage == "en" && reloaded.StartInTray == "startup" && !reloaded.CloseToTray && reloaded.CodexModel == "gpt-5.4",
            "Settings.SaveTo and LoadFrom round-trip isolated settings without touching %LOCALAPPDATA%");

        // ── 2. Agent instructions across Claude, Codex, and Antigravity (agy) ──
        var brief = AgentBrief.Text();
        check(brief.Contains("settings get", StringComparison.Ordinal) &&
              brief.Contains("settings set StartInTray", StringComparison.Ordinal) &&
              brief.Contains("settings patch", StringComparison.Ordinal) &&
              brief.Contains("settings schedule", StringComparison.Ordinal),
            "AgentBrief.Text documents settings get/set/patch/schedule for Claude and Codex");

        var codexToml = AgentBrief.MergeCodexConfig("[projects.\"D:\\\\Src\"]\ntrust_level = \"trusted\"\n", brief);
        check(codexToml.Contains("settings patch", StringComparison.Ordinal) &&
              codexToml.Contains("[projects.\"D:\\\\Src\"]", StringComparison.Ordinal),
            "Codex profile merges updated settings instructions while preserving user project trust");

        var agyProfile = Path.Combine(root, "agy-profile");
        AgentBrief.WriteAntigravitySkillFile(agyProfile, brief);
        var skillPath = AgentBrief.AntigravitySkillIn(agyProfile);
        var skillText = File.ReadAllText(skillPath);
        var firstWriteUtc = File.GetLastWriteTimeUtc(skillPath);
        AgentBrief.WriteAntigravitySkillFile(agyProfile, brief);
        check(File.Exists(skillPath) &&
              skillText.StartsWith("---\nname: agentclihub\n", StringComparison.Ordinal) &&
              skillText.Contains("settings get", StringComparison.Ordinal) &&
              File.GetLastWriteTimeUtc(skillPath) == firstWriteUtc,
            "Antigravity global skill SKILL.md is written with valid frontmatter and idempotent updates");

        // ── 3. End-to-end BrowserBridge + agentclihub-browser.exe + AgentConfigService ──
        var helperExe = File.Exists(BrowserBridge.HelperPath)
            ? BrowserBridge.HelperPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AgentCLIHub", "bin", "Release", "net9.0-windows", "agent", "agentclihub-browser.exe"));
        check(File.Exists(helperExe), "agentclihub-browser.exe exists in Release output folder: " + helperExe);

        var regRoot = @"Software\AgentCLIHub-AgentConfigCheck-" + Guid.NewGuid().ToString("N");
        var stubExe = Path.Combine(root, "tray-host", "AgentCLIHub.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(stubExe)!);
        File.WriteAllText(stubExe, "stub");

        var host = new FakeAgentConfigHost(
            Path.Combine(root, "bridge-settings", "settings.json"),
            stubExe,
            regRoot + @"\Run",
            regRoot + @"\StartupApproved\Run");
        var service = new AgentConfigService(host);

        using var bridge = new BrowserBridge(
            (cmd, args) => cmd is "settings" or "config"
                ? service.ExecuteAsync(args)
                : throw new ArgumentException("unexpected command: " + cmd),
            (_, _) => { });
        bridge.Start();

        async Task<(int Exit, string Stdout, string Stderr)> InvokeCliAsync(params string[] cliArgs)
        {
            var psi = new ProcessStartInfo(helperExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in cliArgs) psi.ArgumentList.Add(a);
            psi.Environment["AGENTCLIHUB_BROWSER_PIPE"] = bridge.PipeName;
            psi.Environment[BrowserBridge.TicketEnv] = bridge.Ticket;

            using var proc = Process.Start(psi)!;
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            return (proc.ExitCode, await outTask, await errTask);
        }

        try
        {
            var getAll = await InvokeCliAsync("settings", "get");
            check(getAll.Exit == 0, "agentclihub-browser settings get succeeds over named pipe: " + getAll.Stderr);
            check(!getAll.Stdout.Contains("SECRET_RAW_BOT_TOKEN", StringComparison.Ordinal) &&
                  !getAll.Stdout.Contains("sk-ant-oat01-SECRET", StringComparison.Ordinal),
                "settings get never leaks raw Telegram or OAuth tokens");
            var allJson = JsonNode.Parse(getAll.Stdout)!;
            check(allJson["settings"]?["StartInTray"]?.GetValue<string>() == "always" &&
                  allJson["settings"]?["CloseToTray"]?.GetValue<bool>() == true &&
                  allJson["telegram"]?["hasToken"]?.GetValue<bool>() == true,
                "settings get returns tray defaults and masked token presence");

            var setTray = await InvokeCliAsync("settings", "set", "StartInTray", "startup");
            var setClose = await InvokeCliAsync("settings", "set", "CloseToTray", "false");
            var setStartup = await InvokeCliAsync("settings", "set", "RunAtStartup", "true");
            check(setTray.Exit == 0 && setClose.Exit == 0 && setStartup.Exit == 0 &&
                  host.LoadSettings().StartInTray == "startup" &&
                  !host.LoadSettings().CloseToTray &&
                  host.IsStartupEnabled() &&
                  host.LiveUiUpdates >= 3,
                "settings set persists StartInTray, CloseToTray, and RunAtStartup with live UI notifications");

            var badEnum = await InvokeCliAsync("settings", "set", "StartInTray", "invalid-mode");
            check(badEnum.Exit == 1 && badEnum.Stderr.Contains("always, startup, never", StringComparison.Ordinal) &&
                  host.LoadSettings().StartInTray == "startup",
                "invalid StartInTray value is rejected with allowed options and leaves saved settings intact");

            var patchFile = Path.Combine(root, "patch.json");
            var firstCodexModel = CliCatalog.Models(CliProfile.Codex.Backend).First();
            var firstCodexEffort = CliCatalog.Efforts(CliProfile.Codex.Backend, firstCodexModel).First();
            File.WriteAllText(patchFile, JsonSerializer.Serialize(new
            {
                UiLanguage = "en",
                StartInTray = "always",
                CloseToTray = true,
                CodexModel = firstCodexModel,
                CodexEffort = firstCodexEffort,
                Pet = new { Size = 115, Location = "desktop" },
            }), new UTF8Encoding(false));

            var patched = await InvokeCliAsync("config", "patch", "--file", patchFile);
            var afterPatch = host.LoadSettings();
            check(patched.Exit == 0 &&
                  afterPatch.UiLanguage == "en" &&
                  afterPatch.StartInTray == "always" &&
                  afterPatch.CloseToTray &&
                  afterPatch.CodexModel == firstCodexModel &&
                  afterPatch.CodexEffort == firstCodexEffort &&
                  afterPatch.Pet?.Size == 115 &&
                  afterPatch.Pet?.Desktop == true,
                "config patch --file atomically updates language, tray, CLI model/effort, and pet options: " + patched.Stderr);

            var bmAdd = await InvokeCliAsync("settings", "bookmark", "add", "사내 포털", "https://portal.example.com", "true");
            var bmMove = await InvokeCliAsync("settings", "bookmark", "move", "2", "up");
            check(bmAdd.Exit == 0 && bmMove.Exit == 0 &&
                  host.LoadSettings().Bookmarks[1].Name == "사내 포털",
                "settings bookmark add and move persist updated shortcut order");
            var bmDel = await InvokeCliAsync("settings", "bookmark", "remove", "1");
            check(bmDel.Exit == 0 && host.LoadSettings().Bookmarks.All(b => b.Name != "사내 포털"),
                "settings bookmark remove deletes shortcut");

            var accRename = await InvokeCliAsync("settings", "account", "rename", "default:codex_cli", "회사 Codex");
            check(accRename.Exit == 0 && host.LoadSettings().DefaultAccountNames["codex_cli"] == "회사 Codex",
                "settings account rename updates default account display name");

            var schedDir = Path.Combine(root, "sched-work");
            Directory.CreateDirectory(schedDir);
            var schedFile = Path.Combine(root, "job.json");
            File.WriteAllText(schedFile, JsonSerializer.Serialize(new
            {
                Name = "매분 점검",
                Enabled = true,
                Prompt = "상태 요약",
                Target = "new",
                Folder = schedDir,
                Backend = "codex_cli",
                Model = firstCodexModel,
                Effort = firstCodexEffort,
                CloseTab = true,
                Repeat = "interval",
                Every = 1,
                Unit = "m",
            }), new UTF8Encoding(false));

            var schedSave = await InvokeCliAsync("settings", "schedule", "save", "--file", schedFile);
            check(schedSave.Exit == 0, "settings schedule save --file creates 1-minute scheduled task: " + schedSave.Stderr);
            var jobId = JsonNode.Parse(schedSave.Stdout)!["id"]!.GetValue<string>();
            var schedOff = await InvokeCliAsync("settings", "schedule", "enable", jobId, "off");
            check(schedOff.Exit == 0 && host.LoadSettings().Schedules.Single(j => j.Id == jobId).Enabled == false,
                "settings schedule enable toggles scheduled task off");
            var schedDel = await InvokeCliAsync("settings", "schedule", "remove", jobId);
            check(schedDel.Exit == 0 && host.LoadSettings().Schedules.Count == 0,
                "settings schedule remove deletes scheduled task");

            var winHide = await InvokeCliAsync("settings", "window", "hide");
            var winOpen = await InvokeCliAsync("settings", "open");
            check(winHide.Exit == 0 && winOpen.Exit == 0 && host.IsWindowShown && host.OpenedSettingsCount == 1,
                "settings window hide and settings open control window/tray visibility and [⚙ Settings] view");

            var clearDenied = await InvokeCliAsync("settings", "signin", "clear-passwords");
            var clearConfirmed = await InvokeCliAsync("settings", "signin", "clear-passwords", "--confirm");
            check(clearDenied.Exit == 1 && clearConfirmed.Exit == 0 && host.ClearedSignInPasswords == 1,
                "settings signin clear-passwords requires explicit --confirm flag");
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(regRoot, throwOnMissingSubKey: false);
        }
    }

    private sealed class FakeAgentConfigHost : IAgentConfigHost
    {
        private readonly string _filePath;
        private readonly string _exePath;
        private readonly string _runSubKey;
        private readonly string _approvedSubKey;
        private string _rawBotToken = "123456:SECRET_RAW_BOT_TOKEN";

        public FakeAgentConfigHost(string filePath, string exePath, string runSubKey, string approvedSubKey)
        {
            _filePath = filePath;
            _exePath = exePath;
            _runSubKey = runSubKey;
            _approvedSubKey = approvedSubKey;
            var initial = new Settings();
            initial.SaveTo(_filePath);
        }

        public string SettingsFilePath => _filePath;
        public string AppVersion => "1.9.8-test";
        public bool IsWindowShown { get; private set; } = true;
        public int LiveUiUpdates { get; private set; }
        public int OpenedSettingsCount { get; private set; }
        public int ClearedSignInPasswords { get; private set; }

        public Settings LoadSettings() => Settings.LoadFrom(_filePath);
        public void SaveSettings(Settings settings) => settings.SaveTo(_filePath);
        public bool IsStartupEnabled() => StartupRegistration.IsEnabled(_runSubKey, _approvedSubKey);
        public void SetStartupEnabled(bool enabled) => StartupRegistration.SetEnabled(enabled, _exePath, _runSubKey, _approvedSubKey);

        public IReadOnlyList<AntigravityModels.Model> AntigravityModels =>
            [new AntigravityModels.Model("gemini-2.5-pro", "Gemini 2.5 Pro")];

        public Task<(IReadOnlyList<AntigravityModels.Model> Models, string? Error)> RefreshAntigravityModelsAsync(string path) =>
            Task.FromResult<(IReadOnlyList<AntigravityModels.Model>, string?)>((AntigravityModels, null));

        public object GetAccountViews(Settings settings) => new object[]
        {
            new { backend = "claude_code", id = "", name = settings.DefaultAccountNames.GetValueOrDefault("claude_code", "기본"), loggedIn = true },
            new { backend = "claude_code", id = "acc1", name = "보조", loggedIn = true, tokenOnly = true, maskedNote = "sk-ant-oat01-*** (never raw)" },
            new { backend = "codex_cli", id = "", name = settings.DefaultAccountNames.GetValueOrDefault("codex_cli", "기본"), loggedIn = true },
        };

        public bool RenameAccount(Settings settings, string backend, string accountId, string newName, out string error)
        {
            var cli = CliProfile.For(backend);
            if (cli is null) { error = "알 수 없는 CLI 입니다."; return false; }
            var trimmed = newName.Trim();
            if (trimmed.Length == 0) { error = "이름이 비어 있습니다."; return false; }
            if (string.IsNullOrEmpty(accountId))
            {
                settings.DefaultAccountNames[cli.Backend] = trimmed;
                SaveSettings(settings);
                error = "";
                return true;
            }
            error = "테스트 계정 없음";
            return false;
        }

        public object AddAccountInteractive(string backend, string name, string login) => new { added = "test", backend, name };
        public void OpenAccountLogin(string accountId) => IsWindowShown = true;
        public void RemoveAccount(string accountId) { }

        public object GetPetStatus(Settings settings) => new
        {
            enabled = settings.Pet?.Enabled ?? false,
            desktop = settings.Pet?.Desktop ?? false,
            size = settings.Pet?.Size ?? 100,
        };

        public void ApplyPet(Settings settings) => SaveSettings(settings);

        public object GetTelegramState()
        {
            var s = LoadSettings();
            return new
            {
                enabled = s.Telegram.Enabled,
                hasToken = _rawBotToken.Length > 0,
                running = s.Telegram.Enabled && _rawBotToken.Length > 0,
                bot = _rawBotToken.Length > 0 ? "test_bot" : "",
                chats = s.Telegram.Chats,
            };
        }

        public void SetTelegramEnabled(bool enabled)
        {
            var s = LoadSettings();
            s.Telegram.Enabled = enabled;
            SaveSettings(s);
        }

        public Task<object> SetTelegramTokenAsync(string token)
        {
            _rawBotToken = token;
            return Task.FromResult(GetTelegramState());
        }

        public void ClearTelegramToken() => _rawBotToken = "";
        public object CreateTelegramPairCode() => new { pairCode = "123456" };
        public void RevokeTelegramChat(long chatId) { }

        public object GetScheduleState()
        {
            var s = LoadSettings();
            return new { jobs = s.Schedules, history = Array.Empty<object>() };
        }

        public object SaveSchedule(JsonNode jobNode)
        {
            var s = LoadSettings();
            var job = MainWindow.ReadJob(jobNode, s);
            if (MainWindow.ValidateJob(job, s) is { } err)
                throw new ArgumentException(err);
            if (string.IsNullOrEmpty(job.Id))
                job.Id = Guid.NewGuid().ToString("N")[..10];
            job.AnchorUtc = DateTime.UtcNow;
            s.Schedules.RemoveAll(j => j.Id == job.Id);
            s.Schedules.Add(job);
            SaveSettings(s);
            return new { saved = true, id = job.Id, schedule = GetScheduleState() };
        }

        public object SetScheduleEnabled(string id, bool enabled)
        {
            var s = LoadSettings();
            var job = s.Schedules.FirstOrDefault(j => j.Id == id)
                ?? throw new ArgumentException("not found");
            job.Enabled = enabled;
            SaveSettings(s);
            return GetScheduleState();
        }

        public object RunScheduleNow(string id) => new { triggered = true, id };

        public object RemoveSchedule(string id)
        {
            var s = LoadSettings();
            if (s.Schedules.RemoveAll(j => j.Id == id) == 0)
                throw new ArgumentException("not found");
            SaveSettings(s);
            return GetScheduleState();
        }

        public object ClearScheduleHistory() => GetScheduleState();

        public void ApplyLiveUi(Settings settings, bool languageChanged, bool signInChanged, bool petChanged) =>
            LiveUiUpdates++;

        public void ShowWindow(bool openSettings)
        {
            IsWindowShown = true;
            if (openSettings) OpenedSettingsCount++;
        }

        public void HideWindowToTray() => IsWindowShown = false;

        public void ClearSignInData(bool everything)
        {
            if (!everything) ClearedSignInPasswords++;
        }
    }
}
