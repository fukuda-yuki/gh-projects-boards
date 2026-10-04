using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NUnit.Framework;
using Windows.Foundation;
using Path = System.IO.Path;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class GanttDependencyWheelHostedTests
{
    private EditingGrid grid = null!;
    private DraftSession session = null!;

    [TearDown]
    public async Task Teardown()
    {
        if (grid is not null) await Ui.Unmount(grid);
        if (session is not null) Assert.That(await session.FlushAsync(), Is.True);
        await Ui.Idle();
    }

    [TestCase("button"), TestCase("line")]
    public async Task NativeWheelOverDependencyKeepsItsSelectionAndScrollsTheTaskList(string surface)
    {
        var project = PlanningPathTests.Registration(32);
        var items = project.Snapshot.Items;
        // Separate the endpoints so the line has an exposed segment outside its button.
        project = project with { Snapshot = project.Snapshot with {
            Items = [items[0], .. items.Skip(2).Take(5), items[1], .. items.Skip(7)] } };
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = [
            new("I1", PlanningMode.Manual, "U1", PlanningContractTests.At("2026-10-05 09:00"), PlanningContractTests.At("2026-10-05 12:00")),
            new("I2", PlanningMode.Auto, "U1")] }, work.Revision, [new("P1T2", "Estimate", "8")],
            dependencies: [new("I2", [new("I1")])]);
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-gantt-wheel-" + Guid.NewGuid().ToString("N"))), work, 0);
        await Ui.Run(() => { grid = new EditingGrid(project, session, () => Task.FromResult(true)); Ui.Window.AppWindow.Resize(new(1400, 1000)); });
        await Ui.Mount(grid);
        await Ui.Run(() => { var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1]; });
        await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 0);
        await SheetNativeInput.Click("GanttLinkSelect-I1-I2");

        ScrollViewer scroll = null!;
        FrameworkElement target = null!;
        Canvas overlay = null!;
        Point linePoint = default;
        var before = 0d; var revision = work.Revision; var selection = "";
        await Ui.Run(() => {
            scroll = Ui.Tree(Ui.Find<ListView>("GanttTasks")).OfType<ScrollViewer>().First();
            Assert.That(scroll.ScrollableHeight, Is.GreaterThan(0));
            Assert.That(SheetNativeInput.WheelLines(), Is.GreaterThan(0));
            before = scroll.VerticalOffset;
            selection = Ui.Find<TextBlock>("GanttSelected").Text;
            Assert.That(selection, Does.Contain("依存を選択").And.Contain("#1").And.Contain("#2"));
            if (surface == "button") target = Ui.Find<Button>("GanttLinkSelect-I1-I2");
            else
            {
                var path = Ui.Find<Polyline>("GanttLink-I1-I2");
                overlay = (Canvas)VisualTreeHelper.GetParent(path);
                target = overlay.Children.OfType<Polyline>().Single(line => line.IsHitTestVisible
                    && line.Stroke is SolidColorBrush { Color.A: 0 });
                var segment = ((Polyline)target).Points;
                linePoint = new(segment[0].X / overlay.ActualWidth,
                    (segment[0].Y + (segment[1].Y - segment[0].Y) / 4) / overlay.ActualHeight);
            }
        });
        var point = surface == "button" ? await SheetNativeInput.PointFor(target)
            : await SheetNativeInput.PointFor(overlay, linePoint.X, linePoint.Y);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PointerEventHandler observed = (_, args) => {
            TestContext.Out.WriteLine($"Native wheel reached {surface}; source={args.OriginalSource.GetType().Name}; target={AutomationProperties.GetAutomationId(target)}");
            delivered.TrySetResult();
        };
        await Ui.Run(() => target.AddHandler(UIElement.PointerWheelChangedEvent, observed, true));
        try
        {
            SheetNativeInput.Move(point); SheetNativeInput.Wheel(-120);
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Ui.Until(() => scroll.VerticalOffset > before + 1);
            await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                Assert.That(Ui.Find<GanttView>("GanttView").SelectedRowId, Is.EqualTo("P1T1"));
                Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Is.EqualTo(selection));
                Assert.That(Ui.Find<Button>("GanttRemoveDependency").Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(work.Revision, Is.EqualTo(revision));
                Assert.That(work.Journal, Is.Empty);
            });
        }
        finally
        {
            await Ui.Run(() => {
                target.RemoveHandler(UIElement.PointerWheelChangedEvent, observed);
                TestContext.Out.WriteLine($"Dependency {surface} wheel: vertical {before} -> {scroll.VerticalOffset}, maximum={scroll.ScrollableHeight}; nativePoint={point}");
            });
        }
    }
}
