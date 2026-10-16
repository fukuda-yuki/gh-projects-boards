using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace GhProjectsBoards.App;

internal sealed class PlanProjectChooser : Grid
{
    private readonly PlanWorkspace workspace;
    private readonly Func<ProjectChoice, Task> open;
    private readonly ListView recent = List("PlanRecentProjects", "最近開いたプロジェクト", 0);
    private readonly ListView owners = List("PlanProjectOwners", "組織", 1);
    private readonly ListView repositories = List("PlanProjectRepositories", "リポジトリ", 2);
    private readonly ListView projects = List("AvailableProjects", "プロジェクト", 6);
    private readonly TextBox search = Named(new TextBox { PlaceholderText = "名前または番号で絞り込み", TabIndex = 3, Width = 320 }, "PlanProjectSearch", "名前または番号で絞り込み");
    private readonly CheckBox closed = Named(new CheckBox { Content = "終了済みも表示（0）", TabIndex = 4, MinWidth = 0 }, "PlanProjectShowClosed", "終了済みも表示（0）");
    private readonly Button reload = Named(new Button { Content = new SymbolIcon(Symbol.Refresh), TabIndex = 5 }, "PlanProjectsReload", "一覧を更新");
    private readonly Button retry = Named(new Button { Content = "再試行", Visibility = Visibility.Collapsed }, "PlanProjectsRetry", "再試行");
    private readonly TextBlock status = Named(new TextBlock { TextWrapping = TextWrapping.Wrap }, "PlanProjectListingStatus", "プロジェクトの取得状況");
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly StackPanel recentPanel = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private IReadOnlyList<ProjectListing> listings = [];
    private ConnectionContext? context;
    private CancellationTokenSource? cancellation;
    private Task loading = Task.CompletedTask;
    private bool syncing, loaded;
    private int generation;

