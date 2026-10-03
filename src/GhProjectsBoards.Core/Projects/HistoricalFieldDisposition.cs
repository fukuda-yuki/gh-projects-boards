namespace GhProjectsBoards.Core.Projects;

internal sealed record HistoricalFieldTarget(string BatchId, string? CreationId, string OperationId);
internal enum HistoricalFieldEvidenceKind { CurrentValue, ProjectFieldAbsent }
internal enum HistoricalFieldDecisionKind { AcceptCurrent, ContinueChange, FieldNotApplicable }
internal sealed record HistoricalFieldFollowUp(string BatchId, string OperationId);
internal sealed record HistoricalFieldObservation(string Id, ScopedId Project, string ItemId, string IssueId,
    FieldKey Key, DateTimeOffset At, HistoricalFieldEvidenceKind Kind, string FieldName, string? DataType,
    FieldObservation? Current, string[] ProjectFieldIds);
internal sealed record HistoricalFieldDecision(string Id, HistoricalFieldTarget Target, HistoricalFieldDecisionKind Kind,
    HistoricalFieldObservation Observation, DateTimeOffset At, long Revision, HistoricalFieldFollowUp? FollowUp = null);
internal sealed record HistoricalFieldReview(HistoricalFieldTarget Target, ApplyBatch Batch, ApplyOperation Operation,
    HistoricalFieldObservation Observation, long Revision, int ConnectionRevision, DraftField? LocalField);
internal sealed record HistoricalFieldDecisionResult(HistoricalFieldDecision? Decision, bool AllLocalWorkSaved,
    string? Problem, HistoricalFieldReview? Review);

internal sealed record HistoricalFieldSource(ApplyBatch Batch, ApplyOperation Operation);

internal static class HistoricalFieldHandling
{
    public static HistoricalFieldSource? Resolve(IReadOnlyList<ApplyBatch> journal, HistoricalFieldTarget target)
    {
        var batches = journal.Where(b => b.Id == target.BatchId).ToArray();
        if (batches.Length != 1) return null;
        var batch = batches[0];
        IEnumerable<ApplyOperation> operations;
        if (target.CreationId is null)
            operations = batch.Operations.Where(o => o.State == ApplyState.Superseded);
        else
        {
            var matches = (batch.Creations ?? []).Where(c => c.Id == target.CreationId).ToArray();
            if (matches.Length != 1) return null;
            var creation = matches[0];
            if (!creation.Completed || creation.Verified is null || creation.ItemId is null || creation.Fields is null
                || creation.Fields.Any(f => f.State != ApplyState.Succeeded)
                || creation.EarlierUncertain && !CreationJournal.IsCompletedOriginalBinding(creation)
                || journal.SelectMany(b => b.Creations ?? []).Any(c => c.LocalId == creation.LocalId && c.PreviousAttempt is not null)) return null;
            operations = creation.EarlierFields ?? [];
        }
        var fields = operations.Where(o => o.Id == target.OperationId).ToArray();
        return fields.Length == 1 && ApplyJournal.HasUnresolvedDispatch(fields[0]) ? new(batch, fields[0]) : null;
    }

    public static bool IsSettled(IReadOnlyList<ApplyBatch> journal, IReadOnlyList<HistoricalFieldDecision> decisions,
        HistoricalFieldTarget target)
    {
        if (Resolve(journal, target) is null) return false;
        var decision = decisions.LastOrDefault(d => d.Target == target);
        if (decision is null) return false;
        if (decision.Kind is HistoricalFieldDecisionKind.AcceptCurrent or HistoricalFieldDecisionKind.FieldNotApplicable) return true;
        if (decision.FollowUp is not { } link) return false;
        return journal.SingleOrDefault(b => b.Id == link.BatchId)?.Operations
            .Any(o => o.Id == link.OperationId && o.State == ApplyState.Succeeded && o.Verification is not null) == true;
    }

    public static bool SameEvidence(HistoricalFieldObservation left, HistoricalFieldObservation right) =>
        left.Project == right.Project && left.ItemId == right.ItemId && left.IssueId == right.IssueId && left.Key == right.Key
        && left.Kind == right.Kind && left.FieldName == right.FieldName && left.DataType == right.DataType
        && left.Current?.Value == right.Current?.Value && left.Current?.Availability == right.Current?.Availability
        && (left.Current?.Options ?? []).SequenceEqual(right.Current?.Options ?? []);

