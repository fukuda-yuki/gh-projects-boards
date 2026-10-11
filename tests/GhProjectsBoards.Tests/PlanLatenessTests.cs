using System.Collections.Immutable;
using System.Globalization;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class PlanLatenessTests
{
    private static readonly DateOnly Status = new(2026, 10, 5);
    private static PlanRow Row(string id = "task") => new(id, id, "acme/work");
    private static PlanDocument Document(params PlanRow[] rows)
        => new(new(new("github.com", 42), "P1"), new(rows.ToImmutableArray(), []),
            new(rows.ToImmutableArray(), new() { StatusDate = Status,
                Columns = [new(PlanField.Start, "S", "Start", "DATE"), new(PlanField.End, "E", "End", "DATE")] }));
    private static PlanLatenessResult Classify(PlanDocument document, DateOnly? today = null)
        => PlanLateness.Classify(PlanOperations.Schedule(document, today ?? Status), document.Baseline,
            new(), document.State.Settings.StatusDate ?? today ?? Status);

    [TestCase(-1, false, PlanLatenessLevel.Overdue, PlanOverdueKind.Finish, TestName = "PublishedEndBeforeStatus_Incomplete_IsFinishOverdue")]
    [TestCase(0, false, PlanLatenessLevel.None, null, TestName = "PublishedEndOnStatus_IsNotOverdue")]
    [TestCase(-1, true, PlanLatenessLevel.None, null, TestName = "PastPublishedEnd_Complete_IsNotOverdue")]
    public void PublishedEndBoundary(int offset, bool complete, PlanLatenessLevel level, PlanOverdueKind? kind)
    {
        var document = Document(Row() with { End = Status.AddDays(offset), Closed = complete });

        var result = Classify(document).Tasks["task"];

        Assert.That(result.Level, Is.EqualTo(level));
        Assert.That(result.OverdueKind, Is.EqualTo(kind));
        Assert.That(result.MissedPublishedDate, Is.EqualTo(kind is null ? (DateOnly?)null : Status.AddDays(offset)));
    }

    [TestCase(null, -1, false, true, TestName = "PastPublishedStart_ActualAbsent_IsStartOverdue")]
    [TestCase(0, -1, false, true, TestName = "PastPublishedStart_ActualZero_IsStartOverdue")]
    [TestCase(1, -1, false, false, TestName = "PastPublishedStart_Started_IsNotStartOverdue")]
    [TestCase(-1, -1, false, false, TestName = "PastPublishedStart_InvalidNegativeActual_IsNotUnstarted")]
    [TestCase(0, 0, false, false, TestName = "PublishedStartOnStatus_IsNotOverdue")]
    [TestCase(0, -1, true, false, TestName = "PastPublishedStart_CompleteWithZeroActual_IsNotOverdue")]
    public void PublishedStartBoundary(int? actual, int offset, bool complete, bool overdue)
    {
        var document = Document(Row() with { Start = Status.AddDays(offset), End = Status.AddDays(5), Actual = actual, Closed = complete });

        var result = Classify(document).Tasks["task"];

        Assert.That(result.Level, Is.EqualTo(overdue ? PlanLatenessLevel.Overdue : PlanLatenessLevel.None));
        Assert.That(result.OverdueKind, Is.EqualTo(overdue ? PlanOverdueKind.Start : (PlanOverdueKind?)null));
        Assert.That(result.MissedPublishedDate, Is.EqualTo(overdue ? Status.AddDays(offset) : (DateOnly?)null));
    }

    [Test]
    public void BothPublishedDatesMissed_FinishTakesPrecedence()
    {
        var document = Document(Row() with { Start = Status.AddDays(-3), End = Status.AddDays(-1), Actual = 0 });

        var result = Classify(document).Tasks["task"];

        Assert.That(result.OverdueKind, Is.EqualTo(PlanOverdueKind.Finish));
        Assert.That(result.MissedPublishedDate, Is.EqualTo(Status.AddDays(-1)));
    }

    [TestCase(true, PlanLatenessLevel.None, TestName = "ExplicitStatusDate_OverridesSuppliedToday")]
    [TestCase(false, PlanLatenessLevel.Overdue, TestName = "UnsetStatusDate_UsesSuppliedToday")]
    public void ResolvedStatusDateMatchesScheduler(bool explicitStatus, PlanLatenessLevel expected)
    {
        var document = Document(Row() with { End = Status });
        if (!explicitStatus) document = document with { State = document.State with { Settings = new() } };

        var result = Classify(document, Status.AddDays(1)).Tasks["task"];

        Assert.That(result.Level, Is.EqualTo(expected));
    }

    [TestCase(0, PlanLatenessLevel.Later, 1, TestName = "CalculatedEndAfterPublished_NotOverdue_IsLater")]
    [TestCase(-3, PlanLatenessLevel.Overdue, 2, TestName = "OverdueWithLaterCalculatedEnd_StillExposesDaysLater")]
    public void LaterEndExposesDaysIndependentlyOfLevel(int publishedOffset, PlanLatenessLevel expected, int days)
    {
        var document = Document(Row() with { Estimate = 16, End = Status.AddDays(publishedOffset) });

        var result = Classify(document).Tasks["task"];

        Assert.That(result.Level, Is.EqualTo(expected));
        Assert.That(result.DaysLater, Is.EqualTo(days));
    }

    [Test]
    public void LocalTaskWithoutBaseline_HasNoLatenessOrPublishedStartDelay()
    {
        var document = Document(Row() with { Estimate = 16, Start = Status.AddDays(-4), End = Status.AddDays(-3) });
        document = document with { Baseline = new([], []) };

        var result = Classify(document).Tasks["task"];

        Assert.That(result.Level, Is.EqualTo(PlanLatenessLevel.None));
        Assert.That(result.DaysLater, Is.Null);
        Assert.That(result.OverdueKind, Is.Null);
        Assert.That(result.MissedPublishedDate, Is.Null);
        Assert.That(result.StartDelayedByPredecessor, Is.False);
    }

    [TestCase(true, 0, true, TestName = "StartMovedAfterPredecessor_IsPredecessorDelay")]
    [TestCase(false, -1, false, TestName = "StartMovedForStatusDate_IsNotPredecessorDelay")]
    [TestCase(true, 1, false, TestName = "PredecessorReasonWithUnmovedStart_IsNotPredecessorDelay")]
    public void PredecessorStartDelayRequiresBothConditions(bool predecessor, int startOffset, bool expected)
    {
        var document = Document(Row("before") with { Estimate = 8 },
            Row() with { Estimate = 8, Start = Status.AddDays(startOffset), Predecessors = predecessor ? ["before"] : [] });

        var result = Classify(document).Tasks["task"];

        Assert.That(result.StartDelayedByPredecessor, Is.EqualTo(expected));
    }

    [TestCase(-1, PlanLatenessLevel.Overdue, 1, 1, TestName = "NestedOverdueLeaf_RollsUpAndCountsLeavesOnce")]
    [TestCase(0, PlanLatenessLevel.Later, 0, 2, TestName = "OnlyLaterLeaves_RollUpEvenWithUnmovedSummaryEnd")]
    public void NestedSummariesRollUpLeafLevelsAndTotals(int endOffset, PlanLatenessLevel level, int overdue, int later)
    {
        var document = Document(
            Row("root") with { End = Status.AddDays(10) },
            Row("nested") with { Parent = "root", End = Status.AddDays(10) },
            Row("a") with { Parent = "nested", Estimate = 16, End = Status.AddDays(endOffset) },
            Row("b") with { Parent = "root", Estimate = 16, End = Status },
            Row("anchor") with { Parent = "root", End = Status.AddDays(10), Actual = 1 },
            Row("external-parent") with { Parent = "outside-project", End = Status });

        var result = Classify(document);

        Assert.That(result.Tasks["root"].Level, Is.EqualTo(level));
        Assert.That(result.Tasks["root"].DaysLater, Is.Null);
        Assert.That(result.Tasks["root"].OverdueDescendantTasks, Is.EqualTo(overdue));
        Assert.That(result.Tasks["root"].LaterDescendantTasks, Is.EqualTo(later));
        Assert.That(result.Tasks["nested"].Level, Is.EqualTo(level));
        Assert.That(result.Tasks["nested"].OverdueDescendantTasks, Is.EqualTo(overdue));
        Assert.That(result.Tasks["nested"].LaterDescendantTasks, Is.EqualTo(later - 1));
        Assert.That(result.OverdueTasks, Is.EqualTo(overdue));
        Assert.That(result.LaterTasks, Is.EqualTo(later));
    }

    [Test]
    public void SummaryOwnEndMovedWithNoLateLeaves_IsLaterButNotCountedInTotals()
    {
        var document = Document(Row("root") with { End = Status.AddDays(-3) },
            Row("child") with { Parent = "root", End = Status });

        var result = Classify(document);

        Assert.That(result.Tasks["root"].Level, Is.EqualTo(PlanLatenessLevel.Later));
        Assert.That(result.Tasks["root"].DaysLater, Is.EqualTo(1));
        Assert.That(result.Tasks["root"].OverdueDescendantTasks, Is.Zero);
        Assert.That(result.Tasks["root"].LaterDescendantTasks, Is.Zero);
        Assert.That(result.OverdueTasks, Is.Zero);
        Assert.That(result.LaterTasks, Is.Zero);
    }

    [TestCase("started", "終了: 状況日 11/11 から残り 16h")]
    [TestCase("remaining", "終了: 開始から 7.5h")]
    [TestCase("estimate", "終了: 開始から 40h")]
    [TestCase("fixed", "終了: 指定")]
    [TestCase("typed", "終了: 指定")]
    [TestCase("no-work-end", "終了: 指定")]
    [TestCase("no-end", null)]
    [TestCase("complete", null)]
    [TestCase("remaining-complete", null)]
    [TestCase("summary", null)]
    [TestCase("milestone", null)]
    [TestCase("fixed-milestone", null)]
    [TestCase("invalid", null)]
    [TestCase("invalid-kept", null)]
    public void EndReasonDescribesScheduledWorkOrKeptEnd(string scenario, string? expected)
    {
        var date = new DateOnly(2026, 11, 11);
        var task = new PlanTask("task", 1) { Start = date, End = date, Estimate = 40.00m };
        task = scenario switch
        {
            "started" => task with { Actual = 1, Remaining = 16.00m },
            "remaining" => task with { Remaining = 7.50m },
            "fixed" => task with { Fixed = true },
            "typed" => PlanEdits.End(task, date),
            "no-work-end" => task with { Estimate = null },
            "no-end" => task with { Estimate = null, End = null },
            "complete" => task with { Closed = true },
            "remaining-complete" => task with { Remaining = 0 },
            "milestone" => task with { Estimate = 0 },
            "fixed-milestone" => task with { Estimate = 0, Fixed = true },
            "invalid" => task with { Remaining = -1 },
            "invalid-kept" => task with { Fixed = true, Start = date.AddDays(1) },
            _ => task
        };
        PlanTask[] tasks = scenario == "summary" ? [task, new("child", 2) { Parent = "task", Estimate = 8 }] : [task];
        var scheduled = PlanScheduler.Calculate(tasks, new() { StatusDate = date }, Status);
        var originalCulture = CultureInfo.CurrentCulture;
        string? reason;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            reason = PlanLateness.EndReason(scheduled[0], date);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }

        Assert.That(reason, Is.EqualTo(expected));
    }

    [TestCase(PlanField.Start)]
    [TestCase(PlanField.End)]
    public void TypedDateDifference_IsEnteredAndTaskCountIsUnchanged(PlanField field)
    {
        var document = Document(Row() with { Start = Status, End = Status });
        var state = PlanOperations.Apply(document, new EditPlanCells(PlanOperationKind.Cell,
            [new("task", field, field == PlanField.Start ? Status.AddDays(-1) : Status.AddDays(1))]), Status).State;

        var result = PlanOperations.Changes(document with { State = state }, Status);

        Assert.That(result.InputKind("task", field), Is.EqualTo(PlanUnpublishedInputKind.Entered));
        Assert.That(result.TaskCount, Is.EqualTo(1));
        Assert.That(result.InputKind("task", PlanField.Title), Is.Null);
        Assert.That(result.InputKind("missing", field), Is.Null);
    }

    [Test]
    public void PredecessorEstimateEdit_DateDifferencesAreRecalculatedAndEffortIsEntered()
    {
        var document = Document(Row("before") with { Estimate = 8, Start = Status, End = Status },
            Row() with { Estimate = 8, Start = Status.AddDays(1), End = Status.AddDays(1), Predecessors = ["before"] });
        Assert.That(PlanOperations.Changes(document, Status).TaskCount, Is.Zero);
        var state = PlanOperations.Apply(document, new EditPlanCells(PlanOperationKind.Cell,
            [new("before", PlanField.Estimate, 16m)]), Status).State;

        var result = PlanOperations.Changes(document with { State = state }, Status);

        Assert.That(result.InputKind("task", PlanField.Start), Is.EqualTo(PlanUnpublishedInputKind.Recalculated));
        Assert.That(result.InputKind("task", PlanField.End), Is.EqualTo(PlanUnpublishedInputKind.Recalculated));
        Assert.That(result.InputKind("before", PlanField.End), Is.EqualTo(PlanUnpublishedInputKind.Recalculated));
        Assert.That(result.InputKind("before", PlanField.Estimate), Is.EqualTo(PlanUnpublishedInputKind.Entered));
        Assert.That(result.TaskCount, Is.EqualTo(2));
        Assert.That(result.Fields["task"], Is.EquivalentTo(new[] { PlanField.Start, PlanField.End }));
    }

    [TestCase(false, TestName = "PublishedCalculatedDatesWithOlderInputs_StatusChangeRemainsRecalculated")]
    [TestCase(true, TestName = "PublishedSummaryDatesWithOlderInputs_StatusChangeRemainsRecalculated")]
    public void PublishedDatesAdvanceWithoutReplacingInputs_RecalculationIsNotEntered(bool summary)
    {
        var child = Row() with { Estimate = 8, Start = Status.AddDays(-3), End = Status.AddDays(-3) };
        var document = summary
            ? Document(Row("summary") with { Start = child.Start, End = child.End }, child with { Parent = "summary" })
            : Document(child);
        // Verified publication advances the baseline while preserving historical inputs for Undo.
        document = document with { Baseline = document.Baseline with
            { Rows = document.Baseline.Rows.Select(row => row with { Start = Status, End = Status }).ToImmutableArray() } };
        Assert.That(PlanOperations.Changes(document, Status).TaskCount, Is.Zero);
        var state = PlanOperations.Apply(document,
            new ReplacePlanSettings(document.State.Settings with { StatusDate = Status.AddDays(1) }), Status).State;

        var result = PlanOperations.Changes(document with { State = state }, Status);

        var identity = summary ? "summary" : "task";
        Assert.Multiple(() =>
        {
            Assert.That(result.InputKind(identity, PlanField.Start), Is.EqualTo(PlanUnpublishedInputKind.Recalculated));
            Assert.That(result.InputKind(identity, PlanField.End), Is.EqualTo(PlanUnpublishedInputKind.Recalculated));
            Assert.That(result.Fields[identity], Is.EquivalentTo(new[] { PlanField.Start, PlanField.End }));
            Assert.That(result.TaskCount, Is.EqualTo(summary ? 2 : 1));
        });
    }

    [Test]
    public void SummaryDatesMatchingOwnInputs_AreStillRecalculated()
    {
        var document = Document(Row("summary") with { Start = Status.AddDays(1), End = Status.AddDays(1) },
            Row() with { Parent = "summary", Estimate = 8 });
        document = document with { Baseline = document.Baseline with
            { Rows = document.Baseline.Rows.Select(row => row with { Start = Status, End = Status }).ToImmutableArray() } };
        Assert.That(PlanOperations.Changes(document, Status).TaskCount, Is.Zero);
        var state = PlanOperations.Apply(document,
            new ReplacePlanSettings(document.State.Settings with { StatusDate = Status.AddDays(1) }), Status).State;
        var updated = document with { State = state };
        var summary = PlanOperations.Schedule(updated, Status).Single(row => row.Input.Identity == "summary");
        Assert.That(summary.Start.Value, Is.EqualTo(summary.Input.Start));
        Assert.That(summary.End.Value, Is.EqualTo(summary.Input.End));

        var result = PlanOperations.Changes(updated, Status);

        Assert.Multiple(() =>
        {
            Assert.That(result.InputKind("summary", PlanField.Start), Is.EqualTo(PlanUnpublishedInputKind.Recalculated));
            Assert.That(result.InputKind("summary", PlanField.End), Is.EqualTo(PlanUnpublishedInputKind.Recalculated));
        });
    }

    [TestCase(false, PlanUnpublishedInputKind.Entered, TestName = "TypedStartMatchingCalculatedStart_IsEntered")]
    [TestCase(true, PlanUnpublishedInputKind.Recalculated, TestName = "TypedStartPushedLaterByPredecessor_IsRecalculated")]
    public void TypedStartKindFollowsDisplayedValue(bool predecessorDelayed, PlanUnpublishedInputKind expected)
    {
        var document = Document(Row("before") with { Estimate = 8, Start = Status, End = Status },
            Row() with { Estimate = 8, Start = Status.AddDays(1), End = Status.AddDays(1), Predecessors = ["before"] });
        var state = PlanOperations.Apply(document, new EditPlanCells(PlanOperationKind.Cell,
            [new("task", PlanField.Start, Status.AddDays(2)), new("before", PlanField.Estimate, predecessorDelayed ? 24m : 8m)]), Status).State;
        var updated = document with { State = state };
        var scheduled = PlanOperations.Schedule(updated, Status).Single(row => row.Input.Identity == "task");
        Assert.That(scheduled.Start.Value, Is.EqualTo(Status.AddDays(predecessorDelayed ? 3 : 2)));
        Assert.That(scheduled.Start.Origin, Is.EqualTo(DateOrigin.Calculated));
        Assert.That(scheduled.Input.Start, Is.EqualTo(Status.AddDays(2)));
        Assert.That(scheduled.Input.StartNoEarlierThan, Is.EqualTo(scheduled.Input.Start));

        var result = PlanOperations.Changes(updated, Status);

        Assert.That(result.InputKind("task", PlanField.Start), Is.EqualTo(expected));
    }

    [TestCase(PlanField.End, TestName = "EstimateEdit_CalculatedEndReturnsToHistoricalInput_IsRecalculated")]
    [TestCase(PlanField.Start, TestName = "StatusDateEdit_CalculatedStartReturnsToHistoricalInput_IsRecalculated")]
    public void AutomaticDateReturningToHistoricalInput_IsRecalculated(PlanField field)
    {
        var savedDate = Status.AddDays(2);
        var row = Row() with { Start = savedDate, End = savedDate, Estimate = field == PlanField.End ? 16 : 8 };
        var document = Document(row) with { Baseline = new([row with
            { Start = Status, End = field == PlanField.End ? Status.AddDays(1) : Status }], []) };
        Assert.That(PlanOperations.Changes(document, Status).TaskCount, Is.Zero);
        PlanCommand edit = field == PlanField.End
            ? new EditPlanCells(PlanOperationKind.Cell, [new("task", PlanField.Estimate, 24m)])
            : new ReplacePlanSettings(document.State.Settings with { StatusDate = savedDate });
        var updated = document with { State = PlanOperations.Apply(document, edit, Status).State };
        var scheduled = PlanOperations.Schedule(updated, Status).Single();
        var date = field == PlanField.End ? scheduled.End : scheduled.Start;
        Assert.That(date.Origin, Is.EqualTo(DateOrigin.Calculated));
        Assert.That(date.Value, Is.EqualTo(savedDate));
        Assert.That(field == PlanField.End ? scheduled.Input.End : scheduled.Input.Start, Is.EqualTo(savedDate));
        Assert.That(scheduled.Input.StartNoEarlierThan, Is.Null);

        var result = PlanOperations.Changes(updated, Status);

        Assert.That(result.InputKind("task", field), Is.EqualTo(PlanUnpublishedInputKind.Recalculated));
        Assert.That(result.TaskCount, Is.EqualTo(1));
    }

    [Test]
    public void PredecessorWinsTieWithTypedStartConstraint_StartIsRecalculated()
    {
        var specified = Status.AddDays(2);
        var document = Document(
            Row("before") with { Estimate = 0, Start = specified, End = specified, StartNoEarlierThan = specified },
            Row() with { Estimate = 8, Start = specified, End = specified, StartNoEarlierThan = specified, Predecessors = ["before"] });
        document = document with { Baseline = document.Baseline with { Rows = document.Baseline.Rows
            .Select(row => row.Identity == "task" ? row with { Start = Status.AddDays(1), End = Status.AddDays(1) } : row).ToImmutableArray() } };
        var scheduled = PlanOperations.Schedule(document, Status).Single(row => row.Input.Identity == "task");
        Assert.That(scheduled.Start.Origin, Is.EqualTo(DateOrigin.Calculated));
        Assert.That(scheduled.Start.Value, Is.EqualTo(scheduled.Input.StartNoEarlierThan));
        Assert.That(scheduled.StartReason, Is.EqualTo("1 の終了後"));

        var result = PlanOperations.Changes(document, Status);

        Assert.That(result.InputKind("task", PlanField.Start), Is.EqualTo(PlanUnpublishedInputKind.Recalculated));
    }

    [Test]
    public void SummaryLatenessUsesItsOwnPublishedEnd()
    {
        var calendar = new PlanCalendar();
        var tasks = PlanScheduler.Calculate([
            new("summary", 1),
            new("child", 2) { Parent = "summary", Estimate = 8, Remaining = 8, Assignees = ["U1"] }
        ], new() { StatusDate = new(2026, 10, 5), People = [new("U1")] }, new(2026, 10, 5));
        Assert.That(PlanScheduler.PublishedEndLateness(new(2026, 10, 1), tasks.Single(t => t.Input.Identity == "summary").End.Value, calendar), Is.EqualTo(2));
        Assert.That(PlanScheduler.PublishedEndLateness(new(2026, 10, 2), tasks.Single(t => t.Input.Identity == "child").End.Value, calendar), Is.EqualTo(1));
    }
    [TestCase("2026-10-09", "2026-10-09", null, false, null)]
    [TestCase("2026-10-09", "2026-10-08", null, false, null)]
    [TestCase(null, "2026-10-09", null, false, null)]
    [TestCase("2026-10-09", null, null, false, null)]
    [TestCase("2026-10-02", "2026-10-05", null, false, 1)]
    [TestCase("2026-10-09", "2026-10-13", null, false, 1)]
    [TestCase("2026-10-05", "2026-10-07", "2026-10-06", false, 1)]
    [TestCase("2026-10-05", "2026-10-07", "2026-10-06", true, 1)]
    public void LatenessCountsOnlyProjectWorkingDays(string? published, string? calculated, string? dayOff, bool imported, int? expected)
    {
        var calendar = new PlanCalendar();
        if (dayOff is not null)
            calendar = imported ? calendar with { ImportedHolidays = new("test", "test", "test", DateTimeOffset.UnixEpoch, 2026, 2026, [new(DateOnly.Parse(dayOff), "holiday")]) }
                : calendar with { CompanyDaysOff = new HashSet<DateOnly> { DateOnly.Parse(dayOff) } };
        Assert.That(PlanScheduler.PublishedEndLateness(published is null ? null : DateOnly.Parse(published),
            calculated is null ? null : DateOnly.Parse(calculated), calendar), Is.EqualTo(expected));
    }
}
