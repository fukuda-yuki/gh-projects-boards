using System.Globalization;
using GhProjectsBoards.App;
using NUnit.Framework;
namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, Parallelizable(ParallelScope.All)]
internal sealed class PlanSummaryPresentationTests
{
    [TestCase(null, "—")]
    [TestCase("0", "0%")]
    [TestCase("0.0049", "0%")]
    [TestCase("0.005", "1%")]
    [TestCase("0.8653", "87%")]
    [TestCase("0.2112", "21%")]
    [TestCase("0.924", "92%")]
    [TestCase("0.975", "98%")]
    [TestCase("1", "100%")]
    [TestCase("1.004", "101%")]
    [TestCase("1.00000001", "101%")]
    [TestCase("1.1655", "117%")]
    [TestCase("1.161", "117%")]
    public void SummaryPercentagesAreWholeAndNeverHideAnOverload(string? ratio, string expected)
    {
        var value = ratio is null ? (decimal?)null : decimal.Parse(ratio, CultureInfo.InvariantCulture);
        Assert.That(PlanSummaryPresentation.Percent(value), Is.EqualTo(expected));
    }

    // A phase with 119 of 120 tasks closed must not read 100%: progress reaches 100% only when every task it
    // counts is closed, however little work the open ones have left.
    [TestCase("0.996", false, "99%")]
    [TestCase("1", false, "99%")]
    [TestCase("1", true, "100%")]
    [TestCase("0.996", true, "100%")]
    [TestCase("0.5", false, "50%")]
    [TestCase(null, false, "—")]
    public void ProgressReadsOneHundredPercentOnlyWhenEveryTaskIsClosed(string? ratio, bool allClosed, string expected)
    {
        var value = ratio is null ? (decimal?)null : decimal.Parse(ratio, CultureInfo.InvariantCulture);
        Assert.That(PlanSummaryPresentation.Progress(value, allClosed), Is.EqualTo(expected));
    }
}
