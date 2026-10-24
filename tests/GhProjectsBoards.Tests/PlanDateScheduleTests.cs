using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #236 part A: start and end are plan values. Refresh, a 基準日 change and GitHub の状態に戻す move no
// task date and create no unpublished date difference; only a PMO edit reschedules, and only the task
// it touches, its transitive successors and the roll-ups.
[TestFixture]
internal sealed class PlanDateScheduleTests
{
    private static DateOnly D(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd");
    private static readonly DateOnly Monday = D("2026-10-05");
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");
    private string root = null!;
    [SetUp] public void Setup() => root = Path.Combine(Path.GetTempPath(), "ghpb-date-plan-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static ProjectPlanSettings Settings(DateOnly? status = null) => new()
    {
        StatusDate = status ?? Monday,
        Columns = [new(PlanField.Start, "start", "Start date", "DATE"), new(PlanField.End, "end", "Target date", "DATE")]
    };
    private static PlanDocument Document(ImmutableArray<PlanRow> rows, DateOnly? status = null)
    {
        var settings = Settings(status);
        return new(Project, new(rows, []), new(rows, settings));
    }
    private static ScheduledTask[] Schedule(PlanDocument document, DateOnly? today = null)
        => PlanOperations.Schedule(document, today ?? document.State.Settings.StatusDate ?? Monday).ToArray();
    private static (DateOnly? Start, DateOnly? End) Dates(PlanDocument document, string identity, DateOnly? today = null)
    {
        var task = Schedule(document, today).Single(t => t.Input.Identity == identity);
        return (task.Start.Value, task.End.Value);
    }
    private static ImmutableArray<string> UnpublishedFields(PlanDocument document, string identity, DateOnly? today = null)
        => document.State.Rows.Any(r => r.Identity == identity) && PlanOperations.Changes(document, today ?? Monday).Fields.TryGetValue(identity, out var fields)
            ? fields : [];

    [Test]
    public void AStatusDateChangeMovesNoTaskDate()
    {
        // A task laid out from its own plan dates, with work left and a predecessor.
        ImmutableArray<PlanRow> rows = [
            new("I1", "First", "acme/repo") { Start = D("2026-10-05"), End = D("2026-10-06"), Estimate = 16, Remaining = 16 },
            new("I2", "Second", "acme/repo") { Start = D("2026-10-07"), End = D("2026-10-07"), Estimate = 8, Remaining = 8, Predecessors = ["I1"] }];
        var before = Document(rows, Monday);
        var after = before with { State = before.State with { Settings = before.State.Settings with { StatusDate = Monday.AddDays(14) } } };

        Assert.Multiple(() =>
        {
            Assert.That(Dates(after, "I1", Monday.AddDays(14)), Is.EqualTo((D("2026-10-05"), D("2026-10-06"))));
            Assert.That(Dates(after, "I2", Monday.AddDays(14)), Is.EqualTo((D("2026-10-07"), D("2026-10-07"))));
        });
    }

    [Test]
    public void AnAssigneeEditMovesNoTaskDateAndChangesOnlyTheAssignee()
    {
        // #236 acceptance 2: 担当者 is not a rescheduling trigger. The edit is one unpublished field and
        // neither the edited task nor its successor moves.
        ImmutableArray<PlanRow> rows = [
            new("I1", "First", "acme/repo") { Start = D("2026-10-05"), End = D("2026-10-05"), Estimate = 8, Remaining = 8 },
            new("I2", "Second", "acme/repo") { Start = D("2026-10-06"), End = D("2026-10-06"), Estimate = 8, Remaining = 8, Predecessors = ["I1"] }];
        var document = Document(rows);
        var before = Schedule(document).Select(t => (t.Input.Identity, t.Start.Value, t.End.Value)).ToArray();

        var updated = document with { State = PlanOperations.Apply(document, new EditPlanCells(PlanOperationKind.Cell,
            [new("I1", PlanField.Assignees, new[] { "U1" })]), Monday).State };

        Assert.Multiple(() =>
        {
            Assert.That(Schedule(updated).Select(t => (t.Input.Identity, t.Start.Value, t.End.Value)), Is.EqualTo(before));
            Assert.That(UnpublishedFields(updated, "I1"), Is.EqualTo(new[] { PlanField.Assignees }));
            Assert.That(UnpublishedFields(updated, "I2"), Is.Empty);
        });
    }

    private static PlanDocument Edited(PlanDocument document, string identity, string field, object? value)
        => document with { State = PlanOperations.Apply(document, new EditPlanCells(PlanOperationKind.Cell, [new(identity, field, value)]), Monday).State };
    private static ImmutableArray<PlanRow> TypedEndRows() => [
        new("I1", "First", "acme/repo") { Start = D("2026-10-05"), End = D("2026-10-06"), Estimate = 16, Remaining = 16 },
        new("I2", "Second", "acme/repo") { Start = D("2026-10-07"), End = D("2026-10-07"), Estimate = 8, Remaining = 8, Predecessors = ["I1"] }];

    [Test]
    public void ATypedEndStaysOnItsTaskWhileItsSuccessorReschedulesFromIt()
    {
        // #268: there is no 日程固定 flag. The typed end is the plan end of that edit; the successor follows it.
        var typed = Edited(Document(TypedEndRows()), "I1", PlanField.End, D("2026-10-09"));
        Assert.Multiple(() =>
        {
            Assert.That(Dates(typed, "I1"), Is.EqualTo((D("2026-10-05"), D("2026-10-09"))));
            Assert.That(Dates(typed, "I2").Start, Is.GreaterThanOrEqualTo(D("2026-10-09")));
            Assert.That(UnpublishedFields(typed, "I1"), Is.EqualTo(new[] { PlanField.End }));
        });
    }

    [TestCase(PlanField.Remaining, 8, "2026-10-05")]
    [TestCase(PlanField.Start, "2026-10-06", "2026-10-07")]
    [TestCase(PlanField.End, null, "2026-10-06")]
    public void ALaterEditOfTheTaskRecalculatesItsTypedEnd(string field, object? value, string end)
    {
        var typed = Edited(Document(TypedEndRows()), "I1", PlanField.End, D("2026-10-09"));
        var input = value switch { int hours => (object)(decimal)hours, string date => D(date), _ => null };
        Assert.That(Dates(Edited(typed, "I1", field, input), "I1").End, Is.EqualTo(D(end)));
    }

    [Test]
    public void ATypedEndBeforeTheStartIsRejected()
    {
        var document = Document(TypedEndRows());
        var error = Assert.Throws<ArgumentException>(() => Edited(document, "I1", PlanField.End, D("2026-10-02")));
        Assert.That(error!.Message, Is.EqualTo("終了日は開始日以降にしてください。"));
    }

    [TestCase(PlanField.Remaining)]
    [TestCase(PlanField.Actual)]
    public void APasteOfEffortReschedulesTheEditedTaskAndItsSuccessor(string field)
    {
        // #236 acceptance 2: a paste of 実績 or 残 reschedules the edited task and its successor like a typed
        // edit; 8h done plus 8h left or 16h left both push the end to 10/6 and the successor to 10/7.
        ImmutableArray<PlanRow> rows = [
            new("I1", "First", "acme/repo") { Start = D("2026-10-05"), End = D("2026-10-05"), Estimate = 40, Actual = 0, Remaining = 8 },
            new("I2", "Second", "acme/repo") { Start = D("2026-10-06"), End = D("2026-10-06"), Estimate = 8, Remaining = 8, Predecessors = ["I1"] }];
        var document = Document(rows);

        var updated = document with { State = PlanOperations.Apply(document, new EditPlanCells(PlanOperationKind.Paste,
            [new("I1", field, field == PlanField.Actual ? 8m : 16m)]), Monday).State };

        Assert.Multiple(() =>
        {
            Assert.That(Dates(updated, "I1"), Is.EqualTo((D("2026-10-05"), D("2026-10-06"))));
            // The successor's plan end moves with the predecessor. Its start stays the stored same-day
            // handoff because the plan describes days: the predecessor's stored end 10/6 lets it start 10/6.
            Assert.That(Dates(updated, "I2").End, Is.EqualTo(D("2026-10-07")));
            Assert.That(UnpublishedFields(updated, "I1"), Is.EquivalentTo(new[] { field, PlanField.End }));
            Assert.That(UnpublishedFields(updated, "I2"), Does.Contain(PlanField.End).And.Not.Contain(PlanField.Start));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CompletingOrCancellingARowMovesNoOtherTasksPlanDate(bool cancelled)
    {
        // #236 acceptance 2 / #231: a row-state command changes only the row's own state fields. The
        // successor keeps the plan dates it already holds in both cases.
        ImmutableArray<PlanRow> rows = [
            new("I1", "First", "acme/repo") { Start = D("2026-10-05"), End = D("2026-10-05"), Estimate = 8, Remaining = 8 },
            new("I2", "Second", "acme/repo") { Start = D("2026-10-06"), End = D("2026-10-06"), Estimate = 8, Remaining = 8, Predecessors = ["I1"] }];
        var document = Document(rows);
        var successor = Dates(document, "I2");

        var updated = document with { State = PlanOperations.Apply(document, new ClosePlanRows(["I1"], cancelled), Monday).State };

        Assert.Multiple(() =>
        {
            Assert.That(Dates(updated, "I2"), Is.EqualTo(successor), "No row-state command moves another task's plan date.");
            Assert.That(UnpublishedFields(updated, "I1"), cancelled
                ? Is.EquivalentTo(new[] { PlanField.Closed, PlanField.Cancelled })
                : Does.Contain(PlanField.Closed));
            Assert.That(UnpublishedFields(updated, "I2"), Is.Empty);
        });
    }


    [Test]
    public void APlanStartIsNeverPulledEarlier()
    {
        // 8h of work planned for 10/20 is late work only because the 基準日 caught up with it, not a
        // reason to move it back to the 基準日.
        ImmutableArray<PlanRow> rows = [new("I1", "Later", "acme/repo") { Start = D("2026-10-20"), End = D("2026-10-20"), Estimate = 8, Remaining = 8 }];

        Assert.That(Dates(Document(rows), "I1"), Is.EqualTo((D("2026-10-20"), D("2026-10-20"))));
    }

    [Test]
    public void ReschedulingAnInProgressTaskConsumesActualAndRemainingFromItsStart()
    {
        // 8h done and 8h left from 10/1 finishes 10/2; the PMO raises what is left to 16h, so the plan
        // now runs to 10/5 without moving the start.
        ImmutableArray<PlanRow> rows = [new("I1", "Running", "acme/repo") { Start = D("2026-10-01"), End = D("2026-10-02"), Estimate = 24, Actual = 8, Remaining = 8 }];
        var document = Document(rows);

        var state = PlanOperations.Apply(document, new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Remaining, 16m)]), Monday).State;

        Assert.That(Dates(document with { State = state }, "I1"), Is.EqualTo((D("2026-10-01"), D("2026-10-05"))));
    }

    [Test]
    public void ATaskWithNoDatesGetsItsFirstDatesWhenAnEditPlacesIt()
    {
        // #236 rule 5: a read gives a task with no dates none. Its first dates arrive with the PMO's own
        // edit — here effort — and they are no earlier than the 基準日.
        ImmutableArray<PlanRow> rows = [new("I1", "New", "acme/repo") { Start = D("2026-10-20"), End = D("2026-10-20") }];
        var document = Document(rows);

        var state = PlanOperations.Apply(document, new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Remaining, 16m)]), Monday).State;

