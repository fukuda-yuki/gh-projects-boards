namespace GhProjectsBoards.Core.Projects;

internal sealed record FieldObservation(string Id, ScopedId Project, DateTimeOffset At, string? Value,
    ValueAvailability Availability, string? Reason, SelectOption[] Options);
internal sealed record ResolutionDecision(FieldKey Key, long Revision, string ObservationId);

internal sealed partial class EditingWorkspace
{
    private RegistrationStore.RegistrationRecord[]? registrations;
    private string[] structuralChanges = [];
    public IReadOnlyList<string> StructuralChanges => structuralChanges;
    public IEnumerable<string> UndoWarnings => history.Where(t => t.InvalidReason is not null).Select(t => t.InvalidReason!).Distinct();
    public bool HasCheckpoint => registrations is not null;
    public DraftField? Field(EditCell cell) => cell.Key is { } key ? fields.GetValueOrDefault(key) : null;
    public void SetRegistrations(IEnumerable<ProjectRegistration> values)
    {
        registrations = values.Where(r => r.Snapshot.Id.Scope == Scope).Select(RegistrationStore.ToRecord).ToArray(); Revision++;
    }
    private static bool SameUndoState(DraftField a, DraftField b) => a.Baseline == b.Baseline && a.Change == b.Change
        && a.Buffer == b.Buffer && a.Stamp == b.Stamp && a.Conflict == b.Conflict
        && a.Observation?.Reason == b.Observation?.Reason;

    public void Reconcile(ProjectRegistration previous, ProjectRegistration current)
    {
        var p = current.Snapshot;
        if (p.Id.Scope != Scope || p.Id != previous.Snapshot.Id || !p.FieldsComplete || !p.ItemsComplete || p.Items.Any(i => !i.ValuesComplete))
            throw new InvalidOperationException("完全な同一Projectの取得結果が必要です。");
        // Opening a cache only initializes missing keys; it never accepts an older shared observation.
        Open(previous);
        var changes = new List<string>();
        foreach (var item in p.Items.Where(i => !previous.Snapshot.Items.Any(old => old.Id == i.Id))) changes.Add($"追加項目: {item.Id.NodeId}");
        foreach (var item in previous.Snapshot.Items.Where(i => !p.Items.Any(next => next.Id == i.Id))) changes.Add($"Projectで未観測: {item.Id.NodeId}（Issue削除の証明ではありません）");
        foreach (var item in p.Items.Where(i => i.IsArchived)) changes.Add($"アーカイブ: {item.Id.NodeId}");
        foreach (var old in previous.Snapshot.Fields)
        {
            var next = p.Fields.SingleOrDefault(f => f.Id == old.Id);
            if (next is null || next.DataType != old.DataType || next.ValueOwner != old.ValueOwner) changes.Add($"フィールド削除・型/所有者変更: {old.Id.NodeId}");
            else foreach (var option in old.Options)
            {
                var found = next.Options.SingleOrDefault(o => o.Id == option.Id);
                if (found is null) changes.Add($"選択肢削除: {old.Id.NodeId} / {option.Id}");
                else if (found.Name != option.Name) changes.Add($"選択肢名変更: {option.Id} / {option.Name} → {found.Name}");
            }
        }
        var observed = new EditingWorkspace(Scope).Open(current).SelectMany(r => r.Cells).Where(c => c.Key is not null)
            .DistinctBy(c => c.Key).ToDictionary(c => c.Key!);
        foreach (var old in fields.Values.ToArray())
        {
            if (!Belongs(old.Key, previous.Snapshot) && !Belongs(old.Key, p) && old.SourceProject != p.Id) continue;
            if (old.Observation is { } accepted && accepted.At > current.RetrievedAt) continue;
            observed.TryGetValue(old.Key, out var cell);
            var reason = cell?.Reason;
            if (cell is null) reason = "項目・Issue・フィールドを確認できません。IDとローカル作業を保持しています。";
            if (cell is not null && cell.Availability is not (ValueAvailability.Present or ValueAvailability.Empty)) reason = AvailabilityText(cell.Availability);
            var local = old.Change is { } c ? c.Value : old.Baseline;
            if (old.Key.Kind == "Select" && local is not null && cell is not null && !cell.Options.Any(o => o.Id == local)) reason = "ローカルの選択肢IDが削除・変更されています。";
            var observation = new FieldObservation(Guid.NewGuid().ToString("N"), p.Id, current.RetrievedAt, cell?.Baseline,
                cell?.Availability ?? ValueAvailability.Unavailable, reason, cell?.Options ?? []);
            var next = old with { Observation = observation };
            if (reason is null)
            {
                var remote = observation.Value;
                // Inactive text is recoverable input, never an implicit local commit.
                if (old.Buffer is not null) next = next with { Observation = observation with { Reason = "未確定文字を保持しています。文字を確定・取消してから再取得してください。" } };
                else if (local == old.Baseline || local == remote)
                    next = next with { Baseline = remote, Change = null, Conflict = false, RetrievedAt = current.RetrievedAt };
                else if (remote == old.Baseline) next = next with { Conflict = false };
                else next = next with { Conflict = true };
            }
            foreach (var transaction in history.ToArray())
            {
                bool InvalidPreviousOption(FieldChange c) => old.Key.Kind == "Select" && c.Key == old.Key
                    && new[] { c.Before, c.After }.Any(state => (state.Change is { } change ? change.Value : state.Baseline) is { } value && !observation.Options.Any(o => o.Id == value));
                if (transaction.Changes.Any(c => c.Key == old.Key) && (!SameUndoState(old, next) || transaction.Changes.Any(InvalidPreviousOption)))
                    history[history.IndexOf(transaction)] = transaction with { InvalidReason = $"再取得により {old.Key.Kind}/{old.Key.NodeId} の以前の操作Undoを無効化しました。" };
            }
            fields[old.Key] = next;
        }
        Open(current); structuralChanges = changes.ToArray(); Revision++;
    }

