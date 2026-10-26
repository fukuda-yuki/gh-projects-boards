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
            Assert.That(result.Milestones.Select(m => m.Milestone.Title), Is.EqualTo(new[] { "Release" }),
                "A milestone no current row uses is not part of this Project's plan.");
            Assert.That(System.Text.Json.JsonSerializer.Serialize(PlanOperations.Schedule(d with { Baseline = d.Baseline with { Milestones = [] } }, Day)), Is.EqualTo(System.Text.Json.JsonSerializer.Serialize(scheduled)));
        });
    }

    [Test]
    public async Task PhaseMappingIsOneUndoStepRoundTripsSettingsAndMovesNoDates()
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
        // The repository catalog holds the other version's group as well; only the group the local row
        // belongs to is reported (#298).
        var remote = d.Baseline with { Milestones = [new("New", Day.AddDays(7), false, false), new("Release", Day, false, false)],
            Rows = [d.Baseline.Rows[0] with { MilestoneTitle = "New" }] };
        var merged = PlanMerge.Merge(d, remote);
        Assert.That(merged.State.Rows[0].MilestoneTitle, Is.EqualTo("New"));
        Assert.That(merged.Baseline.Milestones.Select(m => m.Title), Is.EqualTo(new[] { "New", "Release" }));
        Assert.That(merged.Sync.Conflicts, Is.Empty);
        Assert.That(PlanOperations.Changes(merged, Day).TaskCount, Is.Zero);
        var snapshot = new PlanRemoteSnapshot(remote, ImmutableDictionary<string, string>.Empty, [], 0, 0);
        Assert.That(PlanPublishPlan.Build(merged, snapshot, Day, "milestones").Writes, Is.Empty);
        var mergedMilestone = PlanSummary.CalculateMilestones(merged, PlanOperations.Schedule(merged, Day)).Single();
        Assert.That((mergedMilestone.Milestone.Title, mergedMilestone.Tasks), Is.EqualTo(("New", new[] { "I1" })));
        var locallyChanged = merged with { State = merged.State with { Rows = d.State.Rows } };
        var localMilestone = PlanSummary.CalculateMilestones(locallyChanged, PlanOperations.Schedule(locallyChanged, Day)).Single();
        Assert.That((localMilestone.Milestone.Title, localMilestone.Tasks), Is.EqualTo(("Release", new[] { "I1" })),
            "Summary membership follows the local row, not the read's row.");
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

    [Test]
    public void GroupsOfAnotherVersionInTheSameRepositoryAreNotReported()
    {
        // #298: one repository runs several versions in parallel, each a separate Project. The catalog holds
        // every version's milestones, but only the groups a current plan row belongs to are this Project's.
        var rows = new[] {
            new PlanRow("I1", "SA task", "a/b") { MilestoneTitle = "第2027.04版 SA 完了", Phase = "SA", End = Day.AddDays(4) },
            new PlanRow("I2", "UI task", "a/b") { MilestoneTitle = "第2027.04版 UI 完了", Phase = "UI", End = Day.AddDays(2) } };
        var d = new PlanDocument(new(new("github.com", 1), "P1"),
            new([.. rows], [new("F", "工程", "SINGLE_SELECT") { Options = ["SA", "UI"] }]) {
                Milestones = [new("第2027.04版 SA 完了", Day.AddDays(5), false, false),
                    new("第2027.04版 UI 完了", null, true, false),
                    new("第2027.10版 SA 完了", Day.AddDays(200), true, false),
                    new("第2027.10版 UI 完了", null, false, false)],
                // The repository catalog still validates milestone-cell edits for both versions.
                RepositoryMilestones = [new("a/b", "M1", "第2027.04版 SA 完了"), new("a/b", "M2", "第2027.04版 UI 完了"),
                    new("a/b", "M3", "第2027.10版 SA 完了"), new("a/b", "M4", "第2027.10版 UI 完了")] },
            new([.. rows], new() { StatusDate = Day, Columns = [new(PlanField.Phase, "F", "工程", "SINGLE_SELECT")],
                PhaseMilestones = ImmutableDictionary<string, string>.Empty.Add("SA", "第2027.04版 SA 完了").Add("UI", "第2027.04版 UI 完了") }));
        var report = PlanSummary.Calculate(d, PlanOperations.Schedule(d, Day), Day);
        Assert.Multiple(() => {
            Assert.That(report.Milestones.Select(m => m.Milestone.Title),
                Is.EqualTo(new[] { "第2027.04版 SA 完了", "第2027.04版 UI 完了" }),
                "Unused groups of the other version never appear; the used undated group stays last.");
            Assert.That(report.Phases.Single(p => p.Phase == "SA").Target, Is.EqualTo(Day.AddDays(5)));
            Assert.That(report.Phases.Single(p => p.Phase == "UI").Target, Is.Null, "An undated group has no target.");
            Assert.That(report.Milestones.Single(m => m.Milestone.Title == "第2027.04版 UI 完了").Milestone.Closed, Is.True);
            Assert.That(PlanMilestones.InScope(d).Select(m => m.Title), Is.EqualTo(report.Milestones.Select(m => m.Milestone.Title)),
                "The settings phase-target choices use the same in-scope set as the summary.");
        });
        // Assigning a row to a milestone no current row used makes that group appear; the title it left is
        // no longer used, so its group disappears and its stored phase mapping supplies no target.
        var changed = d with { State = PlanOperations.Apply(d, new EditPlanCells(PlanOperationKind.Cell,
            [new("I1", PlanField.MilestoneTitle, "第2027.10版 SA 完了")]), Day).State };
        var changedReport = PlanSummary.Calculate(changed, PlanOperations.Schedule(changed, Day), Day);
        Assert.Multiple(() => {
            Assert.That(changedReport.Milestones.Select(m => m.Milestone.Title),
                Is.EqualTo(new[] { "第2027.10版 SA 完了", "第2027.04版 UI 完了" }));
            Assert.That(changedReport.Phases.Single(p => p.Phase == "SA").Target, Is.Null,
                "A stored mapping to a group no row uses supplies no target.");
        });
    }
}
