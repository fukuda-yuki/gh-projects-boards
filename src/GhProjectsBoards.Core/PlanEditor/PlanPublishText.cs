namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanPublishText
{
    internal static string Failure(PlanPublishFailure failure) => failure.Reason switch
    {
        "VerificationMismatch" when failure.Field == PlanField.NewTask => "プロジェクトへの追加を確認できません",
        "VerificationMismatch" => "GitHubへの反映を確認できません",
        "NotDispatched" => "まだGitHubへ送信されていません",
        _ when failure.Reason.IndexOf(": ", StringComparison.Ordinal) is var separator && separator >= 0 => failure.Reason[(separator + 2)..],
        _ when failure.Reason.Any(c => c > 127 || char.IsWhiteSpace(c)) => failure.Reason,
        _ => "GitHubへの発行に失敗しました。再発行してください。"
    };
}
