using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PmoPlanningTests
{
    private static (ProjectRegistration Project, EditingWorkspace Work) DependentProject()
    {
        var p = PlanningPathTests.Registration();
        p = p with { Snapshot = p.Snapshot with {
            Fields = p.Snapshot.Fields.Select(f => f.Id.NodeId == "P1-status" ? f with { Name = "Status" } : f).ToArray(),
            Issues = p.Snapshot.Issues.ToDictionary(e => e.Key, e => e.Key.NodeId == "I2"
                ? e.Value with { Native = e.Value.Native! with { Predecessors = [new(p.Snapshot.Id.Scope, "I1")] } } : e.Value) } };
        var work = new EditingWorkspace(p.Snapshot.Id.Scope); work.SetRegistrations([p]);
        work.CommitPlanning(p, PlanningPathTests.Plan() with { Tasks = [new("I1"), new("I2", PlanningMode.Auto, "U1")] }, work.Revision,
            [new("P1T2", "Estimate", "1")]);
        return (p, work);
    }

    [Test]
    public void DoneStatusReplansSuccessorAndUndoRestoresTheConstraintWithoutChangingActuals()
    {
        var (p, work) = DependentProject();
        Assert.That(work.PlanFor(p).Tasks.Single(t => t.Id == "I2").Start, Is.Null);
        work.Commit("P1", work.Open(p)[0].Cells[1], "Done");
        Assert.That(work.PlanFor(p).Tasks.Single(t => t.Id == "I2").Start, Is.EqualTo(PlanningPathTests.Plan().Start));
        Assert.That(work.Planning("P1")!.Tasks.Single(t => t.Id == "I1").ActualFinish, Is.Null);
        Assert.That(work.Planning("P1")!.Tasks.Single(t => t.Id == "I1").Progress, Is.EqualTo(PlanningProgress.Unstarted));
        work.Undo("P1");
        Assert.That(work.PlanFor(p).Tasks.Single(t => t.Id == "I2").Start, Is.Null);
        Assert.That(work.Journal, Is.Empty);
    }

    [Test]
    public void DependencyProblemNamesTheCurrentIssueTitleInsteadOfItsInternalIdentifier()
    {
        var (p, work) = DependentProject();
        work.Commit("P1", work.Open(p)[0].Cells[0], "設計レビュー");
        var problem = work.PlanFor(p).Tasks.Single(t => t.Id == "I2").Problem;
        Assert.That(problem, Does.Contain("#1 設計レビュー").And.Not.Contain("I1"));
    }

    [Test]
    public void ExplicitReopenedProgressStillBlocksADonePredecessorWithoutRemainingWork()
    {
        var (p, work) = DependentProject();
        work.Commit("P1", work.Open(p)[0].Cells[1], "Done");
        var plan = work.Planning("P1")!;
        work.CommitPlanning(p, plan with { Tasks = plan.Tasks.Select(t => t.Id == "I1"
            ? t with { Progress = PlanningProgress.Reopened } : t).ToArray() }, work.Revision);
        Assert.That(work.PlanFor(p).Tasks.Single(t => t.Id == "I2").Start, Is.Null);
    }

    [Test]
    public void WeeklySummaryUsesTheSelectedReportingDayIndependentlyOfTheReplanningCutoff()
    {
        var (p, work) = SummaryTests.Example();
        work.SetAllowance(p, "A", 160, work.Revision);
        var summary = SummaryProjection.Create(work, p, SummaryTests.Day.AddDays(3), SummaryTests.Day);
        Assert.That(summary.People.Single(p => p.Id == "A").Headroom, Is.EqualTo(40));
        Assert.That(summary.Cutoff, Is.EqualTo(SummaryTests.Day));
        Assert.That(work.Planning("P1")!.Cutoff, Is.EqualTo(SummaryTests.Day.ToDateTime(new(18, 0))));
    }
}
