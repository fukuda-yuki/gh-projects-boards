using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;

namespace GhProjectsBoards.App.GridPrototype;

internal sealed class GridPrototypeViewModel : INotifyPropertyChanged
{
    private sealed record RowChange(GridPrototypeRow Row, GridValues Before, GridValues After);
    private sealed record Operation(string Name, RowChange[] Changes, GridPrototypeRow? AddedRow = null);
    private readonly Stack<Operation> history = [];
    private int nextRowId = 101;

    public ObservableCollection<GridPrototypeRow> Rows { get; } = new(Enumerable.Range(1, 100).Select(id =>
        new GridPrototypeRow(id, new GridValues($"試験データ {id:000}", id % 3 == 0 ? "Closed" : "Open",
            id % 4 == 0 ? null : id / 10m, id % 5 == 0 ? null : new DateOnly(2026, 1, 1).AddDays(id - 1),
            id % 4 == 0 ? null : new[] { "High", "Medium", "Low" }[(id - 1) % 3]))));
    public int UndoCount => history.Count;
    public bool CanUndo => history.Count != 0;
    public string RowCountText => $"{Rows.Count} 行";
    public string UndoText => history.TryPeek(out var operation) ? $"元に戻す：{operation.Name}（{UndoCount} 操作）" : "元に戻す";
    public string StatusText { get; private set; } = "100行の試験データを表示しています。";
    public GridResult LastResult { get; private set; } = new(true, 0, []);
    // This measures application logic and notifications, not rendering or input latency.
    public double LastProcessingMilliseconds { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public GridResult Edit(GridAddress address, string text) => Measure(() => Apply("セル編集", [(address, text)]));

    public GridResult Paste(GridAddress anchor, string text) => Measure(() =>
    {
        var start = Rows.ToList().FindIndex(row => row.Id == anchor.RowId);
        if (start < 0 || !Enum.IsDefined(anchor.Field)) return Reject([new(anchor, "貼り付け先を選択してください。")]);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Contains('\r')) return Reject([new(anchor, "改行は CRLF または LF を使用してください。")]);
        // One final line terminator is a clipboard delimiter, not an extra row.
        if (normalized.EndsWith('\n')) normalized = normalized[..^1];
        var lines = normalized.Split('\n');
        var matrix = lines.Select(line => line.Split('\t')).ToArray();
        var width = matrix[0].Length;
        var ragged = Array.FindIndex(matrix, line => line.Length != width);
        if (ragged >= 0)
            return Reject([new(new(Rows[Math.Min(start + ragged, Rows.Count - 1)].Id, anchor.Field),
                $"貼り付けの{ragged + 1}行目の列数が一致しません（必要：{width}列）。")]);
        if ((int)anchor.Field + width > 5)
            return Reject([new(new(anchor.RowId, GridField.Choice), "貼り付け先が5列の編集範囲を超えます。")]);
        if (start + matrix.Length > Rows.Count)
            return Reject([new(anchor, "貼り付け先が行の範囲を超えます。先に新規行を追加してください。")]);

        var edits = new List<(GridAddress, string)>();
        for (var r = 0; r < matrix.Length; r++)
            for (var c = 0; c < width; c++)
                if (matrix[r][c].Length != 0)
                    edits.Add((new(Rows[start + r].Id, anchor.Field + c), matrix[r][c]));
        return Apply("範囲貼り付け", edits);
    });

    public GridResult Clear(IEnumerable<GridAddress> selection) => Measure(() =>
        Apply("値をクリア", selection.Distinct().Select(address => (address, ""))));

    public GridPrototypeRow AddRow()
    {
        var started = Stopwatch.GetTimestamp();
        ClearFeedback();
        var row = new GridPrototypeRow(nextRowId++, new("", "Open", null, null, null), true);
        Rows.Add(row);
        history.Push(new("新規行追加", [], row));
        LastResult = new(true, 0, []);
        StatusText = $"行 {row.RowLabel} を追加しました。タイトルを入力してください。";
        Notify();
        LastProcessingMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return row;
    }

    public bool Undo()
    {
        var started = Stopwatch.GetTimestamp();
        if (!history.TryPop(out var operation)) return false;
        ClearFeedback();
        if (operation.AddedRow is not null) Rows.Remove(operation.AddedRow);
        else foreach (var change in operation.Changes) change.Row.SetValues(change.Before);
        LastResult = new(true, 0, []);
        StatusText = $"{operation.Name}を元に戻しました。";
        Notify();
        LastProcessingMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return true;
    }

    public void ClearFeedback()
    {
        foreach (var row in Rows) row.SetErrors([]);
    }

    public void ShowInteractionError(string message)
    {
        StatusText = message;
        Notify();
    }

    private GridResult Apply(string name, IEnumerable<(GridAddress Address, string Text)> edits)
    {
        ClearFeedback();
        var rows = Rows.ToDictionary(row => row.Id);
        var staged = new Dictionary<int, GridValues>();
        var errors = new List<GridInputError>();
        var changedCells = new HashSet<GridAddress>();
        foreach (var (address, text) in edits)
        {
            if (!rows.TryGetValue(address.RowId, out var row))
            {
                errors.Add(new(address, "編集先の行がありません。"));
                continue;
            }
            var before = staged.GetValueOrDefault(address.RowId, row.Values);
            if (!GridFieldRules.TryChange(before, address.Field, text, out var after, out var error))
                errors.Add(new(address, error));
            else if (before != after)
            {
                staged[address.RowId] = after;
                changedCells.Add(address);
            }
        }
        if (errors.Count > 0) return Reject(errors);
        var changes = staged.Where(pair => rows[pair.Key].Values != pair.Value)
            .Select(pair => new RowChange(rows[pair.Key], rows[pair.Key].Values, pair.Value)).ToArray();
        if (changes.Length > 0)
        {
            foreach (var change in changes) change.Row.SetValues(change.After);
            history.Push(new(name, changes));
        }
        StatusText = changes.Length == 0 ? "変更はありません。" : $"{name}：{changedCells.Count}セルを変更しました。";
        return new(true, changes.Length == 0 ? 0 : changedCells.Count, []);
    }

    private GridResult Reject(IReadOnlyList<GridInputError> errors)
    {
        ClearFeedback();
        foreach (var row in Rows) row.SetErrors(errors.Where(error => error.Address.RowId == row.Id));
        var first = errors[0];
        StatusText = $"適用していません（{errors.Count}件）：行 {first.Address.RowId:000}・{GridFieldRules.Name(first.Address.Field)} — {first.Message}";
        return new(false, 0, errors);
    }

    private GridResult Measure(Func<GridResult> action)
    {
        var started = Stopwatch.GetTimestamp();
        LastResult = action();
        Notify();
        LastProcessingMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return LastResult;
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}
