using System.Text;

namespace AgentCLIHub.Cli;

/// <summary>설치 도우미의 결과 파일은 작업 전에 열고, 기록 실패도 종료 코드로 돌려준다.</summary>
internal static class UpgradeCommand
{
    internal static int Run(string reportPath, Func<string> operation)
    {
        try
        {
            // 권한이 없으면 데이터 이전에 손대기 전에 실패한다.
            using var stream = new FileStream(reportPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var report = new StreamWriter(stream, new UTF8Encoding(true));
            report.Flush();
            int exit;
            string message;
            try { message = operation(); exit = 0; }
            catch (Exception ex) { message = ex.Message; exit = 10; }
            report.WriteLine(message);
            return exit;
        }
        catch (Exception ex)
        {
            // 결과 파일 쓰기가 실패한 catch에서 같은 파일을 다시 쓰면 예외가 밖으로 샌다.
            try { Console.Error.WriteLine("설정 이전 결과 기록 실패: " + ex.Message); } catch { }
            return 11;
        }
    }
}
