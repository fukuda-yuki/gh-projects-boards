using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class PlanMilestoneTests
{
    private static readonly DateOnly Day = new(2026, 10, 9);
    private static PlanDocument Document(params PlanRow[] rows) => new(new(new("github.com", 1), "P1"),
        new([.. rows], [new("F", "工程", "SINGLE_SELECT") { Options = ["SA", "UI"] }]) {
            Milestones = [new("Release", Day, false, false), new("Empty", null, true, false)] },
        new([.. rows], new() { StatusDate = Day, Columns = [new(PlanField.Phase, "F", "工程", "SINGLE_SELECT")],
            PhaseMilestones = ImmutableDictionary<string, string>.Empty.Add("SA", "Release") }));

    [Test]
    public void MembershipExcludesSummariesAndUsesClosedCalculatedAndKeptFinishesWithoutMovingSchedule()
    {
        var d = Document(new("S", "Summary", "a/b") { MilestoneTitle = "Release" },
            new("C", "Closed", "a/b") { Parent = "S", MilestoneTitle = "Release", Closed = true, CloseDate = Day, End = Day.AddDays(20) },
            new("K", "Kept", "a/b") { MilestoneTitle = "Release", Phase = "SA", Start = Day, End = Day.AddDays(5) },
            // #236: a task with no dates of its own gets none, so the open rows state their plan start.
            new("O", "Open", "a/b") { MilestoneTitle = "Release", Remaining = 16, Start = Day },
            new("N", "Other", "a/b") { Remaining = 80, Start = Day });
        var scheduled = PlanOperations.Schedule(d, Day);
        var result = PlanSummary.Calculate(d, scheduled, Day);
        var m = result.Milestones.Single(m => m.Milestone.Title == "Release");
        Assert.Multiple(() => {
            Assert.That(m.Tasks, Is.EquivalentTo(new[] { "C", "K", "O" }));
            Assert.That(m.Closed, Is.EqualTo(1));
            Assert.That(m.Forecast, Is.EqualTo(Day.AddDays(5)));
            Assert.That(m.Difference, Is.EqualTo(2)); // weekend and Oct 12 holiday excluded
            Assert.That(m.LateTasks, Is.EquivalentTo(new[] { "K", "O" }));
            Assert.That(result.Phases.Single(p => p.Phase == "SA").Target, Is.EqualTo(Day));
            Assert.That(result.Phases.Single(p => p.Phase == "SA").Difference, Is.EqualTo(2));
            Assert.That(result.Phases.Single(p => p.Phase == "UI").Target, Is.Null);
            Assert.That(result.Milestones.Single(m => m.Milestone.Title == "Empty").Forecast, Is.Null);
            Assert.That(result.Milestones.Single(m => m.Milestone.Title == "Empty").Difference, Is.Null);
            Assert.That(System.Text.Json.JsonSerializer.Serialize(PlanOperations.Schedule(d with { Baseline = d.Baseline with { Milestones = [] } }, Day)), Is.EqualTo(System.Text.Json.JsonSerializer.Serialize(scheduled)));
        });
    }

    [Test]
    public async Task PhaseMappingIsOneUndoStepRoundTripsSettingsAndPreservesHeldDates()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-milestone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var document = Document(new PlanRow("I1", "Planned", "a/b") { MilestoneTitle = "Release", Remaining = 80, Start = Day.AddDays(-10), End = Day.AddDays(-5) });
            document = document with { State = document.State with {
                Settings = document.State.Settings with { PhaseMilestones = ImmutableDictionary<string, string>.Empty } } };
            var session = await PlanSession.CreateAsync(new(root), document, Day);
            Assert.That((await session.FlushAsync()).Succeeded, Is.True);
            var dates = session.Schedule(Day).Select(t => (t.Start, t.End)).ToArray();
            var unpublished = session.Changes(Day).TaskCount;
            var save = await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with {
                PhaseMilestones = ImmutableDictionary<string, string>.Empty.Add("SA", "Release") }), Day);
            Assert.That(session.UndoCount, Is.EqualTo(1));
            Assert.That(session.Schedule(Day).Select(t => (t.Start, t.End)), Is.EqualTo(dates));
            Assert.That(session.Changes(Day).TaskCount, Is.EqualTo(unpublished));
            Assert.That(save.Succeeded, Is.True, save.Error);
            var path = Path.Combine(root, "settings.json");
            await session.ExportSettingsAsync(path);
            await session.Undo(Day);
            Assert.That(session.Document.State.Settings.PhaseMilestones, Is.Empty);
            Assert.That((await session.ImportSettingsAsync(path, Day)).Applied, Is.True);
            Assert.That(session.Document.State.Settings.PhaseMilestones["SA"], Is.EqualTo("Release"));
            Assert.That(session.UndoCount, Is.EqualTo(1));
            Assert.That(save.Recalculation, Is.Null, "A milestone mapping change moves no dates.");
        } finally { Directory.Delete(root, true); }
    }

    [Test]
    public void UneditedMilestoneMembershipAdoptsRemoteAndSummaryUsesLocalMembership()
    {
        var d = Document(new PlanRow("I1", "Kept", "a/b") { MilestoneTitle = "Release", End = Day });
        var remote = d.Baseline with { Milestones = [new("New", Day.AddDays(7), false, false)],
            Rows = [d.Baseline.Rows[0] with { MilestoneTitle = "New" }] };
        var merged = PlanMerge.Merge(d, remote);
        Assert.That(merged.State.Rows[0].MilestoneTitle, Is.EqualTo("New"));
        Assert.That(merged.Baseline.Milestones.Select(m => m.Title), Is.EqualTo(new[] { "New" }));
        Assert.That(merged.Sync.Conflicts, Is.Empty);
        Assert.That(PlanOperations.Changes(merged, Day).TaskCount, Is.Zero);
        var snapshot = new PlanRemoteSnapshot(remote, ImmutableDictionary<string, string>.Empty, [], 0, 0);
        Assert.That(PlanPublishPlan.Build(merged, snapshot, Day, "milestones").Writes, Is.Empty);
        var locallyChanged = merged with { State = merged.State with { Rows = d.State.Rows } };
        Assert.That(PlanSummary.CalculateMilestones(locallyChanged, PlanOperations.Schedule(locallyChanged, Day))[0].Tasks, Is.Empty);
        Assert.That(PlanOperations.Changes(locallyChanged, Day).Fields["I1"], Does.Contain(PlanField.MilestoneTitle));
    }

    [Test]
    public void MilestoneAndPhaseDifferenceUseImportedAndCompanyHolidaysAndKeepUnknownMappingsEmpty()
    {
        var d = Document(new PlanRow("I1", "Kept", "a/b") { MilestoneTitle = "Release", Phase = "SA", End = Day.AddDays(6) });
        d = d with { State = d.State with { Settings = d.State.Settings with {
            CompanyDaysOff = [Day.AddDays(4)],
            ImportedHolidays = new("test", "test", new string('a', 64), DateTimeOffset.UnixEpoch, 2026, 2026, [new(Day.AddDays(5), "Holiday")]),
            PhaseMilestones = d.State.Settings.PhaseMilestones.Add("UI", "Missing") } } };
        var report = PlanSummary.Calculate(d, PlanOperations.Schedule(d, Day), Day);
        Assert.That(report.Milestones[0].Difference, Is.EqualTo(1));
        Assert.That(report.Phases.Single(p => p.Phase == "SA").Difference, Is.EqualTo(1));
        Assert.That(report.Phases.Single(p => p.Phase == "UI").Target, Is.Null);
    }

    [TestCase(-1, -1), TestCase(0, 0), TestCase(4, 1)]
    public void DifferenceIsSignedWorkingDays(int offset, int expected)
    {
        var d = Document(new PlanRow("K", "Kept", "a/b") { MilestoneTitle = "Release", End = Day.AddDays(offset) });
        var m = PlanSummary.Calculate(d, PlanOperations.Schedule(d, Day), Day).Milestones[0];
        Assert.That(m.Difference, Is.EqualTo(expected));
        Assert.That(m.LateTasks.Count, Is.EqualTo(offset > 0 ? 1 : 0));
    }

    [Test]
    public void MissingTargetAndUnknownFinishDoNotInventForecastOrDifference()
    {
        var d = Document(new PlanRow("K", "Unknown", "a/b") { MilestoneTitle = "Release" },
            new("N", "Undated target", "a/b") { MilestoneTitle = "Empty", End = Day });
        var result = PlanSummary.Calculate(d, PlanOperations.Schedule(d, Day), Day);
        Assert.That(result.Milestones[0].Forecast, Is.Null);
        Assert.That(result.Milestones.All(m => m.Difference is null && m.LateTasks.Count == 0), Is.True);
    }
}
