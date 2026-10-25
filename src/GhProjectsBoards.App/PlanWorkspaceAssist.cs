using System.Text;
using System.Threading.Channels;
using GhProjectsBoards.Core.Assist;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace GhProjectsBoards.App;

internal sealed partial class PlanWorkspaceView
{
    private readonly Border assistPane = Id(new Border { Width = 380, Visibility = Visibility.Collapsed,
        Style = (Style)Application.Current.Resources["WorkspaceCardStyle"], Padding = new(12) }, "PlanAssistPane");
    private readonly ToggleButton assistToggle = Id(new ToggleButton { Height = 32, MinHeight = 0, Padding = new(8, 0, 8, 0) }, "PlanAssist");
    private readonly ListView assistMessages = Id(new ListView { SelectionMode = ListViewSelectionMode.None, Padding = new(0) }, "AssistMessages");
    private readonly TextBox assistInput = Id(new TextBox { Header = "質問・変更の依頼", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, MaxHeight = 160 }, "AssistInput");
    private readonly InfoBar assistFailure = FailureBar("AssistFailure", "AI アシストを続けられませんでした");
    private readonly Button assistSend = Id(new Button { Content = "送信" }, "AssistSend");
    private readonly Button assistStop = Id(new Button { Content = "停止", Visibility = Visibility.Collapsed }, "AssistStop");
    private readonly Button assistChip = Id(new Button { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left }, "AssistSelectedTask");
    private readonly TextBlock assistConnection = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock assistProgress = Id(Label(""), "AssistProgress");
    private readonly List<(AssistProposal Proposal, Button Apply, Button Discard, TextBlock State)> assistProposals = [];
    private AssistSettings assistSettings = new();
    private AssistSettingsStore assistStore = null!;
    private Task assistLoaded = Task.CompletedTask;
    private string assistDataRoot = "";
    private string? assistSettingsError, assistSelected, assistDismissed, assistLastText;
    private bool assistComposing, assistCompositionEnded;
    private IAssistTurn? assistConversation;
    private AssistPlanTools? assistTools;
    private PlanSession? assistSession;
    private object? assistContext;
    private CancellationTokenSource? assistCancellation, assistCheckCancellation;
    private Task assistTurn = Task.CompletedTask;
    private Task assistShutdown = Task.CompletedTask;
    private Task assistCheckTask = Task.CompletedTask;
    private int assistGeneration;
    private FrameworkElement? assistSettingsHeading;
    private Grid? assistEnabledContent;
    private ScrollViewer? assistScroll;
    private bool assistFollow, assistFollowQueued, assistChangingView;
    private double assistPreviousOffset, assistPreviousExtent, assistPreviousViewport;
    internal IAssistRuntime AssistRuntimeEdge { get; set; } = new AssistRuntime();
    internal IAssistSecretStore AssistSecrets { get; set; } = new AssistSecretStore();

