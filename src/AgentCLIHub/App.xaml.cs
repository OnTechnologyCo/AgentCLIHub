using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace AgentCLIHub;

public partial class App : Application
{
    /// <summary>
    /// "지금 떠 있다" 는 표식. 설치 프로그램(<c>installer\AgentCLIHub.iss</c> 의 <c>CheckForMutexes</c>)이
    /// 이 이름을 보고, 이미 트레이에 숨어 돌고 있을 때 바로가기를 다시 누르면 새 프로세스를 겹쳐 띄우는 대신
    /// 떠 있는 창을 앞으로 불러낸다(<see cref="ShowWindowEventName"/>).
    /// </summary>
    private static Mutex? _running;
    private static EventWaitHandle? _showWindowEvent;
    private static CancellationTokenSource? _ipcCts;

    private const string ShowWindowEventName = @"Local\AgentCLIHub.ShowWindow";

    protected override void OnStartup(StartupEventArgs e)
    {
        var isStartupLaunch = e.Args.Any(a => string.Equals(a, StartupRegistration.StartupArg, StringComparison.OrdinalIgnoreCase));
        var allowMulti = e.Args.Any(a => string.Equals(a, "--multi", StringComparison.OrdinalIgnoreCase));

        // Windows 시작프로그램(HKCU\...\Run)으로 뜰 때는 작업 폴더가 System32 로 잡힐 수 있다.
        try
        {
            var sys = Path.TrimEndingDirectorySeparator(Environment.SystemDirectory);
            var cur = Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory);
            if (string.Equals(cur, sys, StringComparison.OrdinalIgnoreCase))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (Directory.Exists(home)) Environment.CurrentDirectory = home;
            }
        }
        catch { }

        var createdNew = true;
        try { _running = new Mutex(true, "AgentCLIHub.Running", out createdNew); } catch { }

        if (!createdNew && !allowMulti)
        {
            // 이미 트레이(또는 창)에서 돌고 있다 — 두 벌이 동시에 뜨면 텔레그램 봇 폴링·예약 작업·settings.json 이 충돌한다.
            // 사용자가 직접 실행한 경우에만 기존 창을 앞으로 깨운다.
            if (!isStartupLaunch)
            {
                try
                {
                    using var ev = EventWaitHandle.OpenExisting(ShowWindowEventName);
                    ev.Set();
                }
                catch { }
            }
            try { _running?.Dispose(); _running = null; } catch { }
            Shutdown(0);
            return;
        }

        try
        {
            UpgradeMigration.RunForCurrentUser("migrate", requireClosed: false);
            UpgradeMigration.RunForCurrentUser("commit", requireClosed: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show("기존 설정을 이전하지 못했습니다. 원본 데이터는 보존됩니다.\n\n" + ex.Message,
                "AgentCLIHub — 데이터 이전", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        try { StartupRegistration.SyncExecutableIfEnabled(); } catch { }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        SessionEnding += (_, _) => (MainWindow as MainWindow)?.RequestExit();
        Exit += (_, _) =>
        {
            try { _ipcCts?.Cancel(); _ipcCts?.Dispose(); } catch { }
            try { _showWindowEvent?.Dispose(); } catch { }
            try { if (createdNew) _running?.ReleaseMutex(); } catch { }
            try { _running?.Dispose(); } catch { }
        };

        StartShowWindowListener();

        base.OnStartup(e);
        var window = new MainWindow(isStartupLaunch);
        MainWindow = window;
        window.ShowInitial();
    }

    private void StartShowWindowListener()
    {
        try
        {
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            _ipcCts = new CancellationTokenSource();
            var token = _ipcCts.Token;
            var handle = _showWindowEvent;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var handles = new WaitHandle[] { handle, token.WaitHandle };
                while (!token.IsCancellationRequested)
                {
                    int idx;
                    try { idx = WaitHandle.WaitAny(handles); }
                    catch { break; }
                    if (idx != 0 || token.IsCancellationRequested) break;
                    Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray());
                }
            });
        }
        catch { }
    }
    public App()
    {
        // 소유하지 않는다(createdNew 를 안 본다). 있는지만 보이면 되는 표식이다.


        // **어디서 터져도 사람이 읽을 수 있게 한다.** WPF 의 기본 동작은 미처리 예외에
        // 창을 통째로 닫는 것이고, 그러면 사용자에게는 "눌렀더니 앱이 사라졌다" 로만 남는다.
        // 여기서 잡아 이유를 보여 준다 — 터미널 하나 못 띄운 것으로 앱이 죽으면 안 된다.
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.ToString(), UiText.Pick("AgentCLIHub — 처리하지 못한 오류", "AgentCLIHub — Unhandled error"),
                            MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}