    internal PlanProjectChooser(PlanWorkspace workspace, Func<ProjectChoice, Task> open, Expander address)
    {
        this.workspace = workspace; this.open = open;
        RowSpacing = 12;
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        Children.Add(new TextBlock { Text = "プロジェクトを開く", Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        recentPanel.Children.Add(Heading("最近開いたプロジェクト")); recentPanel.Children.Add(recent);
        recent.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("""<ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><ItemsStackPanel Orientation="Horizontal"/></ItemsPanelTemplate>""");
        ScrollViewer.SetHorizontalScrollBarVisibility(recent, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollMode(recent, ScrollMode.Enabled);
        ScrollViewer.SetVerticalScrollBarVisibility(recent, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(recent, ScrollMode.Disabled);
        Children.Add(recentPanel); SetRow(recentPanel, 1);
        var columns = new Grid { ColumnSpacing = 16 };
        columns.ColumnDefinitions.Add(new() { Width = new(220) }); columns.ColumnDefinitions.Add(new() { Width = new(300) });
        columns.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var ownerColumn = Column("組織", owners); var repositoryColumn = Column("リポジトリ", repositories);
        var projectColumn = new Grid { RowSpacing = 8 };
        for (var i = 0; i < 3; i++) projectColumn.RowDefinitions.Add(new() { Height = GridLength.Auto });
        projectColumn.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        projectColumn.Children.Add(Heading("プロジェクト"));
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        commands.Children.Add(search); commands.Children.Add(closed); commands.Children.Add(reload); reload.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(reload, "一覧を更新"); projectColumn.Children.Add(commands); SetRow(commands, 1);
        var feedback = new StackPanel { Spacing = 8 }; feedback.Children.Add(progress); feedback.Children.Add(status); feedback.Children.Add(retry);
        projectColumn.Children.Add(feedback); SetRow(feedback, 2);
        projectColumn.Children.Add(projects); SetRow(projects, 3);
        // On wide screens a full-width list separates a title from its metadata; keep the column readable.
        projectColumn.MaxWidth = 860; projectColumn.HorizontalAlignment = HorizontalAlignment.Left;
        columns.Children.Add(ownerColumn); columns.Children.Add(repositoryColumn); SetColumn(repositoryColumn, 1);
        columns.Children.Add(projectColumn); SetColumn(projectColumn, 2);
        Children.Add(columns); SetRow(columns, 2);
        address.TabIndex = 7; AutomationProperties.SetName(address, "URLで開く"); Children.Add(address); SetRow(address, 3);
        repositories.SingleSelectionFollowsFocus = true;
        owners.ContainerContentChanging += (_, e) => Row(e, item => Text(((OwnerChoice)item).Login + (((OwnerChoice)item).Type == "User" ? "（個人）" : "")));
        repositories.ContainerContentChanging += (_, e) => Row(e, item => {
            var choice = (ProjectRepositoryChoice)item;
            var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(Text(choice.DisplayText)); var count = Text(choice.Count.ToString()); count.Margin = new(12, 0, 0, 0); row.Children.Add(count); SetColumn(count, 1); return row;
        });
        recent.ContainerContentChanging += (_, e) => Row(e, item => {
            var choice = (ProjectChoice)item; var row = new StackPanel { Width = 280, Spacing = 2, Padding = new(0, 6, 0, 6) };
            var title = Text(choice.Title); title.MaxLines = 2; title.TextTrimming = TextTrimming.CharacterEllipsis; ToolTipService.SetToolTip(title, choice.Title); row.Children.Add(title); row.Children.Add(Metadata($"{choice.OwnerLogin} · プロジェクト {choice.Number}")); return row;
        });
        projects.ContainerContentChanging += (_, e) => Row(e, item => {
            var choice = (ProjectChoice)item; var listing = listings.First(p => p.Choice.Id == choice.Id);
            var row = new StackPanel { Spacing = 2, Padding = new(0, 6, 0, 6) };
            var heading = new Grid { ColumnSpacing = listing.Closed ? 8 : 0 }; heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            if (listing.Closed) heading.Children.Add(new Border { BorderThickness = new(1), CornerRadius = new(4), Padding = new(6, 0, 6, 1), VerticalAlignment = VerticalAlignment.Top,
                BorderBrush = PlanSheetView.Brush("TextFillColorSecondaryBrush"), Child = Metadata("終了") });
            var title = Text(choice.Title); heading.Children.Add(title); SetColumn(title, 1); row.Children.Add(heading);
            var names = listing.Repositories.Select(r => ProjectChooserFilter.RepositoryName(r, choice.OwnerLogin)).ToArray();
            var repositoriesText = names.Length == 0 ? "" : " · " + string.Join(", ", names.Take(2)) + (names.Length > 2 ? $" +{names.Length - 2}" : "");
            row.Children.Add(Metadata($"#{choice.Number} · 更新 {listing.UpdatedAt.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture)}{repositoriesText}"));
            return row;
        });
        owners.SelectionChanged += (_, _) => { if (!syncing) StartLoad(); };
        repositories.SelectionChanged += (_, _) => { if (!syncing && loaded) Filter(); };
        search.TextChanged += (_, _) => { if (loaded) Filter(); };
        RoutedEventHandler closedChanged = (_, _) => { if (loaded) { RepositoryEntries(); Filter(); } };
        closed.Checked += closedChanged; closed.Unchecked += closedChanged;
        reload.Click += (_, _) => StartLoad(reload: true, resetRepository: false);
        retry.Click += (_, _) => StartLoad(reload: true, resetRepository: false);
        projects.SelectionChanged += async (_, _) => { if (!syncing && projects.SelectedItem is ProjectChoice choice) await Open(choice); };
        recent.SelectionChanged += async (_, _) => { if (!syncing && recent.SelectedItem is ProjectChoice choice) await Open(choice); };
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetName(progress, "プロジェクトを読み込み中");
    }
    internal void Refresh()
    {
        syncing = true;
        try { recent.ItemsSource = workspace.Registered.Take(5).ToArray(); recent.SelectedItem = null; projects.SelectedItem = null;
            recentPanel.Visibility = recent.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible; }
        finally { syncing = false; }
    }
    internal void Activate()
    {
        Refresh();
        if (!ReferenceEquals(context, workspace.Context)) {
            Cancel(); context = workspace.Context; loaded = false; listings = [];
            syncing = true;
            try { owners.ItemsSource = workspace.Owners; owners.SelectedItem = workspace.DefaultOwner; }
            finally { syncing = false; }
            StartLoad();
        } else if (!loaded && cancellation is null) StartLoad(resetRepository: false);
    }
    internal void Cancel() { generation++; cancellation?.Cancel(); }
    internal async Task Stop() { Cancel(); await loading; }
    private async Task Open(ProjectChoice choice)
    {
        await open(choice);
        syncing = true; try { projects.SelectedItem = null; recent.SelectedItem = null; } finally { syncing = false; }
    }
    private void StartLoad(bool reload = false, bool resetRepository = true)
    {
        Cancel();
        if (owners.SelectedItem is not OwnerChoice owner) return;
        var selected = !resetRepository ? repositories.SelectedItem as ProjectRepositoryChoice : null;
        var source = new CancellationTokenSource(); cancellation = source;
        loading = Task.WhenAll(loading, Load(owner, selected, reload, generation, source));
    }
    private async Task Load(OwnerChoice owner, ProjectRepositoryChoice? selected, bool reload, int version, CancellationTokenSource source)
    {
        loaded = false; syncing = true;
        try { projects.ItemsSource = null; repositories.ItemsSource = null; } finally { syncing = false; }
        progress.Visibility = Visibility.Visible; closed.Content = "終了済みも表示"; AutomationProperties.SetName(closed, "終了済みも表示"); status.Text = "読み込み中…"; retry.Visibility = Visibility.Collapsed;
        try {
            var result = await workspace.Listings(owner.Login, reload, source.Token);
            if (version != generation || source.IsCancellationRequested || !ReferenceEquals(context, workspace.Context)) return;
            listings = result; loaded = true; RepositoryEntries(selected); Filter();
        } catch (OperationCanceledException) when (source.IsCancellationRequested || version != generation) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            if (version != generation) return;
            status.Text = owner.Login + " のプロジェクトを取得できません。再試行してください。";
            retry.Visibility = Visibility.Visible;
        } finally {
            if (version == generation) progress.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(cancellation, source)) cancellation = null;
            source.Dispose();
        }
    }
    private void RepositoryEntries(ProjectRepositoryChoice? selection = null)
    {
        selection ??= repositories.SelectedItem as ProjectRepositoryChoice;
        var entries = ProjectChooserFilter.Repositories(listings, ((OwnerChoice)owners.SelectedItem).Login, closed.IsChecked == true);
        syncing = true;
        try { repositories.ItemsSource = entries;
            repositories.SelectedItem = entries.FirstOrDefault(e => e.Kind == selection?.Kind && string.Equals(e.NameWithOwner, selection.NameWithOwner, StringComparison.OrdinalIgnoreCase)) ?? entries[0]; }
        finally { syncing = false; }
    }
    private void Filter()
    {
        if (repositories.SelectedItem is not ProjectRepositoryChoice repository) return;
        var result = ProjectChooserFilter.Projects(listings, repository, closed.IsChecked == true, search.Text);
        syncing = true; try { projects.ItemsSource = result.Select(p => p.Choice).ToArray(); } finally { syncing = false; }
        var label = $"終了済みも表示（{ProjectChooserFilter.ClosedCount(listings, repository)}）";
        closed.Content = label; AutomationProperties.SetName(closed, label);
        status.Text = ProjectChooserFilter.EmptyReason(listings, repository, closed.IsChecked == true, search.Text) switch {
            ProjectChooserEmptyReason.NoProjects => "プロジェクトがありません。",
            ProjectChooserEmptyReason.AllClosed => "すべてのプロジェクトが終了済みです。終了済みも表示をオンにしてください。",
            ProjectChooserEmptyReason.NoSearchMatch => "検索条件に一致するプロジェクトがありません。", _ => "" };
    }
    private static Grid Column(string heading, ListView list)
    {
        var grid = new Grid { RowSpacing = 8 }; grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        grid.Children.Add(Heading(heading)); grid.Children.Add(list); SetRow(list, 1); return grid;
    }
    private static TextBlock Heading(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Metadata(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["WorkspaceMetadataStyle"] };
    private static T Named<T>(T element, string id, string name) where T : DependencyObject { AutomationProperties.SetAutomationId(element, id); AutomationProperties.SetName(element, name); return element; }
    private static ListView List(string id, string name, int tab)
    {
        var list = Named(new ListView { SelectionMode = ListViewSelectionMode.Single, SingleSelectionFollowsFocus = false, TabIndex = tab }, id, name);
        list.ItemTemplate = (DataTemplate)XamlReader.Load("""<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><ContentControl HorizontalContentAlignment="Stretch" IsTabStop="False"/></DataTemplate>""");
        list.ItemContainerStyle = new Style(typeof(ListViewItem)) { BasedOn = (Style)Application.Current.Resources["PlanListViewItemStyle"], Setters = { new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        return list;
    }
    private static void Row(ContainerContentChangingEventArgs args, Func<object, UIElement> create)
    {
        if (args.InRecycleQueue) return;
        if (args.ItemContainer.ContentTemplateRoot is ContentControl root) root.Content = create(args.Item);
        AutomationProperties.SetName(args.ItemContainer, args.Item switch {
            ProjectChoice p => $"{p.Title} {p.OwnerLogin} プロジェクト {p.Number}",
            OwnerChoice o => o.Login + (o.Type == "User" ? "（個人）" : ""),
            ProjectRepositoryChoice r => $"{r.DisplayText} {r.Count}", _ => "" });
        args.Handled = true;
    }
}
