using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.App.GitHub;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;
[TestFixture, Category("Integration")]
internal sealed class EvaluationFixtureTests
{
    private static void Report(EvaluationFixture.StatusMeasures measures)
    {
        var total = measures.Total;
        TestContext.Out.WriteLine($"{measures.StatusDate}: 見積 {total.Estimate} 実績 {total.Actual} 残 {total.Remaining} 見込 {total.Forecast} 差異 {total.Variance:+0;-0;0} 進捗 {total.Progress}% 完了 {total.Closed}/{total.Tasks} 見積超過 {total.Overruns} 残0未完了 {total.OpenAtZero} 完了見込 {measures.Forecast}");
        foreach (var phase in measures.Phases)
            TestContext.Out.WriteLine($"  {phase.Phase}: {phase.Start}–{phase.Forecast} 目標 {phase.Due} 遅れ {phase.LateWorkingDays}日 進捗 {phase.Effort.Progress}% 差異 {phase.Effort.Variance:+0;-0;0} 完了 {phase.Effort.Closed}/{phase.Effort.Tasks}");
        TestContext.Out.WriteLine("  overloaded days in the next 20: " + string.Join(", ", measures.People.Where(p => p.OverloadedDaysNext20 > 0).Select(p => $"{p.Person} {p.OverloadedDaysNext20}d peak {p.PeakPercentNext20}%")));
    }

    [TestCase("2027.04"), TestCase("2027.10")]
    public void BaselineRunsPhasesAsWindowsThatMeetTheirMilestones(string version)
    {
        var plan = EvaluationFixture.ReadPlan(version);
        var baseline = EvaluationFixture.Measures(plan, EvaluationFixture.Simulate(plan, [plan.ProjectStart]).Single());
        Report(baseline);
        Assert.That(baseline.Phases.Where(p => p.LateWorkingDays > 0).Select(p => p.Phase), Is.Empty, "The plan meets every milestone when the project starts.");
        Assert.That(baseline.Forecast, Is.LessThanOrEqualTo(plan.ProjectEnd));
        var gates = plan.Rows.Where(r => r.Start is not null && r.Phase is "IT" or "ST" or "OT").GroupBy(r => r.Phase!).ToDictionary(g => g.Key, g => g.Select(r => r.Start!.Value).Distinct().Single());
        foreach (var (phase, gate) in gates)
            Assert.That(baseline.Phases.Single(p => p.Phase == phase).Start, Is.EqualTo(gate), $"{phase} starts across the version on one date.");
        var order = baseline.Phases.ToArray();
        for (var n = 1; n < order.Length; n++)
            Assert.That(order[n].Start, Is.GreaterThanOrEqualTo(order[n - 1].Start!.Value), $"{order[n].Phase} does not start before {order[n - 1].Phase}.");
    }

