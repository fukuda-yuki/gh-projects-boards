using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;

// #230: the plan's selection line shows the selected task's assignee load for the task's weeks while
// reassigning, so the PMO sees the effect without opening サマリー.
[TestFixture]
internal sealed class PlanLoadTests
{
    private static readonly DateOnly Day = new(2026, 10, 5); // Monday
    private static PlanRow Row(string id, decimal? remaining = 4, string assignee = "U1") => new(id, id, "acme/repo") {
        Assignees = assignee.Length == 0 ? [] : [assignee], Estimate = 4, Actual = 0, Remaining = remaining, Start = Day };
    private static PlanDocument Document(params PlanRow[] rows)
    {
        var settings = new ProjectPlanSettings { StatusDate = Day, People = [new("U1", "person-U1", 50, null), new("U2", "person-U2", 100, null)] };
        var schedule = PlanScheduler.Calculate(rows.Select((r, i) => PlanOperations.TaskInput(r, i)).ToArray(),
            new PlanSettings { StatusDate = Day, ProjectStart = Day, People = settings.People.Select(p => new PlanPerson(p.Identity, p.Rate)).ToArray() },
            Day, rows.Select(r => r.Identity).ToHashSet(StringComparer.Ordinal));
        var byId = schedule.ToDictionary(t => t.Input.Identity);
        var placed = rows.Select(r => r with {
            Start = byId[r.Identity].Start.Value ?? r.Start, End = byId[r.Identity].End.Value ?? r.End }).ToImmutableArray();
        return new(new(new("github.com", 42), "P1"), new(placed, []), new(placed, settings));
    }
    private static PlanAssigneeLoad? Load(PlanDocument d, string task) => PlanLoad.ForTask(d, PlanOperations.Schedule(d, Day), task);

    [Test]
    public void TaskWeeksNameTheAssigneesLoadAndTheDaysOverTheCap()
    {
        // 50% means a 4 h cap; two same-day tasks plan 8 h, so the week reads 40% (8/20 h) while the
        // single day is over the cap at 200%. The weekly average must not hide the day (#230).
        var d = Document(Row("I1"), Row("I2"));
        var load = Load(d, "I1")!;
        Assert.That(load.Identity, Is.EqualTo("U1"));
        Assert.That(load.Name, Is.EqualTo("person-U1"));
        Assert.That(load.Periods, Has.Count.EqualTo(1));
        var week = load.Periods[0];
        Assert.That((week.Start, week.End), Is.EqualTo((Day, Day.AddDays(6))));
        Assert.That(week.Percent, Is.EqualTo(40m));
        Assert.That(week.Overloaded, Is.True);
        Assert.That((week.DaysOverCap, week.MaxDailyPercent, week.MaxDate), Is.EqualTo((1, (decimal?)200, (DateOnly?)Day)));
    }

    [Test]
    public void LongTaskSpansOneWeekEntryPerWeekItLoads()
    {
        // 32 h at a 4 h daily cap is eight working days: the first week holds 20 h of 20 h and the
        // second 12 h of 20 h, so 100% and 60% (the bundled 10/12 holiday is not a working day).
        var d = Document(Row("I1", 32));
        var load = Load(d, "I1")!;
        Assert.That(load.Periods.Select(p => (p.Start, p.Percent)), Is.EqualTo(new (DateOnly, decimal?)[] {
            (new(2026, 10, 5), 100m), (new(2026, 10, 12), 75m) }));
        Assert.That(load.Periods.Select(p => p.DaysOverCap), Is.EqualTo(new[] { 0, 0 }));
    }

    [Test]
    public void NoSingleAssigneeClosedOrUndatedWorkShowsNoLoad()
    {
        var unassigned = Document(Row("I1", 4, assignee: ""));
        Assert.That(Load(unassigned, "I1"), Is.Null);
        var multiple = Document(Row("I1") with { Assignees = ["U1", "U2"] });
        Assert.That(Load(multiple, "I1"), Is.Null);
        var closed = Document(Row("I1") with { Closed = true, CloseDate = Day });
        Assert.That(Load(closed, "I1"), Is.Null);
        var undated = Document(Row("I1") with { Start = null, Estimate = null, Remaining = null });
        Assert.That(Load(undated, "I1"), Is.Null);
        Assert.That(Load(Document(Row("I1")), "missing"), Is.Null);
    }

    [Test]
    public void ReassigningTheTaskChangesTheLoadTextWithoutChangingTheSchedule()
    {
        // The whole point of the band: an assignee change is visible in the load, and the plan's dates
        // stay put. The old assignee keeps the task until the edit is accepted; afterwards the task
        // leaves their weeks.
        var d = Document(Row("I1"), Row("I2"));
        var before = PlanJson.Text(d);
        var old = Load(d, "I1")!;
        Assert.That(old.Periods[0].Percent, Is.EqualTo(40m));
        var reassigned = d with { State = d.State with { Rows = d.State.Rows.Select(r => r.Identity == "I1" ? r with { Assignees = ["U2"] } : r).ToImmutableArray() } };
        var next = Load(reassigned, "I1")!;
        Assert.That(next.Identity, Is.EqualTo("U2"));
        Assert.That(next.Periods[0].Percent, Is.EqualTo(10m), "person-U2 works at 100%, so the one remaining 4 h week is 10% of 40 h.");
        Assert.That(Load(reassigned with { State = reassigned.State with { Rows = reassigned.State.Rows.Select(r => r.Identity == "I2" ? r with { Assignees = ["U2"] } : r).ToImmutableArray() } }, "I1")!.Identity,
            Is.EqualTo("U2"));
        Assert.That(PlanJson.Text(d), Is.EqualTo(before));
    }
}

