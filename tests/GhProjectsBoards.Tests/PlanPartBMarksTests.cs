using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;

// #236 part B: hiding a mark is presentation only, and 見込終了 is the derived forecast the column shows.
[TestFixture]
internal sealed class PlanPartBMarksTests
{
    private static readonly DateOnly Day = new(2026, 10, 5);
    private static PlanDocument Document(params PlanRow[] rows) => new(new(new("github.com", 1), "P1"),
        new(rows.ToImmutableArray(), []), new(rows.ToImmutableArray(), new() { StatusDate = Day }));

    [Test]
    public void HidingAMarkLeavesItsSetAndCountIntact()
    {
        var document = Document(new PlanRow("I1", "Late", "a/b") { Start = Day.AddDays(-3), End = Day.AddDays(-3), Remaining = 8 });
        var task = PlanOperations.Schedule(document, Day).Single();
        var late = PlanLateness.Classify([task], new PlanCalendar(), Day);
        var row = document.State.Rows[0];

        Assert.That(PlanSheetText.Indicator(row, task, late.Tasks["I1"], "", false), Does.Contain("期限超過"));
        Assert.That(PlanSheetText.Indicator(row, task, late.Tasks["I1"], "", false, new PlanMarkVisibility(Overdue: false)),
            Does.Not.Contain("期限超過"), "A hidden mark leaves the indicator tooltip.");
        Assert.That(late.OverdueTasks, Is.EqualTo(1), "The set and its count are unaffected by a hidden mark.");
    }

    [Test]
    public void ForecastEndIsTheDerivedForecastAndItsWorkingDaySlip()
    {
        var document = Document(new PlanRow("I1", "Late", "a/b") { Start = Day.AddDays(-3), End = Day.AddDays(-1), Remaining = 24 });
        var task = PlanOperations.Schedule(document, Day).Single();
        var late = PlanLateness.Classify([task], new PlanCalendar(), Day);

        Assert.That(task.ForecastEnd, Is.Not.Null);
        Assert.That(PlanSheetText.ForecastEnd(task, late.Tasks["I1"]),
            Is.EqualTo(PlanSheetText.Date(task.ForecastEnd!.Value) + " +" + late.Tasks["I1"].DaysLater + "日"));
        // The plan end is before the 基準日, so the red 期限超過 mark wins; the slip still shows the forecast.
        Assert.That(late.OverdueTasks, Is.EqualTo(1));
        Assert.That(late.Tasks["I1"].DaysLater, Is.GreaterThan(0));
    }
}
