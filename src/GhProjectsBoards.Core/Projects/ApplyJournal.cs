using System.Collections.Immutable;

namespace GhProjectsBoards.Core.Projects;

internal enum ApplyState { Pending, Running, Succeeded, Failed, Unknown, Waiting, Blocked, Cancelled, Superseded }
internal sealed record ApplyAttempt(int Number, DateTimeOffset At, ApplyState State, string Reason);
internal sealed record ApplyOperation(string Id, FieldKey Key, string ItemId, string IssueId, string Identity,
    string FieldName, string? Expected, LocalValue Intended, long Stamp, ApplyState State,
    ImmutableArray<ApplyAttempt> Attempts, string Reason, FieldObservation? Verification = null, DateTimeOffset? NotBefore = null);
internal sealed record ApplyBatch(string Id, ScopedId Project, string ProjectName, long ReviewedRevision,
    DateTimeOffset ReviewedAt, ImmutableArray<ApplyOperation> Operations, CreationOperation[]? Creations = null);
internal sealed record ApplyReview(ApplyBatch Batch, string[] Blocked, int SelectedRows, int PendingBuffers);

internal static class ApplyJournal
{
    public static void Validate(DraftRecord record)
    {
        if (record.Version < 3 && record.Journal is { Length: > 0 }) throw new InvalidDataException("Unversioned execution history.");
        var batches = record.Journal ?? [];
        CreationJournal.Validate(record);
        if (batches.Any(b => b is null || string.IsNullOrWhiteSpace(b.Id) || b.Project is null || b.Project.Scope != record.Scope
            || b.ReviewedRevision < 0 || b.ReviewedRevision > record.Revision || b.ReviewedAt == default || b.Operations.IsDefault)
            || batches.Select(b => b.Id).Distinct().Count() != batches.Length) throw new InvalidDataException("Invalid Apply batch.");
        var operations = batches.SelectMany(b => b.Operations).ToArray();
        if (operations.Select(o => o?.Id).Distinct().Count() != operations.Length) throw new InvalidDataException("Duplicate operation.");
        foreach (var batch in batches)
        {
        if (string.IsNullOrWhiteSpace(batch.Project.NodeId) || batch.Operations.Select(o => o?.Key).Distinct().Count() != batch.Operations.Length)
            throw new InvalidDataException("Invalid scoped operation identities.");
        foreach (var o in batch.Operations)
        {
            if (o is null || string.IsNullOrWhiteSpace(o.Id) || o.Key is null || string.IsNullOrWhiteSpace(o.ItemId)
                || string.IsNullOrWhiteSpace(o.IssueId) || o.Stamp < 0 || o.Stamp > batch.ReviewedRevision || !Enum.IsDefined(o.State)
                || o.Intended is null || o.Attempts.IsDefault || o.Reason is null
                || o.State == ApplyState.Running && o.Attempts.Length == 0
                || o.Verification is { } verification && (verification.Project != batch.Project || verification.At == default
                    || !Enum.IsDefined(verification.Availability) || verification.Options is null
                    || verification.Availability == ValueAvailability.Empty && verification.Value is not null
                    || verification.Availability == ValueAvailability.Present && string.IsNullOrWhiteSpace(verification.Value))
                || (o.Key.Kind == "Title" ? o.Key.NodeId != o.IssueId || o.Key.ProjectId is not null || o.Key.FieldId is not null
                    || o.Intended.Clear || string.IsNullOrWhiteSpace(o.Intended.Value) || o.Intended.Value.IndexOfAny(['\r','\n','\t']) >= 0
                    : o.Key.Kind != "Select" || o.Key.NodeId != o.ItemId || o.Key.ProjectId != batch.Project.NodeId || string.IsNullOrWhiteSpace(o.Key.FieldId)
                    || (o.Intended.Clear ? o.Intended.Value is not null : string.IsNullOrWhiteSpace(o.Intended.Value)))
                || o.Attempts.Where((a, i) => a is null || a.Number != i + 1 || a.At == default || !Enum.IsDefined(a.State) || a.Reason is null).Any()
                || o.State == ApplyState.Succeeded && (o.Verification is null || o.Verification.Value != o.Intended.Value
                    || o.Verification.Availability is not (ValueAvailability.Present or ValueAvailability.Empty)))
                throw new InvalidDataException("Invalid Apply operation.");
        }
        }
    }
}

