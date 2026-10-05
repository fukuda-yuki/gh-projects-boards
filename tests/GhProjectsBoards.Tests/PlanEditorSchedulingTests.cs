using System.Diagnostics;
using System.Text;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanEditorSchedulingTests
{
    private static DateOnly D(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd");
    private static readonly DateOnly Monday = D("2026-10-05");
    private static PlanSettings Settings => new() { StatusDate = Monday };
    private static PlanTask Task(int id = 1, decimal? work = 8) => new($"issue:{id}", id) { Estimate = work };
    private static ScheduledTask Run(PlanTask task, PlanSettings? settings = null) => Calculate([task], settings ?? Settings).Single();
    private static IReadOnlyList<ScheduledTask> Calculate(IReadOnlyList<PlanTask> tasks, PlanSettings settings)
        => PlanScheduler.Calculate(tasks, settings, Monday);
    private static void Dates(ScheduledTask task, string? start, string? end)
    {
        Assert.Multiple(() =>
        {
            Assert.That(task.Start.Value, Is.EqualTo(start is null ? null : D(start)));
            Assert.That(task.End.Value, Is.EqualTo(end is null ? null : D(end)));
        });
    }

    [TestCase("2026-10-09", 16, "2026-10-09", "2026-10-13", TestName = "WeekendAndBundledHolidayAreSkipped")]
    [TestCase("2026-10-10", 8, "2026-10-13", "2026-10-13", TestName = "NonworkingEarliestStartMovesToNextWorkingDay")]
    [TestCase("2026-10-05", 4, "2026-10-05", "2026-10-05", TestName = "HalfDayWorkKeepsSameDay")]
    [TestCase("2026-10-05", 9, "2026-10-05", "2026-10-06", TestName = "WorkBeyondEightHoursContinuesNextDay")]
    public void WorkUsesWorkingCalendar(string status, int work, string start, string end)
        => Dates(Run(Task(work: work), Settings with { StatusDate = D(status) }), start, end);

    [TestCase(false, false, "2026-10-06")]
    [TestCase(true, false, "2026-10-07")]
    [TestCase(false, true, "2026-10-07")]
    public void HalfRateUsesFullDayAndSkipsCompanyOrPersonalDays(bool company, bool personal, string end)
    {
        var settings = Settings with
        {
            People = [new("p", 50) { DaysOff = personal ? new HashSet<DateOnly> { Monday.AddDays(1) } : new HashSet<DateOnly>() }],
            Calendar = new() { CompanyDaysOff = company ? new HashSet<DateOnly> { Monday.AddDays(1) } : new HashSet<DateOnly>() }
        };
        Dates(Run(Task() with { Assignees = ["p"] }, settings), "2026-10-05", end);
    }

    [Test]
    public void ExistingCsvImporterAddsHolidaysWithoutRemovingBundle()
    {
        var csv = "国民の祝日・休日月日,国民の祝日・休日名称\n" + string.Join("\n", Enumerable.Range(1, 16).Select(i => $"2026/10/{i},休業"));
        var imported = HolidayCsvImport.Parse(Encoding.UTF8.GetBytes(csv), "holidays.csv", DateTimeOffset.UtcNow, 2026, 2026);
        var settings = Settings with { Calendar = new() { ImportedHolidays = imported } };
        Dates(Run(Task(), settings), "2026-10-19", "2026-10-19");
        Dates(Run(Task(work: 16), settings with { StatusDate = D("2026-11-02") }), "2026-11-02", "2026-11-04");
    }

    [TestCase(4, 4, "2026-10-05", "2026-10-05")]
    [TestCase(8, 4, "2026-10-06", "2026-10-06")]
    [TestCase(3, 5, "2026-10-05", "2026-10-05")]
    public void SuccessorContinuesAtInternalFinishWithoutDailyRounding(int first, int second, string start, string end)
    {
        var result = Calculate([Task(work: first), Task(2, second) with { Predecessors = ["issue:1"] }], Settings);
        Dates(result[1], start, end);
        Assert.That(result[1].StartReason, Is.EqualTo("#1 の終了後"));
    }

    [TestCase("2026-10-01", null, "2026-10-05", "状況日")]
    [TestCase("2026-10-06", null, "2026-10-06", "プロジェクト開始日")]
    [TestCase("2026-10-06", "2026-10-07", "2026-10-07", "開始日指定 10/7")]
    [TestCase("2026-10-05", "2026-10-05", "2026-10-05", "開始日指定 10/5")]
    public void LatestDayConstraintSetsStartAndDeterministicReason(string project, string? specified, string start, string reason)
    {
        var result = Run(Task() with { StartNoEarlierThan = specified is null ? null : D(specified) }, Settings with { ProjectStart = D(project) });
        Dates(result, start, start);
        Assert.That(result.StartReason, Is.EqualTo(reason));
    }

    [TestCase("2026-10-05", "2026-10-06", "#1 の終了後")]
    [TestCase("2026-10-07", "2026-10-07", "開始日指定 10/7")]
    public void SpecifiedStartCompetesWithPredecessor(string specified, string expected, string reason)
    {
        var result = Calculate([Task(), Task(2) with { Predecessors = ["issue:1"], StartNoEarlierThan = D(specified) }], Settings)[1];
        Dates(result, expected, expected);
        Assert.That(result.StartReason, Is.EqualTo(reason));
    }

    [TestCase("2026-10-01", "2026-10-01", "2026-10-05", "開始日を維持")]
    [TestCase("2026-10-07", "2026-10-07", "2026-10-07", "開始日を維持")]
    [TestCase(null, "2026-10-05", "2026-10-05", "状況日")]
    public void ProgressRetainsStartAndSchedulesRemainingFromStatusOrStart(string? start, string expectedStart, string expectedEnd, string reason)
    {
        var result = Run(Task() with { Actual = 4, Remaining = 4, Start = start is null ? null : D(start) });
        Dates(result, expectedStart, expectedEnd);
        Assert.That(result.StartReason, Is.EqualTo(reason));
        Assert.That(result.Start.Origin, Is.EqualTo(start is null ? DateOrigin.Calculated : DateOrigin.Kept));
        Assert.That(result.End.Origin, Is.EqualTo(DateOrigin.Calculated));
        Assert.That(result.Warnings.Contains("進行中タスクの開始日なし"), Is.EqualTo(start is null));
    }

    [TestCase(true, null, 8, "完了")]
    [TestCase(false, 0, 8, "完了")]
    [TestCase(false, null, null, "工数なし")]
    public void CompleteAndNoEffortKeepDates(bool closed, int? remaining, int? estimate, string reason)
    {
        var result = Run(Task(work: estimate) with { Closed = closed, Remaining = remaining, Start = D("2026-10-01"), End = D("2026-10-02") });
        Dates(result, "2026-10-01", "2026-10-02");
        Assert.That(result.StartReason, Is.EqualTo(reason));
        Assert.That(result.Start.Origin, Is.EqualTo(DateOrigin.Kept));
        Assert.That(result.End.Origin, Is.EqualTo(DateOrigin.Kept));
    }

    [TestCase(null, "2026-10-05", true)]
    [TestCase("2026-10-06", "2026-10-07", false)]
    public void CompleteEndConstrainsSuccessorOnlyWhenPresent(string? end, string successor, bool warning)
    {
        var results = Calculate([Task() with { Closed = true, End = end is null ? null : D(end) }, Task(2) with { Predecessors = ["issue:1"] }], Settings);
        Dates(results[1], successor, successor);
        Assert.That(results[0].Warnings.Contains("完了タスクの終了日なし"), Is.EqualTo(warning));
    }

    [TestCase(false, "2026-10-05")]
    [TestCase(true, "2026-10-06")]
    public void MilestoneUsesExactPredecessorEndDayOrEarliestDay(bool predecessor, string day)
    {
        var results = Calculate([Task(work: 16), Task(2, 0) with { Predecessors = predecessor ? ["issue:1"] : [] }], Settings);
        Dates(results[1], day, day);
    }

    [Test]
    public void EstimateEntryFillsOnlyMissingRemainingAndDoesNotMutateOriginal()
    {
        var task = Task(work: null);
        var filled = PlanEdits.Estimate(task, 12);
        Assert.That((filled.Estimate, filled.Remaining), Is.EqualTo((12m, 12m)));
        Assert.That(PlanEdits.Estimate(filled, 20).Remaining, Is.EqualTo(12));
        Assert.That(PlanEdits.Estimate(filled, null).Remaining, Is.EqualTo(12));
        Assert.That(task.Estimate, Is.Null);
        Assert.Throws<ArgumentException>(() => PlanEdits.Estimate(task, -1));
    }

    [Test]
    public void TypedDatesSetConstraintsAndKeepGitHubBaseline()
    {
        var task = Task() with { GitHubStart = Monday, GitHubEnd = Monday };
        var start = PlanEdits.Start(task, Monday.AddDays(1));
        Assert.That(start.StartNoEarlierThan, Is.EqualTo(Monday.AddDays(1)));
        var end = PlanEdits.End(start, Monday.AddDays(2));
        Assert.That(end.Fixed, Is.True);
        var result = Run(end);
        Dates(result, "2026-10-06", "2026-10-07");
        Assert.That(result.StartReason, Is.EqualTo("日程固定"));
        Assert.That(result.Start.DiffersFromGitHub && result.End.DiffersFromGitHub, Is.True);
        Assert.That(PlanEdits.Start(end, null).StartNoEarlierThan, Is.Null);
        Assert.That(PlanEdits.End(end, null).Fixed, Is.True);
        Assert.That(end.GitHubEnd, Is.EqualTo(Monday));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FixedDatesWarnAboutConflictOrMissingDatesWithoutMoving(bool missing)
    {
        var results = Calculate([Task(work: 16), Task(2) with { Fixed = true, Start = Monday, End = missing ? null : Monday, Predecessors = ["issue:1"] }], Settings);
        Dates(results[1], "2026-10-05", missing ? null : "2026-10-05");
        Assert.That(results[1].Warnings, Does.Contain("先行タスクより前に開始"));
        Assert.That(results[1].Warnings.Contains("日程固定の日付不足"), Is.EqualTo(missing));
    }

    [TestCase(0, false)]
    [TestCase(2, true)]
    public void NoOrMultipleAssigneesUseProjectCalendarAndFullRate(int count, bool warning)
    {
        var result = Run(Task() with { Assignees = count == 0 ? [] : ["p", "q"] }, Settings with { People = [new("p", 50) { DaysOff = new HashSet<DateOnly> { Monday } }] });
        Dates(result, "2026-10-05", "2026-10-05");
        Assert.That(result.Warnings.Contains("担当者が複数"), Is.EqualTo(warning));
    }

    [Test]
    public void ExternalPredecessorIsRetainedWarnedAndDoesNotMoveDate()
    {
        var task = Task() with { Predecessors = ["other/repo#99"] };
        var result = Run(task);
        Dates(result, "2026-10-05", "2026-10-05");
        Assert.That(result.Warnings, Does.Contain("プロジェクト外の先行タスク: other/repo#99"));
        Assert.That(result.Input.Predecessors, Is.EqualTo(task.Predecessors));
    }

    private static IEnumerable<TestCaseData> Cycles()
    {
        yield return new TestCaseData(new[] { Task() with { Predecessors = ["issue:2"] }, Task(2) with { Predecessors = ["issue:1"] }, Task(3) with { Predecessors = ["issue:2"] } }, new[] { 1, 2 }).SetName("DependencyCycleNamesOnlyCycleRows");
        yield return new TestCaseData(new[] { Task() with { Parent = "issue:2" }, Task(2) with { Parent = "issue:1" } }, new[] { 1, 2 }).SetName("HierarchyCycleIsRejected");
        yield return new TestCaseData(new[] { Task() with { Predecessors = ["issue:1"] } }, new[] { 1 }).SetName("SelfDependencyIsRejected");
        yield return new TestCaseData(new[] { Task(), Task(2) with { Parent = "issue:1", Predecessors = ["issue:1"] } }, new[] { 1, 2 }).SetName("ChildDependingOnParentIsRejected");
        yield return new TestCaseData(new[] { Task() with { Predecessors = ["issue:2"] }, Task(2) with { Parent = "issue:1" } }, new[] { 1, 2 }).SetName("ParentDependingOnChildIsRejected");
        yield return new TestCaseData(new[] { Task() with { Predecessors = ["issue:3"] }, Task(2) with { Parent = "issue:1" }, Task(3) with { Predecessors = ["issue:2"] } }, new[] { 2, 3 }).SetName("InheritedDependencyCycleIsRejected");
    }
    [TestCaseSource(nameof(Cycles))]
    public void CyclesRejectWholeCalculationWithRowIds(PlanTask[] tasks, int[] ids)
    {
        var error = Assert.Throws<ArgumentException>(() => Calculate(tasks, Settings));
        Assert.That(error!.Message, Is.EqualTo("循環参照: " + string.Join(", ", ids.Select(id => $"#{id}"))));
    }

    [Test]
    public void NestedSummaryRollsUpChildrenIgnoresOwnEffortAndConstrainsSuccessor()
    {
        var rows = new[] { Task(100, 999), Task(10, 999) with { Parent = "issue:100" }, Task(1, 4) with { Parent = "issue:10", Remaining = 4, Actual = 2 }, Task(2, 12) with { Parent = "issue:10", Remaining = 12, Actual = 0 }, Task(3) with { Predecessors = ["issue:100"] } };
        var result = Calculate(rows, Settings);
        Assert.That((result[0].Estimate, result[0].Remaining, result[0].Actual), Is.EqualTo((16m, 16m, 2m)));
        Dates(result[0], "2026-10-05", "2026-10-06");
        Dates(result[4], "2026-10-06", "2026-10-07");
        Assert.That(result[0].StartReason, Is.EqualTo("子タスクの集計"));
        Assert.That(result[0].IsSummary, Is.True);
        Assert.That(result[0].Input.Estimate, Is.EqualTo(999));
        Assert.That(result[4].StartReason, Is.EqualTo("#100 の終了後"));
    }

    [Test]
    public void ParentPredecessorsConstrainAllNestedDescendants()
    {
        var rows = new[] { Task(), Task(2) with { Predecessors = ["issue:1"] }, Task(3) with { Parent = "issue:2" }, Task(4, 4) with { Parent = "issue:3" }, Task(5, 4) with { Parent = "issue:2" } };
        var result = Calculate(rows, Settings);
        foreach (var row in result.Skip(1)) Dates(row, "2026-10-06", "2026-10-06");
        Assert.That(result[3].StartReason, Is.EqualTo("#1 の終了後"));
    }

    [Test]
    public void SummaryMissingEffortStaysUnknownAndDatesUseOnlyPresentChildren()
    {
        var result = Calculate([Task(), Task(2, null) with { Parent = "issue:1" }, Task(3) with { Parent = "issue:1" }], Settings)[0];
        Assert.That(result.Estimate, Is.Null);
        Assert.That(result.Actual, Is.Null);
        Dates(result, "2026-10-05", "2026-10-05");
    }

    [Test]
    public void IndependentTasksNeverLevelAndFilteringDoesNotRenumberRows()
    {
        var settings = Settings with { People = [new("p", 50)] };
        var rows = new[] { Task(12, 4) with { Assignees = ["p"] }, Task(42, 4) with { Assignees = ["p"] } };
        var results = Calculate(rows, settings);
        foreach (var result in results) Dates(result, "2026-10-05", "2026-10-05");
        Assert.That(results.Where(r => r.Input.RowId == 42).Single().Input.RowId, Is.EqualTo(42));
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public void CalculatedDateDifferenceComparesNullableGitHubBaseline(bool matches, bool differs)
    {
        var result = Run(Task() with { GitHubStart = matches ? Monday : null, GitHubEnd = matches ? Monday : null });
        Assert.That(result.Start.Origin, Is.EqualTo(DateOrigin.Calculated));
        Assert.That(result.Start.DiffersFromGitHub, Is.EqualTo(differs));
        Assert.That(result.End.DiffersFromGitHub, Is.EqualTo(differs));
    }

    [TestCase(-1), TestCase(0), TestCase(101)]
    public void InvalidRateRejectsCalculation(int rate) => Assert.Throws<ArgumentException>(() => Run(Task(), Settings with { People = [new("p", rate)] }));

    [Test]
    public void InvalidIdentitiesRejectCalculation()
    {



        Assert.Throws<ArgumentException>(() => Run(Task(0)));
        Assert.Throws<ArgumentException>(() => Calculate([Task(), Task()], Settings));
        Assert.Throws<ArgumentException>(() => Calculate([Task(), Task(2) with { RowId = 1 }], Settings));
    }

    [Test]
    public void HolidayCoverageWarningAndDateOverflowAreExplicit()
    {
        Assert.That(Run(Task(), Settings with { StatusDate = D("2028-01-03") }).Warnings, Does.Contain("祝日データの対象年外"));
        Assert.That(Run(Task(work: decimal.MaxValue)).Warnings, Does.Contain("日程が日付の範囲外"));
    }

    [TestCase(0), TestCase(3)]
    public void RemainingOverridesEstimateWithOrWithoutActual(int actual)
    {
        Dates(Run(Task(work: 80) with { Remaining = 4, Actual = actual, Start = Monday }), "2026-10-05", "2026-10-05");
    }

    [Test]
    public void ProgressWithMissingRemainingUsesEstimateAndKeepsHistoricalStartDespiteConstraints()
    {
        var result = Calculate([Task(work: 40), Task(2, 4) with
        {
            Actual = 2, Start = Monday.AddDays(-3), StartNoEarlierThan = Monday.AddDays(10), Predecessors = ["issue:1"]
        }], Settings with { ProjectStart = Monday.AddDays(2) })[1];
        Dates(result, "2026-10-02", "2026-10-05");
        Assert.That(result.Warnings, Does.Contain("先行タスクより前に開始"));
    }

    [Test]
    public void MissingProgressStartUsesAllEarliestConstraints()
    {
        var result = Run(Task() with { Actual = 2, Remaining = 4, StartNoEarlierThan = Monday.AddDays(2) });
        Dates(result, "2026-10-07", "2026-10-07");
        Assert.That(result.StartReason, Is.EqualTo("開始日指定 10/7"));
    }

    [Test]
    public void ClosedIssueWinsOverFixedAndZeroPlannedWork()
    {
        var result = Run(PlanEdits.Estimate(Task(work: null) with { Fixed = true, Closed = true }, 0));
        Dates(result, null, null);
        Assert.That(result.StartReason, Is.EqualTo("完了"));
        Assert.That(result.Warnings, Does.Contain("完了タスクの終了日なし"));
    }

    [Test]
    public void MissingKeptDatesClearBaselineAndSummaryOfUndatedChildrenStaysUndated()
    {
        var result = Calculate([Task(), Task(2, null) with { Parent = "issue:1", GitHubStart = Monday, GitHubEnd = Monday }], Settings);
        Dates(result[0], null, null);
        Dates(result[1], null, null);
        Assert.That(result[1].Start.DiffersFromGitHub && result[1].End.DiffersFromGitHub, Is.True);
    }

    [Test]
    public void EqualPredecessorsUseSmallestRowIdBeforeOtherEqualConstraints()
    {
        var rows = new[] { Task(20, 0), Task(10, 0), Task(30) with { Predecessors = ["issue:20", "issue:10", "issue:10"], StartNoEarlierThan = Monday } };
        var result = Calculate(rows, Settings with { ProjectStart = Monday })[2];
        Assert.That(result.StartReason, Is.EqualTo("#10 の終了後"));
        Dates(result, "2026-10-05", "2026-10-05");
    }

    [Test]
    public void SummaryRetainsInputsAndLastChildRemovalRestoresLeafMeaning()
    {
        var parent = Task() with { Fixed = true, Start = Monday.AddDays(2), End = Monday.AddDays(3), Remaining = 16 };
        var summary = Calculate([parent, Task(2, 4) with { Parent = parent.Identity }], Settings)[0];
        Dates(summary, "2026-10-05", "2026-10-05");
        Assert.That(summary.Input, Is.EqualTo(parent));
        Dates(Run(summary.Input), "2026-10-07", "2026-10-08");
    }

    [Test]
    public void InheritedPredecessorsWarnFixedDescendantsWithoutMovingThem()
    {
        var result = Calculate([Task(), Task(2) with { Predecessors = ["issue:1"] }, Task(3) with { Parent = "issue:2", Fixed = true, Start = Monday, End = Monday }], Settings);
        Dates(result[2], "2026-10-05", "2026-10-05");
        Assert.That(result[2].Warnings, Does.Contain("先行タスクより前に開始"));
    }

    [Test]
    public void SingleUnconfiguredAssigneeUsesDefaultRateAndExternalParentWarns()
    {
        var result = Run(Task() with { Assignees = ["unknown"], Parent = "outside" });
        Dates(result, "2026-10-05", "2026-10-05");
        Assert.That(result.Warnings, Does.Contain("プロジェクト外の親タスク"));
    }

    [Test]
    public void FractionalWorkAndDifferentSuccessorCalendarPreserveInternalEndpoint()
    {
        var result = Calculate([Task(work: 1.5m), Task(2, 6.5m) with { Predecessors = ["issue:1"] }, Task(3, 1) with { Predecessors = ["issue:2"], Assignees = ["p"] }],
            Settings with { People = [new("p") { DaysOff = new HashSet<DateOnly> { Monday.AddDays(1) } }] });
        Dates(result[1], "2026-10-05", "2026-10-05");
        Dates(result[2], "2026-10-07", "2026-10-07");
    }

    [Test]
    public void LongChainIsCalculatedRegardlessOfInputOrder()
    {
        var rows = Enumerable.Range(1, 1000).Reverse().Select(i => Task(i, 0.01m) with { Predecessors = i == 1 ? [] : [$"issue:{i - 1}"] }).ToArray();
        var result = Calculate(rows, Settings);
        Dates(result[0], "2026-10-06", "2026-10-06");
        Assert.That(result[0].Input.RowId, Is.EqualTo(1000));
    }
    [Test]
    public void TypingZeroEstimateOnNewTaskCreatesMilestoneAfterPredecessor()
    {
        var input = PlanEdits.Estimate(Task(2, null) with { Predecessors = ["issue:1"] }, 0);
        var result = Calculate([Task(work: 16), input], Settings)[1];
        Assert.That(input.Remaining, Is.Zero);
        Dates(result, "2026-10-06", "2026-10-06");
        Assert.That(result.StartReason, Is.EqualTo("#1 の終了後"));
        Assert.That(result.Start.Origin, Is.EqualTo(DateOrigin.Calculated));
    }

    [TestCase(0, 0, null, false, true)]
    [TestCase(0, null, 0, false, true)]
    [TestCase(null, 0, null, false, true)]
    [TestCase(8, 0, null, false, false)]
    [TestCase(null, 0, 3, false, false)]
    [TestCase(0, 0, 3, false, false)]
    [TestCase(0, 0, null, true, false)]
    [TestCase(null, null, null, true, false)]
    public void ZeroPlannedWorkIsMilestoneButFinishedWorkOrClosedIssueIsComplete(int? estimate, int? remaining, int? actual, bool closed, bool milestone)
    {
        var input = Task(work: estimate) with { Remaining = remaining, Actual = actual, Closed = closed, Start = Monday.AddDays(-4), End = Monday.AddDays(-3) };
        var result = Run(input);
        Dates(result, milestone ? "2026-10-05" : "2026-10-01", milestone ? "2026-10-05" : "2026-10-02");
        Assert.That(result.StartReason, Is.EqualTo(milestone ? "状況日" : "完了"));
        Assert.That(result.End.Origin, Is.EqualTo(milestone ? DateOrigin.Calculated : DateOrigin.Kept));
    }

    [TestCase("Estimate", "工数が負の値")]
    [TestCase("Remaining", "工数が負の値")]
    [TestCase("Actual", "工数が負の値")]
    [TestCase("Fixed dates", "開始日が終了日より後")]
    [TestCase("Overflow", "日程が日付の範囲外")]
    [TestCase("Unknown remaining", "残が未入力")]
    public void BadRefreshedRowKeepsGitHubDatesAndCannotMoveSuccessors(string defect, string warning)
    {
        var input = Task() with { GitHubStart = Monday.AddDays(10), GitHubEnd = Monday.AddDays(11), Start = Monday, End = Monday };
        input = defect switch
        {
            "Estimate" => input with { Estimate = -1 },
            "Remaining" => input with { Remaining = -1 },
            "Actual" => input with { Actual = -1 },
            "Fixed dates" => input with { Fixed = true, Start = Monday.AddDays(1) },
            "Unknown remaining" => input with { Actual = 3, Estimate = 0 },
            _ => input with { Estimate = decimal.MaxValue }
        };
        var result = Calculate([input, Task(2) with { Predecessors = [input.Identity] }, Task(3)], Settings);
        Dates(result[0], "2026-10-15", "2026-10-16");
        Assert.That(result[0].StartReason, Is.EqualTo("入力エラー"));
        Assert.That(result[0].Warnings, Does.Contain(warning));
        Assert.That(result[0].Start.Origin, Is.EqualTo(DateOrigin.Kept));
        Assert.That(result[0].End.Origin, Is.EqualTo(DateOrigin.Kept));
        Assert.That(result[0].Start.DiffersFromGitHub || result[0].End.DiffersFromGitHub, Is.False);
        Assert.That(result[0].Input, Is.EqualTo(input));
        Dates(result[1], "2026-10-05", "2026-10-05");
        Dates(result[2], "2026-10-05", "2026-10-05");
    }

    [Test]
    public void InvertedGitHubDatesStayVisibleAsReceivedWithoutDependencyEndpoints()
    {
        var bad = Task() with { Fixed = true, Start = Monday.AddDays(3), End = Monday, GitHubStart = Monday.AddDays(3), GitHubEnd = Monday };
        var result = Calculate([bad, Task(2) with { Predecessors = [bad.Identity] }], Settings);
        Dates(result[0], "2026-10-08", "2026-10-05");
        Assert.That(result[0].Warnings, Does.Contain("開始日が終了日より後"));
        Dates(result[1], "2026-10-05", "2026-10-05");
    }

    [Test]
    public void BadChildIsolatesAncestorRollupsButOtherChildrenAndSuccessorsStillCalculate()
    {
        var result = Calculate([Task(10), Task(20) with { Parent = "issue:10" },
            Task(1, -1) with { Parent = "issue:20", GitHubStart = Monday.AddDays(10), GitHubEnd = Monday.AddDays(20) },
            Task(2) with { Parent = "issue:20" }, Task(3) with { Predecessors = ["issue:10"] }], Settings);
        foreach (var parent in result.Take(2))
        {
            Assert.That(parent.IsSummary, Is.True);
            Assert.That(parent.Warnings, Does.Contain("子タスクに入力エラー"));
            Assert.That(parent.Estimate, Is.Null);
            Assert.That(parent.Remaining, Is.Null);
            Assert.That(parent.Actual, Is.Null);
            Dates(parent, null, null);
        }
        Dates(result[3], "2026-10-05", "2026-10-05");
        Dates(result[4], "2026-10-05", "2026-10-05");
    }

    [Test]
    public void InvalidSummaryInputDoesNotStopItsValidChild()
    {
        var result = Calculate([Task(1, -1), Task(2) with { Parent = "issue:1" }], Settings);
        Assert.That(result[0].Warnings, Does.Contain("工数が負の値"));
        Assert.That(result[0].Estimate, Is.Null);
        Dates(result[1], "2026-10-05", "2026-10-05");
    }

    [Test]
    public void SummaryEffortOverflowIsARowWarning()
    {
        var result = Calculate([Task(), Task(2, decimal.MaxValue) with { Remaining = 1, Parent = "issue:1" }, Task(3, decimal.MaxValue) with { Remaining = 1, Parent = "issue:1" }], Settings);
        Assert.That(result[0].Warnings, Does.Contain("工数の集計が範囲外"));
        Dates(result[0], null, null);
        Dates(result[1], "2026-10-05", "2026-10-05");
        Dates(result[2], "2026-10-05", "2026-10-05");
    }

    [TestCase("Estimate"), TestCase("Remaining"), TestCase("Actual")]
    public void NegativeTypedEffortIsRefusedWithoutReplacingInput(string field)
    {
        var original = Task() with { Remaining = 6, Actual = 2 };
        Assert.Throws<ArgumentException>(() => Edit(-1));
        Assert.That((original.Estimate, original.Remaining, original.Actual), Is.EqualTo((8m, 6m, 2m)));
        var corrected = Edit(0);
        Assert.That(field switch { "Estimate" => corrected.Estimate, "Remaining" => corrected.Remaining, _ => corrected.Actual }, Is.Zero);
        Assert.That(Edit(null), Is.Not.Null);
        PlanTask Edit(decimal? value) => field switch
        {
            "Estimate" => PlanEdits.Estimate(original, value),
            "Remaining" => PlanEdits.Remaining(original, value),
            _ => PlanEdits.Actual(original, value)
        };
    }

    [TestCase(true), TestCase(false)]
    public void InvertedTypedDatePairIsRefusedWithoutChangingInput(bool start)
    {
        var original = Task() with { Start = Monday, End = Monday.AddDays(1), Fixed = start };
        Assert.Throws<ArgumentException>(() => { _ = start ? PlanEdits.Start(original, Monday.AddDays(2)) : PlanEdits.End(original, Monday.AddDays(-1)); });
        Assert.That(original.Start, Is.EqualTo(Monday));
        Assert.That(original.End, Is.EqualTo(Monday.AddDays(1)));
        Assert.That(original.Fixed, Is.EqualTo(start));
    }

    [Test]
    public void ValidCorrectionDoesNotRequireOtherBadRefreshedFieldsToBeFixedFirst()
    {
        var corrected = PlanEdits.Remaining(Task(work: -1) with { Remaining = -2 }, 8);
        Assert.That(corrected.Remaining, Is.EqualTo(8));
        Assert.That(Run(corrected).Warnings, Does.Contain("工数が負の値"));
    }

    [Test]
    public void UnchosenStatusDateUsesCallersTodayOnEveryCalculation()
    {
        var settings = new PlanSettings();
        Assert.That(settings.StatusDate, Is.Null);
        Dates(PlanScheduler.Calculate([Task()], settings, D("2026-10-06"))[0], "2026-10-06", "2026-10-06");
        Dates(PlanScheduler.Calculate([Task()], settings, D("2026-10-13"))[0], "2026-10-13", "2026-10-13");
    }

    [Test]
    public void ExplicitStatusDateStaysUntilClearedThenUsesToday()
    {
        var settings = Settings;
        Dates(PlanScheduler.Calculate([Task()], settings, D("2026-10-13"))[0], "2026-10-05", "2026-10-05");
        Dates(PlanScheduler.Calculate([Task()], settings with { StatusDate = null }, D("2026-10-13"))[0], "2026-10-13", "2026-10-13");
    }

    [TestCase(30, 3), TestCase(70, 7), TestCase(90, 9)]
    public void FractionalRateChainFillsExactlyOneDayAndNextTaskStartsNextDay(int rate, int count)
    {
        var rows = Enumerable.Range(1, count + 1).Select(i => Task(i, i <= count ? 0.8m : 0.1m) with
        { Assignees = ["p"], Predecessors = i == 1 ? [] : [$"issue:{i - 1}"] }).ToArray();
        var result = Calculate(rows, Settings with { People = [new("p", rate)] });
        Dates(result[count - 1], "2026-10-05", "2026-10-05");
        Dates(result[count], "2026-10-06", "2026-10-06");
    }

    [TestCase(30, 70, "0.1", 3)]
    [TestCase(70, 90, "0.7", 1)]
    [TestCase(90, 30, "0.3", 3)]
    public void ExactFractionalEndpointsSurviveRateChanges(int firstRate, int secondRate, string firstWork, int count)
    {
        var rows = Enumerable.Range(1, count).Select(i => Task(i, decimal.Parse(firstWork, System.Globalization.CultureInfo.InvariantCulture)) with
        { Assignees = ["p"], Predecessors = i == 1 ? [] : [$"issue:{i - 1}"] }).ToList();
        rows.Add(Task(count + 1, 7m * secondRate / 100m) with { Assignees = ["q"], Predecessors = [$"issue:{count}"] });
        rows.Add(Task(count + 2, 0.1m) with { Predecessors = [$"issue:{count + 1}"] });
        var result = Calculate(rows, Settings with { People = [new("p", firstRate), new("q", secondRate)] });
        Dates(result[count], "2026-10-05", "2026-10-05");
        Dates(result[count + 1], "2026-10-06", "2026-10-06");
    }

    [TestCase("2.4000000000000000000000000001", "2026-10-06")]
    [TestCase("2.3999999999999999999999999999", "2026-10-05")]
    public void TrueWorkOnEitherSideOfDayBoundaryIsNotRoundedAway(string work, string end)
    {
        var result = Run(Task(work: decimal.Parse(work, System.Globalization.CultureInfo.InvariantCulture)) with { Assignees = ["p"] }, Settings with { People = [new("p", 30)] });
        Dates(result, "2026-10-05", end);
    }
    [Test]
    public void MovingAutomaticStartPastOldEndRecalculatesRatherThanRefusesEdit()
    {
        var input = Task() with { Start = Monday, End = Monday, GitHubStart = Monday, GitHubEnd = Monday };
        var edited = PlanEdits.Start(input, Monday.AddDays(1));
        var result = Run(edited);
        Dates(result, "2026-10-06", "2026-10-06");
        Assert.That(result.StartReason, Is.EqualTo("開始日指定 10/6"));
        Assert.That(result.End.Origin, Is.EqualTo(DateOrigin.Calculated));
        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void AutomaticDatesIgnoreAnObsoleteInvertedPairWhenRecalculating()
    {
        var input = Task() with { Start = Monday.AddDays(1), End = Monday, GitHubStart = Monday.AddDays(1), GitHubEnd = Monday };
        var result = Run(input);
        Dates(result, "2026-10-05", "2026-10-05");
        Assert.That(result.Start.Origin, Is.EqualTo(DateOrigin.Calculated));
        Assert.That(result.Warnings, Is.Empty);
    }
    [Test, Explicit("Deterministic timing experiment, not a CI timing gate")]
    public void ThousandTasksReportTwentyRecalculationSamples()
    {
        var rows = Enumerable.Range(1, 1000).Select(i => Task(i, 4 + i % 17) with
        {
            Remaining = i % 11 == 0 ? 0 : 4 + i % 13,
            Actual = i % 3 == 0 ? 4 : 0,
            Start = i % 3 == 0 ? Monday.AddDays(-3) : null,
            End = i % 11 == 0 ? Monday.AddDays(-1) : null,
            Assignees = [$"p{i % 20}"],
            Predecessors = i % 10 == 1 ? [] : [$"issue:{i - 1}"]
        }).ToArray();
        var settings = Settings with { People = Enumerable.Range(0, 20).Select(i => new PlanPerson($"p{i}", i % 4 == 0 ? 50 : 100)).ToArray() };
        for (var i = 0; i < 5; i++) Calculate(rows, settings);
        var samples = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var watch = Stopwatch.StartNew();
            var result = Calculate(rows, settings);
            watch.Stop(); samples.Add(watch.Elapsed.TotalMilliseconds);
            Assert.That(result.Count, Is.EqualTo(1000));
        }
        var ordered = samples.Order().ToArray();
        TestContext.Out.WriteLine($"1000 tasks, 20 people, 900 FS edges; 5 warmups; samples_ms={string.Join(',', samples.Select(s => s.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))}");
        TestContext.Out.WriteLine($"median_ms={(ordered[9] + ordered[10]) / 2:F3}; max_ms={ordered[^1]:F3}; runtime={Environment.Version}; OS={Environment.OSVersion}; processors={Environment.ProcessorCount}");
    }
}

