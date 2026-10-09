using System;
using System.Windows;

namespace LlmTools;

public partial class App : Application
{
    /// <summary>
    /// "지금 떠 있다" 는 표식. **두 번째 실행을 막지는 않는다** — 창을 두 개 띄우는 것은
    /// 막을 이유가 없고(브라우저 창구도 프로세스마다 따로 연다), 이것은 오로지
    /// 설치 프로그램이 보라고 두는 것이다.
    ///
    /// 이것이 없으면 설치·제거 중에 앱이 떠 있어도 아무 말이 없고, 잠긴 파일 때문에
    /// 설치가 반쯤 되다 만다 — 그때 화면에는 이유가 안 나온다.
    /// installer\LlmTools.iss 의 <c>CheckForMutexes</c> 가 이 이름을 본다.
    /// </summary>
    private static System.Threading.Mutex? _running;

    public App()
    {
        // 소유하지 않는다(createdNew 를 안 본다). 있는지만 보이면 되는 표식이다.
        try { _running = new System.Threading.Mutex(false, "LlmTools.SingleInstance"); } catch { }

        // **어디서 터져도 사람이 읽을 수 있게 한다.** WPF 의 기본 동작은 미처리 예외에
        // 창을 통째로 닫는 것이고, 그러면 사용자에게는 "눌렀더니 앱이 사라졌다" 로만 남는다.
        // 여기서 잡아 이유를 보여 준다 — 터미널 하나 못 띄운 것으로 앱이 죽으면 안 된다.
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.ToString(), UiText.Pick("LLM Tools — 처리하지 못한 오류", "LLM Tools — Unhandled error"),
                            MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}
