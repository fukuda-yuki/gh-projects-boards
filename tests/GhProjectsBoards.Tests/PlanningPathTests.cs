using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanningPathTests
{
    internal static ProjectRegistration Registration(int count = 2)
    {
        var p = EditingTests.Registration(count: count); var snapshot = p.Snapshot;
        var fields = new[] { ("Estimate", "NUMBER"), ("Remaining", "NUMBER"), ("Actual", "NUMBER"), ("Start", "DATE"), ("Finish", "DATE") }
            .Select(f => new ProjectFieldDefinition(new(snapshot.Id.Scope, "F-" + f.Item1), snapshot.Id, f.Item1,
                "ProjectV2Field", f.Item2, FieldOwner.ProjectItem, [], ValueAvailability.Present)).ToArray();
        return p with { Snapshot = snapshot with { Fields = snapshot.Fields.Concat(fields).ToArray(),
            Issues = snapshot.Issues.ToDictionary(i => i.Key, i => i.Value with { Native = new([], [], new(ValueAvailability.Empty), true) }),
            Items = snapshot.Items.Select(i => i with { Values = i.Values.Concat(fields.Select(f =>
                new ProjectFieldValue(f.Id, null, "Absent", ValueAvailability.Empty))).ToArray() }).ToArray() } };
    }
    internal static ProjectPlanning Plan() => new(1, "P1", 0, PlanningContractTests.At("2026-10-05 09:00"),
        PlanningContractTests.At("2026-10-05 09:00"),
        PlanningContract.Roles.Select(r => new PlanningFieldBinding(r, "F-" + r, r is "Start" or "Finish" ? "DATE" : "NUMBER")).ToArray(),
        new("official-2025-2027", PlanningContract.BundledHolidays(), false, []), [new("U1", "Owner", 100)], []);

    [Test]
    public void SelectedEffortUsesExistingAtomicDraftAndReviewPath()
    {
        var p = Registration(); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); w.SetPlanning(Plan(), 0);
        var rows = w.Open(p); var cell = rows[0].Cells.SingleOrDefault(c => c.Key?.FieldId == "F-Estimate");
        Assert.That(cell, Is.Not.Null, "Explicitly mapped NUMBER must be present in the ordinary editing model.");
        w.Commit("P1", cell!, "16.000");
        Assert.That(w.Value(cell!), Is.EqualTo("16"));
        var review = w.ReviewApply(p, new HashSet<string> { rows[0].ItemId });
        Assert.That(review.Blocked, Is.Empty);
        Assert.That(review.Batch.Operations.Single().Intended.Value, Is.EqualTo("16"));
        var restored = EditingWorkspace.Restore(w.Snapshot());
        restored.Undo("P1");
        Assert.That(restored.Value(cell!), Is.Null);
    }

    [Test]
    public async Task AutoDatesAndEffortUseRealCheckpointRefreshApplyAndIndependentReadback()
    {
        var h = await ApplyTests.Harness.Create(2, planning: true); Assert.That(await h.Workspace.PrepareLocalRowsAsync(), Is.True);
        var p = h.Workspace.Selected!; var w = h.Workspace.Drafts!.Workspace;
        w.CommitPlanning(p, Plan() with { Tasks = [new("I1", PlanningMode.Auto, "U1")] }, w.Revision);
        var cell = w.Open(p)[0].Cells.Single(c => c.Key?.FieldId == "F-Estimate"); w.Commit("P1", cell, "16");
        Assert.That(w.Value(cell), Is.EqualTo("16"));
        Assert.That(w.CheckpointRegistrations.Select(p => p.Snapshot.Id.NodeId), Does.Contain("P1"));
        Assert.That(w.PlanFor(p).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-06 18:00")), System.Text.Json.JsonSerializer.Serialize(w.PlanFor(p)));
        Assert.That(await h.Workspace.Drafts.FlushAsync(), Is.True, h.Workspace.Drafts.Status);
        var restored = EditingWorkspace.Restore((await new DraftStore(h.Root).LoadAsync(w.Scope))!);
        Assert.That(restored.PlanFor(p).Tasks[0].Finish, Is.EqualTo(w.PlanFor(p).Tasks[0].Finish));
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        Assert.That(h.Workspace.ApplyReview!.Blocked, Is.Empty);
        Assert.That(h.Workspace.ApplyReview.Batch.Operations.Length, Is.EqualTo(3));
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview);
        w = h.Workspace.Drafts.Workspace;
        Assert.That(w.Journal.Single().Operations.All(o => o.State == ApplyState.Succeeded), Is.True, h.Workspace.Status);
        Assert.That(h.Scalars["P1-T1/F-Finish"]!.GetValue<string>(), Is.EqualTo("2026-10-06"));
        Assert.That(w.DifferenceCount, Is.Zero);
        h.Scalars["P1-T1/F-Estimate"] = 8m;
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        Assert.That(h.Workspace.ApplyReview!.Blocked, Is.Empty);
        Assert.That(h.Workspace.ApplyReview.Batch.Operations.Single().Intended.Value, Is.EqualTo("2026-10-05"));
        Assert.That(h.Workspace.Drafts.Workspace.PlanFor(h.Workspace.Selected!).Tasks[0].Finish,
            Is.EqualTo(PlanningContractTests.At("2026-10-05 18:00")));
    }

    [Test]
    public async Task ActualProjectionRoundTripsWithExplicitAttributionDecisionAndIndependentRemaining()
    {
        var h = await ApplyTests.Harness.Create(2, planning: true); await h.Workspace.PrepareLocalRowsAsync();
        var w = h.Workspace.Drafts!.Workspace; var p = h.Workspace.Selected!;
        var task = new PlanningTask("I1", PlanningMode.Auto, "U1", Progress: PlanningProgress.InProgress,
            ActualStart: PlanningContractTests.At("2026-10-05 09:00"), Actuals: [new("U1", 5, new(2026, 10, 5))]);
        w.CommitPlanning(p, Plan() with { Tasks = [task] }, w.Revision, [new("P1-T1", "Estimate", "16"), new("P1-T1", "Remaining", "4")]);
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Scalars["P1-T1/F-Actual"]!.GetValue<decimal>(), Is.EqualTo(5));
        h.Scalars["P1-T1/F-Actual"] = 6m;
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        w = h.Workspace.Drafts.Workspace; p = h.Workspace.Selected!;
        var decision = w.PlanningDecisions("P1", "P1-T1").Single();
        Assert.That(w.Planning("P1")!.Tasks[0].Actuals!.Single().Hours, Is.EqualTo(5));
        Assert.That(h.Workspace.ApplyReview!.Blocked, Is.Not.Empty);
        var corrected = task with { Actuals = [new("U1", 6, new(2026, 10, 6))] };
        w.CommitPlanning(p, w.Planning("P1")! with { Tasks = [corrected] }, w.Revision, decisions: [new(decision.Key, decision.Observation!.Id, true)]);
        Assert.That(await h.Workspace.Drafts.FlushAsync(), Is.True);
        var restored = EditingWorkspace.Restore((await new DraftStore(h.Root).LoadAsync(w.Scope))!);
        var input = restored.PlanFor(p).Inputs!.Single(i => i.Task.Id == "I1");
        Assert.That(input.Remaining, Is.EqualTo(4)); Assert.That(input.Estimate, Is.EqualTo(16));
        Assert.That(input.Task.Actuals, Is.EqualTo(corrected.Actuals));
        Assert.That(restored.PlanningDecisions("P1", "P1-T1"), Is.Empty);
    }
    [Test]
    public async Task UnmappedNegativeNativeNumbersDoNotBlockCompleteRetrieval()
    {
        var h = await ApplyTests.Harness.Create(2, planning: true); h.Scalars["P1-T1/F-Estimate"] = -1m;
        var read = await new ProjectReader(h.Service).ReadAsync(h.Context, h.Workspace.Selected!.Snapshot.Id);
        Assert.That(read.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
        Assert.That(read.Project!.Items[0].Values.Single(v => v.FieldId?.NodeId == "F-Estimate").Scalar, Is.EqualTo("-1"));
    }
    [TestCase("9e-29", "0.00000000000000000000000000009")]
    [TestCase("1.23456789e-25", "0.000000000000000000000000123456789")]
    [TestCase("16.000", "16")]
    public void NativeFloatObservationDoesNotRoundThroughDecimal(string raw, string expected)
    {
        using var json = System.Text.Json.JsonDocument.Parse(raw);
        Assert.That(PlanningScalars.RemoteNumber(json.RootElement), Is.EqualTo(expected));
    }

    [Test]
    public void UnavailableRowsDoNotPreventPlanningAndInvalidSetupIsAtomic()
    {
        var p = Registration(); p = p with { Snapshot = p.Snapshot with { Items = p.Snapshot.Items.Append(
            new(new(p.Snapshot.Id.Scope, "private-item"), ProjectItemKind.Unavailable, "REDACTED", null, false, [], true)).ToArray() } };
        var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); w.CommitPlanning(p, Plan(), w.Revision);
        Assert.That(w.PlanFor(p).Tasks.Length, Is.EqualTo(2));
        var before = System.Text.Json.JsonSerializer.Serialize(w.Snapshot());
        Assert.Throws<InvalidDataException>(() => w.CommitPlanning(p, Plan() with { Tasks = [new("I1", PlanningMode.Manual,
            ManualStart: PlanningContractTests.At("2026-10-06 09:00"), ManualFinish: PlanningContractTests.At("2026-10-05 18:00"))] }, w.Revision));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(w.Snapshot()), Is.EqualTo(before));
        Assert.That(w.Open(p)[0].Cells.Where(c => c.Key?.FieldId is "F-Actual" or "F-Start" or "F-Finish").All(c => !c.Editable), Is.True);
    }
}
