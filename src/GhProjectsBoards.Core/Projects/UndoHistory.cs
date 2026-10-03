namespace GhProjectsBoards.Core.Projects;

internal sealed partial class EditingWorkspace
{
    // Null is legacy history. An empty declaration is a new operation that did
    // not write input buffers, even when it changed their committed projections.
    private static bool PreservesBuffer(EditTransaction transaction, FieldKey key)
        => transaction.BufferWrites is { } writes && !writes.Contains(key);

    private static bool CanUndoField(EditTransaction transaction, FieldChange change, DraftField current)
        => PreservesBuffer(transaction, change.Key)
            ? (current.Buffer is null || current.Buffer == change.After.Buffer)
                && SameUndoState(current with { Buffer = change.After.Buffer }, change.After)
            : SameUndoState(current, change.After);

    private static LocalRow WithoutConsumedBuffers(LocalRow row, HashSet<FieldKey>? consumed) => row with
    {
        TitleBuffer = consumed?.Contains(new("LocalTitle", row.Id, row.ProjectId)) == true ? null : row.TitleBuffer,
        RepositoryBuffer = consumed?.Contains(new("LocalRepository", row.Id, row.ProjectId)) == true ? null : row.RepositoryBuffer
    };

    private static LocalRow MergeUnwrittenBuffers(EditTransaction transaction, LocalRow state, LocalRow current) => state with
    {
        TitleBuffer = PreservesBuffer(transaction, new("LocalTitle", state.Id, state.ProjectId)) ? current.TitleBuffer : state.TitleBuffer,
        RepositoryBuffer = PreservesBuffer(transaction, new("LocalRepository", state.Id, state.ProjectId)) ? current.RepositoryBuffer : state.RepositoryBuffer
    };

    // Splits must retain the distinction between legacy null and a declared
    // empty footprint. Neither remote acknowledgement nor row promotion owns input.
    private static EditTransaction WithHistoryParts(EditTransaction transaction, FieldChange[] changes, LocalRowChange[]? rows)
        => transaction with { Changes = changes, Rows = rows, BufferWrites = transaction.BufferWrites?.Where(key =>
            changes.Any(c => c.Key == key) || IsLocal(key) && key.ProjectId == transaction.ProjectId
                && (rows ?? []).Any(r => r.Id == key.NodeId)).Distinct().ToArray() };
}
