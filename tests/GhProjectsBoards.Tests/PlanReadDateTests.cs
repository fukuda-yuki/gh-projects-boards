using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// The 基準日 a read records, and the settings/history behaviour around it. The date hold and its release
// notice are removed (#236): a read sets the 基準日 and leaves every plan date alone.
[TestFixture]
internal sealed class PlanReadDateTests
{
    private static readonly DateOnly Day = new(2026, 10, 6);
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");
    private string root = null!;
    [SetUp] public void Setup() => root = Path.Combine(Path.GetTempPath(), "ghpb-read-date-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static PlanDocument Document()
    {
        ImmutableArray<PlanRow> rows = [new("I1", "Summary", "acme/repo") { Start = Day, End = Day.AddDays(1) },
            new("I2", "Task", "acme/repo") { Parent = "I1", Start = Day, End = Day.AddDays(1), Remaining = 16, Assignees = ["U1"] },
            new("I3", "Undated", "acme/repo")];
        var settings = new ProjectPlanSettings { StatusDate = Day, People = [new("U1", "alice", 100, null)],
            Columns = [new(PlanField.Start, "start", "Start date", "DATE"), new(PlanField.End, "end", "Target date", "DATE")] };
        return new(Project, new(rows, []), new(rows, settings));
    }
    private static PlanRemoteSnapshot Remote(PlanDocument document) => new(document.Baseline,
        document.Baseline.Rows.ToImmutableDictionary(r => r.Identity, r => "T-" + r.Identity), [], 0, 0);
    private static EditPlanCells Edit(string title) => new(PlanOperationKind.Cell, [new("I2", PlanField.Title, title)]);

    [TestCase(false), TestCase(true)]
    public async Task SettingsImportPreservesReadDateAndZeroUnpublishedOnLaterReopen(bool hasImportedDate)
    {
        var document = Document();
        var row = document.State.Rows[1] with { Parent = null };
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
        Assert.That(session.Changes(Day.AddDays(30)).TaskCount, Is.Zero);
        var reopened = await PlanSession.OpenAsync(new(root), Project, Day.AddDays(60));
        Assert.That(reopened.Status, Is.EqualTo(PlanLoadStatus.Loaded));
        Assert.That(reopened.Session!.Document.State.Settings.StatusDate, Is.EqualTo(Day));
        Assert.That(reopened.Session.Changes(Day.AddDays(60)).TaskCount, Is.Zero);
    }

    [Test]
    public void InputErrorKeepsPlanDatesAndExcludesPeopleLoad()
    {
        var document = Document();
        var rows = document.State.Rows.SetItem(1, document.State.Rows[1] with
            { Actual = -1, Remaining = 8, Start = Day.AddDays(-1), End = Day.AddDays(-1) });
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
        var opened = await PlanSession.OpenAsync(new(root), Project, Day.AddDays(10));
        session = opened.Session!;
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(picked));
        await session.Undo(Day);
        Assert.That(session.Changes(Day).TaskCount, Is.Zero);
        await session.Redo(Day);
        await session.Execute(Edit("Local title"), Day);
        await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { StatusDate = Day.AddDays(2), ProjectStart = Day.AddDays(-1) }), Day);
        var refreshDay = Day.AddDays(3);
        Assert.That((await session.AcceptRefresh(Remote(original), refreshDay)).Succeeded, Is.True);
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(refreshDay));
        Assert.That(session.UndoCount, Is.EqualTo(2), "Drop the superseded date-only choice but retain other settings and the cell edit.");
        await session.Undo(refreshDay);
        Assert.That(session.Document.State.Settings.ProjectStart, Is.Null);
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(refreshDay));
        await session.Undo(refreshDay);
        Assert.That(session.Document.State.Rows[1].Title, Is.EqualTo("Task"));
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(refreshDay));
        await session.Redo(refreshDay);
        await session.Redo(refreshDay);
        Assert.That(session.Document.State.Rows[1].Title, Is.EqualTo("Local title"));
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(refreshDay));
        Assert.That((await PlanSession.OpenAsync(new(root), Project, refreshDay.AddDays(20))).Status, Is.EqualTo(PlanLoadStatus.Loaded));
    }

    [Test]
    public async Task RefreshRebasesRedoAndCannotRestoreASupersededDate()
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
        await session.Undo(Day.AddDays(5));
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(Day.AddDays(3)));
    }

    [Test]
    public void PlanDatesFeedLatenessAndPeopleLoadAfterARead()
    {
        var document = Document();
        var scheduled = PlanOperations.Schedule(document, Day.AddDays(30));
        var lateness = PlanLateness.Classify(scheduled, new(), Day);
        Assert.That(lateness.OverdueTasks, Is.Zero);
        Assert.That(lateness.LaterTasks, Is.Zero);
        Assert.That(scheduled[1].StartReason, Is.EqualTo("開始日を維持"), "The plan keeps its own start.");
        Assert.That(PlanLateness.EndReason(scheduled[1]), Is.EqualTo("終了: 開始から 16h"));
        var load = PlanPeople.Calculate(document, Day, Day, PlanPeriodScale.Day, 3).People.Single(p => p.Identity == "U1");
        Assert.That(load.Periods.Select(p => p.Planned), Is.EqualTo(new decimal?[] { 8, 8, 0 }),
            "16h at 100% covers the plan's two working days.");
    }

    [Test]
    public async Task ARejectedEditChangesNothingAndANoOpKeepsHistory()
    {
        var session = await PlanSession.CreateAsync(new(root), Document(), Day);
        var before = PlanJson.Text(session.Document);
        Assert.ThrowsAsync<ArgumentException>(() => session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Remaining, -1m)]), Day));
        Assert.That(session.UndoCount, Is.Zero);
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
        await session.Execute(Edit("Task"), Day);
        Assert.That(session.UndoCount, Is.Zero, "Committing the same title is a no-op.");
        await session.Execute(Edit("Edited"), Day);
        Assert.That(session.UndoCount, Is.EqualTo(1));
        await session.Undo(Day);
        Assert.That(session.Document.State.Rows[1].Title, Is.EqualTo("Task"));
        await session.Redo(Day);
        Assert.That(session.Document.State.Rows[1].Title, Is.EqualTo("Edited"));
    }

    [Test]
    public async Task ASettingsChangeThatMovesDatesReportsTheMovedTasksOnce()
    {
        var row = new PlanRow("I2", "Task", "acme/repo") { Start = Day, End = Day, Estimate = 8, Remaining = 8, Assignees = ["U1"] };
        var settings = new ProjectPlanSettings { StatusDate = Day, People = [new("U1", "alice", 100, null)],
            Columns = [new(PlanField.Start, "start", "Start date", "DATE"), new(PlanField.End, "end", "Target date", "DATE")] };
        var document = new PlanDocument(Project, new([row], []), new([row], settings));
        var session = await PlanSession.CreateAsync(new(root), document, Day);
        var result = await session.Execute(new ReplacePlanSettings(settings with { ProjectStart = Day.AddDays(7) }), Day);
        Assert.That(result.Recalculation, Is.Not.Null, "A settings change that moves dates reports them for a filter.");
        Assert.That(result.Recalculation!.ChangedTasks, Is.EquivalentTo(new[] { "I2" }));
        Assert.That((await session.FlushAsync()).Recalculation, Is.Null);
        Assert.That((await session.Execute(Edit("Edited"), Day)).Recalculation, Is.Null, "An ordinary edit shows unpublished dates instead.");
    }

    [Test]
    public async Task AStatusDateChangeThroughRefreshMovesNoDateAndReportsNoMovedTasks()
    {
        var row = new PlanRow("I2", "Task", "acme/repo") { Start = Day, End = Day, Estimate = 8, Remaining = 8, Assignees = ["U1"] };
        var settings = new ProjectPlanSettings { StatusDate = Day, People = [new("U1", "alice", 100, null)],
            Columns = [new(PlanField.Start, "start", "Start date", "DATE"), new(PlanField.End, "end", "Target date", "DATE")] };
        var document = new PlanDocument(Project, new([row], []), new([row], settings));
        var session = await PlanSession.CreateAsync(new(root), document, Day);
        Assert.That(session.Changes(Day).TaskCount, Is.Zero);
        var result = await session.Execute(new ReplacePlanSettings(document.State.Settings with { StatusDate = Day.AddDays(9) }), Day);
        Assert.That(result.Recalculation, Is.Null, "A 基準日 change never moves a task date.");
        Assert.That(session.Schedule(Day.AddDays(9)).Single().End.Value, Is.EqualTo(row.End));
        Assert.That(session.Changes(Day.AddDays(9)).TaskCount, Is.Zero);
    }
}
