using System.Text.Json;
using GhProjectsBoards.App;
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
using Windows.Foundation;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, Category("CreationRowPresentation")]
public sealed class CreationRowPresentationHostedTests
{
    [TestCase(false, 1020, 620), TestCase(true, 740, 420), Category("ExplicitAddReveal")]
    public async Task ExplicitAddRevealsItsEmptyTitleAtTheBottomWithoutChangingFilterOrIndependentInput(bool filtered, double width, double height)
    {
        var project = EditingTests.Registration(count: 20);
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        var existing = work.Open(project)[1];
        work.Commit(project.Snapshot.Id.NodeId, existing.Cells[0], "Issue 2 independent draft"); work.SetBuffer(existing.Cells[0], "Z");
        if (filtered) work.SaveRowView(work.PrepareRowView(project) with { Definition = new(Title: "Issue") });
        var preferences = JsonSerializer.Serialize(work.Snapshot().RowPreferences);
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "creation-add-view-" + Guid.NewGuid().ToString("N")));
        var session = new DraftSession(store, work, 0);
        EditingGrid grid = null!; ScrollViewer scroll = null!;
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)) { Width = width, Height = height });
        await Ui.Mount(grid);
        try
        {
            await Ui.Run(() => {
                scroll = Ui.Tree(Ui.Find<ListView>("ProjectItems", grid)).OfType<ScrollViewer>().Single();
                Assert.That(grid.DisplayedRowIds.Length, Is.EqualTo(20));
                Assert.That(scroll.ScrollableHeight, Is.GreaterThan(0));
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            });
            await Ui.Until(() => Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1);

            await Ui.ClickCommand("GridAddRow", focus: true);
            await Ui.Until(() => work.LocalRows.Count == 1);
            var local = work.LocalRows.Single().Id;
            await Ui.Ready<TextBox>("GridCell20_0");
            await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), Ui.Find<TextBox>("GridCell20_0", grid)));
            await Ui.Run(RenderFrames);
            await Ui.Run(async () => {
                var editor = Ui.Find<TextBox>("GridCell20_0", grid);
                var bounds = Bounds(editor, scroll);
                TestContext.Out.WriteLine($"Explicit Add filtered={filtered}, grid={width}x{height}, title={bounds}, viewport={scroll.ViewportWidth}x{scroll.ViewportHeight}, offset={scroll.VerticalOffset}/{scroll.ScrollableHeight}");
                Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(-1));
                Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(scroll.ViewportHeight + 1), "The new input location must be fully visible before typing, without a test scroll.");
                Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(-1));
                Assert.That(bounds.Right, Is.LessThanOrEqualTo(scroll.ViewportWidth + 1));
                Assert.That(grid.SelectionIdentity?.Item, Is.EqualTo(local));
                Assert.That(Ui.Find<TextBlock>("GridEditorRowIdentity20", grid).Text, Is.EqualTo("新規・未送信"));
                Assert.That(work.LocalRows.Single().Title, Is.Empty);
                Assert.That(work.LocalRows.Single().Repository, Is.Empty);
                Assert.That(work.Buffer(existing.Cells[0]), Is.EqualTo("Z"));
                Assert.That(work.Value(existing.Cells[0]), Is.EqualTo("Issue 2 independent draft"));
                Assert.That(JsonSerializer.Serialize(work.Snapshot().RowPreferences), Is.EqualTo(preferences));
                Assert.That(work.Journal, Is.Empty);
                await ApplyInformationEvidence.Capture(grid, filtered ? "explicit-add-filtered-narrow" : "explicit-add-bottom20");
            });
        }
        finally { await Ui.Unmount(grid); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); await Ui.Idle(); }
    }

    [Test]
    public async Task LostCreationResponseUpdatesOwnedAndRecycledRowIdentityAndSelectedContextWithoutChangingItsJournal()
    {
        var harness = await CreationHarness.Create(2); var local = harness.Add("Retained creation");
        var project = harness.Workspace.Selected!; var session = harness.Session;
        EditingGrid grid = null!;
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)) { Width = 920, Height = 620 });
        await Ui.Mount(grid);
        try
        {
            await Ui.Ready<FrameworkElement>("GridCell2_0");
            await Ui.Run(() => FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>("GridCell2_0", grid)).SetFocus());
            await Ui.Ready<TextBox>("GridCell2_0");
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBlock>("GridEditorRowIdentity2", grid).Text, Is.EqualTo("新規・未送信"));
                Ui.Click(Ui.Find<Button>("GridDetails", grid));
            });
            harness.LoseCreate = true;
            await Ui.Run(async () => await harness.Apply(local));
            await Ui.Until(() => Ui.Find<TextBlock>("GridEditorRowIdentity2", grid).Text == "作成結果未確認");
            var journal = JsonSerializer.Serialize(session.Workspace.Journal);
            var writes = harness.Writes.Select(write => (write.Query, Input: write.Input.GetRawText())).ToArray();
            await Ui.Run(RenderFrames);
            await Ui.Run(() => ((IExpandCollapseProvider)FrameworkElementAutomationPeer.CreatePeerForElement(
                Ui.Find<Expander>("SelectedCellDisclosure", grid)).GetPattern(PatternInterface.ExpandCollapse)).Expand());
            await Ui.Ready<TextBlock>("SelectedCellDiagnostics");
            await Ui.Run(async () => {
                var context = Ui.Find<TextBlock>("SelectedCellDetails", grid).Text + Ui.Find<TextBlock>("SelectedCellDiagnostics", grid).Text;
                Assert.That(context, Does.Contain("作成結果未確認").And.Contain("既に作成されている可能性"));
                Assert.That(context, Does.Not.Contain("未作成"));
                Assert.That(AutomationProperties.GetHelpText(Ui.Find<TextBox>("GridCell2_0", grid)), Does.Contain("作成結果未確認").And.Not.Contain("GitHub未作成"));
                var c = session.Workspace.Creations.Single();
                Assert.That(c.Dispatched, Is.True); Assert.That(c.Verified, Is.Null); Assert.That(c.Completed, Is.False);
                Assert.That(session.Workspace.CreationLocked(local), Is.True);
                Assert.That(JsonSerializer.Serialize(session.Workspace.Journal), Is.EqualTo(journal));
                Assert.That(harness.Writes.Select(write => (write.Query, Input: write.Input.GetRawText())), Is.EqualTo(writes));
                await ApplyInformationEvidence.Capture(grid, "creation-row-unknown");
            });
            await Ui.Run(() => FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>("GridCell0_0", grid)).SetFocus());
            await Ui.Ready<TextBox>("GridCell0_0");
            await Ui.Run(RenderFrames);
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBlock>("GridRowIdentity2", grid).Text, Is.EqualTo("作成結果未確認"));
                Assert.That(JsonSerializer.Serialize(session.Workspace.Journal), Is.EqualTo(journal));
                Assert.That(harness.Writes.Select(write => (write.Query, Input: write.Input.GetRawText())), Is.EqualTo(writes));
            });
        }
        finally { await Ui.Unmount(grid); await Ui.Run(async () => { await harness.Workspace.StopAsync(); Assert.That(await harness.Workspace.FlushDraftsAsync(), Is.True); }); await Ui.Idle(); }
    }

    private static Rect Bounds(FrameworkElement element, FrameworkElement relativeTo) =>
        element.TransformToVisual(relativeTo).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static async Task RenderFrames()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0; EventHandler<object>? handler = null;
        handler = (_, _) => { if (++frames == 2) completed.TrySetResult(); };
        CompositionTarget.Rendering += handler;
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= handler; }
    }
}
