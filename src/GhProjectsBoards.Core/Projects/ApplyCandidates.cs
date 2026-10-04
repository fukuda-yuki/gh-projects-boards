namespace GhProjectsBoards.Core.Projects;

internal sealed record ApplyCandidate(string Id, string Identity, EditRow Row, DraftField[] Fields, bool Missing = false)
{
    public bool IsCreation => Row.IsLocal;
    public int Changes => Fields.Count(f => f.Change is not null);
}

internal sealed partial class EditingWorkspace
{
    public ApplyCandidate[] ApplyCandidates(ProjectRegistration project)
        => ApplyCandidates(project, initializeFields: true);

    public ApplyCandidate[] ReadApplyCandidates(ProjectRegistration project)
        => ApplyCandidates(project, initializeFields: false);

    private ApplyCandidate[] ApplyCandidates(ProjectRegistration project, bool initializeFields)
    {
        var rows = OperationRows(project, initializeFields);
        var candidates = new List<ApplyCandidate>();
        var included = new HashSet<FieldKey>();
        // The status strip uses this projection during editing. Associate each
        // row by identity instead of rescanning every workspace field per row.
        // Retained scalar fields still belong to their item after a column disappears.
        var fieldOrder = fields.Keys.Select((key, ordinal) => (key, ordinal)).ToDictionary(p => p.key, p => p.ordinal);
        var itemFields = fields.Values.Where(f => f.Key.Kind is "Select" or "Number" or "Date"
            && f.Key.ProjectId == project.Snapshot.Id.NodeId).ToLookup(f => f.Key.NodeId);
        foreach (var row in rows)
        {
            var related = row.Cells.Where(c => c.Key is not null && fields.ContainsKey(c.Key))
                .Select(c => fields[c.Key!]).Concat(itemFields[row.ItemId]).DistinctBy(f => f.Key)
                .OrderBy(f => fieldOrder[f.Key]).ToArray();
            foreach (var f in related) included.Add(f.Key);
            if (!row.IsLocal && !related.Any(f => f.Change is not null || f.Buffer is not null || f.Conflict || f.Observation?.Reason == ProjectionDecisionReason)) continue;
            var local = localRows.SingleOrDefault(r => r.Id == row.ItemId);
            var issueId = project.Snapshot.Items.SingleOrDefault(i => i.Id.NodeId == row.ItemId)?.ContentId;
            var issue = issueId is null ? null : project.Snapshot.Issues.GetValueOrDefault(issueId);
            var identity = local is not null ? $"新規作成 / {local.Repository} / {local.Title}"
                : issue is not null ? $"{issue.Repository.NameWithOwner} #{issue.Number} / {Value(row.Cells[0])}" : $"確認が必要な項目 / {row.ItemId}";
            candidates.Add(new(row.ItemId, identity, row, related));
        }
        foreach (var f in fields.Values.Where(f => !included.Contains(f.Key) && f.SourceProject == project.Snapshot.Id
            && (f.Change is not null || f.Buffer is not null || f.Conflict)))
            candidates.Add(new("unavailable:" + f.Key, "取得結果で確認できない変更", new("unavailable:" + f.Key, []), [f], true));
        return candidates.ToArray();
    }
}
