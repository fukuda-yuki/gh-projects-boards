using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace GhProjectsBoards.App;

public sealed partial class RegistrationPanel : UserControl
{
    private RegistrationWorkspace? workspace;
    private ProjectChoice? choice;
    private ProjectRegistration? rendered;
    private long renderedRefreshGeneration;
    private bool updating;
    private DispatcherTimer? deferredRendering;
    private int revision = -1;
    private ConnectionScope? displayedProfile;
    private ScopedId? displayedProjectIdentity;
    private bool subscribed;
    private int lifetime;
    private ContentDialog? activeDialog;
    private ConnectionScope[] profileChoices = [];
    private NavigationKey[] navigationKeys = [];
    private RepositoryChoice[] navigationRepositories = [];
    private ScopedId? navigationRepository;
    private readonly Dictionary<TreeViewNode, NavigationKey> nodeKeys = [];
    private readonly Dictionary<ScopedId, ((string Item, FieldKey? Field)? Selection, ProjectView View, string? Person,
        GanttViewPosition? Gantt, string? DailyProjectFieldId, DateOnly? ReportingDay)> projectViewPositions = [];
    internal RegistrationWorkspace Workspace => workspace!;
    public event EventHandler? ConnectionRequested;
    internal bool FocusHeader() => ConnectionSettings.Focus(FocusState.Programmatic);
    internal void ShowCloseProblem(string message)
    {
        Status.Text = message; WorkspaceStatusBar.Visibility = Visibility.Visible;
    }
    private void RequestConnection(object sender, RoutedEventArgs e)
    {
        if (CanLeaveForConnection()) ConnectionRequested?.Invoke(this, EventArgs.Empty);
    }
    public RegistrationPanel()
    {
        InitializeComponent();
        Owner.TextChanged += (_, _) => Repositories.ItemsSource = null;
        Loaded += (_, _) => { Attach(); Update(); FocusGettingStarted(); };
        Unloaded += (_, _) => Detach();
    }
    internal void Initialize(RegistrationWorkspace value)
    {
        Detach();
        workspace = value;
        guideOpen = guideRegistration = guideReturnToDiscovery = guideReturnRegistration = false;
        guideReturnFocus = null;
        GettingStarted.Visibility = Visibility.Collapsed;
        rendered = null; revision = -1; displayedProfile = null;
        profileChoices = []; navigationKeys = []; nodeKeys.Clear(); navigationRepositories = []; navigationRepository = null;
        Navigation.RootNodes.Clear(); Profiles.ItemsSource = null;
        if (IsLoaded) Attach();
        Update();
    }
    private void Attach()
    {
        if (workspace is null || subscribed) return;
        subscribed = true;
        workspace.Changed += Update;
        workspace.Transitioning += CancelGridWork;
        workspace.CanRefresh = CanRefreshEditors;
    }
    private bool CanRefreshEditors() => EditorHost.Children.OfType<EditingGrid>().All(grid => grid.CanRefresh);
    internal async Task<bool> ConfirmPlanningNavigationAsync(ConnectionScope? scope = null, ScopedId? project = null)
    {
        if (workspace is null) return true;
        if (scope == Workspace.Profile && scope is not null && (project is null || project == Workspace.Selected?.Snapshot.Id)) return true;
        foreach (var grid in EditorHost.Children.OfType<EditingGrid>())
            if (!await grid.ConfirmLeavePlanningSettingsAsync()) { Update(); return false; }
        return true;
    }
    private async void ShowPlanningSettings(object sender, RoutedEventArgs e)
    {
        ProjectCommands.IsOpen = false;
        ProjectSettingsFlyout.Hide();
        if (EditorHost.Children.OfType<EditingGrid>().FirstOrDefault() is { } grid) await grid.ShowPlanningSettingsAsync();
    }
    private void CancelGridWork()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            var expected = lifetime;
            if (!DispatcherQueue.TryEnqueue(() => { if (expected == lifetime && IsLoaded) CancelGridWork(); }))
                throw new InvalidOperationException("The registration UI dispatcher is unavailable.");
            return;
        }
        foreach (var grid in EditorHost.Children.OfType<EditingGrid>()) grid.CancelPending();
        CancelApplyOutcome();
    }
    private void Detach()
    {
        lifetime++;
        CancelApplyOutcome();
        deferredRendering?.Stop(); deferredRendering = null;
        activeDialog?.Hide();
        ProjectSettingsFlyout.Hide();
        StatusDetailsFlyout.Hide();
        ProjectIdentityButton.Flyout.Hide();
        CancelGridWork();
        if (workspace is null || !subscribed) return;
        workspace.Changed -= Update;
        workspace.Transitioning -= CancelGridWork;
        if (workspace.CanRefresh == CanRefreshEditors) workspace.CanRefresh = null;
        subscribed = false;
    }
    internal void Update()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            var expected = lifetime;
            if (!DispatcherQueue.TryEnqueue(() => { if (expected == lifetime && IsLoaded) Update(); }))
                throw new InvalidOperationException("The registration UI dispatcher is unavailable.");
            return;
        }
        if (workspace is null) return;
        updating = true;
        try
        {
            var currentProjectIdentity = workspace.Selected?.Snapshot.Id;
            if (displayedProjectIdentity != currentProjectIdentity)
            {
                ProjectIdentityButton.Flyout.Hide();
                ProjectIdentityCopyStatus.Text = "";
                displayedProjectIdentity = currentProjectIdentity;
            }
            ProjectIdentityContext.Visibility = currentProjectIdentity is null ? Visibility.Collapsed : Visibility.Visible;
            if (displayedProfile != workspace.Profile)
            {
                revision = workspace.ConnectionRevision; displayedProfile = workspace.Profile;
                choice = null; Candidates.ItemsSource = null; Owners.ItemsSource = null; Repositories.ItemsSource = null;
                Confirmation.Text = ""; Owner.Text = ""; Search.Text = ""; Url.Text = ""; InitialRepository.Text = "";
                DefaultRepository.Text = ""; DiscoveryForm.Visibility = Visibility.Collapsed; Preview.Visibility = Visibility.Visible;
                ProjectSettingsFlyout.Hide();
                StatusDetailsFlyout.Hide();
            }
            else if (revision != workspace.ConnectionRevision)
            {
                revision = workspace.ConnectionRevision;
                choice = null; Candidates.ItemsSource = null;
                Confirmation.Text = "接続が変わりました。登録対象のURLまたは検索結果を再確認してください。";
            }
            Status.Text = workspace.Status;
            WorkspaceStatusBar.Visibility = workspace.IsBusy || workspace.Selected is null && workspace.Status.Length > 0 || workspace.Status.Contains("失敗")
                || workspace.Status.Contains("中断") || workspace.Status.Contains("保持") || workspace.Status.Contains("不明")
                || workspace.Status.Contains("IME変換中")
                ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetHelpText(ProjectSettings, workspace.Status);
            StatusDetailsText.Text = workspace.Status;
            ToolTipService.SetToolTip(Status, workspace.Status);
            Identity.Text = workspace.Profile is { } profile
                ? $"{profile.Host}  /  {workspace.ProfileLogin}  /  {(workspace.CanRead ? "接続確認済み" : "未認証・キャッシュのみ")}" : "アカウント未選択";
            ToolTipService.SetToolTip(Identity, workspace.Profile is { } identity ? $"{Identity.Text}\nアカウント ID {identity.ViewerId}" : Identity.Text);
            Add.IsEnabled = !workspace.IsBusy;
            DiscoveryConnectionHint.Visibility = DiscoveryConnection.Visibility = workspace.CanRead ? Visibility.Collapsed : Visibility.Visible;
            Cancel.IsEnabled = workspace.IsBusy;
            Cancel.Visibility = workspace.IsBusy ? Visibility.Visible : Visibility.Collapsed;
            Cancel.Content = "中止";
            ToolTipService.SetToolTip(Cancel, workspace.ExecutingBatchId is null ? "実行中の処理を中止" : "未送信の処理を止めます。完了したGitHub更新は取り消しません。");
            UpdateApplyProgress();
            Progress.Visibility = workspace.IsBusy ? Visibility.Visible : Visibility.Collapsed;
            Register.IsEnabled = choice is not null && workspace.CanRead && choice.Id.Scope == workspace.Profile && !workspace.IsBusy;
            DiscoveryForm.IsEnabled = !workspace.IsBusy;
            var settingsOpen = EditorHost.Children.OfType<EditingGrid>().Any(g => g.PlanningSettingsOpen);
            UpdateGettingStarted(settingsOpen);
            Refresh.IsEnabled = workspace.Selected is not null && workspace.CanRead && !workspace.IsBusy && !settingsOpen;
            Apply.IsEnabled = workspace.Selected is not null && workspace.Drafts is not null && !workspace.IsBusy && !applyDialog && !settingsOpen;
            WeeklyApply.IsEnabled = Apply.IsEnabled && workspace.Drafts!.Workspace.Planning(workspace.Selected!.Snapshot.Id.NodeId) is not null;
            PlanningSettingsCommand.IsEnabled = workspace.Selected is not null && workspace.Drafts is not null && !workspace.IsBusy && !applyDialog && !settingsOpen;
            ApplyHistory.IsEnabled = workspace.Drafts is not null && !workspace.IsBusy && !applyDialog;
            var hasProject = workspace.Selected is not null;
            foreach (var command in new[] { Refresh, Apply, WeeklyApply, PlanningSettingsCommand, ProjectSettings })
                command.Visibility = hasProject ? Visibility.Visible : Visibility.Collapsed;
            ApplyHistory.Visibility = workspace.Drafts is not null ? Visibility.Visible : Visibility.Collapsed;
            ProjectCommands.Visibility = ProjectHeader.Visibility = hasProject || workspace.Drafts is not null ? Visibility.Visible : Visibility.Collapsed;
            Remove.IsEnabled = workspace.Selected is not null;
            ProjectSettings.IsEnabled = workspace.Selected is not null && !settingsOpen;
            SaveSetting.IsEnabled = workspace.Selected is not null && !workspace.IsBusy;
            DefaultRepository.IsEnabled = workspace.Selected is not null && !workspace.IsBusy;
            PartialNotice.IsOpen = workspace.Incomplete is not null;
            UpdateNavigation();
            if (workspace.Selected is { } selected)
            {
                EmptyWorkspace.Visibility = Visibility.Collapsed;
                Grid.SetRow(Items, 1); Items.MaxHeight = double.PositiveInfinity;
                if (EditorHost.Children.Count > 0) Items.Visibility = Visibility.Collapsed;
                if (!ReferenceEquals(rendered, selected))
                {
                    if (EditorHost.Children.OfType<EditingGrid>().Any(g => !g.CanRefresh))
                    {
                        if (deferredRendering is null)
                        {
                            deferredRendering = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                            deferredRendering.Tick += (_, _) => { if (EditorHost.Children.OfType<EditingGrid>().All(g => g.CanRefresh)) { deferredRendering.Stop(); deferredRendering = null; Update(); } };
                            deferredRendering.Start();
                        }
                        return;
                    }
                    var previousGrid = EditorHost.Children.OfType<EditingGrid>().FirstOrDefault();
                    if (previousGrid is not null) projectViewPositions[previousGrid.RowProjection.Project] =
                        (previousGrid.ViewSelection, previousGrid.CurrentProjectView, previousGrid.SummaryPersonId, previousGrid.GanttPosition, previousGrid.DailyProjectFieldId, previousGrid.ReportingDay);
                    var previousProjection = previousGrid?.RowProjection.Project == selected.Snapshot.Id && renderedRefreshGeneration == workspace.AcceptedRefreshGeneration ? previousGrid?.RowProjection : null;
                    var position = projectViewPositions.GetValueOrDefault(selected.Snapshot.Id);
                    var selection = position.Selection;
                    if (workspace.Drafts is { } drafts) { var grid = new EditingGrid(selected, drafts, workspace.PrepareLocalRowsAsync, previousProjection,
                        temporaryColumns: previousGrid?.RowProjection.Project == selected.Snapshot.Id ? previousGrid.TemporaryContextColumns : null);
                        grid.ApplyHistoryRequested += (_, _) => ShowProblemHistory(grid.CurrentApplyProblem); grid.PlanningSettingsChanged += Update;
                        grid.ApplyReviewRequested += (_, _) => ReviewApply(this, new RoutedEventArgs());
                        // Retain the usable view until the replacement and its
                        // identity-based selection have been constructed.
                        grid.RestoreSelection(selection); EditorHost.Children.Clear(); EditorHost.Children.Add(grid);
                        grid.DailyProjectFieldId = position.DailyProjectFieldId;
                        if (projectViewPositions.ContainsKey(selected.Snapshot.Id)) grid.ReportingDay = position.ReportingDay;
                        grid.ShowProjectView(position.View, selection?.Item, position.Person);
                        grid.RestoreGanttPosition(position.Gantt); Items.Visibility = Visibility.Collapsed; }
                    else { EditorHost.Children.Clear(); Items.Visibility = Visibility.Visible; Items.ItemsSource = PreviewRows(selected.Snapshot).ToArray(); }
                    rendered = selected; renderedRefreshGeneration = workspace.AcceptedRefreshGeneration;
                    DefaultRepository.Text = selected.DefaultRepository ?? "";
                }
                var p = selected.Snapshot;
                Summary.Text = p.Title;
                ProjectIdentityButton.Content = $"{selected.OwnerLogin} / Project #{p.Number}";
                if (ProjectIdentityUrl.Text != p.Url) ProjectIdentityUrl.Text = p.Url;
                ProjectContext.Text = $"キャッシュ {selected.RetrievedAt.LocalDateTime:g}";
                ProjectContext.Visibility = Visibility.Visible;
                ToolTipService.SetToolTip(ProjectContext, ProjectContext.Text);
                ToolTipService.SetToolTip(Summary, Summary.Text);
                ProjectInformation.Text = $"{p.Title}\n{selected.OwnerLogin} / Project #{p.Number}\n{p.Url}\n{p.Id.Scope.Host} / アカウント ID {p.Id.Scope.ViewerId}\nProject ID: {p.Id.NodeId}\n\n最終成功：{selected.RetrievedAt.LocalDateTime:g}\n最新の試行：{RegistrationWorkspace.AttemptText(workspace.LatestAttempt)}\n項目 {p.Items.Count} / Issue {p.Issues.Count}\n非対応フィールド {p.Fields.Count(f => f.Availability == ValueAvailability.Unsupported)} / 閲覧不可 {p.Items.Count(i => i.Kind == ProjectItemKind.Unavailable)}";
                if (workspace.Incomplete is { } staged)
                {
                    Grid.SetRow(Items, 3); Items.MaxHeight = 160;
                    Items.Visibility = Visibility.Visible; Items.ItemsSource = PreviewRows(staged).ToArray();
                    Summary.Text += " · 未採用観測あり";
                }
            }
            else
            {
                Grid.SetRow(Items, 1); Items.MaxHeight = double.PositiveInfinity;
                DefaultRepository.Text = "";
                EditorHost.Children.Clear(); Items.Visibility = Visibility.Visible; rendered = null; Items.ItemsSource = workspace.Incomplete is { } partial ? PreviewRows(partial).ToArray() : Array.Empty<string>();
                Summary.Text = workspace.Incomplete is { } p ? $"未登録・一部取得のプレビュー：{p.Title} / 項目 {p.Items.Count}。完全な保存ではありません。" : "ワークスペース";
                ProjectInformation.Text = "Project未選択";
                ProjectIdentityButton.Content = ""; ProjectIdentityUrl.Text = "";
                ProjectContext.Text = ""; ProjectContext.Visibility = Visibility.Collapsed;
                EmptyWorkspace.Visibility = workspace.Incomplete is null ? Visibility.Visible : Visibility.Collapsed;
                if (workspace.Incomplete is null) Items.Visibility = Visibility.Collapsed;
            }
        }
        finally { updating = false; }
    }
    private void UpdateNavigation()
    {
        var profiles = Workspace.Registrations.Select(r => r.Snapshot.Id.Scope).Distinct().ToArray();
        if (!profileChoices.SequenceEqual(profiles))
        {
            profileChoices = profiles;
            Profiles.ItemsSource = profiles.Select(p => $"{Workspace.Registrations.First(r => r.Snapshot.Id.Scope == p).ViewerLogin} · {p.Host} · ID {p.ViewerId}").ToArray();
        }
        var profileIndex = Array.IndexOf(profileChoices, Workspace.Profile);
        if (Profiles.SelectedIndex != profileIndex) Profiles.SelectedIndex = profileIndex;
        var registered = Workspace.Registrations.Where(r => r.Snapshot.Id.Scope == Workspace.Profile)
            .OrderBy(r => r.OwnerLogin, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Snapshot.Number).ToArray();
        Profiles.Visibility = profileChoices.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NavigationHeading.Visibility = NavigationFilterPanel.Visibility = registered.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Add.Visibility = Workspace.Profile is not null ? Visibility.Visible : Visibility.Collapsed;
        if (navigationRepository?.Scope != Workspace.Profile) navigationRepository = null;
        var repositories = registered.SelectMany(r => r.Repositories).DistinctBy(r => r.Id)
            .OrderBy(r => r.NameWithOwner, StringComparer.OrdinalIgnoreCase).Select(r => new RepositoryChoice(r.Id, r.NameWithOwner)).ToList();
        if (navigationRepository is { } missing && repositories.All(r => r.Id != missing))
        {
            var previous = navigationRepositories.FirstOrDefault(r => r.Id == missing);
            if (previous is not null) repositories.Add(previous);
        }
        var choices = new[] { new RepositoryChoice(null, "すべて") }.Concat(repositories).ToArray();
        if (!navigationRepositories.SequenceEqual(choices))
        {
            navigationRepositories = choices; NavigationRepository.ItemsSource = choices;
        }
        NavigationRepository.SelectedItem = navigationRepositories.First(r => r.Id == navigationRepository);
        ClearNavigationRepository.Visibility = navigationRepository is null ? Visibility.Collapsed : Visibility.Visible;
        var matching = registered.Where(r => navigationRepository is null || r.Repositories.Any(repo => repo.Id == navigationRepository)).ToArray();
        NavigationFilterNotice.Text = navigationRepository is not null && Workspace.Selected is { } open
            && matching.All(r => r.Snapshot.Id != open.Snapshot.Id) ? "表示中のProjectは一覧の条件外です。" : matching.Length == 0 ? "条件に合う登録済みProjectはありません。" : "";
        NavigationFilterNotice.Visibility = NavigationFilterNotice.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var entries = matching.Select(r => new NavigationKey(r.OwnerLogin, r.Snapshot.Id,
            registered.Count(other => other.OwnerLogin == r.OwnerLogin
                && string.Equals(other.Snapshot.Title, r.Snapshot.Title, StringComparison.OrdinalIgnoreCase)) > 1
                ? $"{r.Snapshot.Title} · #{r.Snapshot.Number}" : r.Snapshot.Title)).ToArray();
        // Autosave changes draft status, not navigation membership. Replacing the nodes
        // here would discard the user's collapsed branches and keyboard focus.
        if (!navigationKeys.Select(e => (e.Owner, e.Project)).SequenceEqual(entries.Select(e => (e.Owner, e.Project))))
        {
            var focused = IsLoaded && XamlRoot is not null ? FocusManager.GetFocusedElement(XamlRoot) : null;
            var focusedProject = focused is null ? null : nodeKeys.FirstOrDefault(pair => ReferenceEquals(Navigation.ContainerFromNode(pair.Key), focused)).Value?.Project;
            var focusedOwner = focused is null ? null : Navigation.RootNodes.FirstOrDefault(node => ReferenceEquals(Navigation.ContainerFromNode(node), focused))?.Content as string;
            var expandedOwners = Navigation.RootNodes.ToDictionary(n => (string)n.Content, n => n.IsExpanded);
            var selectedKey = Navigation.SelectedNode is { } current && nodeKeys.TryGetValue(current, out var key) ? key : null;
            Navigation.RootNodes.Clear(); nodeKeys.Clear();
            foreach (var owner in entries.GroupBy(e => e.Owner))
            {
                var root = new TreeViewNode { Content = owner.Key, IsExpanded = !expandedOwners.TryGetValue(owner.Key, out var expanded) || expanded };
                foreach (var entry in owner)
                {
                    var node = new TreeViewNode { Content = new NavigationEntry(entry.Project, entry.Title) };
                    root.Children.Add(node); nodeKeys.Add(node, entry);
                }
                Navigation.RootNodes.Add(root);
            }
            if (selectedKey is not null)
                Navigation.SelectedNode = nodeKeys.FirstOrDefault(p => p.Value.Project == selectedKey.Project).Key;
            if (focusedProject is not null || focusedOwner is not null)
            {
                Navigation.UpdateLayout();
                var target = focusedProject is not null ? nodeKeys.FirstOrDefault(pair => pair.Value.Project == focusedProject).Key
                    : Navigation.RootNodes.FirstOrDefault(node => (string)node.Content == focusedOwner);
                if (target is null || Navigation.ContainerFromNode(target) is not TreeViewItem { IsLoaded: true } item
                    || !item.Focus(FocusState.Programmatic)) NavigationRepository.Focus(FocusState.Programmatic);
            }
        }
        else if (!navigationKeys.SequenceEqual(entries))
        {
            var updated = entries.ToDictionary(e => e.Project);
            foreach (var node in nodeKeys.Keys.ToArray())
            {
                var previous = nodeKeys[node];
                var current = updated[previous.Project];
                if (current.Title == previous.Title) continue;
                node.Content = new NavigationEntry(current.Project, current.Title); nodeKeys[node] = current;
            }
        }
        navigationKeys = entries;
        if (Workspace.Selected is { } selected && (Navigation.SelectedNode?.Content as NavigationEntry)?.Id != selected.Snapshot.Id)
            Navigation.SelectedNode = nodeKeys.FirstOrDefault(p => p.Value.Project == selected.Snapshot.Id).Key;
        else if (Workspace.Selected is null && Navigation.SelectedNode is not null)
            Navigation.SelectedNode = null;
    }
    private void ToggleNavigation(object sender, RoutedEventArgs e)
    {
        WorkspaceSplitView.IsPaneOpen = !WorkspaceSplitView.IsPaneOpen;
        AutomationProperties.SetName(NavigationToggle, WorkspaceSplitView.IsPaneOpen ? "Project一覧を折りたたむ" : "Project一覧を表示");
    }
    private void NavigationRepositoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || NavigationRepository.SelectedItem is not RepositoryChoice choice) return;
        navigationRepository = choice.Id;
        Update();
    }
    private void ClearNavigationRepositoryFilter(object sender, RoutedEventArgs e)
    {
        var returnFocus = XamlRoot is not null && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), ClearNavigationRepository);
        navigationRepository = null;
        Update();
        if (returnFocus) NavigationRepository.Focus(FocusState.Programmatic);
    }
    private void CopyProjectUrl(object sender, RoutedEventArgs e)
    {
        if (Workspace.Selected is not { } selected || displayedProjectIdentity != selected.Snapshot.Id
            || ProjectIdentityUrl.Text != selected.Snapshot.Url) return;
        try
        {
            var content = new DataPackage(); content.SetText(ProjectIdentityUrl.Text);
            Clipboard.SetContent(content); Clipboard.Flush();
            ProjectIdentityCopyStatus.Text = "URLをコピーしました。";
        }
        catch (Exception) { ProjectIdentityCopyStatus.Text = "コピーできませんでした。URLを選択してコピーしてください。"; }
    }
    private void ShowStatusDetails(object sender, RoutedEventArgs e)
    {
        ProjectSettingsFlyout.Hide();
        StatusDetailsFlyout.ShowAt(ProjectSettings);
    }
    private void PanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var mode = e.NewSize.Width <= 960 ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        if (WorkspaceSplitView.DisplayMode == mode) return;
        WorkspaceSplitView.DisplayMode = mode;
        WorkspaceSplitView.IsPaneOpen = mode == SplitViewDisplayMode.Inline;
        AutomationProperties.SetName(NavigationToggle, WorkspaceSplitView.IsPaneOpen ? "Project一覧を折りたたむ" : "Project一覧を表示");
    }
    private void BackToPreview(object sender, RoutedEventArgs e)
    {
        if (guideRegistration) OpenGettingStarted();
        else ShowPreview();
    }
    private static IEnumerable<string> PreviewRows(ProjectReadModel p)
    {
        foreach (var item in p.Items)
        {
            var title = item.ContentId is { } id && p.Issues.TryGetValue(id, out var issue)
                ? $"{issue.Repository.NameWithOwner} #{issue.Number} | {issue.Title.Value ?? Availability(issue.Title.Availability)} | {issue.State.Value?.ToString() ?? Availability(issue.State.Availability)}"
                : $"{Kind(item.Kind)} | {item.TypeName} | {item.Id.NodeId}";
            var values = item.Values.Select(v =>
            {
                var field = p.Fields.SingleOrDefault(f => f.Id == v.FieldId);
                return $"{field?.Name ?? "フィールド不明"}: {(v.Availability == ValueAvailability.Present ? field?.Options.SingleOrDefault(o => o.Id == v.OptionId)?.Name ?? v.OptionId : Availability(v.Availability))}";
            });
            yield return title + (item.IsArchived ? " [アーカイブ]" : "") + "\n" + string.Join(" / ", values);
        }
    }
    private static string Kind(ProjectItemKind kind) => kind switch { ProjectItemKind.Issue => "Issue", ProjectItemKind.PullRequest => "Pull Request", ProjectItemKind.Draft => "GitHub Draft", ProjectItemKind.Unavailable => "閲覧不可", _ => "非対応" };
    private static string Availability(ValueAvailability state) => state switch { ValueAvailability.Empty => "明示的な空値", ValueAvailability.Unsupported => "非対応", ValueAvailability.Unavailable => "閲覧不可", ValueAvailability.NotLoaded => "未取得", _ => "取得済み" };
    private void ShowAdd(object sender, RoutedEventArgs e)
    {
        if (!CanLeaveForConnection()) return;
        guideOpen = guideRegistration = false; GettingStarted.Visibility = Visibility.Collapsed;
        DiscoveryBack.Content = "ワークスペースへ戻る";
        choice = null; Confirmation.Text = ""; DiscoveryForm.Visibility = Visibility.Visible; Preview.Visibility = Visibility.Collapsed; Update();
    }
    private void ShowPreview()
    {
        guideOpen = guideRegistration = false; GettingStarted.Visibility = Visibility.Collapsed;
        DiscoveryForm.Visibility = Visibility.Collapsed; Preview.Visibility = Visibility.Visible; Update();
    }
    private void CancelWork(object sender, RoutedEventArgs e) => Workspace.Cancel();
    private async void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || Profiles.SelectedIndex < 0) return;
        var owner = Workspace; var expected = lifetime;
        var profile = profileChoices[Profiles.SelectedIndex];
        if (!await ConfirmPlanningNavigationAsync(profile) || !IsCurrent(owner, expected)) return;
        await owner.SelectProfileAsync(profile);
        if (!IsCurrent(owner, expected)) return;
        choice = null; ShowPreview();
    }
    private async void Navigate(TreeView sender, TreeViewItemInvokedEventArgs e)
    {
        var owner = Workspace; var expected = lifetime;
        if (e.InvokedItem is not TreeViewNode { Content: NavigationEntry entry }) return;
        if (!await ConfirmPlanningNavigationAsync(entry.Id.Scope, entry.Id) || !IsCurrent(owner, expected)) return;
        if (!await owner.SelectAsync(entry.Id) || !IsCurrent(owner, expected) || owner.Selected?.Snapshot.Id != entry.Id) return;
        ShowPreview();
        if (WorkspaceSplitView.DisplayMode == SplitViewDisplayMode.Overlay)
        {
            WorkspaceSplitView.IsPaneOpen = false;
            AutomationProperties.SetName(NavigationToggle, "Project一覧を表示");
            NavigationToggle.Focus(FocusState.Programmatic);
        }
    }
    private void OwnerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Owners.SelectedItem is OwnerChoice owner) { Owner.Text = owner.Login; Repositories.ItemsSource = null; }
    }
    private bool IsCurrent(RegistrationWorkspace owner, int expected) => IsLoaded && expected == lifetime && ReferenceEquals(workspace, owner);
    private async void LoadOwners(object sender, RoutedEventArgs e)
    {
        var owner = Workspace; var expected = lifetime;
        await owner.DiscoverAsync(async (d, c, t) =>
        { var result = await d.OwnersAsync(c, t); t.ThrowIfCancellationRequested(); if (IsCurrent(owner, expected)) Owners.ItemsSource = result; });
    }
    private async void LoadRepositories(object sender, RoutedEventArgs e)
    {
        var owner = Owner.Text.Trim();
        var source = Workspace; var expected = lifetime;
        await source.DiscoverAsync(async (d, c, t) => { var result = await d.RepositoriesAsync(c, owner, t); t.ThrowIfCancellationRequested(); if (!IsCurrent(source, expected)) return; Repositories.ItemsSource = new[] { "所有者の全Project" }.Concat(result.Select(r => r.NameWithOwner)).ToArray(); Repositories.SelectedIndex = 0; });
    }
    private async void SearchProjects(object sender, RoutedEventArgs e)
    {
        var owner = Owner.Text.Trim(); var search = Search.Text;
        var repository = Repositories.SelectedIndex > 0 ? (Repositories.SelectedItem as string)?.Split('/').Last() : null;
        var source = Workspace; var expected = lifetime;
        await source.DiscoverAsync(async (d, c, t) => { var result = await d.ProjectsAsync(c, owner, repository, search, t); t.ThrowIfCancellationRequested(); if (!IsCurrent(source, expected)) return; Candidates.ItemsSource = result; choice = null; Confirmation.Text = $"{result.Count} 件（全ページ取得済み）"; });
    }
    private async void ResolveUrl(object sender, RoutedEventArgs e)
    {
        var url = Url.Text;
        var source = Workspace; var expected = lifetime;
        await source.DiscoverAsync(async (d, c, t) => { var result = await d.ResolveAsync(c, url, t); t.ThrowIfCancellationRequested(); if (!IsCurrent(source, expected)) return; Candidates.ItemsSource = new[] { result }; Candidates.SelectedIndex = 0; });
    }
    private void CandidateSelected(object sender, SelectionChangedEventArgs e)
    {
        choice = Candidates.SelectedItem as ProjectChoice;
        if (choice is not { } p) { Update(); return; }
        Confirmation.Text = $"{p.Title}\n所有者 {p.OwnerLogin} / {p.Id.Scope.Host} / {Workspace.ProfileLogin} / アカウント ID {p.Id.Scope.ViewerId}\n{p.Url}\n{(Workspace.Registrations.Any(r => r.Snapshot.Id == p.Id) ? "既登録：同じ保存データを開きます" : "未登録")}";
        Update();
    }
    private async void RegisterProject(object sender, RoutedEventArgs e)
    {
        if (choice is null) return;
        var target = choice; var fromGuide = guideRegistration;
        var owner = Workspace; var expected = lifetime;
        if (!await ConfirmPlanningNavigationAsync(target.Id.Scope, target.Id) || !IsCurrent(owner, expected)) return;
        await owner.RegisterAsync(target, InitialRepository.Text);
        if (!IsCurrent(owner, expected)) return;
        if (fromGuide && owner.Selected?.Snapshot.Id == target.Id && owner.LatestAttempt is RegistrationAttempt.Complete or RegistrationAttempt.None)
        {
            guideReturnToDiscovery = guideReturnRegistration = false; guideReturnFocus = null;
            OpenGettingStarted();
        }
        else if (owner.Selected is not null || owner.Incomplete is not null) ShowPreview();
    }
    private async void RefreshProject(object sender, RoutedEventArgs e)
    {
        if (Workspace.Selected is not { } r) return;
        var p = r.Snapshot;
        await Workspace.RegisterAsync(new(p.Id, p.OwnerId, r.OwnerLogin, p.OwnerType, p.Number, p.Url, p.Title), r.DefaultRepository, true);
    }
    private async void SaveDefault(object sender, RoutedEventArgs e) => await Workspace.SetDefaultAsync(DefaultRepository.Text);
    private async void RemoveProject(object sender, RoutedEventArgs e)
    {
        if (Workspace.Selected is not { } r) return;
        ProjectSettingsFlyout.Hide();
        var owner = Workspace; var expected = lifetime;
        await owner.StopAsync();
        if (!IsCurrent(owner, expected) || owner.Selected != r) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "ローカル登録を解除",
            Content = $"{r.Snapshot.Title}\nこのプロフィールの登録設定とキャッシュを削除します。GitHubのProject・Issue・項目は変更しません。下書きがある場合は保持・破棄を選択してください。他の登録で共有するIssueの下書きは保持します。",
            PrimaryButtonText = Workspace.HasDraftWork(r.Snapshot) ? "下書きを保持して解除" : "ローカル登録を解除", SecondaryButtonText = Workspace.HasDraftWork(r.Snapshot) ? "専用下書きを破棄して解除" : "", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(dialog, "LocalUnregisterConfirmation");
        var result = await ShowDialogAsync(dialog);
        if (!IsCurrent(owner, expected) || owner.Selected != r) return;
        if (result == ContentDialogResult.Primary) await owner.UnregisterAsync(retainDrafts: true);
        if (result == ContentDialogResult.Secondary) await owner.UnregisterAsync(discardDrafts: true);
    }
    private sealed record NavigationKey(string Owner, ScopedId Project, string Title);
    private sealed record RepositoryChoice(ScopedId? Id, string Name)
    {
        public override string ToString() => Name;
    }
    private sealed record NavigationEntry(ScopedId Id, string Title)
    {
        public override string ToString() => Title;
    }
}
