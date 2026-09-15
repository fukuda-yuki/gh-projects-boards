namespace GhProjectsBoards.Core.Projects;

internal sealed record FieldKey(string Kind, string NodeId, string? ProjectId = null, string? FieldId = null);
internal sealed record LocalValue(string? Value, bool Clear = false);
internal sealed record DraftField(FieldKey Key, string? Baseline, ScopedId SourceProject, DateTimeOffset RetrievedAt,
    LocalValue? Change, string? Buffer, long Stamp, FieldObservation? Observation = null, bool Conflict = false);
internal sealed record FieldChange(FieldKey Key, DraftField Before, DraftField After);
internal sealed record EditTransaction(string Id, string ProjectId, FieldChange[] Changes, string? InvalidReason = null, bool Resolution = false,
    LocalRowChange[]? Rows = null);
internal sealed record DraftRecord(int Version, ConnectionScope Scope, long Revision, DraftField[] Fields, EditTransaction[] History,
    RegistrationStore.RegistrationRecord[]? Registrations = null, string[]? StructuralChanges = null, ApplyBatch[]? Journal = null,
    LocalRow[]? LocalRows = null, ProjectColumnPreferences[]? ColumnPreferences = null);
internal sealed record EditCell(FieldKey? Key, string Display, string? Baseline, string? Reason, SelectOption[] Options,
    ValueAvailability Availability = ValueAvailability.Present, ConnectionScope? Scope = null)
{
    public bool Editable => Key is not null && Reason is null;
}
internal sealed record EditRow(string ItemId, EditCell[] Cells, bool IsLocal = false);

