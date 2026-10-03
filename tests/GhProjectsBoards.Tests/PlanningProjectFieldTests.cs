using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanningProjectFieldTests
{
    private static (ProjectRegistration Project, EditingWorkspace Work, EditRow Row) Setup(bool local = false)
    {
        var project = PlanningAssignmentTests.Assigned("U1");
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        var rowId = local ? work.AddRow(project) : "P1T1";
        var taskId = work.TaskId(project, rowId);
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = [new(taskId, PlanningMode.Auto, "U1", Actuals: [new("U1", 4, new(2026, 10, 6))])] }, work.Revision,
            values: [new(rowId, "Estimate", "8"), new(rowId, "Remaining", "4")]);
        return (project, work, work.Open(project).Single(row => row.ItemId == rowId));
    }
    private static ProjectPlanning InProgress(EditingWorkspace work) => work.Planning("P1")! with {
        Tasks = work.Planning("P1")!.Tasks.Select(task => task with { Progress = PlanningProgress.InProgress,
            ActualStart = new(2026, 10, 5, 9, 0, 0), Actuals = [new("U1", 7, new(2026, 10, 7))] }).ToArray() };

    [TestCase(false), TestCase(true)]
    public async Task CombinedProjectOptionAndPlanningConfirmationUndoAsOneOperation(bool local)
    {
        var (project, work, row) = Setup(local); var plan = work.Planning("P1");
        var option = row.Cells.Single(cell => cell.Key?.Kind is "Select" or "LocalSelect"); var original = work.Value(option);
        var actual = row.Cells.Single(cell => cell.Key?.FieldId == "F-Actual");
        var remaining = row.Cells.Single(cell => cell.Key?.FieldId == "F-Remaining");
        work.SetPlanningBuffer(actual, "7"); work.SetBuffer(remaining, "3"); work.SetBuffer(row.Cells[0], "independent title");
        var history = work.Snapshot().History.Length;

        work.CommitPlanning(project, InProgress(work), work.Revision, values: [new(row.ItemId, "Remaining", "3")],
            consumeBuffers: [actual.Key!, remaining.Key!], projectFields: [new(row.ItemId, option.Key!.FieldId!, "done")]);

        Assert.That(work.Snapshot().History, Has.Length.EqualTo(history + 1));
        Assert.That(work.Value(option), Is.EqualTo("done")); Assert.That(work.Value(actual), Is.EqualTo("7")); Assert.That(work.Value(remaining), Is.EqualTo("3"));
        DraftStore.Validate(work.Snapshot());
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-combined-project-field-" + Guid.NewGuid().ToString("N")));
        await store.SaveAsync(work.Snapshot(), 0);
        work = EditingWorkspace.Restore((await store.LoadAsync(work.Scope))!);
        Assert.That(work.Value(option), Is.EqualTo("done")); Assert.That(work.Value(actual), Is.EqualTo("7")); Assert.That(work.Value(remaining), Is.EqualTo("3"));
        work.Undo("P1");
        Assert.Multiple(() => {
            Assert.That(work.Value(option), Is.EqualTo(original)); Assert.That(work.Value(actual), Is.EqualTo("4")); Assert.That(work.Value(remaining), Is.EqualTo("4"));
            Assert.That(work.Buffer(actual), Is.Null); Assert.That(work.Buffer(remaining), Is.Null); Assert.That(work.Buffer(row.Cells[0]), Is.EqualTo("independent title"));
            Assert.That(JsonSerializer.Serialize(work.Planning("P1")), Is.EqualTo(JsonSerializer.Serialize(plan)));
            Assert.That(work.Journal, Is.Empty);
        });
    }

    [TestCase("done"), TestCase("dup2"), TestCase(null)]
    public void ProjectFieldOnlyChangesDoNotChangePlanningMetadataAndUnchangedOptionsAddNoUndo(string? optionId)
    {
        var (project, work, row) = Setup(); var option = row.Cells[1];
        var start = row.Cells.Single(cell => cell.Key?.FieldId == "F-Start");
        work = EditingWorkspace.Restore(work.Snapshot() with { Fields = work.Fields.Select(field => field.Key == start.Key
            ? field with { Change = new("2026-11-15") } : field).ToArray() });
        var plan = work.Planning("P1")!;
        var otherFields = JsonSerializer.Serialize(work.Fields.Where(field => field.Key != option.Key));
        var before = JsonSerializer.Serialize(plan); var adopted = work.PlanFor(project).Tasks.Single(task => task.Id == "I1");
        var edit = new ProjectSelectEdit(row.ItemId, option.Key!.FieldId!, optionId);
        work.CommitPlanning(project, plan, work.Revision, projectFields: [edit]);
        var afterFirst = JsonSerializer.Serialize(work.Snapshot());
        work.CommitPlanning(project, plan, work.Revision, projectFields: [edit]);
        Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(afterFirst));
        Assert.That(work.Value(option), Is.EqualTo(optionId));
        Assert.That(JsonSerializer.Serialize(work.Planning("P1")), Is.EqualTo(before));
        Assert.That(JsonSerializer.Serialize(work.Fields.Where(field => field.Key != option.Key)), Is.EqualTo(otherFields),
            "A Project option must not refresh an unrelated retained date projection.");
        Assert.That(work.PlanFor(project).Tasks.Single(task => task.Id == "I1").Finish, Is.EqualTo(adopted.Finish));
        work.Undo("P1"); Assert.That(work.Value(option), Is.EqualTo("todo"));
        Assert.That(JsonSerializer.Serialize(work.Planning("P1")), Is.EqualTo(before));
    }

    [TestCase("invalid-option"), TestCase("missing-field"), TestCase("pending"), TestCase("conflict"), TestCase("stale")]
    public void InvalidExplicitProjectFieldRejectsTheEntirePlanningCandidate(string condition)
    {
        var (project, work, row) = Setup(); var option = row.Cells[1];
        if (condition == "pending") work.SetBuffer(option, "unfinished option");
        if (condition == "conflict")
        {
            work.Commit("P1", option, "done", true);
            var current = project with { Snapshot = project.Snapshot with { Items = project.Snapshot.Items.Select(item => item.Id.NodeId == row.ItemId
                ? item with { Values = item.Values.Select(value => value.FieldId.NodeId == option.Key!.FieldId ? value with { OptionId = "dup1" } : value).ToArray() }
                : item).ToArray() } };
            work.Reconcile(project, current); work.SetRegistrations([current]); project = current;
            Assert.That(work.Field(option)!.Conflict, Is.True);
        }
        var retainedOption = work.Value(option);
        var before = JsonSerializer.Serialize(work.Snapshot());
        Assert.Throws<InvalidOperationException>(() => work.CommitPlanning(project, InProgress(work), work.Revision - (condition == "stale" ? 1 : 0),
            values: [new(row.ItemId, "Remaining", "2")], projectFields: [new(row.ItemId, condition == "missing-field" ? "missing" : option.Key!.FieldId!, condition == "invalid-option" ? "missing-option" : "done")]));
        Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
        work.CommitPlanning(project, InProgress(work), work.Revision, values: [new(row.ItemId, "Remaining", "2")]);
        Assert.That(work.Value(option), Is.EqualTo(retainedOption), "An unchanged optional field does not block a planning-only update.");
    }

    [Test]
    public async Task SaveFailureKeepsCombinedCandidateTogetherUntilRetryAndOneUndoAfterReload()
    {
        var (project, work, row) = Setup(); var option = row.Cells[1];
        var directory = Path.Combine(Path.GetTempPath(), "ghpb-project-field-" + Guid.NewGuid().ToString("N"));
        var store = new DraftStore(directory); var session = new DraftSession(store, work, 0);
        Assert.That(await session.FlushAsync(), Is.True);
        using (var held = new FileStream(Path.Combine(directory, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            work.CommitPlanning(project, InProgress(work), work.Revision, values: [new(row.ItemId, "Remaining", "2")], projectFields: [new(row.ItemId, option.Key!.FieldId!, "done")]);
            TestContext.Out.WriteLine("Combined history after-stamps: " + string.Join("; ", work.Snapshot().History.Last().Changes.Select(change => $"{change.Key}: {change.After.Stamp}")));
            DraftStore.Validate(work.Snapshot());
            Assert.That(await session.FlushAsync(), Is.False);
            Assert.That(work.Value(option), Is.EqualTo("done")); Assert.That(work.Planning("P1")!.Tasks.Single().Progress, Is.EqualTo(PlanningProgress.InProgress));
            var saved = EditingWorkspace.Restore((await store.LoadAsync(work.Scope))!);
            Assert.That(saved.Value(option), Is.EqualTo("todo")); Assert.That(saved.Planning("P1")!.Tasks.Single().Progress, Is.EqualTo(PlanningProgress.Unstarted));
        }
        Assert.That(await session.FlushAsync(), Is.True);
        var restored = EditingWorkspace.Restore((await store.LoadAsync(work.Scope))!); restored.Undo("P1");
        Assert.That(restored.Value(option), Is.EqualTo("todo")); Assert.That(restored.Planning("P1")!.Tasks.Single().Progress, Is.EqualTo(PlanningProgress.Unstarted));
        Assert.That(restored.Value(row.Cells.Single(cell => cell.Key?.FieldId == "F-Remaining")), Is.EqualTo("4"));
        Assert.That(restored.Journal, Is.Empty);
    }
}
