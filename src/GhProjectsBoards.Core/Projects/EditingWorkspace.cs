namespace GhProjectsBoards.Core.Projects;

internal sealed record FieldKey(string Kind, string NodeId, string? ProjectId = null, string? FieldId = null);
internal sealed record LocalValue(string? Value, bool Clear = false);
internal sealed record DraftField(FieldKey Key, string? Baseline, ScopedId SourceProject, DateTimeOffset RetrievedAt,
    LocalValue? Change, string? Buffer, long Stamp);
internal sealed record FieldChange(FieldKey Key, DraftField Before, DraftField After);
internal sealed record EditTransaction(string Id, string ProjectId, FieldChange[] Changes);
internal sealed record DraftRecord(int Version, ConnectionScope Scope, long Revision, DraftField[] Fields, EditTransaction[] History);
internal sealed record EditCell(FieldKey? Key, string Display, string? Baseline, string? Reason, SelectOption[] Options,
    ValueAvailability Availability = ValueAvailability.Present, ConnectionScope? Scope = null)
{
    public bool Editable => Key is not null && Reason is null;
}
internal sealed record EditRow(string ItemId, EditCell[] Cells);

// All field and transaction state for one identity scope travels through one durable record.
internal sealed class EditingWorkspace
{
    private readonly Dictionary<FieldKey, DraftField> fields = [];
    private readonly List<EditTransaction> history = [];
    public ConnectionScope Scope { get; }
    public long Revision { get; private set; }
    public EditingWorkspace(ConnectionScope scope) => Scope = scope;
    public IReadOnlyCollection<DraftField> Fields => fields.Values;
    public int DifferenceCount => fields.Values.Count(f => f.Change is not null);
    public DraftRecord Snapshot() => new(1, Scope, Revision, fields.Values.ToArray(), history.ToArray());
    public static EditingWorkspace Restore(DraftRecord record)
    {
        DraftStore.Validate(record);
        var result = new EditingWorkspace(record.Scope) { Revision = record.Revision };
        foreach (var field in record.Fields) result.fields.Add(field.Key, field);
        result.history.AddRange(record.History);
        return result;
    }
    public string? Value(EditCell cell) => cell.Key is { } key && fields.TryGetValue(key, out var f)
        ? f.Change is { } value ? value.Value : f.Baseline : cell.Baseline;
    public string? Buffer(EditCell cell) => cell.Key is { } key && fields.TryGetValue(key, out var f) ? f.Buffer : null;
    public bool Changed(EditCell cell) => cell.Key is { } key && fields.TryGetValue(key, out var f) && f.Change is not null;
    public EditRow[] Open(ProjectRegistration registration)
    {
        var p = registration.Snapshot;
        if (p.Id.Scope != Scope) throw new InvalidOperationException("Scope mismatch");
        var columns = p.Fields.Where(f => f.ValueOwner == FieldOwner.ProjectItem && f.DataType == "SINGLE_SELECT").ToArray();
        string? Permission(CapabilityObservation? c) => c?.CanUpdate switch { true when c.ObservedAt != default => null, false => "更新権限なし（取得時の観測）", _ => "更新権限未確認" };
        return p.Items.Select(item =>
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
                else if (fields[cell.Key!] is { Change: null, Buffer: null } old && old.SourceProject == p.Id
                    && old.RetrievedAt != registration.RetrievedAt && !history.Any(t => t.Changes.Any(c => c.Key == cell.Key)))
                {
                    // Unedited observations can follow a successful explicit refresh; active drafts never rebase here.
                    fields[cell.Key!] = old with { Baseline = cell.Baseline, RetrievedAt = registration.RetrievedAt };
                    Revision++;
                }
            }
            return new EditRow(item.Id.NodeId, cells.ToArray());
        }).ToArray();
    }
    public static string AvailabilityText(ValueAvailability availability) => availability switch
    { ValueAvailability.Empty => "明示的な空値", ValueAvailability.Unsupported => "非対応", ValueAvailability.Unavailable => "閲覧不可", ValueAvailability.NotLoaded => "未取得", _ => "取得済み" };
    public void SetBuffer(EditCell cell, string? text)
    {
        if (cell.Scope != Scope) throw new InvalidOperationException("別プロフィールのセルは編集できません。");
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
        if (cell.Key?.Kind == "Select" && cell.Options.Count(o => o.Name == text) != 1) throw new InvalidOperationException("選択肢が存在しないか同名で曖昧です。");
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
        foreach (var (cell, text, clear, optionId) in batch)
        {
            if (!cell.Editable) throw new InvalidOperationException(cell.Reason ?? "参照専用");
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
            var change = value == old.Baseline ? null : new LocalValue(value, clear);
            var next = old with { Change = change, Buffer = null, Stamp = Revision + 1 };
            if (changes.TryGetValue(key, out var duplicate) && duplicate.After.Change != next.Change)
                throw new InvalidOperationException("同じIssueへの値が競合しています。");
            changes[key] = new(key, old, next);
        }
        if (changes.Count == 0) return;
        Revision++;
        foreach (var change in changes.Values) fields[change.Key] = change.After;
        history.Add(new(Guid.NewGuid().ToString("N"), projectId, changes.Values.ToArray()));
    }
    public void Undo(string projectId)
    {
        var transaction = history.LastOrDefault(t => t.ProjectId == projectId);
        if (transaction is null) return;
        if (transaction.Changes.Any(c => !fields.TryGetValue(c.Key, out var current) || current != c.After))
            throw new InvalidOperationException("後続の共有編集または編集中の文字があるため、この操作は元に戻せません。");
        foreach (var c in transaction.Changes) fields[c.Key] = c.Before;
        history.Remove(transaction); Revision++;
    }
    private static bool Belongs(FieldKey key, ProjectReadModel p) => key.ProjectId == p.Id.NodeId || key.Kind == "Title" && p.Issues.Keys.Any(id => id.NodeId == key.NodeId);
    public bool HasWork(ProjectReadModel p) => fields.Values.Any(f => (f.Change is not null || f.Buffer is not null) && Belongs(f.Key, p))
        || history.Any(t => t.Changes.Any(c => Belongs(c.Key, p)));
    public void ForgetObservations(ProjectReadModel p)
    {
        if (HasWork(p)) throw new InvalidOperationException("Drafts must be reconciled before refresh.");
        foreach (var key in fields.Keys.Where(k => Belongs(k, p)).ToArray()) fields.Remove(key);
        Revision++;
    }
    public void Discard(ProjectReadModel project, IEnumerable<ProjectReadModel> surviving)
    {
        var shared = surviving.Where(p => p.Id.Scope == Scope).SelectMany(p => p.Issues.Keys).Select(k => k.NodeId).ToHashSet();
        var removed = fields.Keys.Where(k => k.ProjectId == project.Id.NodeId || k.Kind == "Title"
            && project.Issues.Keys.Any(id => id.NodeId == k.NodeId) && !shared.Contains(k.NodeId)).ToHashSet();
        foreach (var key in removed) fields.Remove(key);
        history.RemoveAll(t => t.ProjectId == project.Id.NodeId || t.Changes.Any(c => removed.Contains(c.Key)));
        Revision++;
    }
}
