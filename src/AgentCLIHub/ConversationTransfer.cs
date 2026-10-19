using System;
using System.IO;

namespace AgentCLIHub;

/// <summary>열린 탭과 지난 대화가 같은 방식으로 계정을 전환하도록 준비한다. CLI 는 시작하지 않는다.</summary>
public static class ConversationTransfer
{
    public static (ResumeMode Mode, string Note) Prepare(CliProfile source, SessionRef? session, Transcript? transcript,
        string cwd, CliProfile target, string targetHome, string from)
    {
        if (transcript is null)
            return (ResumeMode.New, "넘길 대화 기록이 아직 없어 새 대화로 엽니다.");
        if (source == CliProfile.Claude && target == CliProfile.Claude && session is not null)
        {
            if (!File.Exists(session.Path)) throw new FileNotFoundException("이어갈 Claude 대화 파일이 없습니다.", session.Path);
            return (ResumeMode.Session(session.Id, Path.GetFullPath(session.Path)), "같은 대화를 그대로 이어갑니다.");
        }
        if (!transcript.HasConversation)
            return (ResumeMode.New, "넘길 대화가 비어 있어 새 대화로 엽니다.");
        if (target.Kind == CliKind.Antigravity)
        {
            var handoff = AntigravityTransfer.Write(transcript, cwd, from);
            return (ResumeMode.Handoff(handoff), $"이전 대화를 인계 파일로 전달합니다(사용자 말 {transcript.UserTurns}개) — {handoff}");
        }
        var preface = TranscriptWriter.Preface(transcript, from);
        var (id, path) = target.Kind == CliKind.Claude
            ? TranscriptWriter.ToClaude(transcript, cwd, preface, targetHome)
            : TranscriptWriter.ToCodex(transcript, cwd, targetHome, preface);
        return (ResumeMode.Session(id, target.Kind == CliKind.Claude ? path : null),
            $"대화를 옮겨 적었습니다(사용자 말 {transcript.UserTurns}개) — {path}");
    }
}
