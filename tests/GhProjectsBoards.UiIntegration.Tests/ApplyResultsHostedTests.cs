using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test, Category("ApplyOutcomeRecovery")]
    public async Task CancelledApprovedExecutionRetainsUnsentWorkAndOffersTheExistingHistoryRoute()
    {
        await ControlExternal("ApplyObservation"); gate!.Armed = false;
        await Ui.Run(() => {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "Retained after cancellation");
            Ui.Click("ReviewApplyButton");
        });
        await SelectOutcomeReviewRow("P1-T1");
        await Ui.Run(() => { gate.Armed = true; Ui.DialogButton("ApplyReviewDialog", "PrimaryButton"); });
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Ui.Run(() => Ui.Click("CancelProjectButton"));
        await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gate.Release.TrySetResult();
        await Ui.DialogReady("ApplyOutcomeWarning");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("ApplyOutcomeWarning")!;
            Assert.That(Ui.DialogText("ApplyOutcomeWarning"), Does.Contain("反映結果・履歴").And.Not.Contain("未反映の変更を確認…"));
            Assert.That(dialog.SecondaryButtonText, Is.Empty);
            Assert.That(Work.Journal.Single().Operations.Single().State, Is.EqualTo(ApplyState.Cancelled));
            Assert.That(Work.Fields.Single(field => field.Key == new FieldKey("Title", "I1")).Change?.Value, Is.EqualTo("Retained after cancellation"));
            Assert.That(h.Writes, Is.Empty);
            Ui.DialogButton("ApplyOutcomeWarning", "CloseButton");
        });
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Until(() => Ui.Popup<Button>("ResumeApplyBatch-" + Work.Journal.Single().Id) is { IsLoaded: true });
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("ResumeApplyBatch-" + Work.Journal.Single().Id, Ui.Dialog("ApplyHistoryDialog")).Content, Is.EqualTo("確認して再開"));
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
        Assert.That(h.Writes, Is.Empty);
    }

    [Test, Category("ApplyOutcomeRecovery")]
    public async Task MixedSuccessfulUpdateAndUnknownCreationOpenTheCreationStagesAndKeepTheApprovedIntent()
    {
        string local = "";
        await Ui.Run(() => {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "Existing update completed");
            local = h.Add("New investigation");
            Work.Commit("P1", Work.Open(Workspace.Selected!).Single(row => row.ItemId == local).Cells[1], "done", true);
            h.LoseCreate = true;
            Ui.Click("ReviewApplyButton");
        });
        await SelectOutcomeReviewRow("P1-T1");
        await SelectOutcomeReviewRow(local);
        await Ui.Run(() => {
            Assert.That(Ui.DialogText("ApplyReviewDialog"), Does.Contain("sample-user/first").And.Contain("New investigation")
                .And.Contain(Workspace.Selected!.Snapshot.Title + "へ追加予定").And.Contain("新規・未送信 → Done"));
            Ui.DialogButton("ApplyReviewDialog", "PrimaryButton");
        });
        await Ui.DialogReady("ApplyOutcomeWarning");
        await Ui.Run(async () => {
            var text = Ui.DialogText("ApplyOutcomeWarning");
            Assert.That(text, Does.Contain("タイトル → Existing update completed：反映を確認しました")
                .And.Contain("Issue作成：送信済み・結果未確認").And.Contain("への追加：未実行（Issueの確認待ち）")
                .And.Contain("Status → Done：設定予定・未実行").And.Not.Contain("0フィールド"));
            Assert.That(Work.Creations.Single().MembershipDispatched, Is.False);
            await ApplyInformationEvidence.Capture(Ui.Dialog("ApplyOutcomeWarning")!, "mixed-success-and-unknown-creation-stages");
            Ui.DialogButton("ApplyOutcomeWarning", "CloseButton");
        });
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Until(() => Ui.Popup<Expander>("CreationEvidence-" + Work.Creations.Single().Id) is { IsLoaded: true });
        await Ui.Run(() => {
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("New investigation").And.Contain("Status → Done：設定予定・未実行"));
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
        Assert.That(h.Writes.Count(write => write.Query.Contains("ApplyTitle")), Is.EqualTo(1));
        Assert.That(h.Issues, Has.Count.EqualTo(1));
        Assert.That(h.Members, Is.Empty);
    }

    [Test, Category("ApplyOutcomeRecovery")]
    public async Task PartialOutcomeAndFailedDetailExposeVerifiedStatusAndExactPermissionTarget()
    {
        await Ui.Run(() => {
            var rows = Work.Open(Workspace.Selected!);
            Work.Commit("P1", rows[0].Cells[0], "Weekly review completed");
            Work.Commit("P1", rows[0].Cells[1], "done", true);
            Work.Commit("P1", rows[1].Cells[0], "Independent draft");
            Work.SetBuffer(rows[1].Cells[0], "Z");
            h.Existing.MutationResult = (query, _) => query.Contains("ApplyTitle") ? ScriptedRunner.Http("{}", 403) : null;
            Ui.Click("ReviewApplyButton");
        });
        await SelectOutcomeReviewRow("P1-T1");
        await Ui.Run(() => {
            var row = Ui.Find<FrameworkElement>("ApplyRow-P1-T2", Ui.Dialog("ApplyReviewDialog"));
            var text = string.Join("\n", Ui.Tree(row).OfType<TextBlock>().Select(block => block.Text));
            Assert.That(text, Does.Contain("Independent draft").And.Contain("送らない未確定入力: Z").And.Contain("保持"));
            Assert.That(text, Does.Not.Contain("確定・取消してから再取得"), "Unselected preserved input is not a prerequisite for sending Issue #1.");
            Assert.That(Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled, Is.True);
        });
        await Ui.Run(() => Ui.DialogButton("ApplyReviewDialog", "PrimaryButton"));
        await Ui.DialogReady("ApplyOutcomeWarning");
        await Ui.Run(async () => {
            var text = Ui.DialogText("ApplyOutcomeWarning");
            Assert.That(text, Does.Contain("今回の反映は一部完了しました").And.Contain("sample-user/first #1")
                .And.Contain("Status → Done：反映を確認しました").And.Contain("タイトル更新が拒否")
                .And.Contain("ローカルに保持").And.Contain("アクセス権・認証状態")
                .And.Contain("https://github.com/sample-user/first/issues/1").And.Contain("アカウント"));
            Assert.That(text, Does.Contain("Issue 1").And.Contain("Weekly review completed"), "The failed result must expose both frozen approved Title values.");
            Assert.That(text, Does.Not.Contain("保存済みのタイトル").And.Not.Contain("Independent draft").And.Not.Contain("OAuth"));
            await ApplyInformationEvidence.Capture(Ui.Dialog("ApplyOutcomeWarning")!, "partial-outcome-title-permission-status-verified");
            Ui.DialogButton("ApplyOutcomeWarning", "CloseButton");
        });
        var before = System.Text.Json.JsonSerializer.Serialize(Work.Snapshot());
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Until(() => Ui.Popup<ScrollViewer>("ApplyHistoryEvidence") is { IsLoaded: true });
        await Ui.Run(async () => {
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("同じ実行で反映を確認済み")
                .And.Contain("Status → Done").And.Contain("対象Issue：https://github.com/sample-user/first/issues/1")
                .And.Contain("送信後の値の確認: 未確認"));
            await ApplyInformationEvidence.Capture(Ui.Dialog("ApplyHistoryDialog")!, "failed-title-with-same-execution-status");
            Ui.DialogButton("ApplyHistoryDialog", "SecondaryButton");
        });
        await Ui.Until(() => Ui.Popup<CheckBox>("ApplyShowAllHistory") is { IsLoaded: true });
        await Ui.Run(() => {
            Assert.That(Ui.Find<CheckBox>("ApplyShowAllHistory", Ui.Dialog("ApplyHistoryDialog")).IsChecked, Is.False);
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Not.Contain("反映済み・読み戻し確認済み"));
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("同じ実行で反映を確認済み").And.Contain("Status → Done"),
                "The current incomplete execution must retain its success context without enabling all historical successes.");
            Assert.That(System.Text.Json.JsonSerializer.Serialize(Work.Snapshot()), Is.EqualTo(before));
        });
        var statusId = Work.Journal.Single().Operations.Single(operation => operation.Key.Kind == "Select").Id;
        await Ui.Until(() => Ui.Popup<Button>("ApplyOperationDetails-" + statusId) is { IsLoaded: true });
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ApplyOperationDetails-" + statusId, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.Until(() => Ui.Popup<Expander>("ApplyOperationEvidence-" + statusId) is { IsLoaded: true });
        await Ui.Run(async () => {
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("Done").And.Contain("このProjectの未反映の変更")
                .And.Contain("この実行の承認"), "Broader recovery must not appear to revoke or resend the successful Status field.");
            await ApplyInformationEvidence.Capture(Ui.Dialog("ApplyHistoryDialog")!, "successful-status-scopes-remaining-work-actions");
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
        Assert.That(h.Writes.Count, Is.EqualTo(2));
    }

    [Test, Category("ApplyOutcomeRecovery")]
    public async Task KnownFailureReviewsRemainingWorkAndNewExecutionReplacesOnlyThePreviousHistoryDestination()
    {
        var failedId = await PrepareOutcomeFailure();
        await OpenOutcomeFailure(failedId);
        await Ui.Run(() => {
            h.Existing.MutationResult = null;
            var button = Ui.Find<Button>("ResumeApplyBatch-" + Work.Journal.Single().Id, Ui.Dialog("ApplyHistoryDialog"));
            Assert.That(button.Content, Is.EqualTo("未反映の変更を確認…"));
            Ui.Click(button);
        });
        await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("ApplyTargetRows", Ui.Dialog("ApplyReviewDialog"));
            Assert.That(list.SelectedItems, Is.Empty, "Independent work remains an explicit selection.");
            Assert.That(list.Items.Cast<ApplyConfirmationRow>().Select(row => row.Data.Id), Is.EquivalentTo(new[] { "P1-T1", "P1-T2" }));
            Assert.That(Work.Journal.Single().Operations.Single(operation => operation.Id == failedId).State, Is.EqualTo(ApplyState.Superseded));
            Assert.That(h.Writes.Count, Is.EqualTo(2), "Fresh comparison is not a send.");
        });
        await SelectOutcomeReviewRow("P1-T1");
        await Ui.Run(() => Ui.DialogButton("ApplyReviewDialog", "PrimaryButton"));
        await Ui.Until(() => !Workspace.IsBusy && Work.Journal.Count == 2 && Work.Journal.Last().Operations.All(operation => operation.State == ApplyState.Succeeded));
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog") is null);
        await Task.Delay(400);
        var newId = Work.Journal.Last().Operations.Single().Id;
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Until(() => Ui.Popup<Expander>("ApplyOperationEvidence-" + newId) is { IsLoaded: true });
        await Ui.Run(async () => {
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("送信後の値の確認: Weekly review completed")
                .And.Contain("以前の実行で確認済み（今回は送信していません）").And.Contain("Status → Done"));
            Assert.That(Ui.Dialog("ApplyOutcomeWarning"), Is.Null);
            await ApplyInformationEvidence.Capture(Ui.Dialog("ApplyHistoryDialog")!, "new-title-execution-direct-history-destination");
            Ui.DialogButton("ApplyHistoryDialog", "SecondaryButton");
        });
        await Ui.Until(() => Ui.Popup<CheckBox>("ApplyShowAllHistory") is { IsLoaded: true });
        await Ui.Run(() => {
            Assert.That(Ui.Find<CheckBox>("ApplyShowAllHistory", Ui.Dialog("ApplyHistoryDialog")).IsChecked, Is.False);
            Ui.Toggle(Ui.Find<CheckBox>("ApplyShowAllHistory", Ui.Dialog("ApplyHistoryDialog")));
        });
        await Ui.Until(() => Ui.Popup<Button>("ApplyOperationDetails-" + failedId) is { IsLoaded: true });
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ApplyOperationDetails-" + failedId, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.Until(() => Ui.Popup<Expander>("ApplyOperationEvidence-" + failedId) is { IsLoaded: true });
        await Ui.Run(() => {
            var text = Ui.DialogText("ApplyHistoryDialog");
            Assert.That(text, Does.Contain("当時の送信：失敗").And.Contain("以前の承認").And.Contain("再確認").And.Contain("終了")
                .And.Contain("後の実行で反映を確認済み"));
            Assert.That(text, Does.Not.Contain("撤回").And.Not.Contain("新たな取得・レビューが必要"),
                "A successfully prepared review ended the old approval; settled Title work no longer needs a new review.");
            Ui.Find<Expander>("ApplyOperationEvidence-" + failedId, Ui.Dialog("ApplyHistoryDialog")).IsExpanded = true;
        });
        await Ui.Until(() => Ui.DialogText("ApplyHistoryDialog").Contains("PermissionDenied"));
        await Ui.Run(() => Ui.DialogButton("ApplyHistoryDialog", "CloseButton"));
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Until(() => Ui.Popup<Expander>("ApplyOperationEvidence-" + failedId) is { IsLoaded: true });
        await Ui.Run(() => {
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Not.Contain("ユーザーが以前の承認を撤回").And.Not.Contain("新たな取得・レビューが必要"));
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
        Assert.That(Work.Journal[0].Operations.Single(operation => operation.Id == failedId).Attempts.Single().Reason, Is.EqualTo("PermissionDenied"));
        Assert.That(Work.Journal[0].Operations.Single(operation => operation.Key.Kind == "Select").Attempts, Has.Length.EqualTo(1));
        var independent = Work.Fields.Single(field => field.Key == new FieldKey("Title", "I2"));
        Assert.That((independent.Change?.Value, independent.Buffer), Is.EqualTo(("Independent draft", "Z")));
        Assert.That(h.Writes.Count, Is.EqualTo(3), "Only the remaining Title gets a new approved dispatch.");
    }

    [TestCase(false), TestCase(true), Category("ApplyOutcomeRecovery")]
    public async Task CancelledOrFailedRemainingReviewDoesNotInventAnExecutionOrReplaceHistory(bool failRead)
    {
        var failedId = await PrepareOutcomeFailure();
        await OpenOutcomeFailure(failedId);
        await Ui.Run(() => {
            h.Existing.Unreadable = failRead;
            Ui.Click(Ui.Find<Button>("ResumeApplyBatch-" + Work.Journal.Single().Id, Ui.Dialog("ApplyHistoryDialog")));
        });
        await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() => {
            Assert.That(Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled, Is.False);
            Assert.That(Work.Journal.Single().Operations.Single(operation => operation.Id == failedId).State,
                Is.EqualTo(failRead ? ApplyState.Failed : ApplyState.Superseded));
            Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Until(() => Ui.Popup<Expander>("ApplyOperationEvidence-" + failedId) is { IsLoaded: true });
        await Ui.Run(() => Ui.DialogButton("ApplyHistoryDialog", "CloseButton"));
        Assert.That(Work.Journal, Has.Count.EqualTo(1));
        Assert.That(h.Writes.Count, Is.EqualTo(2));
        Assert.That(Work.Fields.Single(field => field.Key == new FieldKey("Title", "I2")).Buffer, Is.EqualTo("Z"));
    }

    private async Task<string> PrepareOutcomeFailure()
    {
        await Ui.Run(async () => {
            var rows = Work.Open(Workspace.Selected!);
            Work.Commit("P1", rows[0].Cells[0], "Weekly review completed");
            Work.Commit("P1", rows[0].Cells[1], "done", true);
            Work.Commit("P1", rows[1].Cells[0], "Independent draft"); Work.SetBuffer(rows[1].Cells[0], "Z");
            h.Existing.MutationResult = (query, _) => query.Contains("ApplyTitle") ? ScriptedRunner.Http("{}", 403) : null;
            await h.Apply("P1-T1");
        });
        return Work.Journal.Single().Operations.Single(operation => operation.Key.Kind == "Title").Id;
    }

    private static async Task OpenOutcomeFailure(string operationId)
    {
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Until(() => Ui.Popup<Button>("ApplyOperationDetails-" + operationId) is { IsLoaded: true });
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ApplyOperationDetails-" + operationId, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.Until(() => Ui.Popup<ScrollViewer>("ApplyHistoryEvidence") is { IsLoaded: true });
    }

    private static async Task SelectOutcomeReviewRow(string rowId)
    {
        await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Ui.Find<TextBlock>("ApplyCheckStatus", Ui.Dialog("ApplyReviewDialog")).Text.Contains("確認中"));
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("ApplyTargetRows", Ui.Dialog("ApplyReviewDialog"));
            list.SelectedItems.Add(list.Items.Cast<ApplyConfirmationRow>().Single(row => row.Data.Id == rowId));
        });
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
    }

    [TestCase(false), TestCase(true), Category("ApplyProblemRefresh"), Category("ApplyProblemPresentation")]
    public async Task RefreshKeepsTheSelectedFailedOrBlockedTitleAndItsProblemVisible(bool resumeFailedAttempt)
    {
        await Ui.Run(async () =>
        {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "Retained failed title");
            h.Existing.MutationResult = (_, _) => ScriptedRunner.Http("{}", 403);
            await h.Apply("P1-T1");
            if (resumeFailedAttempt) await Workspace.ResumeApplyAsync(Work.Journal.Single().Id);
            Assert.That(Work.Journal.Single().Operations.Single().State,
                Is.EqualTo(resumeFailedAttempt ? ApplyState.Blocked : ApplyState.Failed));
        });
        await Ui.Ready<Button>("NextApplyProblem");
        await Ui.Run(() => Ui.Click("NextApplyProblem"));
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Single().SelectionIdentity?.Field == new FieldKey("Title", "I1"));
        var writesBeforeRefresh = h.Writes.Select(w => (w.Query, Input: w.Input.GetRawText())).ToArray();
        var refreshGeneration = Workspace.AcceptedRefreshGeneration;
        var expectedRows = new[] { "P1-T1", "P1-T2" };
        await Ui.Run(async () =>
        {
            Assert.That(Ui.Find<TextBlock>("ApplyProblemStatus").Text, Does.Contain(resumeFailedAttempt ? "要確認" : "失敗"));
            await ApplyInformationEvidence.Capture(panel, $"selected-problem-before-refresh-{resumeFailedAttempt}");
            AssertProblemUsesFixedStrip(Ui.Tree(panel).OfType<EditingGrid>().Single(), "GridCell0_0");
            Ui.Click("RefreshProjectButton");
        });

        await Ui.Until(() => !Workspace.IsBusy && Workspace.AcceptedRefreshGeneration > refreshGeneration);
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Any(g => g.IsLoaded));
        await Ui.Run(async () =>
        {
            var grid = Ui.Tree(panel).OfType<EditingGrid>().Single();
            Assert.That(grid.DisplayedRowIds, Is.EqualTo(expectedRows));
            Assert.That(grid.SelectionIdentity?.Item, Is.EqualTo("P1-T1"));
            Assert.That(grid.SelectionIdentity?.Field, Is.EqualTo(new FieldKey("Title", "I1")));
            var title = Ui.Find<FrameworkElement>("GridCell0_0", grid) switch
            {
                TextBox editor => editor.Text,
                Button { Content: TextBlock text } => text.Text,
                _ => throw new AssertionException("The retained title needs a rendered value.")
            };
            Assert.That(title, Is.EqualTo("Retained failed title"));
            Assert.That(Ui.Find<TextBlock>("ApplyProblemStatus", grid).Text, Does.Contain(resumeFailedAttempt ? "要確認" : "失敗"));
            Assert.That(Work.Journal.Single().Operations.Single().State,
                Is.EqualTo(resumeFailedAttempt ? ApplyState.Blocked : ApplyState.Failed));
            Assert.That(Work.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Change?.Value, Is.EqualTo("Retained failed title"));
            Assert.That(h.Writes.Select(w => (w.Query, Input: w.Input.GetRawText())), Is.EqualTo(writesBeforeRefresh),
                "Refreshing the selected problem must not dispatch another update.");
            await ApplyInformationEvidence.Capture(panel, $"selected-problem-after-refresh-{resumeFailedAttempt}");
            AssertProblemUsesFixedStrip(grid, "GridCell0_0");
        });
    }

    [Test, Category("ApplyProblemPresentation")]
    public async Task WithdrawingFailedApplyDoesNotReopenAnObsoleteProblemOnHover()
    {
        await Ui.Run(async () =>
        {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[1], "done", true);
            h.Existing.MutationResult = (_, _) => ScriptedRunner.Http("{}", 403);
            await h.Apply("P1-T1");
        });
        await Ui.Ready<Button>("NextApplyProblem");
        await Ui.Run(() => Ui.Click("NextApplyProblem"));
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Single().SelectionIdentity?.Field == new FieldKey("Select", "P1-T1", "P1", "P1-status"));
        await Ui.Run(async () =>
        {
            await ApplyInformationEvidence.Capture(panel, "problem-before-withdrawal");
            var grid = Ui.Tree(panel).OfType<EditingGrid>().Single();
            Assert.That(Ui.Find<TextBlock>("ApplyProblemStatus", grid).Text, Does.Contain("失敗"));
            AssertProblemUsesFixedStrip(grid, "GridCell0_1");
            Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(Ui.Find<Button>("GridCell0_1", grid)),
                "The problem explanation must leave focus on the selected field.");
            Assert.That(ToolTipService.GetToolTip(Ui.Find<TextBlock>("GridRowIdentity0", grid)), Is.Not.Null,
                "The ordinary Issue identity tooltip remains available.");
            Ui.Click("GridApplyHistory");
        });
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Until(() => Ui.Popup<Expander>("ApplyOperationEvidence-" + Work.Journal.Single().Operations.Single().Id) is { IsLoaded: true });
        await Ui.Run(() =>
        {
            Assert.That(ToolTipService.GetToolTip(Ui.Find<Button>("GridCell0_1")), Is.Null);
            Assert.That(Ui.Find<TextBlock>("ApplyHistoryTarget", Ui.Dialog("ApplyHistoryDialog")).Text,
                Does.Contain("#1").And.Contain("Status"), "The selected problem opens its own field history directly.");
        });
        await Ui.Until(() => Ui.Popup<ScrollViewer>("ApplyHistoryEvidence") is { IsLoaded: true });
        await Ui.Run(() => Ui.Find<Expander>("ApplyOperationEvidence-" + Work.Journal.Single().Operations.Single().Id, Ui.Dialog("ApplyHistoryDialog")).IsExpanded = true);
        await Ui.Until(() => Ui.DialogText("ApplyHistoryDialog").Contains(Work.Journal.Single().Operations.Single().Id, StringComparison.Ordinal));
        await Ui.Run(async () => {
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain(Work.Journal.Single().Operations.Single().Id),
                "The complete attempt history remains reachable from the problem strip.");
            await ApplyInformationEvidence.Capture(Ui.Dialog("ApplyHistoryDialog")!, "problem-history-without-obscuring-tooltip");
        });
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("WithdrawApplyBatch-" + Work.Journal.Single().Id, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.Until(() => Work.Journal.Single().Operations.Single().State == ApplyState.Superseded && Ui.Dialog("ApplyHistoryDialog") is null);
        SheetNativeInput.Move(await SheetNativeInput.PointFor("GridCell1_1"));
        await Task.Delay(200);
        SheetNativeInput.Move(await SheetNativeInput.PointFor("GridCell0_1"));
        await Task.Delay(1000);
        await Ui.Run(async () =>
        {
            Assert.That(ToolTipService.GetToolTip(Ui.Find<Button>("GridCell0_1")), Is.Null,
                "A retired problem must not remain associated with the native hover service.");
            Assert.That(Ui.Find<TextBlock>("GridMarker0_1").Text, Does.Not.Contain("!"));
            Assert.That(Work.Journal.Single().Operations.Single().Attempts, Is.Not.Empty);
            await ApplyInformationEvidence.Capture(panel, "problem-withdrawn-after-native-hover");
        });
        Assert.That(h.Writes.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task LateApplyOutcomeCannotNavigateAfterInvokingAnotherProject()
    {
        var first = Workspace.Selected!;
        await Ui.Run(async () =>
        {
            var choice = await new ProjectDiscovery(h.Existing.Service).ResolveAsync(h.Existing.Context,
                "https://github.com/users/sample-user/projects/2", default);
            await Workspace.RegisterAsync(choice, null); await Workspace.SelectAsync(first.Snapshot.Id);
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "approved before navigation");
        });
        await ControlExternal("ApplyTitle"); gate!.Fail = true;
        await Ui.Run(() => Ui.Click("ReviewApplyButton"));
        await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("ApplySelectAll", Ui.Dialog("ApplyReviewDialog"))));
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
        await Ui.Run(() => Ui.DialogButton("ApplyReviewDialog", "PrimaryButton"));
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await OpenNavigation(SplitViewDisplayMode.Inline); await InvokeProjectNode("Project 2");
        await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)); gate.Release.TrySetResult();
        await Ui.Until(() => !Workspace.IsBusy && Workspace.Selected?.Snapshot.Id.NodeId == "P2");
        await Task.Delay(400);
        await Ui.Run(() =>
        {
            Assert.That(Ui.Dialog("ApplyOutcomeWarning"), Is.Null);
            Assert.That(Ui.Tree(panel).OfType<EditingGrid>().Single().DisplayedRowIds, Is.All.StartsWith("P2-"));
            Assert.That(Ui.Find<TextBlock>("ProjectSummary").Text, Is.EqualTo("Project 2"));
            Assert.That(Work.Journal.Single().Project.NodeId, Is.EqualTo("P1"));
        });
    }

    [Test]
    public async Task DeletedProblemTargetExplainsAbsenceWithoutSelectingAnotherRow()
    {
        await Ui.Run(async () =>
        {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[1].Cells[0], "failed title");
            h.Existing.MutationResult = (_, _) => ScriptedRunner.Http("{}", 403);
            await h.Apply("P1-T2");
            h.Existing.ChangeResponse = (query, data) => {
                if (!query.Contains("ProjectItems")) return;
                var items = data["data"]!["node"]!["items"]!;
                items["nodes"]!.AsArray().RemoveAt(1); items["totalCount"] = 1;
            };
            Ui.Click("RefreshProjectButton");
        });
        await Ui.Until(() => !Workspace.IsBusy && Workspace.Selected!.Snapshot.Items.Count == 1);
        await Ui.Until(() => Ui.Find<Button>("NextApplyProblem", panel).IsLoaded);
        await Ui.Run(() =>
        {
            var grid = Ui.Tree(panel).OfType<EditingGrid>().Single();
            var before = grid.SelectionIdentity;
            Ui.Click("NextApplyProblem");
            Assert.That(grid.SelectionIdentity, Is.EqualTo(before));
            Assert.That(Ui.Find<TextBlock>("ApplyProblemStatus", panel).Text, Does.Contain("対象を現在のProjectで確認できません").And.Contain("反映結果"));
            Assert.That(Work.Journal.Single().Operations.Single().State, Is.EqualTo(ApplyState.Failed));
            Ui.Click("GridApplyHistory");
        });
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => { Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("#2")); Ui.DialogButton("ApplyHistoryDialog", "CloseButton"); });
        Assert.That(h.Writes.Count, Is.EqualTo(1));
    }

    [Test, Category("ApplyProblemPresentation")]
    public async Task CompletedApplyHistoryDefaultsToAttentionWithoutDeletingVerifiedRecord()
    {
        await Ui.Run(async () =>
        {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "Verified outgoing title");
            await h.Apply("P1-T1");
        });
        await Ui.OpenHistory();
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() =>
        {
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Not.Contain("反映済み・読み戻し確認済み"));
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("対応が必要な項目はありません"));
            Assert.That(Work.Journal.Single().Operations.Single().State, Is.EqualTo(ApplyState.Succeeded));
            Ui.Toggle(Ui.Find<CheckBox>("ApplyShowAllHistory", Ui.Dialog("ApplyHistoryDialog")));
        });
        await Ui.Until(() => Ui.DialogText("ApplyHistoryDialog").Contains("反映済み・読み戻し確認済み"));
        await Ui.Run(() =>
        {
            var help = Ui.Find<Button>("ApplyHistoryHelp", Ui.Dialog("ApplyHistoryDialog"));
            Assert.That(help.Focus(FocusState.Keyboard), Is.True);
            Ui.Click(help);
            Assert.That(ToolTipService.GetToolTip(help), Is.Not.Null);
        });
        await Ui.Until(() => Ui.Find<Button>("ApplyHistoryHelp", Ui.Dialog("ApplyHistoryDialog")).Flyout.IsOpen);
        await Ui.Run(() =>
        {
            Ui.Find<Button>("ApplyHistoryHelp", Ui.Dialog("ApplyHistoryDialog")).Flyout.Hide();
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
        Assert.That(h.Writes.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task SuccessfulConfirmationReturnsToTableWithoutOpeningResults()
    {
        await Ui.Run(() =>
        {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "Approved outgoing title");
            Ui.Click("ReviewApplyButton");
        });
        await Ui.DialogReady("ApplyReviewDialog");
        await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("ApplySelectAll", Ui.Dialog("ApplyReviewDialog"))));
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
        await Ui.Run(() => Ui.DialogButton("ApplyReviewDialog", "PrimaryButton"));
        await Ui.Until(() => !Workspace.IsBusy && Work.Journal.Any(b => b.Operations.All(o => o.State == ApplyState.Succeeded)));
        // Completion feedback is deferred to avoid taking native composition focus.
        await Task.Delay(400);
        await Ui.Run(() =>
        {
            Assert.That(Ui.Dialog("ApplyHistoryDialog"), Is.Null);
            Assert.That(Ui.ProjectCommand("ApplyHistoryButton").IsEnabled, Is.True);
            Assert.That(Work.DifferenceCount, Is.Zero);
        });
    }

    [TestCase(false, false), TestCase(true, false), TestCase(true, true), Category("ApplyProblemPresentation")]
    public async Task MixedResultsReturnToExactProblemAndPreservePendingInputAndView(bool hidden, bool uncertain)
    {
        string batch = "";
        await Ui.Run(async () =>
        {
            var p = Workspace.Selected!; var rows = Work.Open(p);
            Work.Commit("P1", rows[0].Cells[0], "keep approved");
            Work.SetBuffer(rows[0].Cells[0], "pending text is not sent");
            Work.Commit("P1", rows[1].Cells[0], "failed title");
            Work.Commit("P1", rows[1].Cells[1], "done", true);
            await Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1", "P1-T2" });
            var review = Workspace.ApplyReview!; batch = review.Batch.Id;
            Assert.That(await Workspace.Drafts!.CommitAsync(w => { w.ConfirmApply(review); return w; }, () => true), Is.True);
            if (hidden)
            {
                p = Workspace.Selected!;
                Assert.That(await Workspace.Drafts.CommitAsync(w => {
                    w.SaveRowView(w.PrepareRowView(p) with { Definition = new(Title: "keep") });
                    var columns = w.PrepareColumns(p);
                    w.SaveColumns(columns with { Columns = columns.Columns.Select(c => c.Id.FieldId == "P1-status" ? c with { Visible = false } : c).ToArray() });
                    return w;
                }, () => true), Is.True);
            }
            h.Existing.MutationResult = (_, input) => input.TryGetProperty("id", out var id) && id.GetString() == "I1" ? null
                : uncertain ? new GhProcessResult(ProcessCompletion.TimedOut, true, null) : ScriptedRunner.Http("{}", 403);
        });
        if (hidden)
        {
            await Ui.Until(() => Ui.Find<Button>("GridReapply", panel) is { IsLoaded: true, IsEnabled: true });
            await Ui.Run(() => Ui.Click("GridReapply"));
        }
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ResumeApplyBatch-" + batch, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.DialogReady("ApplyOutcomeWarning");
        await Ui.Run(async () =>
        {
            Assert.That(Ui.DialogText("ApplyOutcomeWarning"), Does.Contain(uncertain ? "要確認 2フィールド" : "失敗 2フィールド"));
            Assert.That(Ui.DialogText("ApplyOutcomeWarning"), Does.Contain("反映確認済み 1フィールド")
                .And.Contain("keep approved：反映を確認しました").And.Contain("タイトル").And.Contain("Status"));
            await ApplyInformationEvidence.Capture(Ui.Dialog("ApplyOutcomeWarning")!, $"apply-warning-{hidden}-{uncertain}");
            Ui.DialogButton("ApplyOutcomeWarning", "CloseButton");
        });
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Single().SelectionIdentity?.Field == new FieldKey("Title", "I2"));
        await Ui.Run(() => Ui.Click("NextApplyProblem"));
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Single().SelectionIdentity?.Field == new FieldKey("Select", "P1-T2", "P1", "P1-status"));
        await Ui.Run(async () =>
        {
            Assert.That(Ui.Find<TextBlock>("ApplyProblemStatus").Text, Does.Contain(uncertain ? "結果の確認が必要" : "失敗"));
            var grid = Ui.Tree(panel).OfType<EditingGrid>().Single();
            var selectedCell = Ui.Find<Button>("GridCell1_1", grid);
            // The selected native editor owns its marker outside the recycled row container.
            var marker = Ui.Find<TextBlock>("GridMarker1_1", VisualTreeHelper.GetParent(selectedCell));
            Assert.That(marker.Text, Is.EqualTo(uncertain ? "?" : "!"));
            Assert.That(marker.TransformToVisual(grid).TransformPoint(new(0, 0)).Y, Is.InRange(0, grid.ActualHeight));
            Assert.That(Work.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("pending text is not sent"));
            Assert.That(Work.Journal.Single().Operations.Count(o => o.State == ApplyState.Succeeded), Is.EqualTo(1));
            Assert.That(Work.Columns(Workspace.Selected!).Hidden("P1-status"), Is.EqualTo(hidden));
            Assert.That(Work.RowView(Workspace.Selected!).Title, Is.EqualTo(hidden ? "keep" : ""));
            await ApplyInformationEvidence.Capture(panel, $"apply-problem-{hidden}-{uncertain}");
            AssertProblemUsesFixedStrip(grid, "GridCell1_1");
            Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(selectedCell));
            if (hidden)
            {
                Ui.Click("GridReapply");
                Assert.That(Ui.Tree(panel).OfType<EditingGrid>().Single().DisplayedRowIds, Is.EqualTo(new[] { "P1-T1" }));
                Assert.That(Work.Columns(Workspace.Selected!).Hidden("P1-status"), Is.True);
            }
        });
        Assert.That(h.Writes.Count, Is.EqualTo(3), "Showing results and navigating must not dispatch another update.");
    }

    private static void AssertProblemUsesFixedStrip(EditingGrid grid, string cellId)
    {
        var status = Ui.Find<TextBlock>("ApplyProblemStatus", grid);
        var target = Ui.Find<FrameworkElement>(cellId, grid);
        Assert.That(status.ActualHeight, Is.GreaterThan(0));
        var origin = status.TransformToVisual(grid).TransformPoint(new(0, 0));
        Assert.That(origin.Y, Is.GreaterThanOrEqualTo(0));
        Assert.That(origin.Y + status.ActualHeight, Is.LessThanOrEqualTo(grid.ActualHeight));
        Assert.That(ToolTipService.GetToolTip(target), Is.Null,
            "Selection must not associate a duplicate problem tooltip with the cell; closing it alone allows hover to reopen it.");
        Assert.That(VisualTreeHelper.GetOpenPopupsForXamlRoot(grid.XamlRoot)
            .SelectMany(p => Ui.Tree(p.Child)).OfType<ToolTip>().Any(t => ReferenceEquals(t.PlacementTarget, target)), Is.False);
    }
}
