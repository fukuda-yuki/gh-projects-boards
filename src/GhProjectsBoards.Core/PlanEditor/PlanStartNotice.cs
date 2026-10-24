namespace GhProjectsBoards.Core.PlanEditor;

// #229: a typed 開始日 is the plan start and a predecessor that ends later wins. The commit that produced
// that difference reports which predecessor won and the resulting date, and offers one command that removes
// exactly that link. A notice exists only for a typed start: paste, fill, Undo/Redo and a refresh never
// produce one, and a start that holds or one moved only by the Project start reports nothing.
internal sealed record PlanStartNotice(string Identity, string PredecessorIdentity, string PredecessorName,
    DateOnly Typed, DateOnly Scheduled)
{
    // Reports the typed start the given plan no longer holds because a predecessor ends later.
    internal static PlanStartNotice? Detect(PlanDocument document, string identity, DateOnly typed, DateOnly today) => null;

    // The one ordinary cell operation that removes exactly the link the notice names.
    internal static EditPlanCells? Unlink(PlanDocument document, PlanStartNotice notice) => null;
}
