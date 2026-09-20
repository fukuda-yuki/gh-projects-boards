using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace GhProjectsBoards.Core.Projects;

internal sealed record LocalSelect(string FieldId, string FieldName, string? OptionId, string? OptionName, bool ExplicitClear = false)
{
    public string Intent => OptionId is not null ? "Set" : ExplicitClear ? "ExplicitClear" : "Unspecified";
}
internal sealed record LocalRow(string Id, string ProjectId, string Title, string Repository,
    string? TitleBuffer, string? RepositoryBuffer, ImmutableArray<LocalSelect> Selects, long Stamp, long CreatedRevision, int Ordinal);
internal sealed record LocalRowChange(string Id, LocalRow? Before, LocalRow? After, int Position);

internal sealed partial class EditingWorkspace
{
    private readonly List<LocalRow> localRows = [];
    public IReadOnlyList<LocalRow> LocalRows => localRows;
    internal static bool IsLocal(FieldKey? key) => key?.Kind is "LocalTitle" or "LocalRepository" or "LocalSelect";
    private LocalRow Local(EditCell cell)
    {
        if (cell.Scope != Scope) throw new InvalidOperationException("別プロフィールのセルです。");
        return localRows.SingleOrDefault(r => r.Id == cell.Key?.NodeId && r.ProjectId == cell.Key.ProjectId)
            ?? throw new InvalidOperationException("新規行は削除・変更されています。表を開き直してください。");
    }
    private string? LocalValueFor(EditCell cell)
    {
        var row = Local(cell);
        return cell.Key!.Kind switch { "LocalTitle" => row.Title, "LocalRepository" => row.Repository,
            _ => row.Selects.SingleOrDefault(s => s.FieldId == cell.Key.FieldId)?.OptionId };
    }
    private string? LocalBufferFor(EditCell cell)
    {
        var row = Local(cell);
        return cell.Key!.Kind switch { "LocalTitle" => row.TitleBuffer, "LocalRepository" => row.RepositoryBuffer, _ => null };
    }
    private void ReplaceLocal(LocalRow row) => localRows[localRows.FindIndex(r => r.Id == row.Id)] = row;
    private void SetLocalBuffer(EditCell cell, string? text)
    {
        var old = Local(cell);
        if (!cell.Editable || LocalBufferFor(cell) == text) return;
        ReplaceLocal(cell.Key!.Kind == "LocalTitle" ? old with { TitleBuffer = text } : old with { RepositoryBuffer = text });
        Revision++;
    }
    public ProjectFieldDefinition[] LocalColumns(ProjectRegistration registration)
    {
        var p = registration.Snapshot;
        var columns = SupportedColumns(registration).ToList();
        foreach (var saved in localRows.Where(r => r.ProjectId == p.Id.NodeId).SelectMany(r => r.Selects).DistinctBy(s => s.FieldId))
            if (!columns.Any(f => f.Id.NodeId == saved.FieldId))
                columns.Add(new(new(Scope, saved.FieldId), p.Id, saved.FieldName, "ProjectV2SingleSelectField", "SINGLE_SELECT",
                    FieldOwner.ProjectItem, [], ValueAvailability.Unavailable));
        return columns.ToArray();
    }
    private IEnumerable<EditRow> OpenLocal(ProjectRegistration registration, ProjectFieldDefinition[] columns)
    {
        foreach (var row in localRows.Where(r => r.ProjectId == registration.Snapshot.Id.NodeId))
        {
            EditCell Cell(string kind, string display, string? value, string? reason = null, SelectOption[]? options = null, string? field = null)
                => new(new(kind, row.Id, row.ProjectId, field), display, value, reason, options ?? [], Scope: Scope);
            var cells = new List<EditCell> { Cell("LocalTitle", "新規タイトル", row.Title) };
            foreach (var f in columns)
            {
                var reason = f.Availability == ValueAvailability.Present ? null : "フィールド未確認（保存したIDと表示を保持）";
                if (f.DataType == "SINGLE_SELECT") cells.Add(Cell("LocalSelect", f.Name, row.Selects.SingleOrDefault(s => s.FieldId == f.Id.NodeId)?.OptionId, reason, f.Options.ToArray(), f.Id.NodeId));
                else
                {
                    reason ??= registration.Snapshot.Capability is { CanUpdate: true, ObservedAt: var observedAt } && observedAt != default
                        ? null : "Projectの更新権限未確認（取得時の観測）";
                    var cell = Cell(PlanningScalars.Kind(f.DataType), f.Name, null, reason, field: f.Id.NodeId) with {
                        Availability = ValueAvailability.Empty,
                        InputLocked = Planning(row.ProjectId)?.Fields.Any(b => b.FieldId == f.Id.NodeId && b.Role is "Actual" or "Start" or "Finish") == true };
                    cells.Add(cell);
                    if (!fields.ContainsKey(cell.Key!)) { fields[cell.Key!] = new(cell.Key!, null, registration.Snapshot.Id, registration.RetrievedAt, null, null, 0); Revision++; }
                }
            }
            cells.Add(Cell("LocalRepository", "新規行の宛先 owner/repository", row.Repository,
                CreationLocked(row.Id) ? "作成承認・履歴に固定された宛先です" : null));
            yield return new(row.Id, cells.ToArray(), true);
        }
    }
    public string[] LocalProblems(ProjectRegistration registration, string id)
    {
        var row = localRows.Single(r => r.Id == id && r.ProjectId == registration.Snapshot.Id.NodeId);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(row.Title)) errors.Add("タイトルが必要です");
        else if (row.Title.IndexOfAny(['\r','\n','\t']) >= 0) errors.Add("タイトルに改行・タブは使えません");
        if (!ValidRepository(row.Repository)) errors.Add("宛先を owner/repository 形式で指定してください");
        foreach (var s in row.Selects)
        {
            var f = registration.Snapshot.Fields.SingleOrDefault(f => f.Id.NodeId == s.FieldId && f.ValueOwner == FieldOwner.ProjectItem && f.DataType == "SINGLE_SELECT");
            if (f?.Availability != ValueAvailability.Present) errors.Add($"フィールド未確認: {s.FieldName} [{s.FieldId}]");
            else if (s.OptionId is not null && !f.Options.Any(o => o.Id == s.OptionId)) errors.Add($"選択肢未確認: {s.FieldName} / {s.OptionName} [{s.OptionId}]");
        }
        return errors.ToArray();
    }
    private static bool ValidRepository(string value) => Regex.IsMatch(value, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?/[A-Za-z0-9_.-]+\z")
        && value.Split('/')[1] is not ("." or "..");
    private LocalRow EmptyLocal(ProjectRegistration p, string title = "") => new("local-" + Guid.NewGuid().ToString("N"), p.Snapshot.Id.NodeId,
        title, p.DefaultRepository ?? "", null, null,
        p.Snapshot.Fields.Where(f => f.ValueOwner == FieldOwner.ProjectItem && f.DataType == "SINGLE_SELECT")
            .Select(f => new LocalSelect(f.Id.NodeId, f.Name, null, null)).ToImmutableArray(), Revision + 1, Revision + 1, 0);
    private void AddLocals(ProjectRegistration project, LocalRow[] added, List<(string Id, string Field, LocalValue Value)>? scalars = null)
    {
        if (project.Snapshot.Id.Scope != Scope) throw new InvalidOperationException("プロフィールが一致しません。");
        if (added.Length == 0) throw new InvalidOperationException("行を選択してください。");
        // Establish the same authoritative cache checkpoint before the first row save.
        if (!HasCheckpoint) throw new InvalidOperationException("プロフィールの保存準備が必要です。");
        added = added.Select((r, i) => r with { CreatedRevision = Revision + 1, Ordinal = i }).ToArray();
        var changes = added.Select((r, i) => new LocalRowChange(r.Id, null, r, localRows.Count + i)).ToArray();
        localRows.AddRange(added); Revision++;
        var fieldChanges = new List<FieldChange>();
        foreach (var (id, field, value) in scalars ?? [])
        {
            var key = new FieldKey("Number", id, project.Snapshot.Id.NodeId, field);
            var before = new DraftField(key, null, project.Snapshot.Id, project.RetrievedAt, null, null, 0);
            var after = before with { Change = value, Stamp = Revision };
            fields[key] = after; fieldChanges.Add(new(key, before, after));
        }
        InvalidatePlan(project.Snapshot.Id.NodeId);
        history.Add(new(Guid.NewGuid().ToString("N"), project.Snapshot.Id.NodeId, fieldChanges.ToArray(), Rows: changes));
    }
    public string AddRow(ProjectRegistration project)
    {
        var row = EmptyLocal(project); AddLocals(project, [row]); return row.Id;
    }
    public string[] DuplicateRows(ProjectRegistration project, IReadOnlyList<EditRow> sources)
    {
        if (project.Snapshot.Id.Scope != Scope) throw new InvalidOperationException("プロフィールが一致しません。");
        var added = new List<LocalRow>();
        var scalars = new List<(string Id, string Field, LocalValue Value)>();
        var current = Restore(Snapshot()).Open(project);
        void CopyWork(string sourceId, string destinationId)
        {
            foreach (var cell in current.Single(r => r.ItemId == sourceId).Cells.Where(c => c.Key?.Kind == "Number" && !c.InputLocked))
            {
                if (!cell.Editable || Field(cell)?.Conflict == true || Field(cell)?.Observation?.Reason is not null)
                    throw new InvalidOperationException("複製元の工数を確認してください。");
                var value = Value(cell);
                if (value is not null) scalars.Add((destinationId, cell.Key!.FieldId!, new(PlanningScalars.Normalize("Number", value))));
                else if (Field(cell)?.Change?.Clear == true) scalars.Add((destinationId, cell.Key!.FieldId!, new(null, true)));
            }
        }
        foreach (var source in sources.DistinctBy(r => r.ItemId))
        {
            if (source.Cells.Any(c => c.Scope != Scope)) throw new InvalidOperationException("別プロフィールの選択です。");
            if (source.IsLocal)
            {
                var old = localRows.SingleOrDefault(r => r.Id == source.ItemId && r.ProjectId == project.Snapshot.Id.NodeId)
                    ?? throw new InvalidOperationException("複製元がありません。");
                if (LocalProblems(project, old.Id).Any(p => p.StartsWith("フィールド") || p.StartsWith("選択肢")))
                    throw new InvalidOperationException("複製元のフィールド・選択肢IDを確認してください。");
                var copy = old with { Id = "local-" + Guid.NewGuid().ToString("N"), TitleBuffer = null, RepositoryBuffer = null, Stamp = Revision + 1 };
                CopyWork(old.Id, copy.Id); added.Add(copy);
                continue;
            }
            var actual = current.SingleOrDefault(r => r.ItemId == source.ItemId)
                ?? throw new InvalidOperationException("複製元を現在のProjectで確認できません。");
            var supported = actual.Cells.Where(c => c.Key is not null).ToArray();
            if (supported.Length == 0 || supported[0].Key?.Kind != "Title") throw new InvalidOperationException("Issueを選択してください。");
            foreach (var cell in supported)
            {
                var f = Field(cell);
                if (cell.Availability is not (ValueAvailability.Present or ValueAvailability.Empty) || f?.Conflict == true
                    || cell.Key?.Kind == "Select" && project.Snapshot.Fields.SingleOrDefault(def => def.Id.NodeId == cell.Key.FieldId)?.Availability != ValueAvailability.Present
                    || f?.Observation?.Reason is { } reason && !reason.StartsWith("未確定文字")
                    || cell.Key?.Kind == "Select" && Value(cell) is { } value && !cell.Options.Any(o => o.Id == value))
                    throw new InvalidOperationException("複製元に競合・未確認の値があります。再取得・比較で解決してください。");
            }
            var row = EmptyLocal(project, Value(supported[0]) ?? "");
            row = row with { Selects = supported.Where(c => c.Key?.Kind == "Select").Select(c => new LocalSelect(c.Key!.FieldId!, c.Display,
                Value(c), c.Options.SingleOrDefault(o => o.Id == Value(c))?.Name)).ToImmutableArray() };
            CopyWork(source.ItemId, row.Id);
            added.Add(row);
        }
        AddLocals(project, added.ToArray(), scalars); return added.Select(r => r.Id).ToArray();
    }
    public string[] AppendRows(ProjectRegistration project, string tsv, ColumnIdentity[]? capturedColumns = null)
    {
        var inputColumns = Columns(project).Visible.Where(c => c.Id.Role != "Reference").Select(c => c.Id).ToArray();
        if (capturedColumns is not null && !inputColumns.SequenceEqual(capturedColumns)) throw new InvalidOperationException("入力列が変わりました。貼り付けをやり直してください。");
        var matrix = ParseTsv(tsv); var columns = Columns(project).Visible.Where(c => c.Id.Role == "Field")
            .Select(c => SupportedColumns(project).Single(f => f.Id.NodeId == c.Id.FieldId)).ToArray();
        if (matrix[0].Length > columns.Length + 1) throw new InvalidOperationException("新規行のTSVはタイトルと表示順の入力列に合わせてください。");
        var added = new List<LocalRow>();
        var scalars = new List<(string Id, string Field, LocalValue Value)>();
        foreach (var line in matrix)
        {
            if (string.IsNullOrWhiteSpace(line[0]) || line[0].IndexOfAny(['\r','\n','\t']) >= 0) throw new InvalidOperationException("追加するすべての行に有効なタイトルが必要です。");
            var row = EmptyLocal(project, line[0]); var selects = row.Selects.ToList();
            for (var c = 1; c < line.Length; c++)
            {
                if (line[c] == "") continue;
                var f = columns[c - 1];
                if (f.DataType != "SINGLE_SELECT")
                {
                    if (f.Availability != ValueAvailability.Present || Planning(project.Snapshot.Id.NodeId)?.Fields.SingleOrDefault(b => b.FieldId == f.Id.NodeId)?.Role is not ("Estimate" or "Remaining"))
                        throw new InvalidOperationException($"{f.Name}: 日付・実績は計画の編集から入力してください。追加していません。");
                    if (project.Snapshot.Capability is not { CanUpdate: true, ObservedAt: var observedAt } || observedAt == default)
                        throw new InvalidOperationException("Projectの更新権限を確認してください。");
                    scalars.Add((row.Id, f.Id.NodeId, new(PlanningScalars.Normalize("Number", line[c]))));
                    continue;
                }
                var options = f.Options.Where(o => o.Name == line[c]).ToArray();
                if (f.Availability != ValueAvailability.Present || options.Length != 1) throw new InvalidOperationException($"{f.Name}: 選択肢が不明・曖昧です。追加していません。");
                var index = selects.FindIndex(s => s.FieldId == f.Id.NodeId);
                selects[index] = new(f.Id.NodeId, f.Name, options[0].Id, options[0].Name);
            }
            added.Add(row with { Selects = selects.ToImmutableArray() });
        }
        AddLocals(project, added.ToArray(), scalars); return added.Select(r => r.Id).ToArray();
    }
    public void RemoveRows(string projectId, IReadOnlyList<EditRow> selected)
    {
        if (selected.Any(r => CreationLocked(r.ItemId))) throw new InvalidOperationException("作成承認・結果のある行は削除できません。実行履歴で解決してください。");
        if (selected.Count == 0 || selected.Any(r => !r.IsLocal || r.Cells.Any(c => c.Scope != Scope)
            || !localRows.Any(l => l.Id == r.ItemId && l.ProjectId == projectId)))
            throw new InvalidOperationException("削除は選択した新規ローカル行だけに適用できます。既存行との混在選択は解除してください。");
        var changes = selected.DistinctBy(r => r.ItemId).Select(r => {
            var index = localRows.FindIndex(l => l.Id == r.ItemId); return new LocalRowChange(r.ItemId, localRows[index], null, index);
        }).ToArray();
        var removed = changes.Select(c => c.Id).ToHashSet();
        var fieldChanges = new Dictionary<FieldKey, FieldChange>();
        foreach (var field in fields.Values.Where(f => removed.Contains(f.Key.NodeId)).ToArray())
        {
            var after = field with { Change = null, Buffer = null, Conflict = false, Observation = null, Stamp = Revision + 1 };
            fields[field.Key] = after; fieldChanges[field.Key] = new(field.Key, field, after);
        }
        PlanningChange? planChange = null;
        if (Planning(projectId) is { } plan && plan.Tasks.Any(t => removed.Contains(t.Id)))
        {
            SetPlanning(plan with { Tasks = plan.Tasks.Where(t => !removed.Contains(t.Id)).ToArray() }, plan.Stamp);
            planChange = new(plan, Planning(projectId)!);
        }
        localRows.RemoveAll(r => removed.Contains(r.Id));
        if (planChange is null) Revision++;
        InvalidatePlan(projectId); ProjectCommittedPlan(projectId, fieldChanges);
        history.Add(new(Guid.NewGuid().ToString("N"), projectId, fieldChanges.Values.ToArray(), Rows: changes, Plan: planChange));
    }
    private void PrepareLocalEdit(string projectId, EditCell cell, string text, bool clear, bool optionId, Dictionary<string, LocalRowChange> changes)
    {
        var old = Local(cell);
        if (old.ProjectId != projectId) throw new InvalidOperationException("別Projectの新規行です。");
        if (cell.Key?.Kind == "LocalRepository" && CreationLocked(old.Id)) throw new InvalidOperationException("作成の宛先は固定されています。");
        var prior = changes.GetValueOrDefault(old.Id); var next = prior?.After ?? old;
        if (cell.Key!.Kind == "LocalTitle") next = next with { Title = clear ? "" : text, TitleBuffer = null };
        else if (cell.Key.Kind == "LocalRepository") next = next with { Repository = clear ? "" : text, RepositoryBuffer = null };
        else
        {
            var options = cell.Options.Where(o => optionId ? o.Id == text : o.Name == text).ToArray();
            if (!clear && options.Length != 1) throw new InvalidOperationException("選択肢が不明・曖昧です。");
            var values = next.Selects.ToList();
            var value = new LocalSelect(cell.Key.FieldId!, cell.Display, clear ? null : options[0].Id, clear ? null : options[0].Name, clear);
            var index = values.FindIndex(s => s.FieldId == cell.Key.FieldId);
            if (index >= 0) values[index] = value; else values.Add(value);
            next = next with { Selects = values.ToImmutableArray() };
        }
        changes[old.Id] = new(old.Id, prior?.Before ?? old, next with { Stamp = Revision + 1 }, localRows.FindIndex(r => r.Id == old.Id));
    }
    private static bool SameLocal(LocalRow a, LocalRow b) => a.Id == b.Id && a.ProjectId == b.ProjectId && a.Title == b.Title
        && a.Repository == b.Repository && a.TitleBuffer == b.TitleBuffer && a.RepositoryBuffer == b.RepositoryBuffer
        && a.Stamp == b.Stamp && a.CreatedRevision == b.CreatedRevision && a.Ordinal == b.Ordinal && a.Selects.SequenceEqual(b.Selects);
    private void GuardLocalUndo(EditTransaction transaction)
    {
        foreach (var c in transaction.Rows ?? [])
        {
            if (CreationLocked(c.Id)) throw new InvalidOperationException("作成履歴に関連する行のUndoは実行できません。履歴と入力を保持しています。");
            var current = localRows.SingleOrDefault(r => r.Id == c.Id);
            if (c.After is null ? current is not null : current is null || !SameLocal(current, c.After))
                throw new InvalidOperationException("後続の新規行編集・未確定文字があるため、この操作は元に戻せません。");
            if (c.Before is null || c.After is null)
            {
                // Later typed input lives outside LocalRow. Never undo an old row
                // addition/removal past work that this transaction cannot restore.
                var unownedWork = fields.Values.Any(f => f.Key.NodeId == c.Id && f.Key.ProjectId == transaction.ProjectId
                    && f.Key.Kind is "Number" or "Date" && (f.Change is not null || f.Buffer is not null || f.Conflict || f.Baseline is not null)
                    && !transaction.Changes.Any(change => change.Key == f.Key));
                var unownedPlan = transaction.Plan is null && Planning(transaction.ProjectId)?.Tasks.Any(t => t.Id == c.Id
                    || (t.LocalLinks ?? []).Any(link => link.PredecessorId == c.Id)) == true;
                if (unownedWork || unownedPlan)
                    throw new InvalidOperationException("後続の工数・日程・未確定文字を保持するため、以前の行追加・削除は元に戻せません。");
            }
        }
    }
    private void SplitLockedCreationUndo(string projectId)
    {
        foreach (var t in history.Where(t => t.ProjectId == projectId && t.InvalidReason is null && (t.Rows ?? []).Any(r => CreationLocked(r.Id))).ToArray())
        {
            var locked = t.Rows!.Where(r => CreationLocked(r.Id)).ToArray();
            var remaining = t.Rows!.Where(r => !CreationLocked(r.Id)).ToArray();
            var index = history.IndexOf(t);
            if (CoupledPlanningRows(t))
            {
                history[index] = t with { InvalidReason = "作成履歴に関連する工数・日程と行は分割してUndoできません。現在の入力を保持しました。" };
                Revision++; continue;
            }
            if (remaining.Length == 0 && t.Changes.Length == 0) continue;
            history[index] = t with { Changes = [], Rows = locked, InvalidReason = "作成履歴のある行を保持しました。無関係なUndo部分は有効です。" };
            history.Insert(index + 1, new(t.Id + "-unlocked", t.ProjectId, t.Changes, Rows: remaining));
            Revision++;
        }
    }
    private void UndoLocal(EditTransaction transaction)
    {
        if (transaction.Rows is not { Length: > 0 }) return;
        foreach (var c in transaction.Rows ?? []) localRows.RemoveAll(r => r.Id == c.Id);
        foreach (var c in (transaction.Rows ?? []).Where(c => c.Before is not null).OrderBy(c => c.Position))
            localRows.Insert(Math.Min(c.Position, localRows.Count), c.Before!);
        localRows.Sort((a, b) => a.CreatedRevision != b.CreatedRevision ? a.CreatedRevision.CompareTo(b.CreatedRevision) : a.Ordinal.CompareTo(b.Ordinal));
    }
    private void InvalidateRemoteUndo(int index, string reason)
    {
        var transaction = history[index];
        // Typed local work and its adopted plan form one operation with the row.
        // A stale graph cannot safely restore only one part of that operation.
        if (CoupledPlanningRows(transaction)) { history[index] = transaction with { InvalidReason = reason }; return; }
        history[index] = transaction with { InvalidReason = reason, Rows = null };
        // Remote acknowledgement cannot revoke recoverable local-row work from a mixed paste.
        if (transaction.Rows is { Length: > 0 })
            history.Insert(index + 1, new(transaction.Id + "-local", transaction.ProjectId, [], Rows: transaction.Rows));
    }
    private static bool CoupledPlanningRows(EditTransaction transaction) => transaction.Rows is { Length: > 0 }
        && (transaction.Plan is not null || transaction.Changes.Any(c => c.Key.Kind is "Number" or "Date"
            && transaction.Rows.Any(r => r.Id == c.Key.NodeId)));
}
