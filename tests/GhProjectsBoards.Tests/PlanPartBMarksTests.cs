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

        Assert.That(PlanSheetText.Indicator(PlanTaskNames.Of(document), row, task, late.Tasks["I1"], "", false), Does.Contain("期限超過"));
        Assert.That(PlanSheetText.Indicator(PlanTaskNames.Of(document), row, task, late.Tasks["I1"], "", false, new PlanMarkVisibility(Overdue: false)),
            Does.Not.Contain("期限超過"), "A hidden mark leaves the indicator tooltip.");
        Assert.That(late.OverdueTasks, Is.EqualTo(1), "The set and its count are unaffected by a hidden mark.");
    }

    [Test]
    public void AHiddenMarkLeavesRequirementRowsAndTheOtherMarksAlone()
    {
        var requirement = new PlanRow("I1", "要求事項", "acme/repo");
        var overdue = new PlanRow("I2", "遅れ", "acme/repo") { Parent = "I1", Start = Day.AddDays(-3), End = Day.AddDays(-3), Remaining = 8 };
        var later = new PlanRow("I3", "遅れ予定", "acme/repo") { Parent = "I1", Start = Day, End = Day, Estimate = 8, Remaining = 16 };
        var document = Document(requirement, overdue, later);
        var schedule = PlanOperations.Schedule(document, Day);
        var tasks = schedule.ToDictionary(t => t.Input.Identity);
        var lateClassification = PlanLateness.Classify(schedule, new PlanCalendar(), Day);
        string Indicator(string identity, PlanMarkVisibility? marks = null) => PlanSheetText.Indicator(PlanTaskNames.Of(document),
            document.State.Rows.Single(r => r.Identity == identity), tasks[identity], lateClassification.Tasks[identity], "", false, marks);

        Assert.That(Indicator("I1"), Does.Contain("配下に期限超過 1").And.Contain("予定より遅れ 1").And.Contain("見積超過 1"));
        Assert.That(Indicator("I1", new PlanMarkVisibility(Overdue: false)), Does.Not.Contain("期限超過").And.Contain("予定より遅れ 1").And.Contain("見積超過 1"),
            "Hiding 期限超過 leaves the requirement row's other marks alone.");
        Assert.That(Indicator("I1", new PlanMarkVisibility(Later: false)), Does.Not.Contain("予定より遅れ").And.Contain("期限超過 1").And.Contain("見積超過 1"));
        Assert.That(Indicator("I1", new PlanMarkVisibility(OverEstimate: false)), Does.Not.Contain("見積超過").And.Contain("期限超過 1").And.Contain("予定より遅れ 1"));
        Assert.That(Indicator("I3"), Does.Contain("見積超過"), "The row's own over-estimate mark is shown by default.");
        Assert.That(Indicator("I3", new PlanMarkVisibility(OverEstimate: false)), Does.Not.Contain("見積超過"), "A hidden 見積超過 mark leaves the row tooltip.");
        Assert.That(lateClassification.OverdueTasks + lateClassification.OverEstimateTasks, Is.GreaterThan(0), "The sets stay complete.");
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
