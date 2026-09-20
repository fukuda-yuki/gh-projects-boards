namespace GhProjectsBoards.Core.Projects;

internal sealed partial class EditingWorkspace
{
    internal static ProjectPlanning UpgradeAssignmentContract(ProjectPlanning plan) => plan with
    {
        Version = plan.Version is 2 or 4 ? 4 : 3, Tasks = plan.Tasks.Select(t => t.Assignment is null
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
        if (plan?.Version is not (3 or 4)) return null;
        var fieldId = plan.Fields.SingleOrDefault(f => f.Role == "Estimate")?.FieldId;
        var project = CheckpointRegistrations.SingleOrDefault(p => p.Snapshot.Id.NodeId == projectId);
        if (fieldId is null || project is null) return null;
        var tasks = plan.Tasks.ToDictionary(t => t.Id); var changed = false;
        foreach (var edit in changes.Values.Where(c => c.Key.Kind == "Number" && c.Key.FieldId == fieldId
            && (c.After.Change is null ? c.After.Baseline : c.After.Change.Value) is not null))
        {
            var id = TaskId(project, edit.Key.NodeId);
            var task = tasks.GetValueOrDefault(id) ?? new(id);
            if (task.Mode != PlanningMode.Unplanned || task.Assignment?.Legacy == true) continue;
            tasks[id] = WithObservedAssignment(project, task with { Mode = PlanningMode.Auto }); changed = true;
        }
        if (!changed) return null;
        var next = plan with { Stamp = Revision, Tasks = tasks.Values.ToArray() };
        PlanningContract.Validate(next, Revision);
        planning.RemoveAll(p => p.ProjectId == projectId); planning.Add(next); InvalidatePlan(projectId);
        return new(plan, next);
    }

    public ProjectPlanning SchedulingCandidate(ProjectRegistration project, string rowId, PlanningMode mode,
        DateTime? start, DateTime? finish, bool adoptAssignment, string[]? clearedEndpoints = null)
    {
        if (mode == PlanningMode.Unplanned) throw new InvalidOperationException("自動計算または日時を指定を選んでください。");
        if (project.Snapshot.Id.Scope != Scope) throw new InvalidOperationException("別プロフィールの計画です。");
        var plan = UpgradeAssignmentContract(Planning(project.Snapshot.Id.NodeId) ?? throw new InvalidOperationException("Projectの計画設定が必要です。"));
        var id = TaskId(project, rowId); var old = plan.Tasks.SingleOrDefault(t => t.Id == id);
        var task = old ?? new(id);
        if (adoptAssignment || old is null) task = WithObservedAssignment(project, task);
        if (mode == PlanningMode.Manual)
        {
            var row = ReadRows(project).Single(r => r.ItemId == rowId);
            foreach (var role in new[] { "Start", "Finish" })
            {
                var value = role == "Start" ? start : finish;
                var binding = plan.Fields.SingleOrDefault(f => f.Role == role);
                var cell = row.Cells.SingleOrDefault(c => c.Key?.FieldId == binding?.FieldId);
                // A GitHub day does not establish its exact time or authorize its
                // deletion when the other endpoint is edited.
                if (value is null && cell is not null && Value(cell) is not null
                    && SchedulingEndpoint(project, rowId, role) is null && clearedEndpoints?.Contains(role) != true)
                    throw new InvalidOperationException((role == "Start" ? "開始" : "終了") + "の日付は取得済みですが時刻は未確認です。日時を入力するか、明示的に消去してください。");
            }
        }
        task = task with { Mode = mode, ManualStart = mode == PlanningMode.Manual ? start : null,
            ManualFinish = mode == PlanningMode.Manual ? finish : null };
        return plan with { Tasks = plan.Tasks.Where(t => t.Id != id).Append(task).ToArray() };
    }

    public void CommitDateInput(ProjectRegistration project, string rowId, string role, string text, long expectedRevision)
    {
        if (role is not ("Start" or "Finish")) throw new InvalidOperationException("日付の入力先が無効です。");
        var (_, cell, task) = PlanningInputTarget(project, rowId, role);
        var value = PlanningContract.ParseMinute(text);
        var candidate = SchedulingCandidate(project, rowId, PlanningMode.Manual,
            role == "Start" ? value : SchedulingEndpoint(project, rowId, "Start"),
            role == "Finish" ? value : SchedulingEndpoint(project, rowId, "Finish"), false, value is null ? [role] : []);
        CommitPlanning(project, candidate, expectedRevision, consumeBuffers: [cell.Key!]);
    }

    public DateTime? SchedulingEndpoint(ProjectRegistration project, string rowId, string role)
    {
        if (project.Snapshot.Id.Scope != Scope) throw new InvalidOperationException("別プロフィールの計画です。");
        var id = TaskId(project, rowId);
        var task = Planning(project.Snapshot.Id.NodeId)?.Tasks.SingleOrDefault(t => t.Id == id);
        if (task?.Mode == PlanningMode.Manual) return role == "Start" ? task.ManualStart : task.ManualFinish;
        var current = PlanFor(project).Tasks.Single(t => t.Id == id);
        return role == "Start" ? current.Start : current.Finish;
    }
}
