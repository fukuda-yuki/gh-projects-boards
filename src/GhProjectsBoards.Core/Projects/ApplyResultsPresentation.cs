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

internal sealed record ApplyExecutionOutcome(ApplyOperation[] VerifiedFields, ApplyAttention[] Remaining, int CompletedCreations)
{
    public bool HasVerifiedWork => VerifiedFields.Length > 0 || CompletedCreations > 0;
    public string Title => HasVerifiedWork ? "今回の反映は一部完了しました" : "今回の反映は完了していません";
    public string Summary => string.Join(" / ", new[] {
        VerifiedFields.Length > 0 ? $"反映確認済み {VerifiedFields.Length}フィールド" : null,
        CompletedCreations > 0 ? $"完了 {CompletedCreations}件の新規Issue" : null,
        Remaining.Length > 0 ? ApplyResultsPresentation.Summary(Remaining) : null
    }.Where(part => part is not null));
}

internal sealed record ApplyOutcomeTarget(string? OperationId, string? CreationId);
internal sealed record CreationStagePresentation(string Issue, string Membership, string[] Fields);

// Read-only presentation of durable evidence. Neither disclosure nor navigation authorizes work.
internal static class ApplyResultsPresentation
{
    public static ApplyOutcomeTarget? OutcomeTarget(ApplyBatch batch, IEnumerable<ApplyAttention> attention)
    {
        foreach (var entry in attention.Where(entry => entry.BatchId == batch.Id && entry.Project == batch.Project))
        {
            if (entry.CreationId is { } creationId && (batch.Creations ?? []).SingleOrDefault(creation => creation.Id == creationId) is { } creation)
                return new((creation.EarlierFields ?? []).Any(field => field.Id == entry.OperationId) ? entry.OperationId : null, creationId);
            if (entry.OperationId is { } operationId && batch.Operations.Any(operation => operation.Id == operationId))
                return new(operationId, null);
        }
        return batch.Operations.FirstOrDefault() is { } first ? new(first.Id, null)
            : (batch.Creations ?? []).FirstOrDefault() is { } firstCreation ? new(null, firstCreation.Id) : null;
    }

    public static CreationStagePresentation CreationStages(ApplyBatch batch, CreationOperation creation, IReadOnlyList<ApplyBatch>? history = null)
    {
        var knowledge = CreationKnowledgePresentation.Describe(creation);
        var issue = creation.Verified is not null ? "Issue：独立確認済み"
            : knowledge.Knowledge == CreationKnowledge.IdentityUnverified ? (creation.Dispatched ? "Issue作成：送信済み・Issueの独立確認待ち" : "Issue：独立確認待ち")
            : creation.Dispatched ? "Issue作成：送信済み・結果未確認" : "Issue作成：未送信";
        var membership = creation.ItemId is not null ? $"{batch.ProjectName}への所属：確認済み"
            : creation.MembershipDispatched ? $"{batch.ProjectName}への追加：送信済み・結果未確認"
            : creation.ReceivedItemId is not null ? $"{batch.ProjectName}への所属：確認待ち"
            : $"{batch.ProjectName}への追加：未実行" + (creation.Verified is null ? "（Issueの確認待ち）" : "");
        string Stage(string kind, string fieldId, LocalValue intended)
        {
            if (kind == "Dependency") fieldId = (history ?? [batch]).Where(candidate => candidate.Project.Scope == batch.Project.Scope)
                .SelectMany(candidate => candidate.Creations ?? []).LastOrDefault(candidate => candidate.LocalId == fieldId && candidate.Verified is not null)?.Verified?.Id ?? fieldId;
            var field = (creation.Fields ?? []).SingleOrDefault(field => field.Key.Kind == kind && field.Key.FieldId == fieldId);
            if (field is null) return creation.Fields is null ? "設定予定・未実行" : "設定結果は未確認";
            if (field.Intended != intended) return "設定結果は未確認";
            if (IsVerified(batch, field)) return "設定値を確認済み" + (field.Attempts.Length == 0 ? "（この設定の送信なし）" : "");
            if (ApplyJournal.HasUnresolvedDispatch(field)) return "送信済み・結果未確認";
            return field.State switch {
                ApplyState.Failed => "失敗・設定未完了", ApplyState.Waiting => "待機中・設定未完了",
                ApplyState.Blocked => "要確認・設定未完了", ApplyState.Superseded => "承認撤回・設定未完了",
                _ => "設定予定・未送信"
            };
        }
        var fields = (creation.SetupIntents ?? creation.Selects.ToArray()).Where(select => select.OptionId is not null || select.ExplicitClear)
            .Select(select => $"{select.FieldName} → {(select.ExplicitClear ? "空にする" : select.OptionName ?? "選択肢 ID " + select.OptionId)}：{Stage("Select", select.FieldId, new(select.OptionId, select.ExplicitClear))}")
            .Concat((creation.SetupPlanningIntents ?? creation.PlanningIntents ?? [])
                .Select(intent => $"{intent.FieldName} → {(intent.Value.Clear ? "空にする" : intent.Kind == "Dependency" ? "依存関係あり" : intent.Value.Value)}：{Stage(intent.Kind, intent.FieldId, intent.Value)}")).ToArray();
        return new(issue, membership, fields);
    }

