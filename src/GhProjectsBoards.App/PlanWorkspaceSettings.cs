using System.Collections.Immutable;
using System.Globalization;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace GhProjectsBoards.App;

// #251: one settings page with a fixed section list grouped by scope and one readable content column that
// scrolls on its own. A section chosen in the list scrolls the content to its heading.
internal sealed partial class PlanWorkspaceView
{
    private readonly NavigationView settingsNav = Id(new NavigationView {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Left, IsPaneToggleButtonVisible = false, IsSettingsVisible = false,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsTitleBarAutoPaddingEnabled = false,
        OpenPaneLength = 260, SelectionFollowsFocus = NavigationViewSelectionFollowsFocus.Disabled }, "PlanSettingsSections");
    private readonly List<(NavigationViewItem Item, FrameworkElement Heading)> settingsSections = [];
    private ListView? columnList;

    private void InitializeSettingsPage()
    {
        var header = new StackPanel { Spacing = 12, Margin = new(16, 12, 16, 8) };
        header.Children.Add(Id(new TextBlock { Text = "設定", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, "PlanSettingsTitle"));
        settingsBack.HorizontalAlignment = HorizontalAlignment.Stretch;
        header.Children.Add(settingsBack);
        settingsNav.PaneHeader = header;
        settingsNav.Content = settingsScroll;
        AutomationProperties.SetName(settingsNav, "設定の項目");
        settingsNav.ItemInvoked += (_, args) => {
            if (args.InvokedItemContainer is NavigationViewItem item && settingsSections.FirstOrDefault(s => s.Item == item).Heading is { } heading)
                ScrollToSettingsHeading(heading);
        };
        settingsArea.Children.Add(settingsNav);
    }

    // Opens a section from elsewhere in the app (表示列, the assist pane's 設定を開く).
    private void ShowSettingsSection(string key)
    {
        if (settingsSections.FirstOrDefault(s => (string)s.Item.Tag == key) is { Heading: { } heading }) ScrollToSettingsHeading(heading);
    }

    private void ScrollToSettingsHeading(FrameworkElement heading)
    {
        settingsScroll.UpdateLayout();
        var top = heading.TransformToVisual(settings).TransformPoint(new()).Y + settings.Margin.Top;
        settingsScroll.ChangeView(null, Math.Clamp(top - 8, 0, settingsScroll.ScrollableHeight), null, true);
        settingsNav.SelectedItem = settingsSections.First(s => s.Heading == heading).Item;
    }

    // The list marks the section whose heading is at or above the top of the viewport.
    private void MarkSettingsSectionInView()
    {
        if (settingsSections.Count == 0 || !settings.IsLoaded) return;
        var current = settingsSections[0].Item;
        foreach (var (item, heading) in settingsSections)
            if (heading.IsLoaded && heading.TransformToVisual(settingsScroll).TransformPoint(new()).Y <= 24) current = item;
        if (settingsScroll.VerticalOffset >= settingsScroll.ScrollableHeight - 1 && settingsScroll.ScrollableHeight > 0
            && settingsNav.SelectedItem is NavigationViewItem chosen && settingsSections.Any(s => s.Item == chosen)) return;
        if (!ReferenceEquals(settingsNav.SelectedItem, current)) settingsNav.SelectedItem = current;
    }

    private void SettingsGroup(string title, string id)
    {
        settingsNav.MenuItems.Add(Id(new NavigationViewItemHeader { Content = title }, "PlanSettingsGroup_" + id));
        settings.Children.Add(Id(new TextBlock { Text = title, Margin = new(0, settings.Children.Count == 0 ? 0 : 24, 0, -8),
            Style = (Style)Application.Current.Resources["WorkspaceMetadataStyle"] }, "PlanSettingsGroupCaption_" + id));
    }

    private StackPanel SettingsSection(string key, string title, string? headingId = null) => SettingsSection(key, title, headingId, out _);
    private StackPanel SettingsSection(string key, string title, string? headingId, out FrameworkElement heading)
    {
        var item = Id(new NavigationViewItem { Content = title, Tag = key }, "PlanSettingsSection_" + key);
        AutomationProperties.SetName(item, title);
        settingsNav.MenuItems.Add(item);
        var text = Id(new TextBlock { Text = title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new(0, 12, 0, 0) },
            headingId ?? "PlanSettingsHeading_" + key);
        heading = text;
        settings.Children.Add(text);
        var content = new StackPanel { Spacing = 12 };
        settings.Children.Add(content);
        settingsSections.Add((item, text));
        return content;
    }

    private void RenderSettings()
    {
        if (workspace.Session is not { } session) return;
        rendering = true;
        try
        {
            var generation = ++settingsGeneration; pendingSettings.Clear(); settingsTextInputs.Clear(); ClearSettingsInputProblem(); settings.Children.Clear();
            var selectedKey = (settingsNav.SelectedItem as NavigationViewItem)?.Tag as string;
            settingsNav.MenuItems.Clear(); settingsSections.Clear();
            var value = session.Document.State.Settings;

            SettingsGroup(workspace.Selected?.Title ?? "このプロジェクト", "project");
            RenderPeriodSettings(SettingsSection("period", "期間"), session, generation);
            var phases = SettingsSection("phases", "工程と目標日");
            var people = SettingsSection("people", "担当者");
            RenderPeopleSettings(people, session, generation);
            RenderCalendarSettings(SettingsSection("calendar", "カレンダー"), session);
            RenderColumnSettings(SettingsSection("columns", "列", "PlanColumnSettings"), session, generation);
            var fields = SettingsSection("fields", "GitHub のフィールド");
            ComboBox? phaseMap = null;
            foreach (var role in PlanColumnMatching.Roles)
            {
                // #258: an organization Issue Field is named as one, so it stays distinguishable from a Project
                // field with the same name.
                var choices = new[] { new PlanColumnDefinition("", "未設定", role.Type) }.Concat(session.Document.Baseline.Columns.Where(c => c.DataType == role.Type))
                    .Select(c => new PlanFieldChoice(c, c.IssueField ? c.Name + PlanSheetColumns.IssueFieldSuffix : c.Name)).ToArray();
                var combo = Id(new ComboBox { Header = PlanSheetView.FullName(PlanSheetView.Columns.Single(c => c.Field == role.Role)), ItemsSource = choices, DisplayMemberPath = "Label", Width = 280 }, "PlanMap" + role.Role);
                AutomationProperties.SetName(combo, (string)combo.Header);
                combo.SelectedItem = choices.FirstOrDefault(c => c.Definition.Id == value.Columns.SingleOrDefault(m => m.Role == role.Role)?.FieldId) ?? choices[0];
                combo.SelectionChanged += async (_, _) => {
                    if (rendering || generation != settingsGeneration || combo.SelectedItem is not PlanFieldChoice { Definition: var selected }) return;
                    await Run(async () => {
                        await ChangeSettings(s => s with { Columns = s.Columns.Where(c => c.Role != role.Role)
                            .Concat(selected.Id.Length == 0 ? [] : new[] { new PlanColumnMapping(role.Role, selected.Id, selected.Name, selected.DataType) }).ToImmutableArray() });
                        RenderSettings();
                    });
                    if (generation != settingsGeneration) return;
                    rendering = true;
                    try { combo.SelectedItem = choices.FirstOrDefault(c => c.Definition.Id == session.Document.State.Settings.Columns.SingleOrDefault(m => m.Role == role.Role)?.FieldId) ?? choices[0]; }
                    finally { rendering = false; }
                };
                if (role.Role == PlanField.Phase) phaseMap = combo;
                fields.Children.Add(combo);
            }
            var sync = session.Document.Sync;
            var excluded = new[] { ("ドラフト", sync.DraftCount), ("プルリクエスト", sync.PullRequestCount), ("参照できない項目", sync.InaccessibleCount) }
                .Where(e => e.Item2 > 0).Select(e => $"{e.Item1} {e.Item2:N0} 件").ToArray();
            if (excluded.Length > 0) fields.Children.Add(Id(Label("計画対象外: " + string.Join("・", excluded)), "PlanExcludedCounts"));
            RenderPhaseSettings(phases, session, generation, phaseMap!);
            SettingsSection("issues", "新しい Issue").Children.Add(TextSetting("新規 Issue の作成先", "PlanDefaultRepository", value.DefaultRepository ?? "",
                text => ChangeSettings(s => s with { DefaultRepository = string.IsNullOrWhiteSpace(text) ? null : text.Trim() })));

            SettingsGroup("この PC（すべてのプロジェクト）", "pc");
            var display = SettingsSection("display", "表示");
            // #236 part B / #251 D1: each lateness mark can be hidden on this PC. The sets, the counts and the filters stay.
            display.Children.Add(MarkSwitch("期限超過の印", "PlanShowOverdueMark", m => m.Overdue, (m, on) => m with { Overdue = on }));
            display.Children.Add(MarkSwitch("予定より遅れの印", "PlanShowLaterMark", m => m.Later, (m, on) => m with { Later = on }));
            display.Children.Add(MarkSwitch("見積超過の印", "PlanShowOverEstimateMark", m => m.OverEstimate, (m, on) => m with { OverEstimate = on }));
            RenderAssistSettings();

            SettingsGroup("ファイル", "files");
            var files = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            files.Children.Add(Button("設定を書き出す", "PlanExportSettings", async () => { if (await SelectFile("export") is { } path) await session.ExportSettingsAsync(path); }));
            files.Children.Add(Button("設定を読み込む", "PlanImportSettings", async () => {
                if (await SelectFile("import") is not { } path) return;
                var result = await session.ImportSettingsAsync(path, Today);
                if (!result.Applied) throw new InvalidOperationException(result.Error);
                Check(result.Save!); RenderTasks(); RenderSettings();
                if (!result.Warnings.IsEmpty) error.Text = string.Join("\n", result.Warnings);
            }));
            SettingsSection("files", "書き出し・読み込み").Children.Add(files);
            settingsNav.SelectedItem = settingsSections.FirstOrDefault(s => (string)s.Item.Tag == selectedKey).Item ?? settingsSections[0].Item;
        }
        finally { rendering = false; }
    }

    private CheckBox MarkSwitch(string label, string id, Func<PlanMarkVisibility, bool> read, Func<PlanMarkVisibility, bool, PlanMarkVisibility> change)
    {
        var box = Id(new CheckBox { Content = label, IsChecked = read(workspace.Display.Marks), MinWidth = 0 }, id);
        AutomationProperties.SetName(box, label);
        ToolTipService.SetToolTip(box, label + "を表示します。件数と絞り込みは変わりません。");
        var generation = settingsGeneration;
        box.Click += async (_, _) => {
            if (rendering || generation != settingsGeneration) return;
            var on = box.IsChecked == true;
            await Run(async () => {
                await workspace.SaveDisplay(workspace.Display with { Marks = change(workspace.Display.Marks, on) });
                RenderTasks();
            });
            if (generation != settingsGeneration) return;
            rendering = true;
            try { box.IsChecked = read(workspace.Display.Marks); }
            finally { rendering = false; }
        };
        return box;
    }

    private void RenderPeriodSettings(StackPanel section, PlanSession session, int generation)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        line.Children.Add(DateSetting("開始日", "PlanProjectStart", s => s.ProjectStart, (s, day) => s with { ProjectStart = day }));
        line.Children.Add(DateSetting("終了日", "PlanProjectEnd", s => s.ProjectEnd, (s, day) => s with { ProjectEnd = day }));
        section.Children.Add(line);

        FrameworkElement DateSetting(string label, string id, Func<ProjectPlanSettings, DateOnly?> read, Func<ProjectPlanSettings, DateOnly?, ProjectPlanSettings> change)
        {
            var picker = Id(new CalendarDatePicker { Header = label, MinWidth = 190, Language = "ja-JP", PlaceholderText = "未設定",
                DateFormat = "{year.full}/{month.integer(2)}/{day.integer(2)} ({dayofweek.abbreviated})" }, id);
            AutomationProperties.SetName(picker, label);
            var clear = Id(new Button { Content = "クリア", VerticalAlignment = VerticalAlignment.Bottom }, id + "Clear");
            AutomationProperties.SetName(clear, label + "をクリア");
            void Show() {
                rendering = true;
                try {
                    var day = read(session.Document.State.Settings);
                    picker.Date = day is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue)) : null;
                    clear.IsEnabled = day is not null;
                }
                finally { rendering = false; }
            }
            async Task Apply(DateOnly? day) {
                if (generation != settingsGeneration || day == read(session.Document.State.Settings)) return;
                try { await Run(() => ChangeSettings(s => change(s, day))); }
                finally { if (generation == settingsGeneration) Show(); }
            }
            picker.DateChanged += async (_, args) => {
                if (rendering) return;
                await Apply(args.NewDate is { } date ? DateOnly.FromDateTime(date.Date) : null);
            };
            clear.Click += async (_, _) => await Apply(null);
            Show();
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            panel.Children.Add(picker); panel.Children.Add(clear);
            return panel;
        }
    }

    private void RenderPhaseSettings(StackPanel section, PlanSession session, int generation, ComboBox phaseMap)
    {
        var phaseDetails = Id(new TextBlock { TextWrapping = TextWrapping.Wrap }, "PlanPhaseOptions");
        var phaseTargets = new StackPanel { Spacing = 4 };
        void ShowPhases()
        {
            phaseTargets.Children.Clear();
            var mapping = session.Document.State.Settings.Columns.SingleOrDefault(c => c.Role == PlanField.Phase);
            var field = session.Document.Baseline.Columns.SingleOrDefault(c => c.Id == mapping?.FieldId);
            if (field is null) { phaseDetails.Text = "工程のフィールドが未設定です。"; phaseDetails.Visibility = Visibility.Visible; return; }
            var parents = session.Document.State.Rows.Where(r => r.Parent is not null).Select(r => r.Parent!).ToHashSet();
            var counts = session.Document.State.Rows.Where(r => !parents.Contains(r.Identity)).ToLookup(r => r.Phase ?? "");
            var unphased = counts[""].Count();
            phaseDetails.Text = unphased > 0 ? $"工程なし {unphased:N0} タスク" : "";
            phaseDetails.Visibility = unphased > 0 ? Visibility.Visible : Visibility.Collapsed;
            Grid PhaseRow(string option, string count, FrameworkElement target) {
                var row = new Grid { ColumnSpacing = 12 };
                foreach (var width in new[] { 100d, 100d, 320d }) row.ColumnDefinitions.Add(new() { Width = new(width) });
                row.Children.Add(new TextBlock { Text = option, VerticalAlignment = VerticalAlignment.Center });
                var tasks = Id(new TextBlock { Text = count, VerticalAlignment = VerticalAlignment.Center }, "PlanPhaseCount_" + option);
                AutomationProperties.SetName(tasks, option + " " + count + " タスク");
                row.Children.Add(tasks); Grid.SetColumn(tasks, 1); row.Children.Add(target); Grid.SetColumn(target, 2);
                return row;
            }
            phaseTargets.Children.Add(PhaseRow("工程", "タスク", Label("目標日（マイルストーン）")));
            var milestones = session.Document.Baseline.Milestones.OrderBy(m => m.Title, StringComparer.Ordinal).ToArray();
            var labels = new[] { "なし" }.Concat(milestones.Select(m => m.Title + " · " + (m.DueDate?.ToString("M/d") ?? "目標日なし"))).ToArray();
            foreach (var option in field.Options) {
                int Accepted() => Array.FindIndex(milestones, m => m.Title == session.Document.State.Settings.PhaseMilestones.GetValueOrDefault(option)) + 1;
                var combo = Id(new ComboBox { ItemsSource = labels, HorizontalAlignment = HorizontalAlignment.Stretch,
                    SelectedIndex = Accepted() }, "PlanPhaseMilestone_" + option);
                AutomationProperties.SetName(combo, option + " 目標日（マイルストーン）");
                combo.SelectionChanged += async (_, _) => {
                    if (rendering || generation != settingsGeneration) return;
                    var selected = combo.SelectedIndex;
                    if (selected == Accepted()) return;
                    await Run(() => ChangeSettings(s => s with { PhaseMilestones = selected <= 0 ? s.PhaseMilestones.Remove(option)
                        : s.PhaseMilestones.SetItem(option, milestones[selected - 1].Title) }));
                    rendering = true;
                    try { combo.SelectedIndex = Accepted(); }
                    finally { rendering = false; }
                };
                phaseTargets.Children.Add(PhaseRow(option, $"{counts[option].Count():N0}", combo));
            }
        }
        ShowPhases(); section.Children.Add(phaseDetails); section.Children.Add(phaseTargets);
        phaseMap.SelectionChanged += async (_, _) => { await operation; if (generation == settingsGeneration) ShowPhases(); };
    }

    private void RenderPeopleSettings(StackPanel section, PlanSession session, int generation)
    {
        var work = PlanPeople.Work(session.Document, sheet?.Session == session ? sheet.Schedule.Values.ToArray() : PlanOperations.Schedule(session.Document, Today));
        if (work.IsEmpty) { section.Children.Add(Label("このプロジェクトでタスクに割り当てられている担当者はいません。")); return; }
        var culture = CultureInfo.GetCultureInfo("ja-JP");
        var stored = session.Document.State.Settings.People.ToDictionary(p => p.Identity, p => p.Name);
        var table = Id(new Grid { ColumnSpacing = 16, RowSpacing = 4 }, "PlanPeopleTable");
        foreach (var width in new[] { 240d, 110, 120, 64, 80 }) table.ColumnDefinitions.Add(new() { Width = new(width) });
        void Add(FrameworkElement element, int row, int column, bool right = false) {
            element.VerticalAlignment = VerticalAlignment.Center;
            if (right) element.HorizontalAlignment = HorizontalAlignment.Right;
            table.Children.Add(element); Grid.SetRow(element, row); Grid.SetColumn(element, column);
        }
        table.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var secondary = (Style)Application.Current.Resources["WorkspaceMetadataStyle"];
        Add(new TextBlock { Text = "担当者", Style = secondary }, 0, 0);
        Add(Id(new TextBlock { Text = "稼働率 %", Style = secondary }, "PlanPeopleHeaderRate"), 0, 1);
        Add(Id(new TextBlock { Text = "上限工数 h", Style = secondary }, "PlanPeopleHeaderAllowance"), 0, 2);
        Add(new TextBlock { Text = "タスク", Style = secondary }, 0, 3, right: true);
        Add(new TextBlock { Text = "残 h", Style = secondary }, 0, 4, right: true);
        var line = 0;
        foreach (var (person, tasks, remaining) in work)
        {
            table.RowDefinitions.Add(new() { Height = GridLength.Auto }); line++;
            // The readable login comes from GitHub; a name set in settings or an imported file is the display name.
            var display = stored.GetValueOrDefault(person.Identity) is { Length: > 0 } name ? name : person.Name;
            var who = new StackPanel();
            who.Children.Add(new TextBlock { Text = display, TextTrimming = TextTrimming.CharacterEllipsis });
            if (display != person.Name) who.Children.Add(new TextBlock { Text = person.Name, Style = secondary, TextTrimming = TextTrimming.CharacterEllipsis });
            Add(who, line, 0);
            var rate = Id(new NumberBox { Value = (double)person.Rate, Minimum = 0, Maximum = 100 }, "PlanRate" + person.Identity);
            AutomationProperties.SetName(rate, person.Name + " 稼働率 (%)");
            rate.ValueChanged += async (_, _) => {
                if (rendering || generation != settingsGeneration) return;
                if (!double.IsNaN(rate.Value)) await Run(() => ChangePerson(person, p => p with { Rate = (decimal)rate.Value }));
                rendering = true;
                try { rate.Value = (double)(session.Document.State.Settings.People.SingleOrDefault(p => p.Identity == person.Identity)?.Rate ?? person.Rate); }
                finally { rendering = false; }
            };
            pendingSettings.Add(async () => {
                // NumberBox validates its native editor on focus loss. Window close can arrive first.
                if (FocusManager.GetFocusedElement(XamlRoot) is not TextBox input) return;
                DependencyObject? ancestor = input;
                while (ancestor is not null && ancestor != rate) ancestor = VisualTreeHelper.GetParent(ancestor);
                if (ancestor is null) return;
                if (!decimal.TryParse(input.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var pendingRate) || pendingRate <= 0 || pendingRate > 100)
                    throw new InvalidOperationException("稼働率は 0 より大きく 100 以下で入力してください。");
                await ChangePerson(person, p => p with { Rate = pendingRate });
                rendering = true;
                try { rate.Value = (double)pendingRate; }
                finally { rendering = false; }
            });
            Add(rate, line, 1);
            var allowance = TextSetting("上限工数 h", "PlanAllowance" + person.Identity, person.Allowance?.ToString(CultureInfo.CurrentCulture) ?? "", async text => {
                decimal? hours = string.IsNullOrWhiteSpace(text) ? null : decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var number) && number >= 0
                    ? number : throw new ArgumentException("上限工数は0以上の数値で入力してください（単位: h）。");
                await ChangePerson(person, p => p with { Allowance = hours });
            });
            allowance.Header = null; allowance.MinWidth = 0;
            AutomationProperties.SetName(allowance, person.Name + " 上限工数 h");
            Add(allowance, line, 2);
            var count = Id(new TextBlock { Text = tasks.ToString("N0", culture) }, "PlanPeopleTasks" + person.Identity);
            AutomationProperties.SetName(count, person.Name + " タスク " + count.Text);
            Add(count, line, 3, right: true);
            var left = Id(new TextBlock { Text = remaining is { } hours ? hours.ToString("#,0.##", culture) : "—" }, "PlanPeopleRemaining" + person.Identity);
            AutomationProperties.SetName(left, person.Name + " 残 " + (remaining is null ? "見込み残存工数が未入力のタスクがあります" : left.Text + " h"));
            Add(left, line, 4, right: true);
        }
        section.Children.Add(table);
    }

    private void RenderCalendarSettings(StackPanel section, PlanSession session)
    {
        var value = session.Document.State.Settings;
        var hours = new StackPanel { Spacing = 4 };
        hours.Children.Add(Id(new TextBlock { Text = "稼働時間", Style = (Style)Application.Current.Resources["WorkspaceMetadataStyle"] }, "PlanWorkingHoursLabel"));
        hours.Children.Add(Id(Label("月–金  09:00–13:00 / 14:00–18:00"), "PlanWorkingHours"));
        section.Children.Add(hours);
        var holidays = value.ImportedHolidays?.ToPreset() ?? PlanningContract.BundledHolidays();
        section.Children.Add(new Expander { Header = $"日本の祝日 {holidays.FirstYear}–{holidays.LastYear}", HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = Label(string.Join("\n", holidays.Dates.Select(d => $"{d.Date:yyyy-MM-dd}  {d.Name}"))) });
        section.Children.Add(Button("祝日CSVを読み込む", "PlanHolidayImport", async () => {
            if (await SelectFile("holiday") is not { } path) return;
            var imported = HolidayCsvImport.Parse(await File.ReadAllBytesAsync(path), path, DateTimeOffset.UtcNow, 2025, holidays.LastYear);
            await ChangeSettings(s => s with { ImportedHolidays = PlanHolidayData.FromPreset(imported) }); RenderSettings();
        }));
        section.Children.Add(DateListSetting("会社休日", "PlanCompanyDaysOff", () => session.Document.State.Settings.CompanyDaysOff,
            dates => ChangeSettings(s => s with { CompanyDaysOff = dates })));
    }

    private FrameworkElement DateListSetting(string label, string id, Func<ImmutableArray<DateOnly>> current, Func<ImmutableArray<DateOnly>, Task> apply)
    {
        var generation = settingsGeneration;
        var culture = CultureInfo.GetCultureInfo("ja-JP");
        var panel = Id(new StackPanel { Spacing = 4 }, id);
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["WorkspaceMetadataStyle"] });
        var picker = Id(new CalendarDatePicker { PlaceholderText = "日付を選択", MinWidth = 190, Language = "ja-JP",
            DateFormat = "{year.full}/{month.integer(2)}/{day.integer(2)} ({dayofweek.abbreviated})" }, id + "Date");
        AutomationProperties.SetName(picker, label + "の日付");
        var dates = Id(new ListView { MaxHeight = 240, SelectionMode = ListViewSelectionMode.None, Padding = new(0) }, id + "Dates");
        AutomationProperties.SetName(dates, label);
        // Add stays available: without a chosen date it opens the calendar instead of being a disabled dead end.
        var add = Button("追加", id + "Add", async () => {
            if (generation != settingsGeneration) return;
            if (picker.Date is not { } selected) { picker.IsCalendarOpen = true; return; }
            var day = DateOnly.FromDateTime(selected.DateTime);
            if (!current().Contains(day)) await apply(current().Append(day).Order().ToImmutableArray());
            ShowDates();
        });
        AutomationProperties.SetName(add, label + "に追加");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(picker); actions.Children.Add(add);
        panel.Children.Add(actions); panel.Children.Add(dates);
        void ShowDates()
        {
            dates.Items.Clear();
            foreach (var day in current().Order())
            {
                var text = day.ToString("yyyy/MM/dd (ddd)", culture);
                var row = new Grid { ColumnSpacing = 12, MinHeight = 36 };
                row.ColumnDefinitions.Add(new() { Width = new(160) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
                var remove = Button("削除", id + "Remove" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture), async () => {
                    if (generation != settingsGeneration) return;
                    await apply(current().Where(d => d != day).ToImmutableArray());
                    ShowDates();
                });
                AutomationProperties.SetName(remove, text + " を" + label + "から削除");
                remove.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(remove); Grid.SetColumn(remove, 1);
                dates.Items.Add(row);
            }
            dates.Visibility = dates.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        ShowDates();
        return panel;
    }

    private void RenderColumnSettings(StackPanel section, PlanSession session, int generation)
    {
        var reset = Button("既定に戻す", "PlanColumnReset", async () => { await ChangeSettings(s => s with { SheetColumns = [] }); RenderSettings(); });
        reset.HorizontalAlignment = HorizontalAlignment.Right;
        section.Children.Add(reset);
        var columns = PlanSheetColumns.All(session.Document);
        Grid Line() {
            var line = new Grid { ColumnSpacing = 8, MinHeight = 40 };
            foreach (var width in new[] { 20d, 44, 170, 72, 160, 104, 116 }) line.ColumnDefinitions.Add(new() { Width = new(width) });
            return line;
        }
        void Cell(Grid line, FrameworkElement content, int column) {
            content.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(content); Grid.SetColumn(content, column);
        }
        var headings = Line();
        var captions = new[] { "表示", "表示名", "幅", "GitHub", "種類", "計画シートで" };
        for (var i = 0; i < captions.Length; i++) Cell(headings, Label(captions[i]), i + 1);
        var table = new StackPanel(); table.Children.Add(headings);
        var list = columnList = Id(new ListView { CanDragItems = true, AllowDrop = true, CanReorderItems = true,
            SelectionMode = ListViewSelectionMode.Single, Padding = new(0),
            ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = {
                new Setter(Control.PaddingProperty, new Thickness(0)), new Setter(FrameworkElement.MinHeightProperty, 40d),
                new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } } }, "PlanColumnList");
        // This short configuration table expands naturally; the settings page owns scrolling.
        ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(list, "計画シートの列の順序");
        AutomationProperties.SetHelpText(list, "ハンドルをドラッグするか、行で Alt+↑ / Alt+↓ を押して順序を変更します。");
        foreach (var column in columns)
        {
            var line = Line(); line.Tag = column.Key;
            var handle = new FontIcon { Glyph = "", FontSize = 14, IsTabStop = false };
            ToolTipService.SetToolTip(handle, "ドラッグまたは Alt+↑ / Alt+↓ で順序を変更");
            AutomationProperties.SetName(handle, column.Name + " の順序"); Cell(line, handle, 0);
            CheckBox? toggle = null;
            if (!column.Required)
            {
                toggle = Id(new CheckBox { IsChecked = column.Visible, MinWidth = 32, MinHeight = 32 }, "PlanColumn" + column.Key);
                AutomationProperties.SetName(toggle, column.Name + " を表示");
                async void Changed(object sender, RoutedEventArgs args) {
                    if (rendering || generation != settingsGeneration) return;
                    var visible = toggle.IsChecked == true;
                    await Run(async () => { await ChangeSettings(_ => PlanSheetColumns.Change(session.Document, column.Key, visible)); RenderSettings(); });
                    if (generation == settingsGeneration) { rendering = true; try { toggle.IsChecked = PlanSheetColumns.All(session.Document).Single(c => c.Key == column.Key).Visible; } finally { rendering = false; } }
                }
                toggle.Checked += Changed; toggle.Unchecked += Changed; Cell(line, toggle, 1);
            }
            TextBox? widthInput = null;
            string CurrentName() => PlanSheetColumns.All(session.Document).Single(c => c.Key == column.Key).Name;
            // The box shows the current name as text; clearing it, or typing the default name, restores the default.
            var name = TextSetting("表示名", "PlanColumnName" + column.Key, column.Name, async text => {
                await ChangeSettings(_ => PlanSheetColumns.Change(session.Document, column.Key, name: text.Trim() == column.DefaultName ? null : text, rename: true));
                var display = CurrentName();
                if (toggle is not null) AutomationProperties.SetName(toggle, display + " を表示");
                if (widthInput is not null) AutomationProperties.SetName(widthInput, display + " の幅");
            }, CurrentName);
            name.Header = null; name.MinWidth = 0; name.MinHeight = 32; name.Padding = new(8, 4, 8, 4);
            name.PlaceholderText = column.DefaultName; AutomationProperties.SetName(name, column.DefaultName + " の表示名");
            Cell(line, name, 2);
            if (column.Key == "Indicator") Cell(line, Label("—"), 3);
            else {
                widthInput = TextSetting("幅", "PlanColumnWidth" + column.Key, column.Width?.ToString(CultureInfo.CurrentCulture) ?? "", async text => {
                    double? width = null;
                    if (!string.IsNullOrWhiteSpace(text)) {
                        if (!double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed)
                            || !double.IsFinite(parsed) || parsed < PlanSheetColumns.MinimumWidth || parsed > PlanSheetColumns.MaximumWidth || parsed != Math.Truncate(parsed))
                            throw new ArgumentException("幅は40〜800の整数で入力してください。");
                        width = parsed;
                    }
                    await ChangeSettings(_ => PlanSheetColumns.Change(session.Document, column.Key, width: width, resize: true));
                });
                widthInput.Header = null; widthInput.MinWidth = 0; widthInput.MinHeight = 32; widthInput.Padding = new(8, 4, 8, 4);
                widthInput.PlaceholderText = PlanSheetView.DefaultColumnWidth(column.Key).ToString(CultureInfo.CurrentCulture);
                AutomationProperties.SetName(widthInput, column.Name + " の幅"); Cell(line, widthInput, 3);
            }
            var source = new TextBlock { Text = column.GitHub, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(source, column.GitHub); Cell(line, source, 4);
            Cell(line, Id(Label(column.Type), "PlanColumnType" + column.Key), 5);
            var mode = Id(Label(column.Editability), "PlanColumnMode" + column.Key);
            if (column.Key == "Repository") ToolTipService.SetToolTip(mode, "リポジトリを変えると Issue の移管になるため、読み取りのみです。");
            Cell(line, mode, 6);
            list.Items.Add(line);
        }
        // Alt+↑ / Alt+↓ moves the row that holds focus, the keyboard counterpart of the drag handle.
        list.PreviewKeyDown += async (_, args) => {
            if (args.Key is not (Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down) || !PlanSheetView.Down(Windows.System.VirtualKey.Menu)
                || rendering || generation != settingsGeneration) return;
            DependencyObject? focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (focused is not null and not ListViewItem) focused = VisualTreeHelper.GetParent(focused);
            if (focused is not ListViewItem { Content: Grid { Tag: string key } }) return;
            args.Handled = true;
            await MoveColumn(session, generation, key, args.Key == Windows.System.VirtualKey.Up ? -1 : 1);
        };
        list.DragItemsCompleted += async (_, _) => {
            if (rendering || generation != settingsGeneration) return;
            var order = list.Items.Cast<Grid>().Select(g => (string)g.Tag).ToArray();
            await Run(async () => {
                await ChangeSettings(_ => PlanSheetColumns.Reorder(session.Document, order));
                RenderSettings();
            });
        };
        table.Children.Add(list); section.Children.Add(table);
    }

    private async Task MoveColumn(PlanSession session, int generation, string key, int step)
    {
        var target = -1;
        await Run(async () => {
            if (generation != settingsGeneration) return;
            var keys = PlanSheetColumns.All(session.Document).Select(c => c.Key).ToList();
            target = keys.IndexOf(key) + step;
            if (target < 0 || target >= keys.Count) { target = keys.IndexOf(key); return; }
            await ChangeSettings(_ => PlanSheetColumns.Move(session.Document, key, target));
            RenderSettings();
        });
        // Keep the keyboard on the moved row so the next Alt+↑ / Alt+↓ continues from it.
        if (columnList is not { } list || target < 0) return;
        list.UpdateLayout();
        if (list.ContainerFromIndex(target) is ListViewItem item) { item.Focus(FocusState.Keyboard); list.SelectedIndex = target; }
    }
}
