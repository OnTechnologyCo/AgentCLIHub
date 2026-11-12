using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AgentCLIHub;

/// <summary>
/// 텔레그램 원격 설정. <b>토큰은 여기 없다</b> — settings.json 은 손편집·백업·공유되는 파일이라
/// 봇 토큰(= 이 PC 의 CLI 를 부릴 열쇠)을 평문으로 두지 않는다. 토큰은 <see cref="TelegramVault"/>.
/// </summary>
public sealed class TelegramSettings
{
    /// <summary>켜 두면 앱이 떠 있는 동안 봇 메시지를 받는다. 기본은 꺼짐.</summary>
    public bool Enabled { get; set; }

    /// <summary>페어링을 마친 대화(텔레그램 chat id). **여기 없는 대화의 말은 실행하지 않는다.**</summary>
    public List<TelegramChat> Chats { get; set; } = [];
}

public sealed class TelegramChat
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime AddedUtc { get; set; }
}

/// <summary>
/// 봇 토큰 보관. DPAPI(<see cref="DataProtectionScope.CurrentUser"/>)로 감싸 같은 Windows 계정에서만 풀린다 —
/// Claude 토큰(<see cref="Accounts"/>)과 같은 방식이다.
/// </summary>
public static class TelegramVault
{
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("AgentCLIHub.Telegram.v1");

    public static string FilePath => Path.Combine(Settings.DataDir, "telegram-token.dat");

    public static void Save(string token)
    {
        if (!TelegramApi.LooksLikeToken(token)) throw new ArgumentException("봇 토큰 모양이 아닙니다.");
        Directory.CreateDirectory(Settings.DataDir);
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Salt, DataProtectionScope.CurrentUser);
        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Salt, DataProtectionScope.CurrentUser);
            var token = Encoding.UTF8.GetString(raw);
            return TelegramApi.LooksLikeToken(token) ? token : null;
        }
        catch { return null; }   // 다른 계정·PC 에서 옮겨 온 파일이면 못 푼다 — 다시 넣으라고 보인다
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}
