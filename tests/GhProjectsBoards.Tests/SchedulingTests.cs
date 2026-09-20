using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class SchedulingTests
{
    private static DateTime At(string text) => PlanningContractTests.At(text);
    private static ProjectPlanning Plan(decimal weight = 100) => PlanningPathTests.Plan() with { People = [new("U1", "Owner", weight)] };
    private static PlanningInput Input(string id, decimal? effort, string? predecessor = null) => new(new(id, PlanningMode.Auto, "U1"), effort, null, [], predecessor is null ? [] : [new(predecessor)]);
    [TestCase("2026-10-05 13:00", "2026-10-05 14:00")]
    [TestCase("2026-10-05 18:00", "2026-10-06 09:00")]
    public void PositiveWorkAtFinishBoundaryStartsInNextEligibleInterval(string anchor, string start)
    {
        var result = PlanningEngine.Calculate(Plan() with { Start = At(anchor) }, [Input("A", 1)], 1).Tasks[0];
        Assert.That(result.Start, Is.EqualTo(At(start)));
    }
    [Test]
    public void FridayHalfWeightSkipsOfficialMondayHolidayAndIndependentTasksAreNotStaggered()
    {
        var plan = Plan(50) with { Start = At("2026-10-09 09:00") };
        var results = PlanningEngine.Calculate(plan, [Input("A", 8), Input("B", 8)], 1).Tasks;
        Assert.That(results.Select(r => r.Finish), Is.All.EqualTo(At("2026-10-13 18:00")));
        Assert.That(results.Select(r => r.Start), Is.All.EqualTo(plan.Start));
    }
    [Test]
    public void PersonExceptionOverridesProjectHolidayAndMissingYearNeedsExplicitPolicy()
    {
        var plan = Plan() with { Start = At("2028-01-03 09:00") };
        Assert.That(PlanningEngine.Calculate(plan, [Input("A", 1)], 1).Tasks[0].Resolved, Is.False);
        var day = new DateOnly(2028, 1, 3);
        plan = plan with { Calendar = plan.Calendar with { Exceptions = [new(day, null, []), new(day, "U1", [new(600, 720)])] } };
        var result = PlanningEngine.Calculate(plan, [Input("A", 1)], 2).Tasks[0];
        Assert.That(result.Start, Is.EqualTo(At("2028-01-03 10:00"))); Assert.That(result.Finish, Is.EqualTo(At("2028-01-03 11:00")));
        plan = plan with { Calendar = plan.Calendar with { Exceptions = [], HolidaysNotConsidered = true } };
        result = PlanningEngine.Calculate(plan, [Input("A", 1)], 3).Tasks[0];
        Assert.That(result.Start, Is.EqualTo(plan.Start)); Assert.That(result.Warnings, Has.Some.Contains("祝日"));
    }
    [Test]
    public void FinalMinuteRoundingIsVisibleAndNeverChangesRawEffort()
    {
        var result = PlanningEngine.Calculate(Plan(80), [Input("A", .00000001m)], 9).Tasks[0];
        Assert.That(result.Finish, Is.EqualTo(At("2026-10-05 09:01")));
        Assert.That(result.RawHours, Is.EqualTo(.00000001m)); Assert.That(result.RoundedMinutes, Is.EqualTo(1));
        Assert.That(result.Warnings, Has.Some.Contains("分"));
    }
    [TestCase(1)]
    [TestCase(2)]
    public void NativeAssigneesNeverSilentlySelectPlanningOwner(int count)
    {
        var input = Input("A", 1) with { Task = new("A", PlanningMode.Auto), Assignees = Enumerable.Range(1, count).Select(i => "U" + i).ToArray() };
        Assert.That(PlanningEngine.Calculate(Plan(), [input], 1).Tasks[0].Problem, Does.Contain("担当者"));
    }
    [Test]
    public void InProgressUsesIndependentRemainingFromExplicitCutoffAndKeepsActuals()
    {
        var task = new PlanningTask("A", PlanningMode.Auto, "U1", Progress: PlanningProgress.InProgress,
            ActualStart: At("2026-10-05 09:00"), Actuals: [new("U1", 20, new(2026, 10, 5))]);
        var input = new PlanningInput(task, 16, 3, [], []);
        var result = PlanningEngine.Calculate(Plan(50) with { Cutoff = At("2026-10-06 09:00") }, [input], 1);
        Assert.That(result.Tasks[0].Start, Is.EqualTo(At("2026-10-06 09:00"))); Assert.That(result.Tasks[0].Finish, Is.EqualTo(At("2026-10-06 16:00")));
        Assert.That(result.Inputs![0].Task.Actuals!.Single().Hours, Is.EqualTo(20)); Assert.That(result.Inputs[0].Estimate, Is.EqualTo(16));
        Assert.That(PlanningEngine.Calculate(Plan(), [input with { Remaining = null, Task = task with { Progress = PlanningProgress.Reopened } }], 2).Tasks[0].Resolved, Is.False);
    }
    [Test]
    public void CompletedUsesDeliberateActualDatesAndZeroRemainingWithoutInferringFinish()
    {
        var input = Input("A", 16) with { Remaining = 0, Task = new("A", PlanningMode.Auto, "U1", Progress: PlanningProgress.Completed,
            ActualStart: At("2026-10-02 09:00"), ActualFinish: At("2026-10-02 17:11")) };
        var result = PlanningEngine.Calculate(Plan(), [input], 1).Tasks[0];
        Assert.That(result.Start, Is.EqualTo(input.Task.ActualStart)); Assert.That(result.Finish, Is.EqualTo(input.Task.ActualFinish));
        Assert.That(PlanningEngine.Calculate(Plan(), [input with { Task = input.Task with { ActualFinish = null } }], 2).Tasks[0].Finish, Is.Null);
        Assert.That(PlanningEngine.Calculate(Plan(), [input with { Remaining = 1 }], 3).Tasks[0].Resolved, Is.False);
    }
    [Test]
    public void ZeroCapacityAndUnsupportedLinkRetainLabelledManualException()
    {
        var a = Input("A", 1);
        var result = PlanningEngine.Calculate(Plan(0), [a], 1).Tasks[0];
        Assert.That(result.Resolved, Is.False);
        result = PlanningEngine.Calculate(Plan(0), [a with { Task = a.Task with { Mode = PlanningMode.Manual, ManualStart = At("2026-10-05 09:00"), ManualFinish = At("2026-10-05 10:00") }, Predecessors = [new("external", "SS")] }], 2).Tasks[0];
        Assert.That(result.Resolved, Is.True); Assert.That(result.Warnings, Is.Not.Empty);
    }
    [TestCase(100, "2026-10-06 18:00")]
    [TestCase(80, "2026-10-07 13:00")]
    [TestCase(50, "2026-10-08 18:00")]
    public void WeightIsAppliedOnceAndRawLaborDoesNotChange(int weight, string finish)
    {
        var result = PlanningEngine.Calculate(Plan(weight), [Input("A", 16)], 17).Tasks.Single();
        Assert.That(result.Finish, Is.EqualTo(At(finish))); Assert.That(result.RawHours, Is.EqualTo(16));
    }
    [TestCase(4, 4, "2026-10-05 14:00")]
    [TestCase(3, 5, "2026-10-05 12:00")]
    public void FinishToStartRetainsValidFinishAndPlacesSuccessorAcrossLunch(int a, int b, string start)
    {
        var results = PlanningEngine.Calculate(Plan(), [Input("B", b, "A"), Input("A", a)], 1).Tasks;
        Assert.That(results[0].Start, Is.EqualTo(At(start))); Assert.That(results[0].Finish, Is.EqualTo(At("2026-10-05 18:00")));
    }
    [Test]
    public void ManualAdoptedFinishControlsSuccessorAndWarningsRemainVisible()
    {
        var manual = Input("A", 16) with { Task = new("A", PlanningMode.Manual, "U1", At("2026-10-05 12:07"), At("2026-10-06 16:19")) };
        var result = PlanningEngine.Calculate(Plan(50), [manual, Input("B", 1, "A")], 2).Tasks;
        Assert.That(result[0].Finish, Is.EqualTo(manual.Task.ManualFinish)); Assert.That(result[0].Warnings, Is.Not.Empty);
        Assert.That(result[1].Start, Is.EqualTo(manual.Task.ManualFinish)); Assert.That(result[1].Warnings, Is.Not.Empty);
    }
    [Test]
    public void CycleAndMissingDependencyAreUnresolvedWithoutErasingManualException()
    {
        var a = Input("A", 4, "B"); var b = Input("B", 4, "A") with { Task = new("B", PlanningMode.Manual, "U1", At("2026-10-05 09:00"), At("2026-10-05 13:00")) };
        var result = PlanningEngine.Calculate(Plan(), [a, b, Input("C", 4, "missing")], 1).Tasks;
        Assert.That(result[0].Resolved, Is.False); Assert.That(result[0].Problem, Does.Contain("循環"));
        Assert.That(result[1].Finish, Is.EqualTo(b.Task.ManualFinish)); Assert.That(result[1].Warnings, Is.Not.Empty);
        Assert.That(result[2].Resolved, Is.False); Assert.That(result[2].Problem, Does.Contain("先行"));
    }
    [Test]
    public void FixedFinishPlacesWorkWithoutViolatingForwardBoundsAndDeadlineOnlyWarns()
    {
        var input = Input("A", 8) with { Task = Input("A", 8).Task with { FixedFinish = At("2026-10-06 18:00"), Deadline = At("2026-10-05 18:00") } };
        var result = PlanningEngine.Calculate(Plan(), [input], 1).Tasks.Single();
        Assert.That(result.Start, Is.EqualTo(At("2026-10-06 09:00"))); Assert.That(result.Finish, Is.EqualTo(input.Task.FixedFinish));
        Assert.That(result.Warnings, Has.Some.Contains("期限"));
        input = input with { Task = input.Task with { EarliestStart = At("2026-10-06 14:00") } };
        Assert.That(PlanningEngine.Calculate(Plan(), [input], 2).Tasks[0].Resolved, Is.False);
    }
}
