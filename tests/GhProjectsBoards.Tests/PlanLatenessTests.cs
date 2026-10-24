using System.Collections.Immutable;
using System.Globalization;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #236: lateness compares the plan dates with the 基準日 and the plan with the 見込終了 forecast.
// 期限超過 is red, 予定より遅れ and 見積超過 are yellow, 残 0・未完了 shows nothing, and 遅れの原因
// holds 期限超過 plus the 予定より遅れ a task caused with its own remaining work.
[TestFixture]
internal sealed class PlanLatenessTests
{
    private static DateOnly D(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd");
    // Thursday; the following Monday is 2026-10-05.
    private static readonly DateOnly Status = D("2026-10-05");
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");
    private static PlanRow Row(string id = "task") => new(id, id, "acme/work");
    private static PlanCalendar Calendar => new();
    private static PlanDocument Document(params PlanRow[] rows)
        => new(Project, new(rows.ToImmutableArray(), []),
            new(rows.ToImmutableArray(), new() { StatusDate = Status,
                Columns = [new(PlanField.Start, "S", "Start", "DATE"), new(PlanField.End, "E", "End", "DATE")] }));
    private static ScheduledTask[] Schedule(PlanDocument document, DateOnly? today = null)
        => PlanOperations.Schedule(document, today ?? Status).ToArray();
    private static PlanTaskLateness Late(PlanDocument document, string identity = "task", DateOnly? today = null)
    {
        // A read day is the 基準日 of the document it produces, so passing one models the read that set it.
        if (today is { } day) document = document with { State = document.State with { Settings = document.State.Settings with { StatusDate = day } } };
        var scheduled = Schedule(document);
        return PlanLateness.Classify(scheduled, Calendar, document.State.Settings.StatusDate ?? Status).Tasks[identity];
    }
    private static string Indicator(PlanDocument document, string identity = "task", DateOnly? today = null)
    {
        if (today is { } day) document = document with { State = document.State with { Settings = document.State.Settings with { StatusDate = day } } };
        var scheduled = Schedule(document);
        var task = scheduled.Single(t => t.Input.Identity == identity);
        var lateness = PlanLateness.Classify(scheduled, Calendar, document.State.Settings.StatusDate ?? Status);
        return PlanSheetText.Indicator(document.State.Rows.Single(r => r.Identity == identity), task, lateness.Tasks[identity], "", false);
    }

    [Test]
    public void AnInProgressPlanEndBeforeTheStatusDateIsFinishOverdueAndAlsoNamesTheForecastSlip()
    {
        // 8h done and 16h left from 10/1 finishes 10/5; the read day is 10/6, so the plan is late and
        // the forecast shows how far the remaining work now reaches.
        var document = Document(Row() with { Start = D("2026-10-01"), End = D("2026-10-05"), Estimate = 24, Actual = 8, Remaining = 16 });

        var late = Late(document, today: D("2026-10-06"));

        Assert.Multiple(() =>
        {
            Assert.That(late.Level, Is.EqualTo(PlanLatenessLevel.Overdue));
            Assert.That(late.OverdueKind, Is.EqualTo(PlanOverdueKind.Finish));
            Assert.That(late.MissedDate, Is.EqualTo(D("2026-10-05")));
            Assert.That(late.DaysLater, Is.EqualTo(2), "The remaining work now reaches 10/7.");
            Assert.That(Indicator(document, today: D("2026-10-06")), Is.EqualTo("期限超過: 終了日 10/5 を過ぎて未完了（見込み残存工数 16h）、見込終了は +2日"));
        });
    }

    [Test]
    public void AnUnstartedPlanStartBeforeTheStatusDateIsStartOverdue()
    {
        var document = Document(Row() with { Start = D("2026-10-01"), End = D("2026-10-20") });

        var late = Late(document);

        Assert.Multiple(() =>
        {
            Assert.That(late.Level, Is.EqualTo(PlanLatenessLevel.Overdue));
            Assert.That(late.OverdueKind, Is.EqualTo(PlanOverdueKind.Start));
            Assert.That(late.MissedDate, Is.EqualTo(D("2026-10-01")));
            Assert.That(Indicator(document), Is.EqualTo("期限超過: 開始日 10/1 を過ぎて未着手"));
        });
    }

    [TestCase("2026-10-05", TestName = "PlanEndOnTheStatusDate_IsNotOverdue")]
    [TestCase("2026-10-06", TestName = "PlanEndAfterTheStatusDate_IsNotOverdue")]
    public void PlanEndOnOrAfterTheStatusDateIsNotOverdue(string end)
    {
        var document = Document(Row() with { Start = D(end), End = D(end), Estimate = 8, Remaining = 8 });

        Assert.That(Late(document).Level, Is.EqualTo(PlanLatenessLevel.None));
    }

    [Test]
    public void OwnRemainingWorkThatSlipsPastTheStatusDateIsLaterAndIsACause()
    {
        // Started 10/1 with 8h done and 16h left: the plan finishes exactly on the 基準日, and the
        // remaining work now reaches 10/6. The PMO sees a yellow slip, not a red overdue.
        var document = Document(Row() with { Start = D("2026-10-01"), End = D("2026-10-05"), Estimate = 24, Actual = 8, Remaining = 16 });

        var late = Late(document);

        Assert.Multiple(() =>
        {
            Assert.That(late.Level, Is.EqualTo(PlanLatenessLevel.Later));
            Assert.That(late.DaysLater, Is.EqualTo(1), "10/6.");
            Assert.That(late.Cause, Is.EqualTo(PlanLateCause.OwnRemaining));
            Assert.That(late.IsCause, Is.True);
            Assert.That(Indicator(document), Is.EqualTo("予定より遅れ: +1日（自分の残による）"));
        });
    }

    [Test]
    public void APredecessorSlipIsLaterButNotACause()
    {
        var document = Document(
            Row("before") with { Start = D("2026-10-01"), End = D("2026-10-07"), Estimate = 40, Remaining = 40 },
            Row() with { Start = D("2026-10-05"), End = D("2026-10-05"), Estimate = 8, Remaining = 8, Predecessors = ["before"] });

        var late = Late(document);
        var result = PlanLateness.Classify(Schedule(document), Calendar, Status);

        Assert.Multiple(() =>
        {
            Assert.That(late.Level, Is.EqualTo(PlanLatenessLevel.Later));
            Assert.That(late.Cause, Is.EqualTo(PlanLateCause.Predecessor));
            Assert.That(late.CausePredecessorRowId, Is.EqualTo(1));
            Assert.That(late.IsCause, Is.False, "A task late only because a predecessor moved is not a cause.");
            Assert.That(result.CauseTasks, Is.EqualTo(1), "Only the predecessor, overdue on its own dates.");
            Assert.That(Indicator(document), Is.EqualTo("予定より遅れ: +3日（先行 1 の遅れによる）"),
                "The plan end is 10/7; the predecessor's forecast uses up 10/9, so the forecast reaches 10/13 (10/12 is Sports Day).");
        });
    }

    [TestCase(true, TestName = "OverEstimateWithOverdue_RedWinsAndTheTooltipNamesBoth")]
    [TestCase(false, TestName = "OverEstimateWithoutOverdue_IsOverEstimateOnly")]
    public void OverEstimateIsReportedIndependentlyOfOverdue(bool overdue)
    {
        var document = Document(Row() with { Start = D("2026-10-05"), End = D("2026-10-09"), Estimate = 8, Actual = 16, Remaining = 16 });

        var late = Late(document, today: overdue ? D("2026-10-12") : Status);
        var result = PlanLateness.Classify(Schedule(document, overdue ? D("2026-10-12") : Status), Calendar, overdue ? D("2026-10-12") : Status);

        Assert.Multiple(() =>
        {
            Assert.That(late.OverEstimate, Is.True);
            Assert.That(result.OverEstimateTasks, Is.EqualTo(1));
            Assert.That(late.Level == PlanLatenessLevel.Overdue, Is.EqualTo(overdue));
            Assert.That((result.OverdueTasks, result.LaterTasks), Is.EqualTo((overdue ? 1 : 0, 0)));
        });
    }

    [Test]
    public void ZeroRemainingWhileOpenShowsNoMarkOnThePlanSheet()
    {
        var document = Document(Row() with { Start = D("2026-10-05"), End = D("2026-10-05"), Estimate = 40, Remaining = 0 });
        var scheduled = Schedule(document).Single();

        Assert.Multiple(() =>
        {
            Assert.That(scheduled.IsZeroRemainingOpen, Is.True);
            Assert.That(Late(document).Level, Is.EqualTo(PlanLatenessLevel.None), "残 0 is not a lateness mark.");
            Assert.That(Indicator(document), Is.Empty);
        });
    }

    [Test]
    public void AClosedTaskIsNeverLateAndHasNoForecast()
    {
        var document = Document(Row() with { Start = D("2026-09-01"), End = D("2026-09-01"), Estimate = 8, Closed = true, CloseDate = D("2026-09-30") });
        var scheduled = Schedule(document).Single();

        Assert.Multiple(() =>
        {
            Assert.That(Late(document).Level, Is.EqualTo(PlanLatenessLevel.None));
            Assert.That(Indicator(document), Is.EqualTo("完了"));
            Assert.That(scheduled.ForecastStart, Is.Null);
            Assert.That(scheduled.ForecastEnd, Is.Null);
        });
    }

    [Test]
    public void ATaskIsLateWhenItsForecastEndPassesItsStoredPlanEnd()
    {
        var document = Document(Row() with { Start = D("2026-10-02"), End = D("2026-10-03"), Estimate = 24, Remaining = 24 });

        var late = Late(document, today: D("2026-10-02"));

        Assert.Multiple(() =>
        {
            Assert.That(late.Level, Is.EqualTo(PlanLatenessLevel.Later), "A task whose plan end stays gets a forecast and can be 予定より遅れ.");
            Assert.That(late.Cause, Is.EqualTo(PlanLateCause.OwnRemaining));
        });
    }

    [Test]
    public void ATaskWithoutEffortHasNoForecastAndIsNotLater()
    {
        var document = Document(Row() with { Start = D("2026-10-01"), End = D("2026-10-20") });
        var scheduled = Schedule(document).Single();
        var late = Late(document);

        Assert.Multiple(() =>
        {
            Assert.That(scheduled.ForecastEnd, Is.Null);
            Assert.That(late.DaysLater, Is.Null, "A task without effort is not 予定より遅れ.");
            Assert.That(late.OverdueKind, Is.EqualTo(PlanOverdueKind.Start), "期限超過 still applies from its dates.");
        });
    }

    [Test]
    public void ATaskWithoutDatesHasNoForecastAndNoMark()
    {
        var document = Document(Row() with { Parent = "outside" });
        var scheduled = Schedule(document).Single();

        Assert.Multiple(() =>
        {
            Assert.That(scheduled.ForecastEnd, Is.Null);
            Assert.That(Late(document).Level, Is.EqualTo(PlanLatenessLevel.None));
        });
    }

    [Test]
    public void SuccessorsFollowAPredecessorForecastAndIgnoreAClosedOne()
    {
        var open = Document(
            Row("before") with { Start = D("2026-10-01"), End = D("2026-10-07"), Estimate = 40, Actual = 8, Remaining = 40 },
            Row("after") with { Start = D("2026-10-08"), End = D("2026-10-08"), Estimate = 8, Remaining = 8, Predecessors = ["before"] });
        var closed = Document(
            Row("before") with { Start = D("2026-10-01"), End = D("2026-10-07"), Estimate = 40, Closed = true, CloseDate = D("2026-10-02") },
            Row("after") with { Start = D("2026-10-08"), End = D("2026-10-08"), Estimate = 8, Remaining = 8, Predecessors = ["before"] });

        Assert.Multiple(() =>
        {
            Assert.That(Schedule(open).Single(t => t.Input.Identity == "before").ForecastEnd, Is.EqualTo(D("2026-10-09")));
            // The successor starts when the predecessor's forecast work is used up (10/9 at the end of the day),
            // so on the next working day; 10/12 is Sports Day.
            Assert.That(Schedule(open).Single(t => t.Input.Identity == "after").ForecastStart, Is.EqualTo(D("2026-10-13")));
            Assert.That(Schedule(closed).Single(t => t.Input.Identity == "after").ForecastStart, Is.EqualTo(D("2026-10-08")),
                "A closed predecessor has no forecast, so its own end date constrains instead.");
        });
    }

    [Test]
    public void SummaryRollsUpTheStrongestMarkAndCountsLeavesOnce()
    {
        var document = Document(
            Row("root") with { Start = D("2026-10-01"), End = D("2026-10-20") },
            Row("overdue") with { Parent = "root", Start = D("2026-10-01"), End = D("2026-10-02"), Estimate = 8, Remaining = 8 },
            Row("late") with { Parent = "root", Start = D("2026-10-01"), End = D("2026-10-05"), Estimate = 24, Actual = 8, Remaining = 16 },
            Row("over") with { Parent = "root", Start = D("2026-10-05"), End = D("2026-10-09"), Estimate = 8, Actual = 16, Remaining = 16 });

        var result = PlanLateness.Classify(Schedule(document), Calendar, Status);

        Assert.Multiple(() =>
        {
            Assert.That(result.Tasks["root"].Level, Is.EqualTo(PlanLatenessLevel.Overdue), "The red mark wins for a summary.");
            Assert.That(result.Tasks["root"].OverdueDescendantTasks, Is.EqualTo(1));
            Assert.That(result.Tasks["root"].LaterDescendantTasks, Is.EqualTo(1));
            Assert.That(result.Tasks["root"].OverEstimateDescendantTasks, Is.EqualTo(1));
            Assert.That(result.OverdueTasks, Is.EqualTo(1));
            Assert.That(result.LaterTasks, Is.EqualTo(1));
            Assert.That(result.OverEstimateTasks, Is.EqualTo(1));
            Assert.That(result.CauseTasks, Is.EqualTo(2), "The overdue task and the one late through its own remaining work.");
        });
    }

    [Test]
    public void SummaryCountsOnlyDescendantsWhenNoLeafIsLate()
    {
        var document = Document(Row("root") with { Start = D("2026-10-01"), End = D("2026-10-20") },
            Row("child") with { Parent = "root", Start = D("2026-10-05"), End = D("2026-10-05"), Estimate = 8, Remaining = 8 });

        var result = PlanLateness.Classify(Schedule(document), Calendar, Status);

        Assert.Multiple(() =>
        {
            Assert.That(result.Tasks["root"].Level, Is.EqualTo(PlanLatenessLevel.None));
            Assert.That(result.Tasks["root"].OverdueDescendantTasks, Is.Zero);
            Assert.That(result.Tasks["root"].LaterDescendantTasks, Is.Zero);
            Assert.That(result.Tasks["root"].OverEstimateDescendantTasks, Is.Zero);
        });
    }

    [TestCase("started", "終了: 開始から 17h")]
    [TestCase("remaining", "終了: 開始から 7.5h")]
    [TestCase("estimate", "終了: 開始から 40h")]
    [TestCase("no-work-end", "終了: 指定")]
    [TestCase("no-end", null)]
    [TestCase("complete", null)]
    [TestCase("remaining-complete", null)]
    [TestCase("summary", null)]
    [TestCase("milestone", null)]
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
            "no-work-end" => task with { Estimate = null },
            "no-end" => task with { Estimate = null, End = null },
            "complete" => task with { Closed = true },
            "remaining-complete" => task with { Remaining = 0 },
            "milestone" => task with { Estimate = 0 },
            "invalid" => task with { Remaining = -1 },
            "invalid-kept" => task with { Estimate = null, Start = date.AddDays(1) },
            _ => task
        };
        PlanTask[] tasks = scenario == "summary" ? [task, new("child", 2) { Parent = "task", Estimate = 8 }] : [task];
        var scheduled = PlanScheduler.Calculate(tasks, new() { StatusDate = date }, Status);
        var originalCulture = CultureInfo.CurrentCulture;
        string? reason;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            reason = PlanLateness.EndReason(scheduled[0]);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }

        Assert.That(reason, Is.EqualTo(expected));
    }

    [TestCase(PlanField.Start)]
    [TestCase(PlanField.End)]
    public void TypedDateDifference_IsEnteredAndTaskCountIsUnchanged(string field)
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
        // #236: an edit reschedules the edited task, its transitive successors and the roll-ups. Raising the
        // predecessor's estimate to 32h pushes its end to 10/8 and the successor's start with it.
        var document = Document(Row("before") with { Estimate = 8, Start = Status, End = Status },
            Row() with { Estimate = 8, Start = Status.AddDays(2), End = Status.AddDays(2), Predecessors = ["before"] });
        Assert.That(PlanOperations.Changes(document, Status).TaskCount, Is.Zero);
        var state = PlanOperations.Apply(document, new EditPlanCells(PlanOperationKind.Cell,
            [new("before", PlanField.Estimate, 32m)]), Status).State;
        Assert.That(PlanOperations.Schedule(document with { State = state }, Status)
            .Single(t => t.Input.Identity == "task").Start.Value, Is.EqualTo(Status.AddDays(3)));

        var result = PlanOperations.Changes(document with { State = state }, Status);

        Assert.That(result.InputKind("task", PlanField.Start), Is.EqualTo(PlanUnpublishedInputKind.Recalculated));
        Assert.That(result.InputKind("before", PlanField.End), Is.EqualTo(PlanUnpublishedInputKind.Recalculated));
        Assert.That(result.InputKind("before", PlanField.Estimate), Is.EqualTo(PlanUnpublishedInputKind.Entered));
        Assert.That(result.Fields["task"], Is.EquivalentTo(new[] { PlanField.Start, PlanField.End }));
    }

    [TestCase(false, PlanUnpublishedInputKind.Entered, TestName = "TypedStartKeptAsThePlannedStart_IsEntered")]
    [TestCase(true, PlanUnpublishedInputKind.Recalculated, TestName = "TypedStartPushedLaterByPredecessor_IsRecalculated")]
    public void TypedStartKindFollowsDisplayedValue(bool predecessorDelayed, PlanUnpublishedInputKind expected)
    {
        var document = Document(Row("before") with { Estimate = 8, Start = Status, End = Status },
            Row() with { Estimate = 8, Start = Status.AddDays(1), End = Status.AddDays(1), Predecessors = ["before"] });
        var state = PlanOperations.Apply(document, new EditPlanCells(PlanOperationKind.Cell,
            [new("task", PlanField.Start, Status.AddDays(2)), new("before", PlanField.Estimate, predecessorDelayed ? 32m : 8m)]), Status).State;
        var updated = document with { State = state };
        var scheduled = PlanOperations.Schedule(updated, Status).Single(row => row.Input.Identity == "task");
        Assert.That(scheduled.Start.Value, Is.EqualTo(Status.AddDays(predecessorDelayed ? 3 : 2)));
        Assert.That(scheduled.Input.Start, Is.EqualTo(Status.AddDays(2)), "The typed start stays the row's own start.");

        var result = PlanOperations.Changes(updated, Status);

        Assert.That(result.InputKind("task", PlanField.Start), Is.EqualTo(expected));
    }

    [Test]
    public void PredecessorWinsTieWithTypedStart_StartKeepsTheTypedValue()
    {
        var specified = Status.AddDays(2);
        var document = Document(
            Row("before") with { Estimate = 0, Start = specified, End = specified },
            Row() with { Estimate = 8, Start = specified, End = specified, Predecessors = ["before"] });
        document = document with { Baseline = document.Baseline with { Rows = document.Baseline.Rows
            .Select(row => row.Identity == "task" ? row with { Start = Status.AddDays(1), End = Status.AddDays(1) } : row).ToImmutableArray() } };
        var scheduled = PlanOperations.Schedule(document, Status).Single(row => row.Input.Identity == "task");
        Assert.That(scheduled.Start.Value, Is.EqualTo(specified));
        Assert.That(scheduled.StartReason, Is.EqualTo("1 の終了後"), "The predecessor names the reason on a tie.");

        var result = PlanOperations.Changes(document, Status);

        Assert.That(result.InputKind("task", PlanField.Start), Is.EqualTo(PlanUnpublishedInputKind.Entered),
            "The displayed start is still the value the PMO typed.");
    }

    [Test]
    public void SummaryDateDifferencesAreAlwaysRecalculated()
    {
        // A summary's dates are always the scheduler's roll-up, so its difference is a calculation even when
        // it equals the summary's own inputs. The child states its plan start (#236).
        var document = Document(Row("summary") with { Start = Status.AddDays(1), End = Status.AddDays(1) },
            Row() with { Parent = "summary", Estimate = 8, Start = Status.AddDays(1) });
        document = document with { Baseline = document.Baseline with
            { Rows = document.Baseline.Rows.Select(row => row with { Start = Status, End = Status }).ToImmutableArray() } };
        var summary = PlanOperations.Schedule(document, Status).Single(row => row.Input.Identity == "summary");
        Assert.That(summary.Start.Value, Is.EqualTo(summary.Input.Start));
        Assert.That(summary.End.Value, Is.EqualTo(summary.Input.End));

        var result = PlanOperations.Changes(document, Status);

        Assert.Multiple(() =>
        {
            Assert.That(result.InputKind("summary", PlanField.Start), Is.EqualTo(PlanUnpublishedInputKind.Recalculated));
            Assert.That(result.InputKind("summary", PlanField.End), Is.EqualTo(PlanUnpublishedInputKind.Recalculated));
        });
    }

    [TestCase("2026-10-09", "2026-10-09", null, false, null)]
    [TestCase("2026-10-09", "2026-10-08", null, false, null)]
    [TestCase(null, "2026-10-09", null, false, null)]
    [TestCase("2026-10-09", null, null, false, null)]
    [TestCase("2026-10-02", "2026-10-05", null, false, 1)]
    [TestCase("2026-10-09", "2026-10-13", null, false, 1)]
    [TestCase("2026-10-05", "2026-10-07", "2026-10-06", false, 1)]
    [TestCase("2026-10-05", "2026-10-07", "2026-10-06", true, 1)]
    public void ForecastSlipCountsOnlyProjectWorkingDays(string? planned, string? forecast, string? dayOff, bool imported, int? expected)
    {
        var calendar = new PlanCalendar();
        if (dayOff is not null)
            calendar = imported ? calendar with { ImportedHolidays = new("test", "test", "test", DateTimeOffset.UnixEpoch, 2026, 2026, [new(DateOnly.Parse(dayOff), "holiday")]) }
                : calendar with { CompanyDaysOff = new HashSet<DateOnly> { DateOnly.Parse(dayOff) } };
        Assert.That(PlanScheduler.WorkingDaysBetween(planned is null ? null : DateOnly.Parse(planned),
            forecast is null ? null : DateOnly.Parse(forecast), calendar), Is.EqualTo(expected));
    }
}
