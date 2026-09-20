using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal enum ProjectView { Boards, Gantt, Summary }

internal sealed partial class EditingGrid
{
    private GanttView? gantt;
    private SummaryView? summaryView;
    private SelectorBar? projectViews;
    private SelectorBarItem boardsView = null!, ganttView = null!, summaryItem = null!;
    private FrameworkElement[] boardsElements = [];
    private readonly Dictionary<UIElement, Visibility> boardsVisibility = [];
    private bool switchingView;
    private long ganttRevision = -1;
    private EditingWorkspace? ganttWorkspace;
    private long ganttProjectionGeneration = -1;
    internal bool ShowingGantt => gantt?.Visibility == Visibility.Visible;
    internal bool ShowingSummary => summaryView?.Visibility == Visibility.Visible;
    internal ProjectView CurrentProjectView => ShowingGantt ? ProjectView.Gantt : ShowingSummary ? ProjectView.Summary : ProjectView.Boards;
    internal string? SummaryPersonId => summaryView?.SelectedPersonId;
    internal (string Item, FieldKey? Field)? ViewSelection => ShowingGantt && gantt?.SelectedRowId is { } id
        ? (id, SelectionIdentity is { } selected && selected.Item == id ? selected.Field : canonicalRows.FirstOrDefault(r => r.ItemId == id)?.Cells[0].Key)
        : ShowingSummary && summaryView?.SelectedRowId is { } summaryRow ? (summaryRow, canonicalRows.FirstOrDefault(r => r.ItemId == summaryRow)?.Cells[0].Key) : SelectionIdentity;

    private void InitializeProjectViews(Grid commandRow)
    {
        boardsElements = Children.OfType<FrameworkElement>().ToArray();
        // The view selector and contextual commands share the existing header.
        // Adding a second full row would consume the sheet's working viewport.
        Children.Remove(commandRow);
        var header = new Grid();
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new());
        SetColumn(commandRow, 1); header.Children.Add(commandRow);
        projectViews = new SelectorBar { Padding = new(8, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = (Style)Application.Current.Resources["ProjectViewsStyle"] };
        AutomationProperties.SetAutomationId(projectViews, "ProjectViews");
        boardsView = new() { Text = "Boards" }; ganttView = new() { Text = "Gantt" };
        summaryItem = new SelectorBarItem { Text = summaryEnabled ? "Summary" : "Summary（準備中）", IsEnabled = summaryEnabled };
        AutomationProperties.SetAutomationId(boardsView, "ProjectViewBoards"); AutomationProperties.SetAutomationId(ganttView, "ProjectViewGantt");
        AutomationProperties.SetAutomationId(summaryItem, "ProjectViewSummary");
        projectViews.Items.Add(boardsView); projectViews.Items.Add(ganttView); projectViews.Items.Add(summaryItem);
        projectViews.SelectedItem = boardsView;
        // A view change must never end an active native composition merely by stealing focus.
        projectViews.GettingFocus += (_, args) => { if (!CanRefresh) args.Cancel = true; };
        projectViews.SelectionChanged += (_, _) => {
            if (switchingView) return;
            if (!CanRefresh) { switchingView = true; projectViews.SelectedItem = ShowingGantt ? ganttView : ShowingSummary ? summaryItem : boardsView; switchingView = false; return; }
            ShowProjectView(projectViews.SelectedItem == ganttView ? ProjectView.Gantt : projectViews.SelectedItem == summaryItem ? ProjectView.Summary : ProjectView.Boards);
        };
        header.Children.Add(projectViews); Children.Add(header);
    }
    internal void ShowProjectView(bool showGantt, string? selectedRowId = null)
        => ShowProjectView(showGantt ? ProjectView.Gantt : ProjectView.Boards, selectedRowId);
    internal void ShowProjectView(ProjectView view, string? selectedRowId = null, string? personId = null)
    {
        if (!CanRefresh || projectViews is null) return;
        if (view == ProjectView.Summary && !summaryEnabled) view = ProjectView.Boards;
        var prior = CurrentProjectView; var id = selectedRowId ?? ViewSelection?.Item;
        switchingView = true; projectViews.SelectedItem = view == ProjectView.Gantt ? ganttView : view == ProjectView.Summary ? summaryItem : boardsView; switchingView = false;
        if (prior == ProjectView.Boards && view != ProjectView.Boards)
        {
            boardsVisibility.Clear();
            foreach (var child in boardsElements) { boardsVisibility[child] = child.Visibility; child.Visibility = Visibility.Collapsed; }
        }
        if (gantt is not null) gantt.Visibility = Visibility.Collapsed;
        if (summaryView is not null) summaryView.Visibility = Visibility.Collapsed;
        if (view == ProjectView.Summary)
        {
            EnsureSummary(); summaryView!.Visibility = Visibility.Visible; UpdateSummary(true, personId, id);
        }
        else if (view == ProjectView.Gantt)
        {
            if (gantt is null)
            {
                gantt = new GanttView { Visibility = Visibility.Collapsed };
                SetRow(gantt, 1); SetRowSpan(gantt, RowDefinitions.Count - 1); Children.Add(gantt);
                gantt.EditRequested += async id => { if (SelectGanttRow(id)) { await ShowSchedulingEditorAsync(gantt.SchedulingAnchor); UpdateGantt(true); } };
                gantt.TaskDetailsRequested += async id => { if (SelectGanttRow(id)) { await PlanningDialogAsync(false); UpdateGantt(true); } };
                gantt.BoardsRequested += id => { if (SelectGanttRow(id)) ShowProjectView(false); };
                gantt.UndoRequested += () => { Run(Undo); UpdateGantt(true); };
                gantt.SettingsRequested += async () => { await PlanningDialogAsync(true); UpdateGantt(true); };
                gantt.SaveRequested += async () => { await FlushDraftsAsync("gantt-retry"); Update(); };
            }
            gantt.Visibility = Visibility.Visible; UpdateGantt(true, id);
        }
        else if (prior != ProjectView.Boards)
        {
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
