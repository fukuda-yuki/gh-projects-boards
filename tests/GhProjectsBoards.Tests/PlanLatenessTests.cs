using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class PlanLatenessTests
{
    [Test]
    public void SummaryLatenessUsesItsOwnPublishedEnd()
    {
        var calendar = new PlanCalendar();
        var tasks = PlanScheduler.Calculate([
            new("summary", 1),
            new("child", 2) { Parent = "summary", Estimate = 8, Remaining = 8, Assignees = ["U1"] }
        ], new() { StatusDate = new(2026, 10, 5), People = [new("U1")] }, new(2026, 10, 5));
        Assert.That(PlanScheduler.PublishedEndLateness(new(2026, 10, 1), tasks.Single(t => t.Input.Identity == "summary").End.Value, calendar), Is.EqualTo(2));
        Assert.That(PlanScheduler.PublishedEndLateness(new(2026, 10, 2), tasks.Single(t => t.Input.Identity == "child").End.Value, calendar), Is.EqualTo(1));
    }
    [TestCase("2026-10-09", "2026-10-09", null, false, null)]
    [TestCase("2026-10-09", "2026-10-08", null, false, null)]
    [TestCase(null, "2026-10-09", null, false, null)]
    [TestCase("2026-10-09", null, null, false, null)]
    [TestCase("2026-10-02", "2026-10-05", null, false, 1)]
    [TestCase("2026-10-09", "2026-10-13", null, false, 1)]
    [TestCase("2026-10-05", "2026-10-07", "2026-10-06", false, 1)]
    [TestCase("2026-10-05", "2026-10-07", "2026-10-06", true, 1)]
    public void LatenessCountsOnlyProjectWorkingDays(string? published, string? calculated, string? dayOff, bool imported, int? expected)
    {
        var calendar = new PlanCalendar();
        if (dayOff is not null)
            calendar = imported ? calendar with { ImportedHolidays = new("test", "test", "test", DateTimeOffset.UnixEpoch, 2026, 2026, [new(DateOnly.Parse(dayOff), "holiday")]) }
                : calendar with { CompanyDaysOff = new HashSet<DateOnly> { DateOnly.Parse(dayOff) } };
        Assert.That(PlanScheduler.PublishedEndLateness(published is null ? null : DateOnly.Parse(published),
            calculated is null ? null : DateOnly.Parse(calculated), calendar), Is.EqualTo(expected));
    }
}