    internal static void ValidateObservation(HistoricalFieldSource source, HistoricalFieldObservation value)
    {
        var operation = source.Operation;
        if (value is null || string.IsNullOrWhiteSpace(value.Id) || value.Project != source.Batch.Project
            || value.ItemId != operation.ItemId || value.IssueId != operation.IssueId || value.Key != operation.Key
            || value.At == default || !Enum.IsDefined(value.Kind) || string.IsNullOrWhiteSpace(value.FieldName)
            || value.ProjectFieldIds is null || value.ProjectFieldIds.Any(string.IsNullOrWhiteSpace)
            || value.ProjectFieldIds.Distinct().Count() != value.ProjectFieldIds.Length)
            throw new InvalidDataException("Invalid historical field observation identity.");
        if (value.At < source.Batch.ReviewedAt || operation.Attempts.Any(a => value.At < a.At)
            || operation.Verification is { } earlier && value.At < earlier.At)
            throw new InvalidDataException("Historical observation predates the handling being reviewed.");
        if (value.Kind == HistoricalFieldEvidenceKind.ProjectFieldAbsent)
        {
            if (operation.Key.Kind is not ("Select" or "Number" or "Date") || value.Current is not null || value.DataType is not null
                || value.ProjectFieldIds.Contains(operation.Key.FieldId)) throw new InvalidDataException("Missing authoritative field absence.");
            return;
        }
        var current = value.Current;
        if (current is null || current.Project != value.Project || current.At != value.At || current.Id != value.Id
            || current.Reason is not null || current.Options is null || current.Options.Any(o => o is null || string.IsNullOrWhiteSpace(o.Id))
            || current.Options.Select(o => o.Id).Distinct().Count() != current.Options.Length
            || current.Availability is not (ValueAvailability.Present or ValueAvailability.Empty)
            || (current.Availability == ValueAvailability.Empty ? current.Value is not null : string.IsNullOrWhiteSpace(current.Value))
            || operation.Key.Kind == "Title" && current.Availability != ValueAvailability.Present
            || operation.Key.Kind == "Dependency" && current.Value is not (null or "present")
            || operation.Key.Kind is "Select" or "Number" or "Date" && (!value.ProjectFieldIds.Contains(operation.Key.FieldId)
                || value.DataType != PlanningScalars.DataType(operation.Key.Kind)
                || operation.Key.Kind == "Select" && current.Value is not null && !current.Options.Any(o => o.Id == current.Value)
                || operation.Key.Kind is "Number" or "Date" && !PlanningScalars.RemoteValid(operation.Key.Kind, current.Value)))
            throw new InvalidDataException("Invalid known historical field value.");
    }

    public static void Validate(DraftRecord record)
    {
        var decisions = record.HistoricalDispositions ?? [];
        if (record.Version >= 14 && record.HistoricalDispositions is null || record.Version < 14 && decisions.Length > 0)
            throw new InvalidDataException("Invalid historical disposition schema.");
        var journal = record.Journal ?? [];
        var ids = new HashSet<string>(); var links = new HashSet<HistoricalFieldFollowUp>();
        var terminal = new HashSet<HistoricalFieldTarget>(); long previousRevision = -1;
        var precedingSuccess = new Dictionary<HistoricalFieldTarget, DateTimeOffset>();
        foreach (var decision in decisions)
        {
            if (decision is null || string.IsNullOrWhiteSpace(decision.Id) || !ids.Add(decision.Id) || decision.Target is null
                || !Enum.IsDefined(decision.Kind) || decision.At == default || decision.Revision <= previousRevision
                || decision.Revision > record.Revision || terminal.Contains(decision.Target)) throw new InvalidDataException("Invalid historical disposition.");
            // A successor that completed only after this choice cannot retroactively
            // settle an earlier choice in place of the user's later one.
            if (precedingSuccess.Remove(decision.Target, out var settledAt) && settledAt <= decision.At)
                throw new InvalidDataException("Historical handling was already completed before the later choice.");
            previousRevision = decision.Revision;
            var source = Resolve(journal, decision.Target) ?? throw new InvalidDataException("Historical disposition target is not eligible.");
            ValidateObservation(source, decision.Observation);
            if (decision.Observation.At > decision.At || source.Batch.ReviewedRevision >= decision.Revision
                || (decision.Kind == HistoricalFieldDecisionKind.FieldNotApplicable
                    ? decision.Observation.Kind != HistoricalFieldEvidenceKind.ProjectFieldAbsent
                    : decision.Observation.Kind != HistoricalFieldEvidenceKind.CurrentValue))
                throw new InvalidDataException("Historical decision lacks its required evidence.");
            if (decision.Kind != HistoricalFieldDecisionKind.ContinueChange)
            {
                if (decision.FollowUp is not null) throw new InvalidDataException("Terminal decision cannot authorize follow-up.");
                terminal.Add(decision.Target);
            }
            if (decision.FollowUp is not { } link) continue;
            var batch = journal.SingleOrDefault(b => b.Id == link.BatchId);
            var operation = batch?.Operations.SingleOrDefault(o => o.Id == link.OperationId);
            if (!links.Add(link) || batch is null || operation is null || batch.Id == source.Batch.Id
                || batch.Project != source.Batch.Project || batch.ReviewedRevision < decision.Revision
                || batch.ReviewedAt < decision.At || operation.Key != source.Operation.Key
                || operation.ItemId != source.Operation.ItemId || operation.IssueId != source.Operation.IssueId)
                throw new InvalidDataException("Historical follow-up does not match the explicitly chosen target.");
            if (operation.State == ApplyState.Succeeded && operation.Verification is { } verification)
            {
                if (verification.At < batch.ReviewedAt || operation.Attempts.Any(a => verification.At < a.At))
                    throw new InvalidDataException("Historical follow-up verification predates its approval or dispatch.");
                precedingSuccess[decision.Target] = verification.At;
            }
        }
    }
}

