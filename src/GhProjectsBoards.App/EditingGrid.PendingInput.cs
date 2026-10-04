using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private readonly HashSet<string> temporaryContextColumns = [];
    internal IEnumerable<string> TemporaryContextColumns => temporaryContextColumns;
    private readonly Button pendingInput = new() { MinHeight = 32, Padding = new(8, 4, 8, 4), Visibility = Visibility.Collapsed };
    private readonly Grid pendingContext = new() { ColumnSpacing = 12, Visibility = Visibility.Collapsed };
    private readonly TextBlock pendingValue = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock confirmedValue = new() { TextTrimming = TextTrimming.CharacterEllipsis };

    private void InitializePendingInput(StackPanel footer)
    {
        AutomationProperties.SetAutomationId(pendingInput, "GridPendingInput");
        AutomationProperties.SetHelpText(pendingInput, "次の入力途中のセルへ移動します。非表示の対象は一時表示します。");
        pendingInput.GettingFocus += (_, args) => { if (!CanRefresh) args.Cancel = true; };
        pendingInput.Click += (_, _) => {
            if (!CanRefresh) return;
            if (CurrentProjectView != ProjectView.Boards) ShowProjectView(ProjectView.Boards);
            GoToPendingInput();
        };
        AutomationProperties.SetAutomationId(pendingContext, "PendingInputContext");
        AutomationProperties.SetAutomationId(pendingValue, "PendingInputValue");
        AutomationProperties.SetAutomationId(confirmedValue, "ConfirmedInputValue");
        pendingContext.ColumnDefinitions.Add(new()); pendingContext.ColumnDefinitions.Add(new());
        pendingContext.Children.Add(pendingValue); SetColumn(confirmedValue, 1); pendingContext.Children.Add(confirmedValue);
        footer.Children.Add(pendingContext);
    }

    private void UpdatePendingCount(EditRow[] canonical)
    {
        var count = canonical.SelectMany(row => row.Cells).Count(cell => session.Workspace.Buffer(cell) is not null);
        pendingInput.Content = $"入力途中 {count}セル";
        pendingInput.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdatePendingInput()
    {
        var work = session.Workspace;
        if (!active || currentRow >= rows.Length || work.Buffer(rows[currentRow].Cells[currentColumn]) is not { } pending)
        { pendingContext.Visibility = Visibility.Collapsed; return; }
        var cell = rows[currentRow].Cells[currentColumn];
        string Display(string? value) => cell.Key?.Kind is "Select" or "LocalSelect" ? SelectDisplay(cell, value) : value ?? "（空値）";
        pendingValue.Text = "入力途中: " + pending;
        confirmedValue.Text = "確定値: " + Display(work.Value(cell));
        pendingContext.Visibility = CellHasProblem(cell) ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsPlanningField(EditCell cell) => cell.Key?.FieldId is { } fieldId
        && session.Workspace.Planning(projectId)?.Fields.Any(field => field.FieldId == fieldId) == true;

    private void GoToPendingInput()
    {
        if (!IsLoaded || !CanRefresh) return;
        var canonical = session.Workspace.Open(registration);
        var unfinished = canonical.SelectMany(row => row.Cells.Where(cell => session.Workspace.Buffer(cell) is not null)
            .Select(cell => (Row: row.ItemId, cell.Key))).ToArray();
        if (unfinished.Length == 0) { UpdatePendingCount(canonical); UpdatePendingInput(); return; }
        var current = Array.FindIndex(unfinished, target => SelectionIdentity == (target.Row, target.Key));
        var next = unfinished[(current + 1) % unfinished.Length];
        RevealContextCell(canonical, next.Row, next.Key);
    }

    // Recovery and unfinished-input routes reveal the same exact target without
    // saving a filter/column preference or consuming the selected editor's input.
    private bool RevealContextCell(EditRow[] canonical, string rowId, FieldKey? key)
    {
        var row = canonical.SingleOrDefault(candidate => candidate.ItemId == rowId);
        if (row is null || key is not null && !row.Cells.Any(cell => cell.Key == key)) return false;
        var changed = !rows.Any(candidate => candidate.ItemId == rowId);
        projection.IncludeNew(canonical, [rowId]);
        if (key?.FieldId is { } fieldId && layout.Hidden(fieldId))
        {
            temporaryContextColumns.Add(fieldId);
            layout = new(layout.Columns.Select(column => temporaryContextColumns.Contains(column.Id.FieldId ?? "")
                ? column with { Preference = column.Preference with { Visible = true } } : column).ToArray());
            changed = true;
        }
        if (changed) RebuildRows();
        var r = Array.FindIndex(rows, candidate => candidate.ItemId == rowId);
        var c = key is null ? 0 : Array.FindIndex(rows[r].Cells, cell => cell.Key == key);
        if (c < 0) return false;
        Select(r, c, false);
        return true;
    }
}
