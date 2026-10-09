using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

// System.Drawing 은 WinForms 를 켜면서 암시적 using 으로 딸려 들어온다. WPF 에도
// Point · Size · Rectangle 이 있어 이름이 겹치므로 **여기서는 전부 별칭으로만 부른다.**
using Drawing = System.Drawing;

// 겹치는 이름은 **쓰는 쪽을 못 박아 둔다.** 화면에 그리는 것은 전부 WPF 쪽이고,
// System.Drawing 것은 위 Drawing. 별칭으로만 부른다(Path 는 System.IO 와도 겹친다).
using Point = System.Windows.Point;
using Image = System.Windows.Controls.Image;
using Path = System.Windows.Shapes.Path;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace LlmTools;

/// <summary>
/// 화면에서 영역을 끌어 골라 PNG 로 돌려준다(캡처 도구의 그 동작).
///
/// **화면을 먼저 얼려 놓고 고르게 한다.** 순서가 반대면 두 가지가 깨진다 —
/// 우리가 덮은 어두운 판이 그림에 같이 찍히고, 고르는 동안 아래 화면이 바뀌면
/// 사용자가 본 것과 다른 그림이 나온다. 얼린 그림 위에서 고르면 본 대로 잘린다.
///
/// **잘라 내는 계산은 전부 그림의 픽셀 단위로 한다.** WPF 는 DIP 로 그리고 화면은
/// 물리 픽셀이라, 배율이 100% 가 아니면 두 값이 다르다. 화면 좌표를 직접 쓰면
/// 고배율 모니터에서 고른 자리와 잘린 자리가 어긋난다 — 마우스 자리를 그림 크기와
/// 창 크기의 비로 환산해 두면 배율이 몇이든 맞는다.
/// </summary>
public static class RegionCapture
{
    /// <summary>이만큼도 안 되는 사각형은 "잘못 누른 것" 으로 본다(점 하나를 첨부해 봐야 쓸 데가 없다).</summary>
    private const int MinPixels = 4;

    /// <summary>
    /// 영역을 고르게 하고 PNG 바이트를 돌려준다. 취소(Esc · 오른쪽 버튼 · 너무 작음)면 <c>null</c>.
    /// </summary>
    public static byte[]? Pick(Window owner)
    {
        // 모든 모니터를 합친 사각형. 한 대만 쓰면 두 번째 화면은 고를 수가 없다.
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (vs.Width <= 0 || vs.Height <= 0) return null;

        using var frozen = new Drawing.Bitmap(vs.Width, vs.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(frozen))
            g.CopyFromScreen(vs.X, vs.Y, 0, 0, new Drawing.Size(vs.Width, vs.Height),
                             Drawing.CopyPixelOperation.SourceCopy);

        var sel = ShowPicker(owner, frozen, vs);
        if (sel is not { } r) return null;
        if (r.Width < MinPixels || r.Height < MinPixels) return null;

        using var cut = frozen.Clone(r, frozen.PixelFormat);
        using var ms = new MemoryStream();
        cut.Save(ms, Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>얼린 그림을 화면 전체에 깔고 영역을 끌게 한다. 고른 사각형은 **그림의 픽셀 좌표**다.</summary>
    private static Drawing.Rectangle? ShowPicker(Window owner, Drawing.Bitmap frozen, Drawing.Rectangle vs)
    {
        var shot = ToBitmapSource(frozen);

        var root = new Grid { Background = Brushes.Black, Cursor = Cursors.Cross };
        root.Children.Add(new Image { Source = shot, Stretch = Stretch.Fill });

        // 안 고른 곳을 어둡게 덮는다. 고른 사각형만 구멍을 내 원래 밝기로 보여 준다 —
        // 덮기만 하고 구멍을 안 내면 무엇을 고르는 중인지 그림이 안 보인다.
        var dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)) };
        var edge = new Path { Stroke = new SolidColorBrush(Color.FromRgb(0x4C, 0xC9, 0x8F)), StrokeThickness = 1 };
        root.Children.Add(dim);
        root.Children.Add(edge);

