using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace GhProjectsBoards.App;

internal sealed partial class PlanWorkspaceView
{
    private readonly ToggleButton detailToggle = Id(new ToggleButton { Height = 32, MinHeight = 0, Padding = new(8, 0, 8, 0) }, "PlanDetail");
    private readonly Border detailPane = Id(new Border { Width = 380, Visibility = Visibility.Collapsed,
        Style = (Style)Application.Current.Resources["WorkspaceCardStyle"], Padding = new(12),
        TabFocusNavigation = KeyboardNavigationMode.Cycle }, "PlanDetailPane");
    private readonly TextBlock detailId = Id(Label(""), "PlanDetailId"), detailReference = Id(Label(""), "PlanDetailReference"),
        detailTitle = Id(new TextBlock { TextWrapping = TextWrapping.Wrap, MaxLines = 3, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, "PlanDetailTitle"),
        detailState = Id(Label(""), "PlanDetailState"), detailUpdated = Id(Label(""), "PlanDetailUpdated"),
        detailEmpty = Id(Label("行を選ぶと、その Issue の本文とコメントを表示します"), "PlanDetailEmpty"),
        detailLocal = Id(Label("GitHub にまだ作成されていないタスクです。発行すると本文を表示できます"), "PlanDetailLocal"),
        detailLoading = Id(Label("読み込み中…"), "PlanDetailLoading"), detailFailureText = Id(Label(""), "PlanDetailFailureText");
    private readonly InfoBar detailFailure = FailureBar("PlanDetailFailure", "Issue を読み込めませんでした");
    private readonly Button detailOpen = Id(new Button { Content = "GitHub で開く" }, "PlanDetailOpen"),
        detailReload = Id(new Button { Content = "再読み込み" }, "PlanDetailReload");
    private readonly Grid detailBody = new();
    private readonly Grid detailIdentityLine = new(), detailStateLine = new() { Visibility = Visibility.Collapsed };
    // A TextBlock drops trailing spaces from its measured width, so the separators carry their gap as margin.
    private readonly TextBlock detailIdentitySeparator = new() { Text = "·", Margin = new(4, 0, 4, 0) }, detailStateSeparator = new() { Text = "·", Margin = new(4, 0, 4, 0) };
    private readonly ContentControl detailHeader = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
    private readonly ScrollViewer detailMarkdown = Id(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed }, "PlanDetailMarkdown");
    private readonly Dictionary<string, IssueDetail> detailCache = [];
    private CancellationTokenSource? detailCancellation;
    private Task detailWork = Task.CompletedTask;
    private PlanSession? detailSession;
    private ConnectionContext? detailContext;
    private string? detailIdentity, detailUrl;
    private int detailGeneration;
    private bool detailConnecting, detailNavigatePending;
    private ulong detailNavigation;
    private int detailNavigationGeneration, detailPendingNavigationGeneration;
    private WebView2? detailWeb;
    private Task<WebView2?>? detailWebInitialization;
    private readonly SemaphoreSlim detailNavigationGate = new(1, 1);
    private TaskCompletionSource? detailNavigationStarted;
    private TaskCompletionSource<bool>? detailNavigationCompleted;
    internal Func<Uri, Task> DetailExternalOpen { get; set; } = async uri => { await Launcher.LaunchUriAsync(uri); };
    internal Func<string, Task<CoreWebView2Environment>> DetailEnvironmentEdge { get; set; } = async folder =>
        await CoreWebView2Environment.CreateWithOptionsAsync(null, folder, null);

