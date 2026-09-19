using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

public sealed partial class RegistrationPanel
{
    private TaskCompletionSource? connectionReturn;
    internal void ReturnFromConnection() => connectionReturn?.TrySetResult();
    internal bool CanLeaveForConnection()
    {
        if (CanRefreshEditors()) return true;
        Status.Text = "IME変換中です。自然に確定・取消してから接続設定を開いてください。";
        WorkspaceStatusBar.Visibility = Visibility.Visible;
        return false;
    }

    private async void ReviewApply(object sender, RoutedEventArgs e)
    {
        if (applyDialog || Workspace.Selected is not { } initial || Workspace.Drafts is not { } session) return;
        if (!CanRefreshEditors())
        {
            Status.Text = "IME変換中です。自然に確定・取消してから反映内容の確認を開いてください。";
            WorkspaceStatusBar.Visibility = Visibility.Visible;
            return;
        }
        var owner = Workspace; var expected = lifetime; var projectId = initial.Snapshot.Id; var login = owner.ProfileLogin;
        var visible = EditorHost.Children.OfType<EditingGrid>().FirstOrDefault()?.DisplayedRowIds ?? [];
        var selectedIds = new HashSet<string>();
        var table = new ApplyConfirmationTable();
        var status = ApplyText(""); AutomationProperties.SetAutomationId(status, "ApplyCheckStatus");
        var counts = ApplyText(""); AutomationProperties.SetAutomationId(counts, "ApplyTargetCounts");
        var reasons = ApplyText(""); AutomationProperties.SetAutomationId(reasons, "ApplyBlockReason");
        AutomationProperties.SetLiveSetting(reasons, AutomationLiveSetting.Polite);
        var includeHidden = new CheckBox { Content = "非表示行も候補に追加する" };
        AutomationProperties.SetAutomationId(includeHidden, "ApplyIncludeHidden");
        var hiddenText = ApplyText(""); AutomationProperties.SetAutomationId(hiddenText, "ApplyHiddenSummary");
        var retry = new Button { Content = "最新状態を再確認" }; AutomationProperties.SetAutomationId(retry, "ApplyCheckAgain");
        var connection = new Button { Content = "接続設定" }; AutomationProperties.SetAutomationId(connection, "ApplyConnectionSettings");
        var history = new Button { Content = "反映結果・履歴" }; AutomationProperties.SetAutomationId(history, "ApplyReviewHistory");
        var jump = new Button { Content = "問題の行へ" }; AutomationProperties.SetAutomationId(jump, "ApplyGoToProblem");
        var restart = new Button { Content = "再確認してやり直す" }; AutomationProperties.SetAutomationId(restart, "ApplyRestartReview");
        var recovery = ApplyPanel(4);
        recovery.Children.Add(ApplyText("前回の承認を取り消して再確認します。反映済みの変更は保持し、ここでは送信しません。"));
        recovery.Children.Add(restart);
        var informationText = ApplyText("");
        var information = new Button { Content = "確認情報", Flyout = new Flyout { Content = informationText } };
        AutomationProperties.SetAutomationId(information, "ApplyReviewIdentity");
        var legend = ApplyText("GitHubの値 → 反映する値（Projectフィールド）");
        var header = ApplyPanel(4);
        header.Children.Add(ApplyText($"{initial.Snapshot.Title} / {projectId.Scope.Host} / {login}", true));
        header.Children.Add(counts); header.Children.Add(status); header.Children.Add(legend);
        var hidden = ApplyPanel(4); hidden.Children.Add(hiddenText); hidden.Children.Add(includeHidden);
        var problem = ApplyPanel(4); problem.Children.Add(reasons);
        problem.Children.Add(recovery);
        var actions = ApplyPanel(8); actions.Orientation = Orientation.Horizontal;
        actions.Children.Add(jump); actions.Children.Add(connection); problem.Children.Add(actions);
        var auxiliary = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 580 };
        foreach (var button in new[] { retry, information, history })
        {
            button.Content = new TextBlock { Text = (string)button.Content, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(button, auxiliary.ColumnDefinitions.Count);
            auxiliary.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); auxiliary.Children.Add(button);
        }
        var content = new Grid { RowSpacing = 8 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            content.RowDefinitions.Add(new() { Height = height });
        var sections = new FrameworkElement[] { header, hidden, problem, table, auxiliary };
        for (var i = 0; i < sections.Length; i++) { Grid.SetRow(sections[i], i); content.Children.Add(sections[i]); }
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "反映内容の確認", Content = content,
            PrimaryButtonText = "GitHubに反映（確認中）", IsPrimaryButtonEnabled = false,
            CloseButtonText = "編集へ戻る", DefaultButton = ContentDialogButton.Close };
        void SizeReview()
        {
            content.Width = Math.Min(1100, Math.Max(280, XamlRoot.Size.Width - 112));
            content.Height = Math.Max(180, Math.Min(600, XamlRoot.Size.Height - 200));
            dialog.Resources["ContentDialogMaxWidth"] = content.Width + 64;
            informationText.MaxWidth = Math.Max(240, Math.Min(560, content.Width - 64));
        }
        SizeReview();
        var reviewRoot = XamlRoot;
        void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeReview();
        reviewRoot.Changed += RootChanged;
        AutomationProperties.SetAutomationId(dialog, "ApplyReviewDialog");
        bool open = true, suspended = false, checking = false, goConnection = false, goHistory = false, restartRequested = false;
        int request = 0, nextProblem = 0;
        ApplyReview? review = null;
        ApplyConfirmationPresentation? presentation = null;
        ConfirmationColumn[] retainedColumns = [];
        var retainedCandidates = new Dictionary<string, ApplyCandidate>();
        Task checkingTask = Task.CompletedTask;
        bool Current() => open && !suspended && IsCurrent(owner, expected) && owner.Selected?.Snapshot.Id == projectId && ReferenceEquals(owner.Drafts, session);

