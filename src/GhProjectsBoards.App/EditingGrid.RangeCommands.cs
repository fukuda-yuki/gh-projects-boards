using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private Button rangeCommands = null!;
    private MenuFlyout rangeMenu = null!;
    private MenuFlyoutItem rangeFillDown = null!;
    private bool HasSelectedRange => active && currentRow < rows.Length && !ShowingGantt && !ShowingSummary && !SelectedRange().Single;
    private bool CurrentCellInputActive => active && currentRow < rows.Length
        && (session.Workspace.Buffer(rows[currentRow].Cells[currentColumn]) is not null
            || controls[currentRow][currentColumn] is TitleCell { Editing: true });

    private string SelectionRangeText()
    {
        var range = SelectedRange();
        var first = layout.Visible[Math.Min(anchorColumn, currentColumn)].Name;
        var last = range.ColumnCount == 1 ? "" : " ～ " + layout.Visible[Math.Max(anchorColumn, currentColumn)].Name;
        return $"{first}{last}：{range.RowCount}行・{range.RowCount * range.ColumnCount}セル";
    }

    private Button CreateRangeCommands()
    {
        rangeCommands = new Button { Content = "範囲操作", Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0), Padding = new(10, 6, 10, 6) };
        AutomationProperties.SetAutomationId(rangeCommands, "GridRangeCommands");
        rangeCommands.KeyDown += (_, args) => {
            if (args.Key == VirtualKey.Z && Down(VirtualKey.Control) && CanUseRangeCommands()
                && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), rangeCommands))
            { Run(Undo); args.Handled = true; }
        };
        rangeMenu = new MenuFlyout();
        var paste = new MenuFlyoutItem { Text = "選択範囲に貼り付け", Icon = new SymbolIcon(Symbol.Paste) };
        AutomationProperties.SetAutomationId(paste, "GridRangePaste");
        paste.Click += async (_, _) => { if (CanUseRangeCommands()) await PasteAsync(); };
        rangeFillDown = new MenuFlyoutItem { Text = "先頭セルの値を下へコピー", Icon = new SymbolIcon(Symbol.Download) };
        AutomationProperties.SetAutomationId(rangeFillDown, "GridRangeFillDown");
        rangeFillDown.Click += (_, _) => { if (CanUseRangeCommands()) Run(FillDown); };
        rangeMenu.Items.Add(paste); rangeMenu.Items.Add(rangeFillDown);
        rangeMenu.Opening += (_, _) => UpdateRangeCommands();
        rangeCommands.Flyout = rangeMenu;
        return rangeCommands;
    }

    private bool CanUseRangeCommands()
    {
        if (!active || currentRow >= rows.Length || !CanRefresh || SelectedRange().Single) return false;
        var cell = rows[currentRow].Cells[currentColumn];
        // Moving focus to a command does not finish the cell's native edit.
        return RowStillPresent(currentRow) && session.Workspace.Buffer(cell) is null
            && controls[currentRow][currentColumn] is not TitleCell { Editing: true };
    }

    private void UpdateRangeCommands()
    {
        if (rangeCommands is null) return;
        var available = CanUseRangeCommands();
        rangeCommands.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available) { rangeMenu.Hide(); return; }
        var range = SelectedRange();
        rangeFillDown.IsEnabled = range.ColumnCount == 1 && range.RowCount >= 2;
        rangeFillDown.Text = rangeFillDown.IsEnabled ? "先頭セルの値を下へコピー" : "下へコピー（1列を選択）";
        AutomationProperties.SetHelpText(rangeCommands, $"{range.RowCount}行・{range.RowCount * range.ColumnCount}セルを編集");
    }
}
