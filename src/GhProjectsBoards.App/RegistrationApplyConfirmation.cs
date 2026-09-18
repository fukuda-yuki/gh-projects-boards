using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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

    private sealed record ConfirmationRow(ApplyCandidate Candidate, string Label)
    {
        public override string ToString() => Label;
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
        var owner = Workspace; var expected = lifetime; var projectId = initial.Snapshot.Id;
        var visible = EditorHost.Children.OfType<EditingGrid>().FirstOrDefault()?.DisplayedRowIds ?? [];
        var selectedIds = new HashSet<string>();
        var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 144, MinHeight = 56 };
        AutomationProperties.SetAutomationId(list, "ApplyTargetRows");
        var status = ApplyText(""); AutomationProperties.SetAutomationId(status, "ApplyCheckStatus");
        var counts = ApplyText(""); AutomationProperties.SetAutomationId(counts, "ApplyTargetCounts");
        var reasons = ApplyText(""); AutomationProperties.SetAutomationId(reasons, "ApplyBlockReason");
        var differences = ApplyPanel(12); AutomationProperties.SetAutomationId(differences, "ApplyDifferences");
        var includeHidden = new CheckBox { Content = "非表示行も候補に追加する" };
        AutomationProperties.SetAutomationId(includeHidden, "ApplyIncludeHidden");
        var selectAll = new Button { Content = "表示中の変更をすべて選択" }; AutomationProperties.SetAutomationId(selectAll, "ApplySelectAll");
        var retry = new Button { Content = "最新状態を再確認" }; AutomationProperties.SetAutomationId(retry, "ApplyCheckAgain");
        var connection = new Button { Content = "接続設定" }; AutomationProperties.SetAutomationId(connection, "ApplyConnectionSettings");
        var history = new Button { Content = "反映結果・履歴" }; AutomationProperties.SetAutomationId(history, "ApplyReviewHistory");
        var content = ApplyPanel();
        content.Children.Add(ApplyText($"{initial.Snapshot.Title} / {projectId.Scope.Host} / {owner.ProfileLogin}", true));
        content.Children.Add(ApplyText("反映する行を選び、変更内容を確認してください。\n選択だけでは送信しません。未確定入力は送信しません。"));
        content.Children.Add(status); content.Children.Add(counts);
        var selectionCommands = ApplyPanel(4); selectionCommands.Orientation = Orientation.Horizontal;
        selectionCommands.Children.Add(selectAll); selectionCommands.Children.Add(includeHidden); content.Children.Add(selectionCommands);
        content.Children.Add(list);
        content.Children.Add(new ScrollViewer { Content = differences, MaxHeight = 240, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        content.Children.Add(reasons);
        var recovery = ApplyPanel(); recovery.Orientation = Orientation.Horizontal;
        recovery.Children.Add(retry); recovery.Children.Add(connection); recovery.Children.Add(history); content.Children.Add(recovery);
        content.Children.Add(ApplyDetails("送信先の識別情報", ApplyText($"Project ID {projectId.NodeId}\nアカウント ID {projectId.Scope.ViewerId}\n送信直前にも対象・値・権限を再確認します。"), "ApplyReviewIdentity"));
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "反映内容の確認",
            Content = new ScrollViewer { Content = content, MaxHeight = Math.Max(220, XamlRoot.Size.Height - 200), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = "GitHubに反映（0件）", IsPrimaryButtonEnabled = false,
            CloseButtonText = "編集へ戻る", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(dialog, "ApplyReviewDialog");
        bool populating = false, open = true, suspended = false, checking = false, goConnection = false, goHistory = false;
        int request = 0;
        ApplyReview? review = null;
        var retainedCandidates = new Dictionary<string, ApplyCandidate>();
        Task checkingTask = Task.CompletedTask;
        bool Current() => open && !suspended && IsCurrent(owner, expected) && owner.Selected?.Snapshot.Id == projectId && ReferenceEquals(owner.Drafts, session);

        void UpdateApproval()
        {
            if (!open) return;
            var reason = checking ? "GitHubの最新状態を確認中です。完了するまで反映できません。"
                : !Current() ? "接続先またはProjectが変わりました。元の作業を保持しました。対象を確認し直してください。"
                : owner.ApplyBlockReason(review);
            dialog.IsPrimaryButtonEnabled = reason is null;
            reasons.Text = reason ?? "選択した変更だけを送信します。未確定入力は送信しません。";
            dialog.PrimaryButtonText = $"GitHubに反映（{review?.IssueCount ?? 0}件）";
            retry.IsEnabled = !checking && Current();
        }
        void Populate()
        {
            if (!Current()) { UpdateApproval(); return; }
            populating = true;
            var p = owner.Selected!;
            var fresh = session.Workspace.ApplyCandidates(p);
            foreach (var candidate in fresh) retainedCandidates[candidate.Id] = candidate;
            var candidates = fresh.Concat(retainedCandidates.Values.Where(c => selectedIds.Contains(c.Id) && fresh.All(n => n.Id != c.Id))
                .Select(c => c with { Fields = c.Fields.Select(f => session.Workspace.Fields.SingleOrDefault(n => n.Key == f.Key) ?? f).ToArray() })).ToArray();
            var displayed = candidates.Where(c => includeHidden.IsChecked == true || visible.Contains(c.Id)).ToArray();
            selectedIds.IntersectWith(displayed.Select(c => c.Id));
            var rows = displayed.Select(c => new ConfirmationRow(c, c.Identity + (c.IsCreation
                ? (session.Workspace.LocalProblems(p, c.Id).Length > 0 ? " / 準備中: " + string.Join("、", session.Workspace.LocalProblems(p, c.Id)) : " / 新規作成")
                : c.Changes > 0 ? $" / {c.Changes}フィールド変更" : " / 未確定入力・確認が必要"))).ToArray();
            list.ItemsSource = rows;
            foreach (var row in rows.Where(r => selectedIds.Contains(r.Candidate.Id))) list.SelectedItems.Add(row);
            counts.Text = $"変更候補 {displayed.Length}行 / 選択 {selectedIds.Count}行 / 非表示の作業 {candidates.Count(c => !visible.Contains(c.Id))}行\n"
                + $"更新 {review?.UpdatedIssues ?? 0}件・新規作成 {review?.CreatedIssues ?? 0}件 / フィールド変更 {review?.Batch.Operations.Length ?? 0}件";
            differences.Children.Clear();
            foreach (var candidate in displayed.Where(c => selectedIds.Contains(c.Id)))
                differences.Children.Add(CandidateDetails(candidate, review, !checking && review is not null, () => QueueCheck()));
            if (selectedIds.Count == 0) differences.Children.Add(ApplyText("反映する行を選ぶと、GitHubの値 → 反映する値をここで確認できます。"));
            populating = false;
            UpdateApproval();
        }
        async Task CheckLoop()
        {
            while (Current())
            {
                var thisRequest = request;
                checking = true; review = null;
                status.Text = "GitHubの最新状態を確認中…"; Populate();
                var ids = selectedIds.ToHashSet();
                await owner.PrepareApplyAsync(ids, new(projectId, visible, ids.ToArray(), includeHidden.IsChecked == true));
                if (!Current()) break;
                if (thisRequest != request) continue;
                checking = false; review = owner.ApplyReview;
                status.Text = review is null ? owner.Status : $"最新確認済み {review.Batch.ReviewedAt.LocalDateTime:g}（まだ送信していません）";
                if (review is null && owner.Status.Contains("表示対象が変わりました"))
                {
                    selectedIds.Clear();
                    visible = EditorHost.Children.OfType<EditingGrid>().FirstOrDefault()?.DisplayedRowIds ?? [];
                    status.Text += " 対象を広げず、選択を解除しました。行を選び直してください。";
                }
                Populate();
                break;
            }
            checking = false; UpdateApproval();
        }
        void QueueCheck()
        {
            if (!Current()) return;
            request++; review = null; dialog.IsPrimaryButtonEnabled = false;
            if (!checkingTask.IsCompleted) { owner.Cancel(); return; }
            // The task yields in the existing workspace runner; no automatic writes are involved.
            checkingTask = CheckLoop();
        }
        list.SelectionChanged += (_, _) =>
        {
            if (populating) return;
            selectedIds.Clear(); selectedIds.UnionWith(list.SelectedItems.Cast<ConfirmationRow>().Select(r => r.Candidate.Id));
            QueueCheck();
        };
        includeHidden.Checked += (_, _) => { Populate(); QueueCheck(); };
        includeHidden.Unchecked += (_, _) => { Populate(); QueueCheck(); };
        selectAll.Click += (_, _) => { populating = true; list.SelectAll(); selectedIds.UnionWith(list.Items.Cast<ConfirmationRow>().Select(r => r.Candidate.Id)); populating = false; QueueCheck(); };
        retry.Click += (_, _) => QueueCheck();
        connection.Click += (_, _) => { goConnection = true; suspended = true; owner.Cancel(); dialog.Hide(); };
        history.Click += (_, _) => { goHistory = true; suspended = true; owner.Cancel(); dialog.Hide(); };
        dialog.PrimaryButtonClick += (_, args) => { if (checking || owner.ApplyBlockReason(review) is not null || !Current()) { args.Cancel = true; UpdateApproval(); } };
        void Changed() { if (DispatcherQueue.HasThreadAccess) UpdateApproval(); else DispatcherQueue.TryEnqueue(UpdateApproval); }
        owner.Changed += Changed; session.Changed += Changed;
        applyDialog = true; ApplyHistory.IsEnabled = false;
        bool applied = false;
        try
        {
            Populate();
            while (open)
            {
                var showing = ShowDialogAsync(dialog);
                QueueCheck();
                var result = await showing;
                suspended = true; owner.Cancel();
                await checkingTask;
                if (goConnection)
                {
                    goConnection = false;
                    connectionReturn = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    ConnectionRequested?.Invoke(this, EventArgs.Empty);
                    await connectionReturn.Task;
                    connectionReturn = null;
                    if (!IsCurrent(owner, expected) || owner.Selected?.Snapshot.Id != projectId || owner.Profile != projectId.Scope) break;
                    suspended = false; review = null; Populate();
                    continue;
                }
                if (result == ContentDialogResult.Primary && review is not null)
                {
                    await owner.ConfirmApplyAsync(review);
                    applied = session.Workspace.Journal.Any(b => b.Id == review.Batch.Id);
                }
                break;
            }
        }
        finally
        {
            open = false; owner.Changed -= Changed; session.Changed -= Changed;
            applyDialog = false;
            if (IsLoaded) Update();
        }
        // Completion must not move native focus away from an active IME composition.
        // The persistent workspace result and history command remain available.
        if ((applied || goHistory) && IsCurrent(owner, expected) && CanRefreshEditors()) ShowApplyHistory(this, new RoutedEventArgs());
    }

    private StackPanel CandidateDetails(ApplyCandidate candidate, ApplyReview? review, bool checkedLatest, Action changed)
    {
        var panel = ApplyPanel(4);
        panel.Children.Add(ApplyText(candidate.Identity, true));
        if (candidate.Missing) panel.Children.Add(ApplyMessage("取得結果で確認できません", "変更は保持しています。最新状態を確認できるまで送信できません。", InfoBarSeverity.Warning));
        if (candidate.IsCreation)
        {
            var local = Workspace.Drafts!.Workspace.LocalRows.Single(r => r.Id == candidate.Id);
            panel.Children.Add(ApplyText($"新規Issue / Repository: {local.Repository}\nタイトル: {local.Title}"));
            foreach (var select in local.Selects) panel.Children.Add(ApplyText($"このProjectの {select.FieldName}{HiddenColumnNote(select.FieldId)}: {SelectIntentText(select)}"));
            foreach (var problem in Workspace.Drafts.Workspace.LocalProblems(Workspace.Selected!, local.Id)) panel.Children.Add(ApplyText("準備中: " + problem));
            if (local.TitleBuffer is { } title) panel.Children.Add(ApplyText("送らない未確定入力（タイトル）: " + title));
            if (local.RepositoryBuffer is { } repository) panel.Children.Add(ApplyText("送らない未確定入力（Repository）: " + repository));
        }
        foreach (var field in candidate.Fields.Where(f => f.Change is not null || f.Buffer is not null || f.Conflict))
        {
            var definition = Workspace.Selected!.Snapshot.Fields.SingleOrDefault(f => f.Id.NodeId == field.Key.FieldId);
            var name = field.Key.Kind == "Title" ? "タイトル（Issue共通）" : $"{definition?.Name ?? field.Key.FieldId}（このProjectのフィールド）{HiddenColumnNote(field.Key.FieldId)}";
            string Value(string? value) => value is null ? "（空値）" : field.Key.Kind == "Title" ? value : definition?.Options.SingleOrDefault(o => o.Id == value)?.Name ?? value;
            var remote = field.Observation;
            var current = checkedLatest && remote?.Availability is ValueAvailability.Present or ValueAvailability.Empty
                ? Value(remote!.Value) : $"保存済み {Value(field.Baseline)}（{field.RetrievedAt.LocalDateTime:g} / 最新未確認）";
            panel.Children.Add(ApplyText(name, true));
            panel.Children.Add(ApplyText($"GitHubの値: {current} → 反映する値: {(field.Change is null ? "変更なし" : Value(field.Change.Value))}"));
            if (field.Buffer is { } buffer) panel.Children.Add(ApplyText("送らない未確定入力: " + buffer));
            if (field.Conflict || remote?.Reason is { } reason && !reason.StartsWith("未確定文字"))
                panel.Children.Add(ApplyText(field.Conflict ? "競合: GitHubと端末内の両方で変更されています。" : remote!.Reason!));
            if (checkedLatest && field.Conflict && field.Buffer is null && remote is { Reason: null, Availability: ValueAvailability.Present or ValueAvailability.Empty })
            {
                var decision = Workspace.Drafts!.Workspace.Decision(field.Key);
                foreach (var useRemote in new[] { true, false })
                {
                    var button = new Button { Content = useRemote ? "GitHubの値を使う" : "自分の変更を使う" };
                    AutomationProperties.SetAutomationId(button, $"ApplyResolve-{candidate.Id}-{field.Key.Kind}{(field.Key.FieldId is null ? "" : "-" + field.Key.FieldId)}-{(useRemote ? "Remote" : "Local")}");
                    button.Click += async (_, _) =>
                    {
                        button.IsEnabled = false;
                        var chosen = useRemote ? new LocalValue(remote.Value, field.Key.Kind != "Title" && remote.Value is null) : field.Change!;
                        await Workspace.Drafts!.CommitAsync(w => { w.Resolve(Workspace.Selected!.Snapshot.Id.NodeId, decision, chosen); return w; }, () => CanRefreshEditors());
                        changed();
                    };
                    panel.Children.Add(button);
                }
            }
        }
        if (review?.Blocked.Length > 0)
            foreach (var blocked in review.Blocked.Where(b => b.Contains(candidate.Id))) panel.Children.Add(ApplyText(blocked));
        return panel;
    }
}
