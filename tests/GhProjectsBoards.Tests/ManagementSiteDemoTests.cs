using System.Globalization;
using System.Text;
using GhProjectsBoards.Core.Reports;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #271: the demo records are the acceptance data for the management site. These tests fix the numbers the pages
// present, so a change to the demo generator or to the counting rules has to be deliberate.
[TestFixture]
internal sealed class ManagementSiteDemoTests
{
    private static readonly string[] FixedHeaders =
    [
        "種類", "タスク", "リポジトリ", "番号", "タイトル", "状態", "閉じた理由", "閉じた日", "親",
        "担当者", "ラベル", "マイルストーン", "Issue の種類", "ブロック元", "アーカイブ", "追加日", "URL",
    ];
    private static readonly string[] Forbidden = ["遅れ", "見込", "要確認", "現在の工程", "予測", "判定"];

    private static string generated = "";
    private static string raw = "";
    private static string site = "";
    private static SiteConfig configuration = null!;
    private static ProjectProgress[] projects = null!;

    [OneTimeSetUp]
    public void Generate()
    {
        generated = Path.Combine(Path.GetTempPath(), "ghpb-demo-site-" + Guid.NewGuid().ToString("N"));
        DemoData.Write(generated, Resource("VersionPlan-2027.04.json"), Resource("VersionPlan-2027.10.json"));
        raw = Path.Combine(generated, "raw");
        site = Path.Combine(generated, "site");
        SiteBuilder.Build(raw, Path.Combine(generated, "site.json"), site);
        configuration = SiteConfigReader.Parse(File.ReadAllText(Path.Combine(generated, "site.json")));
        projects = configuration.Projects.Select(project => SiteAnalysis.Compute(project, raw)).ToArray();
    }

    [OneTimeTearDown]
    public void Delete() { if (Directory.Exists(generated)) Directory.Delete(generated, true); }

