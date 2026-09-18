namespace GhProjectsBoards.Core.Projects;

internal sealed record CellRange(int Row, int Column, int RowCount = 1, int ColumnCount = 1)
{
    public bool Single => RowCount == 1 && ColumnCount == 1;
}
internal sealed record CopiedValue(string Kind, string? FieldId, string? Value, FieldKey? Source, bool Pending);
internal sealed record CopiedCells(ConnectionScope Scope, string ProjectId, CopiedValue[][] Values);

internal sealed partial class EditingWorkspace
{
    private static string ValueKind(EditCell cell) => cell.Key?.Kind switch {
        "Title" or "LocalTitle" => "Title", "Select" or "LocalSelect" => "Select",
        "LocalRepository" => "Repository", _ => "Reference"
    };

    private static void CheckRange(EditRow[] rows, CellRange range)
    {
        if (range.Row < 0 || range.Column < 0 || range.RowCount < 1 || range.ColumnCount < 1
            || range.RowCount > rows.Length - range.Row
            || rows.Skip(range.Row).Take(range.RowCount).Any(r => range.ColumnCount > r.Cells.Length - range.Column))
            throw new InvalidOperationException("貼り付け・コピー範囲が表の端を超えています。新規行は明示的に追加してください。");
    }

    public CopiedCells CopyCells(string projectId, EditRow[] rows, CellRange range)
    {
        CheckRange(rows, range);
        return new(Scope, projectId, rows.Skip(range.Row).Take(range.RowCount).Select(row =>
            row.Cells.Skip(range.Column).Take(range.ColumnCount).Select(cell => {
                if (cell.Scope != Scope) throw new InvalidOperationException("別プロフィールのセルです。");
                if (cell.Key is not null && cell.Availability is not (ValueAvailability.Present or ValueAvailability.Empty))
                    throw new InvalidOperationException("未取得・非対応の値を空欄としてコピーできません。");
                var value = cell.Key is null ? cell.Display : Value(cell);
                if (ValueKind(cell) == "Select" && value is not null && !cell.Options.Any(o => o.Id == value))
                    throw new InvalidOperationException("保存された選択肢IDを確認できません。");
                return new CopiedValue(ValueKind(cell), cell.Key?.FieldId, value, cell.Key, Buffer(cell) is not null);
            }).ToArray()).ToArray());
    }

    private void CheckBulkCell(EditRow row, EditCell cell, int position, bool pending)
    {
        var reason = !cell.Editable ? cell.Reason ?? "参照専用"
            : Field(cell)?.Conflict == true ? "競合の比較画面で採用値を選択してください。"
            : Field(cell)?.Observation?.Reason;
        if (pending && Buffer(cell) is not null) reason = "未確定入力があります。セルを確定または取消してから実行してください。";
        if (!pending && reason?.StartsWith("未確定文字") == true) reason = null;
        if (reason is not null) throw new InvalidOperationException($"行 {position + 1} [{row.ItemId}] / {cell.Display} [{cell.Key?.FieldId ?? cell.Key?.Kind}]: {reason}");
    }

    public void Fill(string projectId, EditRow[] rows, int sourceRow, int column, int firstRow, int lastRow)
    {
        CheckRange(rows, new(sourceRow, column));
        CheckRange(rows, new(firstRow, column, lastRow - firstRow + 1));
        var source = rows[sourceRow].Cells[column];
        CheckBulkCell(rows[sourceRow], source, sourceRow, true);
        var copied = CopyCells(projectId, rows, new(sourceRow, column)).Values[0][0];
        if (string.IsNullOrEmpty(copied.Value)) throw new InvalidOperationException("空値からのフィルはできません。「値をクリア」を使用してください。");
        var batch = new List<(EditCell, string, bool, bool)>();
        for (var r = firstRow; r <= lastRow; r++)
        {
            var cell = rows[r].Cells[column];
            CheckBulkCell(rows[r], cell, r, true);
            CheckCopiedValue(projectId, cell, copied, Scope, projectId);
            if (copied.Kind == "Select" && !cell.Options.Any(o => o.Id == copied.Value))
                throw new InvalidOperationException($"行 {r + 1} [{rows[r].ItemId}] / {cell.Display} [{cell.Key?.FieldId}]: 選択肢IDが存在しません。");
            if (r != sourceRow) batch.Add((cell, copied.Value, false, copied.Kind == "Select"));
        }
        Apply(projectId, batch.ToArray());
    }

