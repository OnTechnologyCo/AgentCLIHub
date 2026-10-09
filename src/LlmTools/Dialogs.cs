using System;
using System.Windows;
using System.Windows.Interop;

using WinForms = System.Windows.Forms;

namespace LlmTools;

/// <summary>
/// 파일·폴더 고르기 창.
///
/// **WPF 것(<c>Microsoft.Win32.OpenFileDialog</c>)을 안 쓴다.** 그쪽은 최신 공용 항목
/// 대화상자(IFileDialog COM)를 타는데, 회사 PC 의 보안 제품이 그 경로를 막는 일이 있다 —
/// 그때 증상은 "버튼을 눌렀는데 아무 창도 안 뜬다" 뿐이라 원인이 화면에 안 드러난다.
///
/// 그래서 WinForms 것을 쓰되 <see cref="WinForms.FileDialog.AutoUpgradeEnabled"/> 를
/// **끈다.** 이것을 안 끄면 WinForms 도 .NET Core 부터는 같은 IFileDialog 로 올라가
/// 바꾼 의미가 없다. 끄면 옛 Win32 대화상자(GetOpenFileName · SHBrowseForFolder)로
/// 떨어진다 — 생김새는 낡았지만 막히지 않는다.
///
/// 부모 창을 넘겨 모달로 띄운다. 안 넘기면 대화상자가 뒤로 숨어, 사용자에게는
/// 앱이 멈춘 것으로 보인다.
/// </summary>
public static class Dialogs
{
    /// <summary>WPF 창을 WinForms 가 주인으로 알아볼 수 있게 감싼다.</summary>
    private sealed class Owner(IntPtr handle) : WinForms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    private static WinForms.IWin32Window? OwnerOf(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        return h == IntPtr.Zero ? null : new Owner(h);
    }

    private static bool Show(Window owner, WinForms.CommonDialog dlg)
    {
        var o = OwnerOf(owner);
        var r = o is null ? dlg.ShowDialog() : dlg.ShowDialog(o);
        return r == WinForms.DialogResult.OK;
    }

    /// <summary>폴더 하나. 취소하면 <c>null</c>.</summary>
    public static string? PickFolder(Window owner, string title, string? startAt)
    {
        using var dlg = new WinForms.FolderBrowserDialog
        {
            // 옛 대화상자는 제목 줄이 없다 — 설명 칸이 그 자리를 대신한다.
            Description = title,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            AutoUpgradeEnabled = false,
        };
        if (startAt is { Length: > 0 } && System.IO.Directory.Exists(startAt))
            dlg.SelectedPath = startAt;

        return Show(owner, dlg) && dlg.SelectedPath is { Length: > 0 } ? dlg.SelectedPath : null;
    }

    /// <summary>파일 하나. 취소하면 <c>null</c>.</summary>
    public static string? PickFile(Window owner, string title, string filter)
    {
        using var dlg = NewFileDialog(title, filter, multi: false);
        return Show(owner, dlg) && dlg.FileName is { Length: > 0 } ? dlg.FileName : null;
    }

    /// <summary>파일 여럿. 취소하면 빈 배열 — 부르는 쪽이 null 을 또 따지지 않게 한다.</summary>
    public static string[] PickFiles(Window owner, string title, string filter)
    {
        using var dlg = NewFileDialog(title, filter, multi: true);
        return Show(owner, dlg) ? dlg.FileNames : [];
    }

    private static WinForms.OpenFileDialog NewFileDialog(string title, string filter, bool multi) => new()
    {
        Title = title,
        Filter = filter,
        Multiselect = multi,
        // 위 주석 참조. **이 줄이 이 파일의 요점이다.**
        AutoUpgradeEnabled = false,
        // 옛 대화상자는 마지막 폴더를 스스로 기억하지 않는다. 앱이 살아 있는 동안이라도
        // 같은 자리에서 다시 열리게 둔다 — 첨부를 여러 번 고르는 일이 흔하다.
        RestoreDirectory = false,
        CheckFileExists = true,
        CheckPathExists = true,
    };
}
