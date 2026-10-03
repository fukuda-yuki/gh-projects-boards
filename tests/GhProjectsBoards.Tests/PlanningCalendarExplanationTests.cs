using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanningCalendarExplanationTests
{
    private static DateTime At(string text) => PlanningContractTests.At(text);
    private static PlanningInput Input(string id, string owner, decimal hours, params PlanningLink[] predecessors)
        => new(new(id, PlanningMode.Auto, owner), hours, null, [], predecessors);

    [Test]
    public void ForwardStartGapIncludesTheAdoptedWeekendAndHolidayBetweenItsEndpoints()
    {
        var configuration = PlanningPathTests.Plan() with { Start = At("2026-10-09 18:00") };
        var plan = PlanningEngine.Calculate(configuration, [Input("A", "U1", 3)], 21);
        var before = System.Text.Json.JsonSerializer.Serialize(plan);

        var explanation = PlanningCalendarExplanation.Create(plan, "A")!;

        Assert.That(explanation.Days.Select(day => day.Date), Is.EqualTo(new[] {
            new DateOnly(2026, 10, 9), new(2026, 10, 10), new(2026, 10, 11), new(2026, 10, 12), new(2026, 10, 13) }));
        Assert.That(explanation.Days.Select(day => day.Source), Is.EqualTo(new[] {
            WorkingDaySource.Regular, WorkingDaySource.Weekend, WorkingDaySource.Weekend, WorkingDaySource.Holiday, WorkingDaySource.Regular }));
        Assert.That(explanation.Days.Single(day => day.Source == WorkingDaySource.Holiday).HolidayName, Is.EqualTo("スポーツの日"));
        Assert.That(plan.Tasks.Single().Start, Is.EqualTo(At("2026-10-13 09:00")));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(plan), Is.EqualTo(before));
    }

    [Test]
    public void PersonalIntervalOverridesProjectExceptionInTheAdoptedExplanation()
    {
        var day = new DateOnly(2026, 10, 12);
        var configuration = PlanningPathTests.Plan() with { Start = At("2026-10-12 09:00") };
        configuration = configuration with { Calendar = configuration.Calendar with
        {
            Exceptions = [new(day, null, []), new(day, "U1", [new(600, 720)])]
        } };
        var plan = PlanningEngine.Calculate(configuration, [Input("A", "U1", 1)], 9);

        var explanation = PlanningCalendarExplanation.Create(plan, "A")!;

        Assert.That(explanation.PersonName, Is.EqualTo("Owner"));
        Assert.That(explanation.SourceRevision, Is.EqualTo(9));
        Assert.That(explanation.Days.Single().Source, Is.EqualTo(WorkingDaySource.PersonalException));
        Assert.That(explanation.Days.Single().Intervals, Is.EqualTo(new WorkingInterval[] { new(600, 720) }));
        Assert.That(explanation.Days.Single().HolidayName, Is.Null, "A personal exception, not the coincident holiday, supplied these intervals.");
        Assert.That(plan.Tasks.Single().Start, Is.EqualTo(At("2026-10-12 10:00")));
    }

    [TestCase("2026-10-12 09:00", false, WorkingDaySource.Holiday)]
    [TestCase("2026-10-10 09:00", false, WorkingDaySource.Weekend)]
    [TestCase("2026-10-13 09:00", false, WorkingDaySource.Regular)]
    [TestCase("2026-10-12 09:00", true, WorkingDaySource.ProjectException)]
    public void EffectiveDayReportsTheSourceActuallyUsedByTheAdoptedCalendar(string start, bool projectException,
        WorkingDaySource source)
    {
        var configuration = PlanningPathTests.Plan() with { Start = At(start) };
        if (projectException) configuration = configuration with { Calendar = configuration.Calendar with
        {
            Exceptions = [new(DateOnly.FromDateTime(At(start)), null, [new(600, 720)])]
        } };
        var plan = PlanningEngine.Calculate(configuration, [Input("A", "U1", 1)], 10);

        var explanation = PlanningCalendarExplanation.Create(plan, "A")!;

        Assert.That(explanation.Days.Single(d => d.Date == DateOnly.FromDateTime(At(start))).Source, Is.EqualTo(source));
        Assert.That(explanation.Days.Single(d => d.Date == DateOnly.FromDateTime(At(start))).Intervals,
            source is WorkingDaySource.Holiday or WorkingDaySource.Weekend ? Is.Empty : Is.Not.Empty);
    }

    [Test]
    public void PredecessorAtPersonalIntervalEndExplainsNoRemainingTimeAndNextDayStart()
    {
        var configuration = PlanningPathTests.Plan() with
        {
            Start = At("2026-10-13 09:00"), People = [new("U1", "Owner 1"), new("U2", "Owner 2")]
        };
        configuration = configuration with { Calendar = configuration.Calendar with
        {
            Exceptions = [new(new(2026, 10, 13), "U2", [new(600, 720)])]
        } };
        var plan = PlanningEngine.Calculate(configuration,
            [Input("A", "U1", 3), Input("B", "U2", 2.5m, new PlanningLink("A"))], 17);
        var before = System.Text.Json.JsonSerializer.Serialize(plan);

        var explanation = PlanningCalendarExplanation.Create(plan, "B")!;

        Assert.That(explanation.PersonId, Is.EqualTo("U2"));
        Assert.That(explanation.PersonName, Is.EqualTo("Owner 2"));
        Assert.That(explanation.Start!.Boundary, Is.EqualTo(At("2026-10-13 12:00")));
        Assert.That(explanation.Start.AdoptedStart, Is.EqualTo(At("2026-10-14 09:00")));
        Assert.That(explanation.Start.PredecessorId, Is.EqualTo("A"));
        Assert.That(explanation.Start.Controller, Is.EqualTo("先行 A"));
        Assert.That(explanation.Start.NoRemainingInterval, Is.True);
        Assert.That(explanation.Days.Select(d => d.Date), Is.EqualTo(new[] { new DateOnly(2026, 10, 13), new(2026, 10, 14) }));
        Assert.That(explanation.Days[0].Source, Is.EqualTo(WorkingDaySource.PersonalException));
        Assert.That(explanation.Days[0].Intervals, Is.EqualTo(new WorkingInterval[] { new(600, 720) }));
        Assert.That(explanation.Days[1].Source, Is.EqualTo(WorkingDaySource.Regular));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(plan), Is.EqualTo(before), "An explanation must not adopt or mutate planning data.");
    }

    [Test]
    public void LunchDelayHasLaterWorkingTimeOnTheSameDay()
    {
        var plan = PlanningEngine.Calculate(PlanningPathTests.Plan(),
            [Input("A", "U1", 4), Input("B", "U1", 1, new PlanningLink("A"))], 3);

        var explanation = PlanningCalendarExplanation.Create(plan, "B")!;

        Assert.That(explanation.Start!.Boundary, Is.EqualTo(At("2026-10-05 13:00")));
        Assert.That(explanation.Start.AdoptedStart, Is.EqualTo(At("2026-10-05 14:00")));
        Assert.That(explanation.Start.NoRemainingInterval, Is.False);
        Assert.That(explanation.Days, Has.Length.EqualTo(1));
    }

    [Test]
    public void CoincidentPredecessorFinishDoesNotReplaceTheRecordedEarliestStartCause()
    {
        var configuration = PlanningPathTests.Plan() with
        {
            Start = At("2026-10-13 09:00"), People = [new("U1", "Owner 1"), new("U2", "Owner 2")]
        };
        configuration = configuration with { Calendar = configuration.Calendar with
        {
            Exceptions = [new(new(2026, 10, 13), "U2", [new(600, 720)])]
        } };
        var successor = Input("B", "U2", 1, new PlanningLink("A"));
        successor = successor with { Task = successor.Task with { EarliestStart = At("2026-10-13 12:00") } };
        var plan = PlanningEngine.Calculate(configuration, [Input("A", "U1", 3), successor], 18);

        var explanation = PlanningCalendarExplanation.Create(plan, "B")!;

        Assert.That(plan.Tasks.Single(t => t.Id == "A").Finish, Is.EqualTo(explanation.Start!.Boundary));
        Assert.That(explanation.Start.Controller, Is.EqualTo("最早開始"));
        Assert.That(explanation.Start.PredecessorId, Is.Null, "An equal timestamp does not establish which constraint controlled the start.");
        Assert.That(explanation.Start.AdoptedStart, Is.EqualTo(At("2026-10-14 09:00")));
    }

    [TestCase("manual")]
    [TestCase("unknown-controller")]
    [TestCase("completed")]
    [TestCase("fixed-finish")]
    public void CalendarContextDoesNotInventAStartCauseForNonForwardOrUnknownPlacement(string kind)
    {
        var input = Input("A", "U1", 1);
        input = kind switch
        {
            "manual" => input with { Task = input.Task with { Mode = PlanningMode.Manual,
                ManualStart = At("2026-10-06 09:00"), ManualFinish = At("2026-10-06 10:00") } },
            "completed" => input with { Remaining = 0, Task = input.Task with { Progress = PlanningProgress.Completed,
                ActualStart = At("2026-10-06 09:00"), ActualFinish = At("2026-10-06 10:00") } },
            "fixed-finish" => input with { Task = input.Task with { FixedFinish = At("2026-10-06 10:00") } },
            _ => input
        };
        var configuration = PlanningPathTests.Plan() with { Start = At("2026-10-05 18:00") };
        var plan = PlanningEngine.Calculate(configuration, [input], 6);
        if (kind == "unknown-controller") plan = plan with { Tasks = [plan.Tasks.Single() with { Controller = "Unknown" }] };

        var explanation = PlanningCalendarExplanation.Create(plan, "A")!;

        Assert.That(explanation.Days, Is.Not.Empty);
        Assert.That(explanation.Start, Is.Null);
    }

    [TestCase("stale")]
    [TestCase("missing-inputs")]
    [TestCase("missing-configuration")]
    [TestCase("unresolved")]
    public void IncompleteOrDifferentRevisionEvidenceHasNoCalendarStartClaim(string kind)
    {
        var input = Input("A", "U1", 1);
        if (kind == "unresolved") input = input with { Estimate = null };
        var plan = PlanningEngine.Calculate(PlanningPathTests.Plan(), [input], 7);
        plan = kind switch
        {
            "stale" => plan with { Tasks = [plan.Tasks.Single() with { SourceRevision = 6 }] },
            "missing-inputs" => plan with { Inputs = null },
            "missing-configuration" => plan with { Configuration = null },
            _ => plan
        };

        var explanation = PlanningCalendarExplanation.Create(plan, "A");

        Assert.That(explanation?.Start, Is.Null);
    }
}
