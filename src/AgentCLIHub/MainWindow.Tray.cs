using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace AgentCLIHub;

/// <summary>
/// 작업 표시줄 알림 영역(시계 옆 시스템 트레이) 아이콘과 우클릭 메뉴, 트레이로 시작·닫기 동작을 맡는다.
///
/// <b>왜 처음에 투명한 채로 한 번 <c>Show()</c> 를 거치나.</b>
/// WPF 창은 <c>Show()</c> 가 한 번도 불리지 않으면 HWND 가 안 생기고 <c>Loaded</c> 가 오지 않는다 —
/// 그러면 WebView2(<c>index.html</c>)가 뜨지 않아 탭 복원·예약 작업(<see cref="ScheduleRunner"/>)·
/// 텔레그램 원격(<see cref="TelegramRemote"/>)·에이전트 창구(<see cref="BrowserBridge"/>)가 트레이에서
/// 아예 돌지 않는다. 그래서 트레이로 시작할 때는 작업 표시줄과 Alt+Tab 에서 가린 투명 상태로 먼저 띄워
/// 초기화를 마친 뒤 곧바로 <c>Hide()</c> 로 숨긴다.
/// </summary>
public partial class MainWindow
{
    private readonly bool _isStartupLaunch;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ContextMenuStrip? _trayMenu;
    private Drawing.Icon? _trayIconHandle;
    private Drawing.Font? _trayBoldFont;
    private DispatcherTimer? _initialHideFallback;
    private WindowState _lastNonMinimizedState = WindowState.Normal;
    private bool _startingHiddenInTray;
    private bool _toolWindowApplied;
    private bool _exitRequested;

    public bool IsExitRequested => _exitRequested;

    public static bool ShouldStartInTray(Settings settings, bool isStartupLaunch) =>
        Settings.NormalizeStartInTray(settings.StartInTray) switch
        {
            "always" => true,
            "startup" => isStartupLaunch,
            _ => false,
        };

    public void ShowInitial()
    {
        EnsureTrayIcon();

        StateChanged += (_, _) =>
        {
            if (WindowState is WindowState.Normal or WindowState.Maximized)
                _lastNonMinimizedState = WindowState;
            UpdateTrayTooltip();
        };
        IsVisibleChanged += (_, _) => UpdateTrayTooltip();
        SourceInitialized += OnTraySourceInitialized;
        Closed += (_, _) => DisposeTrayIcon();

        var cfg = Settings.Load();
        if (!ShouldStartInTray(cfg, _isStartupLaunch))
        {
            Show();
            UpdateTrayTooltip();
            return;
        }

        _startingHiddenInTray = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Opacity = 0;

        _initialHideFallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _initialHideFallback.Tick += (_, _) => CompleteInitialTrayStart();
        _initialHideFallback.Start();

        Show();
        UpdateTrayTooltip();
    }

