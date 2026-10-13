using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class PlanInitialSettingsTests
{
    private static readonly DateOnly Day = new(2026, 10, 13);
    // Every milestone passed here is used by one undated Issue of the Project; Unused adds milestones of another version.
    private static PlanBaseline Baseline(params PlanMilestone[] milestones) => new([
        new("I1", "Later", "a/b") { Start = Day.AddDays(2), End = Day.AddDays(30) },
        new("I2", "Earlier", "a/b") { Start = Day, End = Day.AddDays(10) },
        new("I3", "Undated", "a/b"),
        .. milestones.Select((m, i) => new PlanRow("M" + i, "Milestone task", "a/b") { MilestoneTitle = m.Title })
    ], [new("F", "工程", "SINGLE_SELECT") { Options = ["SA", "P", "PS", "UI"] }]) { Milestones = [.. milestones] };
    private static PlanBaseline Unused(PlanBaseline baseline, params PlanMilestone[] milestones)
        => baseline with { Milestones = [.. baseline.Milestones, .. milestones] };
    private static ProjectPlanSettings Settings() => new() { Columns = [new(PlanField.Phase, "F", "工程", "SINGLE_SELECT")] };

    [Test]
    public void EmptyProjectHasNoPeriodOrMappings()
    {
        var settings = PlanInitialSettings.From(new([], []), new());
        Assert.That(settings.ProjectStart, Is.Null);
        Assert.That(settings.ProjectEnd, Is.Null);
        Assert.That(settings.PhaseMilestones, Is.Empty);
    }

    [Test]
    public void EarliestIssueStartAndLatestMilestoneDueInitializePeriod()
    {
        var settings = PlanInitialSettings.From(Baseline(new("SA 完了", Day.AddDays(5), false, false),
            new("PS 完了", Day.AddDays(20), true, true), new("Undated", null, false, false)), Settings());
        Assert.That(settings.ProjectStart, Is.EqualTo(Day));
        Assert.That(settings.ProjectEnd, Is.EqualTo(Day.AddDays(20)), "Milestone due dates take precedence over later Issue ends.");
    }

    [TestCase(false), TestCase(true)]
    public void NoMilestoneDueFallsBackToLatestIssueEnd(bool undatedMilestone)
    {
        var baseline = undatedMilestone ? Baseline(new PlanMilestone("SA 完了", null, false, false)) : Baseline();
        Assert.That(PlanInitialSettings.From(baseline, Settings()).ProjectEnd, Is.EqualTo(Day.AddDays(30)));
    }

    [TestCase(false, false), TestCase(true, false), TestCase(false, true), TestCase(true, true)]
    public void MissingDatesStayEmptyAndReversedPeriodClearsBoth(bool start, bool end)
    {
        var baseline = Baseline() with { Rows = [new("I", "Task", "a/b") { Start = start ? Day : null, End = end ? Day.AddDays(-1) : null }] };
        var settings = PlanInitialSettings.From(baseline, Settings());
        Assert.That(settings.ProjectStart, Is.EqualTo(start && !end ? Day : (DateOnly?)null));
        Assert.That(settings.ProjectEnd, Is.EqualTo(end && !start ? Day.AddDays(-1) : (DateOnly?)null));
    }

    [TestCase("SA", true), TestCase("sa 完了", true), TestCase("SA-完了", true), TestCase("SA_完了", true),
     TestCase("SA/完了", true), TestCase("SA完了", true), TestCase("第2027.04版 SA 完了", true), TestCase("2027.04-SA", true),
     TestCase("SA2", false), TestCase("SAX", false), TestCase("XSA 完了", false), TestCase("2SA 完了", false)]
    public void PhaseNameMatchesAnywhereAtAsciiLetterDigitBoundary(string title, bool matched)
    {
        var settings = PlanInitialSettings.From(Baseline(new PlanMilestone(title, null, true, false)), Settings());
        Assert.That(settings.PhaseMilestones.GetValueOrDefault("SA"), Is.EqualTo(matched ? title : null));
    }

    [Test]
    public void PrefixesMissingAndAmbiguousGroupsRemainUnmapped()
    {
        var settings = PlanInitialSettings.From(Baseline(new("SA", Day, false, false), new("sa 完了", Day, false, false),
            new("ps 完了", Day, false, false)), Settings());
        Assert.That(settings.PhaseMilestones, Is.EquivalentTo(new Dictionary<string, string> { ["PS"] = "ps 完了" }));
    }

    [Test]
    public void MilestonesNoProjectIssueUsesAreIgnored()
    {
        var baseline = Unused(Baseline(new PlanMilestone("2027.04 SA 完了", Day.AddDays(5), false, false)),
            new("2027.10 PS 完了", Day.AddDays(200), false, false), new("2027.10 UI 完了", Day.AddDays(150), false, false));
        var settings = PlanInitialSettings.From(baseline, Settings());
        Assert.That(settings.ProjectEnd, Is.EqualTo(Day.AddDays(5)), "Another version's later milestone must not become the end.");
        Assert.That(settings.PhaseMilestones, Is.EquivalentTo(new Dictionary<string, string> { ["SA"] = "2027.04 SA 完了" }));
    }

    [Test]
    public void OnlyUnusedMilestonesFallBackToLatestIssueEnd()
        => Assert.That(PlanInitialSettings.From(Unused(Baseline(), new PlanMilestone("SA 完了", Day.AddDays(90), false, false)), Settings()).ProjectEnd,
            Is.EqualTo(Day.AddDays(30)));

    [Test]
    public void UnmappedPhaseFieldDoesNotInferMappings()
        => Assert.That(PlanInitialSettings.From(Baseline(new PlanMilestone("SA", Day, false, false)), new()).PhaseMilestones, Is.Empty);

    [Test]
    public void ExistingSettingsArePreservedWhileEmptySettingsAreInitialized()
    {
        var original = Settings() with { ProjectStart = Day.AddDays(-10), ProjectEnd = Day.AddDays(40),
            PhaseMilestones = ImmutableDictionary<string, string>.Empty.Add("SA", "Chosen") };
        var settings = PlanInitialSettings.From(Baseline(new("SA 完了", Day, false, false), new("PS 完了", Day, false, false)), original);
        Assert.That(settings.ProjectStart, Is.EqualTo(original.ProjectStart));
        Assert.That(settings.ProjectEnd, Is.EqualTo(original.ProjectEnd));
        Assert.That(settings.PhaseMilestones["SA"], Is.EqualTo("Chosen"));
        Assert.That(settings.PhaseMilestones["PS"], Is.EqualTo("PS 完了"));
    }

    [TestCase("2027.04"), TestCase("2027.10")]
    public void EvaluationFirstOpenSettingsPreserveDatesAndZeroUnpublishedTasks(string version)
    {
        var plan = EvaluationFixture.ReadPlan(version);
        var snapshot = EvaluationFixture.Simulate(plan, [plan.StatusDate]).Single();
        var document = snapshot.Document;
        var empty = document.State.Settings with { ProjectStart = null, ProjectEnd = null, PhaseMilestones = ImmutableDictionary<string, string>.Empty };
        var settings = PlanInitialSettings.From(document.Baseline, empty);
        var initialized = document with { State = document.State with { Settings = settings }, Dates = new(PlanDateHoldOrigin.FirstTakeIn) };
        Assert.That(settings.ProjectStart, Is.EqualTo(plan.ProjectStart));
        Assert.That(settings.ProjectEnd, Is.EqualTo(plan.Milestones.Max(m => m.Due)));
        Assert.That(settings.PhaseMilestones, Is.EquivalentTo(plan.Milestones.ToDictionary(m => m.Phase, m => m.Title)));
        Assert.That(PlanOperations.Schedule(initialized, plan.StatusDate).Select(t => (t.Start.Value, t.End.Value)),
            Is.EqualTo(snapshot.Rows.Select(r => (r.Start, r.End))));
        Assert.That(PlanOperations.Changes(initialized, plan.StatusDate).TaskCount, Is.Zero);
        var recalculated = initialized with { Dates = new() };
        Assert.That(PlanOperations.Schedule(recalculated, plan.StatusDate).Select(t => (t.Start.Value, t.End.Value)),
            Is.EqualTo(PlanOperations.Schedule(document, plan.StatusDate).Select(t => (t.Start.Value, t.End.Value))),
            "The derived ProjectStart floor must not move the evaluation schedule.");
    }
}
