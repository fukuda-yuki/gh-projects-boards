using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test]
    public async Task LegacyFailedHistoryExplainsFailureAndRetainsItsOriginalDiagnostic()
    {
        const string legacy = "以前の送信結果が不確定です。明示的な再照合・新規レビューが必要です。";
        string operationId = "";
        await Ui.Run(async () =>
        {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "Unsent retry review");
            h.Existing.MutationResult = (_, _) => ScriptedRunner.Http("{}", 403);
            await h.Apply("P1-T1");
            var batch = Work.Journal.Single(); var operation = batch.Operations.Single(); operationId = operation.Id;
            Assert.That(operation.Attempts.Single().State, Is.EqualTo(ApplyState.Failed));
            Assert.That(await Workspace.Drafts!.CommitAsync(w => {
                w.RecordApply(batch.Id, operation with { State = ApplyState.Blocked, Reason = legacy }); return w;
            }, () => true), Is.True);
        });
        var before = await File.ReadAllBytesAsync(new DraftStore(h.Existing.Root).FileFor(Work.Scope));
        var writes = h.Writes.Select(w => (w.Query, Input: w.Input.GetRawText())).ToArray();

        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() =>
        {
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("前回の送信は失敗しました。")
                .And.Not.Contain("以前の送信結果が不確定です"));
            Ui.Click(Ui.Find<Button>("ApplyOperationDetails-" + operationId, Ui.Dialog("ApplyHistoryDialog")));
        });
        await Ui.Until(() => Ui.Popup<Expander>("ApplyOperationEvidence-" + operationId) is { IsLoaded: true });
        await Ui.Run(() => Ui.Find<Expander>("ApplyOperationEvidence-" + operationId, Ui.Dialog("ApplyHistoryDialog")).IsExpanded = true);
        await Ui.Until(() => Ui.DialogText("ApplyHistoryDialog").Contains(legacy));
        await Ui.Run(() =>
        {
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("PermissionDenied"));
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
        Assert.That(await File.ReadAllBytesAsync(new DraftStore(h.Existing.Root).FileFor(Work.Scope)), Is.EqualTo(before));
        Assert.That(h.Writes.Select(w => (w.Query, Input: w.Input.GetRawText())), Is.EqualTo(writes));
    }

    [Test]
    public async Task BoundCreationCompletionReturnsToEditingAndKeepsOriginalUnknownInFullHistory()
    {
        string batchId = "", attempt = "";
        await Ui.Run(async () =>
        {
            var local = h.Add("Recovered response-loss Issue");
            h.LoseCreate = true;
            await h.Apply(local);
            batchId = Work.Journal.Single().Id; attempt = Work.Creations.Single().Id;
            await Workspace.InspectCreationBindingAsync(batchId, attempt, "https://github.com/sample-user/first/issues/1001");
            Assert.That(Workspace.CreationBindingPreview, Is.Not.Null);
            await Workspace.ConfirmCreationBindingAsync(batchId, attempt, Workspace.CreationBindingPreview!, Workspace.CreationBindingRevision);
            Assert.That(Work.Creations.Single().Completed, Is.False);
        });

        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ResolveCreation-" + attempt, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.DialogReady("CreationSetupReviewDialog");
        await Ui.Run(() => Ui.DialogButton("CreationSetupReviewDialog", "PrimaryButton"));
        await Ui.Until(() => !Workspace.IsBusy && Work.Creations.Single().Completed
            && Workspace.Drafts!.DurableRevision == Work.Revision);
        await Ui.Until(() => Ui.Dialog("CreationSetupReviewDialog") is null);
        await Task.Delay(400);
        await Ui.Run(async () =>
        {
            Assert.That(Ui.Dialog("ApplyOutcomeWarning"), Is.Null, "The original lost response must not reopen an incomplete-work warning.");
            Assert.That(Ui.Find<TextBlock>("ApplyProblemStatus").Text, Is.EqualTo("対応が必要な項目はありません。"));
            Assert.That(Work.Creations.Single().Fields, Is.Empty);
            Assert.That(h.Issues.Keys, Is.EquivalentTo(new[] { "created1" }));
            Assert.That(h.Members, Is.EquivalentTo(new[] { "created1" }));
            await ApplyInformationEvidence.Capture(panel, "bound-creation-completed-editing");
        });

        var writes = h.Writes.Select(w => (w.Query, Input: w.Input.GetRawText())).ToArray();
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => {
            if (Ui.Popup<ScrollViewer>("ApplyHistoryEvidence") is not null)
            {
                Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("Recovered response-loss Issue").And.Contain("Issueの確認とProjectへの追加が完了しました。"));
                Ui.DialogButton("ApplyHistoryDialog", "SecondaryButton");
            }
        });
        await Ui.Until(() => Ui.Popup<CheckBox>("ApplyShowAllHistory") is { IsLoaded: true });
        await Ui.Run(() =>
        {
            Assert.That(Ui.Find<ListView>("ApplyResultBatches", Ui.Dialog("ApplyHistoryDialog")).Items, Is.Empty);
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("対応が必要な項目はありません。"));
            Ui.Toggle(Ui.Find<CheckBox>("ApplyShowAllHistory", Ui.Dialog("ApplyHistoryDialog")));
        });
        await Ui.Until(() => Ui.DialogText("ApplyHistoryDialog").Contains("Recovered response-loss Issue"));
        await Ui.Run(async () =>
        {
            var dialog = Ui.Dialog("ApplyHistoryDialog")!;
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("Issueの確認とProjectへの追加が完了しました。")
                .And.Contain("元の作成要求は結果不明のまま保存されています。")
                .And.Not.Contain("フィールド: 確認済み 0 / 0"));
            Assert.That(Ui.Tree(dialog).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "ResolveCreation-" + attempt), Is.False);
            await ApplyInformationEvidence.Capture(dialog, "bound-creation-full-history");
            Ui.Click(Ui.Find<Button>("CreationHistoryDetails-" + attempt, dialog));
        });
        await Ui.Until(() => Ui.Popup<Expander>("CreationEvidence-" + attempt) is { IsLoaded: true });
        await Ui.Run(() => Ui.Find<Expander>("CreationEvidence-" + attempt, Ui.Dialog("ApplyHistoryDialog")).IsExpanded = true);
        await Ui.Until(() => Ui.DialogText("ApplyHistoryDialog").Contains("元の作成成功の証明ではありません"));
        await Ui.Run(() => Ui.DialogButton("ApplyHistoryDialog", "CloseButton"));
        Assert.That(h.Writes.Select(w => (w.Query, Input: w.Input.GetRawText())), Is.EqualTo(writes));
        var saved = await new DraftStore(h.Existing.Root).LoadAsync(Work.Scope);
        var retained = saved!.Journal!.Single(b => b.Id == batchId).Creations!.Single();
        Assert.That(retained.Completed && retained.UserBound && retained.EarlierUncertain, Is.True);
        Assert.That(retained.Received, Is.Null, "Completion and history inspection must not manufacture the original response.");
    }

    [Test]
    public async Task UncertainCreationHistoryShowsDestinationStageAndContextualResolutionWithoutRetrying()
    {
        string local = "", attempt = "";
        await Ui.Run(async () =>
        {
            local = h.Add("Investigate deployment", "sample-user/first");
            h.LoseCreate = true;
            await h.Apply(local);
            attempt = Work.Creations.Single().Id;
        });
        await Ui.OpenHistory();
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("CreationHistoryDetails-" + attempt, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.Until(() => Ui.Popup<Expander>("CreationEvidence-" + attempt) is { IsLoaded: true });
        await Ui.Run(() => Ui.Find<Expander>("CreationEvidence-" + attempt, Ui.Dialog("ApplyHistoryDialog")).IsExpanded = true);
        await Ui.Until(() => Ui.DialogText("ApplyHistoryDialog").Contains(local));
        await Ui.Run(() =>
        {
            var dialog = Ui.Dialog("ApplyHistoryDialog")!;
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("Investigate deployment")
                .And.Contain("sample-user/first").And.Contain("作成結果未確認")
                .And.Contain("Issue作成：送信済み・結果未確認").And.Contain("への追加：未実行（Issueの確認待ち）").And.Contain(local));
            Ui.Click(Ui.Find<Button>("ResolveCreation-" + attempt, dialog));
        });
        await Ui.DialogReady("CreationResolutionDialog");
        await Ui.Run(() =>
        {
            Assert.That(Ui.DialogText("CreationResolutionDialog"), Does.Contain("作成済みの可能性があります"));
            Ui.DialogButton("CreationResolutionDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.ProjectCommand("ApplyHistoryButton").IsEnabled);
        await Ui.Idle();
        Assert.That(Work.Creations.Single().Verified, Is.Null);
        Assert.That(Work.Creations.Single().Dispatched, Is.True);
        Assert.That(h.Issues.Count, Is.EqualTo(1), "Inspecting and keeping the uncertain attempt must not create another Issue.");
    }
}

[TestFixture, NonParallelizable]
public sealed class ComparisonPresentationTests
{
    [TestCase("present")]
    [TestCase("empty")]
    [TestCase("unavailable")]
    public async Task ComparisonDistinguishesLocalClearKnownEmptyAndUnconfirmedRemoteWithoutChangingDraft(string remoteState)
    {
        var previous = EditingTests.Registration(count: 1);
        var work = new EditingWorkspace(previous.Snapshot.Id.Scope);
        work.SetRegistrations([previous]);
        var cell = work.Open(previous)[0].Cells[1];
        if (remoteState == "empty") work.Commit("P1", cell, "Done");
        else work.Clear("P1", [cell]);
        var current = ReconciliationTests.Remote(previous, "Issue 1", remoteState == "empty" ? null : "done");
        if (remoteState == "unavailable") current = current with { Snapshot = current.Snapshot with {
            Items = current.Snapshot.Items.Select(i => i with {
                Values = i.Values.Select(v => v with { Availability = ValueAvailability.Unavailable, OptionId = null }).ToArray()
            }).ToArray()
        } };
        work.Reconcile(previous, current); work.SetRegistrations([current]);
        var expected = work.Fields.Single(f => f.Key == cell.Key).Change;
        var session = new DraftSession(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-comparison-" + Guid.NewGuid().ToString("N"))), work, 0);
        EditingGrid grid = null!;
        await Ui.Run(() => grid = new EditingGrid(current, session, () => Task.FromResult(true)));
        await Ui.Mount(grid);
        try
        {
            TextBox editor = null!;
            await Ui.Ready<TextBox>("GridCell0_0");
            await Ui.Run(() =>
            {
                editor = Ui.Find<TextBox>("GridCell0_0");
                Assert.That(editor.Focus(FocusState.Keyboard), Is.True);
                editor.Text = "比較後も保持する未確定文字";
                editor.Select(2, 0);
            });
            await Ui.ClickCommand("GridConflicts", focus: true);
            await Ui.DialogReady("ConflictDialog");
            await Ui.Run(() =>
            {
                var dialog = Ui.Dialog("ConflictDialog")!;
                Assert.That(Ui.Find<TextBlock>("ConflictBaselineValue", dialog).Text, Is.EqualTo("Todo [ID: todo]"));
                Assert.That(Ui.Find<TextBlock>("ConflictLocalValue", dialog).Text,
                    Is.EqualTo(remoteState == "empty" ? "Done [ID: done]" : "明示的にクリア"));
                Assert.That(Ui.Find<TextBlock>("ConflictRemoteValue", dialog).Text, Is.EqualTo(remoteState switch {
                    "empty" => "（明示的な空値）", "unavailable" => "未確認（閲覧不可）", _ => "Done [ID: done]"
                }));
                var comparison = Ui.Find<ContentControl>("ConflictComparison", dialog);
                Assert.That(comparison.IsLoaded && comparison.ActualHeight > 0, Is.True);
                Assert.That(AutomationProperties.GetName(comparison), Does.Contain("B 基準:").And.Contain("L ローカル:").And.Contain("R GitHub:"));
                var canResolve = remoteState != "unavailable";
                Assert.That(dialog.IsPrimaryButtonEnabled, Is.EqualTo(canResolve));
                Assert.That(dialog.IsSecondaryButtonEnabled, Is.EqualTo(canResolve));
                Assert.That(Ui.Find<Button>("ConflictUseAlternative", dialog).IsEnabled, Is.EqualTo(canResolve));
                Assert.That(Ui.Find<ComboBox>("ConflictAlternativeOption", dialog).IsEnabled, Is.EqualTo(canResolve));
                Ui.DialogButton("ConflictDialog", "CloseButton");
            });
            await Ui.Until(() => Ui.Dialog("ConflictDialog") is null);
            await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), editor));
            await Ui.Run(() =>
            {
                Assert.That(editor.Text, Is.EqualTo("比較後も保持する未確定文字"));
                Assert.That(editor.SelectionStart, Is.EqualTo(2));
                Assert.That(editor.SelectionLength, Is.Zero);
                Assert.That(session.Workspace.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo(editor.Text));
            });
            await Ui.Idle();
            Assert.That(session.Workspace.Fields.Single(f => f.Key == cell.Key).Change, Is.EqualTo(expected));
        }
        finally
        {
            await Ui.Run(() => Ui.Dialog("ConflictDialog")?.Hide());
            await Ui.Unmount(grid);
            await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
            await Ui.Idle();
        }
    }
}
