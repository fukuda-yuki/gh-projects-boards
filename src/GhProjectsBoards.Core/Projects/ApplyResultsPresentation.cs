namespace GhProjectsBoards.Core.Projects;

internal enum ApplyAttentionKind { Failed, Unsent, Review, Uncertain }

internal sealed record ApplyAttention(string BatchId, ScopedId Project, string RowId, FieldKey? Field,
    string Identity, string FieldName, ApplyAttentionKind Kind, string Reason, string? OperationId = null,
    string? CreationId = null, bool Withdrawn = false)
{
    public string StateText => Kind switch {
        ApplyAttentionKind.Failed => "失敗", ApplyAttentionKind.Unsent => "未送信",
        ApplyAttentionKind.Uncertain => "結果の確認が必要", _ => "要確認"
    };
    public string Description => StateText + "：" + Reason;
}

// Read-only presentation of durable evidence. Neither disclosure nor navigation authorizes work.
internal static class ApplyResultsPresentation
{
    public static ApplyAttentionKind? Kind(ApplyOperation operation)
    {
        if (operation.State == ApplyState.Succeeded) return null;
        if (operation.State is ApplyState.Unknown or ApplyState.Running
            || operation.Attempts.Any(a => a.State is ApplyState.Unknown or ApplyState.Running)) return ApplyAttentionKind.Uncertain;
        return operation.State switch {
            ApplyState.Superseded => null,
            ApplyState.Failed => ApplyAttentionKind.Failed,
            ApplyState.Blocked => ApplyAttentionKind.Review,
            _ => ApplyAttentionKind.Unsent
        };
    }

    public static ApplyAttention[] Attention(IEnumerable<ApplyBatch> history)
    {
        var batches = history.ToArray();
        var latestCreations = batches.SelectMany(b => b.Creations ?? []).GroupBy(c => c.LocalId).ToDictionary(g => g.Key, g => g.Last());
        var result = new List<ApplyAttention>();
        foreach (var batch in batches.Reverse())
        {
            foreach (var operation in batch.Operations)
                if (Kind(operation) is { } kind)
                    result.Add(new(batch.Id, batch.Project, operation.ItemId, operation.Key, Identity(operation),
                        operation.Key.Kind == "Title" ? "タイトル" : operation.FieldName, kind, Reason(operation.Reason),
                        operation.Id, Withdrawn: operation.State == ApplyState.Superseded));
            foreach (var c in batch.Creations ?? [])
            {
                if (latestCreations[c.LocalId].Id != c.Id) continue;
                var promoted = c.Completed && c.ItemId is not null;
                var row = promoted ? c.ItemId! : c.LocalId;
                var title = promoted && c.Verified is { } issue ? new FieldKey("Title", issue.Id) : new FieldKey("LocalTitle", c.LocalId);
                if (c.EarlierUncertain)
                    result.Add(new(batch.Id, batch.Project, row, title, c.Repository.Name + " / " + c.Title,
                        "新規Issue", ApplyAttentionKind.Uncertain, "以前の作成試行の結果が未確認です。", CreationId: c.Id));
                foreach (var retired in c.EarlierFields ?? [])
                    if (Kind(retired) == ApplyAttentionKind.Uncertain)
                        result.Add(new(batch.Id, batch.Project, row,
                            promoted ? retired.Key : new("LocalSelect", c.LocalId, batch.Project.NodeId, retired.Key.FieldId),
                            c.Repository.Name + " / " + c.Title, retired.FieldName, ApplyAttentionKind.Uncertain,
                            "以前に承認した設定の送信結果が未確認です。履歴で確認してください。", retired.Id, c.Id, true));
                if (c.Completed || !c.Authorized && !c.Dispatched) continue;
                var fields = (c.Fields ?? []).Where(f => Kind(f) is not null).ToArray();
                if (fields.Length == 0 || c.ItemId is null)
                    result.Add(new(batch.Id, batch.Project, row, title, c.Repository.Name + " / " + c.Title, "新規Issue",
                        c.Dispatched && c.Verified is null || c.MembershipDispatched && c.ItemId is null ? ApplyAttentionKind.Uncertain
                            : c.Verified is not null ? ApplyAttentionKind.Review : ApplyAttentionKind.Unsent,
                        c.Verified is not null ? "Issue確認済み・Project設定が未完了です。" : c.Dispatched ? "作成済みの可能性があります。履歴から結果を確認してください。" : "Issueはまだ作成していません。",
                        CreationId: c.Id, Withdrawn: !c.Authorized));
                foreach (var field in fields)
                    result.Add(new(batch.Id, batch.Project, row, new("LocalSelect", c.LocalId, batch.Project.NodeId, field.Key.FieldId),
                        c.Repository.Name + " / " + c.Title, field.FieldName, Kind(field)!.Value, Reason(field.Reason), field.Id, c.Id, !c.Authorized));
            }
        }
        return result.ToArray();
    }

    public static string Summary(IEnumerable<ApplyAttention> attention)
    {
        var entries = attention.ToArray();
        if (entries.Length == 0) return "対応が必要な項目はありません。";
        var parts = new List<string>();
        foreach (var (label, kinds) in new[] {
            ("失敗", new[] { ApplyAttentionKind.Failed }), ("未送信", new[] { ApplyAttentionKind.Unsent }),
            ("要確認", new[] { ApplyAttentionKind.Review, ApplyAttentionKind.Uncertain }) })
        {
            var matches = entries.Where(e => kinds.Contains(e.Kind)).ToArray();
            var fields = matches.Count(e => e.OperationId is not null);
            var issues = matches.Where(e => e.OperationId is null).Select(e => e.CreationId).Distinct().Count();
            if (fields > 0) parts.Add($"{label} {fields}フィールド");
            if (issues > 0) parts.Add($"{label} {issues}件の新規Issue");
        }
        return string.Join(" / ", parts);
    }

    public static string Identity(ApplyOperation operation) => operation.Identity.EndsWith(" / " + operation.IssueId, StringComparison.Ordinal)
        ? operation.Identity[..^(operation.IssueId.Length + 3)] : operation.Identity;

    private static string Reason(string reason) => reason switch {
        "PermissionDenied" => "更新する権限を確認してください。",
        "IdentityChanged" => "接続アカウントが変わりました。接続を確認してください。",
        "NotLoggedIn" or "AuthenticationExpired" => "接続設定で認証を確認してください。",
        "RateLimited" => "GitHubの制限により待機しています。",
        "Network" or "TimedOut" => "通信結果を確認できませんでした。",
        "NotFoundOrInaccessible" => "対象を確認できません。最新の状態を取得してください。",
        "Cancelled" => "処理を中止しました。",
        "InvalidResponse" or "GraphQl" => "GitHubの応答を確認できませんでした。",
        _ => reason
    };
}