        Assert.That(Dates(document with { State = state }, "I1"), Is.EqualTo((D("2026-10-20"), D("2026-10-21"))));

        ImmutableArray<PlanRow> undated = [new("I2", "Undated", "acme/repo")];
        var untouched = Document(undated);
        Assert.That(Dates(untouched, "I2"), Is.EqualTo(((DateOnly?)null, (DateOnly?)null)),
            "A read leaves a task with no dates without one.");
        var placed = PlanOperations.Apply(untouched, new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Estimate, 16m)]), Monday).State;
        Assert.That(Dates(untouched with { State = placed }, "I2"), Is.EqualTo((Monday, D("2026-10-06"))));
    }

    [Test]
    public void RefreshCreatesNoUnpublishedDateDifferenceAndKeepsEveryDate()
    {
        ImmutableArray<PlanRow> rows = [
            new("I1", "First", "acme/repo") { Start = D("2026-10-05"), End = D("2026-10-06"), Estimate = 16, Remaining = 16 },
            new("I2", "Second", "acme/repo") { Start = D("2026-10-07"), End = D("2026-10-07"), Estimate = 8, Remaining = 8, Predecessors = ["I1"] },
            new("I3", "Closed", "acme/repo") { Start = D("2026-09-30"), End = D("2026-09-30"), Estimate = 8, Closed = true, CloseDate = D("2026-09-30") }];
        var document = Document(rows);
        var refreshed = document with { State = document.State with { Settings = document.State.Settings with { StatusDate = Monday.AddDays(9) } } };

        var changes = PlanOperations.Changes(refreshed, Monday.AddDays(9));

        Assert.Multiple(() =>
        {
            Assert.That(Schedule(refreshed, Monday.AddDays(9)).Select(t => (t.Input.Identity, t.Start.Value, t.End.Value)),
                Is.EqualTo(Schedule(document, Monday).Select(t => (t.Input.Identity, t.Start.Value, t.End.Value))));
            Assert.That(changes.Fields.Values.SelectMany(f => f), Does.Not.Contain(PlanField.Start).And.Not.Contain(PlanField.End));
        });
    }

    [Test]
    public void ARefreshAdoptsTheCloseDayAsTheOnlyDateChange()
    {
        ImmutableArray<PlanRow> rows = [
            new("I1", "Closing", "acme/repo") { Start = D("2026-10-05"), End = D("2026-10-05"), Estimate = 8, Remaining = 8 },
            new("I2", "Open", "acme/repo") { Start = D("2026-10-06"), End = D("2026-10-07"), Estimate = 16, Remaining = 16 }];
        var document = Document(rows);
        var closed = D("2026-10-08");
        var remote = document.Baseline.Rows.SetItem(0, document.Baseline.Rows[0] with { Closed = true, CloseDate = closed, Predecessors = [] });
        var merged = document with
        {
            Baseline = document.Baseline with { Rows = remote },
            State = document.State with { Rows = document.State.Rows.SetItem(0, document.State.Rows[0] with { Closed = true, CloseDate = closed }) }
        };

        var changes = PlanOperations.Changes(merged, closed);

        Assert.Multiple(() =>
        {
            Assert.That(Dates(merged, "I1", closed).End, Is.EqualTo(closed), "Closing is a fact from GitHub; its end becomes the close day.");
            Assert.That(changes.Fields["I1"], Does.Contain(PlanField.End));
            Assert.That(changes.Fields.GetValueOrDefault("I2", []), Does.Not.Contain(PlanField.Start).And.Not.Contain(PlanField.End));
        });
    }
}
