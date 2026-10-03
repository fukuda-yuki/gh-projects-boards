using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanningWarningPresentationTests
{
    private static PlanningInput Input(string id, bool breakdown = false, params PlanningLink[] links)
        => new(new(id, PlanningMode.Auto, "U1", Contributions: breakdown ? null : [new("U1", 4, null)],
            Assignment: new(["U1"], true, false)), 4, null, ["U1"], links);
    private static AdoptedPlan Plan(params PlanningInput[] inputs) => PlanningEngine.Calculate(PlanningPathTests.Plan(), inputs, 23);

    [TestCase("under", "EffortBreakdown", "見積の未割当: 2人時")]
    [TestCase("over", "ContributionInconsistency", "見積の内訳が合計を超えています。")]
    [TestCase("actual", "ActualReport", "実績合計の内訳・報告対象日が未入力です。")]
    [TestCase("actual-zero", "ActualReport", "実績合計の内訳・報告対象日が未入力です。")]
    public void EffortCausesDistinguishOptionalAllocationFromEnteredInconsistencyAndRequiredReport(string condition, string impact, string warning)
    {
        var input = Input("A");
        input = condition switch {
            "under" => input with { Task = input.Task with { Contributions = [new("U1", 2, null)] } },
            "over" => input with { Task = input.Task with { Contributions = [new("U1", 5, null)] } },
            _ => input with { ActualTotal = condition == "actual-zero" ? 0 : 3 }
        };
        var inputsBefore = JsonSerializer.Serialize(input);
        var plan = Plan(input);
        var planBefore = JsonSerializer.Serialize(plan);

        var presentation = PlanningWarningPresentation.Create(plan, "A");

        Assert.Multiple(() => {
            Assert.That(presentation.Causes.Single().Impact.ToString(), Is.EqualTo(impact));
            Assert.That(presentation.Causes.Single().Message, Is.EqualTo(warning));
            Assert.That(presentation.HasScheduleAttention, Is.False, "These data checks do not invalidate calculated dates.");
            Assert.That(presentation.HasDataAttention, Is.EqualTo(condition != "under"));
            Assert.That(plan.Tasks.Single().Warnings, Is.EqualTo(new[] { warning }), "Preserve the existing warning contract.");
            Assert.That(plan.Tasks.Single().Start, Is.EqualTo(PlanningContractTests.At("2026-10-05 09:00")));
            Assert.That(plan.Tasks.Single().Finish, Is.EqualTo(PlanningContractTests.At("2026-10-05 13:00")));
            Assert.That(JsonSerializer.Serialize(input), Is.EqualTo(inputsBefore), "Do not fill or rebalance contributions.");
            Assert.That(JsonSerializer.Serialize(plan), Is.EqualTo(planBefore));
        });
    }

    [Test]
    public void ExplicitUnattributedActualReportIsRetainedWithoutInventingMissingAttribution()
    {
        var input = Input("A");
        input = input with { ActualTotal = 3, Task = input.Task with { Actuals = [new(null, 3, new(2026, 10, 9))] } };
        var before = JsonSerializer.Serialize(input);

        var plan = Plan(input);

        Assert.That(PlanningWarningPresentation.Create(plan, "A").Causes, Is.Empty);
        Assert.That(JsonSerializer.Serialize(input), Is.EqualTo(before));
    }

    [Test]
    public void MixedDataCausesKeepExactUpstreamSourceAcrossAChainAndDiamond()
    {
        var input = Input("A");
        input = input with { ActualTotal = 3, Task = input.Task with { Contributions = [new("U1", 5, null)] } };
        var plan = Plan(input, Input("B", false, new PlanningLink("A")), Input("C", false, new PlanningLink("A")),
            Input("D", false, new PlanningLink("B"), new PlanningLink("C")));
        var before = JsonSerializer.Serialize(plan);

        var presentation = PlanningWarningPresentation.Create(plan, "D");

        Assert.That(presentation.Causes.Select(cause => cause.Impact.ToString()), Is.EquivalentTo(new[] { "ActualReport", "ContributionInconsistency" }));
        Assert.That(presentation.Causes.Select(cause => cause.SourceTaskId), Is.All.EqualTo("A"));
        Assert.That(presentation.Causes.Select(cause => cause.Inherited), Is.All.True);
        Assert.That(presentation.HasScheduleAttention, Is.False);
        Assert.That(presentation.HasDataAttention, Is.True);
        Assert.That(JsonSerializer.Serialize(plan), Is.EqualTo(before));
    }

    [Test]
    public void OptionalPredecessorBreakdownKeepsItsSourceWithoutScheduleAttention()
    {
        var plan = Plan(Input("A", true), Input("B", false, new PlanningLink("A")));
        var before = JsonSerializer.Serialize(plan);

        var presentation = PlanningWarningPresentation.Create(plan, "B");

        Assert.That(presentation.HasScheduleAttention, Is.False);
        Assert.That(presentation.Causes, Has.Length.EqualTo(1));
        Assert.That(presentation.Causes.Single(), Is.EqualTo(new PlanningWarningCause("A", "見積の未割当: 4人時", PlanningWarningImpact.EffortBreakdown, true)));
        Assert.That(plan.Tasks.Single(task => task.Id == "B").Warnings, Is.EqualTo(new[] { "先行 A に警告があります。" }),
            "The engine's existing warning output is preserved for other consumers.");
        Assert.That(JsonSerializer.Serialize(plan), Is.EqualTo(before));
    }

    [Test]
    public void RealUpstreamScheduleAndOptionalCausesAreDeduplicatedAcrossAChainAndDiamond()
    {
        var first = Input("A", true);
        first = first with { Task = first.Task with { Deadline = PlanningContractTests.At("2026-10-05 10:00") } };
        var plan = Plan(first, Input("B", false, new PlanningLink("A")), Input("C", false, new PlanningLink("A")),
            Input("D", false, new PlanningLink("B"), new PlanningLink("C")));
        var before = JsonSerializer.Serialize(plan);

        var presentation = PlanningWarningPresentation.Create(plan, "D");

        Assert.That(presentation.HasScheduleAttention, Is.True);
        Assert.That(presentation.Causes, Has.Length.EqualTo(2));
        Assert.That(presentation.Causes.Select(cause => cause.SourceTaskId), Is.All.EqualTo("A"));
        Assert.That(presentation.Causes.Select(cause => cause.Inherited), Is.All.True);
        Assert.That(presentation.Causes.Single(cause => cause.Impact == PlanningWarningImpact.Schedule).Message,
            Is.EqualTo("期限を超えています。工数と依存関係は保持しています。"));
        Assert.That(presentation.Causes.Single(cause => cause.Impact == PlanningWarningImpact.EffortBreakdown).Message,
            Is.EqualTo("見積の未割当: 4人時"));
        Assert.That(JsonSerializer.Serialize(plan), Is.EqualTo(before));
    }

    [Test]
    public void AdoptedManualPredecessorProblemKeepsItsExactSourceAndMessage()
    {
        var first = Input("A");
        first = first with { Task = first.Task with { Mode = PlanningMode.Manual,
            ManualStart = PlanningContractTests.At("2026-10-05 09:00"), ManualFinish = PlanningContractTests.At("2026-10-05 10:00") },
            SourceProblem = "Observed source is unavailable." };
        var plan = Plan(first, Input("B", false, new PlanningLink("A")));

        var presentation = PlanningWarningPresentation.Create(plan, "B");

        Assert.That(presentation.HasScheduleAttention, Is.True);
        Assert.That(presentation.Causes.Count(cause => cause.SourceTaskId == "A" && cause.Message == "Observed source is unavailable."), Is.EqualTo(1));
        Assert.That(presentation.Causes.Single(cause => cause.Message == "Observed source is unavailable.").Impact, Is.EqualTo(PlanningWarningImpact.Schedule));
    }

    [TestCase("legacy"), TestCase("stale"), TestCase("missing"), TestCase("cycle")]
    public void UnknownOrUnverifiableWarningOriginsRetainCautionWithoutGuessing(string condition)
    {
        var plan = Plan(Input("A", true), Input("B", false, new PlanningLink("A")));
        var first = plan.Tasks.Single(task => task.Id == "A");
        first = condition switch {
            "legacy" => first with { WarningDetails = null },
            "stale" => first with { SourceRevision = plan.SourceRevision - 1 },
            "cycle" => first with { Warnings = ["Recorded upstream notice"], WarningDetails = [new("Recorded upstream notice", PlanningWarningKind.Inherited, "B")] },
            _ => first
        };
        plan = plan with { Tasks = condition == "missing" ? plan.Tasks.Where(task => task.Id != "A").ToArray()
            : plan.Tasks.Select(task => task.Id == "A" ? first : task).ToArray() };
        var before = JsonSerializer.Serialize(plan);

        var presentation = PlanningWarningPresentation.Create(plan, "B");

        Assert.That(presentation.HasScheduleAttention, Is.True);
        Assert.That(presentation.Causes.Any(cause => cause.Impact == PlanningWarningImpact.Unknown), Is.True);
        Assert.That(presentation.Causes.Any(cause => cause.Impact == PlanningWarningImpact.EffortBreakdown), Is.False);
        Assert.That(JsonSerializer.Serialize(plan), Is.EqualTo(before));
    }
}
