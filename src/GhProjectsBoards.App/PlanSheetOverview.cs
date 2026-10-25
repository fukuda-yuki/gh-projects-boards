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
    internal bool FoldingEnabled => !remoteBusy && !FilterActive;
    internal bool IsFolded(string identity) => folded.Contains(identity);
    private AppBarButton collapseAll = null!, expandAll = null!, goToDate = null!, predecessorAdd = null!;
    private AppBarButton linkTasks = null!, unlinkTasks = null!;
    // #231: the row-state commands also live in the overflow, with the same enablement and hints as the row
    // context menu, so the keyboard route does not depend on a pointer.
    private AppBarButton completeRows = null!, cancelRows = null!, removeRows = null!, reopenRows = null!;
    // #247: 行を削除 is the same row command in the overflow as in the row menu, beside the primary and Ctrl+-.
    private AppBarButton deleteOverflow = null!, deletePrimary = null!;
    private Flyout? predecessorFlyout;

    private void InitializeOverview(CommandBar commands)
    {
        predecessorAdd = AddCommand(commands, "先行タスクを追加…", "PlanSheetPredecessorAdd", Symbol.Link, OpenPredecessorSearch);
        linkTasks = AddCommand(commands, "タスクをリンク", "PlanSheetLinkTasks", Symbol.Link, () => LinkSelectedTasks(false), queueInSheet: false);
        unlinkTasks = AddCommand(commands, "リンクを解除", "PlanSheetUnlinkTasks", Symbol.Link, () => LinkSelectedTasks(true), queueInSheet: false);
        linkTasks.Icon = CommandIcon("M1,2 H6 V6 H1 Z M10,10 H15 V14 H10 Z M3,7 H4 V11 H7 V9 L10,11.5 L7,14 V12 H3 Z");
        unlinkTasks.Icon = CommandIcon("M1,2 H6 V6 H1 Z M10,10 H15 V14 H10 Z M3,7 H4 V12 H3 Z M6,8 L7,7 L9,9 L11,7 L12,8 L10,10 L12,12 L11,13 L9,11 L7,13 L6,12 L8,10 Z");
        linkTasks.KeyboardAcceleratorTextOverride = "Ctrl+F2";
        unlinkTasks.KeyboardAcceleratorTextOverride = "Ctrl+Shift+F2";
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
        RefreshOutlineCommands();
        collapseAll.IsEnabled = expandAll.IsEnabled = FoldingEnabled && SummaryIds.Count > 0;
        var reason = FilterActive ? "絞り込み中は折りたためません" : "";
        foreach (var button in new[] { collapseAll, expandAll }) {
            button.AllowFocusWhenDisabled = reason.Length > 0;
            AutomationProperties.SetHelpText(button, reason);
            ToolTipService.SetToolTip(button, reason.Length > 0 ? reason : button.Label);
        }
        goToDate.IsEnabled = SelectedDate() is not null;
        // #231: the row-state commands report the same eligibility and reasons in the overflow as in the row
        // context menu, so a PMO reading either route is told the same thing.
        foreach (var (button, action) in new[] { (completeRows, PlanRowAction.Complete), (cancelRows, PlanRowAction.Cancel),
            (removeRows, PlanRowAction.Remove), (reopenRows, PlanRowAction.Reopen) }) {
            var (enabled, hint) = RowCommandState(CommandTargets, action);
            button.IsEnabled = enabled; button.AllowFocusWhenDisabled = true;
            AutomationProperties.SetHelpText(button, hint ?? "");
            ToolTipService.SetToolTip(button, hint is null ? button.Label : button.Label + ": " + hint);
        }
        // #247: 行を削除 carries the row menu's refusal (a published Issue cannot be deleted) on the primary
        // command and in the overflow too, so no route offers a deletion the plan would refuse.
        var deleteReason = DeleteReason(CommandTargets);
        foreach (var button in new[] { deletePrimary, deleteOverflow }) {
            button.IsEnabled = deleteReason is null; button.AllowFocusWhenDisabled = true;
            AutomationProperties.SetHelpText(button, deleteReason ?? "");
            ToolTipService.SetToolTip(button, deleteReason ?? button.Label);
        }
        predecessorAdd.IsEnabled = Rows.ContainsKey(selected);
        foreach (var (button, unlink) in new[] { (linkTasks, false), (unlinkTasks, true) }) {
            var unavailable = TaskLinkUnavailable(unlink) ?? PlanTaskLinks.Prepare(Session.Document.State, MoveTargets, unlink).Reason;
            button.IsEnabled = unavailable is null; button.AllowFocusWhenDisabled = true;
            AutomationProperties.SetHelpText(button, unavailable ?? "");
            ToolTipService.SetToolTip(button, unavailable ?? button.Label);
        }
    }

    private string? TaskLinkUnavailable(bool unlink) => remoteBusy ? "更新または発行が進行中です。"
        : FilterActive ? unlink ? "絞り込み中はリンクを解除できません" : "絞り込み中はリンクできません" : null;

    internal Task LinkSelectedTasks(bool unlink)
    {
        var targets = MoveTargets;
        return Run(async () => {
            await CommitPending();
            if (TaskLinkUnavailable(unlink) is { } unavailable) throw new ArgumentException(unavailable);
            var (command, reason) = PlanTaskLinks.Prepare(Session.Document.State, targets, unlink);
            if (reason is not null) throw new ArgumentException(reason);
            Check(await Session.Execute(command!, Today));
            Refresh();
        });
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
        var settings = Session.Document.State.Settings;
        dates = dates.Append(StatusDate).Concat(Session.Document.Baseline.Milestones.Where(m => m.DueDate.HasValue).Select(m => m.DueDate!.Value))
            .Concat(new[] { settings.ProjectStart, settings.ProjectEnd }.Where(day => day is not null).Select(day => day!.Value)).ToArray();
        // Every scale ends at the version plus a short margin: padding a coarse scale by viewport fractions
        // filled the 月 chart with a year of empty months. The range stays independent of selection, so
        // choosing another task never moves the date coordinates; the anchor is clamped into the range instead.
        // Full-period fixed marks use pixel gutters in the fitted scale, not extra calendar days.
        var margin = acceptedZoom == 3 ? 0 : TimelineMarginDays;
        FirstDay = DateOnly.FromDayNumber(Math.Max(0, dates.Min().DayNumber - margin));
        var last = Math.Min(DateOnly.MaxValue.DayNumber, dates.Max().DayNumber + margin);
        DayCount = last - FirstDay.DayNumber + 1;
        return (previousFirst.DayNumber - FirstDay.DayNumber) * DayWidth;
    }

    internal const int TimelineMarginDays = 7;
    // A row the sheet selected by itself because a filter or fold hid the PMO's row is not the PMO's
    // choice: until the PMO selects a row, opening and scale changes keep the 基準日 in place (#265).
    // A sheet opens on the empty new-task row, which carries no dates, so the same rule covers opening.
    private string? automaticSelection;
    private DateOnly TimelineAnchor => selected != automaticSelection ? SelectedDate() ?? StatusDate : StatusDate;

    internal static double AnchorOffset(DateOnly anchor, DateOnly first, double dayWidth, double viewport) =>
        Math.Max(0, (anchor.DayNumber - first.DayNumber) * dayWidth - viewport / 5);

    private void PositionTimelineAnchor(DateOnly? anchor = null)
    {
        // ChangeView clamps against the arranged native extent, not the new content width alone.
        chartHorizontal.UpdateLayout();
        ChangeTimelineOffset(acceptedZoom == 3 ? 0 : AnchorOffset(anchor ?? TimelineAnchor, FirstDay, DayWidth, ChartViewport));
    }

    private async Task GoToStatusDate()
    {
        await CommitPending();
        PositionTimelineAnchor(StatusDate);
    }

    private DateOnly? SelectedDate() => Schedule.TryGetValue(selected, out var task) ? task.Start.Value ?? task.End.Value : null;

    private async Task GoToSelectedDate()
    {
        await CommitPending();
        if (SelectedDate() is not { } day) return;
        var offset = Math.Max(0, (day.DayNumber - FirstDay.DayNumber - 1) * DayWidth);
        // A just-changed scale must update the native extent before ChangeView clamps the offset.
        chartHorizontal.UpdateLayout();
        ChangeTimelineOffset(offset);
    }

    private sealed record PredecessorChoice(string Identity, string Label, string Search);

    private async Task OpenPredecessorSearch()
    {
        await CommitPending();
        var target = selected;
        if (!Rows.ContainsKey(target)) return;
        predecessorFlyout?.Hide();
        var query = Id(new TextBox { PlaceholderText = "#番号・タスク名・担当者・要求事項" }, "PlanPredecessorSearch");
        AutomationProperties.SetName(query, "先行タスクを検索");
        var candidates = Id(new ListView { Height = 260, SelectionMode = ListViewSelectionMode.Single }, "PlanPredecessorCandidates");
        AutomationProperties.SetName(candidates, "先行タスクの候補");
        var confirm = Id(new Button { Content = "追加", IsEnabled = false, Style = (Style)Application.Current.Resources["AccentButtonStyle"] }, "PlanPredecessorConfirm");
        var cancel = Id(new Button { Content = "キャンセル" }, "PlanPredecessorCancel");
        var feedback = Id(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush("SystemFillColorCriticalBrush") }, "PlanPredecessorError");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(confirm); actions.Children.Add(cancel);
        var content = new StackPanel { Width = Math.Min(560, Math.Max(240, ActualWidth - 80)), Spacing = 8 };
        content.Children.Add(query); content.Children.Add(candidates); content.Children.Add(feedback); content.Children.Add(actions);
        var flyout = predecessorFlyout = new Flyout {
            AreOpenCloseAnimationsEnabled = false,
            Content = content, Placement = FlyoutPlacementMode.Bottom,
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter)) { BasedOn = (Style)Application.Current.Resources["PlanFlyoutPresenterStyle"], Setters = {
                new Setter(FrameworkElement.MaxWidthProperty, Math.Max(0, XamlRoot.Size.Width - 32))
            } }
        };
        // One line per task (#263): the name the 先行 cell will show, then what tells two similar titles apart.
        var choices = PlanPredecessorChoices.Rank(Session.Document, target).Where(Rows.ContainsKey).Select(identity => {
            var row = Rows[identity];
            var ancestors = new List<string>(); var current = row;
            var visited = new HashSet<string>();
            while (current.Parent is { } parent && visited.Add(parent) && Rows.TryGetValue(parent, out current!)) ancestors.Add(current.Title);
            var assignee = row.Assignees.Length == 0 ? "" : " " + AssigneeName(row.Assignees[0]);
            var end = Schedule.GetValueOrDefault(identity)?.End.Value is { } day ? " · 終了 " + PlanSheetText.Date(day) : "";
            var label = $"{TaskNames[identity]} {row.Title}{assignee}{end}";
            return new PredecessorChoice(identity, label, label + " " + string.Join(" ", ancestors));
        }).ToArray();
        PredecessorChoice[] matches = [];
        void Search()
        {
            matches = choices.Where(choice => choice.Search.Contains(PlanInput.Fold(query.Text).Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
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
        // A menu opening this flyout restores its own focus after the flyout has opened; claim keyboard focus
        // again on the next two dispatcher turns so the search is ready to type into.
        void FocusQuery() {
            if (ReferenceEquals(predecessorFlyout, flyout) && flyout.IsOpen && query.IsLoaded) query.Focus(FocusState.Programmatic);
        }
        query.Loaded += (_, _) => FocusQuery();
        flyout.Opened += (_, _) => {
            FocusQuery();
            DispatcherQueue.TryEnqueue(FocusQuery);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusQuery);
        };
        flyout.Closed += (_, _) => { if (ReferenceEquals(predecessorFlyout, flyout)) predecessorFlyout = null; };
        Search();
        // The whole sheet leaves no useful space above or below its placement bounds.
        flyout.ShowAt(commandBar);
    }
}
