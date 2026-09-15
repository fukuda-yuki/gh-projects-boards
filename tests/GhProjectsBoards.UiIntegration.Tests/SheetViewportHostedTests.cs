using GhProjectsBoards.App;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
using Windows.Foundation;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable]
public sealed class SheetViewportHostedTests
{
    [Test]
    public async Task WorkspaceViewportFitsBetweenHeaderAndFooterAndReachesLastRowWithoutMovingFocus()
    {
        var harness = await CreationHarness.Create(101);
        var measurements = new List<string>();
        RegistrationPanel panel = null!;
        await Ui.Run(() =>
        {
            panel = new RegistrationPanel { Width = 1100, Height = 700 };
            panel.Initialize(harness.Workspace);
        });
        await Ui.Mount(panel);
        try
        {
            await Ui.Ready<TextBox>("GridCell0_0");
            foreach (var size in new[] { new Size(1100, 700), new Size(860, 560) })
            {
                await Ui.Run(async () =>
                {
                    panel.Width = size.Width; panel.Height = size.Height;
                    panel.UpdateLayout(); await RenderFrames();
                    var split = Ui.Tree(panel).OfType<SplitView>().Single();
                    if (split.DisplayMode == SplitViewDisplayMode.Overlay && split.IsPaneOpen) Ui.Click("ToggleProjectNavigation");
                    Assert.That(Ui.Find<Button>("GridReapply").Focus(FocusState.Keyboard), Is.True);
                });
                await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(panel.XamlRoot), Ui.Find<Button>("GridReapply")));
                ScrollViewer scroll = null!;
                await Ui.Run(() =>
                {
                    var grid = Ui.Tree(panel).OfType<EditingGrid>().Single();
                    var list = Ui.Find<ListView>("ProjectItems", grid);
                    scroll = Ui.Tree(list).OfType<ScrollViewer>().First();
                    var header = Bounds(Ui.Find<Grid>("SheetHeader"), panel);
                    var footer = Bounds(Ui.Find<TextBlock>("GridSelection"), panel);
                    var listBounds = Bounds(list, panel);
                    var scrollBounds = Bounds(scroll, panel);
                    var peer = FrameworkElementAutomationPeer.CreatePeerForElement(list);
                    measurements.Add($"Panel={panel.ActualWidth}x{panel.ActualHeight}, scale={panel.XamlRoot.RasterizationScale}; header={header}; footer={footer}; ListView={listBounds}; ScrollViewer={scrollBounds}; viewport={scroll.ViewportWidth}x{scroll.ViewportHeight}; extent={scroll.ExtentWidth}x{scroll.ExtentHeight}; UIA ListView screen={peer.GetBoundingRectangle()}");
                    Assert.That(listBounds.Top, Is.GreaterThanOrEqualTo(header.Bottom - 1));
                    Assert.That(listBounds.Bottom, Is.LessThanOrEqualTo(footer.Top + 1), "The sheet's actual layout must fit above its visible footer.");
                    Assert.That(scrollBounds.Top, Is.GreaterThanOrEqualTo(listBounds.Top - 1));
                    Assert.That(scrollBounds.Bottom, Is.LessThanOrEqualTo(listBounds.Bottom + 1));
                    Assert.That(scroll.ViewportHeight, Is.InRange(1d, listBounds.Height + 1));
                    Assert.That(scroll.ScrollableHeight, Is.GreaterThan(0));
                    scroll.ChangeView(0, scroll.ScrollableHeight, null, true);
                });
                await Ui.Until(() => Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1);
                await Ui.Ready<TextBox>("GridCell100_0");
                await Ui.Run(async () => await RenderFrames());
                await Ui.Run(() =>
                {
                    var last = Ui.Find<TextBox>("GridCell100_0");
                    var lastBounds = Bounds(last, panel);
                    var viewport = Bounds(scroll, panel);
                    measurements.Add($"Bottom: row101={lastBounds}; viewportTop={viewport.Top}, viewportHeight={scroll.ViewportHeight}; offset={scroll.VerticalOffset}/{scroll.ScrollableHeight}; focus={FocusManager.GetFocusedElement(panel.XamlRoot)?.GetType().Name}");
                    Assert.That(last.IsLoaded && last.ActualHeight > 0, Is.True);
                    Assert.That(lastBounds.Top, Is.GreaterThanOrEqualTo(viewport.Top - 1));
                    Assert.That(lastBounds.Bottom, Is.LessThanOrEqualTo(viewport.Top + scroll.ViewportHeight + 1), "At the bottom, the last row must be fully inside the actual data viewport.");
                    Assert.That(scroll.VerticalOffset, Is.EqualTo(scroll.ScrollableHeight).Within(1));
                    Assert.That(FocusManager.GetFocusedElement(panel.XamlRoot), Is.SameAs(Ui.Find<Button>("GridReapply")));
                    Assert.That(harness.Workspace.Drafts!.Workspace.DifferenceCount, Is.Zero);
                    Assert.That(harness.Writes, Is.Empty);
                });
            }
        }
        finally
        {
            foreach (var measurement in measurements) TestContext.Out.WriteLine(measurement);
            await Ui.Unmount(panel);
            await Ui.Run(async () => Assert.That(await harness.Workspace.FlushDraftsAsync(), Is.True));
            await Ui.Idle();
        }
    }

    private static Rect Bounds(FrameworkElement element, FrameworkElement relativeTo) =>
        element.TransformToVisual(relativeTo).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static async Task RenderFrames()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        EventHandler<object>? handler = null;
        handler = (_, _) => { if (++frames == 2) completed.TrySetResult(); };
        CompositionTarget.Rendering += handler;
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= handler; }
    }
}
