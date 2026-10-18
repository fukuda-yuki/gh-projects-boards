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
        var d = Document(new PlanRow("A", "One", "a/b") { Assignees = ["U1"], Remaining = 8 }, new("B", "Two", "a/b") { Assignees = ["U1"], Remaining = 8 });
        var scheduled = PlanOperations.Schedule(d, Day);
        var supplied = scheduled.Select(t => t with { PlannedHours = new Dictionary<DateOnly, decimal> { [Day] = 8 } }).ToArray();
        var result = PlanSummary.Calculate(d, supplied, Day);
        var month = result.Months!.People.Single(p => p.Identity == "U1").Periods[0];
        Assert.That(month.Planned, Is.EqualTo(16));
        Assert.That(month.DailyOverloads.Single().Percent, Is.EqualTo(400));
        Assert.That(month.MaxDailyPercent, Is.EqualTo(400));
        Assert.That(month.Tasks, Is.EquivalentTo(new[] { "A", "B" }));
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
        Assert.That(unknown.RequiredLoad, Is.Null);
        var zero = Calculate(Document(new PlanRow("I", "Zero", "a/b") { Remaining = 0 }));
        Assert.That(zero.Total.Progress.Value, Is.Null);
        Assert.That(zero.RequiredLoad, Is.Zero);
    }

    [Test]
    public void AttentionUsesPlanDatesAndExistingEffortFlags()
    {
        // 期限超過 compares the plan end with the 基準日 (#236), so the row needs the plan dates it is judged on.
        var d = Document(new PlanRow("I", "Overdue", "a/b") { Start = Day.AddDays(-2), End = Day.AddDays(-1), Estimate = 8, Actual = 10, Remaining = 0 });
        var result = Calculate(d);
        Assert.That(result.Attention.Overdue, Is.EqualTo(new[] { "I" }));
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
    public void MonthlyUnknownWorkStaysUnallocatedAndExtendsThroughKnownLaterFinish()
    {
        var d = Document(new("I", "Unknown", "a/b") { Assignees = ["U1"] },
            new("K", "Kept", "a/b") { Assignees = ["U1"], Remaining = 8, Fixed = true, Start = new(2026, 11, 2), End = new(2026, 11, 2) });
        var result = Calculate(d);
        Assert.That(result.ForecastFinish, Is.Null);
        Assert.That(result.Months!.Periods, Has.Count.EqualTo(2));
        var person = result.Months.People.Single(p => p.Identity == "U1");
        Assert.That(person.Unallocated, Does.Contain("I"));
        Assert.That(person.Periods.All(p => p.Planned is null && p.Percent is null && p.MaxDailyPercent is null), Is.True);
        Assert.That(person.Periods[1].DailyOverloads.Single().Tasks, Is.EqualTo(new[] { "K" }));
    }
}