    public static ApplyOperation[] EarlierVerifiedWork(IReadOnlyList<ApplyBatch> history, ApplyBatch batch, ApplyOperation operation)
    {
        var earlier = history.TakeWhile(candidate => candidate.Id != batch.Id).LastOrDefault(candidate => candidate.Project == batch.Project
            && candidate.Operations.Any(previous => previous.Key == operation.Key && previous.IssueId == operation.IssueId
                && previous.State == ApplyState.Superseded && previous.Attempts.Any(attempt => attempt.State == ApplyState.Failed)
                && !ApplyJournal.HasUnresolvedDispatch(previous)));
        return earlier?.Operations.Where(previous => previous.IssueId == operation.IssueId && IsVerified(earlier, previous)
            && !batch.Operations.Any(current => current.Key == previous.Key)).ToArray() ?? [];
    }

    public static bool ResolvedByLaterExecution(IReadOnlyList<ApplyBatch> history, ApplyBatch batch, ApplyOperation operation) =>
        operation.State == ApplyState.Superseded && !ApplyJournal.HasUnresolvedDispatch(operation)
        && history.SkipWhile(candidate => candidate.Id != batch.Id).Skip(1).Any(candidate => candidate.Project == batch.Project
            && candidate.Operations.Any(later => later.Key == operation.Key && later.IssueId == operation.IssueId
                && later.Intended == operation.Intended && IsVerified(candidate, later)));

    public static bool RequiresFreshReview(ApplyBatch batch)
    {
        if ((batch.Creations?.Length ?? 0) != 0) return false;
        var remaining = batch.Operations.Where(operation => operation.State is not (ApplyState.Succeeded or ApplyState.Superseded)).ToArray();
        return remaining.Length > 0 && remaining.All(operation => !ApplyJournal.HasUnresolvedDispatch(operation)
            && (operation.State == ApplyState.Failed || operation.State == ApplyState.Blocked
                && operation.Attempts.Length > 0 && operation.Attempts.All(attempt => attempt.State == ApplyState.Failed)));
    }

    public static ApplyExecutionOutcome ExecutionOutcome(ApplyBatch batch, IEnumerable<ApplyAttention> attention) => new(
        batch.Operations.Concat((batch.Creations ?? []).SelectMany(c => c.Fields ?? []))
            .Where(operation => IsVerified(batch, operation)).DistinctBy(operation => operation.Id).ToArray(),
        attention.Where(entry => entry.BatchId == batch.Id).ToArray(),
        (batch.Creations ?? []).Count(creation => creation.Completed));