    private void InitializeDetail(Grid work, StackPanel commands)
    {
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        caption.Children.Add(new FontIcon { Glyph = "\uE8A5", FontSize = 14 }); caption.Children.Add(Label("詳細"));
        detailToggle.Content = caption;
        AutomationProperties.SetName(detailToggle, "詳細"); ToolTipService.SetToolTip(detailToggle, "詳細（Ctrl+I）");
        AutomationProperties.SetName(detailPane, "Issue の詳細");
        commands.Children.Insert(0, detailToggle);
        detailToggle.Checked += async (_, _) => await RequestDetailToggle(true);
        detailToggle.Unchecked += async (_, _) => await RequestDetailToggle(false);
        work.Children.Add(detailPane); Grid.SetColumn(detailPane, 1);
        var panel = new Grid { RowSpacing = 8 };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var header = new StackPanel { Spacing = 4 };
        foreach (var (line, first, separator, last) in new[] {
            (detailIdentityLine, detailId, detailIdentitySeparator, detailReference),
            (detailStateLine, detailState, detailStateSeparator, detailUpdated) }) {
            line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            foreach (var text in new[] { first, separator, last }) {
                text.Style = (Style)Application.Current.Resources["WorkspaceMetadataStyle"];
                text.TextWrapping = TextWrapping.NoWrap; line.Children.Add(text);
            }
            last.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(separator, 1); Grid.SetColumn(last, 2);
            AutomationProperties.SetAccessibilityView(separator, AccessibilityView.Raw);
        }
        header.Children.Add(detailIdentityLine); header.Children.Add(detailTitle); header.Children.Add(detailStateLine);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(detailOpen); actions.Children.Add(detailReload); header.Children.Add(actions);
        InitializeDetailEditing(header, actions);
        detailHeader.Content = header;
        AutomationProperties.SetName(detailHeader, "Issue の詳細");
        panel.Children.Add(detailHeader); panel.Children.Add(detailBody); Grid.SetRow(detailBody, 1); detailPane.Child = panel;
        foreach (var element in new FrameworkElement[] { detailEmpty, detailLocal, detailLoading, detailFailure, detailMarkdown }) {
            element.Visibility = Visibility.Collapsed; detailBody.Children.Add(element);
        }
        detailOpen.Click += async (_, _) => await OpenDetailLink(detailUrl);
        detailReload.Click += (_, _) => UpdateDetailSelection(reload: true);
        var retry = Id(new Button { Content = "再試行" }, "PlanDetailRetry");
        retry.Click += (_, _) => UpdateDetailSelection(reload: true);
        detailFailure.ActionButton = retry; detailFailure.Content = detailFailureText;
        detailFailure.VerticalAlignment = VerticalAlignment.Top;
        foreach (var button in new[] { detailOpen, detailReload, retry }) {
            AutomationProperties.SetName(button, (string)button.Content);
            ToolTipService.SetToolTip(button, button.Content);
        }
        AutomationProperties.SetLiveSetting(detailLoading, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(detailFailureText, AutomationLiveSetting.Polite);
        detailPane.PreviewKeyDown += (_, args) => {
            if (args.Key != VirtualKey.F6 || PlanSheetView.Down(VirtualKey.Control) || PlanSheetView.Down(VirtualKey.Menu)) return;
            args.Handled = true;
            if (PlanSheetView.Down(VirtualKey.Shift)) FocusCommandRow(); else sheet?.FocusSelected();
        };
        root.PreviewKeyDown += async (_, args) => {
            if (args.Key != VirtualKey.I || !PlanSheetView.Down(VirtualKey.Control) || PlanSheetView.Down(VirtualKey.Menu)
                || PlanSheetView.Down(VirtualKey.Shift) || !detailToggle.IsEnabled || sheet?.HasActiveCellEdit == true) return;
            args.Handled = true; await RequestDetailToggle(detailToggle.IsChecked != true);
            if (detailToggle.IsChecked != true) sheet?.FocusSelected();
        };
    }
    private bool DetailVisible => detailPane.Visibility == Visibility.Visible;
    private void UpdateDetailVisibility()
    {
        detailToggle.IsEnabled = workspace.Session is not null && currentPage == "tasks";
        sheet?.SetDetailChecked(detailToggle.IsChecked == true);
        detailPane.Visibility = detailToggle.IsEnabled && detailToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (DetailVisible) UpdateDetailSelection(); else CancelDetail();
    }
    private void CancelDetail(bool clearCache = false)
    {
        detailGeneration++; detailIdentity = null;
        detailCancellation?.Cancel(); detailCancellation = null;
        if (clearCache) { detailCache.Clear(); detailPublishedIdentity = null; }
    }
    private void ClearDetailContent(bool keepHeader = false)
    {
        detailLoadedIssue = null; UpdateDetailEditCommands();
        if (keepHeader && FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsDetailFocus(focused)
            && !ReferenceEquals(focused, detailOpen) && !ReferenceEquals(focused, detailReload) && !ReferenceEquals(focused, detailToggle)) {
            if (detailReload.IsEnabled) detailReload.Focus(FocusState.Programmatic); else FocusDetail();
        }
        foreach (var element in detailBody.Children.OfType<FrameworkElement>()) element.Visibility = Visibility.Collapsed;
        detailFailure.IsOpen = false;
        // A same-Issue reload must not disable its focused button, which evicts native focus to the shell.
        if (keepHeader) return;
        ToolTipService.SetToolTip(detailTitle, null);
        detailReference.Text = detailState.Text = detailUpdated.Text = "";
        detailIdentitySeparator.Visibility = detailStateLine.Visibility = Visibility.Collapsed;
        detailUrl = null; detailOpen.IsEnabled = detailReload.IsEnabled = false;
        detailHeader.IsTabStop = true;
    }
    private void UpdateDetailSelection(bool reload = false)
    {
        if (DetailEditing) { UpdateDetailPinnedNote(); return; }
        if (detailSession != workspace.Session || !ReferenceEquals(detailContext, workspace.Context) || workspace.Context?.IsInvalidated == true) {
            CancelDetail(clearCache: true); detailSession = workspace.Session; detailContext = workspace.Context;
        }
        if (!DetailVisible || closing || busyKind is not null || detailConnecting) return;
        var row = sheet is { } currentSheet && currentSheet.Session == workspace.Session
            ? workspace.Session?.Document.State.Rows.FirstOrDefault(r => currentSheet.IsSelectedRow(r.Identity)) : null;
        var identity = row?.Identity ?? "";
        detailId.Text = row is null ? "" : $"ID {sheet!.PlanIds.GetValueOrDefault(identity)}";
        if (row is not null && !workspace.Session!.Document.Sync.IssueLinks.ContainsKey(identity)) detailTitle.Text = row.Title;
        if (!reload && detailIdentity == identity) return;
        var keepHeader = reload && detailIdentity == identity;
        CancelDetail(); detailIdentity = identity;
        if (reload || detailPublishedIdentity != identity) detailPublishedIdentity = null;
        ClearDetailContent(keepHeader); detailTitle.Text = row?.Title ?? "";
        if (row is null) { detailEmpty.Visibility = Visibility.Visible; UpdateDetailEditCommands(); return; }
        if (workspace.Session!.Document.Sync.IssueLinks.GetValueOrDefault(identity) is not { } link) {
            detailLocal.Visibility = Visibility.Visible; UpdateDetailEditCommands(); return;
        }
        detailReference.Text = link.Caption; detailUrl = link.Url; detailOpen.IsEnabled = true;
        UpdateDetailEditCommands();
        detailIdentitySeparator.Visibility = Visibility.Visible;
        detailHeader.IsTabStop = false;
        if (workspace.Context?.IsInvalidated != false || workspace.Service is null) {
            ShowDetailFailure("接続アカウントを確認できません。接続し直してください。"); return;
        }
        detailReload.IsEnabled = true;
        if (reload) detailCache.Remove(identity);
        var cancellation = detailCancellation = new();
        detailWork = ReadDetail(identity, detailGeneration, cancellation, workspace.Service, workspace.Context);
    }
    private async Task ReadDetail(string identity, int generation, CancellationTokenSource cancellation, GhConnectionService service, ConnectionContext context)
    {
        var token = cancellation.Token;
        try {
            await Task.Delay(300, token);
            if (!detailCache.TryGetValue(identity, out var issue)) {
                // Use the existing authenticated gate, but cancel this read before any user remote operation starts.
                // Its cancellation releases the gate without a second connection or a competing credential owner.
                var read = new IssueDetailReader(service).Read(context, identity, token);
                var delay = Task.Delay(300, token);
                if (await Task.WhenAny(read, delay) != read && !token.IsCancellationRequested && generation == detailGeneration) {
                    detailLoading.Visibility = Visibility.Visible; AnnounceDetail(detailLoading);
                }
                var result = await read;
                if (token.IsCancellationRequested || generation != detailGeneration) return;
                detailLoading.Visibility = Visibility.Collapsed;
                if (result.Issue is null) { ShowDetailFailure(result.FailureReason); return; }
                detailCache[identity] = issue = result.Issue;
            }
            await DisplayDetail(issue, generation, token);
            if (!token.IsCancellationRequested && generation == detailGeneration && detailFocusEditAfterRead) {
                detailFocusEditAfterRead = false; detailEdit.Focus(FocusState.Programmatic);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            if (generation == detailGeneration && !closing) ShowDetailFailure("Issue を取得できません。再試行してください。");
        }
        finally {
            if (ReferenceEquals(detailCancellation, cancellation)) detailCancellation = null;
            cancellation.Dispose();
        }
    }
    private void ShowDetailFailure(string reason)
    {
        detailLoadedIssue = null; UpdateDetailEditCommands();
        detailLoading.Visibility = Visibility.Collapsed;
        detailFailureText.Text = reason;
        detailFailure.Visibility = Visibility.Visible; detailFailure.IsOpen = true;
        AnnounceDetail(detailFailureText);
    }
    private static void AnnounceDetail(TextBlock text) =>
        (FrameworkElementAutomationPeer.FromElement(text) ?? FrameworkElementAutomationPeer.CreatePeerForElement(text))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    private static string DetailTime(DateTimeOffset value) => value.ToLocalTime().ToString("M/d H:mm", CultureInfo.GetCultureInfo("ja-JP"));
    private async Task DisplayDetail(IssueDetail issue, int generation, CancellationToken token)
    {
        if (generation != detailGeneration || closing) return;
        detailLoadedIssue = issue; UpdateDetailEditCommands();
        detailReference.Text = issue.Reference; detailTitle.Text = issue.Title;
        ToolTipService.SetToolTip(detailTitle, issue.Title);
        detailState.Text = issue.State == "CLOSED" ? "クローズ" : "オープン";
        detailUpdated.Text = "最終更新 " + DetailTime(issue.UpdatedAt); detailUrl = issue.Url;
        detailStateLine.Visibility = Visibility.Visible;
        await detailNavigationGate.WaitAsync(token);
        try {
            detailWebInitialization ??= CreateDetailWeb();
            var web = await detailWebInitialization.WaitAsync(token);
            if (generation != detailGeneration || closing) return;
            if (web is null) { ShowDetailMarkdown(issue); return; }
            // NavigateToString's URI varies by runtime. The pending document and its navigation ID,
            // not a particular URI, distinguish it from links and late completion events.
            var started = detailNavigationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = detailNavigationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            detailNavigation = 0; detailPendingNavigationGeneration = generation; detailNavigatePending = true;
            web.NavigateToString(DetailHtml(issue));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            var success = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            if (!success && generation == detailGeneration && !closing) ShowDetailMarkdown(issue);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) {
            // An unstarted navigation has no ID to reject later. Retire that control before another
            // document can be pending, so its delayed start cannot acquire the new document's identity.
            if (detailNavigatePending && !closing) ResetDetailWeb();
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException) {
            ResetDetailWeb();
            if (generation == detailGeneration && !closing) ShowDetailMarkdown(issue);
        }
        finally {
            detailNavigatePending = false;
            detailNavigationStarted = null; detailNavigationCompleted = null;
            detailNavigationGate.Release();
        }
    }
    private void ResetDetailWeb()
    {
        detailNavigatePending = false; detailNavigation = 0;
        var web = detailWeb;
        detailWeb = null; detailWebInitialization = null;
        if (web is not null) { detailBody.Children.Remove(web); web.Close(); }
    }
    private async Task<WebView2?> CreateDetailWeb()
    {
        WebView2? web = null;
        try {
            var environment = await DetailEnvironmentEdge(Path.Combine(assistDataRoot, "PlanningEditor", "v1", "webview2"));
            if (closing) return null;
            web = detailWeb = Id(new WebView2(), "PlanDetailBody");
            AutomationProperties.SetName(web, "Issue の本文とコメント");
            detailBody.Children.Add(web);
            await web.EnsureCoreWebView2Async(environment);
            if (closing) { web.Close(); return null; }
            var core = web.CoreWebView2;
            core.Settings.IsScriptEnabled = false; core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false; core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.NavigationStarting += (sender, args) => {
                if (!ReferenceEquals(web, detailWeb)) { args.Cancel = true; return; }
                if (detailNavigatePending && !args.IsUserInitiated && !args.IsRedirected) {
                    detailNavigatePending = false; detailNavigation = args.NavigationId;
                    detailNavigationGeneration = detailPendingNavigationGeneration;
                    args.Cancel = detailNavigationGeneration != detailGeneration;
                    detailNavigationStarted?.TrySetResult(); return;
                }
                args.Cancel = true; _ = OpenDetailLink(args.Uri);
            };
            core.NewWindowRequested += (sender, args) => { args.Handled = true; _ = OpenDetailLink(args.Uri); };
            core.NavigationCompleted += (_, args) => {
                if (!ReferenceEquals(web, detailWeb) || args.NavigationId != detailNavigation) return;
                detailNavigationCompleted?.TrySetResult(args.IsSuccess);
                if (!closing && args.IsSuccess && detailNavigationGeneration == detailGeneration && DetailVisible)
                    web.Visibility = Visibility.Visible;
            };
            core.DownloadStarting += (_, args) => args.Cancel = true;
            web.Visibility = Visibility.Collapsed;
            return web;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            if (web is not null) { detailBody.Children.Remove(web); web.Close(); }
            detailWeb = null; return null;
        }
    }
    private void ShowDetailMarkdown(IssueDetail issue)
    {
        if (detailWeb is not null) detailWeb.Visibility = Visibility.Collapsed;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(Id(Label("表示部品 (WebView2) を使えないため、Markdown のまま表示しています"), "PlanDetailFallbackNote"));
        void Text(string value) => content.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var body = DetailMarkdownText(issue.Body);
        Text(string.IsNullOrWhiteSpace(body) ? "本文はありません" : body);
        content.Children.Add(SettingsHeading(DetailCommentHeading(issue.CommentCount)));
        foreach (var comment in issue.Comments) { Text(comment.Author + " · " + DetailTime(comment.CreatedAt)); Text(DetailMarkdownText(comment.Body)); }
        if (issue.CommentCount == 0) Text("コメントはありません");
        detailMarkdown.Content = content; detailMarkdown.Visibility = Visibility.Visible;
        detailMarkdown.ChangeView(null, 0, null, true);
    }
    private static string DetailMarkdownText(string value) => Regex.Replace(value, "<!--.*?-->", "", RegexOptions.Singleline);
    private static string DetailCommentHeading(int count) => count == 0 ? "コメント"
        : count <= 5 ? $"コメント {count} 件" : $"コメント {count} 件（最新 5 件）";
    private static string DetailHtml(IssueDetail issue)
    {
        var html = new StringBuilder("""
            <!doctype html><html lang="ja"><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src https: data:; style-src 'unsafe-inline'">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <style>
            :root{color-scheme:light}body{font:14px 'Segoe UI','Yu Gothic UI',sans-serif;color:#242424;background:#fff;margin:8px;overflow-wrap:anywhere}
            h1,h2,h3,h4,h5,h6{font-size:1.1em;margin:14px 0 6px}p,ul,ol{margin:6px 0}a{color:#005fb8}img{max-width:100%;height:auto}
            table{border-collapse:collapse;max-width:100%;display:block;overflow:auto}td,th{border:1px solid #ccc;padding:4px 6px}
            pre{overflow:auto;padding:8px;background:#f5f5f5}code{font-family:Consolas,monospace}blockquote{border-left:3px solid #ccc;margin:8px 0;padding-left:10px}
            input[type=checkbox]{pointer-events:none}.comment{border-top:1px solid #ddd;padding-top:8px;margin-top:12px}.meta{color:#616161}
            @media(forced-colors:active){body{color:CanvasText;background:Canvas}a{color:LinkText}}
            </style>
            """);
        html.Append("<base href=\"").Append(WebUtility.HtmlEncode(issue.Url))
            .Append("\"></head><body>");
        html.Append(string.IsNullOrWhiteSpace(issue.BodyHtml) ? "<p>本文はありません</p>" : issue.BodyHtml);
        html.Append("<h2>").Append(DetailCommentHeading(issue.CommentCount)).Append("</h2>");
        foreach (var comment in issue.Comments)
            html.Append("<section class='comment'><div class='meta'>").Append(WebUtility.HtmlEncode(comment.Author + " · " + DetailTime(comment.CreatedAt)))
                .Append("</div>").Append(comment.BodyHtml).Append("</section>");
        if (issue.CommentCount == 0) html.Append("<p>コメントはありません</p>");
        return html.Append("</body></html>").ToString();
    }
    private async Task OpenDetailLink(string? target)
    {
        if (closing || !Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try { await DetailExternalOpen(uri); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { error.Text = "ブラウザーを開けませんでした。リンクをもう一度押してください。"; }
    }
    private bool IsDetailFocus(DependencyObject? target)
    {
        for (var node = target; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, detailPane) || ReferenceEquals(node, detailToggle) || AutomationProperties.GetAutomationId(node) == "PlanSheetDetail") return true;
        return false;
    }
    private void FocusCommandRow()
    {
        if (FocusManager.FindFirstFocusableElement(toolbar) is Control command) command.Focus(FocusState.Keyboard);
    }
    private void FocusDetail()
    {
        if (detailOpen.IsEnabled) detailOpen.Focus(FocusState.Keyboard);
        else if (detailReload.IsEnabled) detailReload.Focus(FocusState.Keyboard);
        else detailHeader.Focus(FocusState.Keyboard);
    }
}
