using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace AgentCLIHub;

/// <summary>설치 프로그램과 앱이 함께 쓰는 사용자 데이터 이전. 외부 CLI 홈과 연결 대상은 건드리지 않는다.</summary>
public static class UpgradeMigration
{
    private const string Receipt = ".agentclihub-upgrade.json";
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentCLIHub");

    public static string RunForCurrentUser(string operation, bool requireClosed = true)
    {
        using var gate = new Mutex(false, "Local\\AgentCLIHub.Upgrade");
        bool entered;
        try { entered = gate.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { entered = true; }
        if (!entered) throw new IOException("다른 설치 또는 데이터 이전이 진행 중입니다.");
        try
        {
            var parent = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if ((requireClosed || Directory.Exists(Path.Combine(parent, "LlmTools"))) &&
                operation is "inspect" or "migrate" or "rollback")
                foreach (var name in new[] { "LlmTools.SingleInstance", "AgentCLIHub.Running" })
                    if (Mutex.TryOpenExisting(name, out var running))
                    {
                        running.Dispose();
                        throw new IOException("LLM Tools / AgentCLIHub 창을 모두 닫은 뒤 다시 설치하세요.");
                    }
            return Run(parent, operation);
        }
        finally { gate.ReleaseMutex(); }
    }

    // parent는 테스트에서도 주입한다. CLI는 사용자 지정 경로를 받지 않고 현재 사용자 폴더만 사용한다.
    public static string Run(string parent, string operation)
    {
        var oldRoot = Path.Combine(Path.GetFullPath(parent), "LlmTools");
        var newRoot = Path.Combine(Path.GetFullPath(parent), "AgentCLIHub");
        if (operation == "commit")
        {
            RejectLink(newRoot);
            if (Directory.Exists(newRoot)) File.Delete(Path.Combine(newRoot, Receipt));
            return "Migration committed.";
        }
        if (operation == "rollback") return Rollback(oldRoot, newRoot);
        if (operation is not ("inspect" or "migrate")) throw new ArgumentException("Unknown upgrade operation.");
        RejectLink(oldRoot);
        RejectLink(newRoot);
        if (!Directory.Exists(oldRoot))
        {
            CompletePending(oldRoot, newRoot);
            ValidateSettings(newRoot);
            return "No legacy data; current settings validated.";
        }
        if (Directory.Exists(newRoot) && Directory.EnumerateFileSystemEntries(newRoot).Any())
            throw new IOException("기존 LlmTools와 AgentCLIHub 데이터가 모두 있습니다. 덮어쓰지 않았습니다. 두 폴더의 충돌을 먼저 확인해야 합니다.");
        ValidateSettings(oldRoot);
        var before = Snapshot(oldRoot);
        if (operation == "inspect") return $"Legacy settings validated; {before.Count} entries ready.";

        var originalSettings = ReadSettings(oldRoot);
        var convertedSettings = ConvertSettings(originalSettings, oldRoot, newRoot);
        var expected = new Dictionary<string, string>(before, StringComparer.OrdinalIgnoreCase);
        if (convertedSettings is not null) expected["settings.json"] = Hash(convertedSettings);
        var receipt = new JsonObject
        {
            ["schema"] = 1,
            ["originalSettings"] = originalSettings is null ? null : Convert.ToBase64String(originalSettings),
            ["expected"] = ToJson(expected)
        }.ToJsonString();

        if (Directory.Exists(newRoot)) Directory.Delete(newRoot); // 비어 있음을 위에서 확인했다.
        AtomicWrite(Path.Combine(oldRoot, Receipt), Encoding.UTF8.GetBytes(receipt));
        try { Directory.Move(oldRoot, newRoot); } // 같은 부모 안에서 원자적으로 옮겨 ACL·암호화 파일·junction을 보존한다.
        catch { File.Delete(Path.Combine(oldRoot, Receipt)); throw; }
        try
        {
            AssertSnapshot(before, Snapshot(newRoot));
            if (convertedSettings is not null) AtomicWrite(Path.Combine(newRoot, "settings.json"), convertedSettings);
            AssertSnapshot(expected, Snapshot(newRoot));
            ValidateSettings(newRoot);
            return $"Migrated and verified {before.Count} entries. Legacy data directory removed.";
        }
        catch
        {
            if (originalSettings is not null) AtomicWrite(Path.Combine(newRoot, "settings.json"), originalSettings);
            File.Delete(Path.Combine(newRoot, Receipt));
            Directory.Move(newRoot, oldRoot);
            throw;
        }
    }

    private static void CompletePending(string oldRoot, string newRoot)
    {
        var path = Path.Combine(newRoot, Receipt);
        if (!File.Exists(path)) return;
        var receipt = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var original = receipt["originalSettings"]?.GetValue<string>();
        var current = ReadSettings(newRoot);
        // 폴더를 옮긴 직후 전원이 끊겼다면 설정 경로 변환을 마저 수행한다.
        if (original is not null && current is not null && current.AsSpan().SequenceEqual(Convert.FromBase64String(original)))
            AtomicWrite(Path.Combine(newRoot, "settings.json"), ConvertSettings(current, oldRoot, newRoot)!);
        var expected = receipt["expected"]!.AsObject().ToDictionary(p => p.Key,
            p => p.Value!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
        AssertSnapshot(expected, Snapshot(newRoot));
    }

    private static string Rollback(string oldRoot, string newRoot)
    {
        RejectLink(oldRoot);
        RejectLink(newRoot);
        var path = Path.Combine(newRoot, Receipt);
        if (!File.Exists(path)) return "No pending migration.";
        if (Directory.Exists(oldRoot)) throw new IOException("기존 폴더가 다시 생성되어 자동 복원을 중단했습니다. 데이터는 보존되어 있습니다.");
        var receipt = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new IOException("이전 복구 기록을 읽지 못했습니다.");
        var expected = (receipt["expected"] as JsonObject)?.ToDictionary(p => p.Key,
            p => p.Value!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            ?? throw new IOException("이전 검증 기록이 없습니다.");
        AssertSnapshot(expected, Snapshot(newRoot)); // 새 앱이 데이터를 바꿨으면 자동으로 되돌리지 않는다.
        var original = receipt["originalSettings"]?.GetValue<string>();
        var converted = ReadSettings(newRoot);
        try
        {
            if (original is not null) AtomicWrite(Path.Combine(newRoot, "settings.json"), Convert.FromBase64String(original));
            Directory.Move(newRoot, oldRoot);
            File.Delete(Path.Combine(oldRoot, Receipt));
        }
        catch
        {
            if (Directory.Exists(newRoot) && converted is not null)
                AtomicWrite(Path.Combine(newRoot, "settings.json"), converted);
            throw;
        }
        return "Original data directory and settings restored.";
    }

    private static byte[]? ReadSettings(string root) => File.Exists(Path.Combine(root, "settings.json"))
        ? File.ReadAllBytes(Path.Combine(root, "settings.json")) : null;

    private static JsonObject ParseSettings(byte[] bytes)
    {
        // 구버전 PowerShell이 저장한 UTF-8 BOM도 허용한다.
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        return JsonNode.Parse(text) as JsonObject ?? throw new IOException("settings.json 형식이 올바르지 않습니다.");
    }

    private static void ValidateSettings(string root)
    {
        if (ReadSettings(root) is { } bytes)
        {
            var settings = ParseSettings(bytes);
            foreach (var key in new[] { "Tabs", "Accounts", "Bookmarks" })
                if (settings[key] is { } value && value is not JsonArray)
                    throw new IOException($"settings.json의 {key} 형식을 확인해야 합니다. 원본은 변경하지 않았습니다.");
        }
    }

    private static byte[]? ConvertSettings(byte[]? bytes, string oldRoot, string newRoot)
    {
        if (bytes is null) return null;
        var node = ParseSettings(bytes);
        bool changed = false;
        void Visit(JsonNode current)
        {
            if (current is JsonObject obj)
                foreach (var pair in obj.ToArray())
                {
                    if (pair.Value is JsonValue val && val.TryGetValue<string>(out var value))
                    {
                        var next = Relocate(value, oldRoot, newRoot);
                        if (next != value) { obj[pair.Key] = next; changed = true; }
                    }
                    else if (pair.Value is not null) Visit(pair.Value);
                }
            else if (current is JsonArray array)
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue val && val.TryGetValue<string>(out var value))
                    {
                        var next = Relocate(value, oldRoot, newRoot);
                        if (next != value) { array[i] = next; changed = true; }
                    }
                    else if (array[i] is { } child) Visit(child);
                }
        }
        Visit(node);
        return changed ? Encoding.UTF8.GetBytes(node.ToJsonString(new() { WriteIndented = true })) : bytes;
    }

    private static string Relocate(string value, string oldRoot, string newRoot)
    {
        var normalized = value.Replace('/', '\\');
        var prefix = oldRoot.Replace('/', '\\').TrimEnd('\\');
        if (normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return newRoot;
        return normalized.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)
            ? newRoot + normalized[prefix.Length..] : value;
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Walk(string dir)
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(root, entry.FullName);
                if (relative == Receipt) continue;
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    var target = entry.LinkTarget ?? throw new IOException("읽을 수 없는 연결 파일이 있습니다.");
                    var resolved = entry.ResolveLinkTarget(false)?.FullName ?? target;
                    if (Relocate(resolved.Replace("\\\\?\\", ""), root, "MOVED") != resolved.Replace("\\\\?\\", ""))
                        throw new IOException("데이터 폴더 내부를 절대 경로로 연결한 항목이 있습니다. 연결을 확인해야 합니다: " + relative);
                    result[relative] = "link:" + target;
                    continue; // 외부 CLI 홈의 junction은 절대 따라가거나 삭제하지 않는다.
                }
                if (entry is DirectoryInfo)
                {
                    result[relative] = "directory";
                    Walk(entry.FullName);
                }
                else
                {
                    using var file = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    result[relative] = Convert.ToHexString(SHA256.HashData(file));
                }
            }
        }
        Walk(root);
        return result;
    }

    private static void AssertSnapshot(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        if (expected.Count != actual.Count || expected.Any(p => !actual.TryGetValue(p.Key, out var hash) || hash != p.Value))
            throw new IOException("이전 중 파일이 변경되었거나 검증에 실패했습니다. 원본을 보존합니다.");
    }

    private static JsonObject ToJson(Dictionary<string, string> values)
    {
        var obj = new JsonObject();
        foreach (var pair in values) obj[pair.Key] = pair.Value;
        return obj;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void RejectLink(string path)
    {
        if ((Directory.Exists(path) || File.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("데이터 루트가 연결 폴더라 자동 이전을 중단했습니다: " + path);
        if (File.Exists(path)) throw new IOException("데이터 폴더 경로에 파일이 있습니다: " + path);
    }

    private static void AtomicWrite(string path, byte[] data)
    {
        var temp = path + ".agentclihub-upgrade.tmp";
        try { File.WriteAllBytes(temp, data); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
