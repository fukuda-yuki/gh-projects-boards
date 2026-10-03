using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private void RefreshCreationRowIdentity(int row)
    {
        void Refresh(TextBlock label)
        {
            label.Text = RowIdentity(rows[row], compact: true);
            ToolTipService.SetToolTip(label, RowIdentity(rows[row]));
        }
        if (recycledPresentation)
        {
            if (recycledRows.TryGetValue(row, out var presentation)) Refresh(presentation.Identity);
            var key = (rows[row].ItemId, rows[row].Cells[0].Key, layout.Visible[0].Id);
            if (ownedEditors.TryGetValue(key, out var owned) && owned.RowIdentityLabel is { } label)
            {
                AutomationProperties.SetAutomationId(label, $"GridEditorRowIdentity{row}");
                Refresh(label);
            }
        }
        else if (rowLines[row] is { } line)
            foreach (var label in Descendants(line).OfType<TextBlock>().Where(text => AutomationProperties.GetAutomationId(text) == $"GridRowIdentity{row}")) Refresh(label);
    }

    private void RevealAddedRow(string localId)
    {
        var request = generation;
        var previousSelection = SelectionIdentity;
        // Explicit Add creates a target. Finish its new-row/footer layout before
        // requesting the viewport; ordinary refresh and selection never use this.
        Update("add-row");
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded || request != generation || !CanRefresh || CurrentProjectView != ProjectView.Boards || SelectionIdentity != previousSelection) return;
            var row = Array.FindIndex(rows, candidate => candidate.ItemId == localId && candidate.IsLocal);
            if (row < 0 || !RowStillPresent(row)) return;
            UpdateLayout(); AttachSheetScroll();
            list.ScrollIntoView(list.Items[row], ScrollIntoViewAlignment.Leading);
            list.UpdateLayout();
            Select(row, 0, false, false);
            UpdateLayout();
            // Selection can reveal pending/context rows above the footer. Use
            // that final viewport before granting native input to the new cell.
            list.ScrollIntoView(list.Items[row], ScrollIntoViewAlignment.Leading);
            list.UpdateLayout();
            if (recycledPresentation) PositionOwnedEditors();
            if (request == generation && IsLoaded && CanRefresh && RowStillPresent(row)) Select(row, 0, false);
        });
    }
}
