using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;
// #240: Delete clears the selected cells but never タスク名, because an accidentally untitled row is
// what holds its own writes back at publish (#247).
[TestFixture]
internal sealed class PlanClearTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");
    private string root = null!;
    [SetUp] public void Setup() { root = Path.Combine(Path.GetTempPath(), "ghpb-clear-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TearDown] public void Cleanup() => Directory.Delete(root, true);
    private static PlanDocument Initial()
    {
        var rows = ImmutableArray.Create(new PlanRow("I1", "Task", "acme/repo") { Assignees = ["p1"], Predecessors = ["I2"], Estimate = 16, Actual = 4 },
            new PlanRow("I2", "Other", "acme/repo"));
        return new(Project, new(rows, []), new(rows, new() { DefaultRepository = "acme/repo", StatusDate = Today, ProjectStart = Today }));
    }
    private async Task<PlanSession> Start() => await PlanSession.CreateAsync(new(root), Initial(), Today);

    [Test]
    public async Task ClearingATaskNameKeepsItAndClearsTheOtherSelectedCells()
    {
        var session = await Start();
        var result = await session.Execute(new ClearPlanCells(["I1"], [PlanField.Title, PlanField.Assignees, PlanField.Predecessors]), Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var row = session.Document.State.Rows.Single(r => r.Identity == "I1");
        Assert.That(row.Title, Is.EqualTo("Task"), "A range Delete never blanks a task name.");
        Assert.That(row.Assignees, Is.Empty, "The other selected cells still clear.");
        Assert.That(row.Predecessors, Is.Empty);
        Assert.That(session.UndoCount, Is.EqualTo(1), "The cleared cells are still one Undo step.");
        Assert.That((await session.Undo(Today)).Succeeded, Is.True);
        var restored = session.Document.State.Rows.Single(r => r.Identity == "I1");
        Assert.That(restored.Assignees, Is.EqualTo(new[] { "p1" }));
        Assert.That(restored.Predecessors, Is.EqualTo(new[] { "I2" }));
    }

    [Test]
    public async Task ClearingARangeSpannedByTheComputedForecastColumnClearsTheEditableCells()
    {
        // #236 part B: the default columns now include the read-only 見込終了, so a range Delete spans a field
        // the PMO cannot clear. It is skipped like タスク名: the editable cells still clear as one Undo step,
        // and an explicit edit of the computed column is still refused.
        var session = await Start();
        var result = await session.Execute(new ClearPlanCells(["I1"],
            [PlanField.Title, PlanField.Assignees, PlanField.Predecessors, "ForecastEnd"]), Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var row = session.Document.State.Rows.Single(r => r.Identity == "I1");
        Assert.That(row.Title, Is.EqualTo("Task"), "A range Delete never blanks a task name.");
        Assert.That(row.Assignees, Is.Empty, "The editable cells still clear beside the computed column.");
        Assert.That(row.Predecessors, Is.Empty);
        Assert.That(session.UndoCount, Is.EqualTo(1));
        var refusal = Assert.ThrowsAsync<ArgumentException>(async () =>
            await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", "ForecastEnd", new DateOnly(2026, 10, 9))]), Today));
        Assert.That(refusal!.Message, Does.Contain("見込終了"), "An explicit edit of the computed column is still refused.");
    }

    [Test]
    public async Task ClearingOnlyTaskNameCellsChangesNothingAndAddsNoHistory()
    {
        var session = await Start();
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Title, "Renamed")]), Today);
        var before = PlanJson.Text(session.Document);
        var undo = session.UndoCount; var redo = session.RedoCount;
        var result = await session.Execute(new ClearPlanCells(["I1", "I2"], [PlanField.Title]), Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
        Assert.That((session.UndoCount, session.RedoCount), Is.EqualTo((undo, redo)), "A no-op keeps Undo and Redo unchanged.");
        Assert.That(session.Document.State.Rows.Select(r => r.Title), Is.EqualTo(new[] { "Task", "Renamed" }));
    }

    [Test]
    public async Task ClearingEveryCellOfARowStillLeavesTheTaskNamed()
    {
        // The reported defect: whole-cell clearing left a nameless task that only publish rejected.
        var session = await Start();
        var fields = ImmutableArray.Create(PlanField.Title, PlanField.Estimate, PlanField.Actual, PlanField.Assignees, PlanField.Predecessors);
        Assert.That((await session.Execute(new ClearPlanCells(["I1"], fields), Today)).Succeeded, Is.True);
        var row = session.Document.State.Rows.Single(r => r.Identity == "I1");
        Assert.That(row.Title, Is.EqualTo("Task"));
        Assert.That(row.Estimate, Is.Null); Assert.That(row.Actual, Is.Null);
        Assert.That(PlanOperations.Changes(session.Document, Today).Fields["I1"], Does.Not.Contain(PlanField.Title));
        Assert.That(PlanOperations.UntitledNewRows(session.Document), Is.Empty);
    }
}
