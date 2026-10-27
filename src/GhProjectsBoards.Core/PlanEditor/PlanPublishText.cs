namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanPublishText
{
    // #258/#319: a value written on the Issue is shown in every Project holding that Issue. The review states
    // that once below the destination line, naming the column whose values it covers and how many tasks.
    internal static string IssueDestinationNote(string label, int taskCount)
        => $"「{label}」の値（{taskCount} タスク）は Issue 自体に書き込みます。この Issue を含むほかのプロジェクトにも同じ値が表示されます。";
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
