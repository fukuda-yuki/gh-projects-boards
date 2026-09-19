using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class PlanningHostedTests
{
    private EditingGrid grid = null!;
    private DraftSession session = null!;
    private ProjectRegistration project = null!;
    private string clipboard = "16";
    [SetUp]
    public async Task Setup()
    {
        project = PlanningPathTests.Registration(); var work = new EditingWorkspace(project.Snapshot.Id.Scope);
        work.SetRegistrations([project]);
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = [new("I1", PlanningMode.Auto, "U1")] }, work.Revision);
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-planning-ui-" + Guid.NewGuid().ToString("N"))), work, 0);
        await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true), readClipboard: () => Task.FromResult(clipboard)));
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridCell0_2"); await Ui.Idle();
    }
    [TearDown]
    public async Task Teardown()
    {
        await Ui.Run(() => Ui.Dialog("PlanningDialog")?.Hide()); await Ui.Unmount(grid);
        await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); await Ui.Idle();
    }
    [Test]
    public async Task PasteRendersAutoDatesAndUndoRestoresInputAndDerivedDatesTogether()
    {
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Field?.FieldId == "F-Estimate");
        await Ui.ClickCommand("GridPaste");
        await Ui.Until(() => session.Workspace.DifferenceCount > 0);
        await Ui.Run(() => Assert.That(session.Workspace.Value(session.Workspace.Open(project)[0].Cells[2]), Is.EqualTo("16"),
            System.Text.Json.JsonSerializer.Serialize(session.Workspace.Fields.Where(f => f.Change is not null))));
        await Ui.Until(() => Ui.Find<TextBox>("GridCell0_6").Text == "2026-10-06");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("GridCell0_6").IsReadOnly, Is.True);
            Assert.That(session.Workspace.PlanFor(project).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-06 18:00")));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
        await Ui.ClickCommand("GridUndo");
        await Ui.Until(() => Ui.Find<TextBox>("GridCell0_2").Text == "" && Ui.Find<TextBox>("GridCell0_6").Text == "");
        Assert.That(session.Workspace.DifferenceCount, Is.Zero);
    }
    [Test]
    public async Task PendingInputDoesNotRecalculateAndManualSaveRetainsTheOtherEndpoint()
    {
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Focus(FocusState.Keyboard)); await Ui.ClickCommand("GridPaste");
        await Ui.Until(() => Ui.Find<TextBox>("GridCell0_6").Text == "2026-10-06");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Text = "24");
        await Ui.Run(() => Assert.That(session.Workspace.PlanFor(project).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-06 18:00"))));
        await Ui.ClickCommand("GridPlanning"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => {
            Ui.Find<TextBox>("PlanTaskStart", Ui.Dialog("PlanningDialog")).Text = "2026-10-05 12:07";
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Run(() => {
            var task = session.Workspace.Planning("P1")!.Tasks.Single();
            Assert.That(task.Mode, Is.EqualTo(PlanningMode.Manual));
            Assert.That(task.ManualStart, Is.EqualTo(PlanningContractTests.At("2026-10-05 12:07")));
            Assert.That(task.ManualFinish, Is.EqualTo(PlanningContractTests.At("2026-10-06 18:00")));
            Assert.That(session.Workspace.Buffer(session.Workspace.Open(project)[0].Cells[2]), Is.EqualTo("24"));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }
}
