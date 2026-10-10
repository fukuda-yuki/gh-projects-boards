using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanProgressDateTests
{
    private static readonly DateOnly Start = new(2026, 10, 5), End = new(2026, 10, 6), Status = new(2026, 10, 21);
    private string root = null!;
    private PlanSession? session;
    [SetUp] public void Setup() => root = Path.Combine(Path.GetTempPath(), "ghpb-progress-dates-" + Guid.NewGuid().ToString("N"));
    [TearDown] public async Task Cleanup()
    {
        if (session is not null) await session.FlushAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
        session = null;
    }
    private static PlanDocument Initial()
    {
        var parent = new PlanRow("P", "Requirement", "acme/repo") { Start = Start, End = End };
        var task = new PlanRow("A", "Work", "acme/repo") {
            Parent = "P", Assignees = ["U1"], Estimate = 16, Remaining = 16, Actual = 0, Start = Start, End = End
        };
        var next = new PlanRow("B", "Successor", "acme/repo") {
            Assignees = ["U1"], Estimate = 8, Remaining = 8, Actual = 0, Predecessors = ["A"], Start = End.AddDays(1), End = End.AddDays(1)
        };
        ImmutableArray<PlanRow> baseline = [parent, task, next];
        return new(new(new("github.com", 42), "P1"), new(baseline, []),
            new(baseline.Select(r => r with { Start = null, End = null }).ToImmutableArray(),
                new() { StatusDate = Start, People = [new("U1", "alice", 100, null)] }));
    }
    private static PlanRemoteSnapshot Remote(PlanBaseline baseline) => new(baseline,
        baseline.Rows.ToImmutableDictionary(r => r.Identity, r => "item-" + r.Identity), [], 0, 0);
    private static PlanBaseline Progress(PlanDocument document, string progress) => document.Baseline with {
        Rows = document.Baseline.Rows.Select(r => r.Identity == "A" ? r with {
            Actual = progress == "closed" ? 0 : 8,
            Remaining = progress == "closed" ? 16 : progress == "complete" ? 0 : 8, Closed = progress == "closed"
        } : r).ToImmutableArray()
    };

    [TestCase("complete", false)]
    [TestCase("in-progress", false)]
    [TestCase("closed", false)]
    [TestCase("complete", true)]
    [TestCase("in-progress", true)]
    public async Task ProgressRetainsPublishedDatesThroughSchedulingUndoAndReopen(string progress, bool local)
    {
        var initial = Initial();
        session = await PlanSession.CreateAsync(new(root), initial, Start);
        if (local)
            Assert.That((await session.Execute(new EditPlanCells(PlanOperationKind.Paste,
                [new("A", PlanField.Actual, 8m), new("A", PlanField.Remaining, progress == "complete" ? 0m : 8m)]), Start)).Succeeded, Is.True);
        else
        {
            await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("A", PlanField.Title, "PMO title")]), Start);
            Assert.That((await session.AcceptRefresh(Remote(Progress(initial, progress)), Start)).Succeeded, Is.True);
        }
        await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { StatusDate = Status }), Status);
        AssertRetained();
        Assert.That((await session.Undo(Status)).Succeeded, Is.True);
        Assert.That((await session.Undo(Start)).Succeeded, Is.True);
        if (local)
        {
            var undone = session.Document.State.Rows.Single(r => r.Identity == "A");
            Assert.That((undone.Actual, undone.Remaining, undone.Start, undone.End), Is.EqualTo((0m, 16m, (DateOnly?)null, (DateOnly?)null)));
        }
        else
        {
            var retained = session.Document.State.Rows.Single(r => r.Identity == "A");
            Assert.That(retained.Title, Is.EqualTo("Work"));
            Assert.That(retained.Start, Is.EqualTo(Start), "Undo of unrelated local work must not undo adopted progress dates.");
        }
        await session.Redo(Start); await session.Redo(Status);
        var reopened = await PlanSession.OpenAsync(new(root), initial.Project, Status);
        Assert.That(reopened.Status, Is.EqualTo(PlanLoadStatus.Loaded), reopened.Error);
        session = reopened.Session!;
        AssertRetained();

        void AssertRetained()
        {
            var complete = progress != "in-progress";
            var row = session!.Document.State.Rows.Single(r => r.Identity == "A");
            Assert.That(row.Start, Is.EqualTo(Start));
            Assert.That(row.End, Is.EqualTo(complete ? End : (DateOnly?)null));
            Assert.That(row.Fixed, Is.False);
            Assert.That(row.StartNoEarlierThan, Is.Null);
            var schedule = session.Schedule(Status).ToDictionary(t => t.Input.Identity);
            Assert.That((schedule["A"].Start.Value, schedule["A"].End.Value), Is.EqualTo((Start, complete ? End : Status)));
            Assert.That((schedule["P"].Start.Value, schedule["P"].End.Value), Is.EqualTo((Start, complete ? End : Status)));
            Assert.That(schedule["B"].Start.Value, Is.EqualTo(complete ? Status : Status.AddDays(1)));
            Assert.That(schedule["A"].Warnings, Does.Not.Contain("完了タスクの終了日なし").And.Not.Contain("進行中タスクの開始日なし"));
            var fields = session.Changes(Status).Fields.GetValueOrDefault("A");
            Assert.That(fields.IsDefaultOrEmpty || !fields.Contains(PlanField.Start), Is.True);
            if (complete) Assert.That(fields.IsDefaultOrEmpty || !fields.Contains(PlanField.End), Is.True);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RefreshPreservesDateClearsThatWereAlreadyRetained(bool complete)
    {
        var initial = Initial();
        var baseline = Progress(initial, complete ? "complete" : "in-progress");
        var document = initial with { Baseline = baseline, State = initial.State with {
            Rows = initial.State.Rows.Select(r => r.Identity == "A" ? r with { Actual = 8, Remaining = complete ? 0 : 8 } : r).ToImmutableArray()
        } };
        var remote = baseline with { Rows = baseline.Rows.Select(r => r.Identity == "A" ? r with { Actual = 9 } : r).ToImmutableArray() };
        var result = PlanMerge.Merge(document, remote).State.Rows.Single(r => r.Identity == "A");
        Assert.That((result.Start, result.End), Is.EqualTo(((DateOnly?)null, (DateOnly?)null)));
        Assert.That(result.Actual, Is.EqualTo(9));
    }

    [TestCase(PlanField.Start)]
    [TestCase(PlanField.End)]
    public async Task CompletingAndExplicitlyClearingADateInOneEditKeepsThatClear(PlanField field)
    {
        session = await PlanSession.CreateAsync(new(root), Initial(), Start);
        var result = await session.Execute(new EditPlanCells(PlanOperationKind.Paste,
            [new("A", PlanField.Remaining, 0m), new("A", field, null)]), Start);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var row = session.Document.State.Rows.Single(r => r.Identity == "A");
        Assert.That(field == PlanField.Start ? row.Start : row.End, Is.Null);
        Assert.That(session.UndoCount, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RefreshPreservesTypedStartAndReportsAConflictingRemoteDate(bool remoteDateChanged)
    {
        var initial = Initial();
        var document = initial with { State = initial.State with {
            Rows = initial.State.Rows.Select(r => r.Identity == "A" ? r with { Start = End, StartNoEarlierThan = End } : r).ToImmutableArray()
        } };
        var remote = Progress(initial, "complete");
        if (remoteDateChanged) remote = remote with { Rows = remote.Rows.Select(r => r.Identity == "A" ? r with { Start = End.AddDays(1), End = End.AddDays(2) } : r).ToImmutableArray() };
        var merged = PlanMerge.Merge(document, remote);
        var result = merged.State.Rows.Single(r => r.Identity == "A");
        Assert.That((result.Start, result.StartNoEarlierThan), Is.EqualTo((End, End)));
        Assert.That(result.End, Is.EqualTo(remoteDateChanged ? End.AddDays(2) : End));
        Assert.That(merged.Sync.Conflicts.Any(c => c.Identity == "A" && c.Field == PlanField.Start), Is.EqualTo(remoteDateChanged));
    }

    [Test]
    public void RefreshDoesNotResolveAnExistingDateConflictWhenProgressArrives()
    {
        var initial = Initial();
        var conflict = new PlanConflict("A", PlanField.Start, PlanValues.Get(initial.Baseline.Rows[1], PlanField.Start), "null", "\"2026-10-07\"");
        var document = initial with { Sync = initial.Sync with { Conflicts = [conflict] } };
        var merged = PlanMerge.Merge(document, Progress(initial, "complete"));
        Assert.That(merged.State.Rows.Single(r => r.Identity == "A").Start, Is.Null);
        Assert.That(merged.Sync.Conflicts.Single(c => c.Field == PlanField.Start).Local, Is.EqualTo("null"));
    }

    [Test]
    public void RefreshDoesNotInventMissingCompletedDates()
    {
        var initial = Initial();
        var remote = Progress(initial, "complete");
        remote = remote with { Rows = remote.Rows.Select(r => r.Identity == "A" ? r with { Start = null, End = null } : r).ToImmutableArray() };
        var merged = PlanMerge.Merge(initial, remote);
        var task = PlanOperations.Schedule(merged, Status).Single(t => t.Input.Identity == "A");
        Assert.That((task.Start.Value, task.End.Value), Is.EqualTo(((DateOnly?)null, (DateOnly?)null)));
        Assert.That(task.Warnings, Does.Contain("完了タスクの終了日なし"));
    }
}
