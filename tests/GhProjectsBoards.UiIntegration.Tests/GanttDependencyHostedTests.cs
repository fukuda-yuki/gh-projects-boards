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
public sealed class GanttDependencyHostedTests
{
    private EditingGrid grid = null!;
    private DraftSession session = null!;
    private ProjectRegistration project = null!;
    [SetUp]
    public async Task Setup()
    {
        project = PlanningPathTests.Registration(); var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = [
            new("I1", PlanningMode.Manual, "U1", PlanningContractTests.At("2026-10-05 12:07"), PlanningContractTests.At("2026-10-08 16:19")),
            new("I2", PlanningMode.Auto, "U1")] }, work.Revision, [new("P1T2", "Estimate", "8")]);
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-gantt-link-" + Guid.NewGuid().ToString("N"))), work, 0);
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
    private PlanningLink[] Links() => session.Workspace.PlanFor(project).Inputs!.Single(i => i.Task.Id == "I2").Predecessors;

    [Test]
    public async Task NativeConnectorDragPreviewsWithoutMutationThenAddsSelectsRemovesAndUndoesTheEdge()
    {
        var before = session.Workspace.Planning("P1")!.Tasks[0]; var history = session.Workspace.Snapshot().History.Length;
        await SheetNativeInput.Drag("GanttDependencyStart-P1T1", "GanttBar-P1T2", async () => await Ui.Run(() => {
            Assert.That(Links(), Is.Empty);
            Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Contain("依存プレビュー").And.Contain("#1").And.Contain("#2"));
        }));
        await Ui.Until(() => Links().Any(l => l.PredecessorId == "I1"));
        await Ui.Run(() => {
            Assert.That(session.Workspace.Planning("P1")!.Tasks[0], Is.EqualTo(before), "A connector must never move or resize the source bar.");
            Assert.That(session.Workspace.Snapshot().History.Length, Is.EqualTo(history + 1));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
        await SheetNativeInput.Click("GanttLinkSelect-I1-I2");
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Contain("依存を選択").And.Contain("#1").And.Contain("#2")));
        await Ui.Run(async () => await ApplyInformationEvidence.Capture(grid, "gantt-dependency-selected"));
        await Ui.ClickCommand("GanttRemoveDependency");
        await Ui.Until(() => Links().Length == 0);
        await Ui.ClickCommand("GanttUndo");
        await Ui.Until(() => Links().Any(l => l.PredecessorId == "I1"));
        await Ui.ClickCommand("GanttUndo");
        await Ui.Until(() => Links().Length == 0);
    }

    [Test]
    public async Task NativeResizeAfterLinkCreationRetainsTheDependency()
    {
        await SheetNativeInput.Drag("GanttDependencyStart-P1T1", "GanttBar-P1T2");
        await Ui.Until(() => Links().Any(l => l.PredecessorId == "I1"));
        var before = session.Workspace.Planning("P1")!.Tasks[0];
        var first = await SheetNativeInput.PointFor("GanttResizeFinish-P1T1"); var dayWidth = 0d;
        await Ui.Run(() => dayWidth = Ui.Find<GanttView>("GanttView").Axis.DayWidth * grid.XamlRoot.RasterizationScale);

        await SheetNativeInput.Drag(first, new Point(first.X + dayWidth, first.Y));

        await Ui.Until(() => session.Workspace.Planning("P1")!.Tasks[0].ManualFinish == before.ManualFinish!.Value.AddDays(1));
        await Ui.Run(() => {
            Assert.That(session.Workspace.Planning("P1")!.Tasks[0].ManualStart, Is.EqualTo(before.ManualStart));
            Assert.That(Links().Select(l => l.PredecessorId), Is.EqualTo(new[] { "I1" }));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }

    [TestCase("escape"), TestCase("capture"), TestCase("revision"), TestCase("outside")]
    public async Task InterruptedOrInvalidDependencyDropLeavesDatesAndLinksUnchanged(string interruption)
    {
        var before = session.Workspace.Planning("P1")!.Tasks; var history = session.Workspace.Snapshot().History.Length;
        var first = await SheetNativeInput.PointFor("GanttDependencyStart-P1T1");
        var last = await SheetNativeInput.PointFor(interruption == "outside" ? "GanttSearch" : "GanttBar-P1T2");
        await SheetNativeInput.Drag(first, last, async () => {
            if (interruption == "escape") await SheetNativeInput.Press(VirtualKey.Escape);
            else await Ui.Run(() => {
                if (interruption == "capture") Ui.Find<GanttView>("GanttView").ReleasePointerCaptures();
                else if (interruption == "revision") session.Workspace.SetBuffer(session.Workspace.Open(project)[1].Cells[0], "changed during drag");
            });
        });
        await Ui.Run(() => {
            Assert.That(Links(), Is.Empty);
            Assert.That(session.Workspace.Planning("P1")!.Tasks, Is.EqualTo(before));
            Assert.That(session.Workspace.Snapshot().History.Length, Is.EqualTo(history));
            Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Not.Contain("プレビュー"));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }
}