    private static void CheckCopiedValue(string projectId, EditCell cell, CopiedValue value, ConnectionScope scope, string copiedProject)
    {
        if (cell.Scope != scope || ValueKind(cell) != value.Kind
            || value.Kind == "Select" && (value.FieldId != cell.Key?.FieldId || copiedProject != projectId))
            throw new InvalidOperationException("コピー元と対象のプロフィール・フィールドID・型が一致しません。");
    }
    private bool SourceHasBuffer(CopiedValue value)
    {
        if (value.Pending) return true;
        if (value.Source is not { } key) return false;
        if (!IsLocal(key)) return fields.TryGetValue(key, out var original) && original.Buffer is not null;
        var local = localRows.SingleOrDefault(row => row.Id == key.NodeId && row.ProjectId == key.ProjectId);
        return key.Kind == "LocalTitle" ? local?.TitleBuffer is not null : key.Kind == "LocalRepository" && local?.RepositoryBuffer is not null;
    }

    public void PasteSelection(string projectId, EditRow[] rows, CellRange selection, string tsv, CopiedCells? copied = null)
    {
        CheckRange(rows, selection);
        var matrix = ParseTsv(tsv);
        var single = matrix.Length == 1 && matrix[0].Length == 1;
        var expand = single && !selection.Single;
        if (expand && selection.ColumnCount != 1)
            throw new InvalidOperationException("1つの値を複数列へ展開できません。同じ列の範囲を選択してください。");
        if (!single && !selection.Single && (matrix.Length != selection.RowCount || matrix[0].Length != selection.ColumnCount))
            throw new InvalidOperationException("選択範囲とTSVの行数・列数が一致しません。全体を取り消しました。");
        var target = expand ? selection : new CellRange(selection.Row, selection.Column, matrix.Length, matrix[0].Length);
        CheckRange(rows, target);
        if (copied is not null && (copied.Values is null || copied.Values.Length != matrix.Length
            || copied.Values.Any(line => line is null || line.Length != matrix[0].Length || line.Any(v => v is null))))
            throw new InvalidOperationException("内部コピーの形状を確認できません。");
        var batch = new List<(EditCell, string, bool, bool)>();
        for (var r = 0; r < target.RowCount; r++)
            for (var c = 0; c < target.ColumnCount; c++)
            {
                var row = rows[target.Row + r]; var cell = row.Cells[target.Column + c];
                CheckBulkCell(row, cell, target.Row + r, expand && matrix[0][0] != "");
                var text = matrix[expand ? 0 : r][expand ? 0 : c];
                var value = copied?.Values[expand ? 0 : r][expand ? 0 : c];
                if (value is not null)
                {
                    CheckCopiedValue(projectId, cell, value, copied!.Scope, copied.ProjectId);
                    if (expand && text != "" && SourceHasBuffer(value))
                        throw new InvalidOperationException("コピー元に未確定入力があります。確定または取消してからコピーし直してください。");
                    text = value.Value ?? "";
                }
                if (text == "") continue;
                try
                {
                    if (value?.Kind != "Select") ValidateText(cell, text);
                    else if (!cell.Options.Any(o => o.Id == text)) throw new InvalidOperationException("選択肢IDが存在しません。");
                }
                catch (InvalidOperationException error)
                { throw new InvalidOperationException($"行 {target.Row + r + 1} [{row.ItemId}] / {cell.Display} [{cell.Key?.FieldId ?? cell.Key?.Kind}]: {error.Message}"); }
                batch.Add((cell, text, false, value?.Kind == "Select"));
            }
        Apply(projectId, batch.ToArray());
    }
}
