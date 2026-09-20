namespace GhProjectsBoards.Core.Projects;

internal enum GanttState { Scheduled, Unplanned, Partial, Unresolved, Stale, Unavailable }
internal sealed record GanttRow(string RowId, string TaskId, string Title, string Identity,
    TaskPlan? Plan, PlanningInput? Input, GanttState State, bool HiddenOnBoards)
{
    public bool HasBar => State == GanttState.Scheduled;
    public string StateText => State switch { GanttState.Scheduled => Plan!.Mode.ToString(), GanttState.Unplanned => "未計画",
        GanttState.Partial => "片側のみ", GanttState.Unresolved => "未解決", GanttState.Stale => "古い結果", _ => "未確認" };
}
internal sealed record GanttProjection(GanttRow[] Rows, AdoptedPlan Plan)
{
    public static GanttProjection Create(EditingWorkspace work, ProjectRegistration project, IEnumerable<string> boardsRows)
    {
        var plan = work.PlanFor(project);
        var results = plan.Tasks.ToDictionary(t => t.Id);
        var inputs = (plan.Inputs ?? []).ToDictionary(i => i.Task.Id);
        var visible = boardsRows.ToHashSet();
        var items = project.Snapshot.Items.ToDictionary(i => i.Id.NodeId);
        var rows = work.ReadRows(project).Select(row => {
            var item = items.GetValueOrDefault(row.ItemId);
            var taskId = row.IsLocal ? row.ItemId : item?.ContentId?.NodeId ?? row.ItemId;
            var issue = project.Snapshot.Issues.GetValueOrDefault(new(work.Scope, taskId));
            var result = results.GetValueOrDefault(taskId);
            var input = inputs.GetValueOrDefault(taskId);
            var identity = row.IsLocal ? "新規 · " + row.ItemId.Replace("local-", "", StringComparison.Ordinal)[..8]
                : issue is not null ? $"{issue.Repository.NameWithOwner} #{issue.Number}" : row.ItemId;
            var title = row.Cells.FirstOrDefault();
            var state = !row.IsLocal && item?.Kind != ProjectItemKind.Issue ? GanttState.Unavailable : StateFor(result, plan.SourceRevision);
            return new GanttRow(row.ItemId, taskId, title is null ? identity : work.Value(title) ?? title.Display,
                identity, result, input, state, !visible.Contains(row.ItemId));
        }).ToArray();
        return new(rows, plan);
    }
    internal static GanttState StateFor(TaskPlan? result, long revision) => result is null || result.Mode == PlanningMode.Unplanned ? GanttState.Unplanned
        : result.SourceRevision != revision ? GanttState.Stale
        : result.Resolved && result.Finish >= result.Start ? GanttState.Scheduled
        : result.Mode == PlanningMode.Manual && (result.Start is not null || result.Finish is not null) ? GanttState.Partial
        : GanttState.Unresolved;
}
internal sealed record GanttAxis(DateTime Origin, int Days, double DayWidth)
{
    public double Position(DateTime endpoint) => (endpoint - Origin).TotalDays * DayWidth;
    public double Width => Days * DayWidth;
    public static GanttAxis For(GanttProjection projection, bool week)
    {
        var dates = projection.Rows.Where(r => r.State is GanttState.Scheduled or GanttState.Partial)
            .SelectMany(r => new[] { r.Plan?.Start, r.Plan?.Finish }).Append(projection.Plan.Configuration?.Start)
            .Where(d => d.HasValue).Select(d => d!.Value).ToArray();
        // A calendar page is navigation context only, never a fabricated task endpoint.
        var first = dates.Length == 0 ? DateTime.SpecifyKind(DateTime.UtcNow.AddHours(9).Date, DateTimeKind.Unspecified) : dates.Min().Date;
        var last = dates.Length == 0 ? first.AddDays(28) : dates.Max().Date;
        var availableDays = (DateTime.MaxValue.Date - first).Days + 1;
        return new(first, Math.Min(availableDays, Math.Max(28, (last - first).Days + 8)), week ? 28 : 96);
    }
}