internal sealed partial class EditingWorkspace
{
    private readonly List<ApplyBatch> journal = [];
    public IReadOnlyList<ApplyBatch> Journal => journal;
    public bool HasUnresolvedApply => journal.Any(b => b.Operations.Any(o => o.State is not (ApplyState.Succeeded or ApplyState.Superseded)))
        || Creations.Any(c => !c.Completed && (c.Authorized || c.Dispatched) || c.EarlierUncertain);
    public IEnumerable<ProjectRegistration> CheckpointRegistrations => (registrations ?? []).Select(RegistrationStore.FromRecord);
    public void SupersedeApply(string batchId)
    {
        var index = journal.FindIndex(b => b.Id == batchId);
        if (index < 0) throw new InvalidOperationException("Unknown batch.");
        journal[index] = journal[index] with { Creations = (journal[index].Creations ?? []).Select(c => c with { Authorized = false }).ToArray(), Operations = journal[index].Operations.Select(o => o.State == ApplyState.Succeeded ? o :
            o with { State = ApplyState.Superseded, Reason = "ユーザーが以前の承認を撤回。試行履歴を保持し、新たな取得・レビューが必要。" }).ToImmutableArray() };
        Revision++;
    }

    public ApplyReview ReviewApply(ProjectRegistration project, IReadOnlySet<string> selectedItems, IReadOnlyDictionary<string, CreationRepository>? destinations = null)
    {
        if (project.Snapshot.Id.Scope != Scope || !HasCheckpoint) throw new InvalidOperationException("保存済み同一プロフィールが必要です。");
        var p = project.Snapshot;
        var blocked = new List<string>(); var operations = new List<ApplyOperation>();
        var rows = new EditingWorkspace(Scope).Open(project).Where(r => selectedItems.Contains(r.ItemId)).ToArray();
        var locals = localRows.Where(r => r.ProjectId == p.Id.NodeId && selectedItems.Contains(r.Id)).ToArray();
        if (rows.Length + locals.Length != selectedItems.Count) blocked.Add("選択した項目を現在のProjectで確認できません。");
        var creations = ReviewCreations(project, locals, destinations, blocked);
        foreach (var row in rows)
        foreach (var cell in row.Cells.Where(c => c.Key is not null))
        {
            if (!fields.TryGetValue(cell.Key!, out var f) || f.Change is null || operations.Any(o => o.Key == f.Key)) continue;
            var reason = cell.Reason ?? (f.Conflict ? "未解決の競合" : f.Observation?.Reason);
            // Buffer-related notices exclude text, not an independently committed payload.
            if (reason?.StartsWith("未確定文字") == true) reason = null;
            if (reason is not null) { blocked.Add($"{row.ItemId}/{cell.Key!.Kind}: {reason}"); continue; }
            if (cell.Baseline != f.Baseline) { blocked.Add($"{row.ItemId}: 再取得・競合解決が必要です。"); continue; }
            var issue = p.Issues[p.Items.Single(i => i.Id.NodeId == row.ItemId).ContentId!];
            operations.Add(new(Guid.NewGuid().ToString("N"), f.Key, row.ItemId, issue.Id.NodeId,
                $"{issue.Repository.NameWithOwner} #{issue.Number} / {issue.Id.NodeId}", f.Key.Kind == "Title" ? "Issue title（全Projectで共有）" : cell.Display,
                f.Baseline, f.Change, f.Stamp, ApplyState.Pending, [], "未送信"));
        }
        return new(new(Guid.NewGuid().ToString("N"), p.Id, p.Title, Revision, DateTimeOffset.UtcNow, operations.ToImmutableArray(), creations),
            blocked.ToArray(), rows.Length + locals.Length, fields.Values.Count(f => f.Buffer is not null && rows.Any(r => r.Cells.Any(c => c.Key == f.Key)))
                + locals.Count(r => r.TitleBuffer is not null) + locals.Count(r => r.RepositoryBuffer is not null));
    }
    public void ConfirmApply(ApplyReview review)
    {
        if (review.Batch.Project.Scope != Scope || review.Batch.ReviewedRevision != Revision || review.Blocked.Length != 0)
            throw new InvalidOperationException("比較後に変更がありました。再レビューしてください。");
        if (journal.Any(b => b.Operations.Any(o => o.State is not (ApplyState.Succeeded or ApplyState.Superseded))))
            throw new InvalidOperationException("未解決の既存更新履歴を先に確認してください。");
        foreach (var c in review.Batch.Creations ?? [])
            if (CreationLocked(c.LocalId) || !localRows.Any(r => r.Id == c.LocalId && r.Stamp == c.Stamp && r.Repository == c.Repository.Name))
                throw new InvalidOperationException("作成履歴・宛先が変わっています。");
        journal.Add(review.Batch); Revision++;
    }
    public void RecordApply(string batchId, ApplyOperation operation, bool acknowledge = false)
    {
        var index = journal.FindIndex(b => b.Id == batchId);
        if (index < 0 || !journal[index].Operations.Any(o => o.Id == operation.Id)) throw new InvalidOperationException("Unknown operation.");
        var frozen = journal[index].Operations.Single(o => o.Id == operation.Id);
        if (operation.Key != frozen.Key || operation.ItemId != frozen.ItemId || operation.IssueId != frozen.IssueId
            || operation.Expected != frozen.Expected || operation.Intended != frozen.Intended || operation.Stamp != frozen.Stamp)
            throw new InvalidOperationException("Confirmed payload cannot change.");
        journal[index] = journal[index] with { Operations = journal[index].Operations.Select(o => o.Id == operation.Id ? operation : o).ToImmutableArray() };
        if (acknowledge)
        {
            var old = fields[operation.Key];
            var value = old.Change is { } change ? change.Value : old.Baseline;
            fields[old.Key] = old with { Baseline = operation.Intended.Value,
                Change = value == operation.Intended.Value ? null : new(value, old.Key.Kind == "Select" && value is null),
                Observation = operation.Verification, Conflict = false, RetrievedAt = operation.Verification!.At };
            for (var i = 0; i < history.Count; i++)
                if (history[i].Changes.Any(c => c.Key == old.Key)) InvalidateRemoteUndo(i, "Applyの観測後は以前の基準値を復元できません。");
            registrations = CheckpointRegistrations.Select(r =>
            {
                var p = r.Snapshot;
                if (old.Key.Kind == "Title")
                {
                    var id = new ScopedId(Scope, operation.IssueId);
                    if (p.Issues.TryGetValue(id, out var issue))
                    {
                        var issues = p.Issues.ToDictionary(x => x.Key, x => x.Value);
                        issues[id] = issue with { Title = new(ValueAvailability.Present, operation.Intended.Value) };
                        p = p with { Issues = issues };
                    }
                }
                else if (p.Id.NodeId == old.Key.ProjectId)
                    p = p with { Items = p.Items.Select(item => item.Id.NodeId != operation.ItemId ? item : item with {
                        Values = item.Values.Select(v => v.FieldId?.NodeId != old.Key.FieldId ? v : v with {
                            OptionId = operation.Intended.Value, Availability = operation.Intended.Clear ? ValueAvailability.Empty : ValueAvailability.Present }).ToArray() }).ToArray() };
                return RegistrationStore.ToRecord(r with { Snapshot = p });
            }).ToArray();
        }
        Revision++;
    }
}
