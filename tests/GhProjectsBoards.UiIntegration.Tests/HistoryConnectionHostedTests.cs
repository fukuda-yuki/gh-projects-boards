using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [TestCase(42), TestCase(99), Category("HistoryConnection")]
    public async Task CachedHistoryExplainsConnectionAndReturnsToTheSameHistoryWithoutSending(long authenticatedViewer)
    {
        string batchId = "", operationId = "";
        ScrollViewer evidenceScroll = null!; double evidenceOffset = 0;
        await Ui.Run(async () => {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "Only after a new review");
            h.Existing.MutationResult = (_, _) => ScriptedRunner.Http("{}", 403);
            await h.Apply("P1-T1");
            batchId = Work.Journal.Single().Id;
            operationId = Work.Journal.Single().Operations.Single().Id;
            Workspace.SuspendConnection();
        });
        var originalProject = Workspace.Selected!.Snapshot.Id;
        var journal = System.Text.Json.JsonSerializer.Serialize(Work.Journal);
        var writes = h.Writes.Select(w => (w.Query, Input: w.Input.GetRawText())).ToArray();
        var connection = await MountConnectionEntry(new ConnectionViewModel((_, _) => h.Existing.Service));
        h.Existing.Boundary.ViewerId = authenticatedViewer;

        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(async () => {
            var dialog = Ui.Dialog("ApplyHistoryDialog")!;
            Assert.That(Ui.Find<Button>("ResumeApplyBatch-" + batchId, dialog).IsEnabled, Is.False);
            Assert.That(Ui.Find<TextBlock>("ApplyHistoryConnectionHint-" + batchId, dialog).Text,
                Does.Contain("接続が確認されていません"));
            await ApplyInformationEvidence.Capture(dialog, $"history-connection-needed-{authenticatedViewer}");
            Ui.Toggle(Ui.Find<CheckBox>("ApplyShowAllHistory", dialog));
        });
        await Ui.Until(() => Ui.Popup<Button>("ApplyOperationDetails-" + operationId) is { IsLoaded: true, IsEnabled: true });
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ApplyOperationDetails-" + operationId, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.Until(() => Ui.Popup<ScrollViewer>("ApplyHistoryEvidence") is { IsLoaded: true });
        await Ui.Until(() => Ui.Popup<Expander>("ApplyOperationEvidence-" + operationId) is { IsLoaded: true });
        await Ui.Run(() => Ui.Find<Expander>("ApplyOperationEvidence-" + operationId, Ui.Dialog("ApplyHistoryDialog")).IsExpanded = true);
        await Ui.Until(() => Ui.DialogText("ApplyHistoryDialog").Contains(operationId));
        await Ui.Run(() => {
            var dialog = Ui.Dialog("ApplyHistoryDialog")!;
            evidenceScroll = Ui.Find<ScrollViewer>("ApplyHistoryEvidence", dialog);
            evidenceOffset = Math.Min(24, evidenceScroll.ScrollableHeight);
            if (evidenceOffset > 0) evidenceScroll.ChangeView(null, evidenceOffset, null, true);
        });
        await Ui.Until(() => Math.Abs(evidenceScroll.VerticalOffset - evidenceOffset) < 0.5);
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ApplyHistoryConnection-" + batchId, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.Until(() => connection.Visibility == Visibility.Visible);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("HostInput", connection).Text, Is.EqualTo(originalProject.Scope.Host));
            Assert.That(Ui.Find<TextBlock>("AccountValue", connection).Text,
                Does.Contain("保存済み").And.Contain(Workspace.ProfileLogin).And.Contain("現在の認証：未確認"));
            Assert.That(Ui.Find<TextBlock>("ConnectionStatus", connection).Text,
                Does.Contain("保存済みの接続先").And.Not.Contain("入力が変わりました"));
            Ui.Click(Ui.Find<Button>("CheckConnectionButton", connection));
        });
        await Ui.Until(() => Ui.Find<Button>("CheckConnectionButton", connection).IsEnabled);
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ProjectsPageButton", connection)));
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(async () => {
            var dialog = Ui.Dialog("ApplyHistoryDialog")!;
            Assert.That(Workspace.Selected!.Snapshot.Id, Is.EqualTo(originalProject));
            Assert.That(Workspace.Profile, Is.EqualTo(originalProject.Scope));
            Assert.That(Ui.Find<ScrollViewer>("ApplyHistoryEvidence", dialog), Is.SameAs(evidenceScroll));
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain(operationId));
            Assert.That(evidenceScroll.VerticalOffset, Is.EqualTo(evidenceOffset).Within(0.5));
            Assert.That(Ui.Find<Button>("ResumeApplyBatch-" + batchId, dialog).IsEnabled, Is.EqualTo(authenticatedViewer == 42));
            Assert.That(Ui.Find<TextBlock>("ApplyHistoryConnectionHint-" + batchId, dialog).Visibility,
                Is.EqualTo(authenticatedViewer == 42 ? Visibility.Collapsed : Visibility.Visible));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(Work.Journal), Is.EqualTo(journal));
            Assert.That(h.Writes.Select(w => (w.Query, Input: w.Input.GetRawText())), Is.EqualTo(writes));
            await ApplyInformationEvidence.Capture(dialog, $"history-connection-return-{authenticatedViewer}");
            Ui.DialogButton("ApplyHistoryDialog", "SecondaryButton");
        });
        await Ui.Until(() => Ui.Popup<CheckBox>("ApplyShowAllHistory") is { IsLoaded: true });
        await Ui.Run(() => {
            Assert.That(Ui.Find<CheckBox>("ApplyShowAllHistory", Ui.Dialog("ApplyHistoryDialog")).IsChecked, Is.True);
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
    }

    [Test, Category("HistoryConnection")]
    public async Task CachedUncertainCreationExplainsTheConnectionNeededForResolutionAfterWithdrawal()
    {
        string batchId = "", creationId = "";
        await Ui.Run(async () => {
            var local = h.Add("Find my existing Issue");
            h.LoseCreate = true;
            await h.Apply(local);
            batchId = Work.Journal.Single().Id; creationId = Work.Creations.Single().Id;
            await Workspace.SupersedeApplyAsync(batchId);
            Workspace.SuspendConnection();
        });
        var writes = h.Writes.Select(w => (w.Query, Input: w.Input.GetRawText())).ToArray();
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("ApplyHistoryDialog")!;
            Assert.That(Ui.Find<Button>("ResolveCreation-" + creationId, dialog).IsEnabled, Is.False);
            Assert.That(Ui.Find<TextBlock>("ApplyHistoryConnectionHint-" + batchId, dialog).Text,
                Does.Contain("接続が確認されていません"));
            Assert.That(Ui.Find<Button>("ApplyHistoryConnection-" + batchId, dialog).IsEnabled, Is.True);
            Assert.That(h.Writes.Select(w => (w.Query, Input: w.Input.GetRawText())), Is.EqualTo(writes));
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
    }
}
