using System.Globalization;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace GhProjectsBoards.App;

internal sealed record GanttViewPosition(DateTime LeftDate, bool Week, string? TopRowId, double RowInset, double VerticalOffset);

// Owns viewport geometry only. Scheduling, edits and persistence stay in the shared workspace.
internal sealed class GanttView : Grid
{
    private const double RowHeight = 52;
    private readonly GanttList list;
    private readonly Grid header = new() { Height = 44 };
    private readonly Canvas axisCanvas = new() { Height = 44 };
    private readonly Canvas lines = new() { IsHitTestVisible = false };
    private readonly ScrollViewer horizontal = new() { Height = 18, HorizontalScrollMode = ScrollMode.Enabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Visible, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Border horizontalExtent = new() { Height = 1 };
    private readonly TextBlock summary = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock selectedText = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly InfoBar operationStatus = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private readonly ComboBox related = new FormComboBox() { MinWidth = 170, MaxWidth = 430, HorizontalAlignment = HorizontalAlignment.Stretch,
        Header = "先行 → 選択 → 後続", DisplayMemberPath = nameof(Relation.Label), PlaceholderText = "関係を選択" };
    private readonly TextBox search = new() { PlaceholderText = "タイトル・番号を検索", Width = 200 };
    private readonly HashSet<GanttLine> realized = [];
    private readonly Button edit;
    private readonly Button board;
    private readonly Button reveal;
    private readonly Button context;
    private readonly Button dailyProgress;
    private readonly Button changedSchedule = new() { Content = "変更後の日程を見る", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
    private string? changedScheduleRow;
    private bool dailyProgressActive;
    private readonly ComboBox scale = new FormComboBox() { Width = 76, ItemsSource = new[] { "日", "週" }, SelectedIndex = 0 };
    private GanttProjection projection = new([], new("", 0, []));
    private readonly Dictionary<string, PlanningWarningPresentation> warningPresentations = [];
    private GanttAxis axis = new(new(2026, 1, 1), 28, 96);
    private GanttRow[] shown = [];
    private bool updating;
    private double identityWidth = 330;
    private ScrollViewer? vertical;
    private GanttViewPosition? pendingPosition;
    private WorkingCalendar? calendar;
    internal event Action<string>? EditRequested;
    internal event Action<string>? TaskDetailsRequested;
    internal event Action<string>? ProgressRequested;
    internal event Action<string>? DailyProgressRequested;
    internal event Action<string>? BoardsRequested;
    internal event Action? UndoRequested;
    internal event Action? SettingsRequested;
    internal string? SelectedRowId => (list.SelectedItem as GanttRow)?.RowId;
    internal GanttProjection AdoptedProjection => projection;
    internal GanttAxis Axis => axis;
    internal FrameworkElement SchedulingAnchor { get; }
    private sealed record Relation(string Label, string? RowId);

    internal GanttView()
    {
        AutomationProperties.SetAutomationId(this, "GanttView");
        Style = (Style)Application.Current.Resources["GanttSurfaceStyle"];
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new());
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsDynamicOverflowEnabled = true, HorizontalAlignment = HorizontalAlignment.Left };
        SchedulingAnchor = commands;
        AutomationProperties.SetAutomationId(commands, "GanttCommands");
        Button Tool(string text, string id, Symbol icon, Action action, bool secondary = false)
        {
            var button = new AppBarButton { Label = text, Icon = new SymbolIcon(icon) };
            AutomationProperties.SetAutomationId(button, id); AutomationProperties.SetName(button, text);
            button.Click += (_, _) => action();
            if (secondary) commands.SecondaryCommands.Add(button); else commands.PrimaryCommands.Add(button);
            return button;
        }
        edit = Tool("日程を編集", "GanttEdit", Symbol.Edit, () => { if (SelectedRowId is { } id) EditRequested?.Invoke(id); });
        dailyProgress = Tool("実績・進捗", "GanttProgress", Symbol.Edit, () => { if (SelectedRowId is { } id) DailyProgressRequested?.Invoke(id); });
        board = Tool("表で開く", "GanttBoards", Symbol.ViewAll, () => { if (SelectedRowId is { } id) BoardsRequested?.Invoke(id); });
        reveal = Tool("選択へ移動", "GanttReveal", Symbol.Find, RevealSelection);
        context = Tool("日程の理由", "GanttDetails", Symbol.List, ShowDetails);
        Tool("元に戻す", "GanttUndo", Symbol.Undo, () => UndoRequested?.Invoke());
        Tool("計画の前提", "GanttSettings", Symbol.Setting, () => SettingsRequested?.Invoke(), true);
        Tool("タスクの詳細", "GanttTaskDetailsEdit", Symbol.Edit, () => { if (SelectedRowId is { } id) TaskDetailsRequested?.Invoke(id); }, true);
        Children.Add(commands);
        var filter = new Grid { ColumnSpacing = 8, Padding = new(8, 2, 8, 4) };
        filter.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); filter.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); filter.ColumnDefinitions.Add(new());
        filter.Children.Add(scale); SetColumn(search, 1); filter.Children.Add(search); SetColumn(summary, 2); filter.Children.Add(summary);
        AutomationProperties.SetAutomationId(scale, "GanttScale"); AutomationProperties.SetName(scale, "時間軸の日・週");
        AutomationProperties.SetAutomationId(search, "GanttSearch"); AutomationProperties.SetName(search, "タスク検索");
        AutomationProperties.SetAutomationId(summary, "GanttSummary");
        SetRow(filter, 1); Children.Add(filter);
        header.ColumnDefinitions.Add(new() { Width = new(identityWidth) }); header.ColumnDefinitions.Add(new());
        var label = new TextBlock { Text = "タスク / 採用日程（日本時間）", Margin = new(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(label); SetColumn(axisCanvas, 1); header.Children.Add(axisCanvas);
        SetRow(header, 2); Children.Add(header);
        list = new GanttList(this) { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new(0), SingleSelectionFollowsFocus = true, ItemTemplate = (DataTemplate)Application.Current.Resources["GanttRowTemplate"] };
        AutomationProperties.SetAutomationId(list, "GanttTasks"); AutomationProperties.SetName(list, "採用計画のタスク");
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Disabled); ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SelectionChanged += (_, _) => { if (!updating) { UpdateSelection(); Draw(); } };
        list.DoubleTapped += (_, _) => { if (SelectedRowId is { } id) EditRequested?.Invoke(id); };
        SetRow(list, 3); Children.Add(list); SetRow(lines, 3); Children.Add(lines);
        horizontal.Content = horizontalExtent; horizontal.Margin = new(identityWidth, 0, 16, 0);
        AutomationProperties.SetAutomationId(horizontal, "GanttHorizontal"); AutomationProperties.SetName(horizontal, "時間軸の横スクロール");
        horizontal.ViewChanged += (_, _) => Draw(); SetRow(horizontal, 4); Children.Add(horizontal);
        var selected = new Grid { ColumnSpacing = 12, Padding = new(12, 6, 12, 8) };
        selected.ColumnDefinitions.Add(new()); selected.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        selected.RowDefinitions.Add(new() { Height = GridLength.Auto }); selected.RowDefinitions.Add(new() { Height = GridLength.Auto });
        selected.RowDefinitions.Add(new() { Height = GridLength.Auto });
        selectedText.MaxLines = 3; selectedText.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetAutomationId(selectedText, "GanttSelected"); selected.Children.Add(selectedText);
        SetColumn(related, 1); SetRowSpan(related, 2); selected.Children.Add(related);
        AutomationProperties.SetAutomationId(related, "GanttRelated");
        related.SelectionChanged += (_, _) => { if (!updating && related.SelectedItem is Relation { RowId: { } id }) SelectRow(id, true); };
        SetRow(notice, 1); selected.Children.Add(notice); notice.MaxLines = 2; notice.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetAutomationId(notice, "GanttNotice");
        AutomationProperties.SetAutomationId(changedSchedule, "GanttChangedSchedule");
        changedSchedule.Click += (_, _) => RevealSelection(); SetRow(changedSchedule, 2); selected.Children.Add(changedSchedule);
        SetRow(selected, 5); Children.Add(selected);
        AutomationProperties.SetAutomationId(operationStatus, "GanttOperationStatus");
        SetRow(operationStatus, 6); Children.Add(operationStatus);
        scale.SelectionChanged += (_, _) => {
            if (updating) return;
            CancelPositionRestore();
            var day = horizontal.HorizontalOffset / axis.DayWidth;
            axis = GanttAxis.For(projection, scale.SelectedIndex == 1); horizontalExtent.Width = axis.Width;
            horizontal.ChangeView(day * axis.DayWidth, null, null, true); Draw();
        };
        search.TextChanged += (_, _) => { if (!updating) Filter(); };
        SizeChanged += (_, _) => {
            identityWidth = ActualWidth < 1000 ? 270 : 330;
            header.ColumnDefinitions[0].Width = new(identityWidth); horizontal.Margin = new(identityWidth, 0, 16, 0);
            related.MaxWidth = ActualWidth < 900 ? 230 : 430;
            Draw();
        };
        ActualThemeChanged += (_, _) => Draw();
        Loaded += (_, _) => {
            vertical = EditingGrid.Descendants(list).OfType<ScrollViewer>().FirstOrDefault();
            if (vertical is not null) vertical.ViewChanged += VerticalChanged;
            Draw();
        };
        Unloaded += (_, _) => { CancelPositionRestore(); if (vertical is not null) vertical.ViewChanged -= VerticalChanged; vertical = null; };
    }
    internal GanttViewPosition CapturePosition()
    {
        if (pendingPosition is { } pending) return pending;
        var offset = vertical?.VerticalOffset ?? 0;
        var index = (int)(offset / RowHeight);
        return new(axis.Origin.AddDays(horizontal.HorizontalOffset / axis.DayWidth), scale.SelectedIndex == 1,
            shown.ElementAtOrDefault(index)?.RowId, offset - index * RowHeight, offset);
    }
    internal void RestorePosition(GanttViewPosition position)
    {
        CancelPositionRestore();
        pendingPosition = position;
        updating = true; scale.SelectedIndex = position.Week ? 1 : 0; updating = false;
        axis = GanttAxis.For(projection, position.Week); horizontalExtent.Width = axis.Width;
        // A remounted Project has no usable native scroll geometry until layout.
        // Consume this return position once; later rendering must not undo panning.
        LayoutUpdated += RestorePositionAfterLayout;
        InvalidateArrange();
    }
    internal void CancelPositionRestore()
    {
        pendingPosition = null;
        LayoutUpdated -= RestorePositionAfterLayout;
    }
    private void RestorePositionAfterLayout(object? sender, object e)
    {
        if (pendingPosition is not { } position || !IsLoaded || Visibility != Visibility.Visible) return;
        if (vertical is null)
        {
            vertical = EditingGrid.Descendants(list).OfType<ScrollViewer>().FirstOrDefault();
            if (vertical is not null) vertical.ViewChanged += VerticalChanged;
        }
        if (horizontal.ViewportWidth <= 0 || vertical is not { ViewportHeight: > 0 }
            || Math.Abs(horizontalExtent.ActualWidth - axis.Width) > 1
            || shown.Length * RowHeight > vertical.ViewportHeight && vertical.ScrollableHeight <= 0) return;
        var index = Array.FindIndex(shown, row => row.RowId == position.TopRowId);
        var rowOffset = index >= 0 ? index * RowHeight + position.RowInset : position.VerticalOffset;
        CancelPositionRestore();
        horizontal.ChangeView(Math.Clamp(axis.Position(position.LeftDate), 0, horizontal.ScrollableWidth), null, null, true);
        vertical.ChangeView(null, Math.Clamp(rowOffset, 0, vertical.ScrollableHeight), null, true);
        Draw();
    }
    private void VerticalChanged(object? sender, ScrollViewerViewChangedEventArgs e) { DrawLinks(); UpdateChangedSchedule(); }
    internal void CycleFocus(bool backwards)
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        var targets = new Control[] { list, edit, context };
        var current = focused is DependencyObject element && EditingGrid.Descendants(list).Contains(element) ? 0 : Array.IndexOf(targets, focused);
        var next = (current + (backwards ? targets.Length - 1 : 1) + targets.Length) % targets.Length;
        for (var i = 0; i < targets.Length; i++, next = (next + 1) % targets.Length)
            if (targets[next].IsEnabled && targets[next].Focus(FocusState.Keyboard)) return;
    }
    internal void ShowOperationStatus(string? problem)
    {
        operationStatus.Message = problem;
        operationStatus.IsOpen = problem is not null;
    }
    internal void Present(GanttProjection value, string? selectedId = null)
    {
        var selected = selectedId ?? SelectedRowId;
        var oldOrigin = axis.Origin; var day = horizontal.HorizontalOffset / axis.DayWidth;
        var verticalOffset = vertical?.VerticalOffset;
        var selectedWasVisible = false;
        if (list.SelectedItem is { } previous && list.ContainerFromItem(previous) is ListViewItem { IsLoaded: true } item)
        {
            var bounds = item.TransformToVisual(list).TransformBounds(new(0, 0, item.ActualWidth, item.ActualHeight));
            selectedWasVisible = bounds.Bottom > 0 && bounds.Top < list.ActualHeight;
        }
        projection = value; warningPresentations.Clear(); axis = GanttAxis.For(value, scale.SelectedIndex == 1);
        calendar = value.Plan.Configuration is { } config ? new WorkingCalendar(config.Calendar) : null;
        horizontalExtent.Width = axis.Width;
        // A task explicitly selected in Boards must remain reachable on return,
        // even when the retained Gantt search excluded that task.
        if (selectedId is not null && value.Rows.FirstOrDefault(r => r.RowId == selectedId) is { } incoming && !MatchesSearch(incoming))
        { updating = true; search.Text = ""; updating = false; }
        Filter(selected);
        // Replacing adopted row records resets ListView's realized range. Restore
        // its viewport after layout so editing a distant task does not lose it.
        if (verticalOffset is { } offset) { list.UpdateLayout(); vertical!.ChangeView(null, offset, null, true); }
        if (selectedWasVisible && list.SelectedItem is { } retained) list.ScrollIntoView(retained);
        horizontal.ChangeView(Math.Max(0, (oldOrigin - axis.Origin).TotalDays + day) * axis.DayWidth, null, null, true);
        Draw();
    }
    private void Filter(string? selected = null)
    {
        selected ??= SelectedRowId;
        var next = projection.Rows.Where(MatchesSearch).ToArray();
        updating = true;
        // Save acknowledgement and editor close may present the same adopted
        // rows again. Replacing them would reset an in-flight viewport restore.
        if (!shown.SequenceEqual(next)) { shown = next; list.ItemsSource = shown; }
        list.SelectedItem = shown.FirstOrDefault(r => r.RowId == selected);
        updating = false;
        summary.Text = $"{shown.Length}/{projection.Rows.Length}件" + string.Concat(projection.Rows.Where(r => !r.HasBar)
            .GroupBy(r => r.State).OrderBy(g => g.Key).Select(g => $" · {g.First().StateText}{g.Count()}件"));
        UpdateSelection(); Draw();
    }
    private bool MatchesSearch(GanttRow row) => search.Text.Length == 0 || (row.Title + " " + row.Identity).Contains(search.Text, StringComparison.OrdinalIgnoreCase);
    internal void SelectRow(string id, bool revealTask)
    {
        var target = projection.Rows.FirstOrDefault(r => r.RowId == id); if (target is null) return;
        if (!shown.Contains(target)) { updating = true; search.Text = ""; updating = false; Filter(id); }
        list.SelectedItem = target;
        if (revealTask) RevealSelection();
    }
    private void RevealSelection()
    {
        CancelPositionRestore();
        if (list.SelectedItem is not GanttRow row) return;
        list.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
        if ((row.Plan?.Start ?? row.Plan?.Finish) is { } first)
            horizontal.ChangeView(Math.Max(0, axis.Position(first) - 32), null, null, true);
        list.Focus(FocusState.Programmatic);
        Draw();
    }
    private void UpdateSelection()
    {
        var row = list.SelectedItem as GanttRow;
        edit.IsEnabled = row?.Input is not null || row?.State == GanttState.Unplanned;
        dailyProgress.IsEnabled = !dailyProgressActive && row?.Input is not null;
        board.IsEnabled = reveal.IsEnabled = context.IsEnabled = row is not null;
        selectedText.Text = row is null ? "タスクを選ぶと、正確な日時と変更理由を確認できます。" : $"{row.Identity}  {row.Title}\n{row.StateText}  {Dates(row)}";
        var p = row?.Plan;
        var causes = row is null ? [] : WarningCauses(row);
        var warnings = causes.Where(IsScheduleWarning).ToArray();
        notice.Text = row is null ? (projection.Rows.Length == 0 ? "Projectにタスクがありません。" : "")
            : string.Join(" / ", new[] { p?.Mode == PlanningMode.Manual ? p.Resolved ? null
                : p.Start is null ? p.Finish is null ? "開始・終了日時が未設定です。" : "開始日時が未設定です。" : "終了日時が未設定です。"
                : Explain(p?.Problem), warnings.Length > 0 ? $"日程の注意 {warnings.Length}件（日程の理由で確認）" : null,
                causes.Any(cause => cause.Impact == PlanningWarningImpact.ActualReport) ? "実績の反映前に担当者・報告対象日を確認（日程の理由）" : null,
                causes.Any(cause => cause.Impact == PlanningWarningImpact.ContributionInconsistency) ? "工数内訳の合計を確認（日程の理由）" : null,
                row.HiddenOnBoards ? "表のフィルター外 · 表で開くとこの行を一時表示" : null }.Where(text => text is not null));
        var relations = row is null ? [] : Relations(row);
        updating = true; related.ItemsSource = relations; related.SelectedIndex = -1; updating = false;
        related.IsEnabled = relations.Length != 0;
        UpdateChangedSchedule();
    }
    internal void ShowChangedSchedule(string? rowId)
    {
        changedScheduleRow = rowId;
        UpdateChangedSchedule();
    }
    internal void SetDailyProgressActive(bool active)
    {
        dailyProgressActive = active;
        dailyProgress.IsEnabled = !active && list.SelectedItem is GanttRow { Input: not null };
    }
    private void UpdateChangedSchedule()
    {
        var row = list.SelectedItem as GanttRow;
        var outside = false;
        if (row is { State: GanttState.Scheduled or GanttState.Partial, Plan: { } plan }
            && (plan.Start ?? plan.Finish) is { } start && (plan.Finish ?? plan.Start) is { } finish)
        {
            var width = Math.Max(0, ActualWidth - identityWidth - 16);
            // A long interval can be inspected without fitting its entire span.
            // Keep the user's scale and viewpoint until they explicitly reveal it.
            outside = axis.Position(finish) < horizontal.HorizontalOffset || axis.Position(start) > horizontal.HorizontalOffset + width;
            if (list.ContainerFromItem(row) is ListViewItem item)
            {
                var bounds = item.TransformToVisual(list).TransformBounds(new(0, 0, item.ActualWidth, item.ActualHeight));
                outside |= bounds.Top < 0 || bounds.Bottom > list.ActualHeight;
            }
            else outside = true;
        }
        changedSchedule.Content = row?.RowId == changedScheduleRow ? "変更後の日程を見る" : "選択したタスクの日程を見る";
        changedSchedule.Visibility = outside ? Visibility.Visible : Visibility.Collapsed;
    }
    private Relation[] Relations(GanttRow row)
    {
        var byId = CanonicalRows();
        return (row.Input?.Predecessors ?? []).Select(link => {
            var previous = byId.GetValueOrDefault(link.PredecessorId);
            return new Relation($"先行 → [{link.Kind}] {previous?.Identity ?? "Project外"} {previous?.Title ?? "未確認のタスク"} / 終了 {Exact(previous?.Plan?.Finish ?? link.ExternalFinish)}", previous?.RowId);
        }).Concat(byId.Values.Where(r => r.Input?.Predecessors.Any(l => l.PredecessorId == row.TaskId) == true)
            .Select(r => new Relation($"→ 後続 {r.Identity} {r.Title}", r.RowId))).ToArray();
    }
    private Dictionary<string, GanttRow> CanonicalRows() => projection.Rows.GroupBy(r => r.TaskId)
        .Where(group => group.All(r => r.Input?.SourceProblem is null))
        .ToDictionary(group => group.Key, group => group.FirstOrDefault(r => r.RowId == SelectedRowId)
            ?? group.OrderBy(r => r.RowId, StringComparer.Ordinal).First());
    private GanttRow? WarningOrigin(string taskId)
    {
        var matches = projection.Rows.Where(row => row.TaskId == taskId).ToArray();
        // A source problem does not erase a uniquely identified visible task.
        // Ambiguous aliases cannot nominate a row for a correction action.
        return matches.Length == 1 ? matches[0] : null;
    }
    private void ShowDetails()
    {
        if (list.SelectedItem is not GanttRow row) return;
        var p = row.Plan; var input = row.Input; var config = projection.Plan.Configuration;
        var panel = new StackPanel { Spacing = 8 };
        void Text(string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        Text($"{row.Identity}  {row.Title}\n{row.StateText}: {Dates(row)}");
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(MaxWidthProperty, Math.Min(560, XamlRoot.Size.Width - 32)));
        presenter.Setters.Add(new Setter(MinWidthProperty, 0d));
        var flyout = new Flyout { FlyoutPresenterStyle = presenter, Content = new ScrollViewer { Content = panel,
            Width = Math.Min(520, XamlRoot.Size.Width - 80), MaxHeight = Math.Max(180, XamlRoot.Size.Height - 160),
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void Action(string text, string id, Action action)
        {
            var button = new Button { Content = text }; AutomationProperties.SetAutomationId(button, id);
            button.Click += (_, _) => {
                // Let the old popup finish restoring focus before opening the
                // correction surface; otherwise it can steal the new focus.
                void Closed(object? sender, object args) { flyout.Closed -= Closed; action(); }
                flyout.Closed += Closed; flyout.Hide();
            }; actions.Children.Add(button);
        }
        if (input?.Task.Progress == PlanningProgress.Unstarted && input.ActualTotal > 0)
            Action("計画上の進捗を確認", "GanttExplanationProgress", () => ProgressRequested?.Invoke(row.RowId));
        else Action("タスクの詳細", "GanttExplanationTask", () => TaskDetailsRequested?.Invoke(row.RowId));
        Action("計画の前提", "GanttExplanationSettings", () => SettingsRequested?.Invoke());
        if (input is not null && config is not null)
        {
            var owner = config.People.FirstOrDefault(o => o.Id == input.Task.OwnerId);
            var provisional = input.Task.Assignment is not { Legacy: false } && input.Task.OwnerId is null && input.Assignees.Length == 0;
            var ownerText = owner?.Name ?? (provisional ? "共通・暫定" : input.Task.OwnerId is null ? "担当者の選択が必要" : "以前の担当者（未確認）");
            var weightText = owner is not null ? owner.WeightPercent + "%" : provisional ? "100%" : "未確認";
            var progress = input.Task.Progress switch { PlanningProgress.Unstarted => "未着手", PlanningProgress.InProgress => "進行中", PlanningProgress.Completed => "完了", _ => "再開" };
            Text($"計画上の進捗: {progress}");
            string Hours(decimal? value) => value is { } hours ? PlanningContract.CanonicalHours(hours) + "人時" : "未入力";
            var remaining = input.Task.Progress is PlanningProgress.InProgress or PlanningProgress.Reopened;
            Text(p?.Mode == PlanningMode.Manual ? "日時を指定：指定した開始・終了を採用"
                : input.Task.Progress == PlanningProgress.Completed ? "完了：実績開始・終了日時を採用"
                : $"計算に使用：{(remaining ? "残時間 " + Hours(input.Remaining) : "見積 " + Hours(input.Estimate))}");
            if (input.Task.Progress == PlanningProgress.Unstarted && input.ActualTotal > 0)
                Text("実績がありますが、計画上の進捗は未着手です。実績の入力だけでは計画上の進捗を変更しません。");
            Text($"日程計算の担当: {ownerText} / 配賦: {weightText}");
            if (remaining) Text($"残作業の基準: {Exact(config.Cutoff)}（Project共通）");
            Text(p?.Mode == PlanningMode.Manual ? "指定した日時を採用しています。工数や前提の変更で上書きしません。"
                : input.Task.Progress == PlanningProgress.Completed ? "完了の実績開始・終了日時を採用しています。"
                : StartReason(p?.Controller, config));
            var calendarExplanation = PlanningCalendarExplanation.Create(projection.Plan, row.TaskId);
            if (calendarExplanation is { } explanation)
            {
                var details = new TextBlock { Text = CalendarExplanationText(explanation), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                AutomationProperties.SetAutomationId(details, "GanttCalendarExplanation"); panel.Children.Add(details);
            }
            panel.Children.Add(actions);
            if (p?.Problem is { } problem) Text((p.Mode == PlanningMode.Manual ? "自動計算の不足条件: " : "日程を決められない理由: ") + Explain(problem));
            var causes = WarningCauses(row);
            var breakdown = causes.Where(cause => cause.Impact == PlanningWarningImpact.EffortBreakdown).ToArray();
            var warnings = causes.Where(IsScheduleWarning).ToArray();
            var actualReports = causes.Where(cause => cause.Impact == PlanningWarningImpact.ActualReport).ToArray();
            var inconsistent = causes.Where(cause => cause.Impact == PlanningWarningImpact.ContributionInconsistency).ToArray();
            void WarningText(string heading, string id, PlanningWarningCause[] items, string suffix = "")
            {
                if (items.Length == 0) return;
                var text = new TextBlock { Text = heading + "\n" + string.Join("\n", items.GroupBy(cause => cause.SourceTaskId).Select(group => {
                    var source = WarningOrigin(group.Key);
                    var inherited = group.Any(cause => cause.Inherited);
                    var context = !inherited ? "" : $"先行 {source?.Identity ?? group.Key}"
                        + (source is { State: GanttState.Scheduled, Plan: { Resolved: true } prior } && prior.SourceRevision == projection.Plan.SourceRevision
                            ? $" · 採用終了 {Exact(prior.Finish)}" : " · 採用終了を確認できません") + "\n";
                    return context + string.Join("\n", group.Select(cause =>
                        (cause.Impact == PlanningWarningImpact.Unknown ? "原因を確認できません: " : "") + Explain(cause.Message)));
                })) + suffix, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                AutomationProperties.SetAutomationId(text, id); panel.Children.Add(text);
            }
            WarningText("日程の注意", "GanttScheduleWarnings", warnings);
            WarningText("実績をGitHubへ反映する前に", "GanttActualReportWarnings", actualReports,
                "\n実績・進捗で担当者と報告対象日を確認してください。日程計算の条件とは別です。");
            WarningText("工数内訳の合計を確認", "GanttContributionWarnings", inconsistent,
                "\nタスクの詳細で内訳を訂正してください。日程計算の担当・配賦とは別の記録です。");
            WarningText("担当者別集計の内訳（任意）", "GanttEffortWarnings", breakdown,
                "\n日程計算の担当・配賦とは別の記録です。未割当分を担当者へ自動配分しません。");
            foreach (var sourceId in causes.Where(cause => cause.Inherited).Select(cause => cause.SourceTaskId).Distinct())
            {
                if (WarningOrigin(sourceId) is not { } source) continue;
                var origin = new HyperlinkButton { Content = $"先行 {source.Identity} を確認" };
                AutomationProperties.SetAutomationId(origin, "GanttWarningOrigin-" + sourceId);
                origin.Click += (_, _) => {
                    void Closed(object? sender, object args) { flyout.Closed -= Closed; SelectRow(source.RowId, true); }
                    flyout.Closed += Closed; flyout.Hide();
                };
                panel.Children.Add(origin);
            }
            var records = new StackPanel { Spacing = 8 };
            void Record(string text, string? id = null)
            {
                var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                if (id is not null) AutomationProperties.SetAutomationId(block, id);
                records.Children.Add(block);
            }
            Record($"見積 {Hours(input.Estimate)} / 残時間 {Hours(input.Remaining)} / 実績 {Hours(input.ActualTotal)}");
            Record($"自動案: {Exact(p?.SuggestedStart)} → {Exact(p?.SuggestedFinish)}\nカレンダー: {config.Calendar.Revision}\n祝日: {config.Calendar.Holidays.Version} / {config.Calendar.Holidays.FirstYear}–{config.Calendar.Holidays.LastYear}\n採用祝日の出典: {config.Calendar.Holidays.Source}\nデータの検証値: {config.Calendar.Holidays.SourceSha256}" +
                (config.Calendar.HolidaysNotConsidered ? "（祝日を考慮しない）" : "") + $"\n採用計画リビジョン: {projection.Plan.SourceRevision}\n軸の網掛けはProject共通です。"
                + (calendarExplanation is null ? "\n稼働時間の採用根拠を確認できません。" : "担当者の稼働時間は上に表示しています。"), "GanttCalendarRecord");
            panel.Children.Add(new Expander { Header = "計算の記録", Content = records, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        else { Text("計画の前提から開始日時と使う列を設定し、タスクの日程を選んでください。"); panel.Children.Add(actions); }
        if (config?.Summary?.Baseline is { } baseline)
        {
            var captured = baseline.Tasks.SingleOrDefault(t => t.TaskId == row.TaskId);
            Text($"基準 {baseline.CapturedAt.LocalDateTime:g} / {baseline.Calendar.Revision}\n"
                + SummaryText.Comparison(new(captured, row, captured is null ? "基準なし（追加）" : "基準と現在")));
        }
        foreach (var relation in Relations(row))
        {
            if (relation.RowId is not { } id) { Text(relation.Label); continue; }
            var previous = projection.Rows.FirstOrDefault(r => r.RowId == id);
            var label = relation.Label + (previous is null ? "" : " · " + previous.StateText);
            if (previous?.Plan is { } prior && previous.State != GanttState.Unplanned && prior.Problem is not null)
                label += "\n関係先の状態：" + Explain(prior.Problem);
            var link = new HyperlinkButton { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(link, relation.Label);
            link.Click += (_, _) => { FlyoutBaseHide(); SelectRow(id, true); };
            panel.Children.Add(link);
        }
        AutomationProperties.SetAutomationId(panel, "GanttTaskDetails");
        context.Flyout = flyout; flyout.ShowAt(context);
        void FlyoutBaseHide() => flyout.Hide();
    }
    private PlanningWarningPresentation WarningsFor(GanttRow row)
    {
        if (row.Plan is null) return new(projection.Plan.SourceRevision, []);
        if (!warningPresentations.TryGetValue(row.TaskId, out var value))
            warningPresentations.Add(row.TaskId, value = PlanningWarningPresentation.Create(projection.Plan, row.TaskId));
        return value;
    }
    private PlanningWarningCause[] WarningCauses(GanttRow row) => WarningsFor(row).Causes
        .Where(cause => cause.Inherited || cause.Message != row.Plan?.Problem).ToArray();
    private static bool IsScheduleWarning(PlanningWarningCause cause)
        => cause.Impact is PlanningWarningImpact.Schedule or PlanningWarningImpact.Unknown;
    private string AttentionLabel(GanttRow row)
    {
        var causes = WarningCauses(row);
        return string.Concat(new[] {
            causes.Any(IsScheduleWarning) ? " · 注意" : null,
            causes.Any(cause => cause.Impact == PlanningWarningImpact.ActualReport) ? " · 実績を確認" : null,
            causes.Any(cause => cause.Impact == PlanningWarningImpact.ContributionInconsistency) ? " · 内訳を確認" : null
        });
    }
    private string CalendarExplanationText(PlanningCalendarExplanation explanation)
    {
        var lines = new List<string> { $"採用計画の稼働時間: {explanation.PersonName}" };
        for (var index = 0; index < explanation.Days.Length; index++)
        {
            var day = explanation.Days[index];
            var last = day.Date;
            while (index + 1 < explanation.Days.Length && explanation.Days[index + 1] is var next
                && next.Date.DayNumber == last.DayNumber + 1 && next.Source == day.Source
                && next.HolidayName == day.HolidayName && next.Intervals.SequenceEqual(day.Intervals))
            { last = next.Date; index++; }
            var source = day.Source switch { WorkingDaySource.PersonalException => "個人例外", WorkingDaySource.ProjectException => "Project例外",
                WorkingDaySource.Weekend => "週末", WorkingDaySource.Holiday => "祝日 · " + (string.IsNullOrWhiteSpace(day.HolidayName) ? "名称未確認" : day.HolidayName), _ => "通常" };
            var intervals = day.Intervals.Length == 0 ? "稼働なし" : string.Join(" / ", day.Intervals.Select(i =>
                $"{i.StartMinute / 60:00}:{i.StartMinute % 60:00}–{i.EndMinute / 60:00}:{i.EndMinute % 60:00}"));
            var range = last == day.Date ? $"{day.Date:yyyy-MM-dd}" : $"{day.Date:yyyy-MM-dd}–{last:MM-dd}";
            lines.Add($"{range} · {source}: {intervals}");
        }
        if (explanation.Start is { } start)
        {
            var boundary = start.PredecessorId is { } predecessor
                ? $"先行 {CanonicalRows().GetValueOrDefault(predecessor)?.Identity ?? predecessor} の終了 {Exact(start.Boundary)}"
                : $"開始基準 {Exact(start.Boundary)}";
            lines.Add(start.NoRemainingInterval ? $"{boundary} 以降、この日に残る稼働区間がありません。"
                : $"{boundary} 以降の稼働区間に合わせています。");
            lines.Add($"次の稼働開始 {Exact(start.AdoptedStart)} を採用しています。");
        }
        return string.Join("\n", lines);
    }
    private string StartReason(string? controller, ProjectPlanning config)
    {
        // These controller values mean no stronger task or predecessor constraint
        // replaced the engine's initial anchor. Compare those inputs, not output dates.
        var anchor = controller switch {
            "見積工数・配賦・カレンダー" => "Project開始 " + Exact(config.Start),
            "残工数・基準日時・配賦・カレンダー" when config.Cutoff > config.Start => "再計画の基準日時 " + Exact(config.Cutoff),
            "残工数・基準日時・配賦・カレンダー" when config.Cutoff == config.Start => "Project開始・再計画の基準日時 " + Exact(config.Start),
            "残工数・基準日時・配賦・カレンダー" => "Project開始 " + Exact(config.Start),
            null => "未確認",
            _ => Explain(controller)
        };
        return "開始基準：" + anchor + "\n稼働カレンダーに合わせて配置します。";
    }
    private string? Explain(string? text)
    {
        if (text is null) return null;
        foreach (var row in projection.Rows.DistinctBy(r => r.TaskId).OrderByDescending(r => r.TaskId.Length))
            text = text.Replace("先行 " + row.TaskId, "先行 " + row.Identity, StringComparison.Ordinal);
        return text.Replace("Auto または Manual", "自動計算または日時を指定", StringComparison.Ordinal).Replace("Manual日時", "指定日時", StringComparison.Ordinal);
    }
    private void Draw()
    {
        if (ActualWidth <= 0) return;
        var width = Math.Max(0, ActualWidth - identityWidth - 16);
        axisCanvas.Clip = new RectangleGeometry { Rect = new(0, 0, width, 44) };
        axisCanvas.Children.Clear();
        foreach (var day in VisibleDays(width))
        {
            var x = day * axis.DayWidth - horizontal.HorizontalOffset;
            var date = axis.Origin.AddDays(day);
            var marker = new TextBlock { Text = scale.SelectedIndex == 0 ? date.ToString("M/d ddd", CultureInfo.GetCultureInfo("ja-JP")) : date.DayOfWeek == DayOfWeek.Monday || day == 0 ? date.ToString("M/d") : "",
                Margin = new(3, 0, 0, 0) };
            Canvas.SetLeft(marker, x); Canvas.SetTop(marker, 4); axisCanvas.Children.Add(marker);
            var state = DayState(date);
            if (state != "") { var note = new TextBlock { Text = state, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] }; Canvas.SetLeft(note, x + 3); Canvas.SetTop(note, 23); axisCanvas.Children.Add(note); }
        }
        foreach (var line in realized.ToArray()) line.Draw();
        DrawLinks();
        UpdateChangedSchedule();
    }
    private IEnumerable<int> VisibleDays(double width)
    {
        var first = Math.Max(0, (int)(horizontal.HorizontalOffset / axis.DayWidth));
        return Enumerable.Range(first, Math.Max(0, Math.Min(axis.Days - first, (int)(width / axis.DayWidth) + 2)));
    }
    private string DayState(DateTime date)
    {
        if (calendar is null) return "未設定";
        try { return calendar.Intervals(DateOnly.FromDateTime(date), null).Length == 0 ? "休" : ""; }
        catch (InvalidOperationException) { return "?"; }
    }
    private void DrawLinks()
    {
        lines.Children.Clear(); lines.Clip = new RectangleGeometry { Rect = new(identityWidth, 0, Math.Max(0, ActualWidth - identityWidth - 16), Math.Max(0, list.ActualHeight)) };
        if (list.SelectedItem is not GanttRow selected) return;
        var byTask = CanonicalRows();
        var selectedEdges = byTask.Values.SelectMany(r => (r.Input?.Predecessors ?? []).Where(l => l.Kind == "FS" && (r.TaskId == selected.TaskId || l.PredecessorId == selected.TaskId)).Select(l => (From: byTask.GetValueOrDefault(l.PredecessorId), To: r)))
            .DistinctBy(edge => (edge.From?.TaskId, edge.To.TaskId));
        foreach (var (from, to) in selectedEdges)
        {
            if (from?.Plan?.Finish is not { } finish || to.Plan?.Start is not { } start || !from.HasBar || !to.HasBar) continue;
            var fromIndex = Array.IndexOf(shown, from); var toIndex = Array.IndexOf(shown, to);
            if (fromIndex < 0 || toIndex < 0) continue;
            var offset = vertical?.VerticalOffset ?? 0;
            var y1 = fromIndex * 52 + 26 - offset; var y2 = toIndex * 52 + 26 - offset;
            if (Math.Max(y1, y2) < 0 || Math.Min(y1, y2) > list.ActualHeight) continue;
            var x1 = identityWidth + axis.Position(finish) - horizontal.HorizontalOffset;
            var x2 = identityWidth + axis.Position(start) - horizontal.HorizontalOffset;
            var bend = Math.Max(x1 + 10, x2 - 10);
            var path = new Polyline { Style = (Style)Application.Current.Resources["GanttLinkStyle"], Points = new() { new(x1, y1), new(bend, y1), new(bend, y2), new(x2, y2) } };
            AutomationProperties.SetAutomationId(path, $"GanttLink-{from.TaskId}-{to.TaskId}");
            lines.Children.Add(path);
            var arrow = new Polyline { Style = (Style)Application.Current.Resources["GanttLinkStyle"], StrokeThickness = 1.5,
                Points = new() { new(x2 + (bend < x2 ? -5 : 5), y2 - 4), new(x2, y2), new(x2 + (bend < x2 ? -5 : 5), y2 + 4) } };
            lines.Children.Add(arrow);
        }
    }
    private static string Exact(DateTime? date) => date?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "未設定";
    private static string Dates(GanttRow row) => $"{Exact(row.Plan?.Start)} → {Exact(row.Plan?.Finish)}";
    internal FrameworkElement CreateRow(GanttRow row) => new GanttLine(this, row);
    private sealed class GanttList(GanttView owner) : ListView
    {
        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            var container = (ListViewItem)element; var row = (GanttRow)item;
            container.Padding = new(0); container.Margin = new(0); container.MinHeight = container.Height = RowHeight;
            container.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetAutomationId(container, "GanttRow-" + row.RowId);
            AutomationProperties.SetName(container, $"{row.Identity} {row.Title} {row.StateText}{owner.AttentionLabel(row)} {Dates(row)}");
        }
    }
    private sealed class GanttLine : Grid
    {
        private readonly GanttView owner;
        private readonly GanttRow row;
        private readonly Canvas canvas = new();
        internal GanttLine(GanttView owner, GanttRow row)
        {
            this.owner = owner; this.row = row; Height = 52;
            ColumnDefinitions.Add(new() { Width = new(owner.identityWidth) }); ColumnDefinitions.Add(new());
            var identity = new StackPanel { Margin = new(12, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
            identity.Children.Add(new TextBlock { Text = $"{row.Identity}  {row.Title}", TextTrimming = TextTrimming.CharacterEllipsis });
            var state = new TextBlock { Text = row.StateText + owner.AttentionLabel(row) + "  " +
                (row.HasBar ? $"{row.Plan!.Start:M/d HH:mm} → {row.Plan.Finish:M/d HH:mm}" : "日程を確認") + (row.HiddenOnBoards ? " · 表の範囲外" : ""),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis };
            AutomationProperties.SetAutomationId(state, "GanttState-" + row.RowId); identity.Children.Add(state);
            Children.Add(identity); SetColumn(canvas, 1); Children.Add(canvas);
            Loaded += (_, _) => { owner.realized.Add(this); Draw(); };
            Unloaded += (_, _) => owner.realized.Remove(this);
            SizeChanged += (_, _) => Draw();
        }
        internal void Draw()
        {
            ColumnDefinitions[0].Width = new(owner.identityWidth);
            var width = Math.Max(0, ActualWidth - owner.identityWidth);
            canvas.Clip = new RectangleGeometry { Rect = new(0, 0, width, 52) }; canvas.Children.Clear();
            foreach (var day in owner.VisibleDays(width))
            {
                var x = day * owner.axis.DayWidth - owner.horizontal.HorizontalOffset;
                var state = owner.DayState(owner.axis.Origin.AddDays(day));
                var band = new Rectangle { Width = owner.axis.DayWidth, Height = 52,
                    Style = (Style)Application.Current.Resources[state == "" ? "GanttWorkingDayStyle" : "GanttRestDayStyle"] };
                AutomationProperties.SetAutomationId(band, $"GanttDay-{row.RowId}-{owner.axis.Origin.AddDays(day):yyyyMMdd}");
                Canvas.SetLeft(band, x); canvas.Children.Add(band);
            }
            if (row.HasBar)
            {
                var x = owner.axis.Position(row.Plan!.Start!.Value) - owner.horizontal.HorizontalOffset;
                var finish = owner.axis.Position(row.Plan.Finish!.Value) - owner.horizontal.HorizontalOffset;
                // The row is the hit target. Never inflate the time interval to make a short bar clickable.
                var bar = new Rectangle { Width = Math.Max(0, finish - x), Height = 16,
                    Style = (Style)Application.Current.Resources[row.Plan.Mode == PlanningMode.Manual ? "GanttManualBarStyle" : "GanttAutoBarStyle"] };
                AutomationProperties.SetAutomationId(bar, "GanttBar-" + row.RowId);
                Canvas.SetLeft(bar, x); Canvas.SetTop(bar, 18); canvas.Children.Add(bar);
                if (finish - x < 1) { var tick = new Line { X1 = x, X2 = x, Y1 = 17, Y2 = 35, Style = (Style)Application.Current.Resources["GanttEndpointStyle"] }; canvas.Children.Add(tick); }
            }
            else if (row.State == GanttState.Partial)
            {
                var x = owner.axis.Position((row.Plan!.Start ?? row.Plan.Finish)!.Value) - owner.horizontal.HorizontalOffset;
                canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 14, Y2 = 38, Style = (Style)Application.Current.Resources["GanttEndpointStyle"] });
            }
            else
            {
                var problem = row.Plan?.Problem ?? "";
                var predecessor = (row.Input?.Predecessors ?? []).Select(link => owner.projection.Rows.FirstOrDefault(r => r.TaskId == link.PredecessorId))
                    .FirstOrDefault(r => r is not null && r.Plan?.Resolved != true && r.Input?.SatisfiesPrecedence != true);
                var label = predecessor is not null ? "先行待ち" : problem.Contains("見積") ? "見積未入力"
                    : problem.Contains("残工数") ? "残時間未入力" : problem.Contains("配賦") ? "稼働率を設定"
                    : problem.Contains("Project開始") ? "開始日を設定" : row.State == GanttState.Unplanned ? "計画する" : "日程を確認";
                var action = new Button { Content = "⚠ " + label, Margin = new(8, 10, 0, 0), MinHeight = 30, Padding = new(8, 2, 8, 2) };
                AutomationProperties.SetAutomationId(action, "GanttResolve-" + row.RowId);
                AutomationProperties.SetName(action, row.Identity + " " + label);
                ToolTipService.SetToolTip(action, predecessor is not null ? predecessor.Identity + " " + predecessor.Title : problem);
                action.Click += (_, _) => {
                    if (predecessor is not null) owner.SelectRow(predecessor.RowId, true);
                    else if (problem.Contains("Project開始") || problem.Contains("配賦") || owner.projection.Plan.Configuration is null) owner.SettingsRequested?.Invoke();
                    else if (problem.Contains("見積") || problem.Contains("残工数")) owner.BoardsRequested?.Invoke(row.RowId);
                    else owner.TaskDetailsRequested?.Invoke(row.RowId);
                };
                canvas.Children.Add(action);
            }
        }
    }
}

public sealed class GanttRowPresenter : ContentControl
{
    public GanttRowPresenter()
    {
        IsTabStop = false;
        Loaded += (_, _) => Present(); DataContextChanged += (_, _) => { if (IsLoaded) Present(); };
    }
    private void Present()
    {
        if (DataContext is not GanttRow row) { Content = null; return; }
        DependencyObject? ancestor = this;
        while (ancestor is not null && ancestor is not GanttView) ancestor = VisualTreeHelper.GetParent(ancestor);
        if (ancestor is GanttView owner) Content = owner.CreateRow(row);
    }
}
