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
    private bool updating;
    private DispatcherTimer? deferredRendering;
    private int revision = -1;
    private ConnectionScope? displayedProfile;
    internal RegistrationWorkspace Workspace => workspace!;
    public RegistrationPanel() => InitializeComponent();
    internal void Initialize(RegistrationWorkspace value)
    {
        workspace = value;
        workspace.Changed += Update;
        workspace.Transitioning += () => { foreach (var grid in EditorHost.Children.OfType<EditingGrid>()) grid.CancelPending(); };
        workspace.CanRefresh = () => EditorHost.Children.OfType<EditingGrid>().All(grid => grid.CanRefresh);
        Owner.TextChanged += (_, _) => { Repositories.ItemsSource = null; };
        Update();
    }
    internal void Update()
    {
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(Update); return; }
        if (workspace is null) return;
        updating = true;
        try
        {
            if (revision != workspace.ConnectionRevision || displayedProfile != workspace.Profile)
            {
                revision = workspace.ConnectionRevision; displayedProfile = workspace.Profile;
                choice = null; Candidates.ItemsSource = null; Owners.ItemsSource = null; Repositories.ItemsSource = null;
                Confirmation.Text = ""; Owner.Text = ""; Search.Text = ""; Url.Text = ""; InitialRepository.Text = "";
                DefaultRepository.Text = ""; DiscoveryForm.Visibility = Visibility.Collapsed; Preview.Visibility = Visibility.Visible;
            }
            Status.Text = workspace.Status;
            Identity.Text = workspace.Profile is { } profile
                ? $"{profile.Host} / {workspace.ProfileLogin} / アカウント ID {profile.ViewerId} / {(workspace.CanRead ? "接続確認済み" : "保存済みプロフィール・未認証（キャッシュのみ）")}" : "プロフィール未選択";
            Add.IsEnabled = workspace.CanRead && !workspace.IsBusy;
            Cancel.IsEnabled = workspace.IsBusy;
            Progress.Visibility = workspace.IsBusy ? Visibility.Visible : Visibility.Collapsed;
            Register.IsEnabled = choice is not null && workspace.CanRead && choice.Id.Scope == workspace.Profile && !workspace.IsBusy;
            DiscoveryForm.IsEnabled = !workspace.IsBusy;
            Refresh.IsEnabled = workspace.Selected is not null && workspace.CanRead && !workspace.IsBusy;
            Apply.IsEnabled = workspace.Selected is not null && workspace.CanRead && !workspace.IsBusy;
            ApplyHistory.IsEnabled = workspace.Drafts is not null && !workspace.IsBusy && !applyDialog;
            Remove.IsEnabled = workspace.Selected is not null;
            SaveSetting.IsEnabled = workspace.Selected is not null && !workspace.IsBusy;
            DefaultRepository.IsEnabled = workspace.Selected is not null && !workspace.IsBusy;
            var profiles = workspace.Registrations.Select(r => r.Snapshot.Id.Scope).Distinct().ToArray();
            Profiles.ItemsSource = profiles.Select(p => $"{p.Host} / ID {p.ViewerId}").ToArray();
            Profiles.SelectedIndex = Array.IndexOf(profiles, workspace.Profile);
            Navigation.RootNodes.Clear();
            foreach (var owner in workspace.Registrations.Where(r => r.Snapshot.Id.Scope == workspace.Profile).GroupBy(r => r.OwnerLogin))
            {
                var root = new TreeViewNode { Content = owner.Key, IsExpanded = true };
                var groups = owner.SelectMany(r => r.Repositories.Count == 0
                    ? new[] { (Name: "Repository関連付けなし", Registration: r) }
                    : r.Repositories.Select(repo => (Name: repo.NameWithOwner, Registration: r))) .GroupBy(pair => pair.Name);
                foreach (var group in groups)
                {
                    var repository = new TreeViewNode { Content = group.Key, IsExpanded = true };
                    foreach (var entry in group) repository.Children.Add(new TreeViewNode { Content = new NavigationEntry(entry.Registration) });
                    root.Children.Add(repository);
                }
                Navigation.RootNodes.Add(root);
            }
            if (workspace.Selected is { } selected)
            {
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
                    rendered = selected; DefaultRepository.Text = selected.DefaultRepository ?? "";
                    var selection = EditorHost.Children.OfType<EditingGrid>().FirstOrDefault()?.SelectionIdentity;
                    EditorHost.Children.Clear();
                    if (workspace.Drafts is { } drafts) { var grid = new EditingGrid(selected, drafts, workspace.PrepareLocalRowsAsync); grid.RestoreSelection(selection); EditorHost.Children.Add(grid); Items.Visibility = Visibility.Collapsed; }
                    else { Items.Visibility = Visibility.Visible; Items.ItemsSource = PreviewRows(selected.Snapshot).ToArray(); }
                }
                var p = selected.Snapshot;
                Summary.Text = $"{p.Title} / {selected.OwnerLogin} / {p.Id.NodeId}\nキャッシュ：最終成功 {selected.RetrievedAt.LocalDateTime:g} / 最新の試行：{RegistrationWorkspace.AttemptText(workspace.LatestAttempt)}\n項目 {p.Items.Count} / Issue {p.Issues.Count} / 非対応フィールド {p.Fields.Count(f => f.Availability == ValueAvailability.Unsupported)} / 閲覧不可 {p.Items.Count(i => i.Kind == ProjectItemKind.Unavailable)}\nローカル編集（GitHub未反映）";
                if (workspace.Incomplete is { } staged)
                {
                    Grid.SetRow(Items, 4); Items.MaxHeight = 160;
                    Items.Visibility = Visibility.Visible; Items.ItemsSource = PreviewRows(staged).ToArray();
                    Summary.Text += "\n一部取得の未採用観測（保存キャッシュ・下書きとは別）：未取得範囲は不明です。";
                }
            }
            else
            {
                Grid.SetRow(Items, 3); Items.MaxHeight = double.PositiveInfinity;
                DefaultRepository.Text = "";
                EditorHost.Children.Clear(); Items.Visibility = Visibility.Visible; rendered = null; Items.ItemsSource = workspace.Incomplete is { } partial ? PreviewRows(partial).ToArray() : Array.Empty<string>();
                Summary.Text = workspace.Incomplete is { } p ? $"未登録・一部取得のプレビュー：{p.Title} / 項目 {p.Items.Count}。完全な保存ではありません。" : "左の登録済みProjectを選択してください。選択だけでは通信しません。";
            }
        }
        finally { updating = false; }
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
    private void ShowAdd(object sender, RoutedEventArgs e) { choice = null; Confirmation.Text = ""; DiscoveryForm.Visibility = Visibility.Visible; Preview.Visibility = Visibility.Collapsed; Update(); }
    private void ShowPreview() { DiscoveryForm.Visibility = Visibility.Collapsed; Preview.Visibility = Visibility.Visible; Update(); }
    private void CancelWork(object sender, RoutedEventArgs e) => Workspace.Cancel();
    private async void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || Profiles.SelectedIndex < 0) return;
        var profiles = Workspace.Registrations.Select(r => r.Snapshot.Id.Scope).Distinct().ToArray();
        await Workspace.SelectProfileAsync(profiles[Profiles.SelectedIndex]); choice = null; ShowPreview();
    }
    private async void Navigate(TreeView sender, TreeViewItemInvokedEventArgs e)
    {
        if (e.InvokedItem is TreeViewNode { Content: NavigationEntry entry }) { await Workspace.SelectAsync(entry.Registration.Snapshot.Id); ShowPreview(); }
    }
    private void OwnerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Owners.SelectedItem is OwnerChoice owner) { Owner.Text = owner.Login; Repositories.ItemsSource = null; }
    }
    private async void LoadOwners(object sender, RoutedEventArgs e) => await Workspace.DiscoverAsync(async (d, c, t) =>
    { var result = await d.OwnersAsync(c, t); t.ThrowIfCancellationRequested(); Owners.ItemsSource = result; });
    private async void LoadRepositories(object sender, RoutedEventArgs e)
    {
        var owner = Owner.Text.Trim();
        await Workspace.DiscoverAsync(async (d, c, t) => { var result = await d.RepositoriesAsync(c, owner, t); t.ThrowIfCancellationRequested(); Repositories.ItemsSource = new[] { "所有者の全Project" }.Concat(result.Select(r => r.NameWithOwner)).ToArray(); Repositories.SelectedIndex = 0; });
    }
    private async void SearchProjects(object sender, RoutedEventArgs e)
    {
        var owner = Owner.Text.Trim(); var search = Search.Text;
        var repository = Repositories.SelectedIndex > 0 ? (Repositories.SelectedItem as string)?.Split('/').Last() : null;
        await Workspace.DiscoverAsync(async (d, c, t) => { var result = await d.ProjectsAsync(c, owner, repository, search, t); t.ThrowIfCancellationRequested(); Candidates.ItemsSource = result; choice = null; Confirmation.Text = $"{result.Count} 件（全ページ取得済み）"; });
    }
    private async void ResolveUrl(object sender, RoutedEventArgs e)
    {
        var url = Url.Text;
        await Workspace.DiscoverAsync(async (d, c, t) => { var result = await d.ResolveAsync(c, url, t); t.ThrowIfCancellationRequested(); Candidates.ItemsSource = new[] { result }; Candidates.SelectedIndex = 0; });
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
        await Workspace.RegisterAsync(choice, InitialRepository.Text); if (Workspace.Selected is not null || Workspace.Incomplete is not null) ShowPreview();
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
        await Workspace.StopAsync();
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "ローカル登録を解除",
            Content = $"{r.Snapshot.Title}\nこのプロフィールの登録設定とキャッシュを削除します。GitHubのProject・Issue・項目は変更しません。下書きがある場合は保持・破棄を選択してください。他の登録で共有するIssueの下書きは保持します。",
            PrimaryButtonText = Workspace.HasDraftWork(r.Snapshot) ? "下書きを保持して解除" : "ローカル登録を解除", SecondaryButtonText = Workspace.HasDraftWork(r.Snapshot) ? "専用下書きを破棄して解除" : "", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(dialog, "LocalUnregisterConfirmation");
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) await Workspace.UnregisterAsync(retainDrafts: true);
        if (result == ContentDialogResult.Secondary) await Workspace.UnregisterAsync(discardDrafts: true);
    }
    private sealed record NavigationEntry(ProjectRegistration Registration)
    {
        public override string ToString() => Registration.Snapshot.Title;
    }
}
