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

    internal static object? Parse(PlanDocument document, string field, string text, DateOnly today, string? identity = null)
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
            case PlanField.Start: case PlanField.End: case PlanField.StartNoEarlierThan:
                var dateText = PlanDateInput.Normalize(text);
                if (PlanDateInput.TryParse(dateText, out var day)) return day;
                var status = document.State.Settings.StatusDate ?? today;
                var candidates = Enumerable.Range(Math.Max(1, status.Year - 4), Math.Min(9999, status.Year + 4) - Math.Max(1, status.Year - 4) + 1)
                    .Select(year => DateOnly.TryParseExact($"{year:D4}/{dateText}", "yyyy/M/d", CultureInfo.InvariantCulture, DateTimeStyles.None, out var candidate) ? candidate : (DateOnly?)null)
                    .Where(candidate => candidate.HasValue).Select(candidate => candidate!.Value)
                    .OrderBy(candidate => Math.Abs(candidate.DayNumber - status.DayNumber)).ThenByDescending(candidate => candidate).ToArray();
                if (candidates.Length > 0) return candidates[0];
                throw new ArgumentException("日付は 2026-10-14、2026/10/14 または 10/14 の形で入力してください。");
            case PlanField.Fixed:
                return PlanInput.Fold(text).Trim() switch { "固定" or "true" or "1" => true, "解除" or "false" or "0" => false,
                    _ => throw new ArgumentException("日程固定は「固定」または空欄にしてください。") };
            case PlanField.Predecessors:
                return Split(PlanInput.Fold(text)).Select(value => {
                    if (!int.TryParse(value.TrimStart('#'), out var id) || id <= 0 || id > document.State.Rows.Length)
                        throw new ArgumentException("先行タスクは計画内のIDで指定してください: " + value);
                    return document.State.Rows[id - 1].Identity;
                }).Distinct().ToImmutableArray();
            case PlanField.Assignees:
                var people = document.State.Settings.People.Select(p => (p.Identity, Name: document.Sync.PeopleNames.GetValueOrDefault(p.Identity, p.Name)))
                    .Concat(document.Sync.PeopleNames.Select(p => (Identity: p.Key, Name: p.Value))).Distinct().ToArray();
                return Split(text).Select(value => {
                    var matches = people.Where(p => p.Name.Equals(value, StringComparison.OrdinalIgnoreCase)).Select(p => p.Identity).Distinct().ToArray();
                    if (matches.Length != 1) throw new ArgumentException("担当者を確認してください: " + value);
                    return matches[0];
                }).Distinct().ToImmutableArray();
            case PlanField.Title: case PlanField.Status:
                if (text.IndexOfAny(['\t', '\r', '\n']) >= 0) throw new ArgumentException("セルは1行で入力してください。");
                return text;
            default: throw new ArgumentException("この列は編集できません。");
        }
    }
    private static string[] Split(string text) => text.Split([',', '、'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    internal static EditPlanCells Paste(PlanDocument document, IReadOnlyList<string> rows,
        IReadOnlyList<string> columns, CellRange selection, string text, DateOnly today)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length > 1 && lines[^1] == "") lines = lines[..^1];
        var matrix = lines.Select(line => line.Split('\t')).ToArray();
        if (matrix.Any(row => row.Length != matrix[0].Length)) throw new ArgumentException("TSVの列数が一致していません。");
        var single = matrix.Length == 1 && matrix[0].Length == 1;
        var target = PasteRange(selection, matrix.Length, matrix[0].Length, rows.Count, columns.Count);
        var existing = document.State.Rows.Select(row => row.Identity).ToHashSet();
        if (rows.Skip(target.Row).Take(target.RowCount).Any(id => !existing.Contains(id)))
            throw new ArgumentException("空行を除いて範囲を選択してください。");
        var expand = single && !selection.Single;
        var changes = ImmutableArray.CreateBuilder<PlanCellChange>();
        for (var r = 0; r < target.RowCount; r++)
            for (var c = 0; c < target.ColumnCount; c++)
            {
                var value = matrix[expand ? 0 : r][expand ? 0 : c];
                if (value.Length == 0) continue;
                var field = columns[target.Column + c];
                changes.Add(new(rows[target.Row + r], field, Parse(document, field, value, today, rows[target.Row + r])));
            }
        return new(PlanOperationKind.Paste, changes.ToImmutable());
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