    private void OnTraySourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource src)
            src.AddHook(TrayWndProc);

        if (_startingHiddenInTray)
            SetToolWindowExtendedStyle(enable: true);
    }

    private IntPtr TrayWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_QUERYENDSESSION = 0x0011;
        const int WM_ENDSESSION = 0x0016;
        if (msg is WM_QUERYENDSESSION or WM_ENDSESSION)
            _exitRequested = true;
        return IntPtr.Zero;
    }

    private void CompleteInitialTrayStart()
    {
        _initialHideFallback?.Stop();
        _initialHideFallback = null;
        if (!_startingHiddenInTray) return;

        _startingHiddenInTray = false;
        Hide();
        SetToolWindowExtendedStyle(enable: false);
        Opacity = 1;
        ShowActivated = true;
        UpdateTrayTooltip();
    }

    public bool IsShownToUser => IsVisible && !_startingHiddenInTray && WindowState != WindowState.Minimized;

    public void HideToTray()
    {
        if (_closingStarted) return;
        EnsureTrayIcon();
        _startingHiddenInTray = false;
        _initialHideFallback?.Stop();
        _initialHideFallback = null;

        if (WindowState is WindowState.Normal or WindowState.Maximized)
            _lastNonMinimizedState = WindowState;

        SetToolWindowExtendedStyle(enable: false);
        Opacity = 1;
        ShowActivated = true;
        ShowInTaskbar = false;
        Hide();
        UpdateTrayTooltip();
    }

    public void ShowFromTray()
    {
        if (_closingStarted) return;
        _startingHiddenInTray = false;
        _initialHideFallback?.Stop();
        _initialHideFallback = null;

        SetToolWindowExtendedStyle(enable: false);
        Opacity = 1;
        ShowActivated = true;
        ShowInTaskbar = true;

        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _lastNonMinimizedState is WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;

        Activate();
        BringWindowToFront();
        UpdateTrayTooltip();
        Post(new { t = "win.shown" });
    }

    public void RequestExit()
    {
        _exitRequested = true;
    }

    public void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    private void EnsureTrayIcon()
    {
        if (_tray is not null) return;

        _trayIconHandle = LoadTrayIcon();
        _trayMenu = new WinForms.ContextMenuStrip
        {
            Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkTrayColorTable()),
            ShowImageMargin = true,
            ShowCheckMargin = true,
            BackColor = Drawing.Color.FromArgb(27, 27, 27),
            ForeColor = Drawing.Color.FromArgb(222, 226, 232),
        };
        _trayMenu.Opening += (_, ev) =>
        {
            ev.Cancel = false;
            PopulateTrayMenu(_trayMenu);
        };

        _tray = new WinForms.NotifyIcon
        {
            Icon = _trayIconHandle,
            ContextMenuStrip = _trayMenu,
            Visible = true,
        };
        UpdateTrayTooltip();

        _tray.MouseClick += (_, ev) =>
        {
            if (ev.Button == WinForms.MouseButtons.Left)
                Dispatcher.BeginInvoke(ShowFromTray);
        };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
    }

    private void DisposeTrayIcon()
    {
        try { _initialHideFallback?.Stop(); } catch { }
        _initialHideFallback = null;

        if (_tray is not null)
        {
            try { _tray.Visible = false; } catch { }
            try { _tray.Dispose(); } catch { }
            _tray = null;
        }
        if (_trayMenu is not null)
        {
            try { _trayMenu.Dispose(); } catch { }
            _trayMenu = null;
        }
        if (_trayBoldFont is not null)
        {
            try { _trayBoldFont.Dispose(); } catch { }
            _trayBoldFont = null;
        }
        if (_trayIconHandle is not null)
        {
            try { _trayIconHandle.Dispose(); } catch { }
            _trayIconHandle = null;
        }
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (res?.Stream is { } stream)
            {
                using (stream)
                    return new Drawing.Icon(stream);
            }
        }
        catch { }

        try
        {
            if (Environment.ProcessPath is { Length: > 0 } exe && File.Exists(exe) &&
                Drawing.Icon.ExtractAssociatedIcon(exe) is { } extracted)
                return extracted;
        }
        catch { }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    private void UpdateTrayTooltip()
    {
        if (_tray is null) return;
        var ver = AppVersion.Length > 0 ? " " + AppVersion : "";
        var cliCount = _tabs.Values.Count(h => !h.IsLogin);
        var webCount = _browsers.Count;
        var total = cliCount + webCount;
        var state = IsShownToUser
            ? UiText.Pick("창 열림", "Window open")
            : UiText.Pick("트레이 실행 중", "Running in tray");
        var tabsPart = total > 0
            ? UiText.Pick($" · 탭 {total}개", $" · {total} tab(s)")
            : "";
        var text = $"AgentCLIHub{ver} · {state}{tabsPart}";
        // NotifyIcon.Text 는 Win32 제한으로 63자까지만 허용된다.
        if (text.Length > 63) text = text[..63];
        try { _tray.Text = text; } catch { }
    }

    private void PopulateTrayMenu(WinForms.ContextMenuStrip menu)
    {
        menu.Items.Clear();
        var cfg = Settings.Load();

        WinForms.ToolStripMenuItem AddItem(string text, Action onClick, bool enabled = true, bool bold = false, bool? check = null)
        {
            var item = new WinForms.ToolStripMenuItem(text)
            {
                Enabled = enabled,
                ForeColor = enabled ? Drawing.Color.FromArgb(222, 226, 232) : Drawing.Color.FromArgb(115, 122, 132),
            };
            if (bold)
            {
                _trayBoldFont ??= new Drawing.Font(menu.Font, Drawing.FontStyle.Bold);
                item.Font = _trayBoldFont;
            }
            if (check is { } isChecked)
                item.Checked = isChecked;
            item.Click += (_, _) => Dispatcher.BeginInvoke(onClick);
            menu.Items.Add(item);
            return item;
        }

        AddItem(
            IsShownToUser
                ? UiText.Pick("창 앞으로 가져오기", "Bring window to front")
                : UiText.Pick("창 열기", "Open window"),
            ShowFromTray,
            bold: true);

        AddItem(
            UiText.Pick("트레이로 숨기기", "Hide to tray"),
            HideToTray,
            enabled: IsShownToUser);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        AddItem(
            UiText.Pick("＋ 새 대화…", "＋ New chat…"),
            () =>
            {
                ShowFromTray();
                Post(new { t = "ui.newchat" });
            });

        AddItem(
            UiText.Pick("↻ 지난 대화 이어하기…", "↻ Resume conversation…"),
            () =>
            {
                ShowFromTray();
                Post(new { t = "hist.open" });
                PostHistory();
            });

        // 열린 탭 바로 전환 서브메뉴
        var tabIds = _order.Where(id => _tabs.ContainsKey(id) || _browsers.ContainsKey(id)).ToList();
        var tabsMenu = new WinForms.ToolStripMenuItem(
            UiText.Pick($"열린 탭 ({tabIds.Count})", $"Open tabs ({tabIds.Count})"))
        {
            Enabled = tabIds.Count > 0,
            ForeColor = tabIds.Count > 0 ? Drawing.Color.FromArgb(222, 226, 232) : Drawing.Color.FromArgb(115, 122, 132),
        };
        foreach (var id in tabIds)
        {
            var tabId = id;
            string label;
            if (_tabs.TryGetValue(tabId, out var h))
            {
                var name = Alias(tabId) is { Length: > 0 } al ? al : TabName(h.Cwd);
                label = $"[{h.Cli.Display}] {name}";
            }
            else if (_browsers.TryGetValue(tabId, out var b))
            {
                var name = Alias(tabId) is { Length: > 0 } al ? al : (b.Title.Length > 0 ? b.Title : b.Url);
                label = $"[WEB] {name}";
            }
            else continue;

            if (label.Length > 56) label = label[..55] + "…";
            var sub = new WinForms.ToolStripMenuItem(label)
            {
                ForeColor = Drawing.Color.FromArgb(222, 226, 232),
            };
            sub.Click += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                ShowFromTray();
                Post(new { t = "tab.show", id = tabId });
            });
            tabsMenu.DropDownItems.Add(sub);
        }
        menu.Items.Add(tabsMenu);

        // 브라우저 바로가기 서브메뉴
        var bookmarks = cfg.Bookmarks.Where(b => b.Show && !string.IsNullOrWhiteSpace(b.Url)).ToList();
        if (bookmarks.Count > 0)
        {
            var bmMenu = new WinForms.ToolStripMenuItem(
                UiText.Pick("브라우저 바로가기", "Browser shortcuts"))
            {
                ForeColor = Drawing.Color.FromArgb(222, 226, 232),
            };
            foreach (var bm in bookmarks)
            {
                var url = bm.Url;
                var name = string.IsNullOrWhiteSpace(bm.Name) ? BrowserHost.HostOf(url) : bm.Name;
                var sub = new WinForms.ToolStripMenuItem(name)
                {
                    ForeColor = Drawing.Color.FromArgb(222, 226, 232),
                };
                sub.Click += (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    ShowFromTray();
                    OpenInNewTabSafe(url);
                });
                bmMenu.DropDownItems.Add(sub);
            }
            menu.Items.Add(bmMenu);
        }

        AddItem(
            UiText.Pick("⚙ 설정 열기", "⚙ Open settings"),
            () =>
            {
                ShowFromTray();
                Post(new { t = "conf.open" });
            });

        menu.Items.Add(new WinForms.ToolStripSeparator());

        // 빠른 토글
        var petStored = PetSheet.StoredPath(cfg.Pet ?? new()) is not null;
        AddItem(
            UiText.Pick("펫 표시", "Show pet"),
            () =>
            {
                var c = Settings.Load();
                c.Pet ??= new();
                c.Pet.Enabled = !c.Pet.Enabled;
                PersistPet(c);
                ApplyPet();
                PostPet();
            },
            enabled: petStored,
            check: petStored && cfg.Pet?.Enabled == true);

        var hasTgToken = TelegramVault.Load() is not null;
        AddItem(
            UiText.Pick("텔레그램 원격", "Telegram remote"),
            () =>
            {
                var c = Settings.Load();
                c.Telegram.Enabled = !c.Telegram.Enabled;
                c.Save();
                ApplyTelegram();
            },
            enabled: hasTgToken,
            check: hasTgToken && cfg.Telegram.Enabled);

        AddItem(
            UiText.Pick("닫기(×) 시 트레이로 숨기기", "Hide to tray on close (×)"),
            () =>
            {
                var c = Settings.Load();
                c.CloseToTray = !c.CloseToTray;
                c.Save();
                Post(new { t = "conf.tray.state", startInTray = c.StartInTray, closeToTray = c.CloseToTray });
            },
            check: cfg.CloseToTray);

        var startupOn = StartupRegistration.IsEnabled();
        AddItem(
            UiText.Pick("Windows 시작 시 자동 실행", "Launch at Windows startup"),
            () => SetStartupRegistration(!StartupRegistration.IsEnabled()),
            check: startupOn);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        AddItem(
            UiText.Pick("종료", "Exit"),
            ExitApplication);
    }

    private void SetToolWindowExtendedStyle(bool enable)
    {
        if (enable == _toolWindowApplied) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        var next = enable ? (style | WS_EX_TOOLWINDOW) : (style & ~WS_EX_TOOLWINDOW);
        SetWindowLong(hwnd, GWL_EXSTYLE, next);
        _toolWindowApplied = enable;
    }

    private void BringWindowToFront()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                SetForegroundWindow(hwnd);
            if (!Topmost)
            {
                Topmost = true;
                Topmost = false;
            }
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private sealed class DarkTrayColorTable : WinForms.ProfessionalColorTable
    {
        public DarkTrayColorTable() => UseSystemColors = false;
        public override Drawing.Color ToolStripDropDownBackground => Drawing.Color.FromArgb(27, 27, 27);
        public override Drawing.Color ImageMarginGradientBegin => Drawing.Color.FromArgb(27, 27, 27);
        public override Drawing.Color ImageMarginGradientMiddle => Drawing.Color.FromArgb(27, 27, 27);
        public override Drawing.Color ImageMarginGradientEnd => Drawing.Color.FromArgb(27, 27, 27);
        public override Drawing.Color MenuBorder => Drawing.Color.FromArgb(56, 56, 56);
        public override Drawing.Color MenuItemBorder => Drawing.Color.FromArgb(61, 97, 82);
        public override Drawing.Color MenuItemSelected => Drawing.Color.FromArgb(26, 42, 34);
        public override Drawing.Color MenuItemSelectedGradientBegin => Drawing.Color.FromArgb(26, 42, 34);
        public override Drawing.Color MenuItemSelectedGradientEnd => Drawing.Color.FromArgb(26, 42, 34);
        public override Drawing.Color MenuItemPressedGradientBegin => Drawing.Color.FromArgb(34, 52, 43);
        public override Drawing.Color MenuItemPressedGradientEnd => Drawing.Color.FromArgb(34, 52, 43);
        public override Drawing.Color SeparatorDark => Drawing.Color.FromArgb(46, 46, 46);
        public override Drawing.Color SeparatorLight => Drawing.Color.FromArgb(27, 27, 27);
        public override Drawing.Color CheckBackground => Drawing.Color.FromArgb(26, 42, 34);
        public override Drawing.Color CheckSelectedBackground => Drawing.Color.FromArgb(36, 58, 47);
        public override Drawing.Color CheckPressedBackground => Drawing.Color.FromArgb(36, 58, 47);
    }
}
