using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class WeeklyApplyTests
{
    private static (ProjectRegistration Project, EditingWorkspace Work) ChangedWork()
    {
        var project = PlanningPathTests.Registration(2);
        var work = new EditingWorkspace(project.Snapshot.Id.Scope);
        work.SetRegistrations([project]);
        work.SetPlanning(PlanningPathTests.Plan(), 0);
        var row = work.Open(project)[0];
        work.Commit("P1", row.Cells[0], "Unpublished title");
        work.Commit("P1", row.Cells.Single(c => c.Key?.FieldId == "F-Estimate"), "24");
        work.Commit("P1", row.Cells.Single(c => c.Key?.FieldId == "F-Remaining"), "8");
        work.CommitActualInput(project, row.ItemId, "7", new(2026, 10, 5), "U1", work.Revision);
        return (project, work);
    }

    [Test]
    public void WeeklyReviewExcludesOtherChangedFieldsAndDoesNotAlterTheDraft()
    {
        var (project, work) = ChangedWork();
        var before = JsonSerializer.Serialize(work.Snapshot());
        var review = work.ReviewApply(project, new HashSet<string> { "P1T1" }, weeklyEffort: true);
        Assert.That(review.Blocked, Is.Empty);
        Assert.That(review.Batch.Operations.Select(o => o.Key.FieldId), Is.EquivalentTo(new[] { "F-Actual", "F-Remaining" }));
        Assert.That(review.Batch.Operations.All(o => o.ItemId == "P1T1" && o.Key.Kind == "Number"), Is.True);
        Assert.That(review.Batch.Creations, Is.Empty);
        Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
        var ordinary = work.ReviewApply(project, new HashSet<string> { "P1T1" });
        Assert.That(ordinary.Batch.Operations.Any(o => o.Key.Kind == "Title"), Is.True);
        Assert.That(ordinary.Batch.Operations.Any(o => o.Key.FieldId == "F-Estimate"), Is.True);
    }

    [Test]
    public void WeeklyReviewCannotSelectNewIssueCreation()
    {
        var (project, work) = ChangedWork();
        var local = work.AddRow(project);
        var review = work.ReviewApply(project, new HashSet<string> { "P1T1", local }, weeklyEffort: true);
        Assert.That(review.Batch.Creations, Is.Empty);
        Assert.That(review.Problems.Any(p => p.RowId == local), Is.True);
        Assert.That(review.Batch.Operations.All(o => o.ItemId == "P1T1"), Is.True);
    }

    [Test]
    public void OutsideActualChangeRetainsReportReconciliationInWeeklyCandidatesWithoutLocalScalarChange()
    {
        var project = ManualPlanningTests.WithScalar(PlanningPathTests.Registration(), "P1T1", "Actual", "10");
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        work.CommitPlanning(project, PlanningPathTests.Plan(), work.Revision);
        var remote = ManualPlanningTests.WithScalar(project, "P1T1", "Actual", "12");
        work.Reconcile(project, remote); work.SetRegistrations([remote]);
        var before = JsonSerializer.Serialize(work.Snapshot());

        var candidates = work.WeeklyApplyCandidates(remote);

        Assert.That(candidates.Select(candidate => candidate.Id), Is.EqualTo(new[] { "P1T1" }));
        var field = candidates.Single().Fields.Single(field => field.Key.FieldId == "F-Actual");
        Assert.That(field.Key.FieldId, Is.EqualTo("F-Actual"));
        Assert.That(field.Change, Is.Null); Assert.That(field.Buffer, Is.Null); Assert.That(field.Conflict, Is.False);
        Assert.That(field.Observation?.Reason, Is.EqualTo(EditingWorkspace.ProjectionDecisionReason));
        Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
    }

    [TestCase(true), TestCase(false)]
    public void WeeklyReviewUsesMappedIdentityAndRetainsUnavailableSelectedFieldsAsProblems(bool removed)
    {
        var (project, work) = ChangedWork();
        var changed = project with { Snapshot = project.Snapshot with {
            Fields = project.Snapshot.Fields.Select(f => f.Id.NodeId == "F-Actual"
                ? f with { DataType = removed ? "TEXT" : f.DataType, Name = "Renamed actual" } : f).ToArray()
        } };
        work.Reconcile(project, changed); work.SetRegistrations([changed]);
        var review = work.ReviewApply(changed, new HashSet<string> { "P1T1" }, weeklyEffort: true);
        if (removed) Assert.That(review.Problems.Any(p => p.Field?.FieldId == "F-Actual"), Is.True);
        else Assert.That(review.Batch.Operations.Any(o => o.Key.FieldId == "F-Actual"), Is.True);
        Assert.That(review.Batch.Operations.All(o => o.Key.FieldId is "F-Actual" or "F-Remaining"), Is.True);
    }

    [Test]
    public async Task WeeklyPrepareAndConfirmPublishOnlyActualAndRemainingAndRetainOtherDraftsAfterReload()
    {
        var h = await ChangedRemoteWork();
        var project = h.Workspace.Selected!;

        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" }, weeklyEffort: true);
        Assert.That(h.Workspace.ApplyReview!.Blocked, Is.Empty);
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview);

        var observed = await new ProjectReader(h.Service).ReadAsync(h.Context, project.Snapshot.Id, default);
        Assert.That(observed.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
        var row = observed.Project!.Items.Single(item => item.Id.NodeId == "P1-T1");
        var values = row.Values.ToDictionary(value => value.FieldId!.NodeId);
        Assert.That(values["F-Actual"].Scalar, Is.EqualTo("7"));
        Assert.That(values["F-Remaining"].Scalar, Is.EqualTo("4"));
        Assert.That(new[] { "F-Estimate", "F-Start", "F-Finish" }.Select(id => values[id].Availability), Is.All.EqualTo(ValueAvailability.Empty));
        Assert.That(observed.Project.Issues[row.ContentId!].Title.Value, Is.EqualTo("Issue 1"));
        Assert.That(h.Writes.Select(write => write.GetProperty("fieldId").GetString()), Is.EquivalentTo(new[] { "F-Actual", "F-Remaining" }));
        Assert.That(h.Writes.All(write => write.GetProperty("projectId").GetString() == "P1" && write.GetProperty("itemId").GetString() == "P1-T1"), Is.True);
        var retained = EditingWorkspace.Restore((await new DraftStore(h.Root).LoadAsync(project.Snapshot.Id.Scope))!);
        Assert.That(retained.Journal.Single().WeeklyEffort, Is.True);
        Assert.That(retained.Journal.Single().Operations.Select(operation => operation.State), Is.All.EqualTo(ApplyState.Succeeded));
        Assert.That(retained.Fields.Where(field => field.Change is not null).Select(field => field.Key.Kind == "Title" ? "Title" : field.Key.FieldId),
            Is.EquivalentTo(new[] { "Title", "F-Estimate", "F-Start", "F-Finish" }));
        Assert.That(retained.Planning("P1")!.Tasks.Single().Actuals!.Single(), Is.EqualTo(new ActualContribution("U1", 7, new(2026, 10, 13))));
    }

    [TestCase("fields"), TestCase("issue")]
    public async Task WeeklyConfirmationRejectsFreshFieldOrIssueIdentityChangesWithoutDispatch(string changed)
    {
        var h = await ChangedRemoteWork();
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" }, weeklyEffort: true);
        var review = h.Workspace.ApplyReview!;
        Assert.That(review.Blocked, Is.Empty);
        h.Boundary.ChangeCombinedResponse = response => {
            if (changed == "issue") response["data"]!["item"]!["content"]!["id"] = "replacement-issue";
            else foreach (var field in response["data"]!["project"]!["fields"]!["nodes"]!.AsArray()
                .Where(field => field!["id"]!.GetValue<string>() is "F-Actual" or "F-Remaining"))
                field!["id"] = "replacement-" + field["id"]!.GetValue<string>();
        };

        await h.Workspace.ConfirmApplyAsync(review);

        Assert.That(h.Writes, Is.Empty);
        Assert.That(h.Scalars, Is.Empty);
        var retained = EditingWorkspace.Restore((await new DraftStore(h.Root).LoadAsync(review.Batch.Project.Scope))!);
        Assert.That(retained.Journal.Single().Operations.All(operation => operation.State != ApplyState.Succeeded && operation.Attempts.IsEmpty), Is.True);
        Assert.That(retained.Fields.Where(field => field.Key.FieldId is "F-Actual" or "F-Remaining").Select(field => field.Change?.Value),
            Does.Contain("7").And.Contain("4"));
    }

    private static async Task<ApplyTests.Harness> ChangedRemoteWork()
    {
        var h = await ApplyTests.Harness.Create(2, planning: true);
        Assert.That(await h.Workspace.PrepareLocalRowsAsync(), Is.True);
        var project = h.Workspace.Selected!; var work = h.Workspace.Drafts!.Workspace;
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = [new("I1", PlanningMode.Auto, "U1",
            Actuals: [new("U1", 7, new(2026, 10, 13))])] }, work.Revision,
            [new("P1-T1", "Estimate", "16"), new("P1-T1", "Remaining", "4")]);
        work.Commit("P1", work.Open(project)[0].Cells[0], "Unpublished title");
        Assert.That(await h.Workspace.Drafts.FlushAsync(), Is.True);
        return h;
    }
}
