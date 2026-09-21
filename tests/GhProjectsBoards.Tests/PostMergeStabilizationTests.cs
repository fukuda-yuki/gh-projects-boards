using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PostMergeStabilizationTests
{
    private static string Json(object? value) => JsonSerializer.Serialize(value);

    [Test]
    public void CanonicalSummaryRetainsProjectTaskOrder()
    {
        var (project, work) = GanttWorkload.Create(12);
        var summary = SummaryProjection.Create(work, project, SummaryTests.Day);
        Assert.That(summary.Comparisons.Select(c => c.TaskId), Is.EqualTo(Enumerable.Range(1, 12).Select(i => "I" + i)));
    }

    [Test]
    public void EquivalentCommittedAppearancesKeepPendingTextOutOfTheAdoptedCalculation()
    {
        var (project, work) = GanttWorkload.Create(3);
        var first = project.Snapshot.Items[0]; var cells = work.ReadRows(project)[0].Cells;
        var duplicate = first with { Id = new(work.Scope, "duplicate"), Values = first.Values.Select(v =>
            v.FieldId is not null && cells.SingleOrDefault(c => c.Key?.FieldId == v.FieldId.NodeId) is { } cell
                ? v with { Scalar = work.Value(cell), Availability = work.Value(cell) is null ? ValueAvailability.Empty : ValueAvailability.Present } : v).ToArray() };
        project = project with { Snapshot = project.Snapshot with { Items = project.Snapshot.Items.Append(duplicate).ToArray() } };
        work.SetRegistrations([project]); var before = work.PlanFor(project).Tasks.Single(t => t.Id == "I1");
        var effort = work.Open(project)[0].Cells.Single(c => c.Key?.FieldId == "F-Estimate");
        work.SetBuffer(effort, "999未確定"); var snapshot = Json(work.Snapshot());
        var projection = GanttProjection.Create(work, project, []);
        Assert.That(projection.Rows.Where(r => r.TaskId == "I1").Select(r => r.Input!.SourceProblem), Is.All.Null);
        Assert.That(work.PlanFor(project).Tasks.Single(t => t.Id == "I1"), Is.EqualTo(before));
        Assert.That(work.Buffer(effort), Is.EqualTo("999未確定"));
        Assert.That(Json(work.Snapshot()), Is.EqualTo(snapshot));
    }

    [Test]
    public async Task RejectedFirstCheckpointWithLockedCandidateRemainsRecoverableAndCanRetry()
    {
        var (_, work) = SummaryTests.Example();
        var root = Path.Combine(Path.GetTempPath(), "ghpb-rejected-lock-" + Guid.NewGuid().ToString("N"));
        var store = new DraftStore(root); var file = store.FileFor(work.Scope);
        FileStream? held = null;
        try
        {
            Assert.ThrowsAsync<InvalidDataException>(async () => await store.SaveAsync(work.Snapshot(), 0, () => {
                held = new FileStream(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp").Single(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return false;
            }));
        }
        finally { held?.Dispose(); }
        var rejected = Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp").Single();
        var original = await File.ReadAllBytesAsync(rejected);
        Assert.That((await new DraftStore(root).CheckpointsAsync()).Problems.Single().Kind, Is.EqualTo("InterruptedCheckpoint"));
        Assert.ThrowsAsync<InvalidDataException>(async () => await new DraftStore(root).SaveAsync(work.Snapshot(), 0), "An unrelated writer cannot bypass an unknown orphan.");
        await store.SaveAsync(work.Snapshot(), 0);
        Assert.That(Json(await store.LoadAsync(work.Scope)), Is.EqualTo(Json(work.Snapshot())));
        Assert.That(await File.ReadAllBytesAsync(rejected), Is.EqualTo(original));
    }

    [Test]
    public async Task IdentityBindingCannotDiscardAnExistingDependencyFieldIntent()
    {
        var harness = await CreationHarness.Create(2, planning: true); var local = harness.Add();
        var project = harness.Workspace.Selected!; var work = harness.Session.Workspace;
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = [new(local), new("I2", LocalLinks: [new(local)])] }, work.Revision);
        harness.LoseCreate = true; await harness.Apply(local);
        work = harness.Session.Workspace;
        var key = new FieldKey("Dependency", "I2", "P1", "I1");
        work = EditingWorkspace.Restore(work.Snapshot() with { Fields = work.Snapshot().Fields.Append(
            new DraftField(key, "present", project.Snapshot.Id, project.RetrievedAt, new(null, true), null, work.Revision)).ToArray() });
        var creation = work.Creations.Single(); var batch = work.Journal.Single(); var before = Json(work.Snapshot());
        var issue = new CreatedIssue("I1", creation.Repository.Id, 1, "https://github.com/sample-user/first/issues/1", "Issue 1", DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => work.BindCreation(batch.Id, creation.Id, issue, work.Revision));
        Assert.That(Json(work.Snapshot()), Is.EqualTo(before));
    }

    [Test]
    public async Task RejectedFirstCheckpointRetainsItsBytesAndAllowsFreshRetry()
    {
        var (_, work) = SummaryTests.Example();
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-post68-save-" + Guid.NewGuid().ToString("N")));
        var file = store.FileFor(work.Scope);
        Assert.ThrowsAsync<InvalidDataException>(async () => await store.SaveAsync(work.Snapshot(), 0, () => false));
        Assert.That(File.Exists(file), Is.False);
        var rejected = Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp*").Single();
        var bytes = await File.ReadAllBytesAsync(rejected);
        await store.SaveAsync(work.Snapshot(), 0);
        Assert.That(Json(await store.LoadAsync(work.Scope)), Is.EqualTo(Json(work.Snapshot())));
        Assert.That(await File.ReadAllBytesAsync(rejected), Is.EqualTo(bytes), "Rejected candidate remains inspectable.");
    }

    [Test]
    public void RollupKeepsRawContributionWhileBothProjectAndPersonTotalsExcludeIt()
    {
        var (project, work) = SummaryTests.Example(); var plan = work.Planning("P1")!;
        work.CommitPlanning(project, plan with { Tasks = [plan.Tasks[0] with { LaborKind = TaskLaborKind.Rollup }, plan.Tasks[1]] }, work.Revision);
        var result = SummaryProjection.Create(work, project, SummaryTests.Day);
        var detail = result.Contributions.Single(c => c.TaskId == "I1");
        Assert.That(new[] { detail.Estimate.Hours, detail.Actual.Hours, detail.Remaining.Hours }, Is.EqualTo(new[] { 144m, 48m, 72m }));
        Assert.That(result.Estimate.Hours, Is.EqualTo(64));
        Assert.That(result.Actual.Hours, Is.EqualTo(56));
        Assert.That(result.People.Single(p => p.Id == "A").Forecast.Hours, Is.Zero);
    }

    [Test]
    public void ConflictingCanonicalAppearancesAreIncompleteAndCannotReplaceBaseline()
    {
        var (project, work) = SummaryTests.Example();
        work.CaptureBaseline(project, work.Revision, null, DateTimeOffset.UtcNow);
        var baseline = work.Planning("P1")!.Summary!.Baseline!;
        // Same Issue, another Project item with empty observed labor versus the retained first item's edits.
        project = project with { Snapshot = project.Snapshot with { Items = project.Snapshot.Items.Append(
            project.Snapshot.Items[0] with { Id = new(work.Scope, "duplicate") }).ToArray() } };
        work.SetRegistrations([project]); var before = Json(work.Snapshot());
        var result = SummaryProjection.Create(work, project, SummaryTests.Day);
        Assert.That(result.Estimate.Complete, Is.False);
        Assert.That(result.Contributions.Single(c => c.TaskId == "I1").Problem, Does.Contain("重複"));
        Assert.Throws<InvalidOperationException>(() => work.CaptureBaseline(project, work.Revision, baseline.Id, DateTimeOffset.UtcNow));
        Assert.That(Json(work.Snapshot()), Is.EqualTo(before));
    }

    [Test]
    public void ColdCombinedProjectionsDoNotInitializeFieldsOrChangeCheckpoint()
    {
        var project = PlanningPathTests.Registration(); var work = new EditingWorkspace(project.Snapshot.Id.Scope);
        work.SetRegistrations([project]); work.SetPlanning(PlanningPathTests.Plan(), 0);
        Assert.That(work.Fields, Is.Empty);
        var before = Json(work.Snapshot());
        _ = SummaryProjection.Create(work, project, SummaryTests.Day);
        _ = GanttProjection.Create(work, project, []);
        _ = work.PlanFor(project);
        Assert.That(Json(work.Snapshot()), Is.EqualTo(before));
    }

    [TestCase("Estimate"), TestCase("Remaining"), TestCase("Actual"), TestCase("Start"), TestCase("Finish")]
    public void ConflictingAppearancesNeverFanOutGeneratedDatesOrActual(string role)
    {
        var (project, work) = SummaryTests.Example();
        var first = project.Snapshot.Items[0];
        var cells = work.ReadRows(project)[0].Cells;
        var values = first.Values.Select(v => v.FieldId is not null && cells.SingleOrDefault(c => c.Key?.FieldId == v.FieldId.NodeId) is { } cell
            ? v with { Scalar = work.Value(cell), Availability = work.Value(cell) is null ? ValueAvailability.Empty : ValueAvailability.Present } : v).ToArray();
        var duplicate = first with { Id = new(work.Scope, "duplicate"), Values = values.Select(v => v.FieldId?.NodeId == "F-" + role
            ? v with { Scalar = role is "Start" or "Finish" ? "2026-10-07" : "999", Availability = ValueAvailability.Present } : v).ToArray() };
        project = project with { Snapshot = project.Snapshot with { Items = project.Snapshot.Items.Append(duplicate).ToArray() } };
        work.SetRegistrations([project]); work.Open(project);
        var originals = Json(work.Fields.Where(f => f.Key.Kind == "Date" || f.Key.FieldId == "F-Actual").ToArray());
        var plan = work.Planning("P1")!;
        work.CommitPlanning(project, plan with { Tasks = [plan.Tasks[0] with { Mode = PlanningMode.Manual,
            ManualStart = PlanningContractTests.At("2026-10-05 10:07"), ManualFinish = PlanningContractTests.At("2026-10-05 16:19") }, plan.Tasks[1]] }, work.Revision);
        Assert.That(Json(work.Fields.Where(f => f.Key.Kind == "Date" || f.Key.FieldId == "F-Actual").ToArray()), Is.EqualTo(originals));
        Assert.That(work.PlanFor(project).Inputs!.Single(i => i.Task.Id == "I1").SourceProblem, Does.Contain("重複"));
    }

    [Test]
    public async Task UncertainCreationCannotBindOverAnExistingProtectedBaselineIdentity()
    {
        var harness = await CreationHarness.Create(2, planning: true); var local = harness.Add();
        var project = harness.Workspace.Selected!; var work = harness.Session.Workspace;
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = [new(local, PlanningMode.Manual)] }, work.Revision);
        work.CaptureBaseline(project, work.Revision, null, DateTimeOffset.UtcNow);
        harness.LoseCreate = true; await harness.Apply(local);
        work = harness.Session.Workspace; var creation = work.Creations.Single(); var batch = work.Journal.Single();
        var before = Json(work.Snapshot());
        var issue = new CreatedIssue("I1", creation.Repository.Id, 1, "https://github.com/sample-user/first/issues/1", "Issue 1", DateTimeOffset.UtcNow);
        Assert.That(work.Planning("P1")!.Tasks.Any(t => t.Id == "I1"), Is.False, "Only the protected baseline owns the target.");
        Assert.Throws<InvalidOperationException>(() => work.BindCreation(batch.Id, creation.Id, issue, work.Revision));
        Assert.That(Json(work.Snapshot()), Is.EqualTo(before));
    }

    [Test]
    public void ReplacingRegistrationInvalidatesPreviouslyCalculatedCanonicalValues()
    {
        var (project, work) = SummaryTests.Example();
        Assert.That(work.PlanFor(project).Inputs!.Single(i => i.Task.Id == "I1").Estimate, Is.EqualTo(144));
        project = project with { Snapshot = project.Snapshot with { Items = project.Snapshot.Items.Append(
            project.Snapshot.Items[0] with { Id = new(work.Scope, "duplicate") }).ToArray() } };
        work.SetRegistrations([project]);
        Assert.That(work.PlanFor(project).Inputs!.Single(i => i.Task.Id == "I1").SourceProblem, Does.Contain("重複"));
    }

    [Test]
    public async Task VerifiedCreationKeepsLocalAndProtectedIdentityWhenPromotionCollides()
    {
        var harness = await CreationHarness.Create(2, planning: true); var local = harness.Add();
        var project = harness.Workspace.Selected!; var work = harness.Session.Workspace;
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = [new(local, PlanningMode.Manual)] }, work.Revision);
        work.CaptureBaseline(project, work.Revision, null, DateTimeOffset.UtcNow);
        harness.LoseCreate = true; await harness.Apply(local);
        work = harness.Session.Workspace; var c = work.Creations.Single(); var batch = work.Journal.Single();
        var item = project.Snapshot.Items.Single(i => i.ContentId?.NodeId == "I1");
        var verified = c with { Verified = new("I1", c.Repository.Id, 1, "https://github.com/sample-user/first/issues/1", "Issue 1", DateTimeOffset.UtcNow),
            Completed = true, ItemId = item.Id.NodeId, Fields = [] };
        work.RecordCreation(batch.Id, verified);
        var baseline = Json(work.Planning("P1")!.Summary);
        var localTask = Json(work.Planning("P1")!.Tasks);
        work.Reconcile(project, project with { RetrievedAt = project.RetrievedAt.AddSeconds(1) });
        Assert.That(Json(work.Planning("P1")!.Summary), Is.EqualTo(baseline));
        Assert.That(Json(work.Planning("P1")!.Tasks), Is.EqualTo(localTask));
        Assert.That(work.LocalRows.Any(r => r.Id == local), Is.True);
        Assert.That(work.Creations.Single(), Is.EqualTo(verified));
        Assert.DoesNotThrow(() => DraftStore.Validate(work.Snapshot()));
    }

    [Test]
    public async Task IdentityBindingCannotCollapseDistinctRetainedDependencyIntents()
    {
        var harness = await CreationHarness.Create(2, planning: true); var local = harness.Add();
        var project = harness.Workspace.Selected!; var work = harness.Session.Workspace;
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = [new(local, PlanningMode.Manual),
            new("I2", PlanningMode.Manual, LocalLinks: [new(local), new("I1", ExternalFinish: PlanningContractTests.At("2026-10-06 18:00"))])] }, work.Revision);
        harness.LoseCreate = true; await harness.Apply(local);
        work = harness.Session.Workspace; var c = work.Creations.Single(); var batch = work.Journal.Single(); var before = Json(work.Snapshot());
        var issue = new CreatedIssue("I1", c.Repository.Id, 1, "https://github.com/sample-user/first/issues/1", "Issue 1", DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => work.BindCreation(batch.Id, c.Id, issue, work.Revision));
        Assert.That(Json(work.Snapshot()), Is.EqualTo(before));
    }
}
