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

internal sealed partial class PlanWorkspaceView : UserControl
{
    private readonly PlanWorkspace workspace;
    private readonly Func<string, string, GhConnectionService> factory;
    private readonly Grid root = new();
    internal TitleBar WorkspaceTitleBar { get; } = new();
    private XamlRoot? captionRoot;
    private readonly Grid surfaces = new();
    private readonly StackPanel failures = new();
    private readonly Border sheetFailureHost = new();
    private readonly InfoBar refreshFailure = FailureBar("PlanRefreshFailure", "最新の情報に更新できませんでした");
    private readonly InfoBar publishFailure = FailureBar("PlanPublishFailure", "発行できませんでした");
    private readonly InfoBar saveFailure = FailureBar("PlanSaveFailure", "保存できませんでした");
    private bool refreshing;
    private readonly Flyout projectFlyout = new();
    private readonly Button projectPicker = Id(new Button(), "PlanProjectPicker");
    private readonly TextBlock projectMetadata = new();
    private readonly StackPanel legend = Id(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Margin = new(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center }, "PlanGanttLegend");
    private readonly Border statusBar = new() { Height = 28 };
    private readonly TextBlock statusCounts = Id(new TextBlock { Margin = new(20, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }, "PlanStatusCounts");
    private readonly SelectorBarItem tasksTab = Id(new SelectorBarItem { Text = "計画" }, "PlanShowTasks");
    private readonly SelectorBarItem peopleTab = Id(new SelectorBarItem { Text = "担当者" }, "PlanShowPeople");
    private readonly SelectorBar tabs = new();
    private bool syncingTabs;
    private int pendingViewSelections;
    private readonly Button settingsButton;
    private string currentPage = "connection", settingsReturnPage = "tasks";
    private readonly StackPanel connection = new() { Spacing = 8, MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel chooser = new() { Spacing = 8 };
    private readonly StackPanel settings = new() { Spacing = 12, MaxWidth = 850, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ScrollViewer settingsScroll;
    private readonly ListView registered = Id(new ListView { SelectionMode = ListViewSelectionMode.Single }, "RegisteredProjects");
    private readonly ListView available = Id(new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 450 }, "AvailableProjects");
    private readonly Dictionary<ScopedId, PlanSheetView> sheets = [];
    private PlanSheetView? sheet;
    private readonly Grid taskArea = new();
    private readonly Grid peopleArea = new();
    private PlanPeopleView? peopleView;
    private readonly Button retrySave;
    private readonly TextBlock title = Id(new TextBlock { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, "OpenProjectName");
    private readonly Border unpublishedBadge = new() { CornerRadius = new(10), MinWidth = 20, Padding = new(6, 2, 6, 2), Background = PlanSheetView.Brush("SheetChangedMarkBrush") };
    private readonly TextBlock unpublishedLabel = new() { Text = "未発行のタスク", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock unpublished = Id(new TextBlock(), "PlanUnpublished");
    private readonly TextBlock error = Id(new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }, "PlanError");
    private readonly TextBox executable = Id(new TextBox { Header = "gh.exe", Text = ConnectionViewModel.FindGh(), MinWidth = 400 }, "PlanGhPath");
    private readonly TextBox host = Id(new TextBox { Header = "接続先", Text = "github.com" }, "PlanHost");
    private readonly TextBox url = Id(new TextBox { Header = "Project URL" }, "PlanProjectUrl");
    private readonly Grid toolbar = new() { Height = 36, Margin = new(12, 0, 12, 0) };
    private readonly Border statusDateHost = new();
    private readonly StackPanel commandButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Height = 3, Visibility = Visibility.Collapsed };
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
        RequestedTheme = ElementTheme.Light;
        FontSize = 14;
        // Keep the existing 22-row people overview within a 720-DIP client area.
        WorkspaceTitleBar.Resources["TitleBarExpandedHeight"] = 32d;
        WorkspaceTitleBar.Resources["TitleBarCompactHeight"] = 32d;
        // The stretch content column remains draggable between the picker and settings.
        WorkspaceTitleBar.Resources["TitleBarMinDragRegionWidth"] = 4d;
        WorkspaceTitleBar.Loaded += (_, _) => {
            captionRoot = XamlRoot; captionRoot.Changed += CaptionRootChanged;
            AlignCaptionInset(); ApplyCaptionColors();
        };
        WorkspaceTitleBar.SizeChanged += (_, _) => AlignCaptionInset();
        ActualThemeChanged += (_, _) => ApplyCaptionColors();
        tabs.Padding = new(0);
        tasksTab.Padding = peopleTab.Padding = new Thickness(12, 5, 12, 3);
        projectPicker.MinHeight = 0; projectPicker.Height = 32;
        projectPicker.Padding = new(8, 0, 8, 0);
        projectPicker.Style = (Style)Application.Current.Resources["SubtleButtonStyle"];
        root.Style = (Style)Application.Current.Resources["PlanWorkspaceSurfaceStyle"];
        this.workspace = workspace; this.factory = factory ?? ((path, server) => new(path, server));
        for (var i = 0; i < 4; i++) root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        registered.ItemTemplate = available.ItemTemplate = (DataTemplate)Application.Current.Resources["PlanProjectChoiceTemplate"];
        registered.MaxHeight = 320;
        registered.ContainerContentChanging += (_, args) => {
            if (args.InRecycleQueue) return;
            UpdateProjectCheck(args.ItemContainer, args.Item as ProjectChoice);
        };
        registered.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = {
            new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } };
        var projects = new StackPanel { Spacing = 8, Width = 340 };
        projects.Children.Add(registered);
        projects.Children.Add(new Border { Height = 1, Background = PlanSheetView.Brush("WorkspaceCardStrokeBrush") });
        projects.Children.Add(Command("Project を開く…", "PlanChooseProject", Symbol.OpenFile, () => { Show("chooser"); return Task.CompletedTask; }));
        projects.Children.Add(Command("接続…", "PlanConnection", Symbol.Link, () => { Show("connection"); return Task.CompletedTask; }));
        projectFlyout.Content = projects;
        projectFlyout.Opened += (_, _) => RefreshProjectChecks();
        projectPicker.Flyout = projectFlyout;
        var projectCaption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        projectMetadata.Style = (Style)Application.Current.Resources["WorkspaceMetadataStyle"];
        projectCaption.Children.Add(title); projectCaption.Children.Add(projectMetadata);
        projectCaption.Children.Add(new FontIcon { Glyph = "\uE70D", FontSize = 12 });
        projectPicker.Content = projectCaption;
        AutomationProperties.SetName(projectPicker, "Project を選択");
        WorkspaceTitleBar.Subtitle = "計画エディタ";
        // Give the vector finite bounds: this SDK's IconSource Viewbox measures a PathIcon unbounded.
        var appGlyph = (PathIcon)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            """<PathIcon xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="16" Height="16" Margin="14,0,0,0" Data="M1,2 H11 V5 H1 Z M6,7 H15 V10 H6 Z M3,12 H9 V15 H3 Z" />""");
        appGlyph.Foreground = PlanSheetView.Brush("SystemControlHighlightAccentBrush");
        WorkspaceTitleBar.Resources["TitleBarContentHorizontalAlignment"] = HorizontalAlignment.Left;
        settingsButton = Button("設定", "PlanShowSettings", () => {
            if (currentPage != "settings") settingsReturnPage = currentPage;
            RenderSettings(); Show("settings"); return Task.CompletedTask;
        });
        settingsButton.Style = (Style)Application.Current.Resources["SubtleButtonStyle"];
        settingsButton.Content = new SymbolIcon(Symbol.Setting);
        settingsButton.MinHeight = 0; settingsButton.Height = 32; settingsButton.Padding = new(8, 0, 8, 0);
        AutomationProperties.SetName(settingsButton, "設定");
        ToolTipService.SetToolTip(settingsButton, "設定");
        root.Children.Add(WorkspaceTitleBar);
        root.Children.Add(toolbar); Grid.SetRow(toolbar, 1);
        toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        tabs.Items.Add(tasksTab); tabs.Items.Add(peopleTab); toolbar.Children.Add(tabs);
        toolbar.Children.Add(commandButtons); Grid.SetColumn(commandButtons, 1);
        tabs.SelectionChanged += async (_, _) => {
            if (syncingTabs) return;
            var selected = tabs.SelectedItem;
            if (selected is null) return;
            pendingViewSelections++;
            try { await Run(() => { if (selected == peopleTab) RenderPeople(); Show(selected == peopleTab ? "people" : "tasks"); return Task.CompletedTask; }); }
            finally { if (--pendingViewSelections == 0) SyncTabs(); }
        };
        commandButtons.Children.Add(new TextBlock { Text = "状況日", VerticalAlignment = VerticalAlignment.Center, Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush") });
        commandButtons.Children.Add(statusDateHost);
        commandButtons.Children.Add(CommandDivider());
        commandButtons.Children.Add(Command("元に戻す", "PlanUndo", Symbol.Undo, async () => { if (workspace.Session is { } session) Check(await session.Undo(Today)); RenderTasks(); RenderSettings(); }));
        commandButtons.Children.Add(Command("やり直し", "PlanRedo", Symbol.Redo, async () => { if (workspace.Session is { } session) Check(await session.Redo(Today)); RenderTasks(); RenderSettings(); }));
        commandButtons.Children.Add(CommandDivider());
        unpublished.VerticalAlignment = VerticalAlignment.Center;
        unpublished.TextAlignment = TextAlignment.Center;
        unpublished.FontSize = 12;
        unpublishedBadge.Child = unpublished;
        var unpublishedGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        unpublishedGroup.Children.Add(unpublishedBadge); unpublishedGroup.Children.Add(unpublishedLabel);
        commandButtons.Children.Add(unpublishedGroup);
        commandButtons.Children.Add(Command("最新の情報に更新", "PlanRefresh", Symbol.Refresh, RefreshRemote));
        InitializePublishing();
        var problem = new StackPanel { Spacing = 4, Margin = new(12, 0, 12, 0) };
        error.Style = (Style)Application.Current.Resources["WorkspaceErrorStyle"];
        error.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => error.Visibility = error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible);
        problem.Children.Add(error);
        problem.Children.Add(publishStage);
        retrySave = Button("保存を再試行", "PlanRetrySave", async () => {
            await workspace.RetrySave(); await CommitPending();
            ClearSaveFailures(); RenderTasks(); RenderSettings();
        }, commitPending: false);
        retrySave.Visibility = Visibility.Collapsed;
        saveFailure.ActionButton = retrySave;
        refreshFailure.ActionButton = Button("再試行", "PlanRetryRefresh", RefreshRemote);
        publishFailure.ActionButton = Button("再試行", "PlanRetryPublish", () => { RenderReview(); Show("publish"); return Task.CompletedTask; });
        failures.Children.Add(refreshFailure); failures.Children.Add(publishFailure); failures.Children.Add(saveFailure); failures.Children.Add(sheetFailureHost);
        root.Children.Add(progress); Grid.SetRow(progress, 2);
        root.Children.Add(problem); Grid.SetRow(problem, 3);
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

        registered.SelectionChanged += async (_, _) => { if (!rendering && registered.SelectedItem is ProjectChoice choice) { if (projectFlyout.IsOpen) projectFlyout.Hide(); await Run(async () => { await workspace.Open(choice, OperationToken); Opened(); }); } };
        available.SelectionChanged += async (_, _) => { if (!rendering && available.SelectedItem is ProjectChoice choice) await Run(async () => { await workspace.Open(choice, OperationToken); Opened(); }); };
        settingsScroll = Id(new ScrollViewer { Content = settings, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, "PlanSettingsScroll");
        foreach (var surface in new FrameworkElement[] { connection, chooser, taskArea, peopleArea, settingsScroll, publishReview }) { surfaces.Children.Add(surface); }
        var cardContent = new Grid();
        cardContent.RowDefinitions.Add(new() { Height = GridLength.Auto });
        cardContent.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        cardContent.Children.Add(failures); cardContent.Children.Add(surfaces); Grid.SetRow(surfaces, 1);
        var card = Id(new Border { Style = (Style)Application.Current.Resources["WorkspaceCardStyle"], Child = cardContent }, "PlanWorkCard");
        root.Children.Add(card); Grid.SetRow(card, 4);
        var statusContent = new Grid();
        statusContent.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        statusContent.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        statusContent.Children.Add(statusCounts);
        foreach (var (caption, fill, stroke, dashed) in new[] {
            ("実績", "GanttTaskBrush", "GanttTaskBrush", false),
            ("残り", "GanttTaskTintBrush", "GanttTaskBrush", false),
            ("発行済みからの遅れ", "GanttLateTintBrush", "GanttLateBrush", true),
            ("非稼働日", "GanttNonWorkingBrush", "WorkspaceCardStrokeBrush", false) }) {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var swatch = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 16, Height = 10, RadiusX = 2, RadiusY = 2, VerticalAlignment = VerticalAlignment.Center,
                Fill = PlanSheetView.Brush(fill), Stroke = PlanSheetView.Brush(stroke), StrokeThickness = 1 };
            if (dashed) swatch.StrokeDashArray = new DoubleCollection { 3, 2 };
            item.Children.Add(swatch);
            item.Children.Add(new TextBlock { Text = caption, Style = (Style)Application.Current.Resources["WorkspaceMetadataStyle"] });
            legend.Children.Add(item);
        }
        statusContent.Children.Add(legend); Grid.SetColumn(legend, 1);
        statusBar.Child = statusContent;
        statusCounts.Style = (Style)Application.Current.Resources["WorkspaceMetadataStyle"];
        root.Children.Add(statusBar); Grid.SetRow(statusBar, 5);
        // A TitleBar that has been loaded keeps its header elements after it unloads, and their
        // handlers would keep this whole view and its native tree alive. Attach them only while loaded.
        Loaded += (_, _) => { WorkspaceTitleBar.LeftHeader = appGlyph; WorkspaceTitleBar.Content = projectPicker; WorkspaceTitleBar.RightHeader = settingsButton; };
        Unloaded += (_, _) => {
            WorkspaceTitleBar.LeftHeader = null; WorkspaceTitleBar.Content = null; WorkspaceTitleBar.RightHeader = null;
            if (captionRoot is { } oldRoot) oldRoot.Changed -= CaptionRootChanged;
            captionRoot = null;
            if (projectFlyout.IsOpen) projectFlyout.Hide(); closing = true; settingsGeneration++; operationCancellation?.Cancel();
        };
        Content = root;
        Show("connection");
    }
    private static Border CommandDivider() => new() {
        Width = 1, Height = 20, VerticalAlignment = VerticalAlignment.Center,
        Background = PlanSheetView.Brush("WorkspaceCardStrokeBrush")
    };
    private void CaptionRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => AlignCaptionInset();
    private void AlignCaptionInset()
    {
        if (XamlRoot is not { } xamlRoot) return;
        var inset = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(xamlRoot.ContentIslandEnvironment.AppWindowId).TitleBar.RightInset;
        // TitleBar 1.8 lays out the native pixel inset as DIPs. Correct its extra width at non-100% DPI.
        WorkspaceTitleBar.Margin = new(0, 0, -inset * (1 - 1 / xamlRoot.RasterizationScale), 0);
    }
    private void ApplyCaptionColors()
    {
        if (XamlRoot is null) return;
        var caption = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId).TitleBar;
        // XAML's forced Light theme does not set the native AppWindow caption palette.
        Windows.UI.Color Color(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;
        caption.ButtonForegroundColor = Color("WindowCaptionForeground");
        caption.ButtonHoverForegroundColor = Color("WindowCaptionButtonStrokePointerOver");
        caption.ButtonPressedForegroundColor = Color("WindowCaptionButtonStrokePressed");
        caption.ButtonInactiveForegroundColor = Color("WindowCaptionForegroundDisabled");
        caption.ButtonBackgroundColor = Color("WindowCaptionButtonBackground");
        caption.ButtonInactiveBackgroundColor = Color("WindowCaptionButtonBackground");
        caption.ButtonHoverBackgroundColor = Color("WindowCaptionButtonBackgroundPointerOver");
        caption.ButtonPressedBackgroundColor = Color("WindowCaptionButtonBackgroundPressed");
    }
    private void SyncTabs()
    {
        // Keep the requested selection until queued switches finish, so another selection still raises an event.
        if (pendingViewSelections != 0) return;
        syncingTabs = true;
        try { tabs.SelectedItem = currentPage == "tasks" ? tasksTab : currentPage == "people" ? peopleTab : null; }
        finally { syncingTabs = false; }
    }
    private void UpdateStatus()
    {
        if (workspace.Session is not { } session) return;
        var count = sheet?.Session == session ? sheet.Unpublished.TaskCount : session.Changes(Today).TaskCount;
        unpublished.Text = count > 0 ? count.ToString("N0", CultureInfo.GetCultureInfo("ja-JP")) : "未発行 0 タスク";
        AutomationProperties.SetName(unpublished, count > 0 ? $"{unpublished.Text} 未発行のタスク" : unpublished.Text);
        unpublished.Foreground = PlanSheetView.Brush(count > 0 ? "UnpublishedBadgeTextBrush" : "TextFillColorSecondaryBrush");
        unpublishedBadge.Background = PlanSheetView.Brush(count > 0 ? "SheetChangedMarkBrush" : "ControlFillColorTransparentBrush");
        unpublishedLabel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var rows = session.Document.State.Rows;
        var parents = rows.Where(r => r.Parent is not null).Select(r => r.Parent).ToHashSet();
        var requirements = rows.Count(r => parents.Contains(r.Identity));
        statusCounts.Text = string.Create(CultureInfo.GetCultureInfo("ja-JP"), $"要求事項 {requirements:N0} · タスク {rows.Length - requirements:N0}");
        // RenderTasks creates and refreshes the session's sheet before updating shell counts.
        if (sheet?.Session == session) {
            if (sheet.OverdueTasks > 0) statusCounts.Text += $" · 期限超過 {sheet.OverdueTasks.ToString("N0", CultureInfo.GetCultureInfo("ja-JP"))}";
            if (sheet.LaterTasks > 0) statusCounts.Text += $" · 予定より遅れ {sheet.LaterTasks.ToString("N0", CultureInfo.GetCultureInfo("ja-JP"))}";
        }
    }
    private static Button CommandButton(string text, string id, Symbol icon)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new FontIcon { Glyph = char.ConvertFromUtf32((int)icon), FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = Id(new Button { Content = content, Height = 32, CornerRadius = new(4), Padding = new(12, 4, 12, 4) }, id);
        AutomationProperties.SetName(button, text);
        return button;
    }
    private Button Command(string text, string id, Symbol icon, Func<Task> action)
    {
        var button = CommandButton(text, id, icon);
        if (id is "PlanUndo" or "PlanRedo" or "PlanChooseProject" or "PlanConnection")
            button.Style = (Style)Application.Current.Resources["SubtleButtonStyle"];
        button.Click += async (_, _) => { if (button.IsLoaded) { if (projectFlyout.IsOpen) projectFlyout.Hide(); await Run(action); } };
        return button;
    }
    private static T Id<T>(T control, string id) where T : DependencyObject { AutomationProperties.SetAutomationId(control, id); return control; }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private Button Button(string text, string id, Func<Task> action, bool commitPending = true)
    {
        var button = Id(new Button { Content = text }, id);
        button.Click += async (_, _) => { if (button.IsLoaded) { if (projectFlyout.IsOpen) projectFlyout.Hide(); await Run(action, commitPending); } }; return button;
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
            error.Text = "";
            using var cancellation = new CancellationTokenSource();
            operationCancellation = cancellation;
            try { if (commitPending) await CommitPending(); await action(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (cancellation.IsCancellationRequested) return;
                RefreshLists();
                if (workspace.Session is null) { taskArea.Children.Clear(); sheet = null; unpublished.Text = ""; Show("connection"); }
                if (ex is IOException or UnauthorizedAccessException) { ShowSaveFailure(ex.Message); return; }
                error.Text = ex is DiscoveryException ? "Projectを取得できません。接続先とURLを確認してください。" : ex is FormatException ? "日付は yyyy-MM-dd で入力してください。" : ex.Message;
            }
            finally { operationCancellation = null; }
        }
    }
    private static InfoBar FailureBar(string id, string title) => Id(new InfoBar {
        Title = title, Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false
    }, id);
    private void ShowSaveFailure(string message)
    {
        sheet?.ClearSaveFailure();
        error.Text = "";
        saveFailure.Message = message; saveFailure.IsOpen = true;
        retrySave.Visibility = Visibility.Visible;
    }
    private void ClearSaveFailures()
    {
        saveFailure.IsOpen = false; retrySave.Visibility = Visibility.Collapsed;
        foreach (var entry in sheets.Values) entry.ClearSaveFailure();
    }
    private async Task RefreshRemote()
    {
        refreshing = true; SetRemotePresentation();
        try {
            await workspace.Refresh(OperationToken);
            refreshFailure.IsOpen = false; ClearSaveFailures();
            if (workspace.Session is { } session && session.Document.Sync.Failures.IsEmpty && session.Document.Sync.Unverified.IsEmpty)
                publishFailure.IsOpen = false;
        }
        catch (OperationCanceledException) when (OperationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            if (workspace.Session is { } session && await session.FlushAsync() is { Succeeded: false } save)
                ShowSaveFailure(save.Error ?? ex.Message);
            else { refreshFailure.Message = ex.Message; refreshFailure.IsOpen = true; }
        }
        finally {
            refreshing = false; SetRemotePresentation();
            RenderTasks(); RenderSettings();
        }
    }
    private void SetRemotePresentation()
    {
        var busy = refreshing || publishing;
        progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in commandButtons.Children.OfType<Button>()) {
            var id = AutomationProperties.GetAutomationId(button);
            if (id is "PlanRefresh" or "PlanPublish") {
                var text = id == "PlanRefresh" ? refreshing ? "更新中…" : "最新の情報に更新" : publishing ? "発行中…" : "発行…";
                ((StackPanel)button.Content).Children.OfType<TextBlock>().Single().Text = text;
                AutomationProperties.SetName(button, text);
            }
            button.IsEnabled = !busy || publishing && id == "PlanPublish";
        }
        if (confirmPublish is not null) UpdatePublishAvailability();
        sheet?.SetRemoteBusy(busy);
        if (peopleView is not null) peopleView.IsEnabled = !busy;
        settingsScroll.IsEnabled = !busy;
        foreach (var bar in new[] { refreshFailure, publishFailure, saveFailure, sheet?.SaveFailure })
            if (bar?.ActionButton is Button retry) retry.IsEnabled = !busy;
    }
    internal string WorkDescription => $"operation={operation.Status}, closing={closing}, pendingSettings={pendingSettings.Count}, sheets=[{string.Join("; ", sheets.Values.Select(s => s.WorkDescription))}]";
    internal async Task<bool> StopAsync()
    {
        closing = true; if (projectFlyout.IsOpen) projectFlyout.Hide(); operationCancellation?.Cancel(); await operation;
        try { await CommitPending(); await workspace.Flush(); return true; }
        catch (Exception ex)
        {
            closing = false;
            publishStage.Text = ""; publishStage.Visibility = Visibility.Collapsed;
            SetPublishBusy(false); RenderTasks(); RenderReview();
            if (ex is IOException or UnauthorizedAccessException) ShowSaveFailure(ex.Message);
            else error.Text = ex.Message;
            return false;
        }
    }
    private void Show(string page)
    {
        currentPage = page; SyncTabs();
        legend.Visibility = page == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        peopleArea.Visibility = page == "people" ? Visibility.Visible : Visibility.Collapsed;
        publishReview.Visibility = page == "publish" ? Visibility.Visible : Visibility.Collapsed;
        connection.Visibility = page == "connection" ? Visibility.Visible : Visibility.Collapsed;
        chooser.Visibility = page == "chooser" ? Visibility.Visible : Visibility.Collapsed;
        taskArea.Visibility = page == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        settingsScroll.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        toolbar.Visibility = workspace.Session is null ? Visibility.Collapsed : Visibility.Visible;
        projectPicker.Visibility = workspace.Context is null ? Visibility.Collapsed : Visibility.Visible;
        settingsButton.Visibility = workspace.Session is null ? Visibility.Collapsed : Visibility.Visible;
        statusBar.Visibility = workspace.Session is null ? Visibility.Collapsed : Visibility.Visible;
        projectMetadata.Text = workspace.Selected is { } project ? $"{project.OwnerLogin} · Project {project.Number}" : "";
        title.Text = workspace.Selected?.Title ?? "GitHub Projects";
    }
    private void Opened() { ClearSaveFailures(); refreshFailure.IsOpen = publishFailure.IsOpen = false; settingsGeneration++; pendingSettings.Clear(); RefreshLists(); RenderTasks(); Show("tasks"); }
    private void RefreshLists()
    {
        rendering = true;
        try { registered.ItemsSource = workspace.Registered; registered.SelectedItem = workspace.Selected; available.ItemsSource = workspace.Available; available.SelectedItem = null; }
        finally { rendering = false; }
        RefreshProjectChecks();
    }
    private void RefreshProjectChecks()
    {
        for (var i = 0; i < registered.Items.Count; i++)
            if (registered.ContainerFromIndex(i) is ListViewItem item)
                UpdateProjectCheck(item, registered.Items[i] as ProjectChoice);
    }
    private void UpdateProjectCheck(Microsoft.UI.Xaml.Controls.Primitives.SelectorItem item, ProjectChoice? choice)
    {
        if (item.ContentTemplateRoot is Grid content && content.Children.OfType<FontIcon>().FirstOrDefault() is { } check)
            check.Opacity = choice is not null && choice.Id == workspace.Selected?.Id ? 1 : 0;
    }
    private void RenderTasks()
    {
        if (workspace.Session is not { } session) return;
        title.Text = workspace.Selected!.Title;
        if (sheet?.Session != session)
        {
            if (!sheets.TryGetValue(session.Document.Project, out var next))
            {
                next = new(session, importCsv: () => Run(() => ImportCsv(session)), changeStatusDate: value => Run(() => ChangeSettings(settings => settings with { StatusDate = value })), hostSaveFailure: true);
                next.Changed += () => { if (ReferenceEquals(sheet, next)) UpdateStatus(); };
                next.SaveFeedbackChanged += () => { if (ReferenceEquals(sheet, next)) { saveFailure.IsOpen = false; retrySave.Visibility = Visibility.Collapsed; } };
                sheets.Add(session.Document.Project, next);
            }
            taskArea.Children.Clear(); sheet = next; taskArea.Children.Add(sheet);
        }
        sheetFailureHost.Child = sheet.SaveFailure;
        statusDateHost.Child = sheet.statusDate;
        sheet.Refresh();
        UpdateStatus();
        if (!publishing && publishReview.Visibility == Visibility.Visible) RenderReview();
        if (peopleArea.Visibility == Visibility.Visible) RenderPeople();
    }
    private void RenderPeople()
    {
        if (workspace.Session is not { } session) return;
        if (peopleView?.Session != session) {
            peopleView = new(session);
            peopleView.Changed += save => { sheet?.Refresh(); UpdateStatus(); if (save.Succeeded) Check(save); };
            peopleArea.Children.Clear(); peopleArea.Children.Add(peopleView);
        } else peopleView.Refresh();
    }
    private async Task ChangeSettings(Func<ProjectPlanSettings, ProjectPlanSettings> change)
    {
        if (workspace.Session is not { } session) return;
        Check(await session.Execute(new ReplacePlanSettings(change(session.Document.State.Settings)), Today));
        RenderTasks();
    }
    private void Check(PlanSaveResult save)
    {
        if (!save.Succeeded) throw new IOException(save.Error);
        ClearSaveFailures();
    }
    private void RenderSettings()
    {
        if (workspace.Session is not { } session) return;
        rendering = true;
        try
        {
            var generation = ++settingsGeneration; pendingSettings.Clear(); settings.Children.Clear();
            settings.Children.Add(Button("戻る", "PlanSettingsBack", () => {
                if (settingsReturnPage == "people") RenderPeople();
                if (settingsReturnPage == "publish") RenderReview();
                Show(settingsReturnPage); return Task.CompletedTask;
            }));
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
            if (!session.Document.Baseline.Columns.Any(c => c.Name == "開始日指定" && c.DataType == "DATE") ||
                !session.Document.Baseline.Columns.Any(c => c.Name == "日程固定" && c.DataType == "SINGLE_SELECT"))
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
            settings.Children.Add(Id(Label($"計画対象外  Draft {session.Document.Sync.DraftCount} / Pull request {session.Document.Sync.PullRequestCount} / 参照できない項目 {session.Document.Sync.InaccessibleCount}"), "PlanExcludedCounts"));
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
        if (sheet is not null) await sheet.FlushInput();
        if (peopleView is not null) await peopleView.FlushInput();
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
            picker.FileTypeFilter.Add(purpose is "holiday" or "csv" ? ".csv" : ".json");
            return (await picker.PickSingleFileAsync().AsTask(purpose == "csv" ? OperationToken : CancellationToken.None))?.Path;
        }
    }
}
