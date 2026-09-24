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

// Owns viewport geometry only. Scheduling, edits and persistence stay in the shared workspace.
internal sealed class GanttView : Grid
{
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
    private readonly ComboBox related = new() { MinWidth = 170, MaxWidth = 430, HorizontalAlignment = HorizontalAlignment.Stretch,
        Header = "先行 → 選択 → 後続", DisplayMemberPath = nameof(Relation.Label), PlaceholderText = "関係を選択" };
    private readonly TextBox search = new() { PlaceholderText = "タイトル・番号を検索", Width = 200 };
    private readonly HashSet<GanttLine> realized = [];
    private readonly Button edit;
    private readonly Button board;
    private readonly Button reveal;
    private readonly Button context;
    private readonly ComboBox scale = new() { Width = 76, ItemsSource = new[] { "日", "週" }, SelectedIndex = 0 };
    private GanttProjection projection = new([], new("", 0, []));
    private GanttAxis axis = new(new(2026, 1, 1), 28, 96);
    private GanttRow[] shown = [];
    private bool updating;
    private double identityWidth = 330;
    private ScrollViewer? vertical;
    private WorkingCalendar? calendar;
    internal event Action<string>? EditRequested;
    internal event Action<string>? TaskDetailsRequested;
    internal event Action<string>? BoardsRequested;
    internal event Action? UndoRequested;
    internal event Action? SettingsRequested;
    internal event Action? SaveRequested;
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
        list = new GanttList { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch,
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
        selectedText.MaxLines = 3; selectedText.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetAutomationId(selectedText, "GanttSelected"); selected.Children.Add(selectedText);
        SetColumn(related, 1); SetRowSpan(related, 2); selected.Children.Add(related);
        AutomationProperties.SetAutomationId(related, "GanttRelated");
        related.SelectionChanged += (_, _) => { if (!updating && related.SelectedItem is Relation { RowId: { } id }) SelectRow(id, true); };
        SetRow(notice, 1); selected.Children.Add(notice); notice.MaxLines = 2; notice.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetAutomationId(notice, "GanttNotice");
        SetRow(selected, 5); Children.Add(selected);
        AutomationProperties.SetAutomationId(operationStatus, "GanttOperationStatus");
        var retry = new Button { Content = "保存を再試行" }; AutomationProperties.SetAutomationId(retry, "GanttRetrySave");
        retry.Click += (_, _) => SaveRequested?.Invoke(); operationStatus.ActionButton = retry;
        SetRow(operationStatus, 6); Children.Add(operationStatus);
        scale.SelectionChanged += (_, _) => {
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
        Unloaded += (_, _) => { if (vertical is not null) vertical.ViewChanged -= VerticalChanged; vertical = null; };
    }
    private void VerticalChanged(object? sender, ScrollViewerViewChangedEventArgs e) => DrawLinks();
    internal void CycleFocus(bool backwards)
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        var targets = new Control[] { list, edit, context };
        var current = focused is DependencyObject element && EditingGrid.Descendants(list).Contains(element) ? 0 : Array.IndexOf(targets, focused);
        var next = (current + (backwards ? targets.Length - 1 : 1) + targets.Length) % targets.Length;
        for (var i = 0; i < targets.Length; i++, next = (next + 1) % targets.Length)
            if (targets[next].IsEnabled && targets[next].Focus(FocusState.Keyboard)) return;
    }
    internal void ShowOperationStatus(string? problem, string saveStatus)
    {
        var saveFailed = saveStatus.Contains("失敗", StringComparison.Ordinal);
        operationStatus.Message = (saveFailed ? saveStatus + " " : "") + problem;
        operationStatus.IsOpen = saveFailed || problem is not null;
        operationStatus.ActionButton.Visibility = saveFailed ? Visibility.Visible : Visibility.Collapsed;
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
        projection = value; axis = GanttAxis.For(value, scale.SelectedIndex == 1);
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
        board.IsEnabled = reveal.IsEnabled = context.IsEnabled = row is not null;
        selectedText.Text = row is null ? "タスクを選ぶと、正確な日時と変更理由を確認できます。" : $"{row.Identity}  {row.Title}\n{row.StateText}  {Dates(row)}";
        var p = row?.Plan;
        var warnings = p?.Warnings.Where(w => !IsEffortBreakdown(w)).ToArray() ?? [];
        notice.Text = row is null ? (projection.Rows.Length == 0 ? "Projectにタスクがありません。" : "")
            : string.Join(" / ", new[] { p?.Mode == PlanningMode.Manual ? p.Resolved ? null
                : p.Start is null ? p.Finish is null ? "開始・終了日時が未設定です。" : "開始日時が未設定です。" : "終了日時が未設定です。"
                : Explain(p?.Problem), warnings.Length > 0 ? $"日程の注意 {warnings.Length}件（日程の理由で確認）" : null,
                p?.Warnings.Any(IsEffortBreakdown) == true ? "担当者別集計の内訳に不足（日程の理由で確認）" : null,
                row.HiddenOnBoards ? "表のフィルター外 · 表で開くとこの行を一時表示" : null }.Where(text => text is not null));
        var relations = row is null ? [] : Relations(row);
        updating = true; related.ItemsSource = relations; related.SelectedIndex = -1; updating = false;
        related.IsEnabled = relations.Length != 0;
    }
    private Relation[] Relations(GanttRow row)
    {
        var byId = CanonicalRows();
        return (row.Input?.Predecessors ?? []).Select(link => {
            var previous = byId.GetValueOrDefault(link.PredecessorId);
            return new Relation($"先行 → [{link.Kind}] {previous?.Identity ?? link.PredecessorId} {previous?.Title ?? "外部・未確認"} / 終了 {Exact(previous?.Plan?.Finish ?? link.ExternalFinish)}", previous?.RowId);
        }).Concat(byId.Values.Where(r => r.Input?.Predecessors.Any(l => l.PredecessorId == row.TaskId) == true)
            .Select(r => new Relation($"→ 後続 {r.Identity} {r.Title}", r.RowId))).ToArray();
    }
    private Dictionary<string, GanttRow> CanonicalRows() => projection.Rows.GroupBy(r => r.TaskId)
        .Where(group => group.All(r => r.Input?.SourceProblem is null))
        .ToDictionary(group => group.Key, group => group.FirstOrDefault(r => r.RowId == SelectedRowId)
            ?? group.OrderBy(r => r.RowId, StringComparer.Ordinal).First());
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
            button.Click += (_, _) => { flyout.Hide(); action(); }; actions.Children.Add(button);
        }
        Action("見積・進捗を編集", "GanttExplanationTask", () => TaskDetailsRequested?.Invoke(row.RowId));
        Action("計画の前提", "GanttExplanationSettings", () => SettingsRequested?.Invoke());
        if (input is not null && config is not null)
        {
            var owner = config.People.FirstOrDefault(o => o.Id == input.Task.OwnerId);
            var provisional = input.Task.Assignment is not { Legacy: false } && input.Task.OwnerId is null && input.Assignees.Length == 0;
            var ownerText = owner?.Name ?? (provisional ? "共通・暫定" : input.Task.OwnerId is null ? "担当者の選択が必要" : input.Task.OwnerId + "（未確認）");
            var weightText = owner is not null ? owner.WeightPercent + "%" : provisional ? "100%" : "未確認";
            var progress = input.Task.Progress switch { PlanningProgress.Unstarted => "未着手", PlanningProgress.InProgress => "進行中", PlanningProgress.Completed => "完了", _ => "再開" };
            string Hours(decimal? value) => value is { } hours ? PlanningContract.CanonicalHours(hours) + "人時" : "未入力";
            var remaining = input.Task.Progress is PlanningProgress.InProgress or PlanningProgress.Reopened;
            Text(input.Task.Progress == PlanningProgress.Completed ? "完了：実績開始・終了日時を採用"
                : $"計算に使用：{(remaining ? "残時間 " + Hours(input.Remaining) : "見積 " + Hours(input.Estimate))}（{progress}）");
            if (input.Task.Progress == PlanningProgress.Unstarted && input.ActualTotal > 0)
                Text("実績がありますが、進捗は未着手です。実績の入力だけでは進捗を変更しません。進捗を確認してください。");
            Text($"日程計算の担当: {ownerText} / 配賦: {weightText}");
            if (remaining) Text($"残作業の基準: {Exact(config.Cutoff)}（Project共通）");
            Text(p?.Mode == PlanningMode.Manual ? "指定した日時を採用しています。工数や前提の変更で上書きしません。"
                : StartReason(p?.Controller, config));
            panel.Children.Add(actions);
            if (p?.Problem is { } problem) Text((p.Mode == PlanningMode.Manual ? "自動計算の不足条件: " : "日程を決められない理由: ") + Explain(problem));
            var breakdown = (p?.Warnings ?? []).Where(IsEffortBreakdown).ToArray();
            var warnings = (p?.Warnings ?? []).Except(breakdown).ToArray();
            if (warnings.Length > 0) Text("日程の注意\n" + string.Join("\n", warnings.Select(Explain)));
            if (breakdown.Length > 0) Text("担当者別集計の内訳\n" + string.Join("\n", breakdown));
            var records = new StackPanel { Spacing = 8 };
            void Record(string text) => records.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            Record($"見積 {Hours(input.Estimate)} / 残時間 {Hours(input.Remaining)} / 実績 {Hours(input.ActualTotal)}");
            Record($"自動案: {Exact(p?.SuggestedStart)} → {Exact(p?.SuggestedFinish)}\nタスクID: {row.TaskId}\nカレンダー: {config.Calendar.Revision}\n祝日: {config.Calendar.Holidays.Version} / {config.Calendar.Holidays.FirstYear}–{config.Calendar.Holidays.LastYear}" +
                (config.Calendar.HolidaysNotConsidered ? "（祝日を考慮しない）" : "") + "\n軸の網掛けはProject共通。個人例外は下記の採用区間に従います。");
            var calendar = new WorkingCalendar(config.Calendar);
            foreach (var date in new[] { p?.Start, p?.Finish }.Where(d => d.HasValue).Select(d => DateOnly.FromDateTime(d!.Value)).Distinct())
            {
                try { Record($"{date:yyyy-MM-dd}: " + string.Join(" / ", calendar.Intervals(date, input.Task.OwnerId).Select(i => $"{i.StartMinute / 60:00}:{i.StartMinute % 60:00}–{i.EndMinute / 60:00}:{i.EndMinute % 60:00}"))); }
                catch (InvalidOperationException e) { Record(e.Message); }
            }
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
    private static bool IsEffortBreakdown(string warning) => warning.StartsWith("見積の未割当", StringComparison.Ordinal)
        || warning.StartsWith("残時間の未割当", StringComparison.Ordinal) || warning.Contains("内訳", StringComparison.Ordinal);
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
    private sealed class GanttList : ListView
    {
        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            var container = (ListViewItem)element; var row = (GanttRow)item;
            container.Padding = new(0); container.Margin = new(0); container.MinHeight = container.Height = 52;
            container.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetAutomationId(container, "GanttRow-" + row.RowId);
            AutomationProperties.SetName(container, $"{row.Identity} {row.Title} {row.StateText} {Dates(row)}");
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
            identity.Children.Add(new TextBlock { Text = row.StateText + (row.Plan?.Warnings.Any(w => !IsEffortBreakdown(w)) == true ? " · 注意" : "") + "  " +
                (row.HasBar ? $"{row.Plan!.Start:M/d HH:mm} → {row.Plan.Finish:M/d HH:mm}" : "日程を確認") + (row.HiddenOnBoards ? " · 表の範囲外" : ""),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
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
                canvas.Children.Add(new TextBlock { Text = row.StateText, Margin = new(8, 16, 0, 0), Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
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
