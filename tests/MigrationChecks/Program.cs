using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentCLIHub;

var root = Path.Combine(Path.GetTempPath(), "agentclihub-migration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
void Check(bool ok, string description)
{
    if (!ok) throw new Exception(description);
    Console.WriteLine("PASS " + description);
    passed++;
}
string Case(string name)
{
    var path = Path.Combine(root, name);
    Directory.CreateDirectory(path);
    return path;
}
void ExpectFailure(Action action, string description)
{
    bool failed = false;
    try { action(); } catch (Exception) { failed = true; }
    Check(failed, description);
}
void Seed(string parent)
{
    var legacy = Path.Combine(parent, "LlmTools");
    Directory.CreateDirectory(Path.Combine(legacy, "accounts", "a"));
    Directory.CreateDirectory(Path.Combine(legacy, "webview2", "Default"));
    var settings = new JsonObject
    {
        ["UiLanguage"] = "en",
        ["Accounts"] = new JsonArray(new JsonObject { ["Id"] = "a", ["Backend"] = "codex_cli" }),
        ["Tabs"] = new JsonArray(new JsonObject { ["Cwd"] = Path.Combine(legacy, "project"), ["Name"] = "LLM Tools notes" }),
        ["Unrelated"] = legacy + "-other"
    };
    File.WriteAllText(Path.Combine(legacy, "settings.json"), settings.ToJsonString());
    File.WriteAllBytes(Path.Combine(legacy, "accounts", "a", "auth.json"), Encoding.UTF8.GetBytes("synthetic-auth-fixture"));
    File.WriteAllBytes(Path.Combine(legacy, "session.dat"), RandomNumberGenerator.GetBytes(80));
    File.WriteAllBytes(Path.Combine(legacy, "webview2", "Default", "Cookies"), RandomNumberGenerator.GetBytes(64));
}
try
{
    var clean = Case("fresh");
    UpgradeMigration.Run(clean, "inspect");
    UpgradeMigration.Run(clean, "migrate");
    UpgradeMigration.Run(clean, "commit");
    Check(!Directory.EnumerateFileSystemEntries(clean).Any(), "fresh installation does not create legacy or migration leftovers");

    var success = Case("success");
    Seed(success);
    var old = Path.Combine(success, "LlmTools");
    var next = Path.Combine(success, "AgentCLIHub");
    var binary = File.ReadAllBytes(Path.Combine(old, "session.dat"));
    var auth = File.ReadAllBytes(Path.Combine(old, "accounts", "a", "auth.json"));
    var cookies = File.ReadAllBytes(Path.Combine(old, "webview2", "Default", "Cookies"));
    UpgradeMigration.Run(success, "inspect");
    Check(Directory.Exists(old) && !Directory.Exists(next), "pre-install inspection leaves data untouched");
    UpgradeMigration.Run(success, "migrate");
    Check(!Directory.Exists(old) && Directory.Exists(next), "legacy directory is relocated without leaving a duplicate");
    var converted = JsonNode.Parse(File.ReadAllText(Path.Combine(next, "settings.json")))!;
    Check(converted["UiLanguage"]!.GetValue<string>() == "en" && converted["Accounts"]!.AsArray().Count == 1,
        "language and account identity survive conversion");
    Check(converted["Tabs"]![0]!["Cwd"]!.GetValue<string>() == Path.Combine(next, "project") &&
        converted["Tabs"]![0]!["Name"]!.GetValue<string>() == "LLM Tools notes" &&
        converted["Unrelated"]!.GetValue<string>() == old + "-other", "only paths inside the old data root are rewritten");
    Check(binary.SequenceEqual(File.ReadAllBytes(Path.Combine(next, "session.dat"))) &&
        auth.SequenceEqual(File.ReadAllBytes(Path.Combine(next, "accounts", "a", "auth.json"))) &&
        cookies.SequenceEqual(File.ReadAllBytes(Path.Combine(next, "webview2", "Default", "Cookies"))),
        "encrypted sessions, account credentials, and browser storage remain byte-identical");
    UpgradeMigration.Run(success, "commit");
    UpgradeMigration.Run(success, "migrate");
    Check(!File.Exists(Path.Combine(next, ".agentclihub-upgrade.json")), "commit removes temporary receipt and repeat installation is safe");

    var cancel = Case("cancel");
    Seed(cancel);
    var original = File.ReadAllBytes(Path.Combine(cancel, "LlmTools", "settings.json"));
    UpgradeMigration.Run(cancel, "migrate");
    UpgradeMigration.Run(cancel, "rollback");
    Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(cancel, "LlmTools", "settings.json"))) &&
        !Directory.Exists(Path.Combine(cancel, "AgentCLIHub")), "cancel restores the original folder and exact settings bytes");

    var conflict = Case("conflict");
    Seed(conflict);
    Directory.CreateDirectory(Path.Combine(conflict, "AgentCLIHub"));
    File.WriteAllText(Path.Combine(conflict, "AgentCLIHub", "keep.txt"), "keep");
    ExpectFailure(() => UpgradeMigration.Run(conflict, "migrate"), "conflicting destination is rejected");
    Check(File.ReadAllText(Path.Combine(conflict, "AgentCLIHub", "keep.txt")) == "keep" &&
        Directory.Exists(Path.Combine(conflict, "LlmTools")), "both profiles survive a conflict");

    var malformed = Case("malformed");
    Seed(malformed);
    File.WriteAllText(Path.Combine(malformed, "LlmTools", "settings.json"), "broken-json");
    ExpectFailure(() => UpgradeMigration.Run(malformed, "migrate"), "invalid settings prevent migration before files move");
    Check(File.ReadAllText(Path.Combine(malformed, "LlmTools", "settings.json")) == "broken-json",
        "invalid settings are preserved for repair");

    var locked = Case("locked");
    Seed(locked);
    using (var hold = new FileStream(Path.Combine(locked, "LlmTools", "session.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        ExpectFailure(() => UpgradeMigration.Run(locked, "migrate"), "locked live files prevent migration");
    Check(Directory.Exists(Path.Combine(locked, "LlmTools")), "locked-file failure keeps the original directory");

    var changed = Case("changed");
    Seed(changed);
    UpgradeMigration.Run(changed, "migrate");
    File.WriteAllText(Path.Combine(changed, "AgentCLIHub", "new-user-data.txt"), "new work");
    ExpectFailure(() => UpgradeMigration.Run(changed, "rollback"), "rollback cannot discard data written after migration");
    Check(File.Exists(Path.Combine(changed, "AgentCLIHub", "new-user-data.txt")), "new work survives a refused rollback");

    var interrupted = Case("interrupted");
    Seed(interrupted);
    var oldBytes = File.ReadAllBytes(Path.Combine(interrupted, "LlmTools", "settings.json"));
    UpgradeMigration.Run(interrupted, "migrate");
    File.WriteAllBytes(Path.Combine(interrupted, "AgentCLIHub", "settings.json"), oldBytes);
    UpgradeMigration.Run(interrupted, "migrate");
    Check(JsonNode.Parse(File.ReadAllText(Path.Combine(interrupted, "AgentCLIHub", "settings.json")))!["Tabs"]![0]!["Cwd"]!
        .GetValue<string>().Contains("AgentCLIHub"), "interrupted path conversion resumes from its receipt");

    if (OperatingSystem.IsWindows())
    {
        var linked = Case("junction");
        Seed(linked);
        var external = Case("external-cli-home");
        File.WriteAllText(Path.Combine(external, "keep.txt"), "shared history");
        var junction = Path.Combine(linked, "LlmTools", "accounts", "a", "sessions");
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        psi.Arguments = "/d /c mklink /J \"" + junction + "\" \"" + external + "\"";
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        Check(proc.ExitCode == 0, "external shared-history junction fixture created");
        UpgradeMigration.Run(linked, "migrate");
        Check(File.ReadAllText(Path.Combine(external, "keep.txt")) == "shared history" &&
            new DirectoryInfo(Path.Combine(linked, "AgentCLIHub", "accounts", "a", "sessions")).LinkTarget is not null,
            "shared CLI home is not traversed, copied, or removed");
        Directory.Delete(Path.Combine(linked, "AgentCLIHub", "accounts", "a", "sessions"));
    }
    Console.WriteLine($"SUCCESS {passed} migration checks");
}
finally { Directory.Delete(root, true); }
