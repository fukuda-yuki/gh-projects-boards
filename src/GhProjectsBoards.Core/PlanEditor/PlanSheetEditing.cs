using System.Collections.Immutable;
using System.Globalization;
using GhProjectsBoards.Core.Projects;
namespace GhProjectsBoards.Core.PlanEditor;

internal sealed record CellRange(int Row, int Column, int RowCount = 1, int ColumnCount = 1)
{
    public bool Single => RowCount == 1 && ColumnCount == 1;
}

// Text/range adaptation only; PlanSession validates and applies the complete operation.
internal static class PlanSheetEditing
{
    internal static object? CopyValue(PlanRow row, string field, string editText)
        => field switch {
            PlanField.Start or PlanField.End => editText.Length == 0 ? null : DateOnly.ParseExact(editText, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            PlanField.Estimate or PlanField.Remaining or PlanField.Actual => editText.Length == 0 ? null : decimal.Parse(editText, CultureInfo.CurrentCulture),
            _ => PlanOperations.Value(row, field)
        };

    internal static string EditBaseline(PlanRow? row, string field, string editText)
        => field == PlanField.Remaining ? row?.Remaining?.ToString(CultureInfo.CurrentCulture) ?? "" : editText;

    internal static object? Parse(PlanDocument document, string field, string text, DateOnly today, string? identity = null,
        ImmutableArray<PlanResource> resolved = default)
    {
        if (field is PlanField.Labels or PlanField.MilestoneTitle)
        {
            var repository = document.State.Rows.SingleOrDefault(r => r.Identity == identity)?.Repository
                ?? document.State.Rows.Select(r => r.Repository).Distinct().SingleOrDefault()
                ?? throw new ArgumentException("編集する Issue を選択してください。");
            var options = (field == PlanField.Labels ? document.Baseline.RepositoryLabels : document.Baseline.RepositoryMilestones)
                .Where(o => o.Repository == repository).Select(o => o.Name).ToArray();
            var name = field == PlanField.Labels ? "ラベル" : "マイルストーン";
            var emptyMessage = $"{repository} に{name}がありません。{name}は GitHub で作成します。";
            if (field == PlanField.MilestoneTitle) return PlanExtraEditing.Select(name, options, text, emptyMessage);
            // The list separator is folded too, so the IME's `，` separates labels.
            return PlanInput.Fold(text).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(label => PlanExtraEditing.Select(name, options, label, emptyMessage)!).Distinct().Order(StringComparer.Ordinal).ToImmutableArray();
        }
        if (field == PlanField.IssueType)
        {
            // #319 D2: the value is the Issue's own type. A repository without Issue types cannot carry one, and
            // the cell is read-only there; the refusal says which condition applies.
            var repository = identity is null ? document.State.Settings.DefaultRepository ?? ""
                : document.State.Rows.SingleOrDefault(r => r.Identity == identity)?.Repository ?? "";
            if (!document.Baseline.IssueTypeRepositories.Contains(repository, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("このリポジトリには Issue の種類がありません");
            return PlanExtraEditing.Select("種類", document.Baseline.IssueTypes.Select(t => t.Name), text);
        }
        if (PlanSheetColumns.IsExtra(field)) return PlanExtraEditing.Parse(document, field, text, today);
        if (field == PlanField.Phase)
        {
            var mapping = document.State.Settings.Columns.SingleOrDefault(c => c.Role == field)
                ?? throw new ArgumentException("工程のフィールドが未設定です。");
            var options = document.Baseline.Columns.SingleOrDefault(c => c.Id == mapping.FieldId)?.Options ?? [];
            return PlanExtraEditing.Select("工程", options, text);
        }
        if (text.Length == 0) return null;
        switch (field)
        {
            case PlanField.Estimate: case PlanField.Remaining: case PlanField.Actual:
                if (PlanInput.TryEffort(text, out var number) && number >= 0) return number;
                throw new ArgumentException("工数は0以上の数値で入力してください。");
            case PlanField.Start: case PlanField.End:
                var dateText = PlanDateInput.Normalize(text);
                if (PlanDateInput.TryParse(dateText, out var day)) return day;
                var status = document.State.Settings.StatusDate ?? today;
                var candidates = Enumerable.Range(Math.Max(1, status.Year - 4), Math.Min(9999, status.Year + 4) - Math.Max(1, status.Year - 4) + 1)
                    .Select(year => DateOnly.TryParseExact($"{year:D4}/{dateText}", "yyyy/M/d", CultureInfo.InvariantCulture, DateTimeStyles.None, out var candidate) ? candidate : (DateOnly?)null)
                    .Where(candidate => candidate.HasValue).Select(candidate => candidate!.Value)
                    .OrderBy(candidate => Math.Abs(candidate.DayNumber - status.DayNumber)).ThenByDescending(candidate => candidate).ToArray();
                if (candidates.Length > 0) return candidates[0];
                throw new ArgumentException("日付は 2026-10-14、2026/10/14 または 10/14 の形で入力してください。");
            case PlanField.Predecessors:
                var names = PlanTaskNames.Of(document);
                return Split(PlanInput.Fold(text)).Select(names.Resolve).Distinct().ToImmutableArray();
            case PlanField.Assignees:
                var people = KnownPeople(document, resolved);
                return PlanAssigneeChoices.Values(text).Select(value => {
                    var matches = people.Where(p => p.Name.Equals(value, StringComparison.OrdinalIgnoreCase)).Select(p => p.Identity).Distinct().ToArray();
                    if (matches.Length != 1) throw new ArgumentException("担当者を確認してください: " + value);
                    return matches[0];
                }).Distinct().ToImmutableArray();
            case PlanField.Title: case PlanField.Status:
                if (text.IndexOfAny(['\t', '\r', '\n']) >= 0) throw new ArgumentException("セルは1行で入力してください。");
                return text;
            default: throw new ArgumentException(field is "ActualStart" or "ActualFinish"
                ? "Issue の履歴から読み取ります（編集不可）" : "この列は編集できません。");
        }
    }
    // The people a typed login can resolve to. The 担当者 candidate list (#225) offers this same source, so a
    // chosen person always commits here.
    private static (string Identity, string Name)[] KnownPeople(PlanDocument document, ImmutableArray<PlanResource> resolved)
    {
        var known = PlanAssigneeChoices.Known(document);
        return (resolved.IsDefaultOrEmpty ? known : known.Concat(resolved.Select(p => (p.Identity, Name: p.Name)))).Distinct().ToArray();
    }
    // #253: the typed logins the sheet must verify against GitHub before it can commit. A value that already
    // matches exactly one known name (stored person or read login) is resolved locally and sends no request.
    internal static string[] UnresolvedAssignees(PlanDocument document, string text, ImmutableArray<PlanResource> resolved = default)
    {
        var people = KnownPeople(document, resolved);
        return PlanAssigneeChoices.Values(text).Where(value => people
            .Where(p => p.Name.Equals(value, StringComparison.OrdinalIgnoreCase)).Select(p => p.Identity).Distinct().Count() != 1)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string[] Split(string text) => text.Split([',', '、'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    internal static EditPlanCells Paste(PlanDocument document, IReadOnlyList<string> rows,
        IReadOnlyList<string> columns, CellRange selection, string text, DateOnly today, ImmutableArray<PlanResource> resolved = default)
    {
        var (target, matrix, expand) = PasteLayout(rows, columns, selection, text);
        var existing = document.State.Rows.Select(row => row.Identity).ToHashSet();
        if (rows.Skip(target.Row).Take(target.RowCount).Any(id => !existing.Contains(id)))
            throw new ArgumentException("空行を除いて範囲を選択してください。");
        var changes = ImmutableArray.CreateBuilder<PlanCellChange>();
        for (var r = 0; r < target.RowCount; r++)
            for (var c = 0; c < target.ColumnCount; c++)
            {
                var value = matrix[expand ? 0 : r][expand ? 0 : c];
                if (value.Length == 0) continue;
                var field = columns[target.Column + c];
                changes.Add(new(rows[target.Row + r], field, Parse(document, field, value, today, rows[target.Row + r], resolved)));
            }
        return new(PlanOperationKind.Paste, changes.ToImmutable());
    }
    // #275: Ctrl+Enter writes the text being typed into every selected cell, as Excel does. Each cell parses the
    // text for its own column, and one cell that cannot take it refuses the whole operation.
    internal static EditPlanCells FillSelection(PlanDocument document, IReadOnlyList<string> rows,
        IReadOnlyList<string> columns, CellRange selection, string text, DateOnly today, ImmutableArray<PlanResource> resolved = default)
    {
        var changes = ImmutableArray.CreateBuilder<PlanCellChange>();
        foreach (var identity in SelectedRows(document, rows, selection))
            for (var c = 0; c < selection.ColumnCount; c++)
            {
                var field = columns[selection.Column + c];
                changes.Add(new(identity, field, Parse(document, field, text, today, identity, resolved)));
            }
        return new(PlanOperationKind.Fill, changes.ToImmutable());
    }
    // The logins Ctrl+Enter must verify first: the typed ones no known name resolves, once per repository of
    // the selected rows.
    internal static ImmutableArray<(string Repository, string Login)> FillSelectionAssignees(PlanDocument document, IReadOnlyList<string> rows,
        IReadOnlyList<string> columns, CellRange selection, string text)
    {
        if (!columns.Skip(selection.Column).Take(selection.ColumnCount).Contains(PlanField.Assignees)) return [];
        var logins = UnresolvedAssignees(document, text);
        return [.. SelectedRows(document, rows, selection)
            .Select(identity => document.State.Rows.Single(row => row.Identity == identity).Repository is { Length: > 0 } repository
                ? repository : document.State.Settings.DefaultRepository ?? "")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(repository => logins.Select(login => (repository, login)))];
    }
    private static string[] SelectedRows(PlanDocument document, IReadOnlyList<string> rows, CellRange selection)
    {
        var targets = rows.Skip(selection.Row).Take(selection.RowCount).ToArray();
        var existing = document.State.Rows.Select(row => row.Identity).ToHashSet();
        if (targets.Any(id => !existing.Contains(id))) throw new ArgumentException("空行を除いて範囲を選択してください。");
        return targets;
    }
    // #253: the typed logins a text paste must verify against GitHub before it can parse the cells. A login
    // that exactly one known name resolves is local and sends no request, exactly as for a single cell.
    internal static ImmutableArray<(string Repository, string Login)> PendingAssignees(PlanDocument document, IReadOnlyList<string> rows,
        IReadOnlyList<string> columns, CellRange selection, string text)
    {
        var (target, matrix, expand) = PasteLayout(rows, columns, selection, text);
        var existing = document.State.Rows.Select(row => row.Identity).ToHashSet();
        if (rows.Skip(target.Row).Take(target.RowCount).Any(id => !existing.Contains(id)))
            throw new ArgumentException("空行を除いて範囲を選択してください。");
        var pending = ImmutableArray.CreateBuilder<(string Repository, string Login)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var r = 0; r < target.RowCount; r++)
            for (var c = 0; c < target.ColumnCount; c++)
            {
                if (columns[target.Column + c] != PlanField.Assignees) continue;
                var value = matrix[expand ? 0 : r][expand ? 0 : c];
                if (value.Length == 0) continue;
                var repository = document.State.Rows.Single(row => row.Identity == rows[target.Row + r]).Repository;
                if (repository.Length == 0) repository = document.State.Settings.DefaultRepository ?? "";
                foreach (var login in UnresolvedAssignees(document, value))
                    if (seen.Add(repository + "\u0000" + login)) pending.Add((repository, login));
            }
        return pending.ToImmutable();
    }
    private static (CellRange Target, string[][] Matrix, bool Expand) PasteLayout(IReadOnlyList<string> rows,
        IReadOnlyList<string> columns, CellRange selection, string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length > 1 && lines[^1] == "") lines = lines[..^1];
        var matrix = lines.Select(line => line.Split('\t')).ToArray();
        if (matrix.Any(line => line.Length != matrix[0].Length)) throw new ArgumentException("TSVの列数が一致していません。");
        var single = matrix.Length == 1 && matrix[0].Length == 1;
        var target = PasteRange(selection, matrix.Length, matrix[0].Length, rows.Count, columns.Count);
        return (target, matrix, single && !selection.Single);
    }
    internal static CellRange PasteRange(CellRange selection, int sourceRows, int sourceColumns, int rowCount, int columnCount)
    {
        var single = sourceRows == 1 && sourceColumns == 1;
        var expand = single && !selection.Single;
        if (expand && selection.ColumnCount != 1) throw new ArgumentException("1つの値を複数列へ展開できません。");
        if (!single && !selection.Single && (sourceRows != selection.RowCount || sourceColumns != selection.ColumnCount))
            throw new ArgumentException("選択範囲と貼り付けの行数・列数が一致しません。");
        var target = expand ? selection : new CellRange(selection.Row, selection.Column, sourceRows, sourceColumns);
        if (target.Row < 0 || target.Column < 0 || target.RowCount < 1 || target.ColumnCount < 1
            || target.Row + target.RowCount > rowCount || target.Column + target.ColumnCount > columnCount)
            throw new ArgumentException("貼り付け範囲が表の端を超えています。");
        return target;
    }

}
