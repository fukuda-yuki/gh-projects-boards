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
    private readonly SplitButton refreshButton = Id(new SplitButton { Height = 32 }, "PlanRefresh");
    private readonly MenuFlyoutItem resetItem = Id(new MenuFlyoutItem { Text = "GitHub の状態に戻す…", AllowFocusWhenDisabled = true }, "PlanReset");
    private readonly InfoBar refreshResult = Id(new InfoBar { Severity = InfoBarSeverity.Informational, IsClosable = true, IsOpen = false }, "PlanRefreshResult");
    private readonly InfoBar csvExportResult = Id(new InfoBar { Severity = InfoBarSeverity.Informational, IsClosable = true, IsOpen = false }, "PlanCsvExportResult");
    private readonly InfoBar refreshFailure = FailureBar("PlanRefreshFailure", "最新の情報に更新できませんでした");
    private readonly InfoBar publishFailure = FailureBar("PlanPublishFailure", "発行できませんでした");
    private readonly InfoBar saveFailure = FailureBar("PlanSaveFailure", "保存できませんでした");
    private readonly InfoBar openFailure = FailureBar("PlanOpenFailure", "計画を開けませんでした");
    private ProjectChoice? unreadableProject;
    private readonly Flyout projectFlyout = new() { AreOpenCloseAnimationsEnabled = false };
    private readonly Button projectPicker = Id(new Button(), "PlanProjectPicker");
    private readonly TextBlock projectMetadata = new();
    private readonly StackPanel legend = Id(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Margin = new(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center }, "PlanGanttLegend");
    private readonly Border statusBar = new() { Height = 28 };
    private readonly TextBlock statusCounts = Id(new TextBlock { Margin = new(20, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }, "PlanStatusCounts");
    // #260 D3: no attention counts in the status bar; while a filter is active it carries the chase list.
    private readonly Button copyByAssignee;
    private readonly SelectorBarItem tasksTab = Id(new SelectorBarItem { Text = "計画" }, "PlanShowTasks");
    private readonly SelectorBarItem summaryTab = Id(new SelectorBarItem { Text = "サマリー" }, "PlanShowSummary");
    private readonly SelectorBar tabs = new();
    private bool syncingTabs;
    private int pendingViewSelections;
    private readonly Button settingsButton;
    private readonly Button undoButton, redoButton;
    private string currentPage = "connection", settingsReturnPage = "tasks";
    private readonly StackPanel connection = new() { Spacing = 8, MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly PlanProjectChooser chooser;
    private readonly StackPanel settings = new() { Spacing = 12, MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(32, 8, 24, 32) };
    private readonly ScrollViewer settingsScroll;
    private readonly Grid settingsArea = new();
    private readonly Button settingsBack;
    private readonly ListView registered = Id(new ListView { SelectionMode = ListViewSelectionMode.Single }, "RegisteredProjects");
    private readonly Dictionary<ScopedId, PlanSheetView> sheets = [];
    private PlanSheetView? sheet;
    private readonly Grid taskArea = new();
    private readonly Grid summaryArea = new();
    private PlanSummaryView? summaryView;
    private readonly Button retrySave;
    private readonly Button conflictAction;
    private readonly TextBlock title = Id(new TextBlock { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, "OpenProjectName");
    // One form for every count, in a fixed width, so an edit that changes the count never moves the toolbar.
    // #264: the count wears the unpublished bar; a positive count uses ordinary text over the same fill.
    private readonly Border unpublishedBadge = new() { CornerRadius = new(10), Width = 128, Padding = new(6, 2, 6, 2), Background = PlanSheetView.Brush("ControlFillColorTransparentBrush") };
    private readonly Border unpublishedBar = new() { Width = 3, CornerRadius = new(1.5), Background = PlanSheetView.Brush("SheetChangedMarkBrush"), Visibility = Visibility.Collapsed };
    private readonly TextBlock unpublished = Id(new TextBlock(), "PlanUnpublished");
    private readonly TextBlock error = Id(new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }, "PlanError");
    private readonly TextBox executable = Id(new TextBox { Header = "gh.exe", Text = ConnectionViewModel.FindGh(), MinWidth = 400 }, "PlanGhPath");
    private readonly TextBox host = Id(new TextBox { Header = "接続先", Text = "github.com" }, "PlanHost");
    private readonly TextBox url = Id(new TextBox { Header = "プロジェクト URL" }, "PlanProjectUrl");
    private readonly Grid toolbar = new() { Height = 36, Margin = new(12, 0, 12, 0) };
    private readonly Border statusDateHost = new();
    private readonly StackPanel commandButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private Task operation = Task.CompletedTask;
    private CancellationTokenSource? operationCancellation;
    private CancellationToken OperationToken => operationCancellation?.Token ?? CancellationToken.None;
    private readonly List<Func<Task>> pendingSettings = [];
    private readonly List<(TextBox Input, Func<Task> Commit, Func<bool> Dirty)> settingsTextInputs = [];
    private readonly PlanInputProblem settingsInputProblem = new("SettingsInputProblem");
    private TextBox? invalidSetting;
    private string? invalidSettingMessage;
    private string? invalidSettingText;
    private string? settingsProblemMessage;
    private bool revealingSetting;
    private int settingsGeneration;
    private bool rendering, closing;
    internal IntPtr WindowHandle { get; set; }
    internal Func<string, Task<string?>>? PickFile { get; set; }
    internal Action<string>? OpenDocument { get; set; }
    private DateOnly Today => PlanClock.Today;

    internal PlanWorkspaceView(PlanWorkspace workspace, Func<string, string, GhConnectionService>? factory = null)
    {
        RequestedTheme = ElementTheme.Light;
        FontSize = 14;
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
        tasksTab.Padding = summaryTab.Padding = new Thickness(12, 5, 12, 3);
        projectPicker.MinHeight = 0; projectPicker.Height = 32;
        projectPicker.Padding = new(8, 0, 8, 0);
        projectPicker.Style = (Style)Application.Current.Resources["SubtleButtonStyle"];
        root.Style = (Style)Application.Current.Resources["PlanWorkspaceSurfaceStyle"];
        this.workspace = workspace; this.factory = factory ?? ((path, server) => new(path, server));
        for (var i = 0; i < 3; i++) root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        registered.ItemTemplate = (DataTemplate)Application.Current.Resources["PlanProjectChoiceTemplate"];
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
        projects.Children.Add(Command("プロジェクトを開く…", "PlanChooseProject", Symbol.OpenFile, () => { Show("chooser"); return Task.CompletedTask; }));
        projects.Children.Add(Command("接続…", "PlanConnection", Symbol.Link, () => { Show("connection"); return Task.CompletedTask; }));
        projectFlyout.Content = projects;
        projectFlyout.Opened += (_, _) => RefreshProjectChecks();
        projectPicker.Flyout = projectFlyout;
        var projectCaption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        projectMetadata.Style = (Style)Application.Current.Resources["WorkspaceMetadataStyle"];
        projectCaption.Children.Add(title); projectCaption.Children.Add(projectMetadata);
        projectCaption.Children.Add(new FontIcon { Glyph = "\uE70D", FontSize = 12 });
        projectPicker.Content = projectCaption;
        AutomationProperties.SetName(projectPicker, "プロジェクトを選択");
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
        var titleCommands = new StackPanel { Orientation = Orientation.Horizontal };
        titleCommands.Children.Add(CreateHelpButton()); titleCommands.Children.Add(settingsButton);
        root.Children.Add(WorkspaceTitleBar);
        root.Children.Add(toolbar); Grid.SetRow(toolbar, 1);
        toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        tabs.Items.Add(tasksTab); tabs.Items.Add(summaryTab); toolbar.Children.Add(tabs);
        toolbar.Children.Add(commandButtons); Grid.SetColumn(commandButtons, 1);
        toolbar.PreviewKeyDown += (_, args) => {
            if (args.Key != Windows.System.VirtualKey.F6 || PlanSheetView.Down(Windows.System.VirtualKey.Control)
                || PlanSheetView.Down(Windows.System.VirtualKey.Menu) || currentPage != "tasks" || sheet is null) return;
            args.Handled = true;
            if (DetailVisible && !PlanSheetView.Down(Windows.System.VirtualKey.Shift)) FocusDetail(); else sheet.FocusSelected();
        };
        tabs.SelectionChanged += async (_, _) => {
            if (syncingTabs) return;
            var selected = tabs.SelectedItem;
            if (selected is null) return;
            pendingViewSelections++;
            try { await Run(() => { if (selected == summaryTab) RenderSummary(); Show(selected == summaryTab ? "summary" : "tasks"); return Task.CompletedTask; }); }
            finally { if (--pendingViewSelections == 0) SyncTabs(); }
        };
        // #224 keeps the 計画 tab selected while the review is open, so clicking it changes no selection and raises
        // no SelectionChanged. #233 still leaves the review from that click and returns focus to the selected cell.
        tasksTab.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, args) => {
            if (syncingTabs || currentPage != "publish" || closing || busyKind is not null) return;
            args.Handled = true;
            _ = Run(() => { Show("tasks"); return Task.CompletedTask; });
        }), handledEventsToo: true);
        commandButtons.Children.Add(new TextBlock { Text = "基準日", VerticalAlignment = VerticalAlignment.Center, Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush") });
        commandButtons.Children.Add(statusDateHost);
        commandButtons.Children.Add(CommandDivider());
        commandButtons.Children.Add(undoButton = Command("元に戻す", "PlanUndo", Symbol.Undo, async () => { if (workspace.Session is { } session) Check(await session.Undo(Today)); sheet?.DismissStatus(); RenderTasks(); RenderSettings(); }));
        commandButtons.Children.Add(redoButton = Command("やり直し", "PlanRedo", Symbol.Redo, async () => { if (workspace.Session is { } session) Check(await session.Redo(Today)); sheet?.DismissStatus(); RenderTasks(); RenderSettings(); }));
        commandButtons.Children.Add(CommandDivider());
        unpublished.VerticalAlignment = VerticalAlignment.Center;
        unpublished.TextAlignment = TextAlignment.Center;
        unpublished.FontSize = 12;
        // #264: the shell row's unpublished count carries the same 3 px orange bar the marked rows wear,
        // so the count and the rows read as one state.
        var unpublishedContent = new Grid { ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new(1, GridUnitType.Star) } } };
        unpublishedBar.VerticalAlignment = VerticalAlignment.Stretch; unpublishedBar.Margin = new(0, 0, 4, 0);
        unpublishedContent.Children.Add(unpublishedBar);
        unpublishedContent.Children.Add(unpublished); Grid.SetColumn(unpublished, 1);
        unpublishedBadge.Child = unpublishedContent;
        var unpublishedGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        unpublishedGroup.Children.Add(unpublishedBadge);
        commandButtons.Children.Add(unpublishedGroup);
        InitializeRefreshCommands();
        commandButtons.Children.Add(refreshButton);
        InitializePublishing();
        var problem = new StackPanel { Spacing = 4, Margin = new(12, 0, 12, 0) };
        error.Style = (Style)Application.Current.Resources["WorkspaceErrorStyle"];
        error.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => error.Visibility = error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible);
        problem.Children.Add(error);
        retrySave = Button("保存を再試行", "PlanRetrySave", async () => {
            await workspace.RetrySave(); await CommitPending();
            ClearSaveFailures(); RenderTasks(); RenderSettings();
        }, commitPending: false);
        retrySave.Visibility = Visibility.Collapsed;
        saveFailure.ActionButton = retrySave;
        // A changed stored plan cannot be resolved by retrying, so the bar offers the way out instead (#248).
        conflictAction = Button("対処する…", "PlanResolveSaveConflict", () => { _ = ResolveSaveConflictAsync(); return Task.CompletedTask; }, commitPending: false);
        conflictAction.Visibility = Visibility.Collapsed;
        saveFailure.Content = conflictAction;
        refreshFailure.ActionButton = Button("再試行", "PlanRetryRefresh", RefreshRemote);
        publishFailure.ActionButton = Button("再試行", "PlanRetryPublish", () => { RenderReview(); Show("publish"); return Task.CompletedTask; });
        AutomationProperties.SetLiveSetting(refreshResult, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        failures.Children.Add(refreshResult);
        AutomationProperties.SetLiveSetting(csvExportResult, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        failures.Children.Add(csvExportResult);
        openFailure.ActionButton = Button("GitHub から読み直す", "PlanReadAgain", () => OpenProject(unreadableProject!, readAgain: true));
        failures.Children.Add(refreshFailure); failures.Children.Add(publishFailure); failures.Children.Add(openFailure); failures.Children.Add(saveFailure); failures.Children.Add(sheetFailureHost);
        root.Children.Add(problem); Grid.SetRow(problem, 2);
        connection.Children.Add(executable); connection.Children.Add(host);
        var address = new Expander { Header = "URLで開く", HorizontalAlignment = HorizontalAlignment.Stretch };
        var addressPanel = new StackPanel { Spacing = 8 }; addressPanel.Children.Add(url);
        addressPanel.Children.Add(Button("開く", "PlanOpenUrl", () => OpenProject(null, url.Text)));
        address.Content = addressPanel; chooser = new(workspace, choice => Run(() => OpenProject(choice)), address);
        connection.Children.Add(Button("接続", "PlanConnect", async () => {
            if (!await DiscardDetailEdit()) return;
            await chooser.Stop(); CancelDetail(clearCache: true); detailConnecting = true;
            try {
                await EndAssist(); ClearAssistMessages();
                openFailure.IsOpen = false;
                await workspace.Connect(this.factory(executable.Text, host.Text), OperationToken);
                RefreshLists(); RenderTasks();
                Show(workspace.Session is null ? "chooser" : "tasks");
            }
            finally { detailConnecting = false; UpdateDetailVisibility(); }
        }));

        registered.SelectionChanged += async (_, _) => { if (!rendering && registered.SelectedItem is ProjectChoice choice) { if (projectFlyout.IsOpen) projectFlyout.Hide(); await Run(() => OpenProject(choice)); } };
        settingsScroll = Id(new ScrollViewer { Content = settings, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, "PlanSettingsScroll");
        settingsBack = Button("計画に戻る", "PlanSettingsBack", () => {
            if (settingsReturnPage == "summary") RenderSummary();
            if (settingsReturnPage == "publish") RenderReview();
            Show(settingsReturnPage); return Task.CompletedTask;
        });
        InitializeSettingsPage();
        settingsScroll.ViewChanged += async (_, args) => {
            if (!rendering && currentPage == "settings") MarkSettingsSectionInView();
            if (rendering || args.IsIntermediate || currentPage != "settings") return;
            if (invalidSetting is { } invalid && invalid.Text == invalidSettingText) { RevealSettingsInput(); return; }
            var focused = FocusManager.GetFocusedElement(XamlRoot);
            var pending = settingsTextInputs.FirstOrDefault(p => ReferenceEquals(p.Input, focused) && p.Dirty());
            if (pending.Input is not null && !SettingIsVisible(pending.Input)) await Run(pending.Commit, commitPending: false);
        };
        foreach (var surface in new FrameworkElement[] { connection, chooser, taskArea, summaryArea, settingsArea, publishReview }) { surfaces.Children.Add(surface); }
        var cardContent = new Grid();
        // Notices sit below the surface: opening or closing one shortens the visible rows from the bottom
        // instead of pushing every row down under the pointer (#233, DESIGN M1).
        cardContent.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        cardContent.RowDefinitions.Add(new() { Height = GridLength.Auto });
        cardContent.Children.Add(surfaceHost); cardContent.Children.Add(failures); Grid.SetRow(failures, 1);
        InitializeBusy(cardContent);
        var card = Id(new Border { Style = (Style)Application.Current.Resources["WorkspaceCardStyle"], Child = cardContent }, "PlanWorkCard");
        var work = new Grid();
        work.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        work.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        work.Children.Add(card); InitializeAssist(work, titleCommands); InitializeDetail(work, titleCommands);
        root.Children.Add(work); Grid.SetRow(work, 3);
        var statusContent = new Grid();
        statusContent.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        statusContent.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        // #260: the counts take the free width and truncate with their full text in the tooltip, so the copy
        // command stays reachable however many assignees the chase list names.
        var statusLine = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        statusLine.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        statusLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        copyByAssignee = Button("担当者ごとにコピー", "PlanCopyByAssignee", CopyFilteredByAssignee, commitPending: false);
        copyByAssignee.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(copyByAssignee, "絞り込んだタスクを担当者ごとに、Issue のリンク付きでコピーします");
        statusLine.Children.Add(statusCounts);
        statusLine.Children.Add(copyByAssignee); Grid.SetColumn(copyByAssignee, 1);
        statusContent.Children.Add(statusLine);
        foreach (var (caption, fill, stroke, dashed) in new[] {
            ("実績工数", "GanttTaskBrush", "GanttTaskBrush", false),
            ("見込み残存工数", "GanttTaskTintBrush", "GanttTaskBrush", false),
            ("計画の終了日から見込終了までの遅れ", "GanttLateTintBrush", "GanttLateBrush", true),
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
        // #260: the chase list can outgrow the window; it truncates with its full text in the tooltip.
        statusCounts.TextTrimming = TextTrimming.CharacterEllipsis; statusCounts.TextWrapping = TextWrapping.NoWrap;
        root.Children.Add(statusBar); Grid.SetRow(statusBar, 4);
        // A TitleBar that has been loaded keeps its header elements after it unloads, and their
        // handlers would keep this whole view and its native tree alive. Attach them only while loaded.
        Loaded += (_, _) => { WorkspaceTitleBar.LeftHeader = appGlyph; WorkspaceTitleBar.Content = projectPicker; WorkspaceTitleBar.RightHeader = titleCommands; };
        Unloaded += (_, _) => {
            chooser.Cancel(); CancelDetail(clearCache: true); detailNavigationStarted?.TrySetCanceled(); detailWeb?.Close();
            WorkspaceTitleBar.LeftHeader = null; WorkspaceTitleBar.Content = null; WorkspaceTitleBar.RightHeader = null;
            if (captionRoot is { } oldRoot) oldRoot.Changed -= CaptionRootChanged;
            captionRoot = null;
            _ = EndAssist();
            if (projectFlyout.IsOpen) projectFlyout.Hide(); ClearSettingsInputProblem(); closing = true; settingsGeneration++; operationCancellation?.Cancel();
        };
        Content = root;
        UpdateCommandAvailability();
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
        try { tabs.SelectedItem = currentPage is "tasks" or "publish" ? tasksTab : currentPage == "summary" ? summaryTab : null; }
        finally { syncingTabs = false; }
    }
    private void UpdateStatus()
    {
        UpdateAssistSelection(); UpdateAssistProposals(); UpdateDetailSelection();
        UpdateCommandAvailability();
        ClearObsoletePublishResult();
        if (workspace.Session is not { } session) return;
        var count = sheet?.Session == session ? sheet.Unpublished.TaskCount : session.Changes(Today).TaskCount;
        unpublished.Text = $"未発行 {count.ToString("N0", CultureInfo.GetCultureInfo("ja-JP"))} タスク";
        AutomationProperties.SetName(unpublished, unpublished.Text);
        unpublished.Foreground = PlanSheetView.Brush(count > 0 ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        unpublishedBadge.Background = PlanSheetView.Brush(count > 0 ? "LayerFillColorDefaultBrush" : "ControlFillColorTransparentBrush");
        unpublishedBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(unpublishedBar, count > 0 ? "PlanUnpublishedBar" : "");
        AutomationProperties.SetName(unpublishedBar, count > 0 ? "未発行の変更" : "");
        var rows = session.Document.State.Rows;
        var parents = rows.Where(r => r.Parent is not null).Select(r => r.Parent).ToHashSet();
        var requirements = rows.Count(r => parents.Contains(r.Identity));
        // #260 D3 / #238: while a filter is on, the status bar is the chase list; without one it is the two
        // counts and nothing else. The attention counts live on the strip and the summary verdict only.
        var chase = sheet?.Session == session && sheet.FilterActive ? sheet.FilterChase : [];
        if (sheet?.Session == session && sheet.FilterActive) {
            statusCounts.Text = chase.Count == 0 ? "条件に一致するタスクはありません" : PlanChase.Line(chase);
            ToolTipService.SetToolTip(statusCounts, statusCounts.Text); AutomationProperties.SetName(statusCounts, statusCounts.Text);
            copyByAssignee.Visibility = chase.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        } else {
            statusCounts.Text = string.Create(CultureInfo.GetCultureInfo("ja-JP"), $"要求事項 {requirements:N0} · タスク {rows.Length - requirements:N0}");
            ToolTipService.SetToolTip(statusCounts, statusCounts.Text); AutomationProperties.SetName(statusCounts, statusCounts.Text);
            copyByAssignee.Visibility = Visibility.Collapsed;
        }
    }
    // 担当者ごとにコピー (#260 / #238 D7): the chase list goes through the sheet's clipboard path, and its
    // failure rule leaves the status bar unchanged on error.
    private async Task CopyFilteredByAssignee()
    {
        if (sheet is not { FilterActive: true } current || !await current.CopyFilteredByAssignee()) return;
        statusCounts.Text = "担当者ごとにコピーしました";
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
        button.Click += async (_, _) => { if (button.IsLoaded) {
            if (projectFlyout.IsOpen) projectFlyout.Hide();
            if (id is "PlanChooseProject" or "PlanConnection" && !await DiscardDetailEdit()) return;
            await Run(action);
        } };
        return button;
    }
    private static T Id<T>(T control, string id) where T : DependencyObject { AutomationProperties.SetAutomationId(control, id); return control; }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private static TextBlock SettingsHeading(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private Button Button(string text, string id, Func<Task> action, bool commitPending = true)
    {
        var button = Id(new Button { Content = text }, id);
        button.Click += async (_, _) => { if (button.IsLoaded) { if (projectFlyout.IsOpen) projectFlyout.Hide(); await Run(action, commitPending); } }; return button;
    }
    private Task Run(Func<Task> action, bool commitPending = true)
    {
        if (closing || busyKind is not null) return Task.CompletedTask;
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
                if (ex is OperationCanceledException && cancellation.IsCancellationRequested) return;
                RefreshLists();
                if (workspace.Session is null) { taskArea.Children.Clear(); sheet = null; unpublished.Text = ""; Show(ex is PlanUnreadableException && workspace.Context is not null ? "chooser" : "connection"); }
                if (ex is PlanUnreadableException unreadable) { ShowUnreadable(unreadable); return; }
                if (ex is IOException or UnauthorizedAccessException) { ShowSaveFailure(ex.Message); return; }
                error.Text = ex is DiscoveryException ? "プロジェクトを取得できません。接続先とURLを確認してください。" : ex is FormatException ? "日付は yyyy-MM-dd または yyyy/M/d で入力してください。" : ex.Message;
            }
            finally { operationCancellation = null; UpdateCommandAvailability(); }
        }
    }
    private static InfoBar FailureBar(string id, string title) => Id(new InfoBar {
        Title = title, Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false
    }, id);
    private void ShowUnreadable(PlanUnreadableException unreadable)
    {
        unreadableProject = unreadable.Project;
        openFailure.Message = $"このPCに保存されている「{unreadable.Project.Title}」の計画を読み込めません。以前の版のアプリで保存されたか、ファイルが壊れています。"
            + "「GitHub から読み直す」を選ぶと、保存されている計画を別名で残し、GitHub の内容からプロジェクトを開き直します。このPCの未発行の変更は引き継がれません。"
            + Environment.NewLine + "詳細: " + unreadable.Message;
        openFailure.IsOpen = true;
    }
    private void ShowSaveFailure(string message, bool conflict = false)
    {
        sheet?.ClearSaveFailure();
        error.Text = "";
        SaveConflictPending = conflict;
        saveFailure.Message = message; saveFailure.IsOpen = true;
        retrySave.Visibility = conflict ? Visibility.Collapsed : Visibility.Visible;
        conflictAction.Visibility = conflict ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ClearSaveFailures()
    {
        SaveConflictPending = false;
        saveFailure.IsOpen = false; retrySave.Visibility = Visibility.Collapsed; conflictAction.Visibility = Visibility.Collapsed;
        foreach (var entry in sheets.Values) entry.ClearSaveFailure();
    }
    private Task RefreshRemote() => ReadRemote(false);
    private async Task ReadRemote(bool reset)
    {
        ClearPublishResult();
        var reporter = BeginBusy(reset ? "reset" : "refresh", reset ? "GitHub の状態に戻しています" : "最新の情報を取得しています");
        var outcome = reset ? "GitHub の状態に戻す操作を中止しました" : "更新を中止しました";
        try {
            // #238: a complete read reports itself at the right end of the attention strip only; no result bar
            // opens, so the rows keep their height and position.
            var result = reset ? await workspace.Reset(OperationToken, reporter) : await workspace.Refresh(OperationToken, reporter);
            if (result is not null && reset) ShowResetResult(result.StatusDate);
            outcome = reset ? "GitHub の状態に戻しました" : "最新の情報に更新しました";
            refreshFailure.IsOpen = false; ClearSaveFailures();
            if (workspace.Session is { } session && session.Document.Sync.Failures.IsEmpty && session.Document.Sync.Unverified.IsEmpty)
                publishFailure.IsOpen = false;
        }
        catch (OperationCanceledException) when (OperationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            outcome = (reset ? "GitHub の状態に戻せませんでした。" : "最新の情報に更新できませんでした。") + ex.Message;
            if (workspace.Session is { } session && await session.FlushAsync() is { Succeeded: false } save)
                ShowSaveFailure(save.Error ?? ex.Message);
            else {
                refreshFailure.Title = reset ? "GitHub の状態に戻せませんでした" : "最新の情報に更新できませんでした";
                refreshFailure.ActionButton = Button("再試行", "PlanRetryRefresh", reset ? ConfirmReset : RefreshRemote);
                refreshFailure.Message = ex.Message; refreshFailure.IsOpen = true;
            }
        }
        finally {
            EndBusy(outcome);
            RenderTasks(); RenderSettings();
        }
    }
    private bool showingRecalculation;
    // A settings change reschedules the plan, so it keeps its own bar and reports the moved tasks. A refresh
    // opens no bar: its result is the strip's right end (#238).
    private void ShowRecalculationResult(PlanRefreshResult result)
    {
        showingRecalculation = true;
        var culture = CultureInfo.GetCultureInfo("ja-JP");
        refreshResult.ActionButton = null; refreshResult.Content = null;
        refreshResult.Title = "日程を再計算しました";
        refreshResult.Message = result.ChangedTasks.Count > 0
            ? "日程が変わったタスク " + result.ChangedTasks.Count.ToString("N0", culture) + "。発行すると GitHub に反映します。"
            : "日程は変わっていません。";
        if (result.ChangedTasks.Count > 0)
            refreshResult.ActionButton = Button("変更したタスクだけ表示", "PlanRefreshChangedFilter", () => {
                Show("tasks"); sheet?.FilterTasks(result.ChangedTasks, "日程変更"); return Task.CompletedTask;
            });
        refreshResult.IsOpen = true;
    }
    private void CloseRefreshResult()
    {
        showingRecalculation = false;
        refreshResult.IsOpen = false;
    }
    private void UpdateCommandAvailability()
    {
        foreach (var button in commandButtons.Children.OfType<Button>())
            // #224: an Undo or a refresh would change the plan under the open review, so both wait until it closes.
            button.IsEnabled = busyKind is null && (button == undoButton ? workspace.Session?.UndoCount > 0 && currentPage != "publish"
                : button == redoButton ? workspace.Session?.RedoCount > 0 && currentPage != "publish" : true);
    }
    private void SetRemotePresentation()
    {
        var busy = busyKind is not null;
        UpdateCommandAvailability();
        refreshButton.IsEnabled = !busy && currentPage != "publish";
        UpdateResetAvailability();
        projectPicker.IsEnabled = settingsButton.IsEnabled = !busy;
        tasksTab.IsEnabled = summaryTab.IsEnabled = !busy;
        surfaceHost.IsEnabled = !busy;
        statusDateHost.IsHitTestVisible = !busy;
        if (sheet is not null) sheet.statusDate.IsEnabled = !busy;
        if (confirmPublish is not null) UpdatePublishAvailability();
        sheet?.SetRemoteBusy(busy);
        UpdateDetailEditCommands();
        if (summaryView is not null) summaryView.IsEnabled = !busy;
        settingsScroll.IsEnabled = settingsBack.IsEnabled = settingsNav.IsEnabled = !busy;
        foreach (var bar in new[] { refreshFailure, publishFailure, openFailure, saveFailure, sheet?.SaveFailure })
            if (bar?.ActionButton is Button retry) retry.IsEnabled = !busy;
    }
    internal string WorkDescription => $"operation={operation.Status}, closing={closing}, pendingSettings={pendingSettings.Count}, sheets=[{string.Join("; ", sheets.Values.Select(s => s.WorkDescription))}]";
    // #248: a save refused because another window stored this plan is not retryable; the two escapes
    // (reload and reapply, or save as a copy) both keep this window's edit, and closing always has a choice.
    internal bool SaveConflictPending { get; private set; }
    private async Task ReapplyStoredPlanAsync()
    {
        if (workspace.Session is not { } session) return;
        var result = await session.ReapplyStoredAsync(Today);
        if (result.Succeeded) { ClearSaveFailures(); RenderTasks(); RenderSettings(); return; }
        ShowSaveFailure(result.Error ?? "読み直した計画を保存できませんでした。", conflict: true);
    }
    private async Task SavePlanCopyAsync()
    {
        if (workspace.Session is not { } session) return;
        if (await SelectFile("plan-copy") is not { } path) return;
        var result = await session.SaveCopyAsync(path);
        if (result.Succeeded) { ClearSaveFailures(); error.Text = "コピーとして保存しました: " + path; return; }
        ShowSaveFailure(result.Error ?? "コピーとして保存できませんでした。", conflict: true);
    }
    private async Task ResolveSaveConflictAsync()
    {
        switch (await ShowSaveConflictDialogAsync(closing: false))
        {
            case SaveConflictChoice.Reapply: await ReapplyAndReportAsync(); break;
            case SaveConflictChoice.SaveCopy: await CopyAndReportAsync(); break;
        }
    }
    private async Task<bool> ReapplyAndReportAsync()
    {
        await ReapplyStoredPlanAsync();
        return !SaveConflictPending;
    }
    private async Task<bool> CopyAndReportAsync()
    {
        await SavePlanCopyAsync();
        return !SaveConflictPending;
    }
    private enum SaveConflictChoice { Reapply, SaveCopy, Discard, Stay }
    private async Task<SaveConflictChoice> ShowSaveConflictDialogAsync(bool closing)
    {
        var discard = false;
        var dialog = Id(new ContentDialog { XamlRoot = XamlRoot, Title = "別のウィンドウがこの計画を保存しました",
            PrimaryButtonText = closing ? "読み直して適用して閉じる" : "読み直して編集を適用",
            SecondaryButtonText = closing ? "コピーとして保存して閉じる" : "コピーとして保存…",
            CloseButtonText = closing ? "閉じない" : "あとで",
            DefaultButton = ContentDialogButton.Primary }, "PlanSaveConflict");
        var discardButton = Button(closing ? "編集を破棄して閉じる" : "編集を破棄", "PlanSaveConflictDiscard", () => { discard = true; dialog.Hide(); return Task.CompletedTask; }, commitPending: false);
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = "このウィンドウの編集は保持しています。保存されている計画を読み直して編集を適用するか、このウィンドウの内容をコピーとして保存してください。"
                + (closing ? Environment.NewLine + "編集を破棄して閉じることもできます。" : "") });
        // Discarding is only a way out of closing; while the window stays open, nothing is thrown away.
        if (closing) panel.Children.Add(discardButton);
        dialog.Content = panel;
        var result = await dialog.ShowAsync();
        if (discard) return SaveConflictChoice.Discard;
        return result switch { ContentDialogResult.Primary => SaveConflictChoice.Reapply, ContentDialogResult.Secondary => SaveConflictChoice.SaveCopy, _ => SaveConflictChoice.Stay };
    }
    // Called by the window when a close could not flush. True means the window may close.
    internal async Task<bool> ResolveCloseConflictAsync()
    {
        if (!SaveConflictPending)
        {
            // A retryable failure keeps its retry path, and the window can still be closed by choice.
            var discard = false;
            var dialog = Id(new ContentDialog { XamlRoot = XamlRoot, Title = "保存できませんでした",
                PrimaryButtonText = "保存を再試行", CloseButtonText = "閉じない", DefaultButton = ContentDialogButton.Primary }, "PlanCloseFailure");
            var discardButton = Button("編集を破棄して閉じる", "PlanCloseDiscard", () => { discard = true; dialog.Hide(); return Task.CompletedTask; }, commitPending: false);
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap,
                Text = (saveFailure.Message.Length > 0 ? saveFailure.Message : "保存できませんでした。") + Environment.NewLine + "編集を破棄して閉じることもできます。" });
            panel.Children.Add(discardButton);
            dialog.Content = panel;
            var result = await dialog.ShowAsync();
            if (discard) return true;
            if (result != ContentDialogResult.Primary) return false;
            try { await workspace.RetrySave(); await CommitPending(); ClearSaveFailures(); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowSaveFailure(ex.Message, ex is PlanSaveFailedException { Result.Failure: PlanSaveFailure.ChangedFile }); return false; }
        }
        var choice = await ShowSaveConflictDialogAsync(closing: true);
        return choice switch {
            SaveConflictChoice.Reapply => await ReapplyAndReportAsync(),
            SaveConflictChoice.SaveCopy => await CopyAndReportAsync(),
            SaveConflictChoice.Discard => true,
            _ => false };
    }
    internal async Task<bool> StopAsync()
    {
        if (!await DiscardDetailEdit()) return false;
        closing = true; if (projectFlyout.IsOpen) projectFlyout.Hide();
        CancelDetail(clearCache: true);
        // Cancel GitHub work before the assist's bounded shutdown so no further publish batches are sent meanwhile.
        operationCancellation?.Cancel();
        detailNavigationStarted?.TrySetCanceled();
        await chooser.Stop();
        await detailWork;
        await EndAssist();
        closing = true; await operation;
        try { await CommitPending(); await workspace.Flush(); return true; }
        catch (Exception ex)
        {
            closing = false;
            SetPublishBusy(false); RenderTasks(); RenderReview();
            if (ex is IOException or UnauthorizedAccessException) ShowSaveFailure(ex.Message, ex is PlanSaveFailedException { Result.Failure: PlanSaveFailure.ChangedFile });
            else error.Text = ex.Message;
            return false;
        }
    }
    private void Show(string page)
    {
        if (page != "tasks") sheet?.CancelPendingFilter();
        // The review replaced the sheet and took its focused cell with it; every way back to the plan gives the
        // keyboard back to the selected cell, as a closing dialog does (#233, DESIGN A2).
        if (currentPage == "publish" && page == "tasks") sheet?.FocusSelected();
        currentPage = page; SyncTabs(); UpdateAssistVisibility(); UpdateDetailVisibility(); SetRemotePresentation();
        openPublish.Style = page == "publish" ? null : (Style)Application.Current.Resources["AccentButtonStyle"];
        legend.Visibility = page == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        summaryArea.Visibility = page == "summary" ? Visibility.Visible : Visibility.Collapsed;
        publishReview.Visibility = page == "publish" ? Visibility.Visible : Visibility.Collapsed;
        connection.Visibility = page == "connection" ? Visibility.Visible : Visibility.Collapsed;
        chooser.Visibility = page == "chooser" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "chooser") chooser.Activate();
        taskArea.Visibility = page == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        settingsArea.Visibility = settingsScroll.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        // The section list is a NavigationView: its pane header and content are realized during the first layout
        // pass after the page becomes visible, so lay it out here. A command that opens settings then completes
        // with its controls loaded instead of one layout pass later (#251).
        if (page == "settings") settingsNav.UpdateLayout();
        toolbar.Visibility = workspace.Session is null ? Visibility.Collapsed : Visibility.Visible;
        projectPicker.Visibility = workspace.Context is null ? Visibility.Collapsed : Visibility.Visible;
        settingsButton.Visibility = workspace.Session is null ? Visibility.Collapsed : Visibility.Visible;
        statusBar.Visibility = workspace.Session is null ? Visibility.Collapsed : Visibility.Visible;
        projectMetadata.Text = workspace.Selected is { } project ? $"{project.OwnerLogin} · プロジェクト {project.Number}" : "";
        title.Text = workspace.Selected?.Title ?? "GitHub Projects";
    }
    private void Opened() { ClearPublishResult(); ClearSaveFailures(); refreshFailure.IsOpen = publishFailure.IsOpen = false; settingsGeneration++; pendingSettings.Clear(); settingsTextInputs.Clear(); ClearSettingsInputProblem(); RefreshLists(); RenderTasks(); Show("tasks"); }
    private void RefreshLists()
    {
        rendering = true;
        try { registered.ItemsSource = workspace.Registered; registered.SelectedItem = workspace.Selected; chooser.Refresh(); }
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
        if (assistSession is not null && (assistSession != workspace.Session || !ReferenceEquals(assistContext, workspace.Context))) {
            _ = EndAssist(); ClearAssistMessages();
        }
        if (workspace.Session is not { } session) return;
        title.Text = workspace.Selected!.Title;
        if (sheet?.Session != session)
        {
            if (!sheets.TryGetValue(session.Document.Project, out var next))
            {
                next = new(session, importCsv: () => Run(() => ImportCsv(session)), exportCsv: () => Run(() => ExportCsv(session)), changeStatusDate: value => Run(() => ChangeSettings(settings => settings with { StatusDate = value })), hostSaveFailure: true, openColumns: () => Run(() => { OpenSettingsPage("columns", "tasks"); return Task.CompletedTask; }),
                    reapplyStoredPlan: ReapplyStoredPlanAsync, savePlanCopy: SavePlanCopyAsync,
                    // #253: verify a typed login on demand for the row's repository through the read-only lease.
                    resolveAssignee: (repository, login, token) => ReferenceEquals(workspace.Session, session)
                        ? workspace.ResolveAssigneeAsync(repository, login, token)
                        : throw new InvalidOperationException("プロジェクトが変更されました。"));
                next.CommandRowFocusRequested += () => {
                    if (!ReferenceEquals(sheet, next)) return;
                    if (DetailVisible && PlanSheetView.Down(Windows.System.VirtualKey.Shift)) FocusDetail(); else FocusCommandRow();
                };
                next.PreservePendingFocus = IsDetailFocus;
                next.SetDetailChecked(detailToggle.IsChecked == true);
                next.DetailToggleRequested += async value => { if (ReferenceEquals(sheet, next)) await RequestDetailToggle(value); };
                next.Changed += () => { if (ReferenceEquals(sheet, next)) UpdateStatus(); };
                next.SelectionChanged += () => { if (ReferenceEquals(sheet, next)) { UpdateAssistSelection(); UpdateDetailSelection(); } };
                next.SaveFeedbackChanged += result => { if (ReferenceEquals(sheet, next)) { UpdateCommandAvailability(); ShowPlanningResult(result); saveFailure.IsOpen = false; retrySave.Visibility = Visibility.Collapsed; } };
                sheets.Add(session.Document.Project, next);
            }
            taskArea.Children.Clear(); sheet = next; taskArea.Children.Add(sheet);
            UpdateSheetSidePane();
        }
        sheet.Marks = workspace.Display.Marks;
        sheetFailureHost.Child = sheet.SaveFailure;
        statusDateHost.Child = sheet.statusDate;
        sheet.Refresh();
        UpdateStatus();
        if (!publishing && publishReview.Visibility == Visibility.Visible) RenderReview();
        if (summaryArea.Visibility == Visibility.Visible) RenderSummary();
    }
    private void RenderSummary()
    {
        if (workspace.Session is not { } session || sheet is null) return;
        sheet.Refresh();
        if (summaryView?.Session != session) {
            summaryView = new(session, (ids, name) => {
                sheet.FilterTasks(ids, name); Show("tasks");
                if (sheet.RowIds.Count > 0) sheet.SelectAndFocus(sheet.RowIds[0], PlanField.Title, false);
            }, section => OpenSettingsPage(section, "summary"), toggle => {
                // The verdict's count buttons press the strip toggle and nothing else (#260 3).
                Show("tasks"); sheet.FilterToggle(toggle);
            }, identity => {
                // A summary person link uses the shared person filter (#230/#227), not an exact-set chip.
                Show("tasks"); sheet.FilterPerson(identity);
            });
            summaryArea.Children.Clear(); summaryArea.Children.Add(summaryView);
        }
        summaryView.Refresh(sheet.Schedule, sheet.StatusDate);
    }
    private async Task ChangeSettings(Func<ProjectPlanSettings, ProjectPlanSettings> change)
    {
        if (workspace.Session is not { } session) return;
        // Execute accepts the operation before persisting it; show the new history while the save runs.
        var save = session.Execute(new ReplacePlanSettings(change(session.Document.State.Settings)), Today);
        UpdateCommandAvailability();
        Check(await save);
        RenderTasks();
    }
    private void ShowPlanningResult(PlanSaveResult save)
    {
        if (save.Recalculation is { } result) ShowRecalculationResult(result);
    }
    private void Check(PlanSaveResult save)
    {
        UpdateCommandAvailability();
        ShowPlanningResult(save);
        if (!save.Succeeded) throw new IOException(save.Error);
        ClearSaveFailures();
    }
    private Task ChangePerson(PlanResource fallback, Func<PlanResource, PlanResource> change) => ChangeSettings(s =>
    {
        var person = s.People.SingleOrDefault(p => p.Identity == fallback.Identity) ?? fallback;
        return s with { People = s.People.Any(p => p.Identity == person.Identity) ? s.People.Select(p => p.Identity == person.Identity ? change(person) : p).ToImmutableArray() : s.People.Add(change(person)) };
    });
    private bool SettingIsVisible(TextBox input)
    {
        if (!input.IsLoaded || currentPage != "settings") return false;
        var top = input.TransformToVisual(settingsScroll).TransformPoint(new()).Y;
        return top >= 0 && top + input.ActualHeight <= settingsScroll.ActualHeight;
    }
    private void ClearSettingsInputProblem()
    {
        ClearSettingsProblem();
        var input = invalidSetting; var message = invalidSettingMessage;
        // Invalidate recovery before closing the popup can cause focus/layout callbacks.
        invalidSetting = null; invalidSettingMessage = null; invalidSettingText = null;
        if (input is not null) AutomationProperties.SetHelpText(input, "");
        if (message is not null && error.Text == message) error.Text = "";
        settingsInputProblem.Close();
    }
    // A control that cannot keep rejected text (a date picker, a number box) reports its rejection beside itself
    // and returns to the accepted value; the text editors keep their own editable rejected text (#251).
    private void ShowSettingsProblem(FrameworkElement target, string message)
    {
        settingsProblemMessage = message;
        settingsInputProblem.Show(target, message);
        error.Text = message;
    }
    private void ClearSettingsProblem()
    {
        var message = settingsProblemMessage;
        settingsProblemMessage = null;
        settingsInputProblem.Close();
        if (message is not null && error.Text == message) error.Text = "";
    }
    private void RevealSettingsInput()
    {
        if (revealingSetting || currentPage != "settings" || invalidSetting is not { IsLoaded: true } input || input.Text != invalidSettingText || invalidSettingMessage is not { } message) return;
        var rejectedText = invalidSettingText;
        revealingSetting = true;
        input.Focus(FocusState.Programmatic);
        PositionProblem();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
            revealingSetting = false;
            if (ReferenceEquals(invalidSetting, input) && input.IsLoaded && input.Text == rejectedText) {
                input.Focus(FocusState.Programmatic); PositionProblem();
            }
        });
        void PositionProblem()
        {
            if (!ReferenceEquals(invalidSetting, input) || invalidSettingMessage != message
                || input.Text != rejectedText || currentPage != "settings") return;
            // Target the settings viewport explicitly; a TextBox has its own internal scroll viewer.
            // Focus and the error surface can also change layout before the next ViewChanged.
            if (!SettingIsVisible(input)) {
                settingsInputProblem.Close();
                var top = input.TransformToVisual(settingsScroll).TransformPoint(new()).Y;
                var offset = settingsScroll.VerticalOffset + top - Math.Max(0, (settingsScroll.ActualHeight - input.ActualHeight) / 2);
                settingsScroll.ChangeView(null, Math.Clamp(offset, 0, settingsScroll.ScrollableHeight), null, true);
            } else settingsInputProblem.Show(input, message);
        }
    }
    private TextBox TextSetting(string label, string id, string value, Func<string, Task> apply, Func<string>? display = null)
    {
        var input = Id(new TextBox { Header = label, Text = value, MinWidth = 280 }, id);
        var accepted = value;
        var generation = settingsGeneration;
        var composing = false; var endedThisTurn = false;
        async Task Commit()
        {
            if (generation != settingsGeneration) return;
            if (composing) { input.Focus(FocusState.Programmatic); throw new InvalidOperationException("入力中の変換を確定してください。"); }
            if (input.Text == accepted) {
                if (ReferenceEquals(invalidSetting, input)) ClearSettingsInputProblem();
                return;
            }
            var text = input.Text;
            try { await apply(text); }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException) {
                var name = AutomationProperties.GetName(input);
                var reason = ex is FormatException ? "日付は yyyy-MM-dd または yyyy/M/d で入力してください。" : ex.Message;
                invalidSetting = input; invalidSettingText = text; invalidSettingMessage = (name.Length > 0 ? name : label) + ": " + reason;
                AutomationProperties.SetHelpText(input, invalidSettingMessage); RevealSettingsInput();
                throw new InvalidOperationException(invalidSettingMessage, ex);
            }
            accepted = display?.Invoke() ?? text;
            if (display is not null && input.Text == text) input.Text = accepted;
            AutomationProperties.SetHelpText(input, "");
            if (ReferenceEquals(invalidSetting, input)) ClearSettingsInputProblem();
        }
        pendingSettings.Add(Commit);
        settingsTextInputs.Add((input, Commit, () => (input.Text != accepted || ReferenceEquals(invalidSetting, input)) && !composing && !endedThisTurn));
        input.TextCompositionStarted += (_, _) => composing = true;
        input.TextCompositionEnded += (_, _) => {
            composing = false; endedThisTurn = true;
            // IME confirmation can precede the routed Enter/Escape in this dispatcher turn.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => endedThisTurn = false);
        };
        input.KeyDown += async (_, args) => {
            if (composing || endedThisTurn) return;
            if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; await Run(Commit, commitPending: false); }
            else if (args.Key == Windows.System.VirtualKey.Escape) {
                args.Handled = true; input.Text = accepted; AutomationProperties.SetHelpText(input, "");
                if (ReferenceEquals(invalidSetting, input)) { ClearSettingsInputProblem(); error.Text = ""; }
            }
        };
        input.LostFocus += async (_, _) => {
            // An unchanged rejection is already reported; retrying it here steals focus from the Project picker.
            // Navigation commands still validate it through CommitPending.
            if (!composing && !endedThisTurn && !rendering && generation == settingsGeneration
                && !(ReferenceEquals(invalidSetting, input) && input.Text == invalidSettingText)
                && (input.Text != accepted || ReferenceEquals(invalidSetting, input))) await Run(Commit, commitPending: false);
        };
        return input;
    }
    private async Task CommitPending()
    {
        if (sheet is not null) await sheet.FlushInput();
        foreach (var commit in pendingSettings.ToArray()) await commit();
    }
    private async Task<string?> SelectFile(string purpose)
    {
        if (PickFile is { } pick) return await pick(purpose);
        if (purpose is "export" or "csv-export" or "plan-copy")
        {
            var csv = purpose == "csv-export";
            var copy = purpose == "plan-copy";
            var title = string.Concat((workspace.Selected?.Title ?? "Project").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd(' ', '.');
            var picker = new FileSavePicker { SuggestedFileName = csv ? (title.Length == 0 ? "Project" : title) + ".csv" : copy ? (title.Length == 0 ? "Project" : title) + "-copy" : "project-settings" };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            picker.FileTypeChoices.Add(csv ? "CSV" : "JSON", [csv ? ".csv" : ".json"]);
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
internal sealed record PlanFieldChoice(PlanColumnDefinition Definition, string Label);