        var canvas = new Canvas();
        var size = new TextBlock
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x16, 0x16, 0x16)),
            Padding = new Thickness(6, 3, 6, 3),
            FontSize = 12,
            Visibility = Visibility.Collapsed,
        };
        var hint = new TextBlock
        {
            Text = UiText.Pick("끌어서 영역을 고르세요 — Esc 또는 오른쪽 버튼으로 취소", "Drag to select a region — Esc or right-click to cancel"),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x16, 0x16, 0x16)),
            Padding = new Thickness(10, 6, 10, 6),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
        };
        canvas.Children.Add(size);
        root.Children.Add(canvas);
        root.Children.Add(hint);

        var win = new Window
        {
            Owner = owner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            AllowsTransparency = false,
            Background = Brushes.Black,
            Content = root,
            Title = UiText.Pick("영역 캡처", "Capture region"),
        };

        // **자리는 물리 픽셀로 잡는다.** WPF 의 Left/Top/Width 는 DIP 라, 배율이 100% 가
        // 아니거나 모니터마다 배율이 다르면 화면을 다 덮지 못해 가장자리가 비어 버린다.
        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            SetWindowPos(hwnd, HwndTopmost, vs.X, vs.Y, vs.Width, vs.Height, SwpShowWindow);
        };

        Point? start = null;
        Drawing.Rectangle? picked = null;

        // DIP → 얼린 그림의 픽셀. 창이 화면을 그대로 덮고 그림을 Fill 로 늘렸으므로
        // 가로·세로 비는 각각 한 번의 나눗셈으로 끝난다.
        Drawing.Rectangle ToPixels(Point a, Point b)
        {
            var sx = frozen.Width / Math.Max(root.ActualWidth, 1);
            var sy = frozen.Height / Math.Max(root.ActualHeight, 1);
            var x1 = (int)Math.Round(Math.Min(a.X, b.X) * sx);
            var y1 = (int)Math.Round(Math.Min(a.Y, b.Y) * sy);
            var x2 = (int)Math.Round(Math.Max(a.X, b.X) * sx);
            var y2 = (int)Math.Round(Math.Max(a.Y, b.Y) * sy);
            x1 = Math.Clamp(x1, 0, frozen.Width);
            y1 = Math.Clamp(y1, 0, frozen.Height);
            x2 = Math.Clamp(x2, 0, frozen.Width);
            y2 = Math.Clamp(y2, 0, frozen.Height);
            return new Drawing.Rectangle(x1, y1, x2 - x1, y2 - y1);
        }

        void Draw(Point? a, Point? b)
        {
            var full = new RectangleGeometry(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            if (a is not { } p1 || b is not { } p2)
            {
                dim.Data = full;
                edge.Data = null;
                size.Visibility = Visibility.Collapsed;
                return;
            }

            var box = new Rect(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y),
                               Math.Abs(p1.X - p2.X), Math.Abs(p1.Y - p2.Y));
            dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(box));
            edge.Data = new RectangleGeometry(box);

            // 몇 픽셀을 고르고 있는지 **잘릴 크기 그대로** 보여 준다(DIP 가 아니라).
            var px = ToPixels(p1, p2);
            size.Text = $"{px.Width} × {px.Height}";
            size.Visibility = Visibility.Visible;
            Canvas.SetLeft(size, Math.Min(box.X, Math.Max(root.ActualWidth - 90, 0)));
            Canvas.SetTop(size, box.Y >= 26 ? box.Y - 26 : box.Y + box.Height + 6);
        }

        root.MouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(root);
            hint.Visibility = Visibility.Collapsed;
            root.CaptureMouse();
            Draw(start, start);
        };
        root.MouseMove += (_, e) => { if (start is { } s) Draw(s, e.GetPosition(root)); };
        root.MouseLeftButtonUp += (_, e) =>
        {
            if (start is not { } s) return;
            root.ReleaseMouseCapture();
            picked = ToPixels(s, e.GetPosition(root));
            win.DialogResult = true;
        };

        // 취소 두 길. 어느 쪽도 없으면 잘못 열었을 때 빠져나갈 방법이 없다.
        root.MouseRightButtonDown += (_, _) => { picked = null; win.DialogResult = false; };
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) { picked = null; win.DialogResult = false; } };
        win.Loaded += (_, _) => { win.Activate(); Draw(null, null); };

        return win.ShowDialog() == true ? picked : null;
    }

    /// <summary>
    /// GDI 비트맵을 WPF 가 그릴 수 있는 것으로 바꾼다. **PNG 스트림을 거쳐 간다** —
    /// HBITMAP 을 직접 넘기는 길은 핸들을 손으로 풀어 줘야 하고, 한 번 빠뜨리면
    /// 캡처를 쓸 때마다 GDI 개체가 새어 나가다 결국 화면이 안 그려진다.
    /// </summary>
    private static BitmapSource ToBitmapSource(Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;

        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;   // 스트림을 놓기 전에 다 읽어 둔다
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
                                            int x, int y, int cx, int cy, uint flags);
}
