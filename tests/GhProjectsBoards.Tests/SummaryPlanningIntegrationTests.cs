using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class SummaryPlanningIntegrationTests
{
    [TestCase(10), TestCase(11)]
    public async Task EitherBranchCheckpointMigratesWithoutLosingPendingWorkHistoryOrMetadata(int version)
    {
        var (project, work) = SummaryTests.Example();
        if (version == 10)
        {
            work.SetAllowance(project, "A", 0, work.Revision);
            work.CaptureBaseline(project, work.Revision, null, DateTimeOffset.UtcNow);
        }
        else work.CommitPlanning(project, EditingWorkspace.UpgradeAssignmentContract(work.Planning("P1")!), work.Revision);
        work.SetBuffer(work.Open(project)[0].Cells[0], "pending日本語");
        var expected = work.Snapshot();
        var source = JsonNode.Parse(Json(expected))!; source["Version"] = version;
        void OriginalPlan(JsonNode? plan)
        {
            if (plan is null) return;
            if (version == 11) plan.AsObject().Remove("Summary");
            foreach (var task in plan["Tasks"]!.AsArray()) task!.AsObject().Remove(version == 10 ? "Assignment" : "LaborKind");
        }
        foreach (var plan in source["Planning"]!.AsArray()) OriginalPlan(plan);
        foreach (var transaction in source["History"]!.AsArray())
            if (transaction?["Plan"] is { } change) { OriginalPlan(change["Before"]); OriginalPlan(change["After"]); }
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-summary-integration-" + Guid.NewGuid().ToString("N")));
        var file = store.FileFor(work.Scope); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var original = source.ToJsonString(); await File.WriteAllTextAsync(file, original);

        var restored = EditingWorkspace.Restore((await store.LoadAsync(work.Scope))!);
        Assert.That(Json(restored.Snapshot()), Is.EqualTo(Json(expected)));
        Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(original), "Reading cannot rewrite a legacy checkpoint.");
        await store.SaveAsync(restored.Snapshot(), expected.Revision);
        Assert.That(await File.ReadAllTextAsync(file + ".bak"), Is.EqualTo(original));
        Assert.That(Json(await store.LoadAsync(work.Scope)), Is.EqualTo(Json(expected)));
        restored.Undo("P1"); work.Undo("P1");
        Assert.That(Json(restored.Snapshot()), Is.EqualTo(Json(work.Snapshot())), "Migration retains the original operation-level Undo.");
    }

    [Test]
    public void SummaryAndContextualEditsRetainEachOthersMetadataAndUndoAsOneOperation()
    {
        var project = PlanningAssignmentTests.Assigned("U1");
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        work.SetPlanning(PlanningPathTests.Plan() with { Version = 3 }, 0);
        var estimate = work.Open(project)[0].Cells.Single(c => c.Key?.FieldId == "F-Estimate");
        work.Commit("P1", estimate, "8");
        work.SetAllowance(project, "U1", 40, work.Revision);
        work.CaptureBaseline(project, work.Revision, null, DateTimeOffset.UtcNow);
        var baseline = Json(work.Planning("P1")!.Summary);
        Assert.That(work.Planning("P1")!.Version, Is.EqualTo(4));
        var before = Json(work.Planning("P1")!.Tasks);

        work.CommitDateInput(project, "P1T1", "Start", "2026-10-05 10:17", work.Revision);
        Assert.That(work.Planning("P1")!.Tasks.Single(t => t.Id == "I1").ManualStart,
            Is.EqualTo(PlanningContractTests.At("2026-10-05 10:17")));
        Assert.That(Json(work.Planning("P1")!.Summary), Is.EqualTo(baseline));
        work.Undo("P1");
        Assert.That(Json(work.Planning("P1")!.Tasks), Is.EqualTo(before));

        work.CommitActualInput(project, "P1T1", "2", new(2026, 10, 5), "U1", work.Revision);
        Assert.That(work.Planning("P1")!.Tasks.Single(t => t.Id == "I1").Actuals,
            Is.EqualTo(new[] { new ActualContribution("U1", 2, new(2026, 10, 5)) }));
        Assert.That(Json(work.Planning("P1")!.Summary), Is.EqualTo(baseline));
        work.Undo("P1");
        Assert.That(Json(work.Planning("P1")!.Tasks), Is.EqualTo(before));

        work.Commit("P1", work.Open(project)[1].Cells.Single(c => c.Key?.FieldId == "F-Estimate"), "4");
        var next = work.Planning("P1")!.Tasks.Single(t => t.Id == "I2");
        Assert.That(next.Mode, Is.EqualTo(PlanningMode.Auto));
        Assert.That(next.OwnerId, Is.EqualTo("U1"));
        Assert.That(Json(work.Planning("P1")!.Summary), Is.EqualTo(baseline));
    }

    [Test]
    public void DetachedPlanningPreviewAcceptsUnchangedProtectedMetadataButRejectsReplacement()
    {
        var (project, work) = SummaryTests.Example();
        work.SetAllowance(project, "A", 40, work.Revision);
        work.CaptureBaseline(project, work.Revision, null, DateTimeOffset.UtcNow);
        var plan = EditingWorkspace.UpgradeAssignmentContract(work.Planning("P1")!);
        var preview = EditingWorkspace.Restore(work.Snapshot());
        preview.CommitPlanning(project, plan, preview.Revision);
        Assert.That(Json(preview.Planning("P1")!.Summary), Is.EqualTo(Json(work.Planning("P1")!.Summary)));
        var before = Json(preview.Snapshot());
        Assert.Throws<InvalidOperationException>(() => preview.CommitPlanning(project,
            plan with { Summary = plan.Summary! with { Baseline = plan.Summary.Baseline! with { Tasks = [] } } }, preview.Revision));
        Assert.That(Json(preview.Snapshot()), Is.EqualTo(before));
    }

    [Test]
    public void BackgroundSnapshotOwnsSummaryAndBaselineCollectionsIncludingUndoHistory()
    {
        var (project, work) = SummaryTests.Example();
        work.SetAllowance(project, "A", 40, work.Revision);
        work.CaptureBaseline(project, work.Revision, null, DateTimeOffset.UtcNow);
        var captured = work.Snapshot(); var expected = Json(captured);
        var other = work.Snapshot(); var summary = other.Planning![0].Summary!;
        summary.Allowances[0] = new("foreign", 99);
        summary.Baseline!.People[0] = new("foreign", "Foreign", 1);
        summary.Baseline.Tasks[0] = summary.Baseline.Tasks[0] with { Title = "foreign" };
        summary.Baseline.Calendar.Holidays.Dates[0] = summary.Baseline.Calendar.Holidays.Dates[0] with { Name = "foreign" };
        other.History.Last().Plan!.After.Summary!.Allowances[0] = new("foreign", 88);
        Assert.That(Json(captured), Is.EqualTo(expected));
        Assert.That(Json(work.Snapshot()), Is.EqualTo(expected));
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value);
}