    public static bool IsVerified(ApplyBatch batch, ApplyOperation operation) => operation.State == ApplyState.Succeeded
        && operation.Verification is { Reason: null, Availability: ValueAvailability.Present or ValueAvailability.Empty } observation
        && observation.Project == batch.Project && observation.Value == operation.Intended.Value
        && (operation.Intended.Clear ? observation.Availability == ValueAvailability.Empty && observation.Value is null
            : observation.Availability == ValueAvailability.Present && observation.Value is not null);

    public static string FieldName(ApplyOperation operation) => operation.Key.Kind == "Title" ? "タイトル" : operation.FieldName;

    public static string VerifiedValue(ApplyOperation operation) => operation.Verification is { } observation
        ? ObservedValue(operation.Key.Kind, observation.Value, observation.Options) : "未確認";

    public static string HistoricalValue(ApplyOperation operation, string? value, ScopedId project, DraftField? field = null)
    {
        if (operation.Key.Kind != "Select") return ObservedValue(operation.Key.Kind, value, []);
        if (value is null) return "空";
        bool Usable(FieldObservation? observation) => project.NodeId == operation.Key.ProjectId
            && observation is { Reason: null, Availability: ValueAvailability.Present or ValueAvailability.Empty }
            && observation.Project == project;
        var current = field?.Key == operation.Key && field.SourceProject == project ? field.Observation : null;
        if (Usable(current)) return current!.Options.Any(option => option.Id == value)
            ? ObservedValue("Select", value, current.Options) + "（現在確認できる名称）"
            : $"選択肢 ID {value}（名前は未確認）";
        return Usable(operation.Verification) && operation.Verification!.Options.Any(option => option.Id == value)
            ? ObservedValue("Select", value, operation.Verification.Options) + "（読み戻し確認時の名称）"
            : $"選択肢 ID {value}（名前は未確認）";
    }

    private static string ObservedValue(string kind, string? value, SelectOption[] options)
    {
        if (value is null) return "空";
        if (kind == "Dependency") return "依存関係あり";
        if (kind != "Select") return value;
        var option = options.SingleOrDefault(option => option.Id == value);
        if (option is null) return $"選択肢 ID {value}（名前は未確認）";
        return options.Count(other => other.Name == option.Name) > 1 ? $"{option.Name}（ID {value}）" : option.Name;
    }

    public static bool HasApprovedCreationFields(CreationOperation creation) =>
        (creation.SetupIntents ?? creation.Selects.ToArray()).Any(select => select.OptionId is not null || select.ExplicitClear)
        || (creation.SetupPlanningIntents ?? creation.PlanningIntents ?? []).Length > 0
        || (creation.Fields?.Length ?? 0) > 0;

    public static string BindingCompletionText(bool hasFields) => hasFields
        ? "Issueの確認とProject設定が完了しました。" : "Issueの確認とProjectへの追加が完了しました。";

    public static string CompletionAnnouncement(ApplyBatch batch) => batch.Operations.Length == 0
        && batch.Creations is { Length: > 0 } creations && creations.All(CreationJournal.IsCompletedOriginalBinding)
            ? BindingCompletionText(creations.Any(HasApprovedCreationFields)) : "GitHubへの反映が完了しました。";

    public static ApplyAttentionKind? Kind(ApplyOperation operation)
    {
        if (operation.State == ApplyState.Succeeded) return null;
        if (ApplyJournal.HasUnresolvedDispatch(operation)) return ApplyAttentionKind.Uncertain;
        return operation.State switch {
            ApplyState.Superseded => null,
            ApplyState.Failed => ApplyAttentionKind.Failed,
            ApplyState.Blocked => ApplyAttentionKind.Review,
            _ => ApplyAttentionKind.Unsent
        };
    }

    public static ApplyAttention[] Attention(EditingWorkspace workspace) => Attention(workspace.Journal, workspace.HistoricalDispositions);