// All field and transaction state for one identity scope travels through one durable record.
internal sealed partial class EditingWorkspace
{
    private readonly Dictionary<FieldKey, DraftField> fields = [];
    private readonly List<EditTransaction> history = [];
    public ConnectionScope Scope { get; }
    public long Revision { get; private set; }
    public EditingWorkspace(ConnectionScope scope) => Scope = scope;
    public IReadOnlyCollection<DraftField> Fields => fields.Values;
    public int DifferenceCount => fields.Values.Count(f => f.Change is not null);
    public DraftRecord Snapshot() => new(6, Scope, Revision, fields.Values.ToArray(), history.ToArray(), registrations, structuralChanges, journal.ToArray(), localRows.ToArray(), columnPreferences.ToArray());
    public static EditingWorkspace Restore(DraftRecord record)
    {
        DraftStore.Validate(record);
        var result = new EditingWorkspace(record.Scope) { Revision = record.Revision };
        foreach (var field in record.Fields) result.fields.Add(field.Key, field);
        result.history.AddRange(record.History);
        result.registrations = record.Registrations; result.structuralChanges = record.StructuralChanges ?? [];
        result.journal.AddRange(record.Journal ?? []);
        result.localRows.AddRange(record.LocalRows ?? []);
        result.columnPreferences.AddRange(record.ColumnPreferences ?? []);
        return result;
    }
    public string? Value(EditCell cell) => IsLocal(cell.Key) ? LocalValueFor(cell) : cell.Key is { } key && fields.TryGetValue(key, out var f)
        ? f.Change is { } value ? value.Value : f.Baseline : cell.Baseline;
    public string? Buffer(EditCell cell) => IsLocal(cell.Key) ? LocalBufferFor(cell) : cell.Key is { } key && fields.TryGetValue(key, out var f) ? f.Buffer : null;
    public bool Changed(EditCell cell) => cell.Key is { } key && fields.TryGetValue(key, out var f) && f.Change is not null;
    public EditRow[] Open(ProjectRegistration registration)
    {
        var p = registration.Snapshot;
        if (p.Id.Scope != Scope) throw new InvalidOperationException("Scope mismatch");
        var columns = LocalColumns(registration);
        string? Permission(CapabilityObservation? c) => c?.CanUpdate switch { true when c.ObservedAt != default => null, false => "更新権限なし（取得時の観測）", _ => "更新権限未確認" };
        return p.Items.Where(item => !Creations.Any(c => c.Verified is not null && c.Verified.Id == item.ContentId?.NodeId
            && localRows.Any(r => r.Id == c.LocalId && r.ProjectId == p.Id.NodeId))).Select(item =>
        {
            var cells = new List<EditCell>();
            var issue = item.Kind == ProjectItemKind.Issue && item.ContentId is { } id ? p.Issues.GetValueOrDefault(id) : null;
            var title = new EditCell(issue is null ? null : new("Title", issue.Id.NodeId), issue?.Title.Value ?? item.Kind.ToString(),
                issue?.Title.Value, issue is null ? "Issue以外は編集対象外" : issue.Title.Availability != ValueAvailability.Present ? AvailabilityText(issue.Title.Availability) : Permission(issue.Capability), [], issue?.Title.Availability ?? ValueAvailability.Unavailable);
            cells.Add(title);
            foreach (var field in columns)
            {
                var v = item.Values.SingleOrDefault(v => v.FieldId == field.Id);
                var reason = issue is null ? "Issue以外は編集対象外" : item.IsArchived ? "アーカイブ項目は編集対象外"
                    : field.Availability != ValueAvailability.Present ? AvailabilityText(field.Availability)
                    : v?.Availability is not (ValueAvailability.Present or ValueAvailability.Empty) ? AvailabilityText(v?.Availability ?? ValueAvailability.NotLoaded) : Permission(p.Capability);
                var key = new FieldKey("Select", item.Id.NodeId, p.Id.NodeId, field.Id.NodeId);
                if (fields.TryGetValue(key, out var saved) && (saved.Change?.Value ?? saved.Baseline) is { } savedOption
                    && !field.Options.Any(o => o.Id == savedOption)) reason = "保存された選択肢IDを確認できません（要照合）";
                cells.Add(new(key, field.Name, v?.OptionId,
                    reason, field.Options.ToArray(), v?.Availability ?? ValueAvailability.NotLoaded));
            }
            var reference = issue is null ? item.Kind switch { ProjectItemKind.PullRequest => "Pull Request", ProjectItemKind.Draft => "GitHub Draft", ProjectItemKind.Unavailable => "閲覧不可", _ => "非対応" }
                : $"{issue.Repository.NameWithOwner} #{issue.Number} | {issue.State.Value}";
            reference += item.IsArchived ? " | アーカイブ" : "";
            var unsupported = p.Fields.Except(columns).Select(f => $"{f.Name}: {AvailabilityText(f.Availability)}");
            cells.Add(new(null, reference + " | " + string.Join(" / ", unsupported), null, "参照専用", []));
            cells = cells.Select(c => c with { Scope = Scope }).ToList();
            foreach (var cell in cells.Where(c => c.Editable))
            {
                if (!fields.ContainsKey(cell.Key!))
                {
                    fields.Add(cell.Key!, new(cell.Key!, cell.Baseline, p.Id, registration.RetrievedAt, null, null, 0));
                    Revision++;
                }
            }
            return new EditRow(item.Id.NodeId, cells.ToArray());
        }).Concat(OpenLocal(registration, columns)).ToArray();
    }
    public static string AvailabilityText(ValueAvailability availability) => availability switch
    { ValueAvailability.Empty => "明示的な空値", ValueAvailability.Unsupported => "非対応", ValueAvailability.Unavailable => "閲覧不可", ValueAvailability.NotLoaded => "未取得", _ => "取得済み" };
    public void SetBuffer(EditCell cell, string? text)
    {
        if (cell.Scope != Scope) throw new InvalidOperationException("別プロフィールのセルは編集できません。");
        if (IsLocal(cell.Key)) { SetLocalBuffer(cell, text); return; }
        if (!cell.Editable || !fields.TryGetValue(cell.Key!, out var old)) return;
        if (old.Buffer == text) return;
        fields[cell.Key!] = old with { Buffer = text };
        Revision++;
    }
    public void Commit(string projectId, EditCell cell, string value, bool optionId = false)
        => Apply(projectId, [(cell, value, false, optionId)]);
    public void Clear(string projectId, IEnumerable<EditCell> cells)
        => Apply(projectId, cells.Select(c => (c, "", true, false)).ToArray());
    public void Paste(string projectId, EditRow[] rows, int row, int column, string tsv)
    {
        var matrix = ParseTsv(tsv);
        if (row < 0 || column < 0 || row + matrix.Length > rows.Length || matrix.Any(line => column + line.Length > rows[row].Cells.Length))
            throw new InvalidOperationException("貼り付け範囲が表の端を超えています。");
        var batch = new List<(EditCell, string, bool, bool)>();
        for (var r = 0; r < matrix.Length; r++)
            for (var c = 0; c < matrix[r].Length; c++)
            {
                var cell = rows[row + r].Cells[column + c];
                if (!cell.Editable) throw new InvalidOperationException($"行 {row + r + 1} 列 {column + c + 1}: {cell.Reason}");
                if (matrix[r][c] != "")
                {
                    try { ValidateText(cell, matrix[r][c]); }
                    catch (InvalidOperationException ex) { throw new InvalidOperationException($"行 {row + r + 1} 列 {column + c + 1}: {ex.Message}"); }
                    batch.Add((cell, matrix[r][c], false, false));
                }
            }
        Apply(projectId, batch.ToArray());
    }
    private static void ValidateText(EditCell cell, string text)
    {
        if (cell.Key?.Kind == "Title" && (string.IsNullOrWhiteSpace(text) || text.IndexOfAny(['\r','\n','\t']) >= 0)) throw new InvalidOperationException("タイトルは空欄・改行・タブにできません。");
        if (cell.Key?.Kind is "Select" or "LocalSelect" && cell.Options.Count(o => o.Name == text) != 1) throw new InvalidOperationException("選択肢が存在しないか同名で曖昧です。");
    }
    public static string[][] ParseTsv(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length > 1 && lines[^1] == "") lines = lines[..^1];
        var rows = lines.Select(line => line.Split('\t')).ToArray();
        if (rows.Any(r => r.Length != rows[0].Length)) throw new InvalidOperationException("TSVの列数が一致していません。");
        return rows;
    }
    private void Apply(string projectId, (EditCell Cell, string Text, bool Clear, bool OptionId)[] batch)
    {
        var changes = new Dictionary<FieldKey, FieldChange>();
        var rowChanges = new Dictionary<string, LocalRowChange>();
        foreach (var (cell, text, clear, optionId) in batch)
        {
            if (!cell.Editable) throw new InvalidOperationException(cell.Reason ?? "参照専用");
            if (IsLocal(cell.Key)) { PrepareLocalEdit(projectId, cell, text, clear, optionId, rowChanges); continue; }
            if (cell.Scope != Scope || cell.Key!.Kind == "Select" && cell.Key.ProjectId != projectId) throw new InvalidOperationException("操作対象のプロフィール・Projectが一致しません。");
            var key = cell.Key!;
            if (key.Kind == "Title" && (clear || string.IsNullOrWhiteSpace(text) || text.IndexOfAny(['\r','\n','\t']) >= 0))
                throw new InvalidOperationException("タイトルは空欄・改行・タブにできません。");
            string? value = text;
            if (key.Kind == "Select")
            {
                var options = cell.Options.Where(o => optionId ? o.Id == text : o.Name == text).ToArray();
                if (!clear && options.Length != 1) throw new InvalidOperationException($"{cell.Display}: 選択肢が存在しないか同名で曖昧です。");
                value = clear ? null : options[0].Id;
            }
            var old = fields[key];
            if (old.Conflict || old.Observation?.Reason is { } blocked && !blocked.StartsWith("未確定文字")) throw new InvalidOperationException(old.Observation?.Reason ?? "競合の比較画面で採用値を選択してください。");
            var change = value == old.Baseline ? null : new LocalValue(value, clear);
            var next = old with { Change = change, Buffer = null, Stamp = Revision + 1,
                Observation = old.Observation?.Reason?.StartsWith("未確定文字") == true ? old.Observation with { Reason = null } : old.Observation };
            if (changes.TryGetValue(key, out var duplicate) && duplicate.After.Change != next.Change)
                throw new InvalidOperationException("同じIssueへの値が競合しています。");
            changes[key] = new(key, old, next);
        }
        if (changes.Count == 0 && rowChanges.Count == 0) return;
        Revision++;
        foreach (var change in changes.Values) fields[change.Key] = change.After;
        foreach (var change in rowChanges.Values) ReplaceLocal(change.After!);
        history.Add(new(Guid.NewGuid().ToString("N"), projectId, changes.Values.ToArray(), Rows: rowChanges.Values.ToArray()));
    }
    public void Undo(string projectId)
    {
        SplitLockedCreationUndo(projectId);
        var transaction = history.LastOrDefault(t => t.ProjectId == projectId && t.InvalidReason is null);
        if (transaction is null) return;
        GuardLocalUndo(transaction);
        if (transaction.Changes.Any(c => !fields.TryGetValue(c.Key, out var current) || !SameUndoState(current, c.After)))
            throw new InvalidOperationException("後続の共有編集または編集中の文字があるため、この操作は元に戻せません。");
        foreach (var c in transaction.Changes) fields[c.Key] = c.Before with { Observation = fields[c.Key].Observation };
        UndoLocal(transaction);
        history.Remove(transaction); Revision++;
    }
    private static bool Belongs(FieldKey key, ProjectReadModel p) => key.ProjectId == p.Id.NodeId || key.Kind == "Title" && p.Issues.Keys.Any(id => id.NodeId == key.NodeId);
    public bool HasWork(ProjectReadModel p) => fields.Values.Any(f => (f.Change is not null || f.Buffer is not null || f.Conflict || f.Observation?.Reason is not null) && (Belongs(f.Key, p) || f.SourceProject == p.Id))
        || history.Any(t => t.Changes.Any(c => Belongs(c.Key, p)) || t.ProjectId == p.Id.NodeId && t.Rows is { Length: > 0 })
        || localRows.Any(r => r.ProjectId == p.Id.NodeId);
    public void Discard(ProjectReadModel project, IEnumerable<ProjectReadModel> surviving)
    {
        if (localRows.Any(r => r.ProjectId == project.Id.NodeId && CreationLocked(r.Id))) throw new InvalidOperationException("作成履歴のある行は破棄できません。");
        var remaining = surviving.Where(p => p.Id.Scope == Scope).ToArray();
        localRows.RemoveAll(r => r.ProjectId == project.Id.NodeId);
        history.RemoveAll(t => t.ProjectId == project.Id.NodeId && t.Rows is { Length: > 0 } && t.Changes.Length == 0);
        for (var i = 0; i < history.Count; i++)
            if (history[i].ProjectId == project.Id.NodeId && history[i].Rows is { Length: > 0 })
                history[i] = history[i] with { Rows = null, InvalidReason = "登録解除で新規行を破棄したため複合操作のUndoを無効化しました。共有値は保持しています。" };
        var shared = remaining.SelectMany(p => p.Issues.Keys).Select(k => k.NodeId).ToHashSet();
        var removed = fields.Keys.Where(k => k.ProjectId == project.Id.NodeId || k.Kind == "Title"
            && (project.Issues.Keys.Any(id => id.NodeId == k.NodeId) || fields[k].SourceProject == project.Id) && !shared.Contains(k.NodeId)).ToHashSet();
        foreach (var key in removed) fields.Remove(key);
        history.RemoveAll(t => t.Changes.Length > 0 && t.Changes.All(c => removed.Contains(c.Key)) && (t.Rows is null || t.Rows.Length == 0 || t.ProjectId == project.Id.NodeId));
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Changes.Any(c => removed.Contains(c.Key))) history[i] = history[i] with {
                Changes = history[i].Changes.Where(c => !removed.Contains(c.Key)).ToArray(), InvalidReason = "登録解除で一部の対象を破棄したため、複合操作のUndoを無効化しました。共有値は保持しています。" };
            if (history[i].ProjectId == project.Id.NodeId && history[i].InvalidReason is null)
            {
                var destination = remaining.FirstOrDefault(p => history[i].Changes.All(c => c.Key.Kind == "Title" && p.Issues.Keys.Any(id => id.NodeId == c.Key.NodeId)));
                history[i] = destination is null ? history[i] with { InvalidReason = "登録解除で操作対象が分かれたためUndoを無効化しました。共有値は保持しています。" }
                    : history[i] with { ProjectId = destination.Id.NodeId };
            }
        }
        Revision++;
    }
}