internal sealed partial class EditingWorkspace
{
    private readonly List<HistoricalFieldDecision> historicalDispositions = [];
    public IReadOnlyList<HistoricalFieldDecision> HistoricalDispositions => historicalDispositions;
    public string? HistoricalFollowUpProblem(string decisionId, ScopedId project)
    {
        var decision = historicalDispositions.SingleOrDefault(d => d.Id == decisionId);
        if (decision is null || decision.Kind != HistoricalFieldDecisionKind.ContinueChange || decision.FollowUp is not null
            || historicalDispositions.LastOrDefault(d => d.Target == decision.Target)?.Id != decision.Id
            || HistoricalFieldHandling.IsSettled(journal, historicalDispositions, decision.Target)
            || HistoricalFieldHandling.Resolve(journal, decision.Target) is not { } source || source.Batch.Project != project)
            return "変更を続ける履歴と対象のProjectを再確認してください。以前の承認は再使用しません。";
        return null;
    }
    public string? HistoricalFollowUpProblem(ApplyReview review)
    {
        if (review.HistoricalDecisionId is not { } id) return null;
        if (HistoricalFollowUpProblem(id, review.Batch.Project) is { } problem) return problem;
        var source = HistoricalFieldHandling.Resolve(journal, historicalDispositions.Single(d => d.Id == id).Target)!;
        return review.Batch.Operations.Count(o => HistoricalOperationMatches(source, review.Batch, o)) == 1 ? null
            : "対象フィールドに反映できる確定済み変更がありません。未確定入力と以前の送信値は使用しません。現在の状態を確認して判断してください。";
    }
    private static bool HistoricalOperationMatches(HistoricalFieldSource source, ApplyBatch batch, ApplyOperation operation) =>
        source.Batch.Project == batch.Project && source.Operation.Key == operation.Key
        && source.Operation.ItemId == operation.ItemId && source.Operation.IssueId == operation.IssueId;
    public HistoricalFieldDecision DecideHistoricalField(HistoricalFieldReview review, HistoricalFieldDecisionKind kind)
    {
        if (review.Revision != Revision || !Enum.IsDefined(kind)
            || HistoricalFieldHandling.IsSettled(journal, historicalDispositions, review.Target))
            throw new InvalidOperationException("比較後に変更があります。履歴を再確認してください。");
        var source = HistoricalFieldHandling.Resolve(journal, review.Target)
            ?? throw new InvalidOperationException("この履歴はフィールドの確認対象ではありません。");
        HistoricalFieldHandling.ValidateObservation(source, review.Observation);
        if (kind == HistoricalFieldDecisionKind.FieldNotApplicable
            ? review.Observation.Kind != HistoricalFieldEvidenceKind.ProjectFieldAbsent
            : review.Observation.Kind != HistoricalFieldEvidenceKind.CurrentValue)
            throw new InvalidOperationException("この観測では選択した判断を保存できません。");
        var decision = new HistoricalFieldDecision(Guid.NewGuid().ToString("N"), review.Target, kind,
            review.Observation, DateTimeOffset.UtcNow, ++Revision);
        historicalDispositions.Add(decision);
        return decision;
    }
}
