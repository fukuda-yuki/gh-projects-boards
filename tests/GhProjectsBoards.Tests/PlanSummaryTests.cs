using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanSummaryTests
{
    private static readonly DateOnly Day = new(2026, 10, 5);
    private static PlanDocument Document(params PlanRow[] rows) => new(new(new("github.com", 1), "P1"),
        new([.. rows], [new("F", "工程", "SINGLE_SELECT") { Options = ["UI", "SA"] }]),
        new([.. rows], new() { ProjectStart = Day, ProjectEnd = Day.AddDays(11), StatusDate = Day,
            Columns = [new(PlanField.Phase, "F", "工程", "SINGLE_SELECT")], People = [new("U1", "Alice", 50, null), new("U2", "Bob", 100, 20)] }));
    private static SummaryPlan Calculate(PlanDocument d, DateOnly? day = null)
        => PlanSummary.Calculate(d, PlanOperations.Schedule(d, day ?? Day), day ?? Day);

    [TestCase(-2, 0, 9), TestCase(0, 0, 9), TestCase(4, 4, 5), TestCase(14, 9, 0)]
    public void WorkingDaysExcludeWeekendAndHolidayAndRespectStatusOutsidePeriod(int offset, int elapsed, int remaining)
    {
        var result = Calculate(Document(), Day.AddDays(offset));
        Assert.Multiple(() => {
            Assert.That(result.WorkingDays, Is.EqualTo(9)); // October 12 is a bundled holiday.
            Assert.That(result.ElapsedDays, Is.EqualTo(elapsed));
            Assert.That(result.RemainingDays, Is.EqualTo(remaining));
        });
    }
    [Test]
    public void CompanyAndImportedHolidayAreCountedOnceAndMissingDatesStayUnknown()
    {
        var d = Document();
        d = d with { State = d.State with { Settings = d.State.Settings with {
            CompanyDaysOff = [Day, Day.AddDays(1)],
            ImportedHolidays = new("test", "test", new string('a', 64), DateTimeOffset.UnixEpoch, 2026, 2026, [new(Day, "Imported")]) } } };
        Assert.That(Calculate(d).WorkingDays, Is.EqualTo(7));
        var noEnd = Calculate(d with { State = d.State with { Settings = d.State.Settings with { ProjectEnd = null } } });
        Assert.That((noEnd.WorkingDays, noEnd.RemainingDays, noEnd.Capacity, noEnd.RequiredLoad, noEnd.Months), Is.EqualTo(((int?)null, (int?)null, (decimal?)null, (decimal?)null, (PeoplePlan?)null)));
    }
    [TestCase(9, -2), TestCase(11, 0), TestCase(14, 1)]
    public void ForecastDifferenceUsesSignedWorkingDays(int offset, int expected)
    {
        var result = Calculate(Document(new PlanRow("I1", "Kept", "a/b") { Start = Day, End = Day.AddDays(offset) }));
        Assert.That(result.ForecastFinish, Is.EqualTo(Day.AddDays(offset)));
        Assert.That(result.FinishDifference, Is.EqualTo(expected));
    }
    [Test]
    public void ClosedReestimatedAndUnknownTasksKeepHonestEffortSetsAndExcludeSummaries()
    {
        var d = Document(
            new("S", "Summary", "a/b") { Estimate = 999, Actual = 999 },
            new("A", "Closed", "a/b") { Parent = "S", Phase = "SA", Estimate = 8, Actual = 10, Closed = true, CloseDate = Day, Remaining = 90 },
            new("B", "Default remaining", "a/b") { Parent = "S", Phase = "UI", Estimate = 10, Actual = 4 },
            new("C", "Reestimated", "a/b") { Phase = "UI", Estimate = 8, Actual = 4, Remaining = 10 },
            new("D", "Unknown", "a/b") { Actual = 3 },
            new("E", "Remaining only", "a/b") { Actual = 2, Remaining = 6 });
        var result = Calculate(d);
        Assert.Multiple(() => {
            Assert.That(result.Total.Count, Is.EqualTo(5));
            Assert.That(result.Total.Closed, Is.EqualTo(1));
            Assert.That(result.Total.Remaining, Is.EqualTo(new SummaryValue(22, 1)));
            Assert.That(result.Total.Forecast, Is.EqualTo(new SummaryValue(42, 1)));
            Assert.That(result.Total.Variance, Is.EqualTo(new SummaryValue(8, 2)));
            Assert.That(result.Total.Progress.Value, Is.EqualTo(20m / 42));
            Assert.That(result.Total.Progress.Excluded, Is.EqualTo(1));
            Assert.That(result.Total.ProgressActual, Is.EqualTo(20));
            Assert.That(result.Total.VarianceEstimate, Is.EqualTo(26));
            Assert.That(result.Total.VarianceForecast, Is.EqualTo(34));
            Assert.That(result.Attention.UnknownEffort, Is.EqualTo(new[] { "D" }));
            Assert.That(result.Attention.OverEstimate, Is.EqualTo(new[] { "C" }));
            Assert.That(result.Phases.Select(p => p.Phase), Is.EqualTo(new string?[] { "UI", "SA", null, "全体" }));
        });
    }
    [TestCase(null, 36), TestCase(30, 20), TestCase(5, 0)]
    public void CapacityUsesRateAndOptionalAllowanceLessActual(int? allowance, int capacity)
    {
        var d = Document(new PlanRow("I", "Task", "a/b") { Assignees = ["U1"], Estimate = 100, Actual = 10 });
        d = d with { State = d.State with { Settings = d.State.Settings with { People = [new("U1", "Alice", 50, allowance)] } } };
        var result = Calculate(d);
        Assert.That(result.People[0].Capacity, Is.EqualTo(capacity));
        Assert.That(result.RequiredLoad, Is.EqualTo(capacity == 0 ? null : (decimal?)(90m / capacity)));
        var after = Calculate(d, Day.AddDays(14));
        Assert.That(after.People[0].Capacity, Is.Zero);
        Assert.That(after.RequiredLoad, Is.Null);
    }
    [Test]
    public void PersonPhaseGroupingCountsAmbiguousWorkOnceAndIncludesItInRequiredLoad()
    {
        var d = Document(new PlanRow("A", "Assigned", "a/b") { Phase = "UI", Assignees = ["U1"], Remaining = 8 },
            new("B", "Unassigned", "a/b") { Phase = "SA", Remaining = 12 },
            new("C", "Multiple", "a/b") { Phase = "SA", Assignees = ["U1", "U2"], Remaining = 8 });
        var result = Calculate(d);
        Assert.That(result.People.Select(p => p.Identity), Is.EqualTo(new[] { "U1", "U2", PlanPeople.Unassigned, PlanPeople.Multiple }));
        Assert.That(result.People[0].Phases[0].Tasks.Identities, Is.EqualTo(new[] { "A" }));
        Assert.That(result.People[0].Tasks.Remaining.Value, Is.EqualTo(8));
        Assert.That(result.People[^1].Capacity, Is.Null);
        Assert.That(result.Capacity, Is.EqualTo(56));
        Assert.That(result.RequiredLoad, Is.EqualTo(0.5m));
        Assert.That(result.PeopleTotal.Tasks.Identities, Is.EquivalentTo(new[] { "A", "B", "C" }));
    }
    [Test]
    public void MonthlyLoadKeepsDailyOverloadAndUsesSuppliedSchedule()
    {
        var d = Document(new PlanRow("A", "One", "a/b") { Assignees = ["U1"], Remaining = 8, Start = Day }, new("B", "Two", "a/b") { Assignees = ["U1"], Remaining = 8, Start = Day });
        var scheduled = PlanOperations.Schedule(d, Day);
        var supplied = scheduled.Select(t => t with { PlannedHours = new Dictionary<DateOnly, decimal> { [Day] = 8 } }).ToArray();
        var result = PlanSummary.Calculate(d, supplied, Day);
        var month = result.Months!.People.Single(p => p.Identity == "U1").Periods[0];
        Assert.That(month.Planned, Is.EqualTo(16));
        Assert.That(month.DailyOverloads.Single().Percent, Is.EqualTo(400));
        Assert.That(month.MaxDailyPercent, Is.EqualTo(400));
        Assert.That(month.MaxDailyDate, Is.EqualTo(Day));
        Assert.That(month.Tasks, Is.EquivalentTo(new[] { "A", "B" }));
        // #230 D1: the cell names the load rate and the days over the cap; the maximum lives beside them
        // for the drill-down and tooltip. October has 21 working days at 50%, so 84 h of capacity.
        var cell = result.MonthCells!.Single(p => p.Identity == "U1").Cells[0];
        Assert.That((cell.Kind, cell.Percent, cell.DaysOverCap, cell.MaxDailyPercent, cell.MaxDate),
            Is.EqualTo((SummaryMonthKind.Work, 16m / 84m * 100m, 1, 400m, (DateOnly?)Day)));
        Assert.That(cell.Overloaded, Is.True);
        Assert.That(cell.Tasks, Is.EquivalentTo(new[] { "A", "B" }));
    }

    [Test]
    public void MonthCellsReadFinishedNoWorkAndWorkFromThePlanWithoutInventingWarnings()
    {
        // A past month that held only closed work reads 完了; a past month without work reads —; an open
        // task keeps its month a load month. The month interval, not the allocation, decides that a month
        // held work, so a closed task's retained dates still finish its month (#230 D1).
        var closed = new PlanRow("C", "Done", "a/b") { Assignees = ["U1"], Start = new(2026, 8, 3), End = new(2026, 8, 7), Closed = true, CloseDate = new(2026, 8, 7) };
        var open = new PlanRow("O", "Ongoing", "a/b") { Assignees = ["U1"], Remaining = 16, Start = Day };
        var d = Document(closed, open) with { State = Document(closed, open).State with { Settings = new() {
            ProjectStart = new(2026, 8, 3), ProjectEnd = Day, StatusDate = Day,
            Columns = [new(PlanField.Phase, "F", "工程", "SINGLE_SELECT")], People = [new("U1", "Alice", 50, null)] } } };
        var result = Calculate(d);
        var cells = result.MonthCells!.Single(p => p.Identity == "U1").Cells;
        Assert.That(result.Months!.Periods.Select(p => p.Start.Month), Is.EqualTo(new[] { 8, 9, 10 }));
        Assert.That(cells.Select(c => c.Kind), Is.EqualTo(new[] { SummaryMonthKind.Finished, SummaryMonthKind.NoWork, SummaryMonthKind.Work }));
        Assert.That(cells[0].Tasks, Is.Empty, "A finished month holds no current allocation.");
        Assert.That((cells[0].DaysOverCap, cells[0].MaxDailyPercent), Is.EqualTo((0, (decimal?)0)));
        Assert.That(cells[2].Percent, Is.EqualTo(16m / 84m * 100m));
        Assert.That(cells[2].DaysOverCap, Is.Zero, "A month under the cap carries no warning data.");
        var noPeriod = Calculate(Document(new PlanRow("X", "X", "a/b")) with { State = Document(new PlanRow("X", "X", "a/b")).State with { Settings = new() { StatusDate = Day, People = [new("U1", "Alice", 50, null)] } } });
        Assert.That((noPeriod.Months, noPeriod.MonthCells), Is.EqualTo(((PeoplePlan?)null, (IReadOnlyList<SummaryMonthPerson>?)null)));
    }

    [Test]
    public void FinishedSetsDriveTheSummaryDashInsteadOfZero()
    {
        // The summary reads — where the only work is finished: this is the domain rule behind the
        // person × phase cell and 工程別 rendering, not a colour choice.
        var done = new PlanRow("A", "Done", "a/b") { Phase = "SA", Start = Day.AddDays(-2), End = Day, Closed = true, CloseDate = Day };
        var open = new PlanRow("B", "Open", "a/b") { Phase = "UI", Remaining = 8, Start = Day };
        var result = Calculate(Document(done, open));
        Assert.That(result.Phases.Single(p => p.Phase == "SA").Tasks.Finished, Is.True);
        Assert.That(result.Phases.Single(p => p.Phase == "UI").Tasks.Finished, Is.False);
        Assert.That(result.Phases.Single(p => p.IsTotal).Tasks.Finished, Is.False);
        Assert.That(Calculate(Document()).Total.Finished, Is.False, "An empty set is not a finished phase.");
    }

    [Test]
    public void EvaluationBandAndPhasesAgreeWithRecordedMeasuresFor1040Rows()
    {
        var plan = EvaluationFixture.ReadPlan();
        var snapshot = EvaluationFixture.Simulate(plan, [plan.StatusDate]).Single();
        var recorded = EvaluationFixture.Measures(plan, snapshot);
        var scheduled = PlanOperations.Schedule(snapshot.Document, snapshot.StatusDate);
        Assert.That(scheduled, Has.Count.EqualTo(1040));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = PlanSummary.Calculate(snapshot.Document, scheduled, snapshot.StatusDate);
        timer.Stop();
        TestContext.Out.WriteLine($"Summary for 1,040 rows: {timer.Elapsed.TotalMilliseconds:F1} ms (calculation only).");
        void Compare(SummaryTasks actual, EvaluationFixture.Effort expected)
        {
            Assert.Multiple(() => {
                Assert.That(actual.Count, Is.EqualTo(expected.Tasks));
                Assert.That(actual.Closed, Is.EqualTo(expected.Closed));
                Assert.That(actual.Estimate.Value, Is.EqualTo(expected.Estimate));
                Assert.That(actual.Actual.Value, Is.EqualTo(expected.Actual));
                Assert.That(actual.Remaining.Value, Is.EqualTo(expected.Remaining));
                Assert.That(actual.Forecast.Value, Is.EqualTo(expected.Forecast));
                Assert.That(actual.Variance.Value, Is.EqualTo(expected.Variance));
                Assert.That(Math.Round(actual.Progress.Value!.Value * 100, 1), Is.EqualTo(expected.Progress));
                Assert.That(actual.OverEstimate, Is.EqualTo(expected.Overruns));
                Assert.That(actual.Remaining.Excluded, Is.Zero);
            });
        }
        Compare(result.Total, recorded.Total);
        Assert.That(result.ForecastFinish, Is.EqualTo(recorded.Forecast));
        foreach (var phase in recorded.Phases)
        {
            var actual = result.Phases.Single(p => p.Phase == phase.Phase).Tasks;
            Compare(actual, phase.Effort);
            Assert.That(actual.Start, Is.EqualTo(phase.Start));
            Assert.That(actual.Finish, Is.EqualTo(phase.Forecast));
        }
        Assert.That(PlanOperations.Changes(snapshot.Document, snapshot.StatusDate).TaskCount, Is.Zero);
    }

    [Test]
    public void UnknownOnlyAndZeroForecastDoNotInventProgressOrLoad()
    {
        var unknown = Calculate(Document(new PlanRow("I", "Unknown", "a/b")));
        Assert.That(unknown.Total.Remaining, Is.EqualTo(new SummaryValue(null, 1)));
        Assert.That(unknown.Total.Progress.Value, Is.Null);
        Assert.That(unknown.Capacity, Is.Null, "No task assigns a person, so Project capacity is unknown.");
        Assert.That(unknown.RequiredLoad, Is.Null);
        var zero = Calculate(Document(new PlanRow("I", "Zero", "a/b") { Remaining = 0 }));
        Assert.That(zero.Total.Progress.Value, Is.Null);
        Assert.That(zero.Capacity, Is.Null);
        Assert.That(zero.RequiredLoad, Is.Null, "Unknown capacity makes required load unknown rather than 0.");
    }

    [Test]
    public void PersonRowsCapacityAndMonthsUseThePeopleAssignedNow()
    {
        var d = Document(
            new PlanRow("A", "Assigned", "a/b") { Assignees = ["U1"], Remaining = 8 },
            new PlanRow("B", "Late assigned", "a/b") { Assignees = ["U3"], Remaining = 16 });
        d = d with { State = d.State with { Settings = d.State.Settings with { People = [new("U1", "Alice", 50, null), new("U2", "Bob", 100, null)] } },
            Sync = d.Sync with { PeopleNames = d.Sync.PeopleNames.Add("U3", "person-U3") } };
        var result = Calculate(d);
        Assert.Multiple(() =>
        {
            Assert.That(result.People.Select(p => p.Identity), Is.EqualTo(new[] { "U1", "U3" }),
                "A stored person with no task is absent; a row assignee without a stored entry appears.");
            Assert.That(result.People.Single(p => p.Identity == "U1").Capacity, Is.EqualTo(36));
            var added = result.People.Single(p => p.Identity == "U3");
            Assert.That((added.Name, added.Capacity), Is.EqualTo(("person-U3", 72m)), "An assigned person with no stored entry uses 100% and the read name.");
            Assert.That(result.Capacity, Is.EqualTo(108), "Project capacity sums the people assigned now.");
            Assert.That(result.RequiredLoad, Is.EqualTo(24m / 108));
        });
        var people = result.Months!.People.Select(p => p.Identity).Where(id => id is not (PlanPeople.Unassigned or PlanPeople.Multiple));
        Assert.That(people, Is.EqualTo(new[] { "U1", "U3" }), "The month matrix lists the same assigned people.");
    }

    [Test]
    public void AttentionUsesPlanDatesAndExistingEffortFlags()
    {
        // 遅れ compares the plan end with the 基準日 (#236), so the row needs the plan dates it is judged on.
        var d = Document(new PlanRow("I", "Overdue", "a/b") { Start = Day.AddDays(-2), End = Day.AddDays(-1), Estimate = 8, Actual = 10, Remaining = 0 });
        var result = Calculate(d);
        Assert.That(result.Attention.Late, Is.EqualTo(new[] { "I" }));
        Assert.That(result.Attention.ZeroRemainingOpen, Is.EqualTo(new[] { "I" }));
        Assert.That(result.Attention.OverEstimate, Is.EqualTo(new[] { "I" }));
        Assert.That(result.Attention.UnassignedRemaining.Value, Is.Zero);
    }

    [Test]
    public void ClosedTaskForecastUsesCloseDateInsteadOfTheStoredEnd()
    {
        var d = Document(new PlanRow("I", "Closed", "a/b") { Closed = true, CloseDate = Day, End = Day.AddDays(4) });
        Assert.That(Calculate(d).ForecastFinish, Is.EqualTo(Day));
    }

    [Test]
    public void ForecastFinishPhaseAndMilestoneFollowTheForecastSchedule()
    {
        // #236 decision 5: 完了見込 and 差 come from the forecast schedule. A task keeps its plan end
        // (10/7) while its 40h of remaining work is forecast from the 基準日 to 10/9, so the band and every
        // measure that shares the finish rule must report the forecast end.
        var row = new PlanRow("I", "Planned", "a/b") { Phase = "UI", MilestoneTitle = "UI 完了", Start = Day, End = Day.AddDays(2), Estimate = 40, Remaining = 40 };
        var document = Document(row);
        document = document with {
            Baseline = document.Baseline with { Milestones = [new("UI 完了", Day.AddDays(4), false, false)] },
            State = document.State with {
                Settings = document.State.Settings with {
                    PhaseMilestones = ImmutableDictionary<string, string>.Empty.Add("UI", "UI 完了") } }
        };
        var result = Calculate(document);
        Assert.Multiple(() => {
            Assert.That(result.Total.Finish, Is.EqualTo(Day.AddDays(4)), "The band's finish is the latest forecast end.");
            Assert.That(result.ForecastFinish, Is.EqualTo(Day.AddDays(4)));
            Assert.That(result.FinishDifference, Is.EqualTo(-4), "10/12 is a bundled holiday, so the signed difference counts 10/13–10/16.");
            Assert.That(result.Phases.Single(p => p.Phase == "UI").Tasks.Finish, Is.EqualTo(Day.AddDays(4)));
            Assert.That(result.Phases.Single(p => p.Phase == "UI").Difference, Is.Zero, "The milestone due date is the forecast end.");
            Assert.That(result.Milestones.Single().Forecast, Is.EqualTo(Day.AddDays(4)));
            Assert.That(result.Milestones.Single().Difference, Is.Zero);
        });
    }

    [Test]
    public void KeptWorkWithoutAForecastContributesItsRetainedPlanEnd()
    {
        // A kept row (no Estimate and no Remaining) has no forecast; its retained plan end still finishes the
        // version, which keeps the kept-date rule in docs/spec.md and the milestone contract true (#236 M2).
        var result = Calculate(Document(new PlanRow("I", "Kept", "a/b") { Start = Day, End = Day.AddDays(3) }));
        Assert.That(result.ForecastFinish, Is.EqualTo(Day.AddDays(3)));
    }

    [Test]
    public void ATaskWithNoEndIsLeftOutOfTheFinishAndCountedRatherThanHidingTheDate()
    {
        // One inserted row with no title, effort or dates must not turn a 1,000-task finish into 「—」; it is
        // left out and counted like an effort-less task in the totals, and nothing invents a date for it.
        var dated = new PlanRow("D", "Dated", "a/b") { Start = Day, End = Day.AddDays(1) };
        var undated = new PlanRow("U", "", "a/b");
        var both = Calculate(Document(dated, undated));
        Assert.Multiple(() => {
            Assert.That(Calculate(Document(undated)).ForecastFinish, Is.Null, "An undated-only plan has no finish.");
            Assert.That(both.ForecastFinish, Is.EqualTo(Day.AddDays(1)), "The dated task still finishes the version.");
            Assert.That(both.Total.FinishExcluded, Is.EqualTo(1));
            Assert.That(both.FinishDifference, Is.Not.Null);
            Assert.That(both.Phases.Single(p => p.IsTotal).Tasks.Finish, Is.EqualTo(Day.AddDays(1)));
        });
    }

    [Test]
    public void MonthlyUnknownWorkStaysUnallocatedAndExtendsThroughKnownLaterFinish()
    {
        var d = Document(new("I", "Unknown", "a/b") { Assignees = ["U1"] },
            new("K", "Kept", "a/b") { Assignees = ["U1"], Remaining = 8, Start = new(2026, 11, 2), End = new(2026, 11, 2) },
            new("K2", "Kept too", "a/b") { Assignees = ["U1"], Remaining = 8, Start = new(2026, 11, 2), End = new(2026, 11, 2) });
        var result = Calculate(d);
        Assert.That(result.ForecastFinish, Is.EqualTo(new DateOnly(2026, 11, 4)), "The undated task is left out; the kept task finishes at its forecast end.");
        Assert.That(result.Total.FinishExcluded, Is.EqualTo(1));
        Assert.That(result.Months!.Periods, Has.Count.EqualTo(2));
        var person = result.Months.People.Single(p => p.Identity == "U1");
        Assert.That(person.Unallocated, Does.Contain("I"));
        Assert.That(person.Periods.All(p => p.Planned is null && p.Percent is null && p.MaxDailyPercent is null), Is.True);
        // Two tasks on the same days overload the person even though the month's total stays unknown.
        Assert.That(person.Periods[1].DailyOverloads.First().Tasks, Is.EqualTo(new[] { "K", "K2" }));
    }
}

