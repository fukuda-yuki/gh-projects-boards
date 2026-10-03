using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;

namespace GhProjectsBoards.App;

public sealed partial class RegistrationPanel
{
    private bool applyDialog;
    private static string ApplyIdentity(ApplyOperation operation) => ApplyResultsPresentation.Identity(operation);
    private void UpdateApplyProgress()
    {
        var batch = Workspace.Drafts?.Workspace.Journal.SingleOrDefault(b => b.Id == Workspace.ExecutingBatchId);
        ApplyProgressPanel.Visibility = batch is null ? Visibility.Collapsed : Visibility.Visible;
        if (batch is null) { ApplyProgressRows.ItemsSource = null; return; }
        string Field(ApplyOperation operation) => $"{ApplyIdentity(operation)} / {(operation.Key.Kind == "Title" ? "タイトル（Issue共通）" : operation.FieldName)}: {ApplyStateText(operation.State)}";
        ApplyProgressRows.ItemsSource = batch.Operations.Select(Field).Concat((batch.Creations ?? []).SelectMany(c =>
            new[] { $"新規作成 / {c.Repository.Name} / {c.Title}: {CreationKnowledgePresentation.Describe(c).RowLabel}" }
                .Concat((c.Fields ?? []).Select(Field)))).ToArray();
    }
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (!IsLoaded) return ContentDialogResult.None;
        var expected = lifetime;
        if (!dialog.Resources.ContainsKey("ContentDialogMaxWidth")) dialog.Resources["ContentDialogMaxWidth"] = 760d;
        activeDialog = dialog;
        try
        {
            var result = await dialog.ShowAsync();
            return expected == lifetime && IsLoaded ? result : ContentDialogResult.None;
        }
        finally
        {
            if (ReferenceEquals(activeDialog, dialog))
            {
                activeDialog = null;
            }
        }
    }
    private sealed record HistoryKey(string BatchId, string? CreationId, string? OperationId);
    private sealed record HistoryRecord(ApplyBatch Batch, CreationOperation? Creation, ApplyOperation? Operation)
    {
        public HistoryKey Key => new(Batch.Id, Creation?.Id, Operation?.Id);
    }
    private sealed class HistoryPlace(RegistrationWorkspace owner, DraftSession session, string? project)
    {
        public RegistrationWorkspace Owner { get; } = owner;
        public DraftSession Session { get; } = session;
        public string? Project { get; } = project;
        public HistoryKey? Selected { get; set; }
        public HistoryKey? Anchor { get; set; }
        public bool IncludeCompleted { get; set; }
        public double ListOffset { get; set; }
        public double EvidenceOffset { get; set; }
        public (HistoryKey Target, string Reason)? SetupCheckFailure { get; set; }
    }
    private HistoryPlace? historyPlace;
    private long historyViewGeneration;

    private async void ShowApplyHistory(object sender, RoutedEventArgs e)
    {
        if (applyDialog || Workspace.Drafts is not { } session) return;
        historyViewGeneration++;
        var readOnly = EditorHost.Children.OfType<EditingGrid>().Any(grid => grid.PlanningSettingsOpen);
        ProjectSettingsFlyout.Hide();
        var owner = Workspace; var expected = lifetime;
        var profile = owner.Profile; var project = owner.Selected?.Snapshot.Id;
        bool Current() => IsCurrent(owner, expected) && owner.Profile == profile
            && owner.Selected?.Snapshot.Id == project && ReferenceEquals(owner.Drafts, session);
        if (historyPlace is not { } saved || saved.Owner != owner || saved.Session != session || saved.Project != project?.NodeId)
            historyPlace = new(owner, session, project?.NodeId);
        var place = historyPlace!;
        var historyRoot = XamlRoot;
        applyDialog = true; ApplyHistory.IsEnabled = false;
        string? resolutionBatch = null; string? resolutionOperation = null; bool setupReview = false;
        string? resumeBatch = null; string? withdrawBatch = null;
        bool reviewRemaining = false;
        HistoricalFieldTarget? historicalTarget = null;
        string? historicalContinuation = null;
        try
        {
            var historyContent = new ListView { SelectionMode = ListViewSelectionMode.None,
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(historyContent, "ApplyResultBatches");
            var allHistory = new CheckBox { Content = "完了分を含む保存履歴", IsChecked = place.IncludeCompleted };
            AutomationProperties.SetAutomationId(allHistory, "ApplyShowAllHistory");
            var attention = ApplyResultsPresentation.Attention(session.Workspace);
            var listContent = ApplyPanel();
            var heading = new Grid { ColumnSpacing = 8 };
            heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            heading.Children.Add(ApplyText(ApplyResultsPresentation.Summary(attention)));
            var help = ApplyHelp("履歴を開くだけでは送信しません。「確認して再開」は反映全体の未完了の結果を照合してから続けます。不確定な送信は自動で繰り返しません。「承認を撤回」は試行を保存したまま反映全体の承認を取り消します。再度反映するには新しいレビューが必要です。", "ApplyHistoryHelp");
            Grid.SetColumn(help, 1); heading.Children.Add(help); listContent.Children.Add(heading);
            listContent.Children.Add(allHistory); listContent.Children.Add(historyContent);
            if (readOnly) listContent.Children.Insert(0, ApplyText("計画の前提を編集中のため、履歴は閲覧のみです。保存または取消して戻ると操作できます。"));
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "反映結果・履歴", Content = listContent,
                CloseButtonText = "閉じる", DefaultButton = ContentDialogButton.Close, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(dialog, "ApplyHistoryDialog");
            var records = new Dictionary<HistoryKey, HistoryRecord>();
            var detailButtons = new Dictionary<HistoryKey, Button>();
            var refreshActions = new List<Action>();
            ScrollViewer? evidenceScroll = null;
            Grid? detailContent = null;
            bool goConnection = false;
            bool historyClosing = false;
            TaskCompletionSource? returning = null;
            void SaveListPlace()
            {
                if (ReferenceEquals(dialog.Content, listContent) && HistoryListScroll(historyContent) is { } scroll)
                    place.ListOffset = scroll.VerticalOffset;
            }
            void RestoreListPlace()
            {
                DispatcherQueue.TryEnqueue(() => {
                    if (!Current() || !dialog.IsLoaded || !ReferenceEquals(dialog.Content, listContent)) return;
                    historyContent.UpdateLayout();
                    HistoryListScroll(historyContent)?.ChangeView(null, place.ListOffset, null, true);
                    if (place.Anchor is { } key && detailButtons.TryGetValue(key, out var button)) button.Focus(FocusState.Programmatic);
                });
            }
            historyContent.Loaded += (_, _) => RestoreListPlace();
            void SizeContent()
            {
                historyContent.MaxHeight = Math.Max(100, historyRoot.Size.Height - 330);
                // The native dialog can offer less than this cap after its padding and width limit.
                listContent.MaxWidth = Math.Max(280, Math.Min(640, historyRoot.Size.Width - 120));
                if (detailContent is not null)
                {
                    detailContent.MaxWidth = listContent.MaxWidth;
                    detailContent.Height = Math.Max(180, historyRoot.Size.Height - 240);
                }
            }
            void RootSizeChanged(XamlRoot root, XamlRootChangedEventArgs args) => SizeContent();
            void AddConnection(StackPanel panel, ApplyBatch batch, List<Button> remoteActions, List<Button> localActions, bool beforeRecords = false)
            {
                if (remoteActions.Count == 0 && localActions.Count == 0) return;
                var prerequisite = ApplyPanel(4);
                var hint = ApplyText("このアカウントへの接続が確認されていません。");
                AutomationProperties.SetAutomationId(hint, "ApplyHistoryConnectionHint-" + batch.Id);
                var connection = new Button { Content = "接続設定", HorizontalAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetAutomationId(connection, "ApplyHistoryConnection-" + batch.Id);
                AutomationProperties.SetName(connection, "接続設定：" + batch.ProjectName);
                connection.Click += (_, _) => {
                    if (!Current() || readOnly || owner.IsBusy || owner.CanRead || !CanLeaveForConnection()) return;
                    SaveListPlace(); goConnection = true; dialog.Hide();
                };
                prerequisite.Children.Add(hint); prerequisite.Children.Add(connection);
                if (beforeRecords) panel.Children.Insert(1, prerequisite); else panel.Children.Add(prerequisite);
                void RefreshActions()
                {
                    var available = Current() && !readOnly && !owner.IsBusy;
                    foreach (var action in remoteActions) action.IsEnabled = available && owner.CanRead;
                    foreach (var action in localActions) action.IsEnabled = available;
                    hint.Text = owner.IsBusy ? "処理中です。完了後に操作してください。" : "このアカウントへの接続が確認されていません。";
                    hint.Visibility = !readOnly && remoteActions.Count > 0 && (owner.IsBusy || !owner.CanRead) ? Visibility.Visible : Visibility.Collapsed;
                    connection.Visibility = !readOnly && remoteActions.Count > 0 && !owner.IsBusy && !owner.CanRead ? Visibility.Visible : Visibility.Collapsed;
                    connection.IsEnabled = available && !owner.CanRead;
                    prerequisite.Visibility = hint.Visibility;
                }
                refreshActions.Add(RefreshActions); RefreshActions();
            }
            void AddBatchActions(StackPanel panel, ApplyBatch batch, List<Button> remote, List<Button> local)
            {
                if (!batch.Operations.Any(o => o.State is not (ApplyState.Succeeded or ApplyState.Superseded))
                    && !(batch.Creations ?? []).Any(c => !c.Completed && c.Authorized)) return;
                var freshReview = CanReviewRemaining(batch, session.Workspace);
                if (!freshReview)
                {
                    var counts = new List<string>();
                    if (batch.Operations.Length > 0) counts.Add($"既存Issue {batch.Operations.Length}フィールド");
                    if (batch.Creations is { Length: > 0 }) counts.Add($"新規Issue {batch.Creations.Length}件");
                    panel.Children.Add(ApplyText("この反映全体への操作（" + string.Join("・", counts) + "）"));
                }
                var resume = new Button { Content = freshReview ? "未反映の変更を確認…" : "確認して再開", IsEnabled = owner.CanRead && !readOnly };
                AutomationProperties.SetAutomationId(resume, ((batch.Creations ?? []).Any() ? "ResumeCreationBatch-" : "ResumeApplyBatch-") + batch.Id);
                AutomationProperties.SetName(resume, $"{resume.Content}：{batch.ProjectName} / {batch.ReviewedAt.LocalDateTime:g}");
                resume.Click += (_, _) => { if (!Current()) return; SaveListPlace(); if (freshReview) reviewRemaining = true; else resumeBatch = batch.Id; dialog.Hide(); };
                var withdraw = new Button { Content = "承認を撤回", IsEnabled = !readOnly };
                AutomationProperties.SetAutomationId(withdraw, "WithdrawApplyBatch-" + batch.Id);
                AutomationProperties.SetName(withdraw, $"反映全体の承認を撤回：{batch.ProjectName} / {batch.ReviewedAt.LocalDateTime:g}");
                withdraw.Click += (_, _) => { if (!Current()) return; SaveListPlace(); withdrawBatch = batch.Id; dialog.Hide(); };
                remote.Add(resume); local.Add(withdraw);
                var actions = new Grid { ColumnSpacing = 12, Margin = new(0, 4, 0, 0) };
                actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new());
                var resumeScope = ApplyPanel(4); var withdrawScope = ApplyPanel(4);
                resumeScope.Children.Add(ApplyText(freshReview ? "このProjectの未反映の変更" : "この実行の未完了の処理", true));
                withdrawScope.Children.Add(ApplyText("この実行の承認", true));
                resume.HorizontalAlignment = withdraw.HorizontalAlignment = HorizontalAlignment.Left;
                resumeScope.Children.Add(resume); withdrawScope.Children.Add(withdraw);
                actions.Children.Add(resumeScope); Grid.SetColumn(withdrawScope, 1); actions.Children.Add(withdrawScope); panel.Children.Add(actions);
                panel.Children.Add(ApplyText(freshReview
                    ? "次の画面で変更を選択します。ここでは送信しません。承認を撤回しても確認済みの成功は取り消しません。"
                    : "承認を撤回しても、試行履歴と確認済みの成功は保持します。"));
            }
            void AddRecordActions(StackPanel panel, HistoryRecord record, List<Button> remote)
            {
                if (record.Operation is { } operation)
                {
                    AddHistoricalHandling(panel, new(record.Batch.Id, record.Creation?.Id, operation.Id), session.Workspace, remote, readOnly,
                        target => { if (!Current()) return; place.Selected = place.Anchor = record.Key; SaveListPlace(); historicalTarget = target; dialog.Hide(); },
                        decision => { if (!Current()) return; place.Selected = place.Anchor = record.Key; SaveListPlace(); historicalContinuation = decision; dialog.Hide(); }, includeDetails: false);
                }
                else if (record.Creation is { } c && c.Dispatched && !c.Completed && session.Workspace.Creations.Last(x => x.LocalId == c.LocalId).Id == c.Id)
                {
                    var resolve = new Button { Content = c.Verified is null ? "作成の不確定結果を解決" : "設定内容を再確認", IsEnabled = owner.CanRead && !readOnly };
                    AutomationProperties.SetAutomationId(resolve, "ResolveCreation-" + c.Id);
                    AutomationProperties.SetName(resolve, $"{resolve.Content}: {c.Repository.Name} / {c.Title}");
                    resolve.Click += (_, _) => { if (!Current()) return; place.Selected = place.Anchor = record.Key; SaveListPlace(); resolutionBatch = record.Batch.Id; resolutionOperation = c.Id; setupReview = c.Verified is not null; dialog.Hide(); };
                    remote.Add(resolve); panel.Children.Add(resolve);
                    if (c.Verified is not null) panel.Children.Add(ApplyText("再確認だけでは送信しません。"));
                }
            }
            StackPanel RecordSummary(HistoryRecord record, bool selected = false)
            {
                if (record.Operation is { } operation)
                {
                    var panel = ApplyPanel(4);
                    var identity = ApplyText($"{ApplyIdentity(operation)} / {(operation.Key.Kind == "Title" ? "タイトル（Issue共通）" : operation.FieldName)}", true);
                    identity.MaxLines = 2; identity.TextTrimming = TextTrimming.CharacterEllipsis;
                    AutomationProperties.SetAutomationId(identity, selected ? "ApplyHistoryTarget" : "ApplyHistoryTarget-" + operation.Id); panel.Children.Add(identity);
                    var uncertain = ApplyResultsPresentation.Kind(operation) == ApplyAttentionKind.Uncertain;
                    panel.Children.Add(ApplyText(HistoricalResultText(operation)));
                    if (!uncertain)
                    {
                        var reason = ApplyText(ApplyResultsPresentation.OutcomeReason(operation)); reason.MaxLines = 2;
                        reason.TextTrimming = TextTrimming.CharacterEllipsis; panel.Children.Add(reason);
                    }
                    return panel;
                }
                var creationSummary = CreationHistory(record.Batch, record.Creation!, includeDetails: false, includeStages: false);
                AutomationProperties.SetAutomationId(creationSummary.Children[0], selected ? "ApplyHistoryTarget" : "CreationHistoryTarget-" + record.Creation!.Id);
                return creationSummary;
            }
            void ShowList()
            {
                place.Selected = null; dialog.Title = "反映結果・履歴"; dialog.SecondaryButtonText = ""; dialog.CloseButtonText = "閉じる";
                dialog.Content = listContent; evidenceScroll = null; detailContent = null; SizeContent(); RestoreListPlace();
            }
            void ShowDetail(HistoryRecord record)
            {
                if (!Current()) return;
                SaveListPlace();
                if (place.Selected != record.Key) place.EvidenceOffset = 0;
                place.Selected = place.Anchor = record.Key;
                var remote = new List<Button>(); var local = new List<Button>();
                var header = ApplyPanel(6);
                var context = ApplyText($"{record.Batch.ProjectName} / レビュー {record.Batch.ReviewedAt.LocalDateTime:g}", true);
                AutomationProperties.SetAutomationId(context, "ApplyHistoryContext"); header.Children.Add(context);
                header.Children.Add(RecordSummary(record, selected: true));
                if (record.Operation is null && record.Creation is { Verified: not null, Completed: false } known)
                    AddCreationComparisonContext(header, record.Batch, known, place);
                AddRecordActions(header, record, remote);
                var knownSetup = record.Operation is null && record.Creation is { Verified: not null, Completed: false };
                if (!knownSetup) AddBatchActions(header, record.Batch, remote, local);
                var body = ApplyPanel(8);
                if (knownSetup)
                {
                    var batchActions = ApplyPanel(4); AddBatchActions(batchActions, record.Batch, remote, local);
                    body.Children.Add(ApplyDetails("反映全体の操作", batchActions, "CreationBatchActions-" + record.Batch.Id));
                    if (place.SetupCheckFailure is { } check && check.Target == record.Key)
                        body.Children.Add(ApplyText("今回の比較で確認できなかった内容: " + check.Reason));
                }
                AddConnection(header, record.Batch, remote, local);
                if (record.Operation is { } operation)
                {
                    var related = record.Batch.Operations.Where(other => other.IssueId == operation.IssueId && other.Id != operation.Id
                        && ApplyResultsPresentation.IsVerified(record.Batch, other)).ToArray();
                    if (related.Length > 0)
                    {
                        body.Children.Add(ApplyText("同じ実行で反映を確認済み", true));
                        foreach (var verified in related)
                            body.Children.Add(ApplyText($"{ApplyResultsPresentation.FieldName(verified)} → {ApplyResultsPresentation.VerifiedValue(verified)}：反映を確認しました。"));
                    }
                    var earlier = ApplyResultsPresentation.EarlierVerifiedWork(session.Workspace.Journal, record.Batch, operation);
                    if (earlier.Length > 0)
                    {
                        body.Children.Add(ApplyText("以前の実行で確認済み（今回は送信していません）", true));
                        foreach (var verified in earlier)
                            body.Children.Add(ApplyText($"{ApplyResultsPresentation.FieldName(verified)} → {ApplyResultsPresentation.VerifiedValue(verified)}"));
                    }
                    if (ApplyResultsPresentation.ResolvedByLaterExecution(session.Workspace.Journal, record.Batch, operation))
                        body.Children.Add(ApplyText("この変更は後の実行で反映を確認済みです。元の送信記録は保持しています。", true));
                    if (operation.State == ApplyState.Failed && operation.Reason == "PermissionDenied" && !ApplyJournal.HasUnresolvedDispatch(operation))
                        AddPermissionTarget(body, record.Batch, operation);
                    body.Children.Add(ApplyChange(operation, includeHistory: true));
                }
                else body.Children.Add(CreationHistory(record.Batch, record.Creation!));
                if (record.Operation is { } selectedOperation)
                    AddHistoricalHandling(body, new(record.Batch.Id, record.Creation?.Id, selectedOperation.Id), session.Workspace, [], true, _ => { }, _ => { }, includeActions: false);
                body.Children.Add(ApplyDetails("実行の識別情報", ApplyText($"実行 {record.Batch.Id}\nProject {record.Batch.Project.NodeId}\n{record.Batch.Project.Scope.Host} / アカウント ID {record.Batch.Project.Scope.ViewerId}"), "ApplyBatchIdentity-" + record.Batch.Id));
                evidenceScroll = new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetAutomationId(evidenceScroll, "ApplyHistoryEvidence");
                evidenceScroll.Loaded += (_, _) => {
                    if (Current() && ReferenceEquals(activeDialog, dialog) && place.Selected == record.Key)
                        evidenceScroll?.ChangeView(null, place.EvidenceOffset, null, true);
                };
                evidenceScroll.ViewChanged += (_, _) => {
                    if (!historyClosing && Current() && place.Selected == record.Key && evidenceScroll is { IsLoaded: true })
                        place.EvidenceOffset = evidenceScroll.VerticalOffset;
                };
                detailContent = new Grid { RowSpacing = 12 };
                detailContent.RowDefinitions.Add(new() { Height = GridLength.Auto }); detailContent.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
                detailContent.Children.Add(header); Grid.SetRow(evidenceScroll, 1); detailContent.Children.Add(evidenceScroll);
                dialog.Title = "履歴の詳細"; dialog.SecondaryButtonText = "履歴一覧へ";
                dialog.CloseButtonText = knownSetup ? "作業へ戻る" : "閉じる"; dialog.Content = detailContent; SizeContent();
            }
            void Populate()
            {
                historyContent.Items.Clear(); records.Clear(); detailButtons.Clear(); refreshActions.Clear();
                foreach (var batch in session.Workspace.Journal.Reverse())
                {
                    var pending = attention.Where(a => a.BatchId == batch.Id).ToArray();
                    var entry = ApplyPanel(12); var remote = new List<Button>(); var local = new List<Button>();
                    entry.Children.Add(ApplyText($"{batch.ProjectName} / レビュー {batch.ReviewedAt.LocalDateTime:g}", true));
                    AddBatchActions(entry, batch, remote, local);
                    void AddDetailButton(StackPanel row, HistoryRecord record)
                    {
                        var details = new Button { Content = "履歴の詳細", HorizontalAlignment = HorizontalAlignment.Left };
                        AutomationProperties.SetAutomationId(details, record.Operation is { } operation ? "ApplyOperationDetails-" + operation.Id : "CreationHistoryDetails-" + record.Creation!.Id);
                        AutomationProperties.SetName(details, "履歴の詳細：" + (record.Operation is { } op ? ApplyIdentity(op) + " / " + op.FieldName : record.Creation!.Repository.Name + " / " + record.Creation.Title));
                        details.Click += (_, _) => ShowDetail(record); detailButtons[record.Key] = details;
                        row.Children.Add(details);
                    }
                    void AddRecord(HistoryRecord record, bool needsAttention)
                    {
                        records[record.Key] = record;
                        if (!place.IncludeCompleted && !needsAttention) return;
                        var row = RecordSummary(record); AddRecordActions(row, record, remote);
                        AddDetailButton(row, record); entry.Children.Add(row);
                    }
                    foreach (var operation in batch.Operations)
                        AddRecord(new(batch, null, operation), pending.Any(a => a.OperationId == operation.Id));
                    if (!place.IncludeCompleted && pending.Length > 0)
                    {
                        var affectedIssues = batch.Operations.Where(operation => pending.Any(item => item.OperationId == operation.Id))
                            .Select(operation => operation.IssueId).ToHashSet();
                        var related = batch.Operations.Where(operation => affectedIssues.Contains(operation.IssueId)
                            && ApplyResultsPresentation.IsVerified(batch, operation)).ToArray();
                        if (related.Length > 0)
                        {
                            var verified = ApplyPanel(4); verified.Children.Add(ApplyText("同じ実行で反映を確認済み", true));
                            foreach (var operation in related)
                            {
                                var row = ApplyPanel(4);
                                row.Children.Add(ApplyText($"{ApplyIdentity(operation)} / {ApplyResultsPresentation.FieldName(operation)} → {ApplyResultsPresentation.VerifiedValue(operation)}"));
                                AddDetailButton(row, new(batch, null, operation)); verified.Children.Add(row);
                            }
                            entry.Children.Add(verified);
                        }
                    }
                    foreach (var creation in batch.Creations ?? [])
                    {
                        AddRecord(new(batch, creation, null), pending.Any(a => a.CreationId == creation.Id));
                        foreach (var field in creation.EarlierFields ?? [])
                            AddRecord(new(batch, creation, field), pending.Any(a => a.OperationId == field.Id));
                    }
                    if (!place.IncludeCompleted && pending.Length == 0) continue;
                    AddConnection(entry, batch, remote, local, beforeRecords: true); historyContent.Items.Add(entry);
                }
                if (session.Workspace.Journal.Count == 0) historyContent.Items.Add(ApplyText("実行履歴なし"));
                historyContent.Visibility = historyContent.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            allHistory.Checked += (_, _) => { SaveListPlace(); place.IncludeCompleted = true; Populate(); RestoreListPlace(); };
            allHistory.Unchecked += (_, _) => { SaveListPlace(); place.IncludeCompleted = false; Populate(); RestoreListPlace(); };
            dialog.SecondaryButtonClick += (_, args) => { args.Cancel = true; ShowList(); };
            dialog.Closing += (_, _) => {
                SaveListPlace();
                if (evidenceScroll is not null) place.EvidenceOffset = evidenceScroll.VerticalOffset;
                historyClosing = true;
            };
            Populate(); SizeContent();
            if (place.Selected is { } selected && records.TryGetValue(selected, out var record)) ShowDetail(record);
            else place.Selected = null;
            void Changed()
            {
                if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(Changed); return; }
                if (!Current()) { if (ReferenceEquals(activeDialog, dialog)) dialog.Hide(); returning?.TrySetResult(); return; }
                foreach (var refresh in refreshActions) refresh();
            }
            void UnloadedHistory(object sender, RoutedEventArgs args)
            { if (ReferenceEquals(activeDialog, dialog)) dialog.Hide(); returning?.TrySetResult(); }
            owner.Changed += Changed; Unloaded += UnloadedHistory; historyRoot.Changed += RootSizeChanged;
            try
            {
                while (Current())
                {
                    Changed(); historyClosing = false; await ShowDialogAsync(dialog);
                    if (!goConnection || !Current()) break;
                    goConnection = false; returning = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    connectionReturn = returning; ConnectionRequested?.Invoke(this, EventArgs.Empty); await returning.Task;
                    if (ReferenceEquals(connectionReturn, returning)) connectionReturn = null;
                    returning = null;
                    // Reuse the selected detail and scroll controls across the connection round trip.
                }
            }
            finally
            {
                owner.Changed -= Changed; Unloaded -= UnloadedHistory; historyRoot.Changed -= RootSizeChanged;
                if (returning is not null && ReferenceEquals(connectionReturn, returning)) connectionReturn = null;
            }
        }
        finally { applyDialog = false; if (IsLoaded) Update(); }
        if (!Current()) return;
        if (reviewRemaining) await ReviewApplyAsync(restartRemaining: true);
        else if (resumeBatch is not null)
        {
            var generation = applyViewGeneration;
            await owner.ResumeApplyAsync(resumeBatch);
            if (IsCurrent(owner, expected) && generation == applyViewGeneration) QueueApplyOutcome(resumeBatch);
        }
        else if (withdrawBatch is not null) await owner.SupersedeApplyAsync(withdrawBatch);
        else if (resolutionBatch is not null && resolutionOperation is not null)
        { if (setupReview) await ReviewCreationSetupAsync(resolutionBatch, resolutionOperation); else await ResolveCreationAsync(resolutionBatch, resolutionOperation); }
        else if (historicalTarget is not null) await ReviewHistoricalFieldAsync(historicalTarget);
        else if (historicalContinuation is not null) await ContinueHistoricalFieldAsync(historicalContinuation);
    }
    private static ScrollViewer? HistoryListScroll(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); index++)
            if (HistoryListScroll(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index)) is { } child) return child;
        return null;
    }
    private static Button ApplyHelp(string text, string id)
    {
        var help = new Button { Content = new FontIcon { Glyph = "\uE946" }, Padding = new(6), MinWidth = 28, MinHeight = 28,
            Flyout = new Flyout { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 } } };
        AutomationProperties.SetAutomationId(help, id); AutomationProperties.SetName(help, "反映結果の操作について");
        ToolTipService.SetToolTip(help, text);
        return help;
    }
    private string HiddenColumnNote(string? fieldId, string? projectId = null) => Workspace.Selected is { } p
        && (projectId is null || p.Snapshot.Id.NodeId == projectId) && Workspace.Drafts?.Workspace.Columns(p).Hidden(fieldId) == true ? "（グリッドでは非表示）" : "";
    private static StackPanel ApplyPanel(double spacing = 8) => new() { Spacing = spacing, HorizontalAlignment = HorizontalAlignment.Stretch };
    private static TextBlock ApplyText(string text, bool emphasis = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
        FontWeight = emphasis ? FontWeights.SemiBold : FontWeights.Normal
    };
    private static InfoBar ApplyMessage(string title, string message, InfoBarSeverity severity) => new()
    {
        Title = title, Message = message, Severity = severity, IsOpen = true, IsClosable = false
    };
    private static Expander ApplyDetails(string title, UIElement content, string id)
    {
        var details = new Expander { Header = title, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(details, id);
        return details;
    }
    private string ApplyValue(ApplyOperation operation, string? value)
    {
        if (operation.Key.Kind == "Dependency") return value is null ? "依存関係なし" : "依存関係あり";
        if (value is null) return "明示的な空値";
        if (operation.Key.Kind is "Title" or "Number" or "Date") return value;
        var name = Workspace.Registrations.Where(p => p.Snapshot.Id.Scope == Workspace.Profile && p.Snapshot.Id.NodeId == operation.Key.ProjectId)
            .SelectMany(p => p.Snapshot.Fields).SingleOrDefault(f => f.Id.NodeId == operation.Key.FieldId)?.Options.SingleOrDefault(o => o.Id == value)?.Name;
        return name ?? value + "（選択肢名は未確認）";
    }
    private string HistoricalStoredValue(ApplyOperation operation, string? value) => ApplyResultsPresentation.HistoricalValue(operation, value,
        new(Workspace.Profile!, operation.Key.ProjectId ?? ""), Workspace.Drafts?.Workspace.Fields.SingleOrDefault(field => field.Key == operation.Key));
    private static string HistoricalResultText(ApplyOperation operation) => ApplyResultsPresentation.Kind(operation) == ApplyAttentionKind.Uncertain
        ? "当時の送信：結果不明" + (operation.State == ApplyState.Superseded ? "（以前の承認は終了）" : "")
        : operation.State == ApplyState.Superseded && operation.Attempts.Length > 0 && operation.Attempts.All(attempt => attempt.State == ApplyState.Failed)
            ? "当時の送信：失敗（以前の承認は終了）" : "当時の結果：" + ApplyStateText(operation.State);
    private static string HistoricalConfirmationValue(ApplyOperation operation)
    {
        if (operation.Verification is not { } observation) return "未確認";
        if (observation.Availability == ValueAvailability.Empty) return "空";
        if (observation.Availability != ValueAvailability.Present)
            return $"未確認（{observation.Availability} / {observation.Reason}）";
        return operation.Key.Kind == "Select"
            ? observation.Options.SingleOrDefault(option => option.Id == observation.Value)?.Name ?? $"選択肢 ID {observation.Value}（名前は未確認）"
            : operation.Key.Kind == "Dependency" ? "依存関係あり" : observation.Value!;
    }
    private StackPanel ApplyChange(ApplyOperation operation, bool includeHistory)
    {
        var panel = ApplyPanel(4);
        var identity = ApplyIdentity(operation);
        if (!includeHistory) panel.Children.Add(ApplyText($"{identity} / {(operation.Key.Kind == "Title" ? "タイトル（Issue共通）" : operation.FieldName)}{HiddenColumnNote(operation.Key.FieldId, operation.Key.ProjectId)}", emphasis: true));
        var values = ApplyPanel(4);
        var comparison = new Grid { ColumnSpacing = 16 };
        comparison.ColumnDefinitions.Add(new()); comparison.ColumnDefinitions.Add(new());
        comparison.Children.Add(ApplyText(includeHistory ? $"比較に使用したGitHubの値: {HistoricalStoredValue(operation, operation.Expected)}"
            : $"GitHub: {ApplyValue(operation, operation.Expected)}"));
        var intended = ApplyText(includeHistory ? $"当時の反映予定値: {(operation.Intended.Clear ? "空にする" : HistoricalStoredValue(operation, operation.Intended.Value))}"
            : $"反映する値: {(operation.Intended.Clear ? "明示的にクリア" : ApplyValue(operation, operation.Intended.Value))}", emphasis: true);
        Grid.SetColumn(intended, 1); comparison.Children.Add(intended); values.Children.Add(comparison);
        if (!includeHistory) values.Children.Add(ApplyText($"所有: {(operation.Key.Kind == "Title" ? "Issue共通のタイトル" : "このProjectの項目フィールド")} / フィールド {operation.Key.FieldId ?? "Issue title"}"));
        if (includeHistory)
        {
            var uncertain = ApplyResultsPresentation.Kind(operation) == ApplyAttentionKind.Uncertain;
            var reason = ApplyResultsPresentation.OutcomeReason(operation);
            if (uncertain && operation.State == ApplyState.Superseded)
            {
                panel.Children.Add(ApplyText("当時の送信：結果不明（以前の承認は終了）"));
                values.Children.Add(ApplyText("当時の状況: " + reason));
            }
            else panel.Children.Add(ApplyText($"{HistoricalResultText(operation)} / {reason}"));
            if (operation.NotBefore is { } wait) panel.Children.Add(ApplyText($"再開可能時刻: {wait.LocalDateTime:g}"));
            var verification = operation.Verification;
            values.Children.Add(ApplyText($"送信後の値の確認: {HistoricalConfirmationValue(operation)}"
                + (verification is null ? "" : $" / 確認日時 {verification.At.LocalDateTime:g}")));
            if (operation.Key.Kind == "Select") values.Children.Add(ApplyText("比較値の当時の名前は未保存です。名称には確認時点を添えています。"));
            var diagnostics = ApplyPanel(4);
            diagnostics.Children.Add(ApplyText($"試行 {operation.Attempts.Length} / {operation.Id}\nIssue {operation.IssueId} / 項目 {operation.ItemId}\nフィールド {operation.Key.FieldId ?? "Issue title"}"));
            if (operation.Key.Kind == "Select")
                diagnostics.Children.Add(ApplyText($"保存された比較値 ID: {operation.Expected ?? "空"}\n保存された反映予定値 ID: {operation.Intended.Value ?? "空"}"));
            if (reason != operation.Reason) diagnostics.Children.Add(ApplyText("保存された診断: " + operation.Reason));
            foreach (var attempt in operation.Attempts)
                diagnostics.Children.Add(ApplyText($"試行 {attempt.Number} 開始 {attempt.At.LocalDateTime:g} / 保存された結果：{ApplyStateText(attempt.State)} / {attempt.Reason}"));
            values.Children.Add(ApplyDetails("送信記録と識別情報", diagnostics, "ApplyOperationEvidence-" + operation.Id));
            panel.Children.Add(values);
        }
        else
        {
            panel.Children.Add(values);
            panel.Children.Add(ApplyDetails("Issueと項目の識別情報", ApplyText($"Issue {operation.IssueId}\n項目 {operation.ItemId}"), "ApplyOperationIdentity-" + operation.Id));
        }
        return panel;
    }
    private StackPanel CreationReview(CreationOperation creation, string projectName)
    {
        var panel = ApplyPanel(4);
        panel.Children.Add(ApplyText("新規Issue作成 / " + creation.Title, emphasis: true));
        panel.Children.Add(ApplyText($"宛先 {creation.Repository.Name}"));
        panel.Children.Add(ApplyText($"{projectName}へ追加予定"));
        foreach (var select in creation.Selects)
            panel.Children.Add(ApplyText($"{select.FieldName}{HiddenColumnNote(select.FieldId)}: {SelectIntentText(select)}"));
        foreach (var intent in creation.PlanningIntents ?? [])
            panel.Children.Add(ApplyText($"{intent.FieldName}: {(intent.Value.Clear ? "クリア" : intent.Value.Value)}"));
        if (creation.Selects.Length == 0 && (creation.PlanningIntents?.Length ?? 0) == 0) panel.Children.Add(ApplyText("Projectフィールドの指定なし"));
        panel.Children.Add(ApplyDetails("ローカル行と作成試行", ApplyText($"Repository ID {creation.Repository.Id}\nローカル行 {creation.LocalId}\n作成試行 {creation.Id}"), "CreationReviewIdentity-" + creation.Id));
        return panel;
    }
    private static string SelectIntentText(LocalSelect select) => select.Intent switch
    {
        "Set" => $"{select.OptionName ?? "選択肢名は未確認"} に設定",
        "ExplicitClear" => "空にする",
        _ => "未指定（送信しません）"
    };
    private StackPanel CreationHistory(ApplyBatch batch, CreationOperation creation, bool includeDetails = true, bool includeStages = true)
    {
        var panel = ApplyPanel(4);
        var title = ApplyText($"{creation.Repository.Name}{(creation.Verified is { } identity ? " #" + identity.Number : "")} / {creation.Title}", emphasis: true);
        if (!includeDetails) { title.MaxLines = 2; title.TextTrimming = TextTrimming.CharacterEllipsis; }
        if (!includeDetails) AutomationProperties.SetAutomationId(title, "ApplyHistoryTarget");
        panel.Children.Add(title);
        var knowledge = CreationKnowledgePresentation.Describe(creation);
        var completedBinding = CreationJournal.IsCompletedOriginalBinding(creation);
        panel.Children.Add(ApplyText(completedBinding
            ? ApplyResultsPresentation.BindingCompletionText(ApplyResultsPresentation.HasApprovedCreationFields(creation))
            : knowledge.RowLabel));
        if (includeStages)
        {
            if (knowledge.Knowledge is CreationKnowledge.OutcomeUnconfirmed or CreationKnowledge.IdentityUnverified)
                panel.Children.Add(ApplyText(knowledge.Description));
            var stages = ApplyResultsPresentation.CreationStages(batch, creation, Workspace.Drafts?.Workspace.Journal);
            panel.Children.Add(ApplyText(stages.Issue)); panel.Children.Add(ApplyText(stages.Membership));
            foreach (var field in stages.Fields) panel.Children.Add(ApplyText(field));
            if (creation.Verified is not null && !creation.Completed && creation.Reason != "未送信")
                panel.Children.Add(ApplyText(creation.Reason));
        }
        if (creation.Verified is { } issue && includeDetails) panel.Children.Add(ApplyText("検証済みIssue: " + issue.Url));
        if (creation.EarlierUncertain && (includeDetails || creation.Verified is null || completedBinding))
            panel.Children.Add(completedBinding || creation.UserBound && creation.PreviousAttempt is null && creation.Verified is not null
                ? ApplyText("元の作成要求は結果不明のまま保存されています。")
                : includeDetails ? ApplyMessage("以前の試行に不確定な結果があります", "現在の結果から、以前の試行でIssueが作成されなかったとは判断できません。", InfoBarSeverity.Warning)
                : ApplyText("元の作成要求は結果不明です。"));
        if (!includeDetails) return panel;
        var details = ApplyPanel(4);
        details.Children.Add(ApplyText("保存された診断: " + creation.Reason));
        details.Children.Add(ApplyText($"ローカル行 {creation.LocalId}\n試行 {creation.Id}\nRepository ID {creation.Repository.Id}\n受信ID: {creation.ReceivedId ?? creation.Received?.Id ?? "未確認"}\nIssue ID: {creation.Verified?.Id ?? "未確認"}\nProject項目: {creation.ItemId ?? "未確認"}\n以前の試行不確定: {creation.EarlierUncertain}"));
        if (creation.PreviousAttempt is { } previous) details.Children.Add(ApplyText("以前の試行: " + previous));
        if (creation.UserBound) details.Children.Add(ApplyText("利用者が確認したURLを関連付けました。元の作成成功の証明ではありません。"));
        foreach (var select in creation.SetupIntents ?? creation.Selects.ToArray())
            details.Children.Add(ApplyText($"{select.FieldName} [{select.FieldId}]: {SelectIntentText(select)}"));
        foreach (var intent in creation.SetupPlanningIntents ?? creation.PlanningIntents ?? [])
            details.Children.Add(ApplyText($"{intent.FieldName}: {(intent.Value.Clear ? "クリア" : intent.Value.Value)}"));
        foreach (var intents in creation.EarlierSetupIntents ?? [])
        {
            details.Children.Add(ApplyText("以前に承認した設定（現在の承認には含みません）", emphasis: true));
            foreach (var select in intents)
                details.Children.Add(ApplyText($"{select.FieldName} [{select.FieldId}]: {SelectIntentText(select)}"));
        }
        foreach (var field in creation.Fields ?? []) details.Children.Add(ApplyChange(field, includeHistory: true));
        foreach (var field in creation.EarlierFields ?? [])
        {
            details.Children.Add(ApplyText("以前に承認したフィールド操作", emphasis: true));
            details.Children.Add(ApplyChange(field, includeHistory: true));
        }
        panel.Children.Add(ApplyDetails("作成・設定の記録と識別情報", details, "CreationEvidence-" + creation.Id));
        return panel;
    }
    private static string ApplyStateText(ApplyState state) => state switch
    {
        ApplyState.Pending => "未送信", ApplyState.Running => "実行中（結果未確認）", ApplyState.Succeeded => "反映済み・読み戻し確認済み",
        ApplyState.Failed => "失敗", ApplyState.Unknown => "結果が不確定", ApplyState.Waiting => "待機中",
        ApplyState.Blocked => "保留", ApplyState.Cancelled => "未送信（キャンセル）", ApplyState.Superseded => "以前の承認は終了", _ => state.ToString()
    };
    private async Task ReviewCreationSetupAsync(string batchId, string id)
    {
        var owner = Workspace; var expected = lifetime;
        var generation = applyViewGeneration;
        var session = owner.Drafts;
        var project = owner.Selected?.Snapshot.Id;
        var historyRequest = historyViewGeneration;
        await owner.PrepareCreationSetupAsync(batchId, id);
        if (!IsCurrent(owner, expected) || generation != applyViewGeneration || owner.Selected?.Snapshot.Id != project
            || !ReferenceEquals(owner.Drafts, session) || historyRequest != historyViewGeneration) return;
        if (Workspace.CreationSetupReview is not { } review)
        {
            ShowCreationComparisonFailure(owner, session, batchId, id);
            return;
        }
        if (historyPlace?.SetupCheckFailure?.Target == new HistoryKey(batchId, id, null)) historyPlace.SetupCheckFailure = null;
        var batch = Workspace.Drafts!.Workspace.Journal.Single(b => b.Id == batchId);
        var content = ApplyPanel();
        content.Children.Add(ApplyText(review.Issue.Title, emphasis: true));
        content.Children.Add(ApplyText($"既知Issue: {review.Issue.Url}\nProject: {batch.ProjectName} / {batch.Project.NodeId}"));
        content.Children.Add(ApplyText("Issueを再作成せず、この実行のProject設定を再承認します。以前の送信結果は保持されます。"));
        var values = ApplyPanel(12);
        values.Children.Add(ApplyText("現在のローカル値から承認する設定", emphasis: true));
        foreach (var intent in review.Intents)
            values.Children.Add(ApplyText($"{intent.FieldName} [{intent.FieldId}]: {SelectIntentText(intent)}"));
        foreach (var intent in review.PlanningIntents ?? [])
            values.Children.Add(ApplyText($"{intent.FieldName}: {(intent.Value.Clear ? "クリア" : intent.Value.Value)}"));
        foreach (var intent in review.WithdrawnPlanning ?? [])
            values.Children.Add(ApplyMessage("削除・型変更された計画フィールドの意図を撤回", intent.FieldName, InfoBarSeverity.Warning));
        if (review.Fields is null) values.Children.Add(ApplyText("所属後に初期値を観測します。"));
        foreach (var withdrawn in review.Withdrawn)
            values.Children.Add(ApplyMessage("削除・型変更されたフィールドの意図を撤回", $"{withdrawn.FieldName} [{withdrawn.FieldId}] / 以前の意図は実行履歴に保持", InfoBarSeverity.Warning));
        foreach (var field in review.Fields ?? []) values.Children.Add(ApplyChange(field, includeHistory: false));
        content.Children.Add(new ScrollViewer { MaxHeight = 320, Content = values, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "既知Issueの設定を再承認", Content = content, PrimaryButtonText = "この設定を承認して再開",
            CloseButtonText = "保留", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(dialog, "CreationSetupReviewDialog");
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && IsCurrent(owner, expected) && generation == applyViewGeneration)
        {
            await owner.ConfirmCreationSetupAsync(review);
            if (IsCurrent(owner, expected) && generation == applyViewGeneration) QueueApplyOutcome(batchId, newlyApproved: true);
        }
    }
    private async Task ResolveCreationAsync(string batchId, string id)
    {
        var owner = Workspace; var expected = lifetime;
        var generation = applyViewGeneration;
        var c = Workspace.Drafts!.Workspace.Creations.Single(c => c.Id == id);
        var url = new TextBox { Header = "関連付けるIssue URL" }; AutomationProperties.SetAutomationId(url, "CreationBindUrl");
        var panel = ApplyPanel();
        panel.Children.Add(ApplyText($"{c.Repository.Name}: {c.Title}", emphasis: true));
        panel.Children.Add(ApplyMessage("作成済みの可能性があります", "保留は何も送信しません。既存IssueのURLを確認するか、重複リスクを別途承認して新しい作成を行います。", InfoBarSeverity.Warning));
        panel.Children.Add(ApplyText($"作成試行 {c.Id}"));
        panel.Children.Add(url);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "不確定なIssue作成", Content = panel,
            PrimaryButtonText = "URLを独立確認", SecondaryButtonText = "新規試行を別承認", CloseButtonText = "保留を続ける", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(dialog, "CreationResolutionDialog");
        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            await owner.InspectCreationBindingAsync(batchId, id, url.Text);
            if (!IsCurrent(owner, expected)) return;
            if (Workspace.CreationBindingPreview is not { } issue) return;
            var revision = Workspace.CreationBindingRevision;
            var content = ApplyPanel();
            content.Children.Add(ApplyText(issue.Title, emphasis: true));
            content.Children.Add(ApplyText(issue.Url));
            content.Children.Add(ApplyText($"Repository ID: {issue.RepositoryId}\nIssue ID: {issue.Id}"));
            content.Children.Add(ApplyText("この行に関連付けます。元の作成成功の証明ではなく、GitHub変更も行いません。"));
            var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = "実際のIssueを確認", Content = content,
                PrimaryButtonText = "このIssueに関連付ける", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
            AutomationProperties.SetAutomationId(confirm, "CreationBindingConfirmDialog");
            if (await ShowDialogAsync(confirm) == ContentDialogResult.Primary) await Workspace.ConfirmCreationBindingAsync(batchId, id, issue, revision);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await owner.PrepareCreationRetryAsync(batchId, id);
            if (!IsCurrent(owner, expected)) return;
            if (Workspace.ApplyReview is not { } review) return;
            var acknowledge = new CheckBox { Content = new TextBlock { Text = "以前の試行でIssueが作成済みの可能性と、重複作成のリスクを理解しました。", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 } };
            AutomationProperties.SetAutomationId(acknowledge, "CreationDuplicateAcknowledgement");
            var content = ApplyPanel();
            content.Children.Add(ApplyText($"不確定な以前の試行: {id}"));
            content.Children.Add(new ScrollViewer { MaxHeight = 280, Content = CreationReview(review.Batch.Creations!.Single(), review.Batch.ProjectName), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            content.Children.Add(acknowledge);
            var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = "別の作成試行を承認", Content = content,
                PrimaryButtonText = "重複リスクで新規作成", IsPrimaryButtonEnabled = false, CloseButtonText = "保留", DefaultButton = ContentDialogButton.Close };
            acknowledge.Checked += (_, _) => confirm.IsPrimaryButtonEnabled = true;
            acknowledge.Unchecked += (_, _) => confirm.IsPrimaryButtonEnabled = false;
            AutomationProperties.SetAutomationId(confirm, "CreationRetryConfirmDialog");
            if (await ShowDialogAsync(confirm) == ContentDialogResult.Primary && IsCurrent(owner, expected) && generation == applyViewGeneration)
            {
                await owner.ConfirmCreationRetryAsync(review);
                if (IsCurrent(owner, expected) && generation == applyViewGeneration) QueueApplyOutcome(review.Batch.Id, newlyApproved: true);
            }
        }
    }
    private sealed record ApplyTarget(string Id, string Description)
    {
        public override string ToString() => $"{Description} / {Id}";
    }
}