    private static string Resource(string name)
    {
        using var stream = typeof(ManagementSiteDemoTests).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("埋め込みリソースが見つかりません: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static int Index(ProjectProgress project, string state) => project.States.ToList().IndexOf(state);

    private static string Page(string path) => File.ReadAllText(Path.Combine(site, path));

    private static List<string[]> Csv(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        for (var n = 1; n < text.Length; n++)
            if (text[n] == '\n') Assert.That(text[n - 1], Is.EqualTo('\r'), "Every line break in the CSV is CRLF.");
        var rows = new List<string[]>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var n = 0; n < text.Length; n++)
        {
            var c = text[n];
            if (quoted)
            {
                if (c != '"') { cell.Append(c); continue; }
                if (n + 1 < text.Length && text[n + 1] == '"') { cell.Append('"'); n++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r') { cells.Add(cell.ToString()); cell.Clear(); rows.Add(cells.ToArray()); cells.Clear(); n++; }
            else cell.Append(c);
        }
        return rows;
    }

    [Test]
    public void TheDemoFixesTheCountsOfEveryProject()
    {
        var expected = new (string Name, int Items, int Tasks, int Parents, int Closed, int Days)[]
        {
            ("第2027.04版", 1045, 1000, 40, 1000, 144),
            ("第2027.10版", 1040, 1000, 40, 720, 274),
            ("改善バックログ", 84, 84, 0, 70, 145),
            ("第2027.01版", 56, 48, 8, 0, 53),
            ("新規プロジェクト", 12, 10, 2, 4, 9),
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(projects.Select(p => p.Config.Name), Is.EqualTo(expected.Select(e => e.Name)));
            for (var n = 0; n < expected.Length; n++)
            {
                var project = projects[n];
                var parents = project.LatestItems.Where(item => item.Content?.Parent is not null)
                    .Select(item => item.Content!.Parent!).Distinct(StringComparer.Ordinal).Count();
                Assert.That((project.Config.Name, project.LatestItems.Count, project.TaskCount, parents, project.ClosedCount, project.RecordedDays),
                    Is.EqualTo(expected[n]), "The demo record's fixed counts");
            }
            var first = projects[0];
            Assert.That(first.LatestItems.Count(item => item.Type == "PULL_REQUEST"), Is.EqualTo(3));
            Assert.That(first.LatestItems.Count(item => item.Type == "DRAFT_ISSUE"), Is.EqualTo(1));
            Assert.That(first.LatestItems.Count(item => item.Type == "REDACTED"), Is.EqualTo(1));
            Assert.That(first.LatestTaskIds, Does.Not.Contain(first.LatestItems.First(item => item.Type == "PULL_REQUEST").Id),
                "PRs, drafts and REDACTED items are not tasks.");
        }
    }

    [Test]
    public void TheFinalWeekBatchClosesTasksAndLeavesClosedTasksUnderTheirStatus()
    {
        var project = projects[1];
        var last = project.Weeks[^1];
        var previous = project.Weeks[^2];
        var done = Index(project, "Done");
        var backlog = Index(project, "Backlog");
        var inProgress = Index(project, "In progress");
        var closedInProgress = project.LatestItems
            .Where(item => project.LatestTaskIds.Contains(item.Id) && RawDay.OptionName(item, "Status") == "In progress").ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(project.Weeks, Has.Count.EqualTo(55), "The twelve-month plan draws one bar per week.");
            Assert.That(SiteAnalysis.Date(last.Monday), Is.EqualTo("2027-10-25"));
            Assert.That(SiteAnalysis.Date(last.Day!.Value), Is.EqualTo("2027-10-29"));
            Assert.That(previous.Counts[done], Is.Zero, "Until the final week the batch has not happened.");
            Assert.That(project.Weeks.Take(54).Select(w => w.Counts[done]).Distinct(), Is.EqualTo(new[] { 0 }));
            Assert.That(last.Counts[done], Is.EqualTo(708));
            Assert.That(last.Counts[inProgress], Is.EqualTo(12), "Closed but not Done tasks stay under their recorded Status (decision B).");
            Assert.That(last.Counts[backlog], Is.EqualTo(280));
            Assert.That(project.ClosedCount, Is.EqualTo(720), "完了 counts the closed Issue state, not Status.");
            Assert.That(closedInProgress, Has.Length.EqualTo(12));
            Assert.That(closedInProgress.All(item => item.Content!.State == "CLOSED"));
        }
    }

    [Test]
    public void EachWeekUsesItsLastRecordedDayAndAMissingDayIsNotCarriedForward()
    {
        var waterfall = projects[0];
        var zero = projects[3];
        var done = Index(waterfall, "Done");
        var missing = zero.Weeks.Single(week => week.Monday == new DateOnly(2026, 9, 14));
        var skipped = zero.Weeks.Single(week => week.Monday == new DateOnly(2026, 8, 31));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(waterfall.Weeks, Has.Count.EqualTo(29));
            Assert.That(SiteAnalysis.Date(waterfall.Weeks[0].Monday), Is.EqualTo("2026-10-12"), "The first drawn week starts on the Monday of the first record.");
            Assert.That(SiteAnalysis.Date(waterfall.Weeks[0].Day!.Value), Is.EqualTo("2026-10-16"), "A bar is the last recorded day in its week, not the first.");
            Assert.That(waterfall.Weeks.Single(w => w.Monday == new DateOnly(2027, 4, 19)).Counts[done], Is.EqualTo(966));
            Assert.That(waterfall.Weeks[^1].Counts[done], Is.EqualTo(1000));
            Assert.That(zero.Weeks, Has.Count.EqualTo(12), "A week inside the drawn range is kept even with no record.");
            Assert.That(missing.Day, Is.Null, "A week with no record is an empty slot, not the previous values.");
            Assert.That(missing.Total, Is.Zero);
            Assert.That(zero.Weeks.Count(w => w.Day is not null), Is.EqualTo(11));
            Assert.That(SiteAnalysis.Date(skipped.Day!.Value), Is.EqualTo("2026-09-04"), "Skipped 09-01 and 09-02 do not change the week's last recorded day.");
            Assert.That(zero.Weeks.Where(w => w.Day is not null).Select(w => w.Total).Distinct(), Is.EqualTo(new[] { 48 }));
        }
    }

    [Test]
    public void TheAgileProjectShowsItsOwnStatesAndIterationsInStartDateOrder()
    {
        var project = projects[2];
        var iterations = Enumerable.Range(1, 15).Select(n => "Iteration " + n.ToString(CultureInfo.InvariantCulture)).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(project.States, Is.EqualTo(new[] { "Todo", "Doing", "Review", "Done" }), "The Project's own Status options, in their order.");
            Assert.That(project.Groups, Has.Count.EqualTo(15), "Completed iterations and the current iteration, plus no 未設定 row because every task has a value.");
            Assert.That(project.Groups.Select(row => row.Name), Is.EqualTo(iterations), "Iterations are ordered by start date.");
            Assert.That(project.Groups.Take(14).Select(row => (row.Tasks, row.Closed)), Is.All.EqualTo((5, 5)));
            Assert.That((project.Groups[^1].Tasks, project.Groups[^1].Closed), Is.EqualTo((14, 0)));
            Assert.That((project.TaskCount, project.ClosedCount), Is.EqualTo((84, 70)));
            Assert.That(project.Weeks[^3].Counts[Index(project, "Done")], Is.EqualTo(65), "Closes cluster at iteration ends.");
            Assert.That(project.Weeks[^2].Counts[Index(project, "Done")], Is.EqualTo(70), "The next iteration end closes the next batch.");
        }
    }

