namespace AgentCLIHub;

/// <summary>Native application dialogs follow the persisted UI language. CLI settings stay separate.</summary>
public static class UiText
{
    public static string Pick(string korean, string english) => Settings.Load().UiLanguage == "en" ? english : korean;
}
