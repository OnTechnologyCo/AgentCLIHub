using AgentCLIHub.Cli;

var root = Path.Combine(Path.GetTempPath(), "agentclihub-installer-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
    passed++;
}
try
{
    var report = Path.Combine(root, "report.txt");
    Check(UpgradeCommand.Run(report, () => "이전 완료") == 0 && File.ReadAllText(report).Trim() == "이전 완료",
        "successful migration writes a readable Korean result");
    Check(File.ReadAllBytes(report).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }),
        "report includes the UTF-8 BOM required by Setup");
    Check(UpgradeCommand.Run(report, () => throw new IOException("앱을 닫으세요")) == 10 &&
        File.ReadAllText(report).Trim() == "앱을 닫으세요", "migration errors retain their original message and controlled exit code");
    var called = false;
    using (var locked = new FileStream(report, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Check(UpgradeCommand.Run(report, () => { called = true; return "unexpected"; }) == 11 && !called,
            "unwritable report stops before migration without an unhandled exception");
    Check(UpgradeCommand.Run(root, () => { called = true; return "unexpected"; }) == 11 && !called,
        "directory report path cannot start migration or trigger a second failing write");
    Check(UpgradeCommand.Run(report, () => "다시 시도 성공") == 0 && File.ReadAllText(report).Contains("다시 시도 성공"),
        "retry replaces the previous error instead of leaving a stale report");
    Console.WriteLine($"{passed} checks passed");
}
finally
{
    var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(root).StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(root).StartsWith("agentclihub-installer-check-")) throw new InvalidOperationException("Unsafe cleanup path");
    Directory.Delete(root, recursive: true);
}
