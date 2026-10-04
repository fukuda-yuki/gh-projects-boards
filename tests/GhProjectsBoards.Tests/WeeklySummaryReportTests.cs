using System.Globalization;
using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class WeeklySummaryReportTests
{
    [Test]
    public void ReportShowsDatedCumulativeWorkAndOverloadWithoutInventingWeeklyProductionOrChangingWork()
    {
        var (project, work) = SummaryTests.Example();
        work.SetAllowance(project, "A", 160, work.Revision); work.SetAllowance(project, "B", 80, work.Revision);
        var before = JsonSerializer.Serialize(work.Snapshot());

        var text = WeeklySummaryReport.Create(SummaryProjection.Create(work, project, SummaryTests.Day));

        Assert.That(text, Does.Contain("報告基準: 2026-09-18"));
        Assert.That(text, Does.Contain("累計実績 | 13 |"));
        Assert.That(text, Does.Contain("今週の増分: 未算出（週初の実績記録なし）"));
        Assert.That(text, Does.Contain("見積 | 26 |"));
        Assert.That(text, Does.Contain("残り | 14 |"));
        Assert.That(text, Does.Contain("完了見込み | 27 |"));
        Assert.That(text, Does.Contain("### B — 超過 2 人日"));
        Assert.That(text, Does.Contain("| 7 | 5 | 12 | 2026-09-18 |"));
        Assert.That(text, Does.Contain("対策: 未記入"));
        Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
    }

    [TestCase(-1, "13（小計・古い報告 1件）", "2026-09-17")]
    [TestCase(1, "7（小計・未確認 1件）", "2026-09-19")]
    public void OldAndFutureReportsKeepIncompleteAmountsAndDatesInsteadOfClaimingCompleteHeadroom(int offset, string total, string date)
    {
        var (project, work) = SummaryTests.Example(); var plan = work.Planning("P1")!;
        work.CommitPlanning(project, plan with { Tasks = [plan.Tasks[0] with { Actuals = [new("A", 48, SummaryTests.Day.AddDays(offset))] }, plan.Tasks[1]] }, work.Revision);
        work.SetAllowance(project, "A", 16, work.Revision);

        var text = WeeklySummaryReport.Create(SummaryProjection.Create(work, project, SummaryTests.Day));

        Assert.That(text, Does.Contain($"累計実績 | {total} |"));
        Assert.That(text, Does.Contain(date));
        Assert.That(text, Does.Contain("比較未完"));
        Assert.That(text, Does.Not.Contain("### A — 超過"));
    }

    [Test]
    public void MissingValuesAndUnpublishedWorkRemainExplicitAndRollupsAreNotPresentedAsContributingOverload()
    {
        var (project, work) = SummaryTests.Example();
        var local = work.AppendRows(project with { DefaultRepository = "owner/repo" }, "New local task\tTodo\t8").Single();
        var summary = SummaryProjection.Create(work, project, SummaryTests.Day);
        var rollup = summary.Contributions[0] with { TaskId = "rollup", Title = "Excluded rollup", IncludedInTotals = false };
        summary = summary with { Contributions = summary.Contributions.Append(rollup).ToArray() };

        var text = WeeklySummaryReport.Create(summary);

        Assert.That(text, Does.Contain("未公開 1件"));
        Assert.That(text, Does.Contain("不明"));
        Assert.That(text, Does.Contain("投入可能工数未設定"));
        Assert.That(text, Does.Not.Contain("Excluded rollup"));
        Assert.That(text, Does.Not.Contain(local));
    }

    [Test]
    public void MarkdownTreatsTitlesNamesAndIdentitiesAsLiteralCellsAndUsesPortableNumbers()
    {
        var (project, work) = SummaryTests.Example(); work.SetAllowance(project, "B", 79, work.Revision);
        var summary = SummaryProjection.Create(work, project, SummaryTests.Day);
        summary = summary with {
            ProjectTitle = "Title | *bold*\r\n<script>&",
            People = summary.People.Select(p => p.Id == "B" ? p with { Name = "B [name]" } : p).ToArray(),
            Contributions = summary.Contributions.Select(c => c.PersonId == "B" ? c with { Title = "Task | `code`\n# heading", Identity = "repo#2 <url>" } : c).ToArray()
        };
        var previous = CultureInfo.CurrentCulture;
        string text;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); text = WeeklySummaryReport.Create(summary); }
        finally { CultureInfo.CurrentCulture = previous; }

        Assert.That(text, Does.Contain("Title \\| \\*bold\\* &lt;script&gt;&amp;"));
        Assert.That(text, Does.Contain("B \\[name\\] — 超過 2.125 人日"));
        Assert.That(text, Does.Contain("Task \\| \\`code\\` \\# heading"));
        Assert.That(text, Does.Contain("repo\\#2 &lt;url&gt;"));
        Assert.That(text, Does.Not.Contain("<script>").And.Not.Contain("2,125"));
    }
}
