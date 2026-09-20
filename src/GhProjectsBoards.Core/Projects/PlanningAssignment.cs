namespace GhProjectsBoards.Core.Projects;

internal sealed partial class EditingWorkspace
{
    internal static ProjectPlanning UpgradeAssignmentContract(ProjectPlanning plan) => plan with
    {
        Version = 3, Tasks = plan.Tasks.Select(t => t.Assignment is null
            ? t with { Assignment = new([], false, Legacy: true) } : t).ToArray()
    };
    internal static PlanningTask WithObservedAssignment(ProjectRegistration project, PlanningTask task)
    {
        var native = project.Snapshot.Issues.GetValueOrDefault(new(project.Snapshot.Id.Scope, task.Id))?.Native;
        var assignees = native?.Assignees.Select(a => a.Id.NodeId).Order().ToArray() ?? [];
        return task with { OwnerId = native?.Complete == true && assignees.Length == 1 ? assignees[0] : null,
            Assignment = new(assignees, native?.Complete == true) };
    }

    private PlanningChange? InitializeEstimatedTasks(string projectId, Dictionary<FieldKey, FieldChange> changes)
    {
        var plan = Planning(projectId);
        if (plan?.Version != 3) return null;
        var fieldId = plan.Fields.SingleOrDefault(f => f.Role == "Estimate")?.FieldId;
        var project = CheckpointRegistrations.SingleOrDefault(p => p.Snapshot.Id.NodeId == projectId);
        if (fieldId is null || project is null) return null;
        var tasks = plan.Tasks.ToDictionary(t => t.Id); var changed = false;
        foreach (var edit in changes.Values.Where(c => c.Key.Kind == "Number" && c.Key.FieldId == fieldId
            && (c.After.Change is null ? c.After.Baseline : c.After.Change.Value) is not null))
        {
            var id = TaskId(project, edit.Key.NodeId);
            var task = tasks.GetValueOrDefault(id) ?? new(id);
            if (task.Mode != PlanningMode.Unplanned) continue;
            tasks[id] = WithObservedAssignment(project, task with { Mode = PlanningMode.Auto }); changed = true;
        }
        if (!changed) return null;
        var next = plan with { Stamp = Revision, Tasks = tasks.Values.ToArray() };
        PlanningContract.Validate(next, Revision);
        planning.RemoveAll(p => p.ProjectId == projectId); planning.Add(next); InvalidatePlan(projectId);
        return new(plan, next);
    }

    public ProjectPlanning SchedulingCandidate(ProjectRegistration project, string rowId, PlanningMode mode,
        DateTime? start, DateTime? finish, bool adoptAssignment)
    {
        if (mode == PlanningMode.Unplanned) throw new InvalidOperationException("自動計算または日時を指定を選んでください。");
        if (project.Snapshot.Id.Scope != Scope) throw new InvalidOperationException("別プロフィールの計画です。");
        var plan = UpgradeAssignmentContract(Planning(project.Snapshot.Id.NodeId) ?? throw new InvalidOperationException("Projectの計画設定が必要です。"));
        var id = TaskId(project, rowId); var old = plan.Tasks.SingleOrDefault(t => t.Id == id);
        var task = old ?? new(id);
        if (adoptAssignment || old is null || old.Mode == PlanningMode.Unplanned) task = WithObservedAssignment(project, task);
        task = task with { Mode = mode, ManualStart = mode == PlanningMode.Manual ? start : null,
            ManualFinish = mode == PlanningMode.Manual ? finish : null };
        return plan with { Version = 3, Tasks = plan.Tasks.Where(t => t.Id != id).Append(task).ToArray() };
    }

    public void CommitDateInput(ProjectRegistration project, string rowId, string role, string text, long expectedRevision)
    {
        if (role is not ("Start" or "Finish")) throw new InvalidOperationException("日付の入力先が無効です。");
        var (_, cell, task) = PlanningInputTarget(project, rowId, role);
        var value = PlanningContract.ParseMinute(text);
        var current = PlanFor(project).Tasks.Single(t => t.Id == task.Id);
        var candidate = SchedulingCandidate(project, rowId, PlanningMode.Manual,
            role == "Start" ? value : current.Start, role == "Finish" ? value : current.Finish, false);
        CommitPlanning(project, candidate, expectedRevision, consumeBuffers: [cell.Key!]);
    }
}
