using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class WeeklyApplyRecoveryTests
{
    [Test]
    public async Task HistoricalWeeklyContinuationKeepsItsFieldScopeAndRetainsOtherDraftsAfterReload()
    {
        var h = await ApplyTests.Harness.Create(1, planning: true);
        Assert.That(await h.Workspace.PrepareLocalRowsAsync(), Is.True);
        var work = h.Workspace.Drafts!.Workspace; var project = h.Workspace.Selected!;
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = [new("I1", PlanningMode.Auto, "U1",
            Actuals: [new("U1", 7, new(2026, 10, 13))])] }, work.Revision,
            [new("P1-T1", "Estimate", "16"), new("P1-T1", "Remaining", "4")]);
        work.Commit("P1", work.Open(project).Single().Cells[0], "Unpublished title");
        Assert.That(await h.Workspace.Drafts.FlushAsync(), Is.True);
        h.MutationResult = (_, _) => new GhProcessResult(ProcessCompletion.TimedOut, true, null);
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" }, weeklyEffort: true);
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        var source = h.Workspace.Drafts.Workspace.Journal.Single();
        var uncertain = source.Operations.First(ApplyJournal.HasUnresolvedDispatch);
        await h.Workspace.SupersedeApplyAsync(source.Id);
        h.MutationResult = null;
        await h.Workspace.PrepareHistoricalFieldAsync(new(source.Id, null, uncertain.Id));
        var decision = await h.Workspace.ConfirmHistoricalFieldAsync(h.Workspace.HistoricalFieldReview!, HistoricalFieldDecisionKind.ContinueChange);
        Assert.That(decision.AllLocalWorkSaved, Is.True, decision.Problem);
        var priorWrites = h.Writes.Select(write => write.GetRawText()).ToArray();

        await h.Workspace.PrepareHistoricalFollowUpAsync(decision.Decision!.Id, new HashSet<string> { "P1-T1" });

        var review = h.Workspace.ApplyReview!;
        Assert.That(h.Workspace.ApplyBlockReason(review), Is.Null);
        Assert.That(review.Batch.WeeklyEffort, Is.True);
        Assert.That(review.Batch.Operations.Select(operation => operation.Key.FieldId), Is.EquivalentTo(new[] { "F-Actual", "F-Remaining" }));
        Assert.That(h.Writes.Select(write => write.GetRawText()), Is.EqualTo(priorWrites));
        await h.Workspace.ConfirmApplyAsync(review);

        var observed = await new ProjectReader(h.Service).ReadAsync(h.Context, project.Snapshot.Id, default);
        Assert.That(observed.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
        var row = observed.Project!.Items.Single();
        Assert.That(observed.Project.Issues[row.ContentId!].Title.Value, Is.EqualTo("Issue 1"));
        Assert.That(row.Values.Single(value => value.FieldId?.NodeId == "F-Actual").Scalar, Is.EqualTo("7"));
        Assert.That(row.Values.Single(value => value.FieldId?.NodeId == "F-Remaining").Scalar, Is.EqualTo("4"));
        Assert.That(h.Writes.Skip(priorWrites.Length).Select(write => write.GetProperty("fieldId").GetString()),
            Is.EquivalentTo(new[] { "F-Actual", "F-Remaining" }));
        var retained = EditingWorkspace.Restore((await new DraftStore(h.Root).LoadAsync(project.Snapshot.Id.Scope))!);
        Assert.That(retained.Journal.Last().WeeklyEffort, Is.True);
        Assert.That(retained.Journal.Last().Operations.Select(operation => operation.State), Is.All.EqualTo(ApplyState.Succeeded));
        Assert.That(retained.Fields.Where(field => field.Change is not null).Select(field => field.Key.Kind == "Title" ? "Title" : field.Key.FieldId),
            Is.EquivalentTo(new[] { "Title", "F-Estimate", "F-Start", "F-Finish" }));
        Assert.That(retained.HistoricalDispositions.Single().FollowUp?.BatchId, Is.EqualTo(review.Batch.Id));
    }
}
