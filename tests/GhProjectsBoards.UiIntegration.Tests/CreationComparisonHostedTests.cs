using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [TestCase(false), TestCase(true), Category("CreationComparisonRecovery")]
    public async Task LateSetupComparisonCannotReplaceNewerHistoryBrowsing(bool failRead)
    {
        await Ui.Run(async () => {
            var local = h.Add("Known setup waiting for comparison");
            Work.Commit("P1", Work.Open(Workspace.Selected!).Single(row => row.ItemId == local).Cells[1], "done", true);
            h.Existing.Boundary.ChangeCombinedResponse = data => data["data"]!["viewer"] = null;
            await h.Apply(local);
            h.Existing.Boundary.ChangeCombinedResponse = null;
        });
        var creationId = Work.Creations.Single().Id;
        await ControlExternal("ApplyObservation");
        gate!.Fail = failRead;
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ResolveCreation-" + creationId, Ui.Dialog("ApplyHistoryDialog"))));
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Ui.Until(() => Ui.Dialog("ApplyHistoryDialog") is null);
        await Ui.Run(() => Ui.Click("GridApplyHistory"));
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => Ui.DialogButton("ApplyHistoryDialog", "SecondaryButton"));
        await Ui.Until(() => Ui.Popup<CheckBox>("ApplyShowAllHistory") is { IsLoaded: true });
        var before = System.Text.Json.JsonSerializer.Serialize(Work.Snapshot());
        var writes = h.Writes.Select(write => (write.Query, Input: write.Input.GetRawText())).ToArray();
        gate.Release.TrySetResult();
        await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() => {
            Assert.That(Ui.Dialog("CreationSetupReviewDialog"), Is.Null);
            Assert.That(Ui.Popup<CheckBox>("ApplyShowAllHistory"), Is.Not.Null);
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Until(() => Ui.Popup<CheckBox>("ApplyShowAllHistory") is { IsLoaded: true });
        await Ui.Run(() => {
            Assert.That(Ui.Popup<ScrollViewer>("ApplyHistoryEvidence"), Is.Null, "The late check must not replace the user's list destination.");
            Assert.That(System.Text.Json.JsonSerializer.Serialize(Work.Snapshot()), Is.EqualTo(before));
            Assert.That(h.Writes.Select(write => (write.Query, Input: write.Input.GetRawText())), Is.EqualTo(writes));
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
    }

    [Test, Category("CreationComparisonRecovery")]
    public async Task FailedKnownIssueComparisonKeepsItsTargetAndReadOnlyRecoveryUntilSeparateApproval()
    {
        string batchId = "", creationId = "", local = "";
        await Ui.Run(async () => {
            var independent = Work.Open(Workspace.Selected!)[1].Cells[0];
            Work.Commit("P1", independent, "Independent draft");
            Work.SetBuffer(independent, "Z");
            local = h.Add("Recover the remaining Status");
            Work.Commit("P1", Work.Open(Workspace.Selected!).Single(row => row.ItemId == local).Cells[1], "done", true);
            h.LoseCreate = true;
            await h.Apply(local);
            var batch = Work.Journal.Single(); batchId = batch.Id; creationId = batch.Creations!.Single().Id;
            await Workspace.InspectCreationBindingAsync(batchId, creationId, "https://github.com/sample-user/first/issues/1001");
            await Workspace.ConfirmCreationBindingAsync(batchId, creationId, Workspace.CreationBindingPreview!, Workspace.CreationBindingRevision);
            h.Existing.Boundary.ChangeCombinedResponse = data => data["data"]!["viewer"] = null;
        });
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ResolveCreation-" + creationId, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.DialogReady("CreationSetupReviewDialog");
        await Ui.Run(() => Ui.DialogButton("CreationSetupReviewDialog", "PrimaryButton"));
        await Ui.DialogReady("ApplyOutcomeWarning");
        await Ui.Run(() => Ui.DialogButton("ApplyOutcomeWarning", "CloseButton"));
        await Ui.Until(() => !Workspace.IsBusy && Workspace.Drafts!.DurableRevision == Work.Revision);
        await Ui.Until(() => Ui.Dialog("ApplyOutcomeWarning") is null && Ui.Find<Button>("ApplyHistoryButton") is { IsLoaded: true, IsEnabled: true });
        var writes = h.Writes.Select(write => (write.Query, Input: write.Input.GetRawText())).ToArray();
        var before = System.Text.Json.JsonSerializer.Serialize(Work.Snapshot());

        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Until(() => Ui.Popup<Button>("ResolveCreation-" + creationId) is { IsLoaded: true });
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ResolveCreation-" + creationId, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.Until(() => !Workspace.IsBusy && Ui.Popup<TextBlock>("CreationComparisonFailure-" + creationId) is { IsLoaded: true });
        await Ui.Run(async () => {
            var text = Ui.DialogText("ApplyHistoryDialog");
            Assert.That(text, Does.Contain("Recover the remaining Status").And.Contain("#1001")
                .And.Contain("Status").And.Contain("未送信").And.Contain("前回確認した値：Todo")
                .And.Contain("今回の比較：未確認").And.Contain("設定内容を再確認"));
            Assert.That(Ui.Dialog("CreationSetupReviewDialog"), Is.Null);
            Assert.That(h.Writes.Select(write => (write.Query, Input: write.Input.GetRawText())), Is.EqualTo(writes));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(Work.Snapshot()), Is.EqualTo(before));
            await ApplyInformationEvidence.Capture(Ui.Dialog("ApplyHistoryDialog")!, "known-issue-comparison-failure-retained-target");
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("ApplyHistoryDialog") is null);
        await Ui.Until(() => Ui.Find<Button>("ApplyHistoryButton") is { IsEnabled: true });
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("ApplyProblemStatus").Text, Does.Contain("Status").And.Contain("再確認"));
            Assert.That(Work.Fields.Single(field => field.Key == new FieldKey("Title", "I2")).Buffer, Is.EqualTo("Z"));
            Ui.Click("GridApplyHistory");
        });
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Until(() => Ui.Popup<Button>("ResolveCreation-" + creationId) is { IsLoaded: true });
        await Ui.Run(() => {
            h.Existing.Boundary.ChangeCombinedResponse = null;
            Ui.Click(Ui.Find<Button>("ResolveCreation-" + creationId, Ui.Dialog("ApplyHistoryDialog")));
        });
        await Ui.DialogReady("CreationSetupReviewDialog");
        await Ui.Run(() => {
            Assert.That(h.Writes.Select(write => (write.Query, Input: write.Input.GetRawText())), Is.EqualTo(writes));
            Assert.That(Work.Creations.Single().Fields!.Single().Attempts, Is.Empty);
            Ui.DialogButton("CreationSetupReviewDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("CreationSetupReviewDialog") is null);
        Assert.That(System.Text.Json.JsonSerializer.Serialize(Work.Snapshot()), Is.EqualTo(before), "Checking and closing must not approve or rewrite history.");
        Assert.That(h.Writes.Select(write => (write.Query, Input: write.Input.GetRawText())), Is.EqualTo(writes));
    }
}
