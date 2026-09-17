using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
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
            await Ui.Ready<TextBox>("GridCell100_0");
            ScrollViewer scroll = null!;
            string requested = "", settled = "";
            await Ui.Run(() =>
            {
                scroll = Ui.Tree(Ui.Find<ListView>("ProjectItems")).OfType<ScrollViewer>().First();
                Assert.That(scroll.ScrollableWidth, Is.GreaterThan(0));
                requested = $"Horizontal request: offset={scroll.HorizontalOffset}, max={scroll.ScrollableWidth}, viewport={scroll.ViewportWidth}, extent={scroll.ExtentWidth}, vertical={scroll.VerticalOffset}/{scroll.ScrollableHeight}, accepted={scroll.ChangeView(scroll.ScrollableWidth, null, null, true)}";
            });
            TestContext.Out.WriteLine(requested);
            try { await Ui.Until(() => Math.Abs(scroll.HorizontalOffset - scroll.ScrollableWidth) < 1); }
            finally { await Ui.Run(() => settled = $"Horizontal settled: offset={scroll.HorizontalOffset}, max={scroll.ScrollableWidth}, viewport={scroll.ViewportWidth}, extent={scroll.ExtentWidth}, vertical={scroll.VerticalOffset}/{scroll.ScrollableHeight}"); TestContext.Out.WriteLine(settled); }
            await Ui.Until(() =>
            {
                var header = Ui.Find<Grid>("SheetHeader"); var row = (Grid)((ListViewItem)Ui.Find<ListView>("ProjectItems").Items[100]).Content;
                return Math.Abs(header.TransformToVisual(grid).TransformPoint(new(0, 0)).X - row.TransformToVisual(grid).TransformPoint(new(0, 0)).X) < 1;
            });
            await Ui.Run(() =>
            {
                var header = Ui.Find<TextBlock>("GridHeader0");
                Assert.That(header.TransformToVisual(grid).TransformPoint(new(0, 0)).Y, Is.EqualTo(headerY).Within(1));
                Assert.That(header.ActualHeight, Is.GreaterThan(0));
                Assert.That(Ui.Find<TextBlock>("SelectedCellDetails").Text, Does.Contain("未確定の作業").And.Contain("I1"));
                Assert.That(session.Workspace.DifferenceCount, Is.Zero);
                Assert.That(session.Workspace.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("未確定の作業"));
            });
        }
        finally
        {
            await Ui.Unmount(grid);
            await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
            await Ui.Idle();
        }
    }
}
