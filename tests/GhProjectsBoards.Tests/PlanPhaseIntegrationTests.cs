using System.Collections.Immutable;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanPhaseIntegrationTests
{
    [Test]
    public async Task PhaseReadsPublishesClearsRefreshesAndResetsWithoutCreatingFields()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-phase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "Task", "acme/repo"), "", true) { Phase = "SA" }], 2) { Phases = ["SA", "UI"] });
            var day = new DateOnly(2026, 10, 6);
            var workspace = new PlanWorkspace(new(root), () => day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open(workspace.Available[0]);
            var session = workspace.Session!;
            Assert.That(session.Document.State.Rows.Single().Phase, Is.EqualTo("SA"));
            Assert.That(session.Document.State.Settings.Columns.Single(c => c.Role == PlanField.Phase).FieldId, Is.EqualTo("F-Phase"));
            var publisher = new PlanPublisher(workspace.Service!, workspace.Context!);
            await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Phase, "ui")]), day);
            var sent = await publisher.PublishAsync(session, day);
            Assert.That(sent.Succeeded, Is.True, sent.Error);
            Assert.That(FakePlanEditor.Load(root).Issues.Single().Phase, Is.EqualTo("UI"));
            Assert.That(session.Changes(day).TaskCount, Is.Zero);
            await session.Execute(new ClearPlanCells(["I1"], [PlanField.Phase]), day);
            sent = await publisher.PublishAsync(session, day);
            Assert.That(sent.Succeeded, Is.True, sent.Error);
            Assert.That(FakePlanEditor.Load(root).Issues.Single().Phase, Is.Null);
            await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Phase, "SA")]), day);
            var refreshed = await publisher.RefreshAsync(session, day);
            Assert.That(refreshed.Succeeded, Is.True, refreshed.Error);
            Assert.That(session.Document.State.Rows.Single().Phase, Is.EqualTo("SA"));
            var reset = await publisher.ResetAsync(session, day);
            Assert.That(reset.Succeeded, Is.True, reset.Error);
            Assert.That(session.Document.State.Rows.Single().Phase, Is.Null);
            await session.Undo(day);
            Assert.That(session.Document.State.Rows.Single().Phase, Is.EqualTo("SA"));
            Assert.That(File.ReadAllText(Path.Combine(root, "calls.jsonl")), Does.Not.Contain("createProjectV2Field"));
        }
        finally { Directory.Delete(root, true); }
    }
}
