using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;

namespace GhProjectsBoards.App;

internal sealed partial class PlanSheetView
{
    private readonly HashSet<string> folded = [];
    internal IReadOnlySet<string> SummaryIds { get; private set; } = new HashSet<string>();
    internal bool FoldingEnabled => !remoteBusy && acceptedFilter.Length == 0;
    internal bool IsFolded(string identity) => folded.Contains(identity);
    private AppBarButton collapseAll = null!, expandAll = null!, goToDate = null!, predecessorAdd = null!;
    private Flyout? predecessorFlyout;

    private void InitializeOverview(CommandBar commands)
    {
        predecessorAdd = AddCommand(commands, "先行タスクを追加…", "PlanSheetPredecessorAdd", Symbol.Link, OpenPredecessorSearch);
        commands.PrimaryCommands.Add(new AppBarSeparator());
        collapseAll = AddCommand(commands, "すべて折りたたむ", "PlanSheetCollapseAll", Symbol.Remove, () => FoldAll(true));
        expandAll = AddCommand(commands, "すべて展開", "PlanSheetExpandAll", Symbol.Add, () => FoldAll(false));
        goToDate = AddCommand(commands, "選択タスクの日程へ移動", "PlanSheetGoToDate", Symbol.Calendar, GoToSelectedDate);
        collapseAll.Icon = CommandIcon("M3,2 L4,1 L8,5 L12,1 L13,2 L8,7 Z M3,14 L8,9 L13,14 L12,15 L8,11 L4,15 Z");
        expandAll.Icon = CommandIcon("M3,6 L8,1 L13,6 L12,7 L8,3 L4,7 Z M3,10 L4,9 L8,13 L12,9 L13,10 L8,15 Z");
        goToDate.Icon = CommandIcon("F0 M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2 Z M8,3 A5,5 0 1 1 8,13 A5,5 0 1 1 8,3 Z M7.5,0 H8.5 V5 H7.5 Z M7.5,11 H8.5 V16 H7.5 Z M0,7.5 H5 V8.5 H0 Z M11,7.5 H16 V8.5 H11 Z");
    }

    private void RefreshOverviewCommands()
    {
        collapseAll.IsEnabled = expandAll.IsEnabled = FoldingEnabled && SummaryIds.Count > 0;
        goToDate.IsEnabled = SelectedDate() is not null;
        predecessorAdd.IsEnabled = Rows.ContainsKey(selected);
    }

    private bool HiddenByFold(PlanRow row)
    {
        var visited = new HashSet<string>();
        while (row.Parent is { } parent && visited.Add(parent))
        {
            if (folded.Contains(parent)) return true;
            if (!Rows.TryGetValue(parent, out row!)) break;
        }
        return false;
    }

    private string? VisibleAncestor(string identity)
    {
        var visited = new HashSet<string>();
        while (Rows.TryGetValue(identity, out var row) && row.Parent is { } parent && visited.Add(parent))
        {
            if (RowIds.Contains(parent)) return parent;
            identity = parent;
        }
        return null;
    }

    internal async Task SetFold(string identity, bool collapse)
    {
        if (!FoldingEnabled || !SummaryIds.Contains(identity)) return;
        await CommitPending();
        if (collapse) folded.Add(identity); else folded.Remove(identity);
        Refresh();
    }

    private async Task FoldAll(bool collapse)
    {
        if (!FoldingEnabled) return;
        await CommitPending();
        folded.Clear();
        if (collapse) folded.UnionWith(SummaryIds);
        Refresh();
    }

    private double UpdateTimelineRange()
    {
        var previousFirst = FirstDay;
        var dates = Schedule.Values.SelectMany(task => new[] { task.Start.Value, task.End.Value })
            .Where(day => day is not null).Select(day => day!.Value).ToArray();
        dates = dates.Append(StatusDate).ToArray();
        // Any task can become the zoom anchor without selection changing the date coordinate system.
        // Full-period fixed marks use pixel gutters in the fitted scale, not extra calendar days.
        var lead = acceptedZoom == 3 ? 0 : (int)Math.Ceiling(ChartViewport / (4 * DayWidth));
        var trail = acceptedZoom == 3 ? 0 : (int)Math.Ceiling(ChartViewport * .75 / DayWidth);
        FirstDay = DateOnly.FromDayNumber(Math.Max(0, dates.Min().DayNumber - lead));
        var last = Math.Min(DateOnly.MaxValue.DayNumber, dates.Max().DayNumber + trail);
        var span = last - FirstDay.DayNumber + 1;
        DayCount = acceptedZoom == 3 ? span : Math.Max(365, span);
        return (previousFirst.DayNumber - FirstDay.DayNumber) * DayWidth;
    }

    private DateOnly TimelineAnchor => SelectedDate() ?? StatusDate;

    internal static double AnchorOffset(DateOnly anchor, DateOnly first, double dayWidth, double viewport) =>
        Math.Max(0, (anchor.DayNumber - first.DayNumber) * dayWidth - viewport / 4);

