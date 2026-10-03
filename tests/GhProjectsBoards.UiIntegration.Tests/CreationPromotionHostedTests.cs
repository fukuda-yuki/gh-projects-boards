using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test, Category("CreationPromotion")]
    public async Task ResumingBoundCreationWhileItsLastRowIsSelectedPromotesTheRowAndKeepsTheWorkspaceOperable()
    {
        await Ui.Unmount(panel);
        await Ui.Run(async () => { await Workspace.StopAsync(); Assert.That(await Workspace.FlushDraftsAsync(), Is.True); });
        h = await CreationHarness.Create(30);
        await Ui.Run(() => panel.Initialize(Workspace));
        await Ui.Mount(panel);
        string local = "", batch = "", attempt = "";
        await Ui.Run(async () => {
            local = h.Add("Review follow-up");
            h.LoseCreate = true;
            await h.Apply(local);
            batch = Work.Journal.Single().Id; attempt = Work.Creations.Single().Id;
        });
        await Ui.Ready<Button>("NextApplyProblem");
        await Ui.Run(() => Ui.Click("NextApplyProblem"));
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Single().SelectionIdentity?.Item == local);
        await Ui.Ready<TextBox>("GridCell30_0");
        await Ui.Run(async () => {
            Assert.That(Ui.Find<TextBox>("GridCell30_0").Focus(FocusState.Keyboard), Is.True);
            await Workspace.InspectCreationBindingAsync(batch, attempt, "https://github.com/sample-user/first/issues/1001");
            await Workspace.ConfirmCreationBindingAsync(batch, attempt, Workspace.CreationBindingPreview!, Workspace.CreationBindingRevision);
        });
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ResumeCreationBatch-" + batch, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.Until(() => !Workspace.IsBusy && Work.Creations.Single().Completed);
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Single().SelectionIdentity?.Item == Work.Creations.Single().ItemId);
        await Ui.Ready<FrameworkElement>("GridCell30_0");
        await Ui.Until(() => {
            var cell = Ui.Find<FrameworkElement>("GridCell30_0");
            var grid = Ui.Tree(panel).OfType<EditingGrid>().Single();
            var viewport = Ui.Tree(Ui.Find<ListView>("ProjectItems", grid)).OfType<ScrollViewer>().Single();
            var bounds = cell.TransformToVisual(viewport).TransformBounds(new Windows.Foundation.Rect(0, 0, cell.ActualWidth, cell.ActualHeight));
            return bounds.Top >= -1 && bounds.Bottom <= viewport.ViewportHeight + 1
                && bounds.Right > 0 && bounds.Left < viewport.ViewportWidth;
        });
        await Ui.Run(() => {
            Assert.That(Work.LocalRows, Is.Empty);
            Assert.That(Ui.Tree(panel).OfType<EditingGrid>().Single().DisplayedRowIds.Length, Is.EqualTo(31));
            var displayedTitle = Ui.Find<FrameworkElement>("GridCell30_0") switch {
                TextBox input => input.Text,
                Button { Content: TextBlock text } => text.Text,
                _ => throw new AssertionException("The promoted row must display its title.")
            };
            Assert.That(displayedTitle, Is.EqualTo("Review follow-up"));
            Assert.That(Ui.Find<Button>("RefreshProjectButton").IsEnabled, Is.True);
            Assert.That(h.Issues.Count, Is.EqualTo(1));
            Assert.That(h.Members.Count, Is.EqualTo(1));
        });
        var writes = h.Writes.Select(w => (w.Query, Input: w.Input.GetRawText())).ToArray();
        var refreshGeneration = Workspace.AcceptedRefreshGeneration;
        await Ui.Run(() => Ui.Click("RefreshProjectButton"));
        await Ui.Until(() => !Workspace.IsBusy && Workspace.AcceptedRefreshGeneration > refreshGeneration);
        await Ui.Run(() => {
            Assert.That(Ui.Tree(panel).OfType<EditingGrid>().Single().SelectionIdentity?.Item, Is.EqualTo(Work.Creations.Single().ItemId));
            Assert.That(h.Writes.Select(w => (w.Query, Input: w.Input.GetRawText())), Is.EqualTo(writes));
            Assert.That(Ui.ProjectCommand("ApplyHistoryButton").IsEnabled, Is.True);
        });
    }
}
