using System.Reflection;
using System.Text.Json;
using AgentCLIHub;

var root = Path.Combine(Path.GetTempPath(), "agentclihub-rename-" + Guid.NewGuid().ToString("N"));
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
    var writeProfile = typeof(AgentBrief).GetMethod("WriteCodexFile", BindingFlags.NonPublic | BindingFlags.Static)!;
    var legacy = Path.Combine(root, "llmtools.config.toml");
    var original = "# LLM Tools 가 만든 파일입니다.\n" +
        "developer_instructions = \"old browser guidance\"\n" +
        "model = \"user-selected-model\"\n[windows]\nsandbox = \"unelevated\"\n" +
        "[projects.'D:\\work']\ntrust_level = \"trusted\"\n";
    File.WriteAllText(legacy, original);
    writeProfile.Invoke(null, [root, "AgentCLIHub browser guidance"]);
    var profile = AgentBrief.CodexFileIn(root);
    var migrated = File.ReadAllText(profile);
    Check(Path.GetFileName(profile) == "agentclihub.config.toml" &&
        migrated.Contains("user-selected-model") && migrated.Contains("sandbox = \"unelevated\"") &&
        migrated.Contains("trust_level = \"trusted\""), "new Codex profile retains existing model, sandbox and folder trust");
    Check(migrated.Contains("AgentCLIHub browser guidance") && !migrated.Contains("LLM Tools") &&
        !migrated.Contains("old browser guidance") && File.ReadAllText(legacy) == original,
        "profile migration replaces app guidance and preserves the original file");
    File.WriteAllText(profile, migrated.Replace("user-selected-model", "new-model"));
    writeProfile.Invoke(null, [root, "updated guidance"]);
    Check(File.ReadAllText(profile).Contains("new-model") && !File.ReadAllText(profile).Contains("user-selected-model"),
        "later launches preserve the new profile instead of reimporting legacy settings");
    var before = File.GetLastWriteTimeUtc(profile);
    writeProfile.Invoke(null, [root, "updated guidance"]);
    Check(File.GetLastWriteTimeUtc(profile) == before, "unchanged profile is not rewritten");

    var transcript = new Transcript { Backend = "codex_cli", SessionId = "test", Cwd = root };
    transcript.Items.Add(new(ItemKind.User, "keep this conversation"));
    var handoff = AntigravityTransfer.Write(transcript, root, "test");
    Check(Path.GetFileName(Path.GetDirectoryName(handoff)) == ".agentclihub-handoff" &&
        File.ReadAllText(handoff).Contains("agentclihub-handoff-v1") &&
        AntigravityTransfer.ReadHandoff(handoff).Title == transcript.Title,
        "new handoffs use AgentCLIHub names and round-trip their content");
    var oldFolder = Path.Combine(root, ".llmtools-handoff");
    Directory.CreateDirectory(oldFolder);
    var oldHandoff = Path.Combine(oldFolder, Guid.NewGuid() + ".json");
    File.WriteAllText(oldHandoff, File.ReadAllText(handoff).Replace("agentclihub-handoff-v1", "llmtools-handoff-v1"));
    Check(AntigravityTransfer.ReadHandoff(oldHandoff).Title == transcript.Title,
        "saved tabs can still open legacy handoff folders and documents");
    var oldPrompt = "[LLM Tools conversation handoff] File: " + oldHandoff + " | import";
    var log = Path.Combine(root, "legacy-transcript.jsonl");
    File.WriteAllText(log, JsonSerializer.Serialize(new { step_index = 0, type = "USER_INPUT", content = oldPrompt }) + "\n");
    Check(AntigravityTransfer.Read(log).Title == transcript.Title,
        "legacy transcript markers recover the original imported conversation");
    var invalid = Path.Combine(root, Guid.NewGuid() + ".json");
    File.Copy(oldHandoff, invalid);
    var rejected = false;
    try { AntigravityTransfer.ReadHandoff(invalid); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "compatibility does not accept handoffs outside the two supported folders");

    TransferChecks.Run(root, Check);
    Console.WriteLine($"{passed} checks passed");
}
finally
{
    var full = Path.GetFullPath(root);
    var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
    if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("agentclihub-rename-"))
        throw new InvalidOperationException("test cleanup escaped its temporary directory");
    Directory.Delete(full, recursive: true);
}
