using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentCLIHub;

/// <summary>agy의 원래 로그인 흐름을 계정별 사용자 홈에서 실행한다. Windows 공용 자격 증명은 사용하지 않는다.</summary>
public static class AntigravityAccounts
{
    public static string ProfileOf(string id) => string.IsNullOrEmpty(id)
        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Path.Combine(Accounts.DirOf(id), "profile");
    public static string HomeOf(string id) => string.IsNullOrEmpty(id)
        ? CliProfile.AntigravityHome : Path.Combine(ProfileOf(id), ".gemini", "antigravity-cli");
    public static string TokenFile(string profile) => Path.Combine(profile, ".gemini", "jetski-standalone-oauth-token");

    public static Dictionary<string, string?> EnvironmentFor(string profile)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (key.StartsWith("AGY_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("ANTIGRAVITY_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("GEMINI_", StringComparison.OrdinalIgnoreCase)) values[key] = null;
        }
        foreach (var key in new[] { "GOOGLE_API_KEY", "GOOGLE_APPLICATION_CREDENTIALS", "GOOGLE_CLOUD_PROJECT",
                                   "GOOGLE_CLOUD_QUOTA_PROJECT", "GOOGLE_CLOUD_LOCATION", "CLOUD_CODE_URL" }) values[key] = null;
        values["USERPROFILE"] = Path.GetFullPath(profile);
        values["HOME"] = Path.GetFullPath(profile);
        values["APPDATA"] = Path.Combine(Path.GetFullPath(profile), "AppData", "Roaming");
        values["LOCALAPPDATA"] = Path.Combine(Path.GetFullPath(profile), "AppData", "Local");
        values["XDG_CONFIG_HOME"] = Path.Combine(Path.GetFullPath(profile), ".config");
        values["XDG_DATA_HOME"] = Path.Combine(Path.GetFullPath(profile), ".local", "share");
        values["XDG_CACHE_HOME"] = Path.Combine(Path.GetFullPath(profile), ".cache");
        // agy 1.4.2의 sshDetector가 네이티브 파일 저장소를 선택하도록 한다.
        // 인증 자체는 agy의 Google OAuth 그대로이며 기본 Windows Credential Manager 항목을 덮어쓰지 않는다.
        values["SSH_CONNECTION"] = "127.0.0.1 1234 127.0.0.1 22";
        return values;
    }

    public static (bool LoggedIn, string Email) State(string profile)
    {
        try
        {
            using var stream = new FileStream(TokenFile(profile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 256 * 1024) return (false, "");
            var json = JsonNode.Parse(stream);
            // 네이티브 OAuth token의 snake_case와 Go 필드 이름을 모두 읽는다. 값은 외부로 보내지 않는다.
            var refresh = Field(json, "refreshtoken");
            if (string.IsNullOrWhiteSpace(refresh)) return (false, "");
            var email = Field(json, "email");
            var idToken = Field(json, "idtoken");
            if (email.Length == 0 && idToken.Split('.') is { Length: >= 2 } parts)
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
                try { email = Field(JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))), "email"); } catch { }
            }
            return (true, email);
        }
        catch { return (false, ""); }
    }

    private static string Field(JsonNode? node, string name, int depth = 0)
    {
        if (depth > 4 || node is not JsonObject obj) return "";
        foreach (var (key, value) in obj)
            if (key.Replace("_", "").Equals(name, StringComparison.OrdinalIgnoreCase) && value is JsonValue v && v.TryGetValue<string>(out var text))
                return text;
        foreach (var (_, value) in obj)
            if (Field(value, name, depth + 1) is { Length: > 0 } found) return found;
        return "";
    }
}