    [TestCase("2027.04"), TestCase("2027.10")]
    public void MidProjectStatusShowsTheTeamsDeviations(string version)
    {
        var plan = EvaluationFixture.ReadPlan(version);
        var snapshots = EvaluationFixture.Simulate(plan, plan.StatusDates);
        using (Assert.EnterMultipleScope())
        foreach (var snapshot in snapshots)
        {
            var recorded = EvaluationFixture.Measures(plan, snapshot);
            Report(recorded);
            Assert.That(recorded.People.Select(p => p.WorkingDays), Is.All.EqualTo(20), $"{snapshot.StatusDate}: every person has 20 working days measured.");
            var byId = snapshot.Rows.ToDictionary(r => r.Identity);
            foreach (var task in snapshot.Rows.Where(r => r.Actual > 0))
            foreach (var predecessor in task.Predecessors.Select(id => byId[id]))
            {
                if (predecessor.Closed && predecessor.CloseDate is { } end)
                    Assert.That(task.Start, Is.GreaterThanOrEqualTo(end), $"{snapshot.StatusDate}: {task.Title} starts after {predecessor.Title} finishes.");
                Assert.That(!predecessor.Closed && predecessor.Remaining > 0, Is.False, $"{snapshot.StatusDate}: {task.Title} has no unfinished predecessor {predecessor.Title}.");
            }
        }
        var middle = snapshots.Single(s => s.StatusDate == plan.StatusDate);
        var measures = EvaluationFixture.Measures(plan, middle);
        var tasks = middle.Rows.Where(r => r.Parent is not null).ToArray();
        Assert.That(measures.Phases.Where(p => p.LateWorkingDays > 0).Select(p => p.Phase), Does.Contain("PS"), "Re-estimated detailed design finishes after its milestone.");
        Assert.That(measures.Phases.Single(p => p.Phase == "PS").Effort.Variance, Is.GreaterThan(0), "見込 exceeds 見積 where 残 was raised.");
        Assert.That(measures.Total.Overruns, Is.GreaterThan(0), "Open tasks re-estimated above 見積 are still being worked on.");
        Assert.That(tasks.Count(t => !t.Closed && t.Remaining == 0), Is.EqualTo(1), "One open task has 残 0.");
        Assert.That(tasks.Single(t => !t.Closed && t.Remaining == 0).End, Is.EqualTo(middle.StatusDate));
        Assert.That(tasks.Count(t => t.Closed && t.Actual < t.Estimate), Is.GreaterThan(0), "Some tasks finish early.");
        Assert.That(tasks.Where(t => t.Closed).All(t => t.Remaining == 0 && t.End == t.CloseDate && t.End < middle.StatusDate && t.Start <= t.End), Is.True, "A closed task ends on its close date before the status date.");
        Assert.That(tasks.Where(t => !t.Closed && t.Actual > 0 && t.Remaining > 0).All(t => t.Start < middle.StatusDate), Is.True);
        Assert.That(measures.Total.Closed, Is.InRange(250, 750), "The status date is in the middle of the project.");
        Assert.That(measures.People.Single(p => p.Person == "U7").OverloadedDaysNext20, Is.GreaterThan(0), "U7 carries shared parts on top of their own work.");
        Assert.That(measures.People.Count(p => p.OverloadedDaysNext20 > 0), Is.LessThanOrEqualTo(3), "Overload is an exception, not the plan.");
    }

    [TestCase("2027.04"), TestCase("2027.10")]
    public async Task FreshEvaluationOpensAndRefreshesInTheMiddleOfTheProjectWithNoUnpublishedTasks(string version)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-evaluation-" + Guid.NewGuid().ToString("N"));
        try
        {
            await EvaluationFixture.Create(root, version);
            var plan = EvaluationFixture.ReadPlan(version);
            var workspace = new PlanWorkspace(new PlanStore(root));
            var runner = new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = Path.Combine(root, "fake-gh") });
            await workspace.Connect(new GhConnectionService(GhProcessTests.FakeExecutable, "github.com", runner));
            Assert.That(workspace.Available.First().Title, Is.EqualTo(plan.Title));
            await workspace.Open(workspace.Available.First());
            var session = workspace.Session!;
            Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(plan.StatusDate));
            Assert.That(session.Document.State.Rows.Length, Is.EqualTo(1040), "40 requirement Issues are additional to the 1,000 executable tasks.");
            Assert.That(session.Document.State.Settings.People.Length, Is.EqualTo(20));
            Assert.That(session.Changes(plan.StatusDate).TaskCount, Is.Zero);
            var publisher = new PlanPublisher(workspace.Service!, workspace.Context!);
            Assert.That((await publisher.RefreshAsync(session, plan.StatusDate)).Succeeded, Is.True);
            Assert.That(session.Changes(plan.StatusDate).TaskCount, Is.Zero);
            Assert.That(session.Document.Sync.NativeOrders["I1"], Is.EqualTo(session.Document.State.Rows.Where(r => r.Parent == "I1").Select(r => r.Identity)));
            var rows = session.Document.State.Rows;
            Assert.That(rows.Count(r => r.Closed), Is.GreaterThan(0));
            Assert.That(rows.Where(r => r.Closed).All(r => r.CloseDate == r.End), Is.True, "Reader preserves replayed close dates.");
            Assert.That(rows.Where(r => r.Parent is not null).All(r => r.Assignees.Length == 1 && r.Estimate > 0), Is.True);
            var fake = FakePlanEditor.Load(Path.Combine(root, "fake-gh"));
            Assert.That(fake.Issues.Where(i => i.Row.Parent is not null).All(i => plan.Phases.Contains(i.Phase)), Is.True, "Every task has a 工程 value.");
            await workspace.Flush();
            Assert.ThrowsAsync<IOException>(async () => await EvaluationFixture.Create(root, version));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
