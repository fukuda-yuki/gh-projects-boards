using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

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
    private bool subscribed;
    private int lifetime;
    private ContentDialog? activeDialog;
    private ConnectionScope[] profileChoices = [];
    private NavigationKey[] navigationKeys = [];
    private readonly Dictionary<TreeViewNode, NavigationKey> nodeKeys = [];
    internal RegistrationWorkspace Workspace => workspace!;
    public event EventHandler? ConnectionRequested;
    internal bool FocusHeader() => ConnectionSettings.Focus(FocusState.Programmatic);
    private void RequestConnection(object sender, RoutedEventArgs e)
    {
        if (CanLeaveForConnection()) ConnectionRequested?.Invoke(this, EventArgs.Empty);
    }
    public RegistrationPanel()
    {
        InitializeComponent();
        Owner.TextChanged += (_, _) => Repositories.ItemsSource = null;
        Loaded += (_, _) => { Attach(); Update(); };
        Unloaded += (_, _) => Detach();
    }
    internal void Initialize(RegistrationWorkspace value)
    {
        Detach();
        workspace = value;
        rendered = null; revision = -1; displayedProfile = null;
        profileChoices = []; navigationKeys = []; nodeKeys.Clear();
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
            WorkspaceStatusBar.Visibility = workspace.Selected is null || workspace.IsBusy || workspace.Status.Contains("失敗")
                || workspace.Status.Contains("中断") || workspace.Status.Contains("保持") || workspace.Status.Contains("不明")
                || workspace.Status.Contains("IME変換中")
                ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetHelpText(ProjectSettings, workspace.Status);
            StatusDetailsText.Text = workspace.Status;
            ToolTipService.SetToolTip(Status, workspace.Status);
            Identity.Text = workspace.Profile is { } profile
                ? $"{profile.Host}  /  {workspace.ProfileLogin}  /  {(workspace.CanRead ? "接続確認済み" : "未認証・キャッシュのみ")}" : "アカウント未選択 — 保存済みアカウントを選択、または接続設定で確認";
            ToolTipService.SetToolTip(Identity, workspace.Profile is { } identity ? $"{Identity.Text}\nアカウント ID {identity.ViewerId}" : Identity.Text);
            Add.IsEnabled = !workspace.IsBusy;
            DiscoveryConnectionHint.Visibility = DiscoveryConnection.Visibility = workspace.CanRead ? Visibility.Collapsed : Visibility.Visible;
            Cancel.IsEnabled = workspace.IsBusy;
            Cancel.Visibility = workspace.IsBusy ? Visibility.Visible : Visibility.Collapsed;
            Cancel.Content = workspace.ExecutingBatchId is null ? "処理をキャンセル" : "未送信の処理を止める";
            UpdateApplyProgress();
            Progress.Visibility = workspace.IsBusy ? Visibility.Visible : Visibility.Collapsed;
            Register.IsEnabled = choice is not null && workspace.CanRead && choice.Id.Scope == workspace.Profile && !workspace.IsBusy;
            DiscoveryForm.IsEnabled = !workspace.IsBusy;
            Refresh.IsEnabled = workspace.Selected is not null && workspace.CanRead && !workspace.IsBusy;
            Apply.IsEnabled = workspace.Selected is not null && workspace.Drafts is not null && !workspace.IsBusy && !applyDialog;
            ApplyHistory.IsEnabled = workspace.Drafts is not null && !workspace.IsBusy && !applyDialog;
            Remove.IsEnabled = workspace.Selected is not null;
            ProjectSettings.IsEnabled = workspace.Selected is not null;
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
                    var previousProjection = previousGrid?.RowProjection.Project == selected.Snapshot.Id && renderedRefreshGeneration == workspace.AcceptedRefreshGeneration ? previousGrid?.RowProjection : null;
                    renderedRefreshGeneration = workspace.AcceptedRefreshGeneration;
                    rendered = selected; DefaultRepository.Text = selected.DefaultRepository ?? "";
                    var selection = EditorHost.Children.OfType<EditingGrid>().FirstOrDefault()?.SelectionIdentity;
                    EditorHost.Children.Clear();
                    if (workspace.Drafts is { } drafts) { var grid = new EditingGrid(selected, drafts, workspace.PrepareLocalRowsAsync, previousProjection,
                        temporaryColumns: previousGrid?.RowProjection.Project == selected.Snapshot.Id ? previousGrid.TemporaryApplyColumns : null);
                        grid.ApplyHistoryRequested += (_, _) => ShowApplyHistory(this, new RoutedEventArgs()); grid.RestoreSelection(selection); EditorHost.Children.Add(grid); Items.Visibility = Visibility.Collapsed; }
                    else { Items.Visibility = Visibility.Visible; Items.ItemsSource = PreviewRows(selected.Snapshot).ToArray(); }
                }
                var p = selected.Snapshot;
                Summary.Text = p.Title;
                ProjectContext.Text = $"キャッシュ {selected.RetrievedAt.LocalDateTime:g}";
                ProjectContext.Visibility = Visibility.Visible;
                ToolTipService.SetToolTip(ProjectContext, ProjectContext.Text);
                ToolTipService.SetToolTip(Summary, Summary.Text);
                ProjectInformation.Text = $"{p.Title}\n{selected.OwnerLogin} / Project #{p.Number}\n{p.Url}\n{p.Id.Scope.Host} / アカウント ID {p.Id.Scope.ViewerId}\nProject ID: {p.Id.NodeId}\n\n最終成功：{selected.RetrievedAt.LocalDateTime:g}\n最新の試行：{RegistrationWorkspace.AttemptText(workspace.LatestAttempt)}\n項目 {p.Items.Count} / Issue {p.Issues.Count}\n非対応フィールド {p.Fields.Count(f => f.Availability == ValueAvailability.Unsupported)} / 閲覧不可 {p.Items.Count(i => i.Kind == ProjectItemKind.Unavailable)}\n\n編集はローカルに保存します。GitHubへの反映は、対象と内容をレビューして実行します。";
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
                ProjectContext.Text = ""; ProjectContext.Visibility = Visibility.Collapsed;
                EmptyWorkspace.Visibility = workspace.Incomplete is null ? Visibility.Visible : Visibility.Collapsed;
                EmptyHint.Text = workspace.Profile is null
                    ? "左の保存済みアカウントを選ぶと、前回保存したProjectを開けます。初めて利用する場合は、右上の「接続設定」でGitHubへの接続を確認してください。"
                    : workspace.CanRead ? "左の登録済みProjectを選択してください。「Projectを追加」から別のProjectを取得できます。"
                    : "左の登録済みProjectを選ぶと、キャッシュを使ってローカル編集できます。最新の取得やGitHubへの反映には「接続設定」が必要です。";
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
        var entries = Workspace.Registrations.Where(r => r.Snapshot.Id.Scope == Workspace.Profile)
            .SelectMany(r => (r.Repositories.Count == 0 ? new[] { "Repository関連付けなし" } : r.Repositories.Select(repo => repo.NameWithOwner))
                .Select(repo => new NavigationKey(r.OwnerLogin, repo, r.Snapshot.Id, r.Snapshot.Title))).ToArray();
        // Autosave changes draft status, not navigation membership. Replacing the nodes
        // here would discard the user's collapsed branches and keyboard focus.
        if (!navigationKeys.Select(e => (e.Owner, e.Repository, e.Project)).SequenceEqual(entries.Select(e => (e.Owner, e.Repository, e.Project))))
        {
            var expandedOwners = Navigation.RootNodes.ToDictionary(n => (string)n.Content, n => n.IsExpanded);
            var expandedRepositories = Navigation.RootNodes.SelectMany(owner => owner.Children.Select(repo => ((Owner: (string)owner.Content, Repository: (string)repo.Content), repo.IsExpanded))).ToDictionary(p => p.Item1, p => p.IsExpanded);
            var selectedKey = Navigation.SelectedNode is { } current && nodeKeys.TryGetValue(current, out var key) ? key : null;
            Navigation.RootNodes.Clear(); nodeKeys.Clear();
            foreach (var owner in entries.GroupBy(e => e.Owner))
            {
                var root = new TreeViewNode { Content = owner.Key, IsExpanded = !expandedOwners.TryGetValue(owner.Key, out var expanded) || expanded };
                foreach (var group in owner.GroupBy(e => e.Repository))
                {
                    var repository = new TreeViewNode { Content = group.Key, IsExpanded = !expandedRepositories.TryGetValue((owner.Key, group.Key), out var repoExpanded) || repoExpanded };
                    foreach (var entry in group)
                    {
                        var node = new TreeViewNode { Content = new NavigationEntry(entry.Project, entry.Title) };
                        repository.Children.Add(node); nodeKeys.Add(node, entry);
                    }
                    root.Children.Add(repository);
                }
                Navigation.RootNodes.Add(root);
            }
            if (selectedKey is not null)
                Navigation.SelectedNode = nodeKeys.FirstOrDefault(p => p.Value.Owner == selectedKey.Owner && p.Value.Repository == selectedKey.Repository && p.Value.Project == selectedKey.Project).Key;
        }
        else if (!navigationKeys.SequenceEqual(entries))
        {
            var updated = entries.ToDictionary(e => (e.Owner, e.Repository, e.Project));
            foreach (var node in nodeKeys.Keys.ToArray())
            {
                var previous = nodeKeys[node];
                var current = updated[(previous.Owner, previous.Repository, previous.Project)];
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
    private void HeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 720;
        Grid.SetColumn(ProjectCommands, narrow ? 0 : 1);
        Grid.SetRow(ProjectCommands, narrow ? 1 : 0);
        Grid.SetColumnSpan(ProjectCommands, narrow ? 2 : 1);
        Grid.SetColumnSpan(ProjectHeading, narrow ? 2 : 1);
        Grid.SetRow(ProjectContext, narrow ? 2 : 1);
        var compact = e.NewSize.Width < 420;
        ProjectCommands.RowSpacing = compact ? 4 : 0;
        Grid.SetColumn(ApplyHistory, compact ? 0 : 2);
        Grid.SetRow(ApplyHistory, compact ? 1 : 0);
        Grid.SetColumn(ProjectSettings, compact ? 1 : 3);
        Grid.SetRow(ProjectSettings, compact ? 1 : 0);
    }
    private void BackToPreview(object sender, RoutedEventArgs e) => ShowPreview();
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
        choice = null; Confirmation.Text = ""; DiscoveryForm.Visibility = Visibility.Visible; Preview.Visibility = Visibility.Collapsed; Update();
    }
    private void ShowPreview() { DiscoveryForm.Visibility = Visibility.Collapsed; Preview.Visibility = Visibility.Visible; Update(); }
    private void CancelWork(object sender, RoutedEventArgs e) => Workspace.Cancel();
    private async void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || Profiles.SelectedIndex < 0) return;
        var owner = Workspace; var expected = lifetime;
        await owner.SelectProfileAsync(profileChoices[Profiles.SelectedIndex]);
        if (!IsCurrent(owner, expected)) return;
        choice = null; ShowPreview();
    }
    private async void Navigate(TreeView sender, TreeViewItemInvokedEventArgs e)
    {
        var owner = Workspace; var expected = lifetime;
        if (e.InvokedItem is not TreeViewNode { Content: NavigationEntry entry }) return;
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
        var owner = Workspace; var expected = lifetime;
        await owner.RegisterAsync(choice, InitialRepository.Text);
        if (IsCurrent(owner, expected) && (owner.Selected is not null || owner.Incomplete is not null)) ShowPreview();
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
    private sealed record NavigationKey(string Owner, string Repository, ScopedId Project, string Title);
    private sealed record NavigationEntry(ScopedId Id, string Title)
    {
        public override string ToString() => Title;
    }
}