    [Test]
    public void TheGroupTablesFollowTheOptionOrderAndTheFieldName()
    {
        var phases = new[] { "SA", "UI", "SS", "PS", "PG", "PT", "IT", "ST", "OT" };
        var tasks = new[] { 120, 120, 120, 120, 160, 120, 120, 80, 40 };
        var first = projects[0];
        var second = projects[1];
        var zero = projects[3];
        var started = projects[4];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Config.GroupField, Is.EqualTo("工程"));
            Assert.That(first.Groups.Select(row => row.Name), Is.EqualTo(phases), "Rows follow the record's option order.");
            Assert.That(first.Groups.Select(row => row.Tasks), Is.EqualTo(tasks));
            Assert.That(first.Groups.Select(row => row.Closed), Is.EqualTo(tasks), "The steady version is complete.");
            Assert.That(second.Groups.Select(row => row.Closed), Is.EqualTo(new[] { 87, 87, 87, 87, 116, 87, 85, 56, 28 }));
            Assert.That(zero.Groups.Select(row => row.Tasks), Is.EqualTo(new[] { 6, 6, 6, 5, 5, 5, 5, 5, 5 }));
            Assert.That(zero.Groups.Select(row => row.Closed).Distinct(), Is.EqualTo(new[] { 0 }));
            Assert.That(started.Groups.Select(row => (row.Tasks, row.Closed)),
                Is.EqualTo(new[] { (2, 1), (1, 1), (1, 1), (1, 1), (1, 0), (1, 0), (1, 0), (1, 0), (1, 0) }));
            Assert.That(started.Groups.Select(row => row.Name), Is.EqualTo(phases));
        }
    }

    [Test]
    public void TheJustStartedProjectEndsWithUnsetAndHasNoPeriod()
    {
        var started = projects[4];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(started.States, Is.EqualTo(new[] { "Backlog", "Ready", "In progress", "In review", "Done", SiteAnalysis.UnsetState }));
            Assert.That(started.Weeks, Has.Count.EqualTo(2));
            Assert.That(started.Weeks[^1].Counts[^1], Is.EqualTo(4), "Tasks without a Status value are drawn last.");
            Assert.That(started.Weeks[^1].Counts[Index(started, "Backlog")], Is.EqualTo(1));
            Assert.That(started.Weeks[^1].Counts[Index(started, "In progress")], Is.EqualTo(1));
            Assert.That(started.Weeks[^1].Counts[Index(started, "Done")], Is.EqualTo(4));
            Assert.That(started.Config.PeriodStart, Is.Null);
            Assert.That(SiteAnalysis.Period(started.Config), Is.EqualTo("—"));
            Assert.That(SiteAnalysis.Date(started.LatestDay), Is.EqualTo("2026-10-23"));
        }
    }

    [Test]
    public void TheListPageSortsByPeriodEndAndLinksEveryRowAndItsCsv()
    {
        var html = Page("index.html");
        var order = new[] { "改善バックログ", "第2027.01版", "第2027.04版", "第2027.10版", "新規プロジェクト" };
        var positions = order.Select(name => html.IndexOf(name, StringComparison.Ordinal)).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(positions, Has.None.LessThan(0));
            Assert.That(positions, Is.Ordered, "Projects sort by period end, and the Project without a period sorts last.");
            Assert.That(html, Does.Contain("—"));
            Assert.That(html, Does.Contain("href=\"p/demo-1/index.html\""));
            Assert.That(html, Does.Contain("download=\"第2027.04版_2027-04-30.csv\""));
            Assert.That(html, Does.Contain("download=\"第2027.10版_2027-10-29.csv\""));
            Assert.That(html, Does.Contain("デモデータ").And.Contain("noindex"));
        }
    }

    [Test]
    public void EveryPageIsPrivateRelativeAndFreeOfJudgementWording()
    {
        var pages = new[] { "index.html" }.Concat(projects.Select(p => "p/" + p.Config.FolderName + "/index.html")).ToArray();
        using (Assert.EnterMultipleScope())
        {
            foreach (var path in pages)
            {
                var html = Page(path);
                Assert.That(Forbidden.Where(word => html.Contains(word, StringComparison.Ordinal)), Is.Empty, path + " must not judge or forecast.");
                Assert.That(html, Does.Not.Contain("<script"), path);
                Assert.That(html, Does.Contain("name=\"robots\" content=\"noindex\""), path);
                Assert.That(html, Does.Contain("デモデータ"), path);
                foreach (var link in System.Text.RegularExpressions.Regex.Matches(html, "(?:href|src)=\"([^\"]*)\"")
                             .Select(match => match.Groups[1].Value))
                    Assert.That(link, Does.Not.StartWith("http").And.Not.StartWith("/"), path + " uses only relative links.");
            }
            Assert.That(File.ReadAllText(Path.Combine(site, "robots.txt")), Is.EqualTo("User-agent: *\nDisallow: /\n"));
            foreach (var project in projects) Assert.That(Page("p/" + project.Config.FolderName + "/index.html"), Does.Contain("状態の推移"));
            Assert.That(Page("p/demo-1/index.html"), Does.Contain("工程ごとの完了"));
            Assert.That(Page("p/demo-3/index.html"), Does.Contain("Iterationごとの完了"));
            Assert.That(Page("p/demo-2/index.html"), Does.Contain("2027-10-29 の週"), "A segment's hover title carries the week.");
            Assert.That(Page("p/demo-2/index.html"), Does.Contain("Done &#183; 708"), "A segment's hover title carries the state and count.");
        }
    }

    [Test]
    public void TheCsvHasOneRowPerItemWithTheTaskFlagAndTheRecordedFields()
    {
        var expected = new (string Project, int Rows, int Tasks, string[] Fields)[]
        {
            ("demo-1", 1045, 1000, ["Status", "実績", "工程", "残", "終了日", "見積", "開始日"]),
            ("demo-2", 1040, 1000, ["Status", "工程"]),
            ("demo-3", 84, 84, ["Iteration", "Status"]),
            ("demo-4", 56, 48, ["Status", "工程", "見積"]),
            ("demo-5", 12, 10, ["Status", "工程"]),
        };
        using (Assert.EnterMultipleScope())
        {
            foreach (var (project, rowCount, tasks, fields) in expected)
            {
                var path = Path.Combine(site, "p", project, "items.csv");
                var rows = Csv(path);
                Assert.That(rows, Has.Count.EqualTo(rowCount + 1), project + " keeps one row per item.");
                Assert.That(rows[0], Is.EqualTo(FixedHeaders.Concat(fields).ToArray()), project + "'s column order");
                Assert.That(rows.Skip(1).Select(row => row.Length).Distinct(), Is.EqualTo(new[] { rows[0].Length }));
                Assert.That(rows.Skip(1).Count(row => row[1] == "はい"), Is.EqualTo(tasks), project + "'s task flag");
                Assert.That(File.ReadAllBytes(path).Take(3), Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
            }
            var first = Csv(Path.Combine(site, "p", "demo-1", "items.csv"));
            var kinds = first.Skip(1).Select(row => row[0]).ToArray();
            Assert.That(kinds.Count(kind => kind == "Issue"), Is.EqualTo(1040), "Every Issue keeps a row.");
            Assert.That(kinds.Count(kind => kind == "PR"), Is.EqualTo(3));
            Assert.That(kinds.TakeLast(2), Is.EquivalentTo(new[] { "下書き", "非公開" }), "Drafts and REDACTED items are last.");
            var located = first.Skip(1).Where(row => row[2].Length > 0)
                .Select(row => (Repository: row[2], Number: int.Parse(row[3], CultureInfo.InvariantCulture))).ToArray();
            Assert.That(located, Is.EqualTo(located.OrderBy(row => row.Repository, StringComparer.Ordinal).ThenBy(row => row.Number).ToArray()),
                "Rows follow repository then number.");
            Assert.That(located, Has.Length.EqualTo(1043));
        }
    }
}
