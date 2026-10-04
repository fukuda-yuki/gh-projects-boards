using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class WorkAllocationInputTests
{
    private static (ProjectRegistration Project, EditingWorkspace Work) Fixture(bool reports = true)
    {
        var project = PlanningPathTests.Registration();
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        var task = new PlanningTask("I1", PlanningMode.Auto, "U1", Progress: PlanningProgress.InProgress,
            ActualStart: PlanningContractTests.At("2026-10-05 09:00"),
            Actuals: reports ? [new("U-old", 3, new(2026, 10, 5)), new(null, 2, new(2026, 10, 6))] : null,
            Contributions: [new("U-old", 6, 1), new("U1", 8, 2), new("U-known", null, null)]);
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = [task] }, work.Revision,
            [new("P1T1", "Estimate", "16"), new("P1T1", "Remaining", "4")]);
        return (project, work);
    }

    [Test]
    public async Task TwoWorkerAllocationPersistsIndependentTotalsAndRestoresReportsSharesAndScheduleWithOneUndo()
    {
        var (project, work) = Fixture(); var row = work.Open(project)[0];
        var actual = row.Cells.Single(c => c.Key?.FieldId == "F-Actual");
        var remaining = row.Cells.Single(c => c.Key?.FieldId == "F-Remaining");
        var beforePlan = JsonSerializer.Serialize(work.Planning("P1"));
        var beforeFinish = work.PlanFor(project).Tasks[0].Finish;
        work.SetPlanningBuffer(actual, "7"); work.SetBuffer(remaining, "3"); work.SetBuffer(row.Cells[0], "keep title input");
        var reports = new[] { new ActualContribution("U-old", 5, new(2026, 10, 13)), new ActualContribution(null, 2, new(2026, 10, 6)) };

        work.CommitWorkAllocation(project, row.ItemId, reports, "3", [new("U-old", 1), new("U1", 1.5m)], work.Revision);

        Assert.Multiple(() => {
            Assert.That(work.Value(actual), Is.EqualTo("7")); Assert.That(work.Value(remaining), Is.EqualTo("3"));
            Assert.That(work.Buffer(actual), Is.Null); Assert.That(work.Buffer(remaining), Is.Null);
            Assert.That(work.Planning("P1")!.Tasks[0].Actuals, Is.EqualTo(reports));
            Assert.That(work.Planning("P1")!.Tasks[0].Contributions, Is.EqualTo(new[] {
                new WorkContribution("U-old", 6, 1), new WorkContribution("U1", 8, 1.5m), new WorkContribution("U-known", null, null) }));
            Assert.That(work.PlanFor(project).Tasks[0].Finish, Is.Not.EqualTo(beforeFinish));
            Assert.That(work.Journal, Is.Empty);
        });
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-allocation-" + Guid.NewGuid().ToString("N")));
        Assert.That(await new DraftSession(store, work, 0).FlushAsync(), Is.True);
        work = EditingWorkspace.Restore((await store.LoadAsync(work.Scope))!);
        Assert.That(work.Value(actual), Is.EqualTo("7")); Assert.That(work.Value(remaining), Is.EqualTo("3"));
        work.Undo("P1");
        Assert.Multiple(() => {
            Assert.That(work.Value(actual), Is.EqualTo("5")); Assert.That(work.Value(remaining), Is.EqualTo("4"));
            Assert.That(work.Buffer(actual), Is.Null); Assert.That(work.Buffer(remaining), Is.Null);
            Assert.That(work.Buffer(row.Cells[0]), Is.EqualTo("keep title input"));
            Assert.That(JsonSerializer.Serialize(work.Planning("P1")), Is.EqualTo(beforePlan));
            Assert.That(work.PlanFor(project).Tasks[0].Finish, Is.EqualTo(beforeFinish));
            Assert.That(work.Journal, Is.Empty);
        });
    }

    [TestCase("overflow"), TestCase("unknown-total"), TestCase("duplicate-person"), TestCase("invalid-hours"), TestCase("missing-date")]
    [TestCase("stale"), TestCase("actual-buffer"), TestCase("remaining-buffer"), TestCase("remove-all")]
    public void InvalidAllocationRejectsEveryValueAndKeepsAllInput(string reason)
    {
        var (project, work) = Fixture(); var row = work.Open(project)[0];
        if (reason == "actual-buffer") work.SetPlanningBuffer(row.Cells.Single(c => c.Key?.FieldId == "F-Actual"), "9");
        if (reason == "remaining-buffer") work.SetBuffer(row.Cells.Single(c => c.Key?.FieldId == "F-Remaining"), "9");
        var reports = reason == "remove-all" ? Array.Empty<ActualContribution>() : new[] {
            new ActualContribution("U-old", 5, reason == "missing-date" ? default : new(2026, 10, 13)), new ActualContribution(null, 2, new(2026, 10, 6)) };
        RemainingContribution[] shares = reason == "duplicate-person" ? [new("U1", 1), new("U1", 1)]
            : [new("U1", reason == "invalid-hours" ? -1 : reason == "overflow" ? 4 : 2)];
        var snapshot = JsonSerializer.Serialize(work.Snapshot());

        Assert.That(() => work.CommitWorkAllocation(project, row.ItemId, reports, reason == "unknown-total" ? null : "3", shares,
            reason == "stale" ? work.Revision - 1 : work.Revision), Throws.Exception.TypeOf<InvalidOperationException>().Or.TypeOf<InvalidDataException>());

        Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(snapshot));
    }

    [Test]
    public void UnknownActualAndRemainingAreNotZeroAndExplicitZeroKeepsItsWorkerAndReportingDay()
    {
        var (project, work) = Fixture(reports: false); var row = work.Open(project)[0];
        work.CommitWorkAllocation(project, row.ItemId, [], null, [], work.Revision);
        Assert.That(work.Value(row.Cells.Single(c => c.Key?.FieldId == "F-Actual")), Is.Null);
        Assert.That(work.Value(row.Cells.Single(c => c.Key?.FieldId == "F-Remaining")), Is.Null);
        Assert.That(work.Planning("P1")!.Tasks[0].Actuals, Is.Null);
        Assert.That(work.Planning("P1")!.Tasks[0].Contributions!.All(c => c.RemainingHours is null), Is.True);
        work.CommitWorkAllocation(project, row.ItemId, [new("U2", 0, new(2026, 10, 13))], "0", [new("U2", 0)], work.Revision);
        Assert.That(work.Value(row.Cells.Single(c => c.Key?.FieldId == "F-Actual")), Is.EqualTo("0"));
        Assert.That(work.Value(row.Cells.Single(c => c.Key?.FieldId == "F-Remaining")), Is.EqualTo("0"));
        Assert.That(work.Planning("P1")!.Tasks[0].Actuals!.Single(), Is.EqualTo(new ActualContribution("U2", 0, new(2026, 10, 13))));
        Assert.That(work.Planning("P1")!.Tasks[0].Contributions!.Single(c => c.PersonId == "U2").RemainingHours, Is.Zero);
    }

    [Test]
    public void ConfirmedBlankRemainingBufferClearsTheTotalWithoutReopeningConsumedInputOnUndo()
    {
        var (project, work) = Fixture(); var row = work.Open(project)[0];
        var remaining = row.Cells.Single(c => c.Key?.FieldId == "F-Remaining");
        work.SetBuffer(remaining, "");
        work.CommitWorkAllocation(project, row.ItemId, work.Planning("P1")!.Tasks[0].Actuals!, null, [], work.Revision);
        Assert.That(work.Value(remaining), Is.Null); Assert.That(work.Buffer(remaining), Is.Null);
        work.Undo("P1");
        Assert.That(work.Value(remaining), Is.EqualTo("4")); Assert.That(work.Buffer(remaining), Is.Null);
    }

    [Test]
    public void AnUnavailableRemainingFieldCannotPartiallyUpdateActualReports()
    {
        var (project, work) = Fixture();
        var fresh = project with { Snapshot = project.Snapshot with { Fields = project.Snapshot.Fields.Where(f => f.Id.NodeId != "F-Remaining").ToArray() } };
        work.SetRegistrations([fresh]); var before = JsonSerializer.Serialize(work.Snapshot());
        Assert.That(() => work.CommitWorkAllocation(fresh, "P1T1", [new("U-old", 7, new(2026, 10, 13))], "3", [new("U1", 2)], work.Revision), Throws.InvalidOperationException);
        Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
    }
}
