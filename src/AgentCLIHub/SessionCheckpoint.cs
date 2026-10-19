namespace AgentCLIHub;

/// <summary>탭에 연결된 대화를 저장한다. 계정·폴더가 같아도 다른 탭의 최신 대화로 바꾸지 않는다.</summary>
public sealed record SessionCheckpoint(string? Id, string? TranscriptPath, bool? Fresh)
{
    public static SessionCheckpoint From(ResumeMode mode) => new(mode.SessionId, mode.TranscriptPath,
        mode.SessionId is not null ? false : mode.Fresh ? true : null);

    public static ResumeMode Restore(TabState tab) => !string.IsNullOrEmpty(tab.SessionId)
        ? ResumeMode.Session(tab.SessionId, tab.TranscriptPath)
        : !string.IsNullOrEmpty(tab.HandoffPath) ? ResumeMode.Handoff(tab.HandoffPath)
        : tab.Fresh == true ? ResumeMode.New : ResumeMode.Last;
}
