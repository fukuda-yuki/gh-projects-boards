using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class SummaryTests
{
    internal static readonly DateOnly Day = new(2026, 9, 18);
    internal static (ProjectRegistration Project, EditingWorkspace Work) Example(DateOnly? reportingDay = null)
    {
        var day = reportingDay ?? Day;
        var p = PlanningPathTests.Registration();
        var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        var plan = PlanningPathTests.Plan() with { Cutoff = day.ToDateTime(new(18, 0)), People = [new("A", "A"), new("B", "B")],
            Tasks = [new("I1", OwnerId: "A", Actuals: [new("A", 48, day)]), new("I2", OwnerId: "B", Actuals: [new("B", 56, day)])] };
        w.CommitPlanning(p, plan, w.Revision, [new("P1T1", "Estimate", "144"), new("P1T1", "Remaining", "72"),
            new("P1T2", "Estimate", "64"), new("P1T2", "Remaining", "40")]);
        return (p, w);
    }
    [Test]
    public void FourRawValuesUseIndependentRemainingAndCountProjectWorkOnce()
    {
        var (p, w) = Example();
        w.SetAllowance(p, "A", 160, w.Revision); w.SetAllowance(p, "B", 80, w.Revision);
        var result = SummaryProjection.Create(w, p, Day);
        Assert.That(result.People.Select(p => p.Id), Is.EquivalentTo(new[] { "A", "B" }));
        var a = result.People.Single(p => p.Id == "A"); var b = result.People.Single(p => p.Id == "B");
        Assert.That(new[] { a.Estimate.Days, a.Actual.Days, a.Remaining.Days, a.Forecast.Days }, Is.EqualTo(new[] { 18m, 6m, 9m, 15m }));
        Assert.That(b.Forecast.Days, Is.EqualTo(12m));
        Assert.That(a.Headroom / 8, Is.EqualTo(5)); Assert.That(b.Headroom / 8, Is.EqualTo(-2));
        Assert.That(result.Estimate.Days, Is.EqualTo(26m)); Assert.That(result.Actual.Days, Is.EqualTo(13m));
    }
    [Test]
    public void RemainingAndWeightEditsPreserveRawWorkAndProtectedSnapshot()
    {
        var (p, w) = Example(); w.SetAllowance(p, "A", 160, w.Revision);
        w.CaptureBaseline(p, w.Revision, null, DateTimeOffset.UtcNow); var before = Json(w.Planning("P1")!.Summary!.Baseline);
        var cell = w.Open(p)[0].Cells.Single(c => c.Key?.FieldId == "F-Remaining"); w.Commit("P1", cell, "96");
        var plan = w.Planning("P1")!; w.CommitPlanning(p, plan with { People = [new("A", "A", 50), new("B", "B")] }, w.Revision);
        var a = SummaryProjection.Create(w, p, Day).People.Single(p => p.Id == "A");
        Assert.That(new[] { a.Allowance, a.Estimate.Hours, a.Actual.Hours, a.Forecast.Hours }, Is.EqualTo(new decimal?[] { 160, 144, 48, 144 }));
        Assert.That(Json(w.Planning("P1")!.Summary!.Baseline), Is.EqualTo(before));
    }
    [TestCase(-1, false, 48, false)]
    [TestCase(0, true, 48, true)]
    [TestCase(1, false, 0, false)]
    public void ReportDatesRemainDatedAndFutureAmountsNeverBecomeSpent(int offset, bool complete, decimal actual, bool zeroRemaining)
    {
        var (p, w) = Example(); var plan = w.Planning("P1")!;
        w.CommitPlanning(p, plan with { Tasks = [plan.Tasks[0] with { Actuals = [new("A", 48, Day.AddDays(offset))] }, plan.Tasks[1]] }, w.Revision,
            [new("P1T1", "Remaining", zeroRemaining ? "0" : null)]);
        var result = SummaryProjection.Create(w, p, Day); var a = result.People.Single(p => p.Id == "A");
        Assert.That(a.Actual.Hours, Is.EqualTo(actual)); Assert.That(a.Actual.Complete, Is.EqualTo(complete));
        Assert.That(a.Forecast.Complete, Is.EqualTo(complete)); Assert.That(result.Contributions.Single(c => c.PersonId == "A").ReportedThrough, Is.EqualTo(Day.AddDays(offset)));
        Assert.That(a.Remaining.Unknown, Is.EqualTo(zeroRemaining ? 0 : 1));
    }
    [Test]
    public void ReassignmentAndJointSharesRetainHistoricalPeopleAndUnallocatedBalances()
    {
        var (p, w) = Example(); var plan = w.Planning("P1")!;
        w.CommitPlanning(p, plan with { Tasks = [plan.Tasks[0] with { OwnerId = "B", Contributions = [new("B", 80, 32), new("C", 40, null)] }, plan.Tasks[1]] }, w.Revision);
        w.SetAllowance(p, "retired", 0, w.Revision);
        var result = SummaryProjection.Create(w, p, Day);
        var historical = result.People.Single(p => p.Id == "A");
        Assert.That(historical.Actual.Hours, Is.EqualTo(48)); Assert.That(historical.Remaining.Hours, Is.Zero);
        Assert.That(result.People.Single(p => p.Id == "").Estimate.Hours, Is.EqualTo(24));
        Assert.That(result.People.Single(p => p.Id == "").Remaining.Hours, Is.EqualTo(40));
        Assert.That(result.People.Single(p => p.Id == "C").Remaining.Complete, Is.False);
        Assert.That(result.People.Single(p => p.Id == "retired").Allowance, Is.Zero);
        Assert.That(result.People.Sum(p => p.Estimate.Hours), Is.EqualTo(208)); Assert.That(result.People.Sum(p => p.Actual.Hours), Is.EqualTo(104));
    }
    [TestCase(TaskLaborKind.Direct, 208, true)]
    [TestCase(TaskLaborKind.Rollup, 64, true)]
    [TestCase(TaskLaborKind.Unspecified, 64, false)]
    public void ParentClassificationAndDuplicateAppearancesCannotDoubleTaskLabor(TaskLaborKind kind, decimal expected, bool complete)
    {
        var (p, w) = Example(); var second = p.Snapshot.Issues[new(w.Scope, "I2")];
        p = p with { Snapshot = p.Snapshot with { Items = p.Snapshot.Items.Append(p.Snapshot.Items[0] with { Id = new(w.Scope, "duplicate") }).ToArray(),
            Issues = p.Snapshot.Issues.ToDictionary(i => i.Key, i => i.Key == second.Id ? i.Value with { Native = i.Value.Native! with { Parent = new(ValueAvailability.Present, new(w.Scope, "I1")) } } : i.Value) } };
        w.SetRegistrations([p]); var plan = w.Planning("P1")!;
        w.CommitPlanning(p, plan with { Tasks = [plan.Tasks[0] with { LaborKind = kind }, plan.Tasks[1]] }, w.Revision);
        var result = SummaryProjection.Create(w, p, Day);
        Assert.That(result.Estimate.Hours, Is.EqualTo(expected)); Assert.That(result.Estimate.Complete, Is.EqualTo(complete));
        Assert.That(result.TaskCount, Is.EqualTo(2));
    }
    [Test]
    public void ProtectedEstablishReplaceAndUndoAreExplicitAndScoped()
    {
        var (p, w) = Example(); w.CaptureBaseline(p, w.Revision, null, DateTimeOffset.UtcNow);
        var old = w.Planning("P1")!.Summary!.Baseline!;
        Assert.Throws<InvalidOperationException>(() => w.CaptureBaseline(p, w.Revision, null, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => w.CommitPlanning(p, w.Planning("P1")! with { Summary = null }, w.Revision));
        w.SetAllowance(p, "A", 8, w.Revision); w.Undo("P1"); Assert.That(w.Planning("P1")!.Summary!.Allowances, Is.Empty);
        w.CaptureBaseline(p, w.Revision, old.Id, DateTimeOffset.UtcNow);
        Assert.That(w.Planning("P1")!.Summary!.Baseline!.Id, Is.Not.EqualTo(old.Id));
        w.Undo("P1"); Assert.That(w.Planning("P1")!.Summary!.Baseline, Is.EqualTo(old));
        w.Undo("P1"); Assert.That(w.Planning("P1")!.Summary?.Baseline, Is.Null);
    }
    [Test]
    public async Task FailedReplacementPreservesDurableBaselineAndRetryBackupAndOldMigrationAreLossless()
    {
        var (p, w) = Example(); w.SetAllowance(p, "A", 0, w.Revision); w.CaptureBaseline(p, w.Revision, null, DateTimeOffset.UtcNow);
        var root = Path.Combine(Path.GetTempPath(), "ghpb-summary-" + Guid.NewGuid().ToString("N")); var store = new DraftStore(root);
        var session = new DraftSession(store, w, 0); Assert.That(await session.FlushAsync(), Is.True);
        var original = await File.ReadAllBytesAsync(store.FileFor(w.Scope)); var old = w.Planning("P1")!.Summary!.Baseline!;
        async Task<bool> Replace() => await session.CommitAsync(candidate => { candidate.CaptureBaseline(p, candidate.Revision, old.Id, DateTimeOffset.UtcNow); return candidate; }, () => true);
        using (var locked = new FileStream(Path.Combine(root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.That(await Replace(), Is.False); Assert.That(await File.ReadAllBytesAsync(store.FileFor(w.Scope)), Is.EqualTo(original));
            Assert.That(session.Workspace.Planning("P1")!.Summary!.Baseline!.Id, Is.EqualTo(old.Id));
        }
        Assert.That(await Replace(), Is.True); session.Workspace.Undo("P1"); Assert.That(await session.FlushAsync(), Is.True);
        var backup = Path.Combine(root, "portable.json"); await store.ExportBackupAsync(w.Scope, backup);
        var restoredStore = new DraftStore(root + "-restored"); await restoredStore.RestoreBackupAsync(backup, w.Scope);
        var restored = (await restoredStore.LoadAsync(w.Scope))!;
        Assert.That(Json(restored), Is.EqualTo(Json(session.Workspace.Snapshot())));
        var legacy = JsonNode.Parse(Json(Example().Work.Snapshot()))!; legacy["Version"] = 9;
        foreach (var plan in legacy["Planning"]!.AsArray()) { plan!.AsObject().Remove("Summary"); foreach (var task in plan["Tasks"]!.AsArray()) task!.AsObject().Remove("LaborKind"); }
        foreach (var tx in legacy["History"]!.AsArray()) if (tx?["Plan"] is { } change)
            foreach (var side in new[] { "Before", "After" }) if (change[side] is { } plan) { plan.AsObject().Remove("Summary"); foreach (var task in plan["Tasks"]!.AsArray()) task!.AsObject().Remove("LaborKind"); }
        var legacyRoot = root + "-legacy"; var legacyStore = new DraftStore(legacyRoot); Directory.CreateDirectory(Path.GetDirectoryName(legacyStore.FileFor(w.Scope))!);
        await File.WriteAllTextAsync(legacyStore.FileFor(w.Scope), legacy.ToJsonString());
        Assert.That((await legacyStore.LoadAsync(w.Scope))!.Planning!.Single().Summary, Is.Null);
        legacy["Version"] = 999; var unsupported = legacy.ToJsonString(); await File.WriteAllTextAsync(legacyStore.FileFor(w.Scope), unsupported);
        Assert.ThrowsAsync<InvalidDataException>(async () => await legacyStore.LoadAsync(w.Scope));
        Assert.That(await File.ReadAllTextAsync(legacyStore.FileFor(w.Scope)), Is.EqualTo(unsupported));
    }
    private static string Json(object? value) => JsonSerializer.Serialize(value);

    [Test]
    public void MissingJointWorkerReportKeepsKnownProjectActualSubtotalIncomplete()
    {
        var (p, w) = Example(); var plan = w.Planning("P1")!;
        w.CommitPlanning(p, plan with { Tasks = [plan.Tasks[0] with {
            Contributions = [new("A", 80, 40), new("C", 64, 32)] }, plan.Tasks[1]] }, w.Revision);
        var result = SummaryProjection.Create(w, p, Day);
        Assert.That(result.Actual.Hours, Is.EqualTo(104));
        Assert.That(result.Actual.Complete, Is.False, "A dated subtotal cannot certify the missing worker's actual as zero.");
        Assert.That(result.People.Single(p => p.Id == "C").Actual.Unknown, Is.EqualTo(1));
    }

    [TestCase("Summary"), TestCase("LaborKind")]
    public async Task CurrentSchemaMissingSummaryMetadataIsRejectedWithoutChangingBytes(string property)
    {
        var (_, w) = Example(); var record = JsonNode.Parse(Json(w.Snapshot()))!;
        var plan = record["Planning"]![0]!;
        if (property == "Summary") plan.AsObject().Remove(property);
        else plan["Tasks"]![0]!.AsObject().Remove(property);
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-summary-corrupt-" + Guid.NewGuid().ToString("N")));
        var file = store.FileFor(w.Scope); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var original = record.ToJsonString(); await File.WriteAllTextAsync(file, original);
        Assert.ThrowsAsync<InvalidDataException>(async () => await store.LoadAsync(w.Scope));
        Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(original));
    }

    [Test]
    public void OpeningAnOlderCutoffKeepsItsDateAndCannotCertifyCurrentHeadroom()
    {
        var (p, w) = Example(); w.SetAllowance(p, "A", 160, w.Revision); var before = Json(w.Snapshot());
        var result = SummaryProjection.Create(w, p, Day.AddDays(1)); var a = result.People.Single(p => p.Id == "A");
        Assert.That(result.Cutoff, Is.EqualTo(Day)); Assert.That(a.Actual.Hours, Is.EqualTo(48));
        Assert.That(a.Actual.Stale, Is.EqualTo(1)); Assert.That(a.Headroom, Is.Null);
        Assert.That(Json(w.Snapshot()), Is.EqualTo(before));
    }

    [Test]
    public async Task VerifiedCreationPromotesBaselineIdentityWithoutRecaptureOrLosingReports()
    {
        var h = await CreationHarness.Create(2, planning: true); var id = h.Add("same title"); var p = h.Workspace.Selected!; var w = h.Session.Workspace;
        w.CommitPlanning(p, PlanningPathTests.Plan() with { Tasks = [new(id, PlanningMode.Manual, "U1",
            PlanningContractTests.At("2026-10-05 12:07"), PlanningContractTests.At("2026-10-05 16:19"), Actuals: [new("U1", 2, Day)])] }, w.Revision,
            [new(id, "Estimate", "8"), new(id, "Remaining", "5")]);
        w.SetAllowance(p, "U1", 80, w.Revision); w.CaptureBaseline(p, w.Revision, null, DateTimeOffset.UtcNow);
        var baseline = w.Planning("P1")!.Summary!.Baseline!; var captured = baseline.Tasks.Single(t => t.TaskId == id);
        await h.Apply(id); await h.Restart(); w = h.Session.Workspace;
        var created = w.Creations.Single(c => c.LocalId == id); Assert.That(created.Completed, Is.True);
        var next = w.Planning("P1")!.Summary!.Baseline!;
        Assert.That(next.Id, Is.EqualTo(baseline.Id)); Assert.That(next.CapturedAt, Is.EqualTo(baseline.CapturedAt));
        Assert.That(next.Tasks.Single(t => t.TaskId == created.Verified!.Id), Is.EqualTo(captured with { TaskId = created.Verified!.Id, RowId = created.ItemId! }));
        Assert.That(w.Planning("P1")!.Tasks.Single(t => t.Id == created.Verified!.Id).Actuals!.Single().Hours, Is.EqualTo(2));
    }
    [Test]
    public void MissingRemoteScopeAndExplicitLocalRemovalKeepDifferentBaselineStates()
    {
        var (p, w) = Example(); var local = w.AppendRows(p with { DefaultRepository = "owner/repo" }, "new\tTodo\t8").Single();
        w.CaptureBaseline(p, w.Revision, null, DateTimeOffset.UtcNow);
        w.RemoveRows("P1", [w.Open(p).Single(r => r.ItemId == local)]);
        var missing = p with { Snapshot = p.Snapshot with { Items = p.Snapshot.Items.Skip(1).ToArray(),
            Issues = p.Snapshot.Issues.Where(i => i.Key.NodeId != "I1").ToDictionary() } };
        w.SetRegistrations([missing]);
        var result = SummaryProjection.Create(w, missing, Day);
        Assert.That(result.Comparisons.Single(c => c.TaskId == local).State, Is.EqualTo("ローカル範囲から削除"));
        Assert.That(result.Comparisons.Single(c => c.TaskId == "I1").State, Is.EqualTo("現在の範囲を未確認"));
        Assert.That(result.People.Single(p => p.Id == "A").Actual.Hours, Is.EqualTo(48));
        Assert.That(result.People.Single(p => p.Id == "A").Forecast.Complete, Is.False);
    }
}
