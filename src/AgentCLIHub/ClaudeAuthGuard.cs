using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCLIHub;

/// <summary>
/// Claude Code 의 로그인 상태를 대신 기억해 둔다.
///
/// **왜 필요한가.** Claude Code 의 계정 상태는 두 파일에 있다.
///   <c>~/.claude.json</c>              — 계정(<c>oauthAccount</c>)·온보딩 완료 표시
///   <c>~/.claude/.credentials.json</c> — 토큰
/// 이 앱은 탭마다 <c>claude</c> 를 하나씩 띄우므로 **여러 프로세스가 같은 파일을 쓴다.**
/// 원자적으로 쓰긴 하지만(옆에 <c>.claude.json.tmp.&lt;pid&gt;.&lt;난수&gt;</c> 가 남는 것이 그 흔적이다)
/// 수가 많아지면 갈아 끼우다 끊긴 채로 남는 일이 실제로 일어났다 — 그날 본체는 43KB 에서
/// 1.4KB 로 잘렸고 <c>hasCompletedOnboarding</c> 과 <c>projects</c> 가 통째로 사라졌다.
/// 그러면 화면에는 **"로그인이 풀렸다 · 처음 켠 것 같다"** 로만 보인다.
///
/// 그래서 성할 때 한 벌 떠 두었다가, 켤 때 깨져 있으면 되돌린다.
///
/// **토큰을 그대로 복사하지 않는다.** 떠 두는 것은 DPAPI(<see cref="DataProtectionScope.CurrentUser"/>)
/// 로 감싼다 — 같은 Windows 계정에서만 풀린다. 원본이 평문이라 해서 사본까지 평문으로 둘
/// 이유는 없다(사본은 원본보다 오래 남는다).
///
/// 실패는 전부 조용히 넘어간다. 이것 때문에 앱이 안 뜨면 본말이 뒤집힌다 —
/// 최악이라야 "로그인을 다시 해야 한다" 이고, 그것은 이 기능이 없을 때의 상태다.
/// </summary>
public static class ClaudeAuthGuard
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string ConfigPath => Path.Combine(Home, ".claude.json");
    private static string CredPath => Path.Combine(Home, ".claude", ".credentials.json");
    private static string VaultPath => Path.Combine(Settings.DataDir, "claude-auth.dat");

    private static readonly byte[] Salt = "LlmTools/claude-auth/v1"u8.ToArray();

    /// <summary>떠 두는 모양. 두 파일을 글자 그대로 담는다 — 형식을 해석해 두면 CLI 가 바뀔 때 같이 깨진다.</summary>
    private sealed record Snap(string Config, string Cred, string Account, DateTime At);

    // ── 성한가 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>.claude.json</c> 이 로그인된 상태로 보이는가. **키 두 개로 판정한다** —
    /// 잘린 파일도 JSON 으로는 멀쩡히 읽히므로(그날 그랬다) 파싱 성공만으로는 부족하다.
    /// </summary>
    private static bool ConfigLooksSignedIn(string text, out string account)
    {
        account = "";
        try
        {
            if (JsonNode.Parse(text) is not JsonObject o) return false;
            if (o["oauthAccount"] is not JsonObject acc) return false;
            if (o["hasCompletedOnboarding"]?.GetValue<bool>() is not true) return false;
            account = acc["accountUuid"]?.ToString() ?? "";
            return true;
        }
        catch { return false; }
    }

    private static bool CredLooksValid(string text)
    {
        try { return JsonNode.Parse(text) is JsonObject o && o.Count > 0; }
        catch { return false; }
    }

    // ── 지키기 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 살아 있는 파일이 성하면 떠 두고, 깨져 있으면 떠 둔 것으로 되돌린다.
    /// </summary>
    /// <param name="mayRestore">
    /// 되돌려도 되는 때인가. **켤 때만 참이다** — 돌아가는 중에 되돌리면 그때 떠 있는
    /// <c>claude</c> 들이 곧바로 자기 상태로 다시 덮어써서, 되돌린 것이 그대로 날아간다.
    /// </param>
    public static void Guard(bool mayRestore, Action<string, string> log)
    {
        try
        {
            var cfg = ReadOrEmpty(ConfigPath);
            var cred = ReadOrEmpty(CredPath);

            if (ConfigLooksSignedIn(cfg, out var account) && CredLooksValid(cred))
            {
                Save(new Snap(cfg, cred, account, DateTime.Now));
                return;
            }

            if (!mayRestore) return;

            var snap = Load();
            if (snap is null)
            {
                log("Claude Code 로그인 상태가 성하지 않은데 되돌릴 사본이 없습니다 — 로그인이 필요할 수 있습니다.", "w");
                return;
            }

            // **돌아가는 claude 가 있으면 손대지 않는다.** 지금 쓰고 있는 파일을 갈아 끼우면
            // 그쪽이 들고 있던 상태와 엇갈려, 고치려던 것을 더 헝클어 놓는다.
            if (System.Diagnostics.Process.GetProcessesByName("claude").Length > 0)
            {
                log("Claude Code 로그인 상태가 깨져 보이지만 claude 가 돌고 있어 되돌리지 않았습니다.", "w");
                return;
            }

            WriteAtomic(ConfigPath, snap.Config);
            WriteAtomic(CredPath, snap.Cred);
            log($"Claude Code 로그인 상태를 되돌렸습니다 ({snap.At:yyyy-MM-dd HH:mm} 사본). " +
                "다시 로그인하지 않아도 됩니다.", "i");
        }
        catch (Exception ex)
        {
            log($"Claude Code 로그인 상태를 지키지 못했습니다 — {ex.Message}", "w");
        }
    }

    // ── 사본 ────────────────────────────────────────────────────────────────

    private static void Save(Snap s)
    {
        try
        {
            // **바뀐 것이 없으면 안 쓴다.** 2분마다 부르는 자리라, 매번 쓰면 아무 일도
            // 없는 동안 디스크만 계속 친다.
            var now = Load();
            if (now is not null && now.Config == s.Config && now.Cred == s.Cred) return;

            var blob = ProtectedData.Protect(
                JsonSerializer.SerializeToUtf8Bytes(s), Salt, DataProtectionScope.CurrentUser);

            Directory.CreateDirectory(Settings.DataDir);
            var tmp = VaultPath + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, VaultPath, overwrite: true);
        }
        catch { /* 사본을 못 떴다고 지금 쓰는 데 지장은 없다 */ }
    }

    private static Snap? Load()
    {
        try
        {
            if (!File.Exists(VaultPath)) return null;
            var raw = ProtectedData.Unprotect(
                File.ReadAllBytes(VaultPath), Salt, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Snap>(raw);
        }
        catch
        {
            // 다른 계정·다른 PC 이거나 깨졌다. 남겨 두면 켤 때마다 같은 실패를 반복한다.
            try { File.Delete(VaultPath); } catch { }
            return null;
        }
    }

    // ── 파일 ────────────────────────────────────────────────────────────────

    private static string ReadOrEmpty(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch { return ""; }
    }

    /// <summary>
    /// **다 쓴 뒤에 갈아 끼운다.** 쓰다가 끊기면 반쯤 쓴 파일이 남고, 그것이 바로 지금
    /// 고치려는 그 사고다. 원본도 한 벌 옆에 남겨 둔다 — 우리 판단이 틀렸을 때의 되돌릴 길이다.
    /// </summary>
    private static void WriteAtomic(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);

        if (File.Exists(path))
            try { File.Copy(path, path + ".before-agentclihub", overwrite: true); } catch { }

        var tmp = path + ".agentclihub.tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
