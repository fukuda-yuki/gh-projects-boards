namespace GhProjectsBoards.Core.Projects;

internal enum CreationKnowledge { Unsent, OutcomeUnconfirmed, IdentityUnverified, SetupIncomplete, Complete }
internal sealed record CreationKnowledgeView(CreationKnowledge Knowledge, string RowLabel, string Description);

internal static class CreationKnowledgePresentation
{
    // Local row storage is not evidence of remote absence. Current verification
    // takes precedence over historical uncertainty, which remains in the journal.
    public static CreationKnowledgeView Describe(CreationOperation? operation)
    {
        if (operation?.Verified is not null)
        {
            if (operation.Completed && operation.ItemId is not null && operation.Fields is not null
                && operation.Fields.All(field => field.State == ApplyState.Succeeded))
                return new(CreationKnowledge.Complete, "設定確認済み", "Issue・Project所属・承認した設定を確認済みです。");
            if (operation.ItemId is null)
                return new(CreationKnowledge.SetupIncomplete, "Project設定が未完了", operation.MembershipDispatched || operation.ReceivedItemId is not null
                    ? "Issueは確認済みです。Projectへの追加結果を確認する必要があります。"
                    : "Issueは確認済みです。Projectへの追加・所属確認が必要です。");
            if (operation.Fields is not null && operation.Fields.All(field => field.State == ApplyState.Succeeded))
                return new(CreationKnowledge.SetupIncomplete, "設定完了の確認待ち", "IssueとProject所属・承認した値は確認済みです。今回の設定完了はまだ確認できていません。");
            return new(CreationKnowledge.SetupIncomplete, "フィールド設定が未完了", "IssueとProject所属は確認済みです。承認したフィールド設定の確認が残っています。");
        }
        if (operation?.Received is not null || operation?.ReceivedId is not null)
            return new(CreationKnowledge.IdentityUnverified, "Issueの確認待ち", "Issueの識別情報を保持していますが、存在と宛先の独立確認は未完了です。");
        if (operation is { Dispatched: true } or { EarlierUncertain: true })
            return new(CreationKnowledge.OutcomeUnconfirmed, "作成結果未確認", "既に作成されている可能性があります。追加の作成前に、履歴から結果を確認してください。");
        return new(CreationKnowledge.Unsent, "新規・未送信", "このアプリからIssue作成を送信していません。");
    }
}
