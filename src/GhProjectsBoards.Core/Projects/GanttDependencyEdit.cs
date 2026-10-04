namespace GhProjectsBoards.Core.Projects;

internal sealed record GanttDependencyEdit(string PredecessorRowId, string PredecessorTaskId,
    string SuccessorRowId, string SuccessorTaskId, long Revision, bool Remove = false)
{
    internal static bool CanConnect(GanttRow row) => row.HasBar
        && row.Input is { RelationshipsComplete: true, SourceProblem: null };

    internal static string? Problem(GanttProjection projection, GanttRow from, GanttRow to, bool remove = false)
    {
        if (from.Input is not { RelationshipsComplete: true, SourceProblem: null }
            || to.Input is not { RelationshipsComplete: true, SourceProblem: null }
            || !remove && (!CanConnect(from) || !CanConnect(to)))
            return "日程・先行関係を確認できるタスクを選択してください。";
        if (from.TaskId == to.TaskId) return "同じタスクは先行・後続にできません。";
        var present = to.Input.Predecessors.Any(link => link.Kind == "FS" && link.PredecessorId == from.TaskId);
        if (remove) return to.Input.Predecessors.Any(link => link.Kind == "FS" && link.ExternalFinish is null && link.PredecessorId == from.TaskId)
            ? null : "選択した依存関係は変更されています。現在の関係を確認してください。";
        if (present) return "この依存関係は既にあります。";
        // Check only the proposed edge against the adopted graph. Scheduling and
        // the edit transaction remain in the existing planning operation.
        var inputs = (projection.Plan.Inputs ?? []).ToDictionary(input => input.Task.Id);
        var pending = new Stack<string>(); var seen = new HashSet<string>(); pending.Push(from.TaskId);
        while (pending.TryPop(out var id))
        {
            if (id == to.TaskId) return "先行関係が循環するため追加できません。";
            if (!seen.Add(id) || !inputs.TryGetValue(id, out var input)) continue;
            foreach (var link in input.Predecessors.Where(link => link.Kind == "FS")) pending.Push(link.PredecessorId);
        }
        return null;
    }
}

internal sealed partial class EditingWorkspace
{
    internal void CommitGanttDependency(ProjectRegistration project, GanttDependencyEdit edit)
    {
        if (project.Snapshot.Id.Scope != Scope || edit.Revision != Revision)
            throw new InvalidOperationException("操作中に作業が変わりました。現在の依存関係を確認してください。");
        var projected = GanttProjection.Create(this, project, []);
        var from = projected.Rows.SingleOrDefault(row => row.RowId == edit.PredecessorRowId);
        var to = projected.Rows.SingleOrDefault(row => row.RowId == edit.SuccessorRowId);
        if (from is null || to is null || from.TaskId != edit.PredecessorTaskId || to.TaskId != edit.SuccessorTaskId)
            throw new InvalidOperationException("依存関係のタスクを確認できません。現在のタスクを選択してください。");
        if (GanttDependencyEdit.Problem(projected, from, to, edit.Remove) is { } problem)
            throw new InvalidOperationException(problem);
        var predecessors = to.Input!.Predecessors.Where(link => link.Kind == "FS" && link.ExternalFinish is null)
            .Select(link => link.PredecessorId).ToHashSet();
        if (edit.Remove) predecessors.Remove(from.TaskId); else predecessors.Add(from.TaskId);
        var plan = Planning(project.Snapshot.Id.NodeId)!;
        if (edit.Remove)
            plan = plan with { Tasks = plan.Tasks.Select(task => task.Id != to.TaskId || task.LocalLinks is null ? task
                : task with { LocalLinks = task.LocalLinks.Where(link => link.Kind != "FS" || link.ExternalFinish is not null
                    || link.PredecessorId != from.TaskId).ToArray() }).ToArray() };
        CommitPlanning(project, plan, edit.Revision, dependencies: [new(to.TaskId, predecessors.ToArray())]);
    }
}
