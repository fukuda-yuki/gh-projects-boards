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
        ScrollViewer historyScroll = null!; double historyOffset = 0;
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
        await Ui.Until(() => Ui.Tree(Ui.Dialog("ApplyHistoryDialog")!).OfType<Expander>().Any(expander =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(expander) == "ApplyOperationDetails-" + operationId && expander.IsLoaded));
        await Ui.Run(() => Ui.Find<Expander>("ApplyOperationDetails-" + operationId, Ui.Dialog("ApplyHistoryDialog")).IsExpanded = true);
        await Ui.Until(() => Ui.DialogText("ApplyHistoryDialog").Contains(operationId));
        await Ui.Until(() => Ui.Tree(Ui.Find<ListView>("ApplyResultBatches", Ui.Dialog("ApplyHistoryDialog")))
            .OfType<ScrollViewer>().Any(scroll => scroll.ScrollableHeight > 0));
        await Ui.Run(() => {
            historyScroll = Ui.Tree(Ui.Find<ListView>("ApplyResultBatches", Ui.Dialog("ApplyHistoryDialog"))).OfType<ScrollViewer>().Single();
            historyOffset = Math.Min(24, historyScroll.ScrollableHeight);
            Assert.That(historyScroll.ChangeView(null, historyOffset, null, true), Is.True);
        });
        await Ui.Until(() => Math.Abs(historyScroll.VerticalOffset - historyOffset) < 0.5);
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ApplyHistoryConnection-" + batchId, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.Until(() => connection.Visibility == Visibility.Visible);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("HostInput", connection).Text, Is.EqualTo(originalProject.Scope.Host));
            Ui.Click(Ui.Find<Button>("CheckConnectionButton", connection));
        });
        await Ui.Until(() => Ui.Find<Button>("CheckConnectionButton", connection).IsEnabled);
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ProjectsPageButton", connection)));
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(async () => {
            var dialog = Ui.Dialog("ApplyHistoryDialog")!;
            Assert.That(Workspace.Selected!.Snapshot.Id, Is.EqualTo(originalProject));
            Assert.That(Workspace.Profile, Is.EqualTo(originalProject.Scope));
            Assert.That(Ui.Find<CheckBox>("ApplyShowAllHistory", dialog).IsChecked, Is.True);
            Assert.That(Ui.Find<Expander>("ApplyOperationDetails-" + operationId, dialog).IsExpanded, Is.True);
            Assert.That(historyScroll.VerticalOffset, Is.EqualTo(historyOffset).Within(0.5));
            Assert.That(Ui.Find<Button>("ResumeApplyBatch-" + batchId, dialog).IsEnabled, Is.EqualTo(authenticatedViewer == 42));
            Assert.That(Ui.Find<TextBlock>("ApplyHistoryConnectionHint-" + batchId, dialog).Visibility,
                Is.EqualTo(authenticatedViewer == 42 ? Visibility.Collapsed : Visibility.Visible));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(Work.Journal), Is.EqualTo(journal));
            Assert.That(h.Writes.Select(w => (w.Query, Input: w.Input.GetRawText())), Is.EqualTo(writes));
            await ApplyInformationEvidence.Capture(dialog, $"history-connection-return-{authenticatedViewer}");
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
