using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ConfirmedUndoTests
{
    private static (ProjectRegistration Project, EditingWorkspace Work, EditRow Row) Planned()
    {
        var project = PlanningAssignmentTests.Assigned("U1");
        var work = new EditingWorkspace(project.Snapshot.Id.Scope);
        work.SetRegistrations([project]);
        work.SetPlanning(PlanningPathTests.Plan() with { Version = 3, People = [new("U1", "Alice", 100)] }, 0);
        return (project, work, work.Open(project)[0]);
    }

    private static async Task<EditingWorkspace> Reload(EditingWorkspace work)
    {
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-confirmed-undo-" + Guid.NewGuid()));
        await store.SaveAsync(work.Snapshot(), 0);
        return EditingWorkspace.Restore((await store.LoadAsync(work.Scope))!);
    }

    [Test]
    public async Task ConfirmedEstimateCanBeUndoneTwiceWithoutReopeningEitherInput()
    {
        var (project, work, row) = Planned();
        var estimate = row.Cells.Single(c => c.Key?.FieldId == "F-Estimate");
        var remaining = row.Cells.Single(c => c.Key?.FieldId == "F-Remaining");
        work.SetBuffer(remaining, "10");
        work.SetBuffer(estimate, "8"); work.Commit("P1", estimate, "8");
        var adopted = work.PlanFor(project).Tasks[0].Finish;
        work.SetBuffer(estimate, "10"); work.Commit("P1", estimate, "10");

        work = await Reload(work);
        work.Undo("P1");
        Assert.Multiple(() => {
            Assert.That(work.Value(estimate), Is.EqualTo("8"));
            Assert.That(work.Buffer(estimate), Is.Null);
            Assert.That(work.Buffer(remaining), Is.EqualTo("10"));
            Assert.That(work.PlanFor(project).Tasks[0].Finish, Is.EqualTo(adopted));
        });
        work.Undo("P1");
        Assert.That(work.Value(estimate), Is.Null);
        Assert.That(work.Buffer(estimate), Is.Null);
        Assert.That(work.PlanFor(project).Tasks[0].Mode, Is.EqualTo(PlanningMode.Unplanned));
        Assert.That(work.Buffer(remaining), Is.EqualTo("10"));
        Assert.That(work.Journal, Is.Empty);
    }

    [Test]
    public async Task CrossedLocalConfirmationsUndoInSequenceWithoutRecreatingConsumedBuffers()
    {
        var project = EditingTests.Registration(count: 1);
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        var id = work.AddRow(project);
        var row = work.Open(project).Single(r => r.ItemId == id);
        var title = row.Cells[0]; var repository = row.Cells[^1];
        work.SetBuffer(repository, "owner/next");
        work.SetBuffer(title, "new title"); work.Commit("P1", title, "new title");
        work.Commit("P1", repository, "owner/next");

        work = await Reload(work);
        work.Undo("P1");
        var afterFirst = work.LocalRows.Single();
        work.Undo("P1");
        Assert.Multiple(() => {
            Assert.That(afterFirst.Title, Is.EqualTo("new title"));
            Assert.That(afterFirst.Repository, Is.EqualTo(""));
            Assert.That(afterFirst.RepositoryBuffer, Is.Null);
            Assert.That(work.LocalRows.Single().Id, Is.EqualTo(id));
            Assert.That(work.Value(title), Is.EqualTo(""));
            Assert.That(work.Buffer(title), Is.Null);
            Assert.That(work.Buffer(repository), Is.Null);
        });
    }

    [Test]
    public async Task EstimateProjectionAndLaterDateConfirmationUndoInSequenceWithoutReopeningDateInput()
    {
        var (project, work, row) = Planned();
        var estimate = row.Cells.Single(c => c.Key?.FieldId == "F-Estimate");
        var finish = row.Cells.Single(c => c.Key?.FieldId == "F-Finish");
        const string pendingFinish = "2026-10-06 18:00";
        work.SetPlanningBuffer(finish, pendingFinish);
        work.SetBuffer(estimate, "8"); work.Commit("P1", estimate, "8");
        var autoFinish = work.PlanFor(project).Tasks[0].Finish;
        work.CommitDateInput(project, row.ItemId, "Finish", pendingFinish, work.Revision);

        work = await Reload(work);
        work.Undo("P1");
        var afterFirst = (work.PlanFor(project).Tasks[0].Mode, work.PlanFor(project).Tasks[0].Finish, work.Buffer(finish));
        work.Undo("P1");
        Assert.Multiple(() => {
            Assert.That(afterFirst.Mode, Is.EqualTo(PlanningMode.Auto));
            Assert.That(afterFirst.Finish, Is.EqualTo(autoFinish));
            Assert.That(afterFirst.Item3, Is.Null);
            Assert.That(work.PlanFor(project).Tasks[0].Mode, Is.EqualTo(PlanningMode.Unplanned));
            Assert.That(work.Buffer(estimate), Is.Null);
            Assert.That(work.Buffer(finish), Is.Null);
            Assert.That(work.Value(estimate), Is.Null);
            Assert.That(work.Value(finish), Is.Null);
        });
    }

    [TestCase("Title"), TestCase("Estimate"), TestCase("Remaining"), TestCase("LocalTitle"), TestCase("LocalRepository")]
    public void SameValueConfirmationSavesInputCompletionWithoutAnEmptyUndo(string kind)
    {
        var (project, work, row) = Planned();
        if (kind.StartsWith("Local", StringComparison.Ordinal))
        {
            var id = work.AddRow(project); row = work.Open(project).Single(r => r.ItemId == id);
        }
        var cell = row.Cells.Single(c => c.Key?.Kind == kind || c.Key?.FieldId == "F-" + kind);
        var value = kind == "LocalRepository" ? "owner/repo" : "8";
        work.Commit("P1", cell, value);
        var independent = kind == "Title" ? row.Cells.Single(c => c.Key?.FieldId == "F-Remaining")
            : kind == "LocalTitle" ? row.Cells[^1] : row.Cells[0];
        var input = kind is "Estimate" or "Remaining" ? "8.000" : value;
        work.SetBuffer(independent, input);
        var before = work.Snapshot();
        work.SetBuffer(cell, input); work.Commit("P1", cell, input);
        Assert.That(work.Buffer(cell), Is.Null);
        Assert.That(work.Buffer(independent), Is.EqualTo(input));
        Assert.That(work.Revision, Is.GreaterThan(before.Revision));
        Assert.That(JsonSerializer.Serialize(work.Snapshot().History), Is.EqualTo(JsonSerializer.Serialize(before.History)));
        Assert.That(work.Fields.Select(f => (f.Key, f.Stamp)), Is.EqualTo(before.Fields.Select(f => (f.Key, f.Stamp))));
        Assert.That(work.LocalRows.Select(r => (r.Id, r.Stamp)), Is.EqualTo(before.LocalRows!.Select(r => (r.Id, r.Stamp))));
        Assert.DoesNotThrow(() => work.Undo("P1"));
        Assert.That(work.Value(cell), Is.Not.EqualTo(value));
        Assert.That(work.Buffer(independent), Is.EqualTo(input));
    }

    [TestCase("Actual"), TestCase("Start"), TestCase("Finish")]
    public async Task IdenticalContextualConfirmationSavesInputCompletionAndKeepsEarlierUndoAfterReload(string role)
    {
        var (project, work, row) = Planned();
        var cell = row.Cells.Single(c => c.Key?.FieldId == "F-" + role);
        var independent = row.Cells.Single(c => c.Key?.FieldId == "F-Remaining");
        var text = role == "Actual" ? "5.000" : role == "Start" ? "2026-10-05 09:15" : "2026-10-06 17:45";
        work.SetBuffer(independent, "unfinished remaining");
        var previous = work.Snapshot();
        void Confirm()
        {
            if (role == "Actual") work.CommitActualInput(project, row.ItemId, text, new(2026, 10, 6), "U1", work.Revision);
            else work.CommitDateInput(project, row.ItemId, role, text, work.Revision);
        }
        work.SetPlanningBuffer(cell, text); Confirm();
        var other = work.Open(project).Single(r => r.ItemId != row.ItemId);
        var beforeOther = work.Snapshot();
        work.CommitActualInput(project, other.ItemId, "2", new(2026, 10, 6), "U1", work.Revision);
        var before = work.Snapshot();

        work.SetPlanningBuffer(cell, text); Confirm();
        work = await Reload(work);

        Assert.Multiple(() => {
            Assert.That(work.Buffer(cell), Is.Null);
            Assert.That(work.Buffer(independent), Is.EqualTo("unfinished remaining"));
            Assert.That(work.Revision, Is.GreaterThan(before.Revision));
            Assert.That(JsonSerializer.Serialize(work.Snapshot().History), Is.EqualTo(JsonSerializer.Serialize(before.History)));
            Assert.That(JsonSerializer.Serialize(work.Planning("P1")), Is.EqualTo(JsonSerializer.Serialize(before.Planning!.Single())));
            Assert.That(work.Fields.Select(f => (f.Key, f.Stamp)), Is.EqualTo(before.Fields.Select(f => (f.Key, f.Stamp))));
        });
        work.Undo("P1");
        Assert.That(JsonSerializer.Serialize(work.Planning("P1")), Is.EqualTo(JsonSerializer.Serialize(beforeOther.Planning!.Single())));
        Assert.That(work.Value(other.Cells.Single(c => c.Key?.FieldId == "F-Actual")), Is.Null);
        work.Undo("P1");
        Assert.That(JsonSerializer.Serialize(work.Planning("P1")), Is.EqualTo(JsonSerializer.Serialize(previous.Planning!.Single())));
        Assert.That(work.Value(cell), Is.Null);
        Assert.That(work.Buffer(cell), Is.Null);
        Assert.That(work.Buffer(independent), Is.EqualTo("unfinished remaining"));
    }

    [Test]
    public void ConfirmingAnObservedEstimateStillAdoptsItsFirstPlan()
    {
        var project = PlanningAssignmentTests.Assigned("U1");
        project = project with { Snapshot = project.Snapshot with { Items = project.Snapshot.Items.Select(i => i with {
            Values = i.Values.Select(v => v.FieldId?.NodeId == "F-Estimate" ? v with { Scalar = "8", Availability = ValueAvailability.Present } : v).ToArray() }).ToArray() } };
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        work.SetPlanning(PlanningPathTests.Plan() with { Version = 3 }, 0);
        var estimate = work.Open(project)[0].Cells.Single(c => c.Key?.FieldId == "F-Estimate");
        work.SetBuffer(estimate, "8"); work.Commit("P1", estimate, "8");
        Assert.That(work.PlanFor(project).Tasks[0].Mode, Is.EqualTo(PlanningMode.Auto));
        work.Undo("P1");
        Assert.That(work.PlanFor(project).Tasks[0].Mode, Is.EqualTo(PlanningMode.Unplanned));
        Assert.That(work.Value(estimate), Is.EqualTo("8")); Assert.That(work.Buffer(estimate), Is.Null);
    }

    [TestCase("matching"), TestCase("absent"), TestCase("different")]
    public void ProjectionUndoPreservesUnwrittenInputButRejectsDifferentLaterInput(string condition)
    {
        var (project, work, row) = Planned();
        var estimate = row.Cells.Single(c => c.Key?.FieldId == "F-Estimate");
        var finish = row.Cells.Single(c => c.Key?.FieldId == "F-Finish");
        const string initial = "2026-10-06 18:00";
        work.SetPlanningBuffer(finish, initial); work.Commit("P1", estimate, "8");
        var pending = condition == "absent" ? null : condition == "different" ? "2026-10-07 18:00" : initial;
        work.SetPlanningBuffer(finish, pending); var before = JsonSerializer.Serialize(work.Snapshot());
        if (condition == "different")
        {
            Assert.That(Assert.Throws<UndoRejectedException>(() => work.Undo("P1"))!.Fields, Is.EqualTo(new[] { finish.Key }));
            Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
        }
        else
        {
            work.Undo("P1"); Assert.That(work.Buffer(finish), Is.EqualTo(pending));
            Assert.That(work.Value(finish), Is.Null);
            Assert.That(work.PlanFor(project).Tasks[0].Mode, Is.EqualTo(PlanningMode.Unplanned));
        }
    }

    [TestCase(false), TestCase(true)]
    public async Task LocalUndoKeepsUnwrittenLaterInputAndRejectsInputOnItsWrittenField(bool affected)
    {
        var project = EditingTests.Registration(count: 1);
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        var id = work.AddRow(project); var row = work.Open(project).Single(r => r.ItemId == id);
        work.Commit("P1", row.Cells[0], "confirmed");
        var pendingCell = affected ? row.Cells[0] : row.Cells[^1]; work.SetBuffer(pendingCell, "later");
        work = await Reload(work); var before = JsonSerializer.Serialize(work.Snapshot());
        if (affected)
        {
            Assert.Throws<InvalidOperationException>(() => work.Undo("P1"));
            Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
        }
        else
        {
            work.Undo("P1"); Assert.That(work.Value(row.Cells[0]), Is.Empty);
            Assert.That(work.Buffer(pendingCell), Is.EqualTo("later"));
        }
    }

    [Test]
    public void RejectedConfirmationAndLaterScalarInputPreserveAllState()
    {
        var (_, work, row) = Planned(); var cell = row.Cells.Single(c => c.Key?.FieldId == "F-Estimate");
        work.SetBuffer(cell, "invalid"); var before = JsonSerializer.Serialize(work.Snapshot());
        Assert.Throws<InvalidOperationException>(() => work.Commit("P1", cell, "invalid"));
        Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
        work.SetBuffer(cell, "8"); work.Commit("P1", cell, "8"); work.SetBuffer(cell, "later");
        before = JsonSerializer.Serialize(work.Snapshot());
        Assert.That(Assert.Throws<UndoRejectedException>(() => work.Undo("P1"))!.Fields, Is.EqualTo(new[] { cell.Key }));
        Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
    }

    [TestCase("Actual"), TestCase("Worker"), TestCase("Start"), TestCase("Finish")]
    public void ContextualMetadataOnlyCommitStillGuardsLaterInput(string change)
    {
        var (project, work, row) = Planned();
        var role = change == "Worker" ? "Actual" : change;
        var cell = row.Cells.Single(c => c.Key?.FieldId == "F-" + role);
        if (role == "Actual") work.CommitActualInput(project, row.ItemId, "5", new(2026, 10, 6), "U1", work.Revision);
        else work.CommitDateInput(project, row.ItemId, role, "2026-10-05 09:00", work.Revision);
        var projected = work.Value(cell);
        var previous = work.Snapshot();
        if (change == "Worker") work.CommitActualReports(project, row.ItemId, [new("U2", 5, new(2026, 10, 6))], work.Revision);
        else if (role == "Actual") work.CommitActualInput(project, row.ItemId, "5", new(2026, 10, 7), "U1", work.Revision);
        else work.CommitDateInput(project, row.ItemId, role, "2026-10-05 10:00", work.Revision);
        Assert.That(work.Value(cell), Is.EqualTo(projected), "Attribution/date precision is not the projected scalar.");
        Assert.That(work.Snapshot().History, Has.Length.EqualTo(previous.History.Length + 1));
        work.SetPlanningBuffer(cell, role == "Actual" ? "7" : "2026-10-05 11:00");
        var before = JsonSerializer.Serialize(work.Snapshot());
        Assert.That(Assert.Throws<UndoRejectedException>(() => work.Undo("P1"))!.Fields, Is.EqualTo(new[] { cell.Key }));
        Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
        work.SetPlanningBuffer(cell, null); work.Undo("P1");
        Assert.That(JsonSerializer.Serialize(work.Planning("P1")), Is.EqualTo(JsonSerializer.Serialize(previous.Planning!.Single())));
    }

    [TestCase(false), TestCase(true)]
    public void BatchUndoRestoresIndependentlyOverwrittenInput(bool clear)
    {
        var project = EditingTests.Registration(count: 2); var work = new EditingWorkspace(project.Snapshot.Id.Scope);
        var rows = work.Open(project); var column = clear ? 1 : 0;
        work.SetBuffer(rows[0].Cells[column], "independent");
        if (clear) work.Clear("P1", rows.Select(r => r.Cells[column]));
        else work.Paste("P1", rows, 0, column, "first\nsecond");
        Assert.That(work.Buffer(rows[0].Cells[column]), Is.Null);
        work.Undo("P1");
        Assert.That(work.Buffer(rows[0].Cells[column]), Is.EqualTo("independent"));
        Assert.That(work.DifferenceCount, Is.Zero);
    }

    [Test]
    public async Task LegacyHistoryKeepsItsMeaningAlongsideDetachedNewHistory()
    {
        var project = EditingTests.Registration(count: 2); var work = new EditingWorkspace(project.Snapshot.Id.Scope);
        var rows = work.Open(project); work.SetBuffer(rows[0].Cells[0], "legacy input");
        work.Commit("P1", rows[0].Cells[0], "legacy input");
        var legacy = EditingTests.LegacyHistory(work.Snapshot()) with { Version = 12 };
        // Construct the old encoded contract explicitly, rather than pretending
        // a current Commit generated historical own-input restoration semantics.
        legacy = legacy with { History = legacy.History.Select(t => t with { Changes = t.Changes.Select(c =>
            c with { Before = c.Before with { Buffer = "legacy input" } }).ToArray() }).ToArray() };
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-legacy-undo-" + Guid.NewGuid()));
        await store.SaveAsync(legacy, 0);
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(store.FileFor(work.Scope)))!;
        foreach (var tx in node["History"]!.AsArray()) tx!.AsObject().Remove("BufferWrites");
        await File.WriteAllTextAsync(store.FileFor(work.Scope), node.ToJsonString());
        work = EditingWorkspace.Restore((await store.LoadAsync(work.Scope))!);
        work.SetBuffer(rows[1].Cells[0], "new input"); work.Commit("P1", rows[1].Cells[0], "new input");
        var captured = work.Snapshot(); var expected = JsonSerializer.Serialize(captured);
        var detached = work.Snapshot(); detached.History.Last().BufferWrites![0] = new("Title", "foreign");
        Assert.That(JsonSerializer.Serialize(captured), Is.EqualTo(expected));
        Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(expected));
        await store.SaveAsync(work.Snapshot(), legacy.Revision);
        work = EditingWorkspace.Restore((await store.LoadAsync(work.Scope))!);
        Assert.That(work.Snapshot().History[0].BufferWrites, Is.Null);
        work.Undo("P1"); Assert.That(work.Buffer(rows[1].Cells[0]), Is.Null);
        work.Undo("P1"); Assert.That(work.Buffer(rows[0].Cells[0]), Is.EqualTo("legacy input"));
        Assert.That(work.Value(rows[0].Cells[0]), Is.EqualTo("Issue 1"));
    }
}
