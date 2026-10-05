using GhProjectsBoards.Core.Prototypes;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;
[TestFixture]
public sealed class PrototypePlanTests
{
    [Test]
    public void EffortEditMovesSuccessorAndLeavesNextChainUnchanged()
    {
        var plan = new PrototypePlan();
        var next = plan.Rows[10];
        plan.Edit(0, 1, "16");
        Assert.That(plan.Rows[0].End, Is.EqualTo("2026-10-07"));
        Assert.That(plan.Rows[1].Start, Is.EqualTo("2026-10-07"));
        Assert.That(plan.Rows[10], Is.EqualTo(next));
        Assert.That(plan.Rows.Length, Is.EqualTo(1000));
        Assert.That(plan.Rows.Select(r => r.Person).Distinct().Count(), Is.EqualTo(20));
    }
    [TestCase("-1"), TestCase("NaN"), TestCase("Infinity"), TestCase("abc")]
    public void InvalidEffortDoesNotChangePlan(string text)
    {
        var plan = new PrototypePlan(); var before = plan.Rows.ToArray();
        Assert.Throws<ArgumentException>(() => plan.Edit(0, 1, text));
        Assert.That(plan.Rows, Is.EqualTo(before));
    }
    [Test]
    public void TypedDateMovesChainAndTitleStaysLiteral()
    {
        var plan = new PrototypePlan();
        plan.Edit(0, 2, "2026-11-01"); plan.Edit(0, 0, "<script>日本語</script>");
        Assert.That(plan.Rows[1].Start, Is.EqualTo("2026-11-02"));
        Assert.That(plan.Rows[0].Title, Is.EqualTo("<script>日本語</script>"));
    }
}
