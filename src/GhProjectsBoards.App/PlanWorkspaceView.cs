using System.Collections.Immutable;
using System.Globalization;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace GhProjectsBoards.App;

internal sealed class PlanWorkspaceView : UserControl
{
    private readonly PlanWorkspace workspace;
    private readonly Func<string, string, GhConnectionService> factory;
    private readonly Grid root = new() { ColumnSpacing = 12, Padding = new(12) };
    private readonly Grid sidebar = new() { RowSpacing = 8 };
    private readonly Grid body = new() { RowSpacing = 8 };
    private readonly StackPanel connection = new() { Spacing = 8, MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel chooser = new() { Spacing = 8 };
    private readonly StackPanel settings = new() { Spacing = 12, MaxWidth = 850, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ScrollViewer settingsScroll;
    private readonly ListView registered = Id(new ListView { SelectionMode = ListViewSelectionMode.Single }, "RegisteredProjects");
    private readonly ListView available = Id(new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 450 }, "AvailableProjects");
    private readonly ListView tasks = Id(new ListView { SelectionMode = ListViewSelectionMode.None }, "PlanTasks");
    private readonly StackPanel taskHeaders = new() { Orientation = Orientation.Horizontal };
    private readonly Grid taskArea = new();
    private readonly ScrollViewer taskScroll;
    private readonly Button retrySave;
    private readonly TextBlock title = Id(new TextBlock { FontSize = 22 }, "OpenProjectName");
    private readonly TextBlock unpublished = Id(new TextBlock(), "PlanUnpublished");
    private readonly TextBlock error = Id(new TextBlock { TextWrapping = TextWrapping.Wrap }, "PlanError");
    private readonly TextBox executable = Id(new TextBox { Header = "gh.exe", Text = ConnectionViewModel.FindGh(), MinWidth = 400 }, "PlanGhPath");
    private readonly TextBox host = Id(new TextBox { Header = "接続先", Text = "github.com" }, "PlanHost");
    private readonly TextBox url = Id(new TextBox { Header = "Project URL" }, "PlanProjectUrl");
    private readonly StackPanel toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private Task operation = Task.CompletedTask;
    private CancellationTokenSource? operationCancellation;
    private CancellationToken OperationToken => operationCancellation?.Token ?? CancellationToken.None;
    private readonly List<Func<Task>> pendingSettings = [];
    private int settingsGeneration;
    private bool rendering, closing;
    internal IntPtr WindowHandle { get; set; }
    internal Func<string, Task<string?>>? PickFile { get; set; }
    private DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    internal PlanWorkspaceView(PlanWorkspace workspace, Func<string, string, GhConnectionService>? factory = null)
    {
        root.Style = (Style)Application.Current.Resources["PlanWorkspaceSurfaceStyle"];
        this.workspace = workspace; this.factory = factory ?? ((path, server) => new(path, server));
        root.ColumnDefinitions.Add(new() { Width = new(205) }); root.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        root.Children.Add(sidebar); root.Children.Add(body); Grid.SetColumn(body, 1);
        for (var i = 0; i < 4; i++) body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        registered.ItemTemplate = available.ItemTemplate = (DataTemplate)Application.Current.Resources["PlanProjectChoiceTemplate"];
        registered.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = {
            new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } };
        sidebar.Children.Add(Label("Projects"));
        sidebar.Children.Add(registered);
        sidebar.Children.Add(Button("Projectを開く", "PlanChooseProject", () => { Show("chooser"); return Task.CompletedTask; }));
        sidebar.Children.Add(Button("接続", "PlanConnection", () => { Show("connection"); return Task.CompletedTask; }));
        for (var row = 0; row < sidebar.Children.Count; row++) Grid.SetRow((FrameworkElement)sidebar.Children[row], row);
        body.Children.Add(title);
        body.Children.Add(toolbar); Grid.SetRow(toolbar, 1);
        toolbar.Children.Add(Button("計画", "PlanShowTasks", () => { Show("tasks"); return Task.CompletedTask; }));
        toolbar.Children.Add(Button("設定", "PlanShowSettings", () => { RenderSettings(); Show("settings"); return Task.CompletedTask; }));
        toolbar.Children.Add(Button("最新の情報に更新", "PlanRefresh", async () => { await workspace.Refresh(OperationToken); RenderTasks(); RenderSettings(); }));
        toolbar.Children.Add(Button("元に戻す", "PlanUndo", async () => { if (workspace.Session is { } session) Check(await session.Undo(Today)); RenderTasks(); RenderSettings(); }));
        toolbar.Children.Add(unpublished);
        var problem = new StackPanel { Spacing = 4 };
        problem.Children.Add(error);
        retrySave = Button("保存を再試行", "PlanRetrySave", async () => {
            await workspace.RetrySave(); await CommitPending(); RenderTasks(); RenderSettings();
        }, commitPending: false);
        retrySave.Visibility = Visibility.Collapsed; problem.Children.Add(retrySave);
        body.Children.Add(problem); Grid.SetRow(problem, 2);
        body.Children.Add(progress); Grid.SetRow(progress, 3);
        connection.Children.Add(executable); connection.Children.Add(host);
        connection.Children.Add(Button("接続", "PlanConnect", async () => {
            await workspace.Connect(this.factory(executable.Text, host.Text), OperationToken);
            RefreshLists(); RenderTasks();
            if (workspace.DiscoveryWarning is { } warning) error.Text = warning;
            Show(workspace.Session is null ? "chooser" : "tasks");
        }));
        chooser.Children.Add(Label("Projectを選択"));
        chooser.Children.Add(available);
        var address = new Expander { Header = "URLで開く", HorizontalAlignment = HorizontalAlignment.Stretch };
        var addressPanel = new StackPanel { Spacing = 8 }; addressPanel.Children.Add(url);
        addressPanel.Children.Add(Button("開く", "PlanOpenUrl", async () => { await workspace.OpenUrl(url.Text, OperationToken); Opened(); }));
        address.Content = addressPanel; chooser.Children.Add(address);

        registered.SelectionChanged += async (_, _) => { if (!rendering && registered.SelectedItem is ProjectChoice choice) await Run(async () => { await workspace.Open(choice, OperationToken); Opened(); }); };
        available.SelectionChanged += async (_, _) => { if (!rendering && available.SelectedItem is ProjectChoice choice) await Run(async () => { await workspace.Open(choice, OperationToken); Opened(); }); };
        taskArea.RowDefinitions.Add(new() { Height = GridLength.Auto }); taskArea.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        tasks.Padding = new(0);
        tasks.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = {
            new Setter(Control.PaddingProperty, new Thickness(0)),
            new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
            new Setter(FrameworkElement.MinHeightProperty, 32d) } };
        taskArea.Children.Add(taskHeaders); taskArea.Children.Add(tasks); Grid.SetRow(tasks, 1);
        taskScroll = new() { Content = taskArea, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled };
        settingsScroll = Id(new ScrollViewer { Content = settings, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, "PlanSettingsScroll");
        foreach (var surface in new FrameworkElement[] { connection, chooser, taskScroll, settingsScroll }) { body.Children.Add(surface); Grid.SetRow(surface, 4); }
        Unloaded += (_, _) => { closing = true; settingsGeneration++; operationCancellation?.Cancel(); };
        Content = root;
        Show("connection");
    }
    private static T Id<T>(T control, string id) where T : DependencyObject { AutomationProperties.SetAutomationId(control, id); return control; }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private Button Button(string text, string id, Func<Task> action, bool commitPending = true)
    {
        var button = Id(new Button { Content = text }, id);
        button.Click += async (_, _) => { if (button.IsLoaded) await Run(action, commitPending); }; return button;
    }
    private Task Run(Func<Task> action, bool commitPending = true)
    {
        if (closing) return Task.CompletedTask;
        var previous = operation;
        // Publish the tail before executing, including synchronous focus events raised by the command.
        operation = Execute();
        return operation;
        async Task Execute()
        {
            await Task.Yield();
            await previous;
            if (closing) return;
            error.Text = ""; retrySave.Visibility = Visibility.Collapsed; progress.Visibility = Visibility.Visible;
            using var cancellation = new CancellationTokenSource();
            operationCancellation = cancellation;
            try { if (commitPending) await CommitPending(); await action(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (cancellation.IsCancellationRequested) return;
                RefreshLists();
                if (workspace.Session is null) { tasks.Items.Clear(); taskHeaders.Children.Clear(); unpublished.Text = ""; Show("connection"); }
                retrySave.Visibility = ex is IOException or UnauthorizedAccessException ? Visibility.Visible : Visibility.Collapsed;
                error.Text = ex is DiscoveryException ? "Projectを取得できません。接続先とURLを確認してください。" : ex is FormatException ? "日付は yyyy-MM-dd で入力してください。" : ex.Message;
            }
            finally { operationCancellation = null; progress.Visibility = Visibility.Collapsed; }
        }
    }
    internal async Task<bool> StopAsync()
    {
        closing = true; operationCancellation?.Cancel(); await operation;
        try { await CommitPending(); await workspace.Flush(); return true; }
        catch (Exception ex) { error.Text = ex.Message; retrySave.Visibility = ex is IOException or UnauthorizedAccessException ? Visibility.Visible : Visibility.Collapsed; closing = false; return false; }
    }
    private void Show(string page)
    {
        connection.Visibility = page == "connection" ? Visibility.Visible : Visibility.Collapsed;
        chooser.Visibility = page == "chooser" ? Visibility.Visible : Visibility.Collapsed;
        taskScroll.Visibility = page == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        settingsScroll.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        toolbar.Visibility = workspace.Session is null ? Visibility.Collapsed : Visibility.Visible;
        sidebar.Visibility = workspace.Context is null ? Visibility.Collapsed : Visibility.Visible;
        root.ColumnDefinitions[0].Width = workspace.Context is null ? new(0) : new(205);
        title.Text = workspace.Selected?.Title ?? "GitHub Projects";
    }
    private void Opened() { settingsGeneration++; pendingSettings.Clear(); RefreshLists(); RenderTasks(); Show("tasks"); }
    private void RefreshLists()
    {
        rendering = true;
        try { registered.ItemsSource = workspace.Registered; registered.SelectedItem = workspace.Selected; available.ItemsSource = workspace.Available; available.SelectedItem = null; }
        finally { rendering = false; }
    }
    private void RenderTasks()
    {
        if (workspace.Session is not { } session) return;
        title.Text = workspace.Selected!.Title;
        unpublished.Text = $"未発行 {session.Changes(Today).TaskCount} タスク";
        var columns = session.Document.State.Settings.Columns;
        taskHeaders.Children.Clear();
        taskHeaders.Children.Add(Cell("タスク", 250));
        foreach (var column in columns) taskHeaders.Children.Add(Cell(column.Name, 110));
        tasks.Items.Clear();
        var calculated = session.Schedule(Today).ToDictionary(t => t.Input.Identity);
        foreach (var row in session.Document.State.Rows)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(Cell(row.Title, 250));
            foreach (var column in columns)
            {
                var text = column.Role switch {
                    PlanField.Start => calculated[row.Identity].Start.Value?.ToString("yyyy-MM-dd") ?? "",
                    PlanField.End => calculated[row.Identity].End.Value?.ToString("yyyy-MM-dd") ?? "",
                    PlanField.Fixed => row.Fixed ? "固定" : "",
                    _ => PlanValues.Get(row, column.Role).Trim('"') is var value && value != "null" ? value : ""
                };
                line.Children.Add(Cell(text, 110));
            }
            tasks.Items.Add(line);
        }
    }
    private static TextBlock Cell(string text, double width) => new() { Text = text, Width = width, Margin = new(4), TextTrimming = TextTrimming.CharacterEllipsis };
    private async Task ChangeSettings(Func<ProjectPlanSettings, ProjectPlanSettings> change)
    {
        if (workspace.Session is not { } session) return;
        Check(await session.Execute(new ReplacePlanSettings(change(session.Document.State.Settings)), Today));
        RenderTasks();
    }
    private static void Check(PlanSaveResult save) { if (!save.Succeeded) throw new IOException(save.Error); }
    private void RenderSettings()
    {
        if (workspace.Session is not { } session) return;
        rendering = true;
        try
        {
            var generation = ++settingsGeneration; pendingSettings.Clear(); settings.Children.Clear();
            var value = session.Document.State.Settings;
            settings.Children.Add(Label("GitHub列"));
            foreach (var role in PlanColumnMatching.Roles)
            {
                var choices = new[] { new PlanColumnDefinition("", "未設定", role.Type) }.Concat(session.Document.Baseline.Columns.Where(c => c.DataType == role.Type)).ToArray();
                var combo = Id(new ComboBox { Header = role.Name, ItemsSource = choices, DisplayMemberPath = "Name", Width = 280 }, "PlanMap" + role.Role);
                combo.SelectedItem = choices.FirstOrDefault(c => c.Id == value.Columns.SingleOrDefault(m => m.Role == role.Role)?.FieldId) ?? choices[0];
                combo.SelectionChanged += async (_, _) => {
                    if (rendering || generation != settingsGeneration || combo.SelectedItem is not PlanColumnDefinition selected) return;
                    await Run(() => ChangeSettings(s => s with { Columns = s.Columns.Where(c => c.Role != role.Role)
                        .Concat(selected.Id.Length == 0 ? [] : new[] { new PlanColumnMapping(role.Role, selected.Id, selected.Name, selected.DataType) }).ToImmutableArray() }));
                    rendering = true;
                    try { combo.SelectedItem = choices.FirstOrDefault(c => c.Id == session.Document.State.Settings.Columns.SingleOrDefault(m => m.Role == role.Role)?.FieldId) ?? choices[0]; }
                    finally { rendering = false; }
                };
                settings.Children.Add(combo);
            }
            settings.Children.Add(Button("不足する日程列を追加", "PlanAddFields", async () => {
                var result = await new PlanPublisher(workspace.Service!, workspace.Context!).AddSchedulingFieldsAsync(session, Today, OperationToken);
                if (!result.Succeeded) throw new InvalidOperationException(result.Error);
                await workspace.Refresh(OperationToken); RenderTasks(); RenderSettings();
            }));
            settings.Children.Add(Label("カレンダー"));
            settings.Children.Add(Label("月–金  09:00–13:00 / 14:00–18:00"));
            var holidays = value.ImportedHolidays?.ToPreset() ?? PlanningContract.BundledHolidays();
            var holidayList = new Expander { Header = $"日本の祝日 {holidays.FirstYear}–{holidays.LastYear}", Content =
                Label(string.Join("\n", holidays.Dates.Select(d => $"{d.Date:yyyy-MM-dd}  {d.Name}"))) };
            settings.Children.Add(holidayList);
            settings.Children.Add(Button("祝日CSVを読み込む", "PlanHolidayImport", async () => {
                if (await SelectFile("holiday") is not { } path) return;
                var imported = HolidayCsvImport.Parse(await File.ReadAllBytesAsync(path), path, DateTimeOffset.UtcNow, 2025, holidays.LastYear);
                await ChangeSettings(s => s with { ImportedHolidays = PlanHolidayData.FromPreset(imported) }); RenderSettings();
            }));
            settings.Children.Add(DateListSetting("会社休日", "PlanCompanyDaysOff", () => session.Document.State.Settings.CompanyDaysOff,
                dates => ChangeSettings(s => s with { CompanyDaysOff = dates })));
            settings.Children.Add(Label("担当者"));
            var people = workspace.People;
            foreach (var person in people)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                line.Children.Add(new TextBlock { Text = person.Name, Width = 150, VerticalAlignment = VerticalAlignment.Center });
                var rate = Id(new NumberBox { Header = "稼働率 (%)", Value = (double)person.Rate, Minimum = 0, Maximum = 100, Width = 150 }, "PlanRate" + person.Identity);
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
                line.Children.Add(rate);
                line.Children.Add(DateListSetting("個人休日", "PlanDaysOff" + person.Identity,
                    () => session.Document.State.Settings.People.SingleOrDefault(p => p.Identity == person.Identity)?.DaysOff ?? person.DaysOff,
                    dates => ChangePerson(person, p => p with { DaysOff = dates })));
                settings.Children.Add(line);
            }
            settings.Children.Add(TextSetting("Project開始日", "PlanProjectStart", value.ProjectStart?.ToString("yyyy-MM-dd") ?? "",
                text => ChangeSettings(s => s with { ProjectStart = string.IsNullOrWhiteSpace(text) ? null : DateOnly.ParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture) })));
            settings.Children.Add(TextSetting("既定リポジトリ", "PlanDefaultRepository", value.DefaultRepository ?? "",
                text => ChangeSettings(s => s with { DefaultRepository = string.IsNullOrWhiteSpace(text) ? null : text.Trim() })));
            var files = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            files.Children.Add(Button("設定を書き出す", "PlanExportSettings", async () => { if (await SelectFile("export") is { } path) await session.ExportSettingsAsync(path); }));
            files.Children.Add(Button("設定を読み込む", "PlanImportSettings", async () => {
                if (await SelectFile("import") is not { } path) return;
                var result = await session.ImportSettingsAsync(path, Today);
                if (!result.Applied) throw new InvalidOperationException(result.Error);
                Check(result.Save!); RenderTasks(); RenderSettings();
                if (!result.Warnings.IsEmpty) error.Text = string.Join("\n", result.Warnings);
            }));
            settings.Children.Add(files);
            settings.Children.Add(Id(Label($"計画対象外  Draft {session.Document.Sync.DraftCount} / Pull request {session.Document.Sync.PullRequestCount}"), "PlanExcludedCounts"));
        }
        finally { rendering = false; }
    }
    private Task ChangePerson(PlanResource fallback, Func<PlanResource, PlanResource> change) => ChangeSettings(s =>
    {
        var person = s.People.SingleOrDefault(p => p.Identity == fallback.Identity) ?? fallback;
        return s with { People = s.People.Where(p => p.Identity != person.Identity).Append(change(person)).ToImmutableArray() };
    });
    private TextBox TextSetting(string label, string id, string value, Func<string, Task> apply)
    {
        var input = Id(new TextBox { Header = label, Text = value, MinWidth = 280 }, id);
        var accepted = value;
        var generation = settingsGeneration;
        async Task Commit()
        {
            if (generation != settingsGeneration || input.Text == accepted) return;
            var text = input.Text;
            await apply(text);
            accepted = text;
        }
        pendingSettings.Add(Commit);
        input.LostFocus += async (_, _) => {
            if (!rendering && generation == settingsGeneration && input.Text != accepted) await Run(Commit);
        };
        return input;
    }
    private async Task CommitPending()
    {
        foreach (var commit in pendingSettings.ToArray()) await commit();
    }
    private FrameworkElement DateListSetting(string label, string id, Func<ImmutableArray<DateOnly>> current, Func<ImmutableArray<DateOnly>, Task> apply)
    {
        var generation = settingsGeneration;
        var panel = Id(new StackPanel { Spacing = 4, MinWidth = 280 }, id);
        panel.Children.Add(Label(label));
        var picker = Id(new CalendarDatePicker { PlaceholderText = "日付を選択", Width = 190 }, id + "Date");
        AutomationProperties.SetName(picker, label + "の日付");
        var dates = Id(new ListView { MaxHeight = 120, SelectionMode = ListViewSelectionMode.Single }, id + "Dates");
        AutomationProperties.SetName(dates, label);
        var add = Button("追加", id + "Add", async () => {
            if (generation != settingsGeneration || picker.Date is not { } selected) return;
            var day = DateOnly.FromDateTime(selected.DateTime);
            if (!current().Contains(day)) await apply(current().Append(day).Order().ToImmutableArray());
            ShowDates();
        });
        var remove = Button("削除", id + "Remove", async () => {
            if (generation != settingsGeneration || dates.SelectedItem is not string selected) return;
            var day = DateOnly.ParseExact(selected, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            await apply(current().Where(d => d != day).ToImmutableArray());
            ShowDates();
        });
        add.IsEnabled = false; remove.IsEnabled = false;
        AutomationProperties.SetName(add, label + "に追加"); AutomationProperties.SetName(remove, label + "から削除");
        picker.DateChanged += (_, _) => add.IsEnabled = picker.Date is not null;
        dates.SelectionChanged += (_, _) => remove.IsEnabled = dates.SelectedItem is not null;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        actions.Children.Add(picker); actions.Children.Add(add); actions.Children.Add(remove);
        panel.Children.Add(actions); panel.Children.Add(dates);
        void ShowDates() => dates.ItemsSource = current().Order().Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
        ShowDates();
        return panel;
    }
    private async Task<string?> SelectFile(string purpose)
    {
        if (PickFile is { } pick) return await pick(purpose);
        if (purpose == "export")
        {
            var picker = new FileSavePicker { SuggestedFileName = "project-settings" };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            picker.FileTypeChoices.Add("JSON", [".json"]);
            return (await picker.PickSaveFileAsync())?.Path;
        }
        else
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            picker.FileTypeFilter.Add(purpose == "holiday" ? ".csv" : ".json");
            return (await picker.PickSingleFileAsync())?.Path;
        }
    }
}

