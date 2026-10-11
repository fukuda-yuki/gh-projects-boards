using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanReadDateTests
{
    private static readonly DateOnly Day = new(2026, 10, 6);
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");
    private string root = null!;
    [SetUp] public void Setup() => root = Path.Combine(Path.GetTempPath(), "ghpb-read-date-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static PlanDocument Document(bool held = true)
    {
        ImmutableArray<PlanRow> rows = [new("I1", "Summary", "acme/repo") { End = Day.AddDays(-1) },
            new("I2", "Task", "acme/repo") { Parent = "I1", Start = Day.AddDays(-1), End = Day.AddDays(-1), Remaining = 16, Assignees = ["U1"] },
            new("I3", "Undated", "acme/repo")];
        var settings = new ProjectPlanSettings { StatusDate = Day, People = [new("U1", "alice", 100, null)],
            Columns = [new(PlanField.Start, "start", "Start date", "DATE"), new(PlanField.End, "end", "Target date", "DATE")] };
        return new(Project, new(rows, []), new(rows, settings)) { Dates = held ? new(PlanDateHoldOrigin.FirstTakeIn) : new() };
    }
    private static PlanRemoteSnapshot Remote(PlanDocument document) => new(document.Baseline,
        document.Baseline.Rows.ToImmutableDictionary(r => r.Identity, r => "T-" + r.Identity), [], 0, 0);
    private static EditPlanCells Edit(string title) => new(PlanOperationKind.Cell, [new("I2", PlanField.Title, title)]);

    [TestCase(false), TestCase(true)]
    public async Task SettingsImportPreservesReadDateAndZeroUnpublishedOnLaterReopen(bool hasImportedDate)
    {
        var document = Document();
        var row = document.State.Rows[1] with { Parent = null, Start = Day, End = Day, Remaining = 8 };
        document = document with { Baseline = document.Baseline with { Rows = [row] }, State = document.State with { Rows = [row] } };
        var session = await PlanSession.CreateAsync(new(root), document, Day);
        var path = Path.Combine(root, "import-settings.json");
        await PlanStore.ExportSettings(path, document.State.Settings with {
            StatusDate = hasImportedDate ? Day.AddDays(-10) : null, DefaultRepository = "acme/other"
        });
        var result = await session.ImportSettingsAsync(path, Day.AddDays(30));
        Assert.That(result.Applied, Is.True, result.Error);
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(Day));
        Assert.That(session.Document.State.Settings.DefaultRepository, Is.EqualTo("acme/other"));
        Assert.That(session.UndoCount, Is.EqualTo(1));
        Assert.That(session.Document.Dates.Held, Is.Null);
        Assert.That(session.Changes(Day.AddDays(30)).TaskCount, Is.Zero);
        var reopened = await PlanSession.OpenAsync(new(root), Project, Day.AddDays(60));
        Assert.That(reopened.Status, Is.EqualTo(PlanLoadStatus.Loaded));
        Assert.That(reopened.Session!.Document.State.Settings.StatusDate, Is.EqualTo(Day));
        Assert.That(reopened.Session.Changes(Day.AddDays(60)).TaskCount, Is.Zero);
    }

    [Test]
    public void HeldInputErrorKeepsGitHubDatesAndExcludesPeopleLoad()
    {
        var document = Document();
        var rows = document.State.Rows.SetItem(1, document.State.Rows[1] with { Actual = -1, Remaining = 8 });
        document = document with { Baseline = document.Baseline with { Rows = rows }, State = document.State with { Rows = rows } };
        var scheduled = PlanOperations.Schedule(document, Day);
        var task = scheduled.Single(t => t.Input.Identity == "I2");
        var load = PlanPeople.Calculate(document, Day, Day.AddDays(-1), PlanPeriodScale.Day, 1).People.Single(p => p.Identity == "U1");
        Assert.Multiple(() => {
            Assert.That(task.StartReason, Is.EqualTo("入力エラー"));
            Assert.That(task.Warnings, Does.Contain("工数が負の値"));
            Assert.That((task.Start.Value, task.End.Value), Is.EqualTo((rows[1].Start, rows[1].End)));
            Assert.That(scheduled.Single(t => t.Input.Identity == "I1").StartReason, Is.EqualTo("入力エラー"));
            Assert.That(load.Unallocated, Is.EquivalentTo(new[] { "I2" }));
            Assert.That(load.Missing, Does.Contain("入力エラー"));
            Assert.That(load.Periods.Single().Planned, Is.Null);
            Assert.That(load.Periods.Single().Tasks, Is.Empty);
        });
    }

    [Test]
    public async Task DatePickIsOneUndoStepPersistsAndNextRefreshSupersedesItWithoutLosingEdits()
    {
        var original = Document();
        var session = await PlanSession.CreateAsync(new(root), original, Day);
        var picked = Day.AddDays(1);
        await session.Execute(new ReplacePlanSettings(original.State.Settings with { StatusDate = picked }), Day);
        Assert.That(session.UndoCount, Is.EqualTo(1));
        Assert.That(session.Document.Dates, Is.EqualTo(new PlanDateState(null, PlanDateHoldOrigin.FirstTakeIn)));
        var opened = await PlanSession.OpenAsync(new(root), Project, Day.AddDays(10));
        session = opened.Session!;
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(picked));
        await session.Undo(Day);
        Assert.That(session.Document.Dates.Held, Is.EqualTo(PlanDateHoldOrigin.FirstTakeIn));
        Assert.That(session.Changes(Day).TaskCount, Is.Zero);
        await session.Redo(Day);
        await session.Execute(Edit("Local title"), Day);
        await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { StatusDate = Day.AddDays(2), ProjectStart = Day.AddDays(-1) }), Day);
        var refreshDay = Day.AddDays(3);
        Assert.That((await session.AcceptRefresh(Remote(original), refreshDay)).Succeeded, Is.True);
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(refreshDay));
        Assert.That(session.Document.Dates, Is.EqualTo(new PlanDateState()));
        Assert.That(session.UndoCount, Is.EqualTo(2), "Drop the superseded date-only choice but retain other settings and the cell edit.");
        await session.Undo(refreshDay);
        Assert.That(session.Document.State.Settings.ProjectStart, Is.Null);
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(refreshDay));
        await session.Undo(refreshDay);
        Assert.That(session.Document.State.Rows[1].Title, Is.EqualTo("Task"));
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(refreshDay));
        Assert.That(session.Document.Dates, Is.EqualTo(new PlanDateState()));
        Assert.That(session.Schedule(refreshDay)[1].Start.Value, Is.EqualTo(refreshDay));
        await session.Redo(refreshDay);
        await session.Redo(refreshDay);
        Assert.That(session.Document.State.Rows[1].Title, Is.EqualTo("Local title"));
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(refreshDay));
        Assert.That((await PlanSession.OpenAsync(new(root), Project, refreshDay.AddDays(20))).Status, Is.EqualTo(PlanLoadStatus.Loaded));
    }

    [Test]
    public async Task RefreshRebasesRedoAndCannotRestoreSupersededDateOrHold()
    {
        var original = Document();
        var session = await PlanSession.CreateAsync(new(root), original, Day);
        await session.Execute(Edit("Edited"), Day);
        await session.Execute(new ReplacePlanSettings(original.State.Settings with { StatusDate = Day.AddDays(1) }), Day);
        await session.Undo(Day); await session.Undo(Day);
        Assert.That(session.RedoCount, Is.EqualTo(2));
        await session.AcceptRefresh(Remote(original), Day.AddDays(3));
        Assert.That(session.RedoCount, Is.EqualTo(1));
        await session.Redo(Day.AddDays(5));
        Assert.That(session.Document.State.Rows[1].Title, Is.EqualTo("Edited"));
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(Day.AddDays(3)));
        Assert.That(session.Document.Dates, Is.EqualTo(new PlanDateState()));
        await session.Undo(Day.AddDays(5));
        Assert.That(session.Document.Dates.Held, Is.Null);
    }

    [TestCase("date", 1)]
    [TestCase("github", 1)]
    [TestCase("unchanged", 0)]
    public void ChangedTasksCompareDisplayedSchedulesAndExcludeSummaries(string change, int expected)
    {
        var before = Document(false);
        var after = before;
        if (change == "date") after = after with { State = after.State with { Settings = after.State.Settings with { StatusDate = Day.AddDays(1) } } };
        if (change == "github") after = after with { State = after.State with { Rows = after.State.Rows.SetItem(1, after.State.Rows[1] with { Remaining = 24 }) } };
        var changed = PlanOperations.ChangedTaskDates(before, after, Day.AddDays(30));
        Assert.That(changed.Count, Is.EqualTo(expected));
        Assert.That(changed, expected == 0 ? Is.Empty : Is.EquivalentTo(new[] { "I2" }));
    }

    [Test]
    public void HeldDatesDoNotRunAutomaticSchedulingBeforeThePlanningAction()
    {
        var document = Document();
        document = document with { State = document.State with { Settings = document.State.Settings with { StatusDate = DateOnly.MaxValue } } };
        var task = PlanOperations.Schedule(document, DateOnly.MaxValue).Single(t => t.Input.Identity == "I2");
        Assert.That(task.Start.Value, Is.EqualTo(Day.AddDays(-1)));
        Assert.That(task.Warnings, Does.Not.Contain("日程が日付の範囲外"));
        Assert.That(task.PlannedHours, Is.Null);
    }

    [Test]
    public void HeldLatenessAndPeopleLoadUseGitHubDatesWithoutEndReason()
    {
        var document = Document();
        var scheduled = PlanOperations.Schedule(document, Day.AddDays(30));
        var lateness = PlanLateness.Classify(scheduled, document.Baseline, new(), Day);
        Assert.That(lateness.OverdueTasks, Is.EqualTo(1));
        Assert.That(lateness.LaterTasks, Is.Zero);
        Assert.That(lateness.Tasks["I2"].DaysLater, Is.Null);
        Assert.That(scheduled[1].StartReason, Is.EqualTo("GitHub の日付（次の編集で再計算）"));
        Assert.That(PlanLateness.EndReason(scheduled[1], Day), Is.Null);
        var load = PlanPeople.Calculate(document, Day.AddDays(30), Day.AddDays(-1), PlanPeriodScale.Day, 3).People.Single(p => p.Identity == "U1");
        Assert.That(load.Periods.Select(p => p.Planned), Is.EqualTo(new decimal?[] { 16, 0, 0 }));
    }

    [TestCase(false), TestCase(true)]
    public async Task ResolutionReleasesResetHoldAndUndoRestoresIt(bool unavailable)
    {
        var document = Document() with { Dates = new(PlanDateHoldOrigin.Reset) };
        document = document with { Sync = unavailable
            ? document.Sync with { Unavailable = ["I3"] }
            : document.Sync with { Conflicts = [new("I2", PlanField.Title, "\"Task\"", "\"Task\"", "\"Remote\"")] } };
        var session = await PlanSession.CreateAsync(new(root), document, Day);
        if (unavailable) await session.ResolveUnavailable("I3", false, Day);
        else await session.ResolveConflict("I2", PlanField.Title, true, Day);
        Assert.That(session.Document.Dates.Recalculated, Is.EqualTo(PlanDateHoldOrigin.Reset));
        Assert.That(session.UndoCount, Is.EqualTo(1));
        await session.Undo(Day);
        Assert.That(session.Document.Dates.Held, Is.EqualTo(PlanDateHoldOrigin.Reset));
        Assert.That(session.Document.Dates.Recalculated, Is.Null);
        Assert.That(session.Changes(Day).TaskCount, Is.Zero);
        await session.Redo(Day);
        Assert.That(session.Document.Dates.Recalculated, Is.EqualTo(PlanDateHoldOrigin.Reset));
    }

    [Test]
    public async Task NoOpPlanningCommandReleasesHoldAndIsReversibleButRejectedEditDoesNot()
    {
        var session = await PlanSession.CreateAsync(new(root), Document(), Day);
        Assert.ThrowsAsync<ArgumentException>(() => session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Remaining, -1m)]), Day));
        Assert.That(session.Document.Dates.Held, Is.EqualTo(PlanDateHoldOrigin.FirstTakeIn));
        Assert.That(session.UndoCount, Is.Zero);
        await session.Execute(Edit("Task"), Day);
        Assert.That(session.Document.Dates.Held, Is.Null);
        Assert.That(session.UndoCount, Is.EqualTo(1));
        await session.Undo(Day);
        Assert.That(session.Changes(Day).TaskCount, Is.Zero);
        await session.Redo(Day);
        Assert.That(session.Document.Dates.Recalculated, Is.EqualTo(PlanDateHoldOrigin.FirstTakeIn));
    }
}
