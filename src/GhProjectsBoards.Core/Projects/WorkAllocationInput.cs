namespace GhProjectsBoards.Core.Projects;

internal sealed record RemainingContribution(string PersonId, decimal? Hours);

internal sealed partial class EditingWorkspace
{
    public void CommitWorkAllocation(ProjectRegistration project, string rowId, ActualContribution[] reports,
        string? remainingTotal, RemainingContribution[] remaining, long expectedRevision)
    {
        var (row, actual, task) = PlanningInputTarget(project, rowId, "Actual");
        var plan = Planning(project.Snapshot.Id.NodeId)!;
        var remainingId = plan.Fields.SingleOrDefault(f => f.Role == "Remaining")?.FieldId;
        var remainingCell = remainingId is null ? null : row.Cells.SingleOrDefault(c => c.Key?.FieldId == remainingId);
        if (remainingCell is not { Editable: true, Key: not null }
            || Field(remainingCell) is not { Conflict: false } field
            || field.Observation?.Reason is { } reason && reason != PendingObservationReason)
            throw new InvalidOperationException("残工数の入力先を確認できません。フィールドと取得状態を確認してください。");
        if (remaining.Any(r => string.IsNullOrWhiteSpace(r.PersonId))
            || remaining.Select(r => r.PersonId).Distinct().Count() != remaining.Length)
            throw new InvalidOperationException("残工数の担当者を重複なく指定してください。");
        decimal? total = remainingTotal is null ? null : PlanningContract.ParseHours(remainingTotal);
        foreach (var share in remaining.Where(r => r.Hours is not null)) _ = PlanningContract.CanonicalHours(share.Hours!.Value);
        if (remaining.Any(r => r.Hours is not null) && total is null)
            throw new InvalidOperationException("残工数を配分するにはタスクの残工数を入力してください。");
        if (remaining.Sum(r => r.Hours ?? 0) > total)
            throw new InvalidOperationException("担当者別の残工数が合計を超えています。内訳または合計を確認してください。");
        if (reports.Length == 0 && (Value(actual) is not null || task.Actuals is { Length: > 0 }))
            throw new InvalidOperationException("実績を消すには「実績を削除」を選んでください。");
        if (Buffer(actual) is { } pendingActual && (reports.Length == 0 || PlanningContract.ParseHours(pendingActual) != reports.Sum(r => r.Hours)))
            throw new InvalidOperationException("内訳の合計を入力中の累計実績に合わせてください。");
        if (Buffer(remainingCell) is { } pendingRemaining
            && (string.IsNullOrWhiteSpace(pendingRemaining) ? total is not null : PlanningContract.ParseHours(pendingRemaining) != total))
            throw new InvalidOperationException("残工数の合計を表の入力中の値に合わせてください。");
        var prior = task.Contributions ?? [];
        // Changing Remaining never reassigns Estimate or drops a known historical person.
        var shares = prior.Select(c => c with { RemainingHours = remaining.SingleOrDefault(r => r.PersonId == c.PersonId)?.Hours })
            .Concat(remaining.Where(r => r.Hours is not null && !prior.Any(c => c.PersonId == r.PersonId)).Select(r => new WorkContribution(r.PersonId, null, r.Hours))).ToArray();
        var next = task with { Actuals = reports.Length == 0 ? task.Actuals : reports.ToArray(),
            Contributions = shares.Length == 0 ? task.Contributions : shares };
        CommitPlanning(project, plan with { Tasks = plan.Tasks.Where(t => t.Id != task.Id).Append(next).ToArray() }, expectedRevision,
            values: [new(rowId, "Remaining", total is { } hours ? PlanningContract.CanonicalHours(hours) : null)],
            consumeBuffers: [actual.Key!, remainingCell.Key]);
    }
}
