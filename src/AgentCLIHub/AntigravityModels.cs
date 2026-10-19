using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentCLIHub;

/// <summary>현재 설치된 agy models 의 공개 출력에서 모델 ID 와 표시 이름을 읽는다.</summary>
public static class AntigravityModels
{
    public sealed record Model(string Id, string Name);
    public sealed record Result(IReadOnlyList<Model> Models, string? Error = null);

    public static IReadOnlyList<Model> Parse(string output)
    {
        var models = new List<Model>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        output = Regex.Replace(output, @"\x1b\[[0-?]*[ -/]*[@-~]", "");
        foreach (var line in output.Split('\n'))
        {
            var columns = line.Trim().Split('\t', 2);
            if (columns.Length != 2) continue;
            var id = columns[0].Trim();
            var name = columns[1].Trim();
            if (id.Length is 0 or > 200 || name.Length is 0 or > 300 ||
                id.Any(char.IsWhiteSpace) || id.Any(char.IsControl) || name.Any(char.IsControl) ||
                id.Equals("id", StringComparison.OrdinalIgnoreCase) || !seen.Add(id)) continue;
            models.Add(new Model(id, name));
        }
        return models;
    }

    public static async Task<Result> ReadAsync(CliLocator.Result cli, CancellationToken cancellationToken = default)
    {
        if (!cli.Found) return new([], "Antigravity CLI를 찾지 못했습니다.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Process? process = null;
        try
        {
            var args = CliLocator.SplitCommandLine(cli.LaunchCommand(cli.CommandHead() + " models"));
            var info = new ProcessStartInfo(args[0])
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in args.Skip(1)) info.ArgumentList.Add(arg);
            info.Environment["PATH"] = CliLocator.LaunchPath(CliProfile.Antigravity);
            info.Environment["NO_COLOR"] = "1";
            info.Environment.Remove("FORCE_COLOR");
            process = Process.Start(info);
            if (process is null) return new([], "모델 목록 조회를 시작하지 못했습니다.");
            process.StandardInput.Close(); // 로그인 입력을 기다리는 숨은 프로세스를 남기지 않는다.
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0)
                return new([], $"모델 목록을 읽지 못했습니다(종료 코드 {process.ExitCode}). CLI 로그인과 네트워크를 확인해 주세요.");
            var models = Parse(await output);
            return models.Count > 0 ? new(models) : new([], "CLI에서 모델 목록을 받지 못했습니다. 로그인 상태와 CLI 버전을 확인해 주세요.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new([], "모델 목록 조회 시간이 초과됐습니다. 다시 불러오기를 눌러 주세요.");
        }
        catch (OperationCanceledException) { throw; }
        catch { return new([], "모델 목록을 읽지 못했습니다. CLI 실행 경로를 확인해 주세요."); }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
        }
    }
}