    public ResolutionDecision Decision(FieldKey key)
    {
        var field = fields[key];
        if (!field.Conflict || field.Observation is not { Reason: null } remote || remote.Availability is not (ValueAvailability.Present or ValueAvailability.Empty))
            throw new InvalidOperationException(field.Observation?.Reason ?? "解消可能な競合ではありません。");
        return new(key, Revision, remote.Id);
    }
    public void Resolve(string projectId, ResolutionDecision decision, LocalValue chosen)
    {
        if (decision.Revision != Revision || !fields.TryGetValue(decision.Key, out var old) || old.Observation?.Id != decision.ObservationId)
            throw new InvalidOperationException("比較後に編集・取得がありました。最新の比較を開き直してください。");
        _ = Decision(decision.Key);
        var observation = old.Observation!;
        if (observation.Project.NodeId != projectId) throw new InvalidOperationException("比較対象のProjectが一致しません。");
        if (old.Buffer is not null) throw new InvalidOperationException("未確定文字を先に確認してください。");
        if (old.Key.Kind == "Title" ? chosen.Clear || string.IsNullOrWhiteSpace(chosen.Value) || chosen.Value.IndexOfAny(['\r','\n','\t']) >= 0
            : chosen.Clear ? chosen.Value is not null : !observation.Options.Any(o => o.Id == chosen.Value))
            throw new InvalidOperationException("採用値が現在のフィールド定義で無効です。");
        var next = old with { Baseline = observation.Value, Change = chosen.Value == observation.Value ? null : chosen,
            Conflict = false, RetrievedAt = observation.At, Stamp = Revision + 1 };
        fields[old.Key] = next; Revision++;
        history.Add(new(Guid.NewGuid().ToString("N"), projectId, [new(old.Key, old, next)], Resolution: true));
    }
}