    private void PositionTimelineAnchor()
    {
        // ChangeView clamps against the arranged native extent, not the new content width alone.
        chartHorizontal.UpdateLayout();
        chartHorizontal.ChangeView(acceptedZoom == 3 ? 0 : AnchorOffset(TimelineAnchor, FirstDay, DayWidth, ChartViewport), null, null, true);
    }

    private DateOnly? SelectedDate() => Schedule.TryGetValue(selected, out var task) ? task.Start.Value ?? task.End.Value : null;

    private async Task GoToSelectedDate()
    {
        await CommitPending();
        if (SelectedDate() is not { } day) return;
        var offset = Math.Max(0, (day.DayNumber - FirstDay.DayNumber - 1) * DayWidth);
        // A just-changed scale must update the native extent before ChangeView clamps the offset.
        chartHorizontal.UpdateLayout();
        chartHorizontal.ChangeView(offset, null, null, true);
    }

    private sealed record PredecessorChoice(string Identity, string Label, string Search);

    private async Task OpenPredecessorSearch()
    {
        await CommitPending();
        var target = selected;
        if (!Rows.ContainsKey(target)) return;
        predecessorFlyout?.Hide();
        var query = Id(new TextBox { Header = "先行タスクを検索", PlaceholderText = "要求事項・タスク名・Issue番号" }, "PlanPredecessorSearch");
        AutomationProperties.SetName(query, "先行タスクを検索");
        var candidates = Id(new ListView { Height = 260, SelectionMode = ListViewSelectionMode.Single }, "PlanPredecessorCandidates");
        AutomationProperties.SetName(candidates, "先行タスクの候補");
        var confirm = Id(new Button { Content = "追加", IsEnabled = false }, "PlanPredecessorConfirm");
        var cancel = Id(new Button { Content = "キャンセル" }, "PlanPredecessorCancel");
        var feedback = Id(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush("SystemFillColorCriticalBrush") }, "PlanPredecessorError");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(confirm); actions.Children.Add(cancel);
        var content = new StackPanel { Width = Math.Min(560, Math.Max(240, ActualWidth - 80)), Spacing = 8 };
        content.Children.Add(query); content.Children.Add(candidates); content.Children.Add(feedback); content.Children.Add(actions);
        var flyout = predecessorFlyout = new Flyout {
            Content = content, Placement = FlyoutPlacementMode.Bottom,
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter)) { Setters = {
                new Setter(FrameworkElement.MaxWidthProperty, Math.Max(0, XamlRoot.Size.Width - 32))
            } }
        };
        var choices = Session.Document.State.Rows.Where(row => row.Identity != target).Select(row => {
            var ancestors = new List<string>(); var current = row;
            var visited = new HashSet<string>();
            while (current.Parent is { } parent && visited.Add(parent) && Rows.TryGetValue(parent, out current!)) ancestors.Add(current.Title);
            ancestors.Reverse();
            var context = string.Join(" / ", ancestors);
            var reference = Session.Document.Sync.IssueLinks.GetValueOrDefault(row.Identity)?.Caption ?? "";
            var label = $"{context}  {row.Title}  · ID {PlanIds[row.Identity]}  {reference}".Trim();
            return new PredecessorChoice(row.Identity, label, context + " " + row.Title + " " + reference);
        }).ToArray();
        PredecessorChoice[] matches = [];
        void Search()
        {
            matches = choices.Where(choice => choice.Search.Contains(query.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
            candidates.ItemsSource = matches.Select(choice => choice.Label).ToArray();
            confirm.IsEnabled = false;
        }
        query.TextChanged += (_, _) => Search();
        candidates.SelectionChanged += (_, _) => confirm.IsEnabled = candidates.SelectedIndex >= 0;
        confirm.Click += async (_, _) => {
            if (candidates.SelectedIndex < 0) return;
            var chosen = matches[candidates.SelectedIndex].Identity;
            await Run(async () => {
                try
                {
                    if (!Rows.ContainsKey(target) || !Rows.ContainsKey(chosen)) throw new InvalidOperationException("タスクを選び直してください。");
                    var row = Session.Document.State.Rows.Single(row => row.Identity == target);
                    if (!row.Predecessors.Contains(chosen))
                        Check(await Session.Execute(new EditPlanCells(PlanOperationKind.Cell,
                            [new(target, PlanField.Predecessors, row.Predecessors.Add(chosen))]), Today));
                    flyout.Hide(); Refresh(); FocusSelected();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                { feedback.Text = ex.Message; }
            }, "Add predecessor");
        };
        cancel.Click += (_, _) => flyout.Hide();
        content.KeyDown += (_, args) => { if (args.Key == VirtualKey.Escape) { flyout.Hide(); args.Handled = true; } };
        flyout.Opened += (_, _) => query.Focus(FocusState.Programmatic);
        flyout.Closed += (_, _) => { if (ReferenceEquals(predecessorFlyout, flyout)) predecessorFlyout = null; };
        Search();
        commandBar.IsOpen = false;
        // The whole sheet leaves no useful space above or below its placement bounds.
        flyout.ShowAt(commandBar);
    }
}
