namespace GhProjectsBoards.Core.Projects;

internal enum GanttDragPart { Move, Start, Finish }
internal sealed record GanttScheduleEdit(string RowId, string TaskId, long Revision, GanttDragPart Part, int Days)
{
    internal static bool CanDrag(GanttRow row) => row.HasBar && row.Input is { Task.Mode: PlanningMode.Manual,
        Task.Progress: not PlanningProgress.Completed, SatisfiesPrecedence: false, SourceProblem: null };

    internal static PlanningTask Preview(PlanningTask task, GanttDragPart part, int days)
    {
        if (task.Mode != PlanningMode.Manual || task.Progress == PlanningProgress.Completed
            || task.ManualStart is null || task.ManualFinish is null || !Enum.IsDefined(part))
            throw new InvalidOperationException("開始・終了を指定した未完了タスクを選択してください。");
        try
        {
            var next = task with { ManualStart = part == GanttDragPart.Finish ? task.ManualStart : task.ManualStart.Value.AddDays(days),
                ManualFinish = part == GanttDragPart.Start ? task.ManualFinish : task.ManualFinish.Value.AddDays(days) };
            if (next.ManualFinish < next.ManualStart) throw new InvalidOperationException("終了日時は開始日時以降にしてください。");
            return next;
        }
        catch (ArgumentOutOfRangeException) { throw new InvalidOperationException("指定できる日付の範囲を超えています。"); }
    }
}

internal sealed partial class EditingWorkspace
{
    internal void CommitGanttSchedule(ProjectRegistration project, GanttScheduleEdit edit)
    {
        if (project.Snapshot.Id.Scope != Scope || edit.Revision != Revision)
            throw new InvalidOperationException("ドラッグ中に作業が変わりました。現在の日程を確認してください。");
        var projected = GanttProjection.Create(this, project, []);
        var row = projected.Rows.SingleOrDefault(row => row.RowId == edit.RowId);
        if (row is null || row.TaskId != edit.TaskId || !GanttScheduleEdit.CanDrag(row))
            throw new InvalidOperationException("開始・終了を指定した未完了タスクを選択してください。");
        var plan = Planning(project.Snapshot.Id.NodeId)!;
        var dateFields = plan.Fields.Where(field => field.Role is "Start" or "Finish").Select(field => field.FieldId).ToHashSet();
        var aliases = projected.Rows.Where(candidate => candidate.TaskId == edit.TaskId).Select(candidate => candidate.RowId).ToHashSet();
        foreach (var cell in ReadRows(project).Where(candidate => aliases.Contains(candidate.ItemId)).SelectMany(candidate => candidate.Cells)
            .Where(cell => cell.Key?.FieldId is { } id && dateFields.Contains(id)))
            if (Buffer(cell) is not null || cell.Reason is not null || Field(cell)?.Conflict == true || Field(cell)?.Observation?.Reason is not null)
                throw new InvalidOperationException("開始・終了の入力や競合を解決してからドラッグしてください。");
        var task = GanttScheduleEdit.Preview(row.Input!.Task, edit.Part, edit.Days);
        if (edit.Days == 0) return;
        CommitPlanning(project, plan with { Tasks = plan.Tasks.Select(existing => existing.Id == task.Id ? task : existing).ToArray() }, edit.Revision);
    }
}
