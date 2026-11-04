using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;

namespace AgentCLIHub;

// An owned window keeps the sprite above native browser tabs without making it globally topmost.
public sealed class PetWindow : Window
{
    private readonly Window _owner;
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly BitmapSource[][] _frames;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Action<double, double> _savePosition;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private PetSettings _settings;
    private string _activity = "idle", _gesture = "idle", _drawnState = "";
    private long _activityUntil, _gestureUntil, _frameStart;
    private int _lastRow = -1, _lastFrame = -1;
    private Point? _dragStart;
    private Point _dragOrigin;
    private bool _moved;

    public PetWindow(Window owner, BitmapSource sheet, PetSettings settings, Action<double, double> savePosition)
    {
        _owner = owner; Owner = owner; _settings = settings; _savePosition = savePosition;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = false;
        Content = _image; Cursor = Cursors.Hand;
        _frames = Enumerable.Range(0, PetSheet.Counts.Count)
            .Select(row => Enumerable.Range(0, PetSheet.Counts[row]).Select(col => PetSheet.Frame(sheet, row, col)).ToArray()).ToArray();
        _owner.LocationChanged += OwnerMoved;
        _owner.SizeChanged += OwnerResized;
        _owner.StateChanged += OwnerMoved;
        _owner.IsVisibleChanged += OwnerVisibility;
        MouseLeftButtonDown += StartDrag;
        MouseMove += MoveDrag;
        MouseLeftButtonUp += EndDrag;
        LostMouseCapture += (_, _) => _dragStart = null;
        KeyDown += OnKey;
        _timer.Tick += (_, _) => Tick();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _owner.LocationChanged -= OwnerMoved;
            _owner.SizeChanged -= OwnerResized;
            _owner.StateChanged -= OwnerMoved;
            _owner.IsVisibleChanged -= OwnerVisibility;
        };
        Configure(settings);
        _timer.Start();
        Play("waving");
    }

    public void Configure(PetSettings settings)
    {
        settings.Normalize(); _settings = settings;
        Width = PetSheet.CellWidth * settings.Size / 100d;
        Height = PetSheet.CellHeight * settings.Size / 100d;
        Title = string.IsNullOrEmpty(settings.Name) ? "AgentCLIHub Pet" : settings.Name;
        ToolTip = UiText.Pick("클릭: 인사 · 두 번 클릭: 점프 · 드래그: 이동 · 우클릭: 동작",
            "Click: wave · Double-click: jump · Drag: move · Right-click: actions");
        System.Windows.Automation.AutomationProperties.SetName(_image, Title);
        var menu = new ContextMenu();
        string[] ko = ["가만히 있기", "우측 이동", "좌측 이동", "인사", "점프", "실패 반응", "입력 기다리기", "작업 중", "결과 검토"];
        string[] en = ["Idle", "Move right", "Move left", "Wave", "Jump", "Failure", "Waiting", "Working", "Review"];
        for (var row = 0; row < 9; row++)
        {
            var state = PetSheet.States[row];
            var item = new MenuItem { Header = UiText.Pick(ko[row], en[row]) };
            item.Click += (_, _) => Play(state);
            menu.Items.Add(item);
        }
        ContextMenu = menu;
        Reposition(); Tick();
    }

    public void Activity(string state)
    {
        if (!PetSheet.States.Contains(state)) return;
        _activity = state;
        _activityUntil = _clock.ElapsedMilliseconds + (state is "failed" or "review" ? 4000 : 3000);
    }

    public void Play(string state)
    {
        if (!PetSheet.States.Contains(state)) return;
        _gesture = state; _gestureUntil = _clock.ElapsedMilliseconds + 1920;
        _drawnState = ""; Tick();
    }

    private void Tick()
    {
        if (!_settings.Enabled || !_owner.IsVisible || _owner.WindowState == WindowState.Minimized) { Hide(); return; }
        if (!IsVisible) Show();
        var now = _clock.ElapsedMilliseconds;
        var state = now < _gestureUntil ? _gesture : now < _activityUntil ? _activity : "idle";
        if (_drawnState != state) { _drawnState = state; _frameStart = now; }
        var row = PetSheet.States.ToList().IndexOf(state);
        var col = _settings.Animate ? (int)((now - _frameStart) / 120 % PetSheet.Counts[row]) : 0;
        if (_settings.FollowCursor && state == "idle" && _dragStart is null && GetCursorPos(out var pointer))
        {
            var point = PointFromScreen(new Point(pointer.X, pointer.Y));
            var inOwner = _owner.PointFromScreen(new Point(pointer.X, pointer.Y));
            var dx = point.X - Width / 2; var dy = point.Y - Height / 2;
            if (inOwner.X >= 0 && inOwner.Y >= 0 && inOwner.X <= _owner.ActualWidth && inOwner.Y <= _owner.ActualHeight &&
                dx * dx + dy * dy > 40 * 40)
            {
                var direction = PetSheet.Direction(dx, dy);
                row = 9 + direction / 8; col = direction % 8;
            }
        }
        if (row == _lastRow && col == _lastFrame) return;
        _image.Source = _frames[row][col]; _lastRow = row; _lastFrame = col;
    }

    private void OwnerMoved(object? sender, EventArgs e) { Reposition(); Tick(); }
    private void OwnerResized(object sender, SizeChangedEventArgs e) => Reposition();
    private void OwnerVisibility(object sender, DependencyPropertyChangedEventArgs e) => Tick();

    private void Reposition()
    {
        if (!_owner.IsLoaded || _owner.WindowState == WindowState.Minimized) return;
        var origin = _owner.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(_owner);
        Left = origin.X / dpi.DpiScaleX + 8 + Math.Max(0, _owner.ActualWidth - Width - 16) * _settings.X;
        Top = origin.Y / dpi.DpiScaleY + 8 + Math.Max(0, _owner.ActualHeight - Height - 16) * _settings.Y;
    }

    private void StartDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Play("jumping"); e.Handled = true; return; }
        if (!GetCursorPos(out var point)) return;
        _dragStart = new Point(point.X, point.Y); _dragOrigin = new Point(_settings.X, _settings.Y);
        _moved = false; CaptureMouse(); e.Handled = true;
    }

    private void MoveDrag(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || !GetCursorPos(out var point)) return;
        var dx = point.X - start.X; var dy = point.Y - start.Y;
        if (!_moved && Math.Abs(dx) + Math.Abs(dy) < 6) return;
        _moved = true;
        var dpi = VisualTreeHelper.GetDpi(_owner);
        var previous = _settings.X;
        _settings.X = Math.Clamp(_dragOrigin.X + dx / dpi.DpiScaleX / Math.Max(1, _owner.ActualWidth - Width - 16), 0, 1);
        _settings.Y = Math.Clamp(_dragOrigin.Y + dy / dpi.DpiScaleY / Math.Max(1, _owner.ActualHeight - Height - 16), 0, 1);
        _gesture = _settings.X >= previous ? "running-right" : "running-left";
        _gestureUntil = _clock.ElapsedMilliseconds + 240;
        Reposition(); Tick();
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        _dragStart = null; ReleaseMouseCapture();
        if (_moved) { _gestureUntil = 0; _savePosition(_settings.X, _settings.Y); }
        else Play("waving");
        e.Handled = true;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: Play("waving"); break;
            case Key.Space: Play("jumping"); break;
            case Key.Left: _settings.X -= .05; break;
            case Key.Right: _settings.X += .05; break;
            case Key.Up: _settings.Y -= .05; break;
            case Key.Down: _settings.Y += .05; break;
            default: return;
        }
        _settings.Normalize(); Reposition(); _savePosition(_settings.X, _settings.Y); e.Handled = true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X; public int Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);
}
