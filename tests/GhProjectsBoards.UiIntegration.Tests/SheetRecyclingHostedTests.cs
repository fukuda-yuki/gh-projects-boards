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
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable]
public sealed class SheetRecyclingHostedTests
{
    [Test]
    public async Task OnePhysicalWheelDetentMovesOnceOverPresentationAndProtectedInput()
    {
        var previous = Environment.GetEnvironmentVariable("GHPB_RECYCLED_PRESENTATION");
        Environment.SetEnvironmentVariable("GHPB_RECYCLED_PRESENTATION", "1");
        var (project, work) = GanttWorkload.Create();
        var session = new DraftSession(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-recycle-wheel-" + Guid.NewGuid().ToString("N"))), work, 0);
        EditingGrid grid = null!; ScrollViewer scroll = null!; double expected = 0;
        var mounted = false;
        try
        {
            await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true)));
            await Ui.Mount(grid); mounted = true; await Ui.Ready<Button>("GridCell0_0");
            await Ui.Run(() =>
            {
                var list = Ui.Find<ListView>("ProjectItems"); scroll = Ui.Tree(list).OfType<ScrollViewer>().First();
                var lines = SheetNativeInput.WheelLines();
                expected = lines == uint.MaxValue ? scroll.ViewportHeight : ((ListViewItem)list.ContainerFromIndex(0)).ActualHeight * lines;
            });
            await MoveOneDetent();
            await Ui.Run(() => scroll.ChangeView(null, 0, null, true));
            await Ui.Until(() => scroll.VerticalOffset < 1);
            await Ui.Run(() => Ui.Click("GridCell0_0"));
            await Ui.Ready<TextBox>("GridCell0_0");
            await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").SelectedText = "pending-wheel");
            await MoveOneDetent();
            await Ui.Run(() => Assert.That(work.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("pending-wheel")));
        }
        finally
        {
            if (mounted) await Ui.Unmount(grid);
            await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
            Environment.SetEnvironmentVariable("GHPB_RECYCLED_PRESENTATION", previous);
        }
        async Task MoveOneDetent()
        {
            SheetNativeInput.Move(await SheetNativeInput.PointFor("GridCell0_0", .3)); SheetNativeInput.Wheel(-120);
            await Ui.Until(() => scroll.VerticalOffset > 0); await SheetNativeInput.Rendered();
            await Ui.Run(() => Assert.That(scroll.VerticalOffset, Is.EqualTo(expected).Within(1),
                "One OS-configured wheel detent must not also run the ScrollViewer's animated default handler."));
        }
    }

    [Test]
    public async Task PendingNativeHostsSurviveDistantFirstInputHorizontalReturnAndStaleAutomation()
    {
        var previous = Environment.GetEnvironmentVariable("GHPB_RECYCLED_PRESENTATION");
        Environment.SetEnvironmentVariable("GHPB_RECYCLED_PRESENTATION", "1");
        var (project, work) = GanttWorkload.Create();
        var folder = Path.Combine(Path.GetTempPath(), "ghpb-recycle-input-" + Guid.NewGuid().ToString("N"));
        var store = new DraftStore(folder); var session = new DraftSession(store, work, 0);
        EditingGrid grid = null!; Grid surface = null!; ListView list = null!; ScrollViewer scroll = null!;
        TextBox initial = null!, last = null!; DependencyObject initialParent = null!, initialHost = null!;
        IValueProvider stale = null!;
        var mounted = false;
        try
        {
            await Ui.Run(() => { grid = new EditingGrid(project, session, () => Task.FromResult(true)); surface = new Grid { Width = 840, Height = 490 }; surface.Children.Add(grid); });
            await Ui.Mount(surface); mounted = true;
            await Ui.Ready<Button>("GridCell0_0");
            await Ui.Run(() =>
            {
                list = Ui.Find<ListView>("ProjectItems"); scroll = Ui.Tree(list).OfType<ScrollViewer>().First();
                stale = (IValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<Button>("GridCell1_0")).GetPattern(PatternInterface.Value);
                Ui.Click("GridCell0_0");
            });
            await Ui.Ready<TextBox>("GridCell0_0");
            await Ui.Run(() =>
            {
                initial = Ui.Find<TextBox>("GridCell0_0"); initial.SelectedText = "initial-pending-value"; initial.Select(7, 0);
                initialParent = VisualTreeHelper.GetParent(initial); initialHost = VisualTreeHelper.GetParent(initialParent);
                Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(initial));
                scroll.ChangeView(null, scroll.ScrollableHeight * .5, null, true);
            });
            await Ui.Until(() => scroll.VerticalOffset > scroll.ScrollableHeight * .4);
            await SheetNativeInput.Rendered();
            await Ui.Run(() =>
            {
                Assert.That(initial.IsLoaded, Is.True); Assert.That(initial.Text, Is.EqualTo("initial-pending-value"));
                Assert.That(initial.SelectionStart, Is.EqualTo(7));
                Assert.That(VisualTreeHelper.GetParent(initial), Is.SameAs(initialParent));
                Assert.That(VisualTreeHelper.GetParent(initialParent), Is.SameAs(initialHost));
                Assert.Throws<ElementNotAvailableException>(() => stale.SetValue("wrong-row"));
                var distant = Ui.Tree(list).OfType<Button>().First(b => AutomationProperties.GetAutomationId(b).StartsWith("GridCell")
                    && AutomationProperties.GetAutomationId(b).EndsWith("_0"));
                Ui.Click(distant);
            });
            await SheetNativeInput.Press((VirtualKey)0x1A);
            await SheetNativeInput.Press(VirtualKey.J);
            await Ui.Until(() => work.Fields.Any(field => field.Key.Kind == "Title" && field.Key.NodeId != "I1" && field.Buffer == "j"));
            await Ui.Run(async () =>
            {
                Assert.That(grid.SelectionIdentity?.Item, Is.Not.EqualTo("P1T1"));
                await ApplyInformationEvidence.Capture(surface, "recycled-distant-first-input");
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            });
            await Ui.Until(() => scroll.VerticalOffset >= scroll.ScrollableHeight - 1);
            await Ui.Ready<Button>("GridCell999_0");
            await ClickCell("GridCell999_0");
            await SheetNativeInput.Press(VirtualKey.K);
            await Ui.Until(() => work.Fields.Any(field => field.Key == new FieldKey("Title", "I1000") && field.Buffer == "k"));
            await Ui.Run(() =>
            {
                last = Ui.Find<TextBox>("GridCell999_0");
                Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(last));
                scroll.ChangeView(scroll.ScrollableWidth, null, null, true);
            });
            await Ui.Until(() => scroll.HorizontalOffset >= scroll.ScrollableWidth - 1);
            await Ui.Run(async () => { await ApplyInformationEvidence.Capture(surface, "recycled-last-horizontal"); scroll.ChangeView(0, 0, null, true); });
            await Ui.Until(() => scroll.VerticalOffset < 1 && scroll.HorizontalOffset < 1);
            await Ui.Run(async () => { await ApplyInformationEvidence.Capture(surface, "recycled-before-return-input"); });
            await ClickCell("GridCell0_0");
            await Ui.Run(async () =>
            {
                Assert.That(Ui.Find<TextBox>("GridCell0_0"), Is.SameAs(initial));
                Assert.That(initial.Text, Is.EqualTo("initial-pending-value"));
                Assert.That(VisualTreeHelper.GetParent(initialParent), Is.SameAs(initialHost));
                Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(initial));
                Assert.That(last.Text, Is.EqualTo("k"));
                var editors = Ui.Tree(grid).OfType<TextBox>().Where(t => AutomationProperties.GetAutomationId(t).StartsWith("GridCell")).ToArray();
                Assert.That(editors, Has.Length.EqualTo(3), "Only the three visited protected title editors remain; other buffers are data.");
                Assert.That(work.Fields.Any(field => field.Buffer == "wrong-row"), Is.False);
                Assert.That(work.Journal, Is.Empty);
                await ApplyInformationEvidence.Capture(surface, "recycled-initial-return");
                Assert.That(await session.FlushAsync(), Is.True);
            });
            var restored = EditingWorkspace.Restore((await store.LoadAsync(work.Scope))!);
            Assert.That(restored.Fields.Single(field => field.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("initial-pending-value"));
            Assert.That(restored.Fields.Single(field => field.Key == new FieldKey("Title", "I1000")).Buffer, Is.EqualTo("k"));
            Assert.That(restored.Buffer(restored.Open(project)[999].Cells.Single(c => c.Key?.FieldId == "F-Estimate")), Is.EqualTo("24未確定"));
        }
        finally
        {
            if (mounted) await Ui.Unmount(surface);
            await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
            Environment.SetEnvironmentVariable("GHPB_RECYCLED_PRESENTATION", previous);
        }
        async Task ClickCell(string id)
        {
            // Promotion changes the pointer's visual target. Observe native focus
            // and the subsequent input, rather than requiring the old presenter's
            // routed PointerReleased notification to bubble through the same tree.
            var point = await SheetNativeInput.PointFor(id, .3);
            await Ui.Run(() => Console.WriteLine($"Click {id}: screen={point}; offset={scroll.VerticalOffset}; extent={scroll.ExtentHeight}; viewport={scroll.ViewportHeight}; target={Ui.Find<FrameworkElement>(id).TransformToVisual(surface).TransformBounds(new(0, 0, Ui.Find<FrameworkElement>(id).ActualWidth, Ui.Find<FrameworkElement>(id).ActualHeight))}"));
            SheetNativeInput.Move(point); SheetNativeInput.Button(true); SheetNativeInput.Button(false);
            try
            {
                await Ui.Until(() => FocusManager.GetFocusedElement(grid.XamlRoot) is TextBox text
                    && AutomationProperties.GetAutomationId(text) == id);
            }
            catch
            {
                await Ui.Run(async () =>
                {
                    Console.WriteLine("Focused after failed click: " + (FocusManager.GetFocusedElement(grid.XamlRoot) is DependencyObject focused ? AutomationProperties.GetAutomationId(focused) : "none"));
                    foreach (var editor in Ui.Tree(grid).OfType<TextBox>().Where(e => AutomationProperties.GetAutomationId(e).StartsWith("GridCell")))
                        Console.WriteLine($"{AutomationProperties.GetAutomationId(editor)}: {editor.TransformToVisual(surface).TransformBounds(new(0, 0, editor.ActualWidth, editor.ActualHeight))}");
                    await ApplyInformationEvidence.Capture(surface, "recycled-mouse-failure");
                });
                throw;
            }
        }
    }

    [Test]
    public async Task DistantPresentationReusesContainersWithoutConstructingEditorsForStoredBuffers()
    {
        var previous = Environment.GetEnvironmentVariable("GHPB_RECYCLED_PRESENTATION");
        Environment.SetEnvironmentVariable("GHPB_RECYCLED_PRESENTATION", "1");
        var (project, work) = GanttWorkload.Create();
        var session = new DraftSession(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-recycle-" + Guid.NewGuid().ToString("N"))), work, 0);
        EditingGrid grid = null!;
        ListView list = null!;
        ScrollViewer scroll = null!;
        var seen = new HashSet<DependencyObject>();
        var seenContent = new HashSet<UIElement>();
        var distinctContainers = new HashSet<DependencyObject>();
        var distinctContent = new HashSet<UIElement>();
        var mounted = false;
        try
        {
            await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true)));
            await Ui.Mount(grid);
            mounted = true;
            await Ui.Ready<ListView>("ProjectItems");
            await Ui.Run(() =>
            {
                list = Ui.Find<ListView>("ProjectItems");
                scroll = Ui.Tree(list).OfType<ScrollViewer>().First();
                Assert.That(list.Items.Count, Is.EqualTo(1000));
                Assert.That(list.Items.Cast<object>().Any(item => item is UIElement), Is.False,
                    "Gate A mechanism: data items must let the native panel generate and recycle containers.");
                seen.UnionWith(Ui.Tree(list).OfType<ListViewItem>());
                seenContent.UnionWith(Ui.Tree(list).OfType<ListViewItem>().Select(item => item.ContentTemplateRoot));
                Assert.That(Ui.Tree(grid).OfType<TextBox>().Any(t => AutomationProperties.GetAutomationId(t) == "GridCell999_2"), Is.False,
                    "An offscreen stored buffer must not eagerly allocate a native editor.");
                scroll.ChangeView(null, scroll.ScrollableHeight * .5, null, true);
            });
            await Ui.Until(() => scroll.VerticalOffset > scroll.ScrollableHeight * .4);
            await SheetNativeInput.Rendered();
            await Ui.Run(() =>
            {
                Assert.That(Ui.Tree(list).OfType<ListViewItem>().Any(seen.Contains), Is.True,
                    "The distant viewport must reuse real native containers.");
                Assert.That(Ui.Tree(list).OfType<ListViewItem>().Any(item => seenContent.Contains(item.ContentTemplateRoot)), Is.True,
                    "Reusing only the container shell while rebuilding content would not qualify this mechanism.");
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            });
            await Ui.Until(() => scroll.VerticalOffset >= scroll.ScrollableHeight - 1);
            await Ui.Ready<FrameworkElement>("GridCell999_0");
            await Ui.Run(() =>
            {
                Assert.That(AutomationProperties.GetName(Ui.Find<FrameworkElement>("GridCell999_0")), Does.Contain("作業 1000"));
                Assert.That(work.Buffer(work.Open(project)[999].Cells.Single(c => c.Key?.FieldId == "F-Estimate")), Is.EqualTo("24未確定"));
                Assert.That(work.Journal, Is.Empty);
            });
            foreach (var fraction in new[] { .1, .7, .2, .9, .3, .8, .4, .6, 0d, 1d })
            {
                await Ui.Run(() => scroll.ChangeView(null, scroll.ScrollableHeight * fraction, null, true));
                await Ui.Until(() => Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight * fraction) < 1);
                await SheetNativeInput.Rendered();
                await Ui.Run(() =>
                {
                    var containers = Ui.Tree(list).OfType<ListViewItem>().ToArray();
                    distinctContainers.UnionWith(containers); distinctContent.UnionWith(containers.Select(item => item.ContentTemplateRoot));
                    var native = Ui.Tree(grid).OfType<TextBox>().Count(text => AutomationProperties.GetAutomationId(text).StartsWith("GridCell"));
                    Assert.That(native, Is.Zero, "Visitation and stored pending data cannot allocate protected editors.");
                    Console.WriteLine($"Reuse sample {fraction}: attachedContainers={containers.Length}; distinctContainers={distinctContainers.Count}; distinctContent={distinctContent.Count}; nativeEditors={native}");
                });
            }
        }
        finally
        {
            if (mounted) await Ui.Unmount(grid);
            await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
            Environment.SetEnvironmentVariable("GHPB_RECYCLED_PRESENTATION", previous);
        }
    }
}
