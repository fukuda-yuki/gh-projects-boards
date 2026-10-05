using System.Collections.Immutable;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.PlanEditor;

internal sealed class PlanSession
{
    public const int HistoryLimit = 200;
    private readonly object gate = new();
    private bool remoteOperation;
    internal IDisposable BeginRemoteOperation()
    {
        lock (gate)
        {
            if (remoteOperation) throw new InvalidOperationException("更新または発行が進行中です。");
            remoteOperation = true; return new RemoteGuard(this);
        }
    }
    private sealed class RemoteGuard(PlanSession owner) : IDisposable
    {
        public void Dispose() { lock (owner.gate) owner.remoteOperation = false; }
    }
    private void RequireEditable(PlanCommand? command = null)
    {
        if (remoteOperation) throw new InvalidOperationException("更新または発行が進行中です。");
        if (checkpoint.Document.Sync.Publish is not null && command is ReplacePlanSettings)
            throw new InvalidOperationException("発行結果を確認してから設定を変更してください。");
    }
    private static PlanPublishProgress? CurrentCreationInputs(PlanPublishProgress? progress, PlanState state)
        => progress is null ? null : progress with { Writes = progress.Writes.Select(w =>
        {
            if (w.Stage != PlanPublishStage.Create || w.ResultId is not null || w.State == PlanWriteState.Dispatched) return w;
            var input = System.Text.Json.Nodes.JsonNode.Parse(w.Input)!;
            input["title"] = state.Rows.Single(r => r.Identity == w.Identity).Title;
            return w with { Input = input.ToJsonString() };
        }).ToImmutableArray() };
    private readonly PlanStore store;
    private PlanCheckpoint checkpoint;
    private string? fingerprint;
    private long savedRevision;
    private Task<PlanSaveResult>? pending;
    private PlanSaveResult lastSave;
    private PlanSession(PlanStore store, PlanCheckpoint checkpoint, string? fingerprint)
    {
        this.store = store; this.checkpoint = checkpoint; this.fingerprint = fingerprint;
        savedRevision = fingerprint is null ? -1 : checkpoint.Revision;
        lastSave = new(true, PlanSaveFailure.None, null, fingerprint);
    }
    public PlanDocument Document { get { lock (gate) return checkpoint.Document; } }
    public int UndoCount { get { lock (gate) return checkpoint.Undo.Length; } }
    public int RedoCount { get { lock (gate) return checkpoint.Redo.Length; } }
    public static async Task<PlanSession> CreateAsync(PlanStore store, PlanDocument document, DateOnly today)
    {
        PlanOperations.ValidateDocument(document, today);
        var loaded = await store.LoadAsync(document.Project).ConfigureAwait(false);
        if (loaded.Status != PlanLoadStatus.Missing) throw new InvalidOperationException(loaded.Error ?? "既存の計画を開いてください。");
        var session = new PlanSession(store, new(1, 0, document, [], []), null);
        await session.FlushAsync().ConfigureAwait(false);
        return session;
    }
    public static async Task<PlanOpenResult> OpenAsync(PlanStore store, ScopedId project, DateOnly today)
    {
        var result = await store.LoadAsync(project).ConfigureAwait(false);
        if (result.Status != PlanLoadStatus.Loaded) return new(result.Status, null, result.Error);
        var session = new PlanSession(store, result.Checkpoint!, result.Fingerprint);
        if (session.Document.Sync.Publish is { } attempt && !attempt.DispatchStarted && attempt.Writes.All(w => w.State == PlanWriteState.Pending))
        {
            var saved = await session.SaveSync(session.Document.Sync with { Publish = null, NotBefore = null, Unverified = [] }).ConfigureAwait(false);
            if (!saved.Succeeded) return new(PlanLoadStatus.Blocked, null, saved.Error);
        }
        return new(PlanLoadStatus.Loaded, session, null);
    }
    public Task<PlanSaveResult> Execute(PlanCommand command, DateOnly today) => ExecuteCore(command, today, false);
    internal Task<PlanSaveResult> AcceptFieldSettings(ProjectPlanSettings settings, DateOnly today) => ExecuteCore(new ReplacePlanSettings(settings), today, true);
    private Task<PlanSaveResult> ExecuteCore(PlanCommand command, DateOnly today, bool fromRemote)
    {
        lock (gate)
        {
            if (!fromRemote) RequireEditable(command);
            else PlanOperations.Require(remoteOperation && checkpoint.Document.Sync.Publish is null, "列設定を変更できません。");
            var (state, kind) = PlanOperations.Apply(checkpoint.Document, command, today);
            var patch = PlanOperations.Difference(checkpoint.Document.State, state, kind);
            if (patch.Rows.IsEmpty && patch.BeforeOrder is null && patch.BeforeSettings is null) return CurrentSave();
            var publish = CurrentCreationInputs(checkpoint.Document.Sync.Publish, state);
            var undo = checkpoint.Undo.Add(patch);
            if (undo.Length > HistoryLimit) undo = undo.RemoveAt(0);
            checkpoint = checkpoint with { Revision = checked(checkpoint.Revision + 1), Document = checkpoint.Document with { State = state, Sync = checkpoint.Document.Sync with { Publish = publish, Failures = checkpoint.Document.Sync.Failures.Where(f => !patch.Rows.Any(r => r.Identity == f.Identity && (r.Before is null || r.After is null || f.Field is PlanField.NewTask or PlanField.Order or PlanField.SubIssueOrder || PlanValues.Get(r.Before, f.Field) != PlanValues.Get(r.After, f.Field)))).ToImmutableArray() } }, Undo = undo, Redo = [] };
            return QueueSave();
        }
    }
    public Task<PlanSaveResult> Undo(DateOnly today) => Travel(false, today);
    public Task<PlanSaveResult> Redo(DateOnly today) => Travel(true, today);
    private Task<PlanSaveResult> Travel(bool forward, DateOnly today)
    {
        lock (gate)
        {
            var from = forward ? checkpoint.Redo : checkpoint.Undo;
            if (from.IsEmpty) return CurrentSave();
            var patch = from[^1];
            RequireEditable();
            if (checkpoint.Document.Sync.Publish is not null && patch.BeforeSettings is not null)
                throw new InvalidOperationException("発行結果を確認してから設定を変更してください。");
            var state = PlanOperations.Replay(checkpoint.Document.State, patch, forward);
            if (checkpoint.Document.Sync.Publish is { } attempt && attempt.Writes.Any(w => !state.Rows.Any(r => r.Identity == w.Identity)))
                throw new InvalidOperationException("発行結果を確認してから行を取り消してください。");
            var baseline = PlanOperations.ReplayBaseline(checkpoint.Document.Baseline, patch, forward);
            PlanOperations.ValidateDocument(checkpoint.Document with { State = state, Baseline = baseline }, today);
            checkpoint = checkpoint with { Revision = checked(checkpoint.Revision + 1), Document = checkpoint.Document with { State = state, Baseline = baseline, Sync = checkpoint.Document.Sync with { Publish = CurrentCreationInputs(checkpoint.Document.Sync.Publish, state), Conflicts = (forward ? patch.AfterConflicts : patch.BeforeConflicts) ?? checkpoint.Document.Sync.Conflicts, Unavailable = (forward ? patch.AfterUnavailable : patch.BeforeUnavailable) ?? checkpoint.Document.Sync.Unavailable } },
                Undo = forward ? checkpoint.Undo.Add(patch) : checkpoint.Undo.RemoveAt(checkpoint.Undo.Length - 1),
                Redo = forward ? checkpoint.Redo.RemoveAt(checkpoint.Redo.Length - 1) : checkpoint.Redo.Add(patch) };
            return QueueSave();
        }
    }
    private Task<PlanSaveResult> CurrentSave() => pending ?? Task.FromResult(lastSave);
    private Task<PlanSaveResult> QueueSave()
    {
        checkpoint = PlanJson.RetainReplayableHistory(checkpoint);
        if (pending is not null) return pending;
        pending = Task.Run(SaveLoop);
        return pending;
    }
    private async Task<PlanSaveResult> SaveLoop()
    {
        while (true)
        {
            PlanCheckpoint captured; string? expected;
            lock (gate) { captured = checkpoint; expected = fingerprint; }
            var result = await store.SaveAsync(captured, expected).ConfigureAwait(false);
            lock (gate)
            {
                lastSave = result;
                if (result.Succeeded) { fingerprint = result.Fingerprint; savedRevision = captured.Revision; }
                if (!result.Succeeded || savedRevision == checkpoint.Revision) { pending = null; return result; }
            }
        }
    }
    public Task<PlanSaveResult> FlushAsync()
    {
        lock (gate)
        {
            // Flush waits for work already requested; only explicit retry restarts a failed save.
            if (pending is not null || savedRevision == checkpoint.Revision || !lastSave.Succeeded) return CurrentSave();
            return QueueSave();
        }
    }
    public Task<PlanSaveResult> RetrySaveAsync() { lock (gate) return savedRevision == checkpoint.Revision ? CurrentSave() : QueueSave(); }
    public PlanUnpublished Changes(DateOnly today) => PlanOperations.Changes(Document, today);
    public IReadOnlyList<ScheduledTask> Schedule(DateOnly today) => PlanOperations.Schedule(Document, today);
    internal Task<PlanSaveResult> SaveSync(PlanSync sync)
    {
        lock (gate)
        {
            if (checkpoint.Document.Sync.Publish is { } previous)
            {
                PlanOperations.Require(sync.Publish is not null || !previous.DispatchStarted, "送信済みの発行結果を先に確認してください。");
                PlanOperations.Require(sync.Publish is null || sync.Publish.RunId == previous.RunId, "未確認の発行記録を置き換えられません。");
                foreach (var creation in previous.Writes.Where(w => w.Stage == PlanPublishStage.Create))
                {
                    if (sync.Publish is null)
                        PlanOperations.Require(creation.State != PlanWriteState.Dispatched && (creation.ResultId is null ||
                            checkpoint.Document.Baseline.Rows.Any(r => r.Identity == creation.ResultId)), "作成結果を確認してから記録を確定してください。");
                    else if (creation.ResultId is not null)
                        PlanOperations.Require(sync.Publish.Writes.Any(w => w.Key == creation.Key && w.ResultId == creation.ResultId), "取得済みのIssue識別子を変更または破棄できません。");
                }
            }
            if (sync.Publish is { } next)
            {
                next = next with { DispatchStarted = next.DispatchStarted || checkpoint.Document.Sync.Publish?.DispatchStarted == true ||
                    next.Writes.Any(w => w.State != PlanWriteState.Pending) };
                PlanPublishPlan.Validate(next, checkpoint.Document);
                sync = sync with { Publish = next };
            }
            checkpoint = checkpoint with { Revision = checked(checkpoint.Revision + 1), Document = checkpoint.Document with { Sync = sync } };
            return QueueSave();
        }
    }
    public Task<PlanSaveResult> ResolveConflict(string identity, PlanField field, bool useGitHub, DateOnly today)
    {
        lock (gate)
        {
            var document = checkpoint.Document;
            if (remoteOperation) throw new InvalidOperationException("更新または発行が進行中です。");
            var conflict = document.Sync.Conflicts.Single(c => c.Identity == identity && c.Field == field);
            var state = document.State;
            if (useGitHub)
            {
                if (field == PlanField.SubIssueOrder) state = PlanMerge.SiblingOrder(state, identity, System.Text.Json.JsonSerializer.Deserialize<string[]>(conflict.Remote!)!);
                else if (field == PlanField.Order)
                {
                    var order = System.Text.Json.JsonSerializer.Deserialize<string[]>(conflict.Remote!)!;
                    var rows = state.Rows.ToDictionary(r => r.Identity);
                    state = state with { Rows = PlanMerge.MergeOrder(order, state.Rows.Select(r => r.Identity)).Select(id => rows[id]).ToImmutableArray() };
                }
                else state = state with { Rows = state.Rows.Select(r => r.Identity == identity ? PlanValues.Set(r, field, conflict.Remote!) : r).ToImmutableArray() };
            }
            var conflicts = document.Sync.Conflicts.Remove(conflict);
            var publish = document.Sync.Publish;
            if (useGitHub && publish is not null)
            {
                var remaining = publish.Writes.Where(w => !(w.State is PlanWriteState.Pending or PlanWriteState.Failed && (field == PlanField.Order || w.Identity == identity || field == PlanField.SubIssueOrder && System.Text.Json.Nodes.JsonNode.Parse(w.Input)?["issueId"]?.GetValue<string>() == identity) && PlanVerification.Field(w, document.State.Settings) == field)).ToImmutableArray();
                publish = remaining.IsEmpty ? null : publish with { Writes = remaining };
            }
            var patch = PlanOperations.Difference(document.State, state, PlanOperationKind.ResolveConflict) with
                { BeforeConflicts = document.Sync.Conflicts, AfterConflicts = conflicts };
            PlanOperations.ValidateDocument(document with { State = state }, today);
            var undo = checkpoint.Undo.Add(patch); if (undo.Length > HistoryLimit) undo = undo.RemoveAt(0);
            checkpoint = checkpoint with { Revision = checked(checkpoint.Revision + 1), Document = document with { State = state, Sync = document.Sync with { Conflicts = conflicts, Publish = publish } }, Undo = undo, Redo = [] };
            return QueueSave();
        }
    }
    public Task<PlanSaveResult> ResolveUnavailable(string identity, bool copyToNew, DateOnly today)
    {
        lock (gate)
        {
            var document = checkpoint.Document;
            if (!document.Sync.Unavailable.Contains(identity)) throw new ArgumentException("利用不可の行ではありません。");
            if (remoteOperation) throw new InvalidOperationException("更新または発行が進行中です。");
            var source = document.State.Rows.Single(r => r.Identity == identity);
            var replacement = copyToNew ? PlanRow.New(source.Title, source.Repository).Identity : null;
            var rows = document.State.Rows.Where(r => r.Identity != identity || copyToNew).Select(r => r with
            {
                Identity = r.Identity == identity ? replacement! : r.Identity,
                Closed = r.Identity == identity ? false : r.Closed,
                Status = r.Identity == identity ? null : r.Status,
                Parent = r.Parent == identity ? replacement : r.Parent,
                Predecessors = r.Predecessors.Where(id => id != identity || copyToNew).Select(id => id == identity ? replacement! : id).ToImmutableArray()
            }).ToImmutableArray();
            var baseline = document.Baseline with { Rows = document.Baseline.Rows.Where(r => r.Identity != identity).ToImmutableArray() };
            var state = document.State with { Rows = rows };
            var unavailable = document.Sync.Unavailable.Remove(identity);
            var conflicts = document.Sync.Conflicts.Where(c => c.Identity != identity).ToImmutableArray();
            var publish = document.Sync.Publish;
            if (publish is not null)
            {
                var remaining = publish.Writes.Where(w => w.Identity != identity && !InputReferences(w.Input, identity)).ToImmutableArray();
                publish = remaining.IsEmpty ? null : publish with { Writes = remaining };
            }
            var updated = document with { Baseline = baseline, State = state, Sync = document.Sync with { Unavailable = unavailable, Conflicts = conflicts, Publish = publish, Failures = document.Sync.Failures.Where(f => f.Identity != identity).ToImmutableArray() } };
            PlanOperations.ValidateDocument(updated, today);
            var patch = PlanOperations.Difference(document.State, state, PlanOperationKind.ResolveConflict) with
            {
                DiscardedRows = document.Baseline.Rows.Where(r => r.Identity == identity).ToImmutableArray(),
                BeforeUnavailable = document.Sync.Unavailable, AfterUnavailable = unavailable,
                BeforeConflicts = document.Sync.Conflicts, AfterConflicts = conflicts
            };
            var undo = checkpoint.Undo.Add(patch); if (undo.Length > HistoryLimit) undo = undo.RemoveAt(0);
            checkpoint = checkpoint with { Revision = checked(checkpoint.Revision + 1), Document = updated, Undo = undo, Redo = [] };
            return QueueSave();
        }
    }
    internal Task<PlanSaveResult> AcceptRefresh(PlanRemoteSnapshot remote, DateOnly today, PlanPublishProgress? adopted = null)
    {
        lock (gate)
        {
            var original = checkpoint.Document;
            var merged = PlanMerge.NativeOrder(original, PlanMerge.Merge(original, remote.Baseline), remote.SubIssueOrders);
            merged = merged with { Sync = merged.Sync with { DraftCount = remote.DraftCount, PullRequestCount = remote.PullRequestCount } };
            PlanOperations.ValidateDocument(merged, today);
            // Remote changes made by this publish are baseline adoption, not external edits to historical inputs.
            var historyBaseline = remote.Baseline; var historyOrders = remote.SubIssueOrders;
            if (adopted is not null)
            {
                var oldRows = original.Baseline.Rows.ToDictionary(r => r.Identity);
                var observedRows = historyBaseline.Rows.ToDictionary(r => r.Identity);
                foreach (var write in adopted.Writes.Where(w => w.State == PlanWriteState.Succeeded && w.Stage is not (PlanPublishStage.Create or PlanPublishStage.Add)))
                {
                    var field = PlanVerification.Field(write, original.State.Settings);
                    if (field == PlanField.SubIssueOrder) { historyOrders = original.Sync.NativeOrders; continue; }
                    if (field == PlanField.Order) continue;
                    if (oldRows.TryGetValue(write.Identity, out var old) && observedRows.TryGetValue(write.Identity, out var observed))
                        observedRows[write.Identity] = PlanValues.Set(observed, field, PlanValues.Get(old, field));
                }
                var sequence = adopted.Writes.Any(w => w.Stage == PlanPublishStage.Order && w.State == PlanWriteState.Succeeded)
                    ? original.Baseline.Rows.Select(r => r.Identity).Where(observedRows.ContainsKey).Concat(historyBaseline.Rows.Select(r => r.Identity)).Distinct()
                    : historyBaseline.Rows.Select(r => r.Identity);
                historyBaseline = historyBaseline with { Rows = sequence.Select(id => observedRows[id]).ToImmutableArray() };
            }
            var undo = RebaseHistory(original, checkpoint.Undo, historyBaseline, historyOrders);
            var redo = RebaseHistory(original, checkpoint.Redo, historyBaseline, historyOrders, forward: true);
            checkpoint = checkpoint with { Revision = checked(checkpoint.Revision + 1), Document = merged, Undo = undo, Redo = redo };
            return QueueSave();
        }
    }
    private static ImmutableArray<PlanPatch> RebaseHistory(PlanDocument original, ImmutableArray<PlanPatch> patches,
        PlanBaseline historyBaseline, ImmutableDictionary<string, ImmutableArray<string>> historyOrders, bool forward = false)
    {
        // Rebase complete row patches so unrelated remote changes do not invalidate local Undo.
        var current = original; var history = new List<PlanPatch>();
        for (var i = patches.Length - 1; i >= 0; i--)
        {
            var patch = patches[i];
            var before = current with { State = PlanOperations.Replay(current.State, patch, forward), Baseline = PlanOperations.ReplayBaseline(current.Baseline, patch, forward),
                Sync = current.Sync with { Conflicts = (forward ? patch.AfterConflicts : patch.BeforeConflicts) ?? current.Sync.Conflicts, Unavailable = (forward ? patch.AfterUnavailable : patch.BeforeUnavailable) ?? current.Sync.Unavailable } };
            var rebasedBefore = PlanMerge.NativeOrder(before, PlanMerge.Merge(before, historyBaseline), historyOrders);
            var rebasedAfter = PlanMerge.NativeOrder(current, PlanMerge.Merge(current, historyBaseline), historyOrders);
            var rebased = forward ? PlanOperations.Difference(rebasedAfter.State, rebasedBefore.State, patch.Kind)
                : PlanOperations.Difference(rebasedBefore.State, rebasedAfter.State, patch.Kind);
            if (!patch.DiscardedRows.IsEmpty) rebased = rebased with
            {
                DiscardedRows = (forward ? rebasedAfter : rebasedBefore).Baseline.Rows.Where(r => !(forward ? rebasedBefore : rebasedAfter).Baseline.Rows.Any(after => after.Identity == r.Identity)).ToImmutableArray(),
                BeforeUnavailable = forward ? rebasedAfter.Sync.Unavailable : rebasedBefore.Sync.Unavailable, AfterUnavailable = forward ? rebasedBefore.Sync.Unavailable : rebasedAfter.Sync.Unavailable
            };
            if (patch.BeforeConflicts.HasValue) rebased = rebased with { BeforeConflicts = forward ? rebasedAfter.Sync.Conflicts : rebasedBefore.Sync.Conflicts, AfterConflicts = forward ? rebasedBefore.Sync.Conflicts : rebasedAfter.Sync.Conflicts };
            if (!rebased.Rows.IsEmpty || rebased.BeforeOrder.HasValue || rebased.BeforeSettings is not null || rebased.BeforeConflicts.HasValue || !rebased.DiscardedRows.IsEmpty)
                history.Add(rebased);
            current = before;
        }
        return history.AsEnumerable().Reverse().ToImmutableArray();
    }
    internal Task<PlanSaveResult> AcceptPublished(PlanRemoteSnapshot remote, DateOnly today)
    {
        lock (gate)
        {
            var document = checkpoint.Document;
            var progress = document.Sync.Publish ?? throw new InvalidOperationException("発行内容がありません。");
            var promoted = progress.Writes.Where(w => w.Stage == PlanPublishStage.Create && w.Identity.StartsWith("local:", StringComparison.Ordinal) && w.ResultId is not null && remote.Items.ContainsKey(w.ResultId))
                .ToDictionary(w => w.Identity, w => w.ResultId!);
            string Id(string id) => promoted.GetValueOrDefault(id) ?? id;
            PlanRow Row(PlanRow row)
            {
                var observed = promoted.ContainsKey(row.Identity) ? remote.Baseline.Rows.Single(r => r.Identity == Id(row.Identity)) : null;
                return row with { Identity = Id(row.Identity), Parent = row.Parent is null ? null : Id(row.Parent),
                    Predecessors = row.Predecessors.Select(Id).ToImmutableArray(), Closed = observed?.Closed ?? row.Closed,
                    Status = observed is null ? row.Status : observed.Status };
            }
            PlanState State(PlanState state) => state with { Rows = state.Rows.Select(Row).ToImmutableArray() };
            var state = State(document.State);
            var histories = checkpoint.Undo;
            // Creation is an irreversible history barrier. Later edits still remain individually undoable.
            var barrier = -1;
            for (var i = 0; i < histories.Length; i++)
                if (histories[i].Rows.Any(r => promoted.ContainsKey(r.Identity) && r.Before is null)) barrier = i;
            PlanPatch Promote(PlanPatch p) => p with
            {
                Rows = p.Rows.Select(r => new PlanRowChange(Id(r.Identity), r.Before is null ? null : Row(r.Before), r.After is null ? null : Row(r.After))).ToImmutableArray(),
                BeforeOrder = p.BeforeOrder?.Select(Id).ToImmutableArray(), AfterOrder = p.AfterOrder?.Select(Id).ToImmutableArray()
            };
            var undo = histories.Skip(barrier + 1).Select(Promote).ToImmutableArray();
            var redo = checkpoint.Redo.Select(Promote).ToImmutableArray();
            var writes = progress.Writes.Select(w => PlanVerification.Verify(w, progress, remote, document.State.Settings)
                ? w with { State = PlanWriteState.Succeeded, Error = null }
                : w with { State = w.Stage == PlanPublishStage.Create && w.State == PlanWriteState.Dispatched ? w.State : PlanWriteState.Failed, Error = w.Error ?? "VerificationMismatch" }).ToImmutableArray();
            // Keep the immutable progress only while an operation is unresolved; its local identities remain lookup keys.
            var unresolvedCreation = writes.Any(w => w.Stage == PlanPublishStage.Create &&
                (w.State == PlanWriteState.Dispatched || w.ResultId is not null && !remote.Items.ContainsKey(w.ResultId)));
            var remaining = unresolvedCreation ? progress with { Writes = writes } : null;
            var failures = writes.Where(w => w.State != PlanWriteState.Succeeded).Select(w => new PlanPublishFailure(Id(w.Identity),
                PlanVerification.Field(w, document.State.Settings), w.Error ?? "NotDispatched")).DistinctBy(f => (f.Identity, f.Field)).ToImmutableArray();
            // A failed add keeps its local identity. Once promoted, a remaining write must use the Issue identity on resume.
            if (remaining is not null && promoted.Count > 0)
            {
                remaining = remaining with { Writes = remaining.Writes.Select(w => w with
                {
                    Identity = Id(w.Identity), Input = ReplaceIdentities(w.Input, promoted)
                }).ToImmutableArray() };
            }
            var baseline = remote.Baseline;
            var missing = document.Baseline.Rows.Where(r => !baseline.Rows.Any(x => x.Identity == r.Identity)).ToImmutableArray();
            baseline = baseline with { Rows = baseline.Rows.AddRange(missing) };
            // Hide this publish's own fields from the external-change merge, including unfinished writes.
            // Historical local values must remain undoable even after their remote baseline advances.
            var original = document with { State = state, Baseline = document.Baseline with { Rows = document.Baseline.Rows.Select(Row).ToImmutableArray() } };
            var priorRows = original.Baseline.Rows.ToDictionary(r => r.Identity);
            foreach (var row in remote.Baseline.Rows.Where(r => promoted.Values.Contains(r.Identity))) priorRows[row.Identity] = row;
            original = original with { Baseline = original.Baseline with { Rows = priorRows.Values.ToImmutableArray() } };
            var externalRows = baseline.Rows.ToDictionary(r => r.Identity);
            foreach (var write in writes)
            {
                var field = PlanVerification.Field(write, state.Settings);
                if (PlanValues.RowFields.Contains(field) && priorRows.TryGetValue(Id(write.Identity), out var prior) && externalRows.TryGetValue(Id(write.Identity), out var observed))
                    externalRows[observed.Identity] = PlanValues.Set(observed, field, PlanValues.Get(prior, field));
            }
            var externalOrder = writes.Any(w => w.Stage == PlanPublishStage.Order)
                ? original.Baseline.Rows.Select(r => r.Identity).Concat(baseline.Rows.Select(r => r.Identity)).Distinct()
                : baseline.Rows.Select(r => r.Identity);
            var external = baseline with { Rows = externalOrder.Select(id => externalRows[id]).ToImmutableArray() };
            var orders = writes.Any(w => PlanVerification.Field(w, state.Settings) == PlanField.SubIssueOrder) ? original.Sync.NativeOrders : remote.SubIssueOrders;
            var merged = PlanMerge.NativeOrder(original, PlanMerge.Merge(original, external), orders);
            undo = RebaseHistory(original, undo, external, orders);
            redo = RebaseHistory(original, redo, external, orders, forward: true);
            var updated = merged with { Baseline = baseline, Sync = merged.Sync with
                { Publish = remaining, Failures = failures, Unverified = [], NativeOrders = remote.SubIssueOrders.Where(p => state.Rows.Any(r => r.Identity == p.Key)).ToImmutableDictionary(), DraftCount = remote.DraftCount, PullRequestCount = remote.PullRequestCount,
                    Unavailable = missing.Select(r => r.Identity).ToImmutableArray() } };
            PlanOperations.ValidateDocument(updated, today);
            checkpoint = checkpoint with { Revision = checked(checkpoint.Revision + 1), Document = updated, Undo = undo, Redo = redo };
            return QueueSave();
        }
    }
    private static bool InputReferences(string json, string identity)
    {
        using var input = System.Text.Json.JsonDocument.Parse(json);
        bool Contains(System.Text.Json.JsonElement element, bool identifier = false) => element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => identifier && element.GetString() is { } text && (text == identity || text == "item:" + identity),
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray().Any(e => Contains(e, identifier)),
            System.Text.Json.JsonValueKind.Object => element.EnumerateObject().Any(p => Contains(p.Value, PlanPublishPlan.IsIdentifierField(p.Name))), _ => false
        };
        return Contains(input.RootElement);
    }
    private static string ReplaceIdentities(string json, IReadOnlyDictionary<string, string> ids)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        void Visit(System.Text.Json.Nodes.JsonNode value, bool identifier = false)
        {
            if (value is System.Text.Json.Nodes.JsonObject obj)
            {
                foreach (var key in obj.Select(p => p.Key).ToArray())
                {
                    if (PlanPublishPlan.IsIdentifierField(key) && obj[key] is System.Text.Json.Nodes.JsonValue scalar && scalar.TryGetValue<string>(out var text))
                    {
                        if (ids.TryGetValue(text, out var id)) obj[key] = id;
                        else if (text.StartsWith("item:", StringComparison.Ordinal) && ids.TryGetValue(text[5..], out id)) obj[key] = "item:" + id;
                    }
                    else if (obj[key] is { } child) Visit(child, PlanPublishPlan.IsIdentifierField(key));
                }
            }
            else if (identifier && value is System.Text.Json.Nodes.JsonArray array)
                for (var i = 0; i < array.Count; i++)
                    if (array[i] is System.Text.Json.Nodes.JsonValue scalar && scalar.TryGetValue<string>(out var text) && ids.TryGetValue(text, out var id)) array[i] = id;
        }
        Visit(node); return node.ToJsonString();
    }
    public Task ExportSettingsAsync(string path) => PlanStore.ExportSettings(path, Document.State.Settings);
    public async Task<PlanImportResult> ImportSettingsAsync(string path, DateOnly today)
    {
        try
        {
            var imported = PlanJson.Read<PlanSettingsFile>(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
            PlanOperations.Require(imported.Version == 1, "未対応の設定ファイル形式です。");
            PlanOperations.ValidateSettings(imported.Settings);
            ImmutableArray<string> warnings; Task<PlanSaveResult> save;
            lock (gate)
            {
                var baseline = checkpoint.Document.Baseline;
                var knownPeople = baseline.Rows.SelectMany(r => r.Assignees).ToHashSet(); var knownFields = baseline.Columns.Select(c => c.Id).ToHashSet();
                warnings = imported.Settings.People.Where(p => !knownPeople.Contains(p.Identity)).Select(p => $"プロジェクトに未確認の担当者: {p.Name}")
                    .Concat(imported.Settings.Columns.Where(c => !knownFields.Contains(c.FieldId)).Select(c => $"プロジェクトに未確認の列: {c.Name}")).ToImmutableArray();
                save = Execute(new ReplacePlanSettings(imported.Settings), today);
            }
            return new(true, warnings, null, await save.ConfigureAwait(false));
        }
        catch (Exception ex) when (PlanJson.IsDataError(ex) || ex is IOException or UnauthorizedAccessException)
        { return new(false, [], ex.Message, null); }
    }
}
