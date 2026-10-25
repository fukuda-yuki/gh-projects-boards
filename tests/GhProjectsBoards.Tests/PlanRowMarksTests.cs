using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #264: the 状態 column holds one mark in the agreed priority, and 見積超過/未発行 are not in it.
// The Core decides which case applies and the wording; the App only maps the case to a shape and colour.
[TestFixture]
internal sealed class PlanRowMarksTests
{
    private static DateOnly D(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd");
    private static readonly DateOnly Status = D("2026-10-05");
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");
    private static PlanRow Row(string id, string title = "") => new(id, title.Length == 0 ? id : title, "acme/work");
    private static PlanDocument Document(params PlanRow[] rows)
        => new(Project, new(rows.ToImmutableArray(), []),
            new(rows.ToImmutableArray(), new() { StatusDate = Status }));

    private static (PlanRowMarkText Mark, ScheduledTask Task) Describe(PlanDocument document, string identity,
        PlanMarkVisibility? marks = null, IReadOnlyList<string>? conflicts = null, string remoteProblem = "")
    {
        var scheduled = PlanOperations.Schedule(document, Status).ToArray();
        var lateness = PlanLateness.Classify(scheduled, new PlanCalendar(), document.State.Settings.StatusDate ?? Status);
        var task = scheduled.Single(t => t.Input.Identity == identity);
        return (PlanSheetText.Mark(PlanTaskNames.Of(document), document.State.Rows.Single(r => r.Identity == identity), task,
            lateness.Tasks.GetValueOrDefault(identity), remoteProblem, marks, conflicts), task);
    }

    [Test]
    public void AnOrdinaryRowCarriesNoMark()
    {
        var document = Document(Row("task") with { Start = D("2026-10-05"), End = D("2026-10-09"), Estimate = 8, Remaining = 8 });

        var (mark, _) = Describe(document, "task");

        Assert.Multiple(() =>
        {
            Assert.That(mark.Mark, Is.EqualTo(PlanRowMark.None));
            Assert.That(mark.Text, Is.Empty);
        });
    }

    [TestCase("finish", TestName = "APassedFinishDateIsTheRedLateMarkAndAddsTheForecastSlip")]
    [TestCase("start", TestName = "APassedStartDateIsTheRedLateMark")]
    public void APassedDateIsTheRedLateMark(string kind)
    {
        // A read day of 10/6 puts the 10/5 plan end behind the 基準日; an unstarted 10/1 start is behind it too.
        var row = kind == "finish"
            ? Row("task") with { Start = D("2026-10-01"), End = D("2026-10-05"), Estimate = 24, Actual = 8, Remaining = 16 }
            : Row("task") with { Start = D("2026-10-01"), End = D("2026-10-20") };
        var document = Document(row) with { State = Document(row).State with { Settings = Document(row).State.Settings with { StatusDate = D("2026-10-06") } } };

        var (mark, _) = Describe(document, "task");

        Assert.Multiple(() =>
        {
            Assert.That(mark.Mark, Is.EqualTo(PlanRowMark.LatePassed));
            Assert.That(mark.Text, Is.EqualTo(kind == "finish" ? "終了日 10/5 を過ぎて未完了 · +2日" : "開始日 10/1 を過ぎて未着手"));
        });
    }

    [Test]
    public void AForecastAfterThePlanEndIsTheOchreLateMarkAndNamesTheCause()
    {
        var document = Document(Row("task") with { Start = D("2026-10-01"), End = D("2026-10-05"), Estimate = 24, Actual = 8, Remaining = 16 });

        var (mark, _) = Describe(document, "task");

        Assert.Multiple(() =>
        {
            Assert.That(mark.Mark, Is.EqualTo(PlanRowMark.LateForecast));
            Assert.That(mark.Text, Is.EqualTo("見込終了 10/6 (火) · +1日（自分の残による）"));
        });
    }

    [Test]
    public void AForecastPushedByAPredecessorNamesThatTask()
    {
        var document = Document(
            Row("before") with { Start = D("2026-10-01"), End = D("2026-10-07"), Estimate = 40, Remaining = 40 },
            Row("task") with { Start = D("2026-10-05"), End = D("2026-10-05"), Estimate = 8, Remaining = 8, Predecessors = ["before"] });
        document = document with { Sync = document.Sync with { IssueLinks = document.Sync.IssueLinks.SetItem("before",
            new("acme/repo#193", "https://github.com/acme/repo/issues/193")) } };

        var (mark, _) = Describe(document, "task");

        Assert.Multiple(() =>
        {
            Assert.That(mark.Mark, Is.EqualTo(PlanRowMark.LateForecast));
            Assert.That(mark.Text, Is.EqualTo("見込終了 10/13 (火) · +3日（先行 #193 の遅れによる）"));
        });
    }

    [Test]
    public void ThePriorityIsFailureConflictPassedForecastSetAsideDone()
    {
        var failed = Document(Row("task") with { Start = D("2026-10-01"), End = D("2026-10-05"), Estimate = 8, Remaining = 8 }) with
        {
            Sync = new() { Failures = [new("task", PlanField.Title, "VerificationMismatch")] }
        };
        var conflicted = Document(Row("task") with { Start = D("2026-10-01"), End = D("2026-10-05"), Estimate = 8, Remaining = 8 });
        var passed = Document(Row("task") with { Start = D("2026-10-01"), End = D("2026-10-05"), Estimate = 8, Remaining = 8 });
        var done = Document(Row("task") with { Start = D("2026-10-01"), End = D("2026-10-01"), Estimate = 8, Closed = true, CloseDate = D("2026-10-01") });
        var cancelled = Document(Row("task") with { Start = D("2026-10-05"), End = D("2026-10-05"), Estimate = 8, Remaining = 8, Cancelled = true });

        Assert.Multiple(() =>
        {
            Assert.That(Describe(failed, "task", conflicts: ["この PC 20h ／ GitHub 16h"], remoteProblem: "発行に失敗: GitHubへの反映を確認できません").Mark.Mark,
                Is.EqualTo(PlanRowMark.Failure), "A publication failure outranks a conflict and lateness.");
            Assert.That(Describe(conflicted, "task", conflicts: ["この PC 20h ／ GitHub 16h"]).Mark.Mark, Is.EqualTo(PlanRowMark.Conflict),
                "A conflict outranks lateness.");
            Assert.That(Describe(passed, "task").Mark.Mark, Is.EqualTo(PlanRowMark.LatePassed));
            Assert.That(Describe(cancelled, "task").Mark.Mark, Is.EqualTo(PlanRowMark.SetAside));
            Assert.That(Describe(cancelled, "task").Mark.Text, Is.EqualTo("中止"));
            Assert.That(Describe(done, "task").Mark.Mark, Is.EqualTo(PlanRowMark.Done));
            Assert.That(Describe(done, "task").Mark.Text, Is.EqualTo("完了"));
        });
    }

    [Test]
    public void ConflictTextNamesBothValues()
    {
        var document = Document(Row("task") with { Start = D("2026-10-05"), End = D("2026-10-05"), Estimate = 8, Remaining = 8 });

        var (mark, _) = Describe(document, "task", conflicts: ["この PC 20h ／ GitHub 16h"]);

        Assert.That(mark.Text, Is.EqualTo("競合: この PC 20h ／ GitHub 16h"));
    }

    [Test]
    public void AnIsolatedRowIsAnInputError()
    {
        var document = Document(Row("task") with { Estimate = -8, Start = D("2026-10-05"), End = D("2026-10-09") });

        var (mark, _) = Describe(document, "task");

        Assert.Multiple(() =>
        {
            Assert.That(mark.Mark, Is.EqualTo(PlanRowMark.Failure));
            Assert.That(mark.Text, Is.EqualTo("入力エラー: 工数が負の値"));
        });
    }

    [Test]
    public void OverEstimateIsTheEstimateCellFigureAndNotAStateMark()
    {
        var document = Document(Row("task") with { Start = D("2026-10-05"), End = D("2026-10-09"), Estimate = 8, Actual = 16, Remaining = 16 });

        var (mark, task) = Describe(document, "task");

        Assert.Multiple(() =>
        {
            Assert.That(mark.Mark, Is.EqualTo(PlanRowMark.None), "見積超過 is not in 状態; this task's dates leave it on plan.");
            Assert.That(mark.Text, Is.Empty);
            Assert.That(PlanSheetText.EstimateOverrun(task), Is.EqualTo("+24h"));
            Assert.That(PlanSheetText.EstimateOverrunWording(task), Is.EqualTo("見積超過: 見込 32h（見積 8h）"));
            Assert.That(PlanSheetText.EstimateOverrun(task, new PlanMarkVisibility(OverEstimate: false)), Is.Null,
                "The 見積超過の印 switch hides the cell figure.");
            Assert.That(PlanSheetText.EstimateOverrunWording(task, new PlanMarkVisibility(OverEstimate: false)), Is.Null);
        });
    }

    [Test]
    public void ARequirementRowCarriesTheStrongestDescendantMarkAndItsCounts()
    {
        var parent = Row("summary", "R01 受注");
        var overdue = Row("overdue") with { Parent = "summary", Start = D("2026-10-01"), End = D("2026-10-01"), Estimate = 8, Remaining = 8 };
        var over = Row("over") with { Parent = "summary", Start = D("2026-10-05"), End = D("2026-10-09"), Estimate = 8, Actual = 16, Remaining = 16 };
        var document = Document(parent, overdue, over);

        var (mark, _) = Describe(document, "summary");

        Assert.Multiple(() =>
        {
            Assert.That(mark.Mark, Is.EqualTo(PlanRowMark.LatePassed), "A passed descendant's red mark wins.");
            Assert.That(mark.Text, Is.EqualTo("配下: 遅れ 1 · 見積超過 1"));
        });
    }

    [Test]
    public void ARequirementRowWithOnlyForecastLatenessCarriesTheOchreMark()
    {
        var parent = Row("summary", "R01 受注");
        var later = Row("later") with { Parent = "summary", Start = D("2026-10-01"), End = D("2026-10-05"), Estimate = 24, Actual = 8, Remaining = 16 };
        var document = Document(parent, later);

        var (mark, _) = Describe(document, "summary");

        Assert.Multiple(() =>
        {
            Assert.That(mark.Mark, Is.EqualTo(PlanRowMark.LateForecast));
            Assert.That(mark.Text, Is.EqualTo("配下: 遅れ 1"));
        });
    }

    [Test]
    public void HidingTheRedMarkRemovesTheRedWordingButKeepsTheLatenessSet()
    {
        var document = Document(Row("task") with { Start = D("2026-10-01"), End = D("2026-10-05"), Estimate = 8, Remaining = 8 }) with
        {
            State = Document(Row("task") with { Start = D("2026-10-01"), End = D("2026-10-05"), Estimate = 8, Remaining = 8 }).State with
            {
                Settings = Document(Row("task") with { Start = D("2026-10-01"), End = D("2026-10-05"), Estimate = 8, Remaining = 8 }).State.Settings
                    with { StatusDate = D("2026-10-06") }
            }
        };

        var (hidden, _) = Describe(document, "task", new PlanMarkVisibility(Overdue: false));

        Assert.Multiple(() =>
        {
            Assert.That(hidden.Mark, Is.EqualTo(PlanRowMark.None));
            Assert.That(hidden.Text, Is.Empty);
            Assert.That(PlanLateness.Classify(PlanOperations.Schedule(document, D("2026-10-06")).ToArray(), new PlanCalendar(), D("2026-10-06")).LateTasks,
                Is.EqualTo(1), "The set and its count are unaffected by a hidden mark.");
        });
    }
}
