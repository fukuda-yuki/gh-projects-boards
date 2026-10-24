using System.Collections.Immutable;
using System.Xml.Linq;
using GhProjectsBoards.Core.Reports;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;

internal sealed class ProgressReportTests
{
    private static DateOnly D(string text) => DateOnly.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    private static ProgressEffort Effort(int tasks, int closed, decimal? progress) => new(tasks, closed, 80, 40, 40, 80, 0, progress, 0);
    private static ProgressTask Row(int number, string title, ProgressLate? late = null, bool cause = false, bool overEstimate = false) =>
        new($"acme/repo#{number}", $"https://github.com/acme/repo/issues/{number}", title, "acme/repo#1", "PS", "PS 完了", ["person-a"],
            16, 8, 16, D("2027-01-04"), D("2027-01-12"), D("2027-01-14"), false, null, late, cause, overEstimate);
    private static ProgressSnapshot Snapshot(string status, DateTimeOffset readAt, string psFinish, int? difference = 3, int owner = 0)
    {
        var tasks = ImmutableArray.Create(
            Row(2, "Finish | passed", new("finish", D("2027-01-12"), 1), cause: true),
            Row(3, "[Not] started", new("start", D("2027-01-09"), null), cause: true),
            Row(4, "Forecast later", new("forecast", D("2027-01-14"), 2), cause: true, overEstimate: true));
        return new(ProgressSnapshot.SchemaV1, readAt, D(status), "Asia/Tokyo",
            new("github.com", owner == 0 ? "acme" : "other", 3, "第2027.04版", "https://github.com/users/acme/projects/3"),
            new(D("2026-10-13"), D("2027-04-30")), D("2027-05-04"), 2, Effort(3, 0, 0.5m),
            [new("PS", "PS 完了", D("2027-01-08"), D("2026-12-01"), D(psFinish), difference, Effort(3, 0, 0.9999m), 3, 1),
             new("PG", null, null, null, null, null, Effort(0, 0, null), 0, 0)],
            new(tasks.Select(t => t.Issue).ToImmutableArray(), tasks.Select(t => t.Issue).ToImmutableArray(), ["acme/repo#4"]),
            [new("acme/repo#1", "https://github.com/acme/repo/issues/1", "R01")], tasks);
    }

    [Test]
    public void ReportStatesTheVerdictPhasesAndEachLateReasonInTheAppsWording()
    {
        var report = ProgressReport.Render([Snapshot("2027-01-13", new(2027, 1, 12, 21, 5, 0, TimeSpan.Zero), "2027-01-13")]).Markdown;

        Assert.That(report, Does.StartWith("# 第2027.04版 進捗レポート\n"));
        Assert.That(report, Does.Contain("基準日 2027-01-13 (水) · 取得 2027-01-13 06:05 (Asia/Tokyo)"));
        Assert.That(report, Does.Contain("**完了見込 2027-05-04 (火)** · 終了日 2027-04-30 (金) より 2 稼働日遅れ"));
        Assert.That(report, Does.Contain("| PS | 2027-01-08 (金) | 2027-01-13 (水) | +3 | 0 / 3 | 99% | 3 | 1 |"),
            "A phase with open tasks never reads 100%.");
        Assert.That(report, Does.Contain("| PG | — | — | — | 0 / 0 | — | 0 | 0 |"));
        Assert.That(report, Does.Contain("## 遅れの原因（3）").And.Contain("## 見積超過（1）"));
        Assert.That(report, Does.Contain("| [acme/repo\\#2](https://github.com/acme/repo/issues/2) | Finish \\| passed | PS | person-a | 終了日 1/12 を過ぎて未完了 · 見込終了は +1日 |"));
        Assert.That(report, Does.Contain("| \\[Not\\] started | PS | person-a | 開始日 1/9 を過ぎて未着手 |"));
        Assert.That(report, Does.Contain("| Forecast later | PS | person-a | 見込終了 1/14 · +2日 |"));
        Assert.That(report, Does.Contain("| Forecast later | PS | person-a | 見積 16h → 見込 24h |"));
        Assert.That(report, Does.Contain("![工程ごとの完了見込の推移](report/phase-trend.svg)"));
    }

    [Test]
    public void LateIsCountedByCaseWhileCausesAreListedMostDaysLateFirst()
    {
        var report = ProgressReport.Render([Snapshot("2027-01-13", DateTimeOffset.UnixEpoch, "2027-01-13")]).Markdown;

        var late = Section(report, "## 遅れ（3）");
        Assert.That(late, Does.Contain("終了日を過ぎて未完了 1 · 開始日を過ぎて未着手 1 · 見込終了が遅れ 1"));
        Assert.That(late, Does.Not.Contain("|").And.Not.Contain("acme/repo"), "The full late set stays in the snapshot, not the report.");
        var causes = Section(report, "## 遅れの原因（3）");
        Assert.That(new[] { "\\#4", "\\#2", "\\#3" }.Select(issue => causes.IndexOf(issue, StringComparison.Ordinal)), Is.Ordered.And.All.GreaterThan(0),
            "Most working days late first; a cause with no known forecast slip comes last.");
    }

    private static string Section(string markdown, string heading)
    {
        var start = markdown.IndexOf(heading, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), heading);
        var end = markdown.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? markdown[start..] : markdown[start..end];
    }

    [Test]
    public void TrendHasOnePanelPerPhaseAndOnePointPerStatusDate()
    {
        var files = ProgressReport.Render([
            Snapshot("2027-01-06", new(2027, 1, 5, 21, 0, 0, TimeSpan.Zero), "2027-01-08", 0),
            Snapshot("2027-01-13", new(2027, 1, 12, 21, 0, 0, TimeSpan.Zero), "2027-01-11"),
            Snapshot("2027-01-13", new(2027, 1, 13, 3, 0, 0, TimeSpan.Zero), "2027-01-13")]);

        var svg = XDocument.Parse(files.TrendSvg).Root!;
        XNamespace ns = "http://www.w3.org/2000/svg";
        var texts = svg.Descendants(ns + "text").Select(t => t.Value).ToArray();
        Assert.That(texts, Does.Contain("PS").And.Contain("PG").And.Contain("目標 1/8").And.Contain("1/13 · +3日").And.Contain("完了見込なし"));
        Assert.That(texts, Does.Not.Contain("1/11 · +3日"), "A same-day rerun replaces that day's point.");
        Assert.That(svg.Descendants(ns + "circle").Count(), Is.EqualTo(2));
        Assert.That(svg.Descendants(ns + "polyline").Single().Attribute("points")!.Value.Split(' '), Has.Length.EqualTo(2));
        Assert.That(svg.Element(ns + "style")!.Value, Does.Contain("prefers-color-scheme:dark"));
        Assert.That(svg.Element(ns + "desc")!.Value, Does.Contain("PS: 完了見込 1/13、目標日 1/8"));
        Assert.That(files.Markdown, Does.Contain("記録 2 回分"));
    }

    [Test]
    public void SnapshotsOfDifferentProjectsAreRefused()
        => Assert.That(() => ProgressReport.Render([
                Snapshot("2027-01-06", DateTimeOffset.UnixEpoch, "2027-01-08"),
                Snapshot("2027-01-13", DateTimeOffset.UnixEpoch, "2027-01-08", owner: 1)]),
            Throws.ArgumentException.With.Message.Contains("複数のプロジェクト"));
}
