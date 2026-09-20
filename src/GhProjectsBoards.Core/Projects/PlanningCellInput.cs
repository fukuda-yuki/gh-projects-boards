namespace GhProjectsBoards.Core.Projects;

internal sealed record ActualInputContext(string TaskId, string? PersonId, bool HasPerson,
    bool Historical, bool MultipleReports, decimal? ObservedTotal, DateOnly? ReportedThrough);

internal sealed partial class EditingWorkspace
{
    public string? PlanningInputRole(EditCell cell)
    {
        if (cell.Scope != Scope || cell.Key is not { } key || !cell.InputLocked || cell.Reason is not null
            || !fields.TryGetValue(key, out var field) || field.Conflict
            || field.Observation?.Reason is { } reason && reason != PendingObservationReason) return null;
        return Planning(key.ProjectId ?? "")?.Fields.SingleOrDefault(b => b.FieldId == key.FieldId
            && PlanningScalars.Kind(b.DataType) == key.Kind && b.Role is "Actual" or "Start" or "Finish")?.Role;
    }

    // Typed projection inputs keep native unfinished text in the existing draft.
    // They do not make these fields eligible for generic NUMBER/DATE writes.
    public void SetPlanningBuffer(EditCell cell, string? text)
    {
        if (PlanningInputRole(cell) is null) throw new InvalidOperationException("この計画セルは入力できません。取得状態・対応するフィールドを確認してください。");
        var old = fields[cell.Key!];
        if (old.Buffer == text) return;
        fields[cell.Key!] = old with { Buffer = text }; Revision++;
        if (old.Buffer is not null && text is not null) pendingTextChanges++;
    }

    public ActualInputContext ActualInput(ProjectRegistration project, string rowId)
    {
        var (row, cell, task) = PlanningInputTarget(project, rowId, "Actual");
        var actuals = task.Actuals;
        if (actuals is { Length: 1 })
            return new(task.Id, actuals[0].PersonId, true, true, false, actuals[0].Hours, actuals[0].ReportedThrough);
        var value = Value(cell);
        var total = value is null ? (decimal?)null : PlanningContract.ParseHours(value);
        var issue = project.Snapshot.Issues.GetValueOrDefault(new(Scope, task.Id));
        var unique = issue?.Native is { Complete: true, Assignees.Length: 1 } native ? native.Assignees[0].Id.NodeId : null;
        // A fetched total has no historical worker. Only a genuinely new report
        // can propose the currently observed single assignee automatically.
        return new(task.Id, total is null ? unique : null, total is null && unique is not null,
            false, actuals is { Length: > 1 }, total, null);
    }

    public void CommitActualInput(ProjectRegistration project, string rowId, string text, DateOnly through,
        string? personId, long expectedRevision)
    {
        if (through == default) throw new InvalidOperationException("報告対象最終日を確認してください。");
        var (_, cell, task) = PlanningInputTarget(project, rowId, "Actual");
        if (task.Actuals is { Length: > 1 }) throw new InvalidOperationException("複数人の実績は内訳で更新してください。合計を自動配分しません。");
        if (task.Actuals is { Length: 1 } old && old[0].PersonId != personId)
            throw new InvalidOperationException("過去の実績担当者を保持します。担当者を変更する場合は内訳を明示的に修正してください。");
        var hours = PlanningContract.ParseHours(text);
        CommitPlanningInput(project, cell, task with { Actuals = [new(personId, hours, through)] }, expectedRevision);
    }

    public void RemoveActualInput(ProjectRegistration project, string rowId, long expectedRevision)
    {
        var (_, cell, task) = PlanningInputTarget(project, rowId, "Actual");
        CommitPlanningInput(project, cell, task with { Actuals = [] }, expectedRevision);
    }

    public void CommitActualReports(ProjectRegistration project, string rowId, ActualContribution[] reports, long expectedRevision)
    {
        var (_, cell, task) = PlanningInputTarget(project, rowId, "Actual");
        if (Buffer(cell) is { } pending && (reports.Length == 0 || PlanningContract.ParseHours(pending) != reports.Sum(a => a.Hours)))
            throw new InvalidOperationException("内訳の合計を入力中の累計実績に合わせてください。");
        CommitPlanningInput(project, cell, task with { Actuals = reports }, expectedRevision);
    }

    private (EditRow Row, EditCell Cell, PlanningTask Task) PlanningInputTarget(ProjectRegistration project, string rowId, string role)
    {
        if (project.Snapshot.Id.Scope != Scope) throw new InvalidOperationException("別プロフィールの計画です。");
        var plan = Planning(project.Snapshot.Id.NodeId) ?? throw new InvalidOperationException("Projectの計画設定が必要です。");
        var binding = plan.Fields.SingleOrDefault(f => f.Role == role) ?? throw new InvalidOperationException("計画フィールドを設定してください。");
        var row = ReadRows(project).SingleOrDefault(r => r.ItemId == rowId) ?? throw new InvalidOperationException("入力対象の行を確認できません。");
        var cell = row.Cells.SingleOrDefault(c => c.Key?.FieldId == binding.FieldId);
        if (cell is null || PlanningInputRole(cell) != role) throw new InvalidOperationException("この計画セルは現在入力できません。比較画面で取得値を確認してください。");
        var id = TaskId(project, rowId);
        var task = plan.Tasks.SingleOrDefault(t => t.Id == id) ?? (plan.Version >= 3 ? WithObservedAssignment(project, new(id)) : new(id));
        return (row, cell, task);
    }

    private void CommitPlanningInput(ProjectRegistration project, EditCell cell, PlanningTask task, long expectedRevision)
    {
        var plan = Planning(project.Snapshot.Id.NodeId)!;
        CommitPlanning(project, plan with { Tasks = plan.Tasks.Where(t => t.Id != task.Id).Append(task).ToArray() },
            expectedRevision, consumeBuffers: [cell.Key!]);
    }
}