        string[] ProblemIds() => presentation?.Rows.Where(r => selectedIds.Contains(r.Id) && r.Problems.Length > 0).Select(r => r.Id).ToArray() ?? [];
        void UpdateApproval()
        {
            if (!open) return;
            var reason = checking ? null : !Current() ? "接続先またはProjectが変わりました。元の作業を保持しました。対象を確認し直してください。"
                : owner.ApplyBlockReason(review);
            dialog.IsPrimaryButtonEnabled = !checking && Current() && reason is null;
            var problemIds = ProblemIds();
            reasons.Text = reason ?? "";
            recovery.Visibility = !checking && Current() && owner.CanRestartApplyReview ? Visibility.Visible : Visibility.Collapsed;
            restart.IsEnabled = !checking && Current() && owner.CanRestartApplyReview;
            jump.Visibility = problemIds.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            connection.Visibility = owner.ApplyNeedsConnectionRecovery ? Visibility.Visible : Visibility.Collapsed;
            problem.Visibility = reasons.Text.Length > 0 || connection.Visibility == Visibility.Visible || recovery.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            dialog.PrimaryButtonText = checking ? "GitHubに反映（確認中）" : review is null ? "GitHubに反映（確認が必要）" : $"GitHubに反映（{review.IssueCount}件）";
            retry.IsEnabled = !checking && Current();
            informationText.Text = $"{initial.Snapshot.Title}\nProject ID {projectId.NodeId}\n{projectId.Scope.Host} / {login}\nアカウント ID {projectId.Scope.ViewerId}\n"
                + (review is null ? $"保存済み情報 {owner.Selected?.RetrievedAt.LocalDateTime:g} / 最新未確認" : $"最新確認済み {review.Batch.ReviewedAt.LocalDateTime:g}")
                + "\n最終反映ボタンを押すまで送信しません。送信直前にも対象・値・権限を再確認します。";
        }
        void Populate()
        {
            if (!Current()) { UpdateApproval(); return; }
            var p = owner.Selected!;
            var fresh = session.Workspace.ApplyCandidates(p);
            foreach (var candidate in fresh) retainedCandidates[candidate.Id] = candidate;
            var candidates = fresh.Concat(retainedCandidates.Values.Where(c => selectedIds.Contains(c.Id) && fresh.All(n => n.Id != c.Id))
                .Select(c => c with { Fields = c.Fields.Select(f => session.Workspace.Fields.SingleOrDefault(n => n.Key == f.Key) ?? f).ToArray() })).ToArray();
            selectedIds.IntersectWith(candidates.Where(c => includeHidden.IsChecked == true || visible.Contains(c.Id)).Select(c => c.Id));
            presentation = ApplyConfirmationPresentation.Create(session.Workspace, p, candidates, visible, includeHidden.IsChecked == true,
                selectedIds, review, !checking && review is not null, retainedColumns);
            retainedColumns = presentation.Columns;
            counts.Text = presentation.Summary;
            hidden.Visibility = presentation.HiddenCount == 0 ? Visibility.Collapsed : Visibility.Visible;
            hiddenText.Text = presentation.HiddenSummary;
            legend.Text = "GitHubの値 → 反映する値" + (presentation.Columns.Any(c => c.Id == ColumnIdentity.Title)
                ? "（タイトルはIssue共通、ほかはこのProject）" : "（Projectフィールド）");
            table.Update(presentation, selectedIds);
            UpdateApproval();
        }
        async Task CheckLoop()
        {
            while (Current())
            {
                var thisRequest = request;
                var restartThisCheck = restartRequested; restartRequested = false;
                checking = true; review = null;
                status.Text = "GitHubの最新状態を確認中…"; Populate();
                var ids = selectedIds.ToHashSet();
                var targets = new RowTargetSelection(projectId, visible, ids.ToArray(), includeHidden.IsChecked == true);
                if (restartThisCheck) await owner.RestartApplyReviewAsync(ids, targets);
                else await owner.PrepareApplyAsync(ids, targets);
                if (!Current()) break;
                if (thisRequest != request) continue;
                checking = false; review = owner.ApplyReview;
                status.Text = review is null ? owner.Status + "（表示は保存済み・最新未確認）" : "最新確認済み";
                if (review is null && owner.ApplySelectionInvalidated)
                {
                    selectedIds.Clear();
                    visible = EditorHost.Children.OfType<EditingGrid>().FirstOrDefault()?.DisplayedRowIds ?? [];
                    status.Text += " 対象を広げず、選択を解除しました。行を選び直してください。";
                }
                Populate(); break;
            }
            checking = false; UpdateApproval();
        }
        void QueueCheck(bool restartApproval = false)
        {
            if (!Current()) return;
            request++; review = null; dialog.IsPrimaryButtonEnabled = false;
            restartRequested |= restartApproval;
            if (!checkingTask.IsCompleted) { owner.Cancel(); return; }
            checkingTask = CheckLoop();
        }
        table.SelectionUpdated = () => { selectedIds.Clear(); selectedIds.UnionWith(table.SelectedIds); QueueCheck(); };
        table.Resolve = async (cell, useRemote) =>
        {
            if (!Current() || checking || !cell.CanResolve) return;
            var field = cell.Draft!;
            var decision = session.Workspace.Decision(field.Key);
            var chosen = useRemote ? new LocalValue(field.Observation!.Value, field.Key.Kind != "Title" && field.Observation.Value is null) : field.Change!;
            checking = true; status.Text = "競合の解決を保存中…"; Populate();
            try { await session.CommitAsync(w => { w.Resolve(projectId.NodeId, decision, chosen); return w; }, () => Current() && CanRefreshEditors()); }
            finally { checking = false; if (Current()) QueueCheck(); }
        };
        includeHidden.Checked += (_, _) => { Populate(); QueueCheck(); };
        includeHidden.Unchecked += (_, _) => { Populate(); QueueCheck(); };
        retry.Click += (_, _) => QueueCheck();
        restart.Click += (_, _) => { if (!checking && owner.CanRestartApplyReview) QueueCheck(restartApproval: true); };
        jump.Click += (_, _) => { var ids = ProblemIds(); if (ids.Length > 0) table.GoToProblem(ids[nextProblem++ % ids.Length]); };
        connection.Click += (_, _) => { goConnection = true; suspended = true; owner.Cancel(); dialog.Hide(); };
        history.Click += (_, _) => { goHistory = true; suspended = true; owner.Cancel(); dialog.Hide(); };
        dialog.PrimaryButtonClick += (_, args) => { if (checking || owner.ApplyBlockReason(review) is not null || !Current()) { args.Cancel = true; UpdateApproval(); } };
        void Changed() { if (DispatcherQueue.HasThreadAccess) UpdateApproval(); else DispatcherQueue.TryEnqueue(UpdateApproval); }
        owner.Changed += Changed; session.Changed += Changed;
        applyDialog = true; ApplyHistory.IsEnabled = false;
        bool applied = false;
        var outcomeGeneration = applyViewGeneration;
        try
        {
            Populate();
            while (open)
            {
                var showing = ShowDialogAsync(dialog);
                QueueCheck();
                var result = await showing;
                suspended = true; owner.Cancel(); await checkingTask;
                if (goConnection)
                {
                    goConnection = false;
                    connectionReturn = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    ConnectionRequested?.Invoke(this, EventArgs.Empty);
                    await connectionReturn.Task; connectionReturn = null;
                    if (!IsCurrent(owner, expected) || owner.Selected?.Snapshot.Id != projectId || owner.Profile != projectId.Scope) break;
                    suspended = false; review = null; Populate(); continue;
                }
                if (result == ContentDialogResult.Primary && review is not null)
                {
                    outcomeGeneration = applyViewGeneration;
                    await owner.ConfirmApplyAsync(review);
                    applied = session.Workspace.Journal.Any(b => b.Id == review.Batch.Id);
                }
                break;
            }
        }
        finally
        {
            open = false; reviewRoot.Changed -= RootChanged; owner.Changed -= Changed; session.Changed -= Changed;
            applyDialog = false;
            if (IsLoaded) Update();
        }
        if (goHistory && IsCurrent(owner, expected) && CanRefreshEditors()) ShowApplyHistory(this, new RoutedEventArgs());
        else if (applied && IsCurrent(owner, expected) && outcomeGeneration == applyViewGeneration) QueueApplyOutcome(review!.Batch.Id);
    }
}
