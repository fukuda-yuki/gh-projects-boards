using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanningCellInputTests
{
    private static (ProjectRegistration Project, EditingWorkspace Work, EditCell Cell) Work(ActualContribution[]? reports = null)
    {
        var p = PlanningPathTests.Registration();
        p = p with { Snapshot = p.Snapshot with { Issues = p.Snapshot.Issues.ToDictionary(x => x.Key, x => x.Value with {
            Native = x.Value.Native! with { Assignees = [new(new(p.Snapshot.Id.Scope, "U2"), "Current worker")] } }) } };
        var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        w.CommitPlanning(p, PlanningPathTests.Plan() with { Tasks = [new("I1", Actuals: reports)] }, w.Revision,
            [new("P1T1", "Estimate", "16"), new("P1T1", "Remaining", "4")]);
        return (p, w, w.Open(p)[0].Cells.Single(c => c.Key?.FieldId == "F-Actual"));
    }
    [TestCase("7", "7"), TestCase("0", "0"), TestCase("7.000", "7")]
    public async Task DatedCorrectionKeepsHistoricalWorkerIndependentWorkAndRestorableUndo(string text, string expected)
    {
        var (p, w, cell) = Work([new("U1", 5, new(2026, 10, 6))]);
        w.SetPlanningBuffer(cell, text);
        var context = w.ActualInput(p, "P1T1");
        Assert.That(context.PersonId, Is.EqualTo("U1")); Assert.That(context.Historical, Is.True);
        Assert.That(w.Value(cell), Is.EqualTo("5")); Assert.That(cell.Editable, Is.False);
        Assert.That(() => w.Commit("P1", cell, text), Throws.InvalidOperationException);
        w.CommitActualInput(p, "P1T1", text, new(2026, 10, 13), context.PersonId, w.Revision);
        Assert.That(w.Value(cell), Is.EqualTo(expected)); Assert.That(w.Buffer(cell), Is.Null);
        Assert.That(w.Planning("P1")!.Tasks.Single().Actuals, Is.EqualTo(new[] { new ActualContribution("U1", decimal.Parse(expected), new(2026, 10, 13)) }));
        var root = Path.Combine(Path.GetTempPath(), "ghpb-typed-actual-" + Guid.NewGuid().ToString("N"));
        var store = new DraftStore(root); Assert.That(await new DraftSession(store, w, 0).FlushAsync(), Is.True);
        w = EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!);
        var row = w.Open(p)[0];
        Assert.That(w.Value(row.Cells.Single(c => c.Key?.FieldId == "F-Estimate")), Is.EqualTo("16"));
        Assert.That(w.Value(row.Cells.Single(c => c.Key?.FieldId == "F-Remaining")), Is.EqualTo("4"));
        w.Undo("P1");
        Assert.That(w.Value(cell), Is.EqualTo("5")); Assert.That(w.Buffer(cell), Is.EqualTo(text));
        Assert.That(w.Planning("P1")!.Tasks.Single().Actuals![0].ReportedThrough, Is.EqualTo(new DateOnly(2026, 10, 6)));
        Assert.That(w.Journal, Is.Empty);
    }
    [Test]
    public void NewSingleWorkerIsProposedButUnknownAndExplicitRemovalRemainDifferentFromZero()
    {
        var (p, w, cell) = Work();
        var context = w.ActualInput(p, "P1T1");
        Assert.That(context.HasPerson, Is.True); Assert.That(context.PersonId, Is.EqualTo("U2"));
        Assert.That(context.ReportedThrough, Is.Null); Assert.That(w.Value(cell), Is.Null);
        w.CommitActualInput(p, "P1T1", "0", new(2026, 10, 6), "U2", w.Revision);
        Assert.That(w.Value(cell), Is.EqualTo("0"));
        w.RemoveActualInput(p, "P1T1", w.Revision);
        Assert.That(w.Value(cell), Is.Null); Assert.That(w.Planning("P1")!.Tasks.Single().Actuals, Is.Empty);
        w.Undo("P1"); Assert.That(w.Value(cell), Is.EqualTo("0"));
    }
    [TestCase("multiple"), TestCase("worker"), TestCase("date"), TestCase("invalid"), TestCase("revision")]
    public void AmbiguousOrInvalidCorrectionPreservesAllDraftAndHistoricalState(string invalid)
    {
        var (p, w, cell) = Work(invalid == "multiple" ? [new("U1", 3, new(2026, 10, 6)), new(null, 2, new(2026, 10, 6))] : [new("U1", 5, new(2026, 10, 6))]);
        w.SetPlanningBuffer(cell, "7"); var before = System.Text.Json.JsonSerializer.Serialize(w.Snapshot());
        Assert.That(() => w.CommitActualInput(p, "P1T1", invalid == "invalid" ? "-1" : "7", invalid == "date" ? default : new(2026, 10, 13),
            invalid == "worker" ? "U2" : "U1", invalid == "revision" ? w.Revision - 1 : w.Revision), Throws.InvalidOperationException);
        Assert.That(System.Text.Json.JsonSerializer.Serialize(w.Snapshot()), Is.EqualTo(before));
    }
    [TestCase(false), TestCase(true)]
    public void CompatibleRefreshKeepsPendingActualAndAConfirmedCorrectionUpdatesTheMatchingProjection(bool remoteMatchesLocal)
    {
        var (p, w, cell) = Work([new("U1", 5, new(2026, 10, 6))]);
        w.SetPlanningBuffer(cell, "7");
        var fresh = p with { RetrievedAt = p.RetrievedAt.AddMinutes(1), Snapshot = p.Snapshot with {
            Items = p.Snapshot.Items.Select(i => i with { Values = i.Values.Select(v => v.FieldId?.NodeId == "F-Actual" && remoteMatchesLocal
                ? v with { Scalar = "5", Availability = ValueAvailability.Present } : v).ToArray() }).ToArray() } };
        w.Reconcile(p, fresh); w.SetRegistrations([fresh]);
        Assert.That(w.Buffer(cell), Is.EqualTo("7"));
        w.CommitActualInput(fresh, "P1T1", "7", new(2026, 10, 13), "U1", w.Revision);
        Assert.That(w.Value(cell), Is.EqualTo("7"));
        Assert.That(w.Planning("P1")!.Tasks.Single().Actuals![0].Hours, Is.EqualTo(7));
        Assert.That(w.Buffer(cell), Is.Null); Assert.That(w.Field(cell)!.Observation!.Reason, Is.Null);
    }
}
