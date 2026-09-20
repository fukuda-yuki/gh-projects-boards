namespace GhProjectsBoards.Core.Projects;

internal sealed record PersonAllowance(string PersonId, decimal Hours);
internal sealed record SummarySettings(PersonAllowance[] Allowances, ProtectedBaseline? Baseline);
internal sealed record BaselineTask(string TaskId, string RowId, string Title, string Identity, decimal? Estimate,
    PlanningMode? Mode, DateTime? Start, DateTime? Finish, TaskLaborKind LaborKind);
internal sealed record ProtectedBaseline(string Id, string ProjectId, DateTimeOffset CapturedAt, long SourceRevision,
    DateTime? ProjectStart, DateTime? Cutoff, PlanningCalendar Calendar, PlanningPerson[] People, BaselineTask[] Tasks);

internal static class SummaryContract
{
    internal static void Validate(ProjectPlanning p, long revision)
    {
        if (p.Version == 1 && (p.Summary is not null || p.Tasks.Any(t => t.LaborKind != TaskLaborKind.Unspecified)))
            throw new InvalidDataException("Unversioned Summary metadata.");
        if (p.Summary is not { } s) return;
        if (s.Allowances is null || s.Allowances.Any(a => a is null || string.IsNullOrWhiteSpace(a.PersonId)
                || a.Hours < 0 || a.Hours > 1_000_000_000m || decimal.Round(a.Hours, 8) != a.Hours)
            || s.Allowances.Select(a => a.PersonId).Distinct().Count() != s.Allowances.Length)
            throw new InvalidDataException("Invalid scoped allowance.");
        if (s.Baseline is not { } b) return;
        if (string.IsNullOrWhiteSpace(b.Id) || b.ProjectId != p.ProjectId || b.CapturedAt == default
            || b.SourceRevision < 0 || b.SourceRevision > revision || b.Calendar is null || b.People is null || b.Tasks is null
            || !PlanningContract.Minute(b.ProjectStart) || !PlanningContract.Minute(b.Cutoff)
            || b.Tasks.Any(t => t is null || string.IsNullOrWhiteSpace(t.TaskId) || string.IsNullOrWhiteSpace(t.RowId)
                || t.Title is null || t.Identity is null || t.Mode is { } mode && !Enum.IsDefined(mode) || !Enum.IsDefined(t.LaborKind)
                || !PlanningContract.Minute(t.Start) || !PlanningContract.Minute(t.Finish) || t.Finish < t.Start
                || t.Estimate is { } e && (e < 0 || e > 1_000_000_000m || decimal.Round(e, 8) != e))
            || b.Tasks.Select(t => t.TaskId).Distinct().Count() != b.Tasks.Length)
            throw new InvalidDataException("Invalid protected baseline.");
        PlanningContract.Validate(new(1, p.ProjectId, b.SourceRevision, b.ProjectStart, b.Cutoff, [], b.Calendar, b.People, []), revision);
    }
}

internal sealed partial class EditingWorkspace
{
    private ProjectPlanning SummaryPlan(ProjectRegistration project, long expectedRevision)
    {
        if (expectedRevision != Revision || !HasCheckpoint || project.Snapshot.Id.Scope != Scope
            || project.Snapshot.Capability is not { CanUpdate: true } capability || capability.ObservedAt == default)
            throw new InvalidOperationException("Project・更新権限または比較後の変更を確認してください。");
        return Planning(project.Snapshot.Id.NodeId) ?? throw new InvalidOperationException("先に計画設定を保存してください。");
    }
    private void CommitSummary(ProjectPlanning before, SummarySettings summary)
    {
        SetPlanning(before with { Version = 2, Summary = summary }, before.Stamp);
        history.Add(new(Guid.NewGuid().ToString("N"), before.ProjectId, [], Plan: new(before, Planning(before.ProjectId)!)));
    }
    public void SetAllowance(ProjectRegistration project, string personId, decimal? hours, long expectedRevision)
    {
        var p = SummaryPlan(project, expectedRevision);
        if (string.IsNullOrWhiteSpace(personId)) throw new InvalidOperationException("担当者を選択してください。");
        if (hours is { } value) _ = PlanningContract.CanonicalHours(value);
        var s = p.Summary ?? new([], null);
        var next = s.Allowances.Where(a => a.PersonId != personId).ToList();
        if (hours is { } h) next.Add(new(personId, h));
        if (s.Allowances.SingleOrDefault(a => a.PersonId == personId)?.Hours == hours) return;
        CommitSummary(p, s with { Allowances = next.ToArray() });
    }
    public void CaptureBaseline(ProjectRegistration project, long expectedRevision, string? replaceBaselineId, DateTimeOffset capturedAt)
    {
        var p = SummaryPlan(project, expectedRevision);
        var s = p.Summary ?? new([], null);
        if (s.Baseline?.Id != replaceBaselineId)
            throw new InvalidOperationException("基準計画が変更されています。置き換える基準計画を確認してください。");
        var projection = GanttProjection.Create(this, project, []);
        var tasks = projection.Rows.DistinctBy(r => r.TaskId).Select(r => new BaselineTask(r.TaskId, r.RowId, r.Title, r.Identity,
            r.Input?.Estimate, r.Plan?.Mode, r.Plan?.Start, r.Plan?.Finish, r.Input?.Task.LaborKind ?? TaskLaborKind.Unspecified)).ToArray();
        var baseline = new ProtectedBaseline(Guid.NewGuid().ToString("N"), p.ProjectId, capturedAt, Revision,
            p.Start, p.Cutoff, p.Calendar, p.People, tasks);
        CommitSummary(p, s with { Baseline = baseline });
    }
}
