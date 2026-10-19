using System;
using System.IO;

namespace AgentCLIHub;

/// <summary>기록 경로의 연결을 먼저 읽고 실제 저장 폴더로 접근한다. Windows 보호 설정은 변경하지 않는다.</summary>
public static class HistoryPaths
{
    public static string ClaudeProjects(string home) => PhysicalDirectory(Path.Combine(home, "projects"));

    public static string PhysicalFile(string path) =>
        Path.Combine(PhysicalDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!), Path.GetFileName(path));

    public static string PhysicalDirectory(string path)
    {
        path = Path.GetFullPath(path);
        // 부모 연결부터 해석한다. 전체 경로를 먼저 열면 RedirectionGuard가 projects 아래 접근을 막는다.
        for (var hops = 0; hops < 40; hops++)
        {
            var root = Path.GetPathRoot(path)!;
            var parts = path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            var redirected = false;
            for (var i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                var entry = new DirectoryInfo(current);
                if (entry.LinkTarget is null) continue;
                var target = entry.ResolveLinkTarget(false)
                    ?? throw new IOException("대화 폴더의 연결 대상을 읽지 못했습니다: " + current);
                path = target.FullName;
                for (var j = i + 1; j < parts.Length; j++) path = Path.Combine(path, parts[j]);
                redirected = true;
                break;
            }
            if (!redirected) return Path.GetFullPath(current);
        }
        throw new IOException("대화 폴더의 연결이 순환하거나 너무 깊습니다: " + path);
    }
}