    private void InitializeAssist(Grid work, StackPanel commands)
    {
        assistDataRoot = Directory.GetParent(Directory.GetParent(workspace.Root)!.FullName)!.FullName;
        assistStore = new(assistDataRoot);
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        caption.Children.Add(new FontIcon { Glyph = "\uE8F2", FontSize = 14 }); caption.Children.Add(Label("アシスト"));
        assistToggle.Content = caption;
        AutomationProperties.SetName(assistToggle, "アシスト"); ToolTipService.SetToolTip(assistToggle, "アシスト");
        commands.Children.Insert(0, assistToggle);
        assistToggle.Checked += async (_, _) => await RequestAssistToggle();
        assistToggle.Unchecked += (_, _) => UpdateAssistVisibility();
        work.Children.Add(assistPane); Grid.SetColumn(assistPane, 1);
        assistSend.Click += async (_, _) => await StartAssist();
        assistStop.Click += (_, _) => assistCancellation?.Cancel();
        assistProgress.Visibility = Visibility.Collapsed;
        ScrollViewer.SetHorizontalScrollMode(assistMessages, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(assistMessages, ScrollBarVisibility.Disabled);
        assistMessages.Loaded += (_, _) => {
            assistMessages.LayoutUpdated -= AssistLayoutUpdated;
            // Loaded can arrive after Unloaded when the view is removed quickly; a late subscription would root the view.
            if (assistMessages.IsLoaded) assistMessages.LayoutUpdated += AssistLayoutUpdated;
            AttachAssistScroll();
        };
        assistMessages.Unloaded += (_, _) => assistMessages.LayoutUpdated -= AssistLayoutUpdated;
        assistMessages.SizeChanged += (_, _) => FollowAssist();
        assistChip.Click += (_, _) => { assistDismissed = assistSelected; assistSelected = null; UpdateAssistSelection(); };
        assistInput.TextCompositionStarted += (_, _) => AssistCompositionStarted();
        assistInput.TextCompositionEnded += (_, _) => AssistCompositionEnded();
        assistInput.KeyDown += async (_, e) => {
            if (e.Key == VirtualKey.Enter && !PlanSheetView.Down(VirtualKey.Shift) && !assistComposing && !assistCompositionEnded) {
                e.Handled = true; await AssistEnter(false);
            }
        };
        var recovery = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        recovery.Children.Add(Button("設定を開く", "AssistFailureSettings", OpenAssistSettings));
        var retry = Id(new Button { Content = "再試行" }, "AssistRetry");
        retry.Click += async (_, _) => { await EndAssist(); assistInput.Text = assistLastText ?? ""; await StartAssist(); };
        recovery.Children.Add(retry); assistFailure.Content = recovery;
        AutomationProperties.SetLiveSetting(assistFailure, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(assistProgress, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        Loaded += async (_, _) => { assistLoaded = LoadAssist(); await assistLoaded; };
    }

    private async Task LoadAssist()
    {
        var loaded = await assistStore.LoadAsync(); assistSettings = loaded.Settings; assistSettingsError = loaded.Error;
        try { await Task.Run(() => AssistConversation.CleanupLeftovers(assistDataRoot)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            ShowAssistFailure("前の会話を片付けられませんでした。保存先へのアクセスを確認してください。");
        }
        if (!closing) RenderAssist();
    }
    private Task OpenAssistSettings()
    {
        if (currentPage != "settings") settingsReturnPage = currentPage;
        OpenSettingsPage("assist");
        return Task.CompletedTask;
    }
    private void UpdateAssistVisibility()
    {
        assistToggle.IsEnabled = workspace.Session is not null;
        assistPane.Visibility = assistToggle.IsChecked == true && workspace.Session is not null && currentPage is "tasks" or "summary"
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateSheetSidePane();
        UpdateAssistSelection(); UpdateAssistProposals();
    }
    private void RenderAssist()
    {
        if (closing) return;
        // #275: with AI assist off there is no pane; アシスト opens its settings instead.
        if (assistSettings.Mode == AssistMode.None) { assistToggle.IsChecked = false; assistPane.Child = null; UpdateAssistVisibility(); return; }
        assistConnection.Text = $"{(assistSettings.Mode == AssistMode.Copilot ? "GitHub Copilot" : new Uri(assistSettings.Endpoint).Host)} · {assistSettings.Model}";
        if (assistEnabledContent is not null) { assistPane.Child = assistEnabledContent; UpdateAssistVisibility(); return; }
        var panel = assistEnabledContent = new Grid { RowSpacing = 8 };
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            panel.RowDefinitions.Add(new() { Height = height });
        var heading = new StackPanel { Spacing = 4 }; heading.Children.Add(assistConnection);
        var fresh = Id(new Button { Content = "新しい会話" }, "AssistNewConversation");
        fresh.Click += async (_, _) => { await EndAssist(); ClearAssistMessages(); };
        heading.Children.Add(fresh); panel.Children.Add(heading);
        assistMessages.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = {
            new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
            new Setter(Control.PaddingProperty, new Thickness(0, 4, 0, 4)) } };
        panel.Children.Add(assistMessages); Grid.SetRow(assistMessages, 1);
        panel.Children.Add(assistFailure); Grid.SetRow(assistFailure, 2);
        panel.Children.Add(assistChip); Grid.SetRow(assistChip, 3);
        panel.Children.Add(assistInput); Grid.SetRow(assistInput, 4);
        var actions = new StackPanel { Spacing = 4 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(assistSend); buttons.Children.Add(assistStop);
        actions.Children.Add(buttons); actions.Children.Add(assistProgress); panel.Children.Add(actions); Grid.SetRow(actions, 5);
        assistPane.Child = panel; UpdateAssistVisibility();
    }
    private void UpdateAssistSelection()
    {
        var identity = sheet?.RowIds.FirstOrDefault(id => sheet.IsSelectedRow(id));
        if (identity != assistSelected && identity != assistDismissed) assistDismissed = null;
        assistSelected = identity == assistDismissed ? null : identity;
        var row = workspace.Session?.Document.State.Rows.FirstOrDefault(r => r.Identity == assistSelected);
        // Requirement header rows are selectable in the sheet but are not plan tasks; offer and send only tasks.
        if (row is null) assistSelected = null;
        assistChip.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is not null) {
            assistChip.Content = new TextBlock { Text = $"選択中: {sheet!.TaskNames[row.Identity]} {row.Title} ×", MaxWidth = 292, TextTrimming = TextTrimming.CharacterEllipsis };
            AutomationProperties.SetName(assistChip, $"選択中: {sheet.TaskNames[row.Identity]} {row.Title}、選択を外す");
        }
    }
    // The native composition edge is also used by hosted tests; it never synthesizes plan edits.
    internal void AssistCompositionStarted() => assistComposing = true;
    internal void AssistCompositionEnded()
    {
        assistComposing = false; assistCompositionEnded = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => assistCompositionEnded = false);
    }
    internal Task AssistEnter(bool shift) => shift || assistComposing || assistCompositionEnded ? Task.CompletedTask : StartAssist();

    private async Task StartAssist()
    {
        if (closing || assistComposing || assistCompositionEnded || !assistTurn.IsCompleted) return;
        await assistLoaded;
        if (assistSettings.Mode == AssistMode.None || string.IsNullOrWhiteSpace(assistInput.Text)) return;
        var text = assistInput.Text;
        await Run(async () => {
            if (!assistTurn.IsCompleted || workspace.Session is not { } session || workspace.Context is not { } context) return;
            try {
                if (assistSession != session || !ReferenceEquals(assistContext, context)) await EndAssist();
                if (closing) return;
                if (context.IsInvalidated) { ShowAssistFailure("接続アカウントを確認できません。接続し直してください。"); return; }
                if (assistConversation is null) {
                    var manual = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "マニュアル.md"));
                    if (closing || workspace.Session != session || !ReferenceEquals(workspace.Context, context)) return;
                    assistTools = new(session, Today, manual); assistSession = session; assistContext = context;
                    assistConversation = AssistRuntimeEdge.Create(assistSettings, assistDataRoot, context.Host, context.Login, assistTools,
                        assistSettings.Mode == AssistMode.Byok ? AssistSecrets.Read() : null);
                }
                UpdateAssistSelection(); assistLastText = text; assistInput.Text = ""; assistFailure.IsOpen = false;
                assistFollow = true;
                assistPreviousOffset = assistScroll?.VerticalOffset ?? 0;
                assistPreviousExtent = assistScroll?.ExtentHeight ?? 0;
                assistPreviousViewport = assistScroll?.ViewportHeight ?? 0;
                assistMessages.Items.Add(Label("あなた: " + text));
                var answer = Id(Label(""), "AssistAnswer" + assistMessages.Items.Count);
                answer.SizeChanged += (_, _) => FollowAssist();
                assistMessages.Items.Add(answer);
                FollowAssist();
                assistCancellation = new(); SetAssistBusy(true);
                assistTurn = StreamAssist(assistConversation, text, assistSelected, answer, assistGeneration, assistCancellation.Token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { ShowAssistFailure("AI アシストを開始できませんでした。設定と API キーを確認してください。"); }
        });
    }
    private async Task StreamAssist(IAssistTurn conversation, string text, string? selected, TextBlock answer, int generation, CancellationToken token)
    {
        var rawAnswer = new StringBuilder();
        var channel = Channel.CreateBounded<AssistEvent>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        var producer = Task.Run(async () => {
            try { await foreach (var e in conversation.Send(text, selected, token).ConfigureAwait(false)) await channel.Writer.WriteAsync(e, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OutOfMemoryException) {
                try { await channel.Writer.WriteAsync(new(AssistEventKind.Error, Failure: new(AssistFailureKind.Runtime, "応答を取得できませんでした。再試行してください。", "再試行")), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
            finally { channel.Writer.TryComplete(); }
        }, CancellationToken.None);
        try {
            while (await channel.Reader.WaitToReadAsync(token).ConfigureAwait(false)) {
                var batch = new List<AssistEvent>(32);
                while (batch.Count < 32 && channel.Reader.TryRead(out var e)) batch.Add(e);
                await DispatchAssist(() => {
                    if (generation != assistGeneration || closing) return;
                    foreach (var e in batch) {
                        if (e.Kind == AssistEventKind.Delta) rawAnswer.Append(e.Text);
                        else if (e.Kind == AssistEventKind.Message && e.Text is { } message) { rawAnswer.Clear(); rawAnswer.Append(message); }
                        else if (e.Kind == AssistEventKind.Progress) { assistProgressBase = e.Text ?? ""; ShowAssistProgress(); }
                        if (e.Failure is { } failure) ShowAssistFailure(failure.Message);
                        if (e.Kind == AssistEventKind.TurnEnd) AddAssistResults(e);
                    }
                    answer.Text = AssistAnswerText.Plain(rawAnswer.ToString());
                    FollowAssist();
                });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) {
            await DispatchAssist(() => { if (generation == assistGeneration) answer.Text += "\n停止しました。"; });
        }
        finally {
            await producer.ConfigureAwait(false);
            await DispatchAssist(() => {
                if (generation == assistGeneration) { SetAssistBusy(false); assistCancellation?.Dispose(); assistCancellation = null; }
            });
        }
    }
    private Task DispatchAssist(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() => {
            try { action(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetResult();
        return completion.Task;
    }
    private void SetAssistBusy(bool busy)
    {
        assistInput.IsEnabled = assistSend.IsEnabled = !busy;
        assistStop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        assistProgressBase = busy ? "回答しています…" : "";
        // Models can think for minutes between events; the elapsed time shows the turn is still alive.
        assistElapsed.Restart();
        // Create the timer only for a running turn and release it afterwards: a live timer keeps the workspace reachable.
        if (busy) { assistElapsedTimer ??= CreateAssistElapsedTimer(); assistElapsedTimer.Start(); }
        else if (assistElapsedTimer is { } timer) { timer.Stop(); timer.Tick -= AssistElapsedTick; assistElapsedTimer = null; }
        ShowAssistProgress();
        assistProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        FollowAssist();
    }
    private string assistProgressBase = "";
    private readonly System.Diagnostics.Stopwatch assistElapsed = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? assistElapsedTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateAssistElapsedTimer()
    {
        var timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += AssistElapsedTick; return timer;
    }
    private void AssistElapsedTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => ShowAssistProgress();
    private void ShowAssistProgress()
    {
        var elapsed = assistElapsed.Elapsed;
        assistProgress.Text = assistProgressBase.Length == 0 || elapsed < TimeSpan.FromSeconds(10) ? assistProgressBase
            : $"{assistProgressBase}　経過 {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }
    private static ScrollViewer? FindAssistScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindAssistScroll(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
    private void AttachAssistScroll()
    {
        // The ListView template may not be applied at its first Loaded, so the first turn resolves it again.
        var scroll = FindAssistScroll(assistMessages);
        if (scroll is null || ReferenceEquals(scroll, assistScroll)) return;
        if (assistScroll is not null) assistScroll.ViewChanged -= AssistViewChanged;
        assistScroll = scroll; scroll.ViewChanged += AssistViewChanged;
    }
    private void AssistLayoutUpdated(object? sender, object e)
    {
        // Virtualized items can change the extent after the queued scroll has already run.
        // Subscribe only while loaded: LayoutUpdated is backed by the global layout manager.
        if (!assistMessages.IsLoaded) { assistMessages.LayoutUpdated -= AssistLayoutUpdated; return; }
        if (assistFollow && assistMessages.ActualHeight > 0 && assistScroll is { } scroll
            && scroll.ScrollableHeight - scroll.VerticalOffset > 1) FollowAssist();
    }
    private void AssistViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (assistScroll is not { } scroll) return;
        // A shrinking answer or larger viewport clamps the offset to the bottom; only an upward move that
        // stops above the bottom is the PMO reading earlier text. Streaming changes the extent every
        // frame, so requiring an unchanged extent would miss the PMO's scroll.
        if (!assistChangingView && scroll.VerticalOffset < assistPreviousOffset - 1
            && scroll.ScrollableHeight - scroll.VerticalOffset > 1)
            assistFollow = false;
        assistPreviousOffset = scroll.VerticalOffset; assistPreviousExtent = scroll.ExtentHeight;
        assistPreviousViewport = scroll.ViewportHeight;
    }
    private void FollowAssist()
    {
        if (!assistFollow || assistFollowQueued || closing || assistMessages.Items.Count == 0) return;
        assistFollowQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
            try {
                if (!assistFollow || closing || !assistMessages.IsLoaded || assistMessages.Items.Count == 0) return;
                AttachAssistScroll();
                assistChangingView = true;
                if (assistMessages.Items[^1] is FrameworkElement { IsLoaded: false } last)
                    assistMessages.ScrollIntoView(last, ScrollIntoViewAlignment.Default);
                assistMessages.UpdateLayout();
                assistScroll?.ChangeView(null, assistScroll.ScrollableHeight, null, true);
            }
            finally { assistChangingView = false; assistFollowQueued = false; }
        });
    }
    private void ShowAssistFailure(string message) { assistFailure.Message = message; assistFailure.IsOpen = true; }
    private void ClearAssistMessages() { assistFollow = false; assistMessages.Items.Clear(); assistProposals.Clear(); assistFailure.IsOpen = false; }
    private async Task EndAssist()
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var generation = ++assistGeneration;
        var cancellation = assistCancellation; assistCancellation = null;
        cancellation?.Cancel(); assistCheckCancellation?.Cancel();
        var conversation = assistConversation; assistTools?.DiscardProposal();
        assistConversation = null; assistTools = null; assistSession = null; assistContext = null;
        try {
            var stop = conversation?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            // Core owns the bounded graceful/force stop. Do not race it with a UI timeout.
            // Close can overlap an already-started New conversation/settings shutdown.
            assistShutdown = Task.WhenAll(assistShutdown.IsCompleted ? Task.CompletedTask : assistShutdown, stop);
            await assistShutdown;
            var remaining = AssistConversation.ShutdownDeadline - elapsed.Elapsed;
            await Task.WhenAll(assistTurn, assistCheckTask).WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            if (!closing && ex is not OperationCanceledException) ShowAssistFailure("会話を終了できませんでした。新しい会話で再試行してください。");
        }
        finally { cancellation?.Dispose(); }
        if (generation != assistGeneration) return;
        assistTurn = Task.CompletedTask; SetAssistBusy(false);
        UpdateAssistProposals();
    }
    private void AddAssistResults(AssistEvent result)
    {
        var referenceSession = assistSession;
        if (result.References is { Count: > 0 } references) {
            assistMessages.Items.Add(Label("参照したタスク"));
            foreach (var task in references.Take(5)) {
                var link = Id(new HyperlinkButton { Content = new TextBlock { Text = $"{task.Name} {task.Title}", TextWrapping = TextWrapping.Wrap, MaxWidth = 304 }, HorizontalAlignment = HorizontalAlignment.Left }, $"AssistTask{task.Identity}-{assistMessages.Items.Count}");
                AutomationProperties.SetName(link, $"{task.Name} {task.Title}");
                link.Click += async (_, _) => await Run(() => {
                    if (workspace.Session != referenceSession) return Task.CompletedTask;
                    sheet?.FilterTasks(new HashSet<string> { task.Identity }, "アシストの参照タスク"); Show("tasks"); sheet?.SelectAndFocus(task.Identity, PlanField.Title, false); return Task.CompletedTask;
                });
                assistMessages.Items.Add(link);
            }
            if (references.Count > 5) {
                var all = Button($"参照した {references.Count} 件を計画で表示", "AssistAllReferences" + assistMessages.Items.Count, () => {
                    if (workspace.Session != referenceSession) return Task.CompletedTask;
                    sheet?.FilterTasks(references.Select(t => t.Identity).ToHashSet(), "アシストの参照タスク"); Show("tasks");
                    return Task.CompletedTask;
                });
                all.HorizontalAlignment = HorizontalAlignment.Left; assistMessages.Items.Add(all);
            }
        }
        if (result.Proposal is not { } proposal || assistProposals.Any(p => p.Proposal.Id == proposal.Id)) return;
        var card = new StackPanel { Spacing = 6 };
        card.Children.Add(Label($"変更の提案 {proposal.Changes.Count} 件"));
        if (proposal.MovedTasks > 0) {
            var culture = System.Globalization.CultureInfo.GetCultureInfo("ja-JP");
            card.Children.Add(Id(Label($"適用すると、この変更で {proposal.MovedTasks.ToString("N0", culture)} 件の日付が変わります。"),
                "AssistRecalculation" + proposal.Id));
        }
        var changes = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = 280, Padding = new(0),
            ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = {
                new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch), new Setter(Control.PaddingProperty, new Thickness(0, 2, 0, 2)) } },
            ItemsSource = proposal.Changes.Select(c => new AssistChangeView($"{c.Task.Name} {c.Task.Title}", PlanSheetColumns.ReviewName(c.Field), c.Current, c.Proposed)).ToArray(),
            ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <Grid ColumnSpacing="8" RowSpacing="4" Padding="0,4,0,8">
                    <Grid.ColumnDefinitions><ColumnDefinition Width="64"/><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
                    <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                    <TextBlock Grid.ColumnSpan="3" Text="{Binding Task}" TextWrapping="Wrap" FontWeight="SemiBold"/>
                    <TextBlock Grid.Row="1" Text="項目" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                    <TextBlock Grid.Row="1" Grid.Column="1" Text="現在" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                    <TextBlock Grid.Row="1" Grid.Column="2" Text="提案" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                    <TextBlock Grid.Row="2" Text="{Binding Field}" TextWrapping="Wrap"/>
                    <TextBlock Grid.Row="2" Grid.Column="1" Text="{Binding Current}" TextWrapping="Wrap"/>
                    <TextBlock Grid.Row="2" Grid.Column="2" Text="{Binding Proposed}" TextWrapping="Wrap"/>
                  </Grid>
                </DataTemplate>
                """) };
        card.Children.Add(changes);
        var effect = proposal.ScheduleEffect;
        card.Children.Add(Label($"日程が変わるタスク {effect.Total} 件" + (effect.Complete ? "" : $"（先頭 {effect.Items.Count} 件）")));
        if (effect.Items.Count > 0) {
            var effects = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = 120, Padding = new(0),
                ItemContainerStyle = changes.ItemContainerStyle,
                ItemsSource = effect.Items.Select(c => $"{c.Task.Name} {c.Task.Title}\n{c.PreviousStart:yyyy-MM-dd}–{c.PreviousEnd:yyyy-MM-dd} → {c.Start:yyyy-MM-dd}–{c.End:yyyy-MM-dd}").ToArray(),
                ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
                    <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                      <TextBlock Text="{Binding}" TextWrapping="Wrap" Padding="0,2,0,6"/>
                    </DataTemplate>
                    """) };
            AutomationProperties.SetName(effects, "日程が変わるタスク"); card.Children.Add(effects);
        }
        var state = Id(Label(""), "AssistProposalState" + proposal.Id); card.Children.Add(state);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var owner = assistTools!;
        var apply = Button("適用", "AssistApply" + proposal.Id, async () => {
            if (!ReferenceEquals(owner, assistTools) || workspace.Session != referenceSession) return;
            try {
                var save = owner.ApplyProposal(proposal.Id);
                // Acceptance changes the document/history before persistence can fail.
                state.Text = "適用しました。"; RenderTasks(); UpdateAssistProposals();
                Check(await save);
            }
            catch (InvalidOperationException ex) { state.Text = ex.Message; }
            UpdateAssistProposals();
        });
        var discard = Id(new Button { Content = "破棄" }, "AssistDiscard" + proposal.Id);
        discard.Click += (_, _) => { if (owner.PendingProposal?.Id == proposal.Id) owner.DiscardProposal(); state.Text = "破棄しました。"; UpdateAssistProposals(); };
        buttons.Children.Add(apply); buttons.Children.Add(discard); card.Children.Add(buttons);
        var surface = Id(new Border { Style = (Style)Application.Current.Resources["WorkspaceCardStyle"],
            Margin = new(0, 4, 0, 4), Padding = new(10), Child = card }, "AssistProposal" + proposal.Id);
        surface.SizeChanged += (_, _) => FollowAssist();
        assistProposals.Add((proposal, apply, discard, state)); assistMessages.Items.Add(surface); UpdateAssistProposals();
    }
    private void UpdateAssistProposals()
    {
        foreach (var item in assistProposals) {
            var pending = assistTools?.PendingProposal?.Id == item.Proposal.Id;
            var stale = pending && assistTools!.ProposalIsStale;
            item.Apply.IsEnabled = pending && !stale; item.Discard.IsEnabled = pending;
            if (stale) item.State.Text = AssistProposal.StaleMessage;
            else if (!pending && item.State.Text.Length == 0) item.State.Text = "この提案は終了しました。";
        }
    }
}

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class AssistChangeView(string task, string field, string current, string proposed)
{
    public string Task { get; } = task;
    public string Field { get; } = field;
    public string Current { get; } = current;
    public string Proposed { get; } = proposed;
}
