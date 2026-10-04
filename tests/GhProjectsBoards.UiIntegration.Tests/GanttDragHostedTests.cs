using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;
using Windows.Foundation;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class GanttDragHostedTests
{
    private EditingGrid grid = null!;
    private DraftSession session = null!;
    private ProjectRegistration project = null!;
    private static DateTime At(string value) => PlanningContractTests.At(value);
    [SetUp]
    public async Task Setup()
    {
        project = PlanningPathTests.Registration(); var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = [
            new("I1", PlanningMode.Manual, "U1", At("2026-10-05 12:07"), At("2026-10-08 16:19")),
            new("I2", PlanningMode.Auto, "U1")] }, work.Revision, [new("P1T2", "Estimate", "8")]);
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-gantt-drag-" + Guid.NewGuid().ToString("N"))), work, 0);
        await Ui.Run(() => { grid = new EditingGrid(project, session, () => Task.FromResult(true)); Ui.Window.AppWindow.Resize(new(1400, 1000)); });
        await Ui.Mount(grid);
        await Ui.Run(() => { var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1]; });
        await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 0);
        await SheetNativeInput.Rendered();
    }
    [TearDown]
    public async Task Teardown()
    {
        await Ui.Unmount(grid); Assert.That(await session.FlushAsync(), Is.True); await Ui.Idle();
    }
    [TestCase("GanttBar-P1T1", "2026-10-06 12:07", "2026-10-09 16:19")]
    [TestCase("GanttResizeStart-P1T1", "2026-10-06 12:07", "2026-10-08 16:19")]
    [TestCase("GanttResizeFinish-P1T1", "2026-10-05 12:07", "2026-10-09 16:19")]
    public async Task NativeDragPreviewsThenCommitsOnceAndUndoRestoresExactDates(string target, string start, string finish)
    {
        var before = session.Workspace.Planning("P1")!.Tasks[0]; var history = session.Workspace.Snapshot().History.Length;
        var first = await SheetNativeInput.PointFor(target); var last = await OneDayAfter(first);
        await SheetNativeInput.Drag(first, last, async () => await Ui.Run(() => {
            Assert.That(session.Workspace.Planning("P1")!.Tasks[0], Is.EqualTo(before));
            Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Contain("プレビュー").And.Contain(start).And.Contain(finish));
        }));
        await Ui.Until(() => session.Workspace.Planning("P1")!.Tasks[0].ManualStart == At(start)
            && session.Workspace.Planning("P1")!.Tasks[0].ManualFinish == At(finish));
        await Ui.Run(async () => await ApplyInformationEvidence.Capture(grid, "gantt-drag-" + target));
        await Ui.Run(() => { Assert.That(session.Workspace.Snapshot().History.Length, Is.EqualTo(history + 1)); Assert.That(session.Workspace.Journal, Is.Empty); });
        await Ui.ClickCommand("GanttUndo");
        await Ui.Run(() => Assert.That(session.Workspace.Planning("P1")!.Tasks[0], Is.EqualTo(before)));
    }
    [TestCase("escape"), TestCase("capture"), TestCase("revision")]
    public async Task InterruptedNativeDragDoesNotCommitDates(string interruption)
    {
        var before = session.Workspace.Planning("P1")!.Tasks[0]; var history = session.Workspace.Snapshot().History.Length;
        var first = await SheetNativeInput.PointFor("GanttBar-P1T1"); var last = await OneDayAfter(first);
        await SheetNativeInput.Drag(first, last, async () => {
            if (interruption == "escape") await SheetNativeInput.Press(VirtualKey.Escape);
            else await Ui.Run(() => {
                if (interruption == "capture") Ui.Find<GanttView>("GanttView").ReleasePointerCaptures();
                else session.Workspace.SetBuffer(session.Workspace.Open(project)[1].Cells[0], "changed during drag");
            });
        });
        await Ui.Run(() => {
            Assert.That(session.Workspace.Planning("P1")!.Tasks[0], Is.EqualTo(before));
            Assert.That(session.Workspace.Snapshot().History.Length, Is.EqualTo(history));
            Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Not.Contain("プレビュー"));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }
    private async Task<Point> OneDayAfter(Point first)
    {
        var width = 0d;
        await Ui.Run(() => width = Ui.Find<GanttView>("GanttView").Axis.DayWidth * grid.XamlRoot.RasterizationScale);
        return new(first.X + width, first.Y);
    }
}
