namespace GhProjectsBoards.Core.PlanEditor;

// #229: a typed 開始日 is the task's own input (開始日指定, a start no earlier than) and a predecessor that
// ends later wins the scheduled start. The commit that produced that difference reports which predecessor won
// and the resulting date, and offers one command that removes exactly that link. A notice exists only for a
// typed start: paste, fill, Undo/Redo and a refresh never produce one, and a start that holds or one moved
// only by the Project start reports nothing.
internal sealed record PlanStartNotice(string Identity, string PredecessorIdentity, string PredecessorName,
    DateOnly Typed, DateOnly Scheduled)
{
    // Reports the typed start the given plan no longer holds because a predecessor ends later. The sheet draws
    // the read schedule, so the comparison uses that same calculation.
    internal static PlanStartNotice? Detect(PlanDocument document, string identity, DateOnly typed, DateOnly today)
    {
        var task = PlanOperations.Schedule(document, today).FirstOrDefault(t => t.Input.Identity == identity);
        if (task is null || task.IsSummary || task.Start.Value is not { } scheduled || scheduled <= typed) return null;
        // Only a predecessor link produces this notice; 基準日 and プロジェクト開始日 are out of its scope.
        var names = PlanTaskNames.Of(document);
        if (!names.TryPredecessor(task.StartReason, out var predecessor) || predecessor == identity) return null;
        // A predecessor inherited from a requirement row is not this task's own link, so no command could
        // remove it from here. Report only a link this row holds.
        if (document.State.Rows.FirstOrDefault(r => r.Identity == identity) is not { } row
            || !row.Predecessors.Contains(predecessor)) return null;
        return new(identity, predecessor, names[predecessor], typed, scheduled);
    }

    // The one ordinary cell operation that removes exactly the link the notice names: one Undo step that
    // Undo restores. A plan that changed since the notice was shown has nothing to remove.
    internal static EditPlanCells? Unlink(PlanDocument document, PlanStartNotice notice)
        => document.State.Rows.FirstOrDefault(r => r.Identity == notice.Identity) is { } row
            && row.Predecessors.Contains(notice.PredecessorIdentity)
            ? new(PlanOperationKind.Cell, [new PlanCellChange(notice.Identity, PlanField.Predecessors,
                row.Predecessors.Remove(notice.PredecessorIdentity))])
            : null;
}
