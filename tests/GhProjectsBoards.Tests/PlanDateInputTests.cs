using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class PlanDateInputTests
{
    [TestCase("2026/10/13", 10, 13), TestCase("2026/1/5", 1, 5), TestCase("2026-10-13", 10, 13)]
    public void FullDateFormatsAcceptCalendarDates(string text, int month, int day)
    {
        Assert.That(PlanDateInput.TryParse(text, out var date), Is.True);
        Assert.That(date, Is.EqualTo(new DateOnly(2026, month, day)));
        Assert.That(PlanDateInput.Parse(text), Is.EqualTo(date));
    }

    [TestCase("13/10/2026"), TestCase("2026-13-01"), TestCase("2026/2/29"), TestCase("10/13")]
    public void InvalidOrYearlessSettingsDatesAreRejected(string text)
    {
        Assert.That(PlanDateInput.TryParse(text, out _), Is.False);
        Assert.That(() => PlanDateInput.Parse(text), Throws.TypeOf<FormatException>());
    }
}
