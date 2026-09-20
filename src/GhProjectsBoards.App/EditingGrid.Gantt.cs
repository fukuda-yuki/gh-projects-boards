using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private GanttView? gantt;
    private SelectorBar? projectViews;
    private SelectorBarItem boardsView = null!, ganttView = null!;
    private FrameworkElement[] boardsElements = [];
    private readonly Dictionary<UIElement, Visibility> boardsVisibility = [];
    private bool switchingView;
    private long ganttRevision = -1;
    private EditingWorkspace? ganttWorkspace;
    private long ganttProjectionGeneration = -1;
    internal bool ShowingGantt => gantt?.Visibility == Visibility.Visible;
    internal (string Item, FieldKey? Field)? ViewSelection => ShowingGantt && gantt?.SelectedRowId is { } id
        ? (id, SelectionIdentity is { } selected && selected.Item == id ? selected.Field : canonicalRows.FirstOrDefault(r => r.ItemId == id)?.Cells[0].Key)
        : SelectionIdentity;

    private void InitializeProjectViews()
    {
        boardsElements = Children.OfType<FrameworkElement>().ToArray();
        RowDefinitions.Insert(0, new() { Height = GridLength.Auto });
        foreach (var child in boardsElements) SetRow(child, GetRow(child) + 1);
        projectViews = new SelectorBar { Padding = new(8, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = (Style)Application.Current.Resources["ProjectViewsStyle"] };
        AutomationProperties.SetAutomationId(projectViews, "ProjectViews");
        boardsView = new() { Text = "Boards" }; ganttView = new() { Text = "Gantt" };
        var summary = new SelectorBarItem { Text = "Summary", IsEnabled = false };
        ToolTipService.SetToolTip(summary, "Summaryは今後対応します。");
        AutomationProperties.SetAutomationId(boardsView, "ProjectViewBoards"); AutomationProperties.SetAutomationId(ganttView, "ProjectViewGantt");
        AutomationProperties.SetAutomationId(summary, "ProjectViewSummary");
        projectViews.Items.Add(boardsView); projectViews.Items.Add(ganttView); projectViews.Items.Add(summary);
        projectViews.SelectedItem = boardsView;
        // A view change must never end an active native composition merely by stealing focus.
        projectViews.GettingFocus += (_, args) => { if (!CanRefresh) args.Cancel = true; };
        projectViews.SelectionChanged += (_, _) => {
            if (switchingView) return;
            if (!CanRefresh) { switchingView = true; projectViews.SelectedItem = ShowingGantt ? ganttView : boardsView; switchingView = false; return; }
            ShowProjectView(projectViews.SelectedItem == ganttView);
        };
        Children.Add(projectViews);
    }
    internal void ShowProjectView(bool showGantt, string? selectedRowId = null)
    {
        if (!CanRefresh || projectViews is null) return;
        switchingView = true; projectViews.SelectedItem = showGantt ? ganttView : boardsView; switchingView = false;
        if (showGantt)
        {
            if (gantt is null)
            {
                gantt = new GanttView { Visibility = Visibility.Collapsed };
                SetRow(gantt, 1); SetRowSpan(gantt, RowDefinitions.Count - 1); Children.Add(gantt);
                gantt.EditRequested += async id => { if (SelectGanttRow(id)) { await PlanningDialogAsync(false); UpdateGantt(true); } };
                gantt.BoardsRequested += id => { if (SelectGanttRow(id)) ShowProjectView(false); };
                gantt.UndoRequested += () => { Run(Undo); UpdateGantt(true); };
                gantt.SettingsRequested += async () => { await PlanningDialogAsync(true); UpdateGantt(true); };
                gantt.SaveRequested += async () => { await FlushDraftsAsync("gantt-retry"); Update(); };
            }
            if (!ShowingGantt)
            {
                boardsVisibility.Clear();
                foreach (var child in boardsElements) { boardsVisibility[child] = child.Visibility; child.Visibility = Visibility.Collapsed; }
            }
            gantt.Visibility = Visibility.Visible; UpdateGantt(true, selectedRowId ?? SelectionIdentity?.Item);
        }
        else if (gantt is not null)
        {
            var id = gantt.SelectedRowId;
            gantt.Visibility = Visibility.Collapsed;
            foreach (var (child, visibility) in boardsVisibility) child.Visibility = visibility;
            boardsVisibility.Clear();
            if (id is not null) SelectGanttRow(id);
            Update();
            // A Project can initially open in Gantt: the collapsed sheet has not
            // necessarily realized its native scroll template yet.
            UpdateLayout(); AttachSheetScroll();
            if (active) { list.ScrollIntoView(list.Items[currentRow]); list.UpdateLayout(); RestoreWorkspaceFocus(); }
        }
    }
    private void AttachSheetScroll()
    {
        var scroll = Descendants(list).OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null || ReferenceEquals(scroll, listScroll)) return;
        DetachWheel();
        if (listScroll is not null) { listScroll.ViewChanged -= ScrollChanged; listScroll.SizeChanged -= ScrollSizeChanged; }
        listScroll = scroll; listScroll.ViewChanged += ScrollChanged; listScroll.SizeChanged += ScrollSizeChanged;
        AttachWheel(); ResizeSheetColumns();
    }
    private bool SelectGanttRow(string id)
    {
        var r = Array.FindIndex(rows, row => row.ItemId == id);
        if (r < 0)
        {
            projection.IncludeNew(session.Workspace.Open(registration), [id]); RebuildRows();
            r = Array.FindIndex(rows, row => row.ItemId == id);
        }
        if (r < 0) return false;
        var column = active && rows[currentRow].ItemId == id ? currentColumn : 0;
        Select(r, column, false, false); return true;
    }
    private void UpdateGantt(bool force = false, string? selected = null)
    {
        if (!ShowingGantt) return;
        gantt!.ShowOperationStatus(operationProblem, session.Status);
        if (!force && ganttWorkspace == session.Workspace && ganttRevision == session.Workspace.PresentationRevision && ganttProjectionGeneration == projection.Generation) return;
        gantt!.Present(GanttProjection.Create(session.Workspace, registration, projection.Ids), selected);
        ganttWorkspace = session.Workspace; ganttRevision = session.Workspace.PresentationRevision; ganttProjectionGeneration = projection.Generation;
    }
}
