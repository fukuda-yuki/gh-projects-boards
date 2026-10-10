using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanPeopleTests
{
    private static readonly DateOnly Day = new(2026, 10, 5);
    private static PlanRow Row(string id, decimal? remaining = 4) => new(id, id, "acme/repo") { Assignees = ["U1"], Estimate = 4, Actual = 0, Remaining = remaining };
    private static PlanDocument Document(ImmutableArray<PlanRow> rows, ProjectPlanSettings? settings = null) =>
        new(new(new("github.com", 42), "P1"), new(rows, []), new(rows, settings ?? new() { StatusDate = Day, People = [new("U1", "alice", 50, 80)] }));

    [TestCase(80, 56, 40, 96, -16)]
    [TestCase(100, 0, 0, 0, 100)]
    public void ForecastAndAllowanceUseCurrentAssignment(decimal allowance, decimal actual, decimal remaining, decimal forecast, decimal difference)
    {
        var d = Document([Row("I1", remaining) with { Actual = actual }]);
        d = d with { State = d.State with { Settings = d.State.Settings with { People = [new("U1", "alice", 50, allowance)] } } };
        var p = PlanPeople.Calculate(d, Day, Day, PlanPeriodScale.Day, 1).People.Single(p => p.Identity == "U1");
        Assert.That(p.Forecast, Is.EqualTo(forecast)); Assert.That(p.Difference, Is.EqualTo(difference));
    }
    [Test]
    public void IndependentTasksOverloadWithoutChangingScheduleOrAssignments()
    {
        var d = Document([Row("I1"), Row("I2")]); var before = PlanJson.Text(d);
        var p = PlanPeople.Calculate(d, Day, Day, PlanPeriodScale.Day, 1).People.Single(p => p.Identity == "U1");
        var load = p.Periods.Single();
        Assert.That((load.Planned, load.Capacity, load.Percent, load.Overloaded), Is.EqualTo((8m, 4m, 200m, true)));
        Assert.That(load.Tasks, Is.EqualTo(new[] { "I1", "I2" }));
        Assert.That(PlanOperations.Schedule(d, Day).Select(s => (s.Start.Value, s.End.Value)), Is.All.EqualTo((Day, Day)));
        Assert.That(PlanJson.Text(d), Is.EqualTo(before));
    }
    [TestCase(PlanPeriodScale.Day, "2026-12-31", "2026-12-31", "2026-12-31")]
    [TestCase(PlanPeriodScale.Week, "2027-01-01", "2026-12-28", "2027-01-03")]
    [TestCase(PlanPeriodScale.Month, "2026-12-31", "2026-12-01", "2026-12-31")]
    public void PeriodBoundariesUseFullCalendarPeriods(PlanPeriodScale scale, string anchor, string start, string end)
    {
        var result = PlanPeople.Calculate(Document([]), Day, DateOnly.Parse(anchor), scale, 2);
        Assert.That((result.Periods[0].Start, result.Periods[0].End), Is.EqualTo((DateOnly.Parse(start), DateOnly.Parse(end))));
        Assert.That(result.Periods[1].Start, Is.EqualTo(DateOnly.Parse(end).AddDays(1)));
    }
    [TestCase(PlanPeriodScale.Day, false, 4)]
    [TestCase(PlanPeriodScale.Week, false, 20)]
    [TestCase(PlanPeriodScale.Month, false, 84)]
    [TestCase(PlanPeriodScale.Day, true, 4)]
    [TestCase(PlanPeriodScale.Week, true, 16)]
    [TestCase(PlanPeriodScale.Month, true, 80)]
    public void CapacityUsesProjectWorkingDaysExcludingCompanyDaysOffAndHolidays(PlanPeriodScale scale, bool companyDayOff, decimal capacity)
    {
        var d = Document([Row("I1", 8)]);
        d = d with { State = d.State with { Settings = d.State.Settings with { CompanyDaysOff = companyDayOff ? [Day.AddDays(1)] : [] } } };
        var p = PlanPeople.Calculate(d, Day, Day, scale, 1).People.Single(p => p.Identity == "U1");
        Assert.That(p.Periods[0].Capacity, Is.EqualTo(capacity));
        Assert.That(p.Periods[0].Planned, Is.EqualTo(scale == PlanPeriodScale.Day ? 4m : 8m));
    }
    [Test]
    public void MissingInputsAndAmbiguousAssignmentsAreNotZeroOrDuplicated()
    {
        var d = Document([Row("I1", null), Row("I2") with { Assignees = [] }, Row("I3") with { Assignees = ["U1", "U2"] }, Row("I4") with { Actual = null }]);
        var report = PlanPeople.Calculate(d, Day, Day, PlanPeriodScale.Week, 1);
        var p = report.People.Single(p => p.Identity == "U1");
        Assert.That(p.Remaining, Is.Null); Assert.That(p.Actual, Is.Zero); Assert.That(p.Forecast, Is.Null);
        Assert.That(p.Missing, Does.Contain("残が未入力").And.Not.Contain("実績が未入力"));
        Assert.That(p.Unallocated, Does.Contain("I1")); Assert.That(p.Periods[0].Planned, Is.Null);
        foreach (var id in new[] { PlanPeople.Unassigned, PlanPeople.Multiple }) {
            var other = report.People.Single(p => p.Identity == id);
            Assert.That(other.Remaining, Is.EqualTo(4)); Assert.That(other.Periods[0].Capacity, Is.Null);
            Assert.That(other.Periods[0].Percent, Is.Null);
        }
    }
    [Test]
    public void UnstartedTasksHaveZeroActualAndKnownForecastWithoutChangingStoredInputs()
    {
        var d = Document([Row("I1", 40) with { Actual = null }, Row("I2", 50) with { Actual = null, Estimate = null }]);
        var before = PlanJson.Text(d);
        var p = PlanPeople.Calculate(d, Day, Day, PlanPeriodScale.Day, 1).People.Single(p => p.Identity == "U1");
        Assert.That((p.Actual, p.Forecast, p.Difference), Is.EqualTo((0m, 90m, -10m)));
        Assert.That(p.Missing, Does.Not.Contain("実績が未入力"));
        Assert.That(PlanJson.Text(d), Is.EqualTo(before));
    }
    [Test]
    public void DailyAllocationRetainsWorkImmediatelyAcrossADayBoundary()
    {
        var d = Document([Row("I1", 3.999999999999999999999999999m),
            Row("I2", 0.000000000000000000000000002m) with { Predecessors = ["I1"] }]);
        var p = PlanPeople.Calculate(d, Day, Day, PlanPeriodScale.Day, 2).People.Single(p => p.Identity == "U1");
        Assert.That(p.Periods.Select(p => p.Planned), Is.EqualTo(new[] { 4m, 0.000000000000000000000000001m }));
    }
    [Test]
    public void SameDayDependencyUsesTheSchedulersPartialDayAllocation()
    {
        var d = Document([Row("I1", 1), Row("I2", 5) with { Predecessors = ["I1"] }]);
        var p = PlanPeople.Calculate(d, Day, Day, PlanPeriodScale.Day, 2).People.Single(p => p.Identity == "U1");
        Assert.That(p.Periods.Select(p => p.Planned), Is.EqualTo(new[] { 4m, 2m }));
        Assert.That(p.Periods[0].Tasks, Is.EqualTo(new[] { "I1", "I2" }));
    }
    [Test]
    public void CurrentAssigneeOwnsActualAndNoOtherProjectContributes()
    {
        var d = Document([Row("I1", 40) with { Actual = 56, Assignees = ["U2"] }]);
        var other = Document([Row("I9", 900)]);
        var result = PlanPeople.Calculate(d, Day, Day, PlanPeriodScale.Month, 1);
        Assert.That(result.People.Single(p => p.Identity == "U1").Actual, Is.Zero);
        Assert.That(result.People.Single(p => p.Identity == "U2").Forecast, Is.EqualTo(96));
        Assert.That(result.People.Sum(p => p.Remaining), Is.EqualTo(40));
        Assert.That(other.State.Rows[0].Remaining, Is.EqualTo(900));
    }
    [TestCase(PlanPeriodScale.Week)]
    [TestCase(PlanPeriodScale.Month)]
    public void AggregationSumsHoursAndCapacityInsteadOfAveragingDailyPercentages(PlanPeriodScale scale)
    {
        var d = Document([Row("I1", 4), Row("I2", 4)]);
        var daily = PlanPeople.Calculate(d, Day, Day, PlanPeriodScale.Day, 31).People.Single(p => p.Identity == "U1");
        var report = PlanPeople.Calculate(d, Day, Day, scale, 1);
        var p = report.People.Single(p => p.Identity == "U1").Periods[0];
        Assert.That(p.Planned, Is.EqualTo(8));
        Assert.That(p.Percent, Is.EqualTo(8m / p.Capacity * 100m));
        Assert.That(daily.Periods[0].Overloaded, Is.True);
        Assert.That(p.Overloaded, Is.False);
    }
    [TestCase(false)]
    [TestCase(true)]
    public void FixedMissingDatesOrNoWorkingDaysRemainUnallocated(bool weekend)
    {
        var d = Document([Row("I1", 4) with { Fixed = true, Start = weekend ? Day.AddDays(5) : null, End = weekend ? Day.AddDays(6) : null }]);
        var p = PlanPeople.Calculate(d, Day, Day, PlanPeriodScale.Week, 1).People.Single(p => p.Identity == "U1");
        Assert.That(p.Unallocated, Is.EqualTo(new[] { "I1" }));
        Assert.That(p.Periods[0].Planned, Is.Null);
        Assert.That(p.Missing, Does.Contain(weekend ? "期間内に稼働日なし" : "日程が未入力"));
    }
    [Test]
    public void FixedAndCompletedTasksUseKeptDatesWithoutCountingSummaryTwice()
    {
        var d = Document([Row("I0") with { Estimate = 999, Remaining = 999 }, Row("I1", 12) with { Parent = "I0", Fixed = true, Start = Day, End = Day.AddDays(1) }, Row("I2", 4) with { Closed = true, Start = Day, End = Day }]);
        var p = PlanPeople.Calculate(d, Day, Day, PlanPeriodScale.Day, 2).People.Single(p => p.Identity == "U1");
        Assert.That(p.Remaining, Is.EqualTo(16));
        Assert.That(p.Periods.Select(p => p.Planned), Is.EqualTo(new[] { 6m, 6m }));
        Assert.That(p.Periods[0].Tasks, Is.EqualTo(new[] { "I1" }));
    }
    [TestCase(PlanPeriodScale.Week, false)]
    [TestCase(PlanPeriodScale.Month, false)]
    [TestCase(PlanPeriodScale.Week, true)]
    public void PeriodRetainsKnownOverloadDateAndCausesEvenWhenAverageOrOtherWorkIsUnknown(PlanPeriodScale scale, bool missing)
    {
        var d = Document(missing ? [Row("I1"), Row("I2"), Row("I3", null)] : [Row("I1"), Row("I2")]);
        var before = PlanJson.Text(d);
        var load = PlanPeople.Calculate(d, Day, Day, scale, 1).People.Single(p => p.Identity == "U1").Periods[0];
        Assert.That(load.Overloaded, Is.False);
        Assert.That(load.DailyOverloads, Has.Count.EqualTo(1));
        var overloaded = load.DailyOverloads.Single();
        Assert.That((overloaded.Date, overloaded.Planned, overloaded.Capacity, overloaded.Percent), Is.EqualTo((Day, 8m, 4m, 200m)));
        Assert.That(overloaded.Tasks, Is.EqualTo(new[] { "I1", "I2" }));
        if (missing) Assert.That(load.Planned, Is.Null);
        Assert.That(PlanJson.Text(d), Is.EqualTo(before));
        var reassigned = d with { State = d.State with { Rows = d.State.Rows.Select(r => r.Identity == "I2" ? r with { Assignees = ["U2"] } : r).ToImmutableArray() } };
        Assert.That(PlanPeople.Calculate(reassigned, Day, Day, scale, 1).People.Single(p => p.Identity == "U1").Periods[0].DailyOverloads, Is.Empty);
    }
}
