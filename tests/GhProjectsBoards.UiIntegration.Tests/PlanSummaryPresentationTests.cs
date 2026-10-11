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
}