    public static ApplyAttention[] Attention(IEnumerable<ApplyBatch> history, IReadOnlyList<HistoricalFieldDecision> decisions)
    {
        var batches = history.ToArray();
        var latestCreations = batches.SelectMany(b => b.Creations ?? []).GroupBy(c => c.LocalId).ToDictionary(g => g.Key, g => g.Last());
        var result = new List<ApplyAttention>();
        foreach (var batch in batches.Reverse())
        {
            foreach (var operation in batch.Operations)
                if (Kind(operation) is { } kind && !HistoricalFieldHandling.IsSettled(batches, decisions, new(batch.Id, null, operation.Id)))
                    result.Add(new(batch.Id, batch.Project, operation.ItemId, operation.Key, Identity(operation),
                        operation.Key.Kind == "Title" ? "タイトル" : operation.FieldName, kind, AttentionReason(operation),
                        operation.Id, Withdrawn: operation.State == ApplyState.Superseded));
            foreach (var c in batch.Creations ?? [])
            {
                if (latestCreations[c.LocalId].Id != c.Id) continue;
                var promoted = c.Completed && c.ItemId is not null;
                var row = promoted ? c.ItemId! : c.LocalId;
                var title = promoted && c.Verified is { } issue ? new FieldKey("Title", issue.Id) : new FieldKey("LocalTitle", c.LocalId, batch.Project.NodeId);
                // Binding the original attempt completes current work without proving the lost request succeeded.
                // A separate retry still carries possible duplicate creation and remains actionable.
                if (c.EarlierUncertain && !CreationJournal.IsCompletedOriginalBinding(c)
                    && !(c.UserBound && c.PreviousAttempt is null && c.Verified is not null))
                    result.Add(new(batch.Id, batch.Project, row, title, c.Repository.Name + " / " + c.Title,
                        "新規Issue", ApplyAttentionKind.Uncertain, "以前の作成試行の結果が未確認です。", CreationId: c.Id));
                foreach (var retired in c.EarlierFields ?? [])
                    if (Kind(retired) == ApplyAttentionKind.Uncertain && !HistoricalFieldHandling.IsSettled(batches, decisions, new(batch.Id, c.Id, retired.Id)))
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
                        c.Repository.Name + " / " + c.Title, field.FieldName, Kind(field)!.Value,
                        c.Verified is not null && c.ItemId is not null && field.State == ApplyState.Pending && field.Attempts.Length == 0
                            ? "設定内容の再確認が必要です。" : AttentionReason(field), field.Id, c.Id, !c.Authorized));
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

    public static string OutcomeReason(ApplyOperation operation)
    {
        if (operation.State == ApplyState.Superseded && operation.Reason == ApplyJournal.LegacySupersessionReason)
            return "以前の承認は終了しています。";
        if (operation.State == ApplyState.Failed && !ApplyJournal.HasUnresolvedDispatch(operation) && operation.Reason == "PermissionDenied")
            return $"{FieldName(operation)}更新が拒否され、未反映です。変更内容はローカルに保持しています。";
        // Only this legacy summary contradicts definite failed-attempt evidence; the saved diagnostic stays intact.
        if (operation.State == ApplyState.Blocked
            && operation.Reason == "以前の送信結果が不確定です。明示的な再照合・新規レビューが必要です。"
            && operation.Attempts.Length > 0 && operation.Attempts.All(attempt => attempt.State == ApplyState.Failed))
            return "前回の送信は失敗しました。残った変更を再確認し、新しいレビューで承認してください。";
        return operation.Reason;
    }

    private static string AttentionReason(ApplyOperation operation) => operation.State == ApplyState.Failed
        && operation.Reason == "PermissionDenied" && !ApplyJournal.HasUnresolvedDispatch(operation)
            ? "更新が拒否されました。変更はローカルに保持しています。" : Reason(OutcomeReason(operation));

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
