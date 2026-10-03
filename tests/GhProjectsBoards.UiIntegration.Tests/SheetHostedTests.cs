using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable]
public sealed class SheetHostedTests
{
    [Test]
    public async Task ScrollingKeepsColumnContextAndSelectedPendingDetailsWithoutChangingWork()
    {
        var p = EditingTests.Registration();
        var work = new EditingWorkspace(p.Snapshot.Id.Scope);
        work.SetRegistrations([p]); work.Open(p);
        var session = new DraftSession(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-sheet-" + Guid.NewGuid().ToString("N"))), work, 0);
        EditingGrid grid = null!;
        await Ui.Run(() => grid = new EditingGrid(p, session, () => Task.FromResult(true)) { Width = 640 });
        await Ui.Mount(grid);
        try
        {
            await Ui.Ready<FrameworkElement>("GridCell0_0");
            await Ui.Run(() => Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>("GridCell0_0")).SetFocus());
            await Ui.Ready<TextBox>("GridCell0_0");
            double headerY = 0;
            await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Focus(FocusState.Programmatic));
            await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.StartsWith("タイトル：1行・1セル"));
            await Ui.Run(() =>
            {
                var editor = Ui.Find<TextBox>("GridCell0_0"); editor.Text = "未確定の作業";
                Ui.Find<Button>("GridDetails").Focus(FocusState.Keyboard);
                Ui.Click("GridDetails");
            });
            await Ui.Ready<TextBlock>("SelectedCellDetails");
            await Ui.Until(() => Ui.Find<TextBlock>("SelectedCellDetails").Text.Contains("未確定の作業"));
            await Ui.Until(() => ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Ui.Root.XamlRoot), Ui.Find<Button>("GridDetails")));
            await SheetNativeInput.Rendered();
            await Ui.Run(() =>
            {
                var header = Ui.Find<TextBlock>("GridHeader0");
                headerY = header.TransformToVisual(grid).TransformPoint(new(0, 0)).Y;
                var list = Ui.Find<ListView>("ProjectItems"); list.ScrollIntoView(list.Items[^1]);
            });
            // Merely scrolling an untouched row must not select it or create an
            // editor. Read the rendered cell through its public value pattern.
            await Ui.Ready<FrameworkElement>("GridCell100_0");
            ScrollViewer scroll = null!;
            string requested = "", settled = "";
            await Ui.Until(() => {
                var viewport = Ui.Tree(Ui.Find<ListView>("ProjectItems")).OfType<ScrollViewer>().First();
                var cell = Ui.Find<FrameworkElement>("GridCell100_0");
                var top = cell.TransformToVisual(viewport).TransformPoint(new(0, 0)).Y;
                return cell.ActualHeight > 0 && top >= -1 && top + cell.ActualHeight <= viewport.ViewportHeight + 1
                    && !FrameworkElementAutomationPeer.CreatePeerForElement(cell).IsOffscreen();
            });
            await Ui.Run(() =>
            {
                scroll = Ui.Tree(Ui.Find<ListView>("ProjectItems")).OfType<ScrollViewer>().First();
                var lastCell = Ui.Find<FrameworkElement>("GridCell100_0");
                var lastPeer = FrameworkElementAutomationPeer.CreatePeerForElement(lastCell);
                Assert.That(((IValueProvider)lastPeer.GetPattern(PatternInterface.Value)).Value, Is.EqualTo("Issue 101"));
                Assert.That(lastPeer.IsOffscreen(), Is.False, "The final task must actually be in the viewport.");
                var top = lastCell.TransformToVisual(scroll).TransformPoint(new(0, 0)).Y;
                Assert.That(top, Is.GreaterThanOrEqualTo(-1));
                Assert.That(top + lastCell.ActualHeight, Is.LessThanOrEqualTo(scroll.ViewportHeight + 1));
                Assert.That(grid.SelectionIdentity?.Field, Is.EqualTo(new FieldKey("Title", "I1")));
                Assert.That(scroll.ScrollableWidth, Is.GreaterThan(0));
                requested = $"Horizontal request: offset={scroll.HorizontalOffset}, max={scroll.ScrollableWidth}, viewport={scroll.ViewportWidth}, extent={scroll.ExtentWidth}, vertical={scroll.VerticalOffset}/{scroll.ScrollableHeight}, accepted={scroll.ChangeView(scroll.ScrollableWidth, null, null, true)}";
            });
            TestContext.Out.WriteLine(requested);
            try { await Ui.Until(() => Math.Abs(scroll.HorizontalOffset - scroll.ScrollableWidth) < 1); }
            finally { await Ui.Run(() => settled = $"Horizontal settled: offset={scroll.HorizontalOffset}, max={scroll.ScrollableWidth}, viewport={scroll.ViewportWidth}, extent={scroll.ExtentWidth}, vertical={scroll.VerticalOffset}/{scroll.ScrollableHeight}"); TestContext.Out.WriteLine(settled); }
            await Ui.Until(() =>
            {
                var header = Ui.Find<Grid>("SheetHeader");
                var container = (ListViewItem)Ui.Find<ListView>("ProjectItems").ContainerFromIndex(100);
                var row = (Grid)container.ContentTemplateRoot;
                return Math.Abs(header.TransformToVisual(grid).TransformPoint(new(0, 0)).X - row.TransformToVisual(grid).TransformPoint(new(0, 0)).X) < 1;
            });
            await Ui.Run(() =>
            {
                var header = Ui.Find<TextBlock>("GridHeader0");
                Assert.That(header.TransformToVisual(grid).TransformPoint(new(0, 0)).Y, Is.EqualTo(headerY).Within(1));
                Assert.That(header.ActualHeight, Is.GreaterThan(0));
                Assert.That(Ui.Find<TextBlock>("SelectedCellDetails").Text, Does.Contain("未確定の作業").And.Contain("#1  owner/repo").And.Contain("Issue 1"));
                Assert.That(session.Workspace.DifferenceCount, Is.Zero);
                Assert.That(session.Workspace.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("未確定の作業"));
            });
            await Ui.Run(() => ((Microsoft.UI.Xaml.Automation.Provider.IExpandCollapseProvider)
                Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<Expander>("SelectedCellDisclosure"))
                    .GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.ExpandCollapse)).Expand());
            await Ui.Ready<TextBlock>("SelectedCellDiagnostics");
            await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("SelectedCellDiagnostics").Text, Does.Contain("ID: I1")));
        }
        finally
        {
            await Ui.Unmount(grid);
            await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
            await Ui.Idle();
        }
    }
}
