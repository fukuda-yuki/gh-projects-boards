using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.Core.Reports;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #271: the demo data must be written in the production raw format, be deterministic, and carry the five
// scenarios the site's acceptance uses (steady completion, final-week batch, agile iteration batches, zero closed
// with missing days, just started with unset Status).
[TestFixture]
internal sealed class DemoDataTests
{
    private static readonly string Plan = Resource("VersionPlan-2027.04.json");
    private static readonly string Plan12m = Resource("VersionPlan-2027.10.json");
    private string root = "";
    private string generated = "";

    [OneTimeSetUp]
    public void Generate()
    {
        generated = Path.Combine(Path.GetTempPath(), "ghpb-demo-" + Guid.NewGuid().ToString("N"));
        DemoData.Write(generated, Plan, Plan12m);
    }

    [OneTimeTearDown]
    public void DeleteGenerated()
    {
        if (Directory.Exists(generated)) Directory.Delete(generated, true);
    }

    [SetUp]
    public void CreateRoot() => root = Path.Combine(Path.GetTempPath(), "ghpb-demo-test-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void DeleteRoot()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static string Resource(string name)
    {
        using var stream = typeof(DemoDataTests).Assembly.GetManifestResourceStream(name)
            ?? throw new ArgumentException("埋め込みの版計画がありません: " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static readonly (string Url, string Name, string GroupField, string? PeriodStart, string? PeriodEnd)[] SiteProjects =
    [
        ("https://github.com/users/demo/projects/1", "第2027.04版", "工程", "2026-10-13", "2027-04-30"),
        ("https://github.com/users/demo/projects/2", "第2027.10版", "工程", "2026-10-13", "2027-10-29"),
        ("https://github.com/users/demo/projects/3", "改善バックログ", "Iteration", "2026-04-06", "2026-10-25"),
        ("https://github.com/users/demo/projects/4", "第2027.01版", "工程", "2026-08-03", "2026-12-25"),
        ("https://github.com/users/demo/projects/5", "新規プロジェクト", "工程", null, null),
    ];

    [Test]
    public void TheSameInputsWriteTheSameFiles()
    {
        var second = Path.Combine(root, "second");
        DemoData.Write(second, Plan, Plan12m);
        var expected = Files(generated);
        var actual = Files(second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Keys, Is.EqualTo(expected.Keys), "The same relative file list.");
            foreach (var (name, bytes) in expected) Assert.That(actual[name], Is.EqualTo(bytes), name);
        }
    }

    [TestCase(1, "2026-12-16")]
    [TestCase(2, "2027-10-29")]
    [TestCase(3, "2026-07-01")]
    [TestCase(4, "2026-09-03")]
    [TestCase(5, "2026-10-21")]
    public void ADemoDayReadsBackAsTheGeneratedState(int project, string text)
    {
        var day = DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var snapshot = DemoData.Snapshot(project, day, Plan, Plan12m);
        var raw = Path.Combine(root, "readback");
        var (path, items, removed) = RawStore.Write(raw, snapshot);
        var folder = RawStore.Folder(raw, "demo", project, day);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RawStore.Read(folder, day), Is.EqualTo(snapshot.Items), "Every generated item line comes back unchanged.");
            Assert.That(File.ReadLines(path).First(), Is.EqualTo(snapshot.ProjectLine), "The Project line is written as generated.");
            Assert.That(items, Is.EqualTo(snapshot.Items.Count));
            Assert.That(removed, Is.EqualTo(0));
            Assert.That(Path.GetFileName(path), Is.EqualTo("full-" + text + ".jsonl"), "The first day in a fresh root is the month's full record.");
        }
    }

    [Test]
    public void EachMonthStartsWithAFullRecordAndLaterDaysAChangesRecord()
    {
        for (var project = 1; project <= 5; project++)
        {
            var days = RecordedDays(project);
            var folder = Path.Combine(RawRoot, "demo", project.ToString(CultureInfo.InvariantCulture));
            foreach (var month in days.GroupBy(day => new DateOnly(day.Year, day.Month, 1)))
            {
                var files = Directory.EnumerateFiles(Path.Combine(folder, month.Key.ToString("yyyy-MM", CultureInfo.InvariantCulture)), "*.jsonl")
                    .Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
                Assert.That(files, Has.Count.EqualTo(month.Count()), "One file per recorded day of the month.");
                Assert.That(files, Does.Contain("full-" + month.First().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl"),
                    "The month's first recorded day is the full record.");
                foreach (var day in month.Skip(1))
                    Assert.That(files, Does.Contain("changes-" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl"));
            }
        }
    }

    [Test]
    public void TheSiteConfigurationListsTheFiveDemoProjects()
    {
        var path = Path.Combine(generated, "site.json");
        var bytes = File.ReadAllBytes(path);
        var site = JsonNode.Parse(Encoding.UTF8.GetString(bytes))!;
        var projects = site["projects"]!.AsArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bytes.Length > 2 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, Is.False, "UTF-8 without BOM.");
            Assert.That(site["title"]!.GetValue<string>(), Is.EqualTo("プロジェクト進捗"));
            Assert.That(site["demo"]!.GetValue<bool>(), Is.True);
            Assert.That(projects, Has.Count.EqualTo(SiteProjects.Length));
            for (var n = 0; n < SiteProjects.Length; n++)
            {
                var expected = SiteProjects[n];
                var project = projects[n]!.AsObject();
                var label = expected.Url;
                Assert.That(project["url"]!.GetValue<string>(), Is.EqualTo(expected.Url), label);
                Assert.That(project["name"]!.GetValue<string>(), Is.EqualTo(expected.Name), label);
                Assert.That(project["stateField"]!.GetValue<string>(), Is.EqualTo("Status"), label);
                Assert.That(project["groupField"]!.GetValue<string>(), Is.EqualTo(expected.GroupField), label);
                Assert.That(project.ContainsKey("periodStart"), Is.EqualTo(expected.PeriodStart is not null), label);
                if (expected.PeriodStart is not null) Assert.That(project["periodStart"]!.GetValue<string>(), Is.EqualTo(expected.PeriodStart), label);
                if (expected.PeriodEnd is not null) Assert.That(project["periodEnd"]!.GetValue<string>(), Is.EqualTo(expected.PeriodEnd), label);
                Assert.That(Directory.Exists(Path.Combine(RawRoot, "demo", (n + 1).ToString(CultureInfo.InvariantCulture))), Is.True, "Raw folder for " + expected.Name);
            }
        }
    }

    [Test]
    public void TheSteadyWaterfallCompletesEveryTask()
    {
        var days = RecordedDays(1);
        var latest = ReadDay(1, days.Max);
        var (parents, tasks) = SplitLeafIssues(latest.Items);
        var project = ProjectFieldNames(latest.ProjectLine);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(days, Has.Count.EqualTo(144));
            Assert.That(days.Min, Is.EqualTo(new DateOnly(2026, 10, 13)));
            Assert.That(days.Max, Is.EqualTo(new DateOnly(2027, 4, 30)));
            Assert.That(latest.Items, Has.Count.EqualTo(1045));
            Assert.That(parents, Has.Length.EqualTo(40));
            Assert.That(tasks, Has.Length.EqualTo(1000));
            Assert.That(latest.Items.Count(item => ItemType(item) == "PULL_REQUEST"), Is.EqualTo(3));
            Assert.That(latest.Items.Count(item => ItemType(item) == "DRAFT_ISSUE"), Is.EqualTo(1));
            Assert.That(latest.Items.Count(item => ItemType(item) == "REDACTED"), Is.EqualTo(1));
            Assert.That(tasks.Count(IsClosed), Is.EqualTo(1000), "All 1,000 tasks are closed on the last record.");
            Assert.That(tasks.Select(item => OptionName(item, "Status")).Distinct(), Is.EqualTo(new[] { "Done" }));
            Assert.That(tasks.All(item => HasField(item, "工程")), Is.True, "Every task carries its phase.");
            Assert.That(tasks.Select(item => OptionName(item, "工程")).Distinct().Count(), Is.EqualTo(9));
            Assert.That(tasks.All(item => HasField(item, "見積") && HasField(item, "実績") && HasField(item, "残") && HasField(item, "開始日") && HasField(item, "終了日")), Is.True);
            Assert.That(parents.Count(IsClosed), Is.EqualTo(40), "Each requirement closes after all of its tasks.");
            Assert.That(parents.All(item => !HasField(item, "工程")), Is.True, "Requirement rows do not carry a phase.");
            Assert.That(project, Does.Contain("Title"), "Built-in columns are defined so the CSV can exclude them.");
            Assert.That(project, Does.Contain("Assignees"));
            Assert.That(latest.Items.All(item => !item.GetProperty("fields").TryGetProperty("Title", out _)), Is.True, "Built-in columns never hold item values.");
            var pullRequests = latest.Items.Where(item => ItemType(item) == "PULL_REQUEST").ToArray();
            Assert.That(pullRequests.Select(PullRequestState).OrderBy(state => state, StringComparer.Ordinal),
                Is.EqualTo(new[] { "CLOSED", "MERGED", "OPEN" }), "One open, one merged and one closed pull request.");
            Assert.That(pullRequests.Count(item => ContentOf(item)!.Value.TryGetProperty("mergedAt", out var merged) && merged.ValueKind == JsonValueKind.String), Is.EqualTo(1));
            var phases = ProjectFieldOptions(latest.ProjectLine, "工程");
            Assert.That(phases, Is.EqualTo(new[] { "SA", "UI", "SS", "PS", "PG", "PT", "IT", "ST", "OT" }));
            Assert.That(tasks.Select(item => OptionName(item, "工程")).Distinct().OrderBy(name => name, StringComparer.Ordinal), Is.EquivalentTo(phases));
            Assert.That(phases.Select(phase => tasks.Count(item => OptionName(item, "工程") == phase)),
                Is.EqualTo(new[] { 120, 120, 120, 120, 160, 120, 120, 80, 40 }), "The plan's phase rows.");
            var statuses = ProjectFieldOptions(latest.ProjectLine, "Status");
            Assert.That(statuses, Is.EqualTo(new[] { "Backlog", "Ready", "In progress", "In review", "Done" }));
            Assert.That(ProjectNumber(latest.ProjectLine), Is.EqualTo(1));
        }
    }

    [Test]
    public void TheFinalWeekWaterfallClosesInABatch()
    {
        var days = RecordedDays(2);
        var beforeFinalWeek = ReadDay(2, new DateOnly(2027, 10, 22));
        var beforeTasks = SplitLeafIssues(beforeFinalWeek.Items).Tasks;
        var latest = ReadDay(2, days.Max);
        var (parents, tasks) = SplitLeafIssues(latest.Items);
        var closed = tasks.Where(IsClosed).ToArray();
        var open = tasks.Where(item => !IsClosed(item)).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(days, Has.Count.EqualTo(274));
            Assert.That(days.Min, Is.EqualTo(new DateOnly(2026, 10, 13)));
            Assert.That(days.Max, Is.EqualTo(new DateOnly(2027, 10, 29)));
            Assert.That(beforeTasks.Count(IsClosed), Is.EqualTo(0), "Nothing is closed before the final week.");
            Assert.That(beforeTasks.Count(item => OptionName(item, "Status") == "Ready"), Is.EqualTo(30));
            Assert.That(beforeTasks.Count(item => OptionName(item, "Status") == "In progress"), Is.EqualTo(20));
            Assert.That(beforeTasks.Count(item => OptionName(item, "Status") == "Backlog"), Is.EqualTo(950));
            Assert.That(latest.Items, Has.Count.EqualTo(1040));
            Assert.That(parents, Has.Length.EqualTo(40));
            Assert.That(tasks, Has.Length.EqualTo(1000));
            Assert.That(closed, Has.Length.EqualTo(720), "The final week closes 720 tasks.");
            Assert.That(closed.Count(item => OptionName(item, "Status") == "In progress"), Is.EqualTo(12), "Closed tasks may stay in Status In progress.");
            Assert.That(closed.Count(item => OptionName(item, "Status") == "Done"), Is.EqualTo(708));
            Assert.That(open, Has.Length.EqualTo(280));
            Assert.That(open.All(item => OptionName(item, "Status") == "Backlog"), Is.True, "The remaining tasks stay open in Backlog.");
            Assert.That(parents.All(item => !IsClosed(item)), Is.True);
        }
    }

    [Test]
    public void TheAgileProjectBatchesAtIterationEnds()
    {
        var days = RecordedDays(3);
        var latest = ReadDay(3, days.Max);
        var (parents, tasks) = SplitLeafIssues(latest.Items);
        var iterationField = ProjectField(latest.ProjectLine, "Iteration");
        var completed = iterationField["completedIterations"]!.AsArray();
        var current = iterationField["iterations"]!.AsArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(days, Has.Count.EqualTo(145));
            Assert.That(days.Min, Is.EqualTo(new DateOnly(2026, 4, 6)));
            Assert.That(days.Max, Is.EqualTo(new DateOnly(2026, 10, 23)));
            Assert.That(latest.Items, Has.Count.EqualTo(84));
            Assert.That(latest.Items.Select(ItemType).Distinct(), Is.EqualTo(new[] { "ISSUE" }), "A flat backlog: every item is an Issue.");
            Assert.That(parents, Is.Empty, "No Issue is the parent of another item.");
            Assert.That(tasks, Has.Length.EqualTo(84));
            Assert.That(tasks.Count(IsClosed), Is.EqualTo(70));
            Assert.That(tasks.Where(IsClosed).Select(item => OptionName(item, "Status")).Distinct(), Is.EqualTo(new[] { "Done" }));
            Assert.That(tasks.Count(item => OptionName(item, "Status") == "Doing"), Is.EqualTo(9));
            Assert.That(tasks.Count(item => OptionName(item, "Status") == "Todo"), Is.EqualTo(5));
            Assert.That(ProjectFieldOptions(latest.ProjectLine, "Status"), Is.EqualTo(new[] { "Todo", "Doing", "Review", "Done" }));
            Assert.That(iterationField["dataType"]!.GetValue<string>(), Is.EqualTo("ITERATION"));
            Assert.That(completed, Has.Count.EqualTo(14), "Every finished iteration moves to completedIterations.");
            Assert.That(current, Has.Count.EqualTo(1));
            Assert.That(current[0]!["title"]!.GetValue<string>(), Is.EqualTo("Iteration 15"));
            Assert.That(completed.Select(iteration => iteration!["title"]!.GetValue<string>()),
                Is.EqualTo(Enumerable.Range(1, 14).Select(number => "Iteration " + number.ToString(CultureInfo.InvariantCulture))));
            Assert.That(tasks.All(item => item.GetProperty("fields").TryGetProperty("Iteration", out var value)
                && value.ValueKind == JsonValueKind.Object
                && value.GetProperty("title").GetString()!.StartsWith("Iteration ", StringComparison.Ordinal)), Is.True, "Every Issue records its iteration.");
            var rows = tasks.GroupBy(IterationTitleOf).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            Assert.That(Enumerable.Range(1, 14).Select(number => rows["Iteration " + number.ToString(CultureInfo.InvariantCulture)]),
                Is.EqualTo(Enumerable.Repeat(5, 14)), "Each completed iteration holds five closed Issues.");
            Assert.That(rows["Iteration 15"], Is.EqualTo(14), "The current iteration holds the remaining Issues.");
        }
    }

    [Test]
    public void TheZeroClosedProjectSkipsDaysAndOneWholeWeek()
    {
        var days = RecordedDays(4);
        var latest = ReadDay(4, days.Max);
        var (parents, tasks) = SplitLeafIssues(latest.Items);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(days, Has.Count.EqualTo(53));
            Assert.That(days.Min, Is.EqualTo(new DateOnly(2026, 8, 3)));
            Assert.That(days.Max, Is.EqualTo(new DateOnly(2026, 10, 23)));
            Assert.That(days, Does.Not.Contain(new DateOnly(2026, 9, 1)), "An isolated day with no record.");
            Assert.That(days, Does.Not.Contain(new DateOnly(2026, 9, 2)), "A second isolated day with no record.");
            Assert.That(days.Where(day => day >= new DateOnly(2026, 9, 14) && day <= new DateOnly(2026, 9, 18)), Is.Empty, "One whole week has no record.");
            Assert.That(days, Does.Contain(new DateOnly(2026, 9, 3)), "Recording resumes after the isolated days.");
            Assert.That(days, Does.Contain(new DateOnly(2026, 9, 11)), "The week before the missing week is recorded.");
            Assert.That(days, Does.Contain(new DateOnly(2026, 9, 21)), "Recording resumes after the missing week.");
            Assert.That(latest.Items, Has.Count.EqualTo(56));
            Assert.That(parents, Has.Length.EqualTo(8));
            Assert.That(tasks, Has.Length.EqualTo(48));
            Assert.That(tasks.Count(IsClosed), Is.EqualTo(0), "Nothing is ever closed.");
            Assert.That(tasks.All(item => OptionName(item, "Status") == "Backlog"), Is.True, "Every task stays in the first Status option.");
            Assert.That(tasks.All(item => HasField(item, "見積")), Is.True);
            Assert.That(tasks.All(item => HasField(item, "工程")), Is.True);
        }
    }

    [Test]
    public void TheJustStartedProjectHasTasksWithoutStatus()
    {
        var days = RecordedDays(5);
        var latest = ReadDay(5, days.Max);
        var (parents, tasks) = SplitLeafIssues(latest.Items);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(days, Has.Count.EqualTo(9));
            Assert.That(days.Min, Is.EqualTo(new DateOnly(2026, 10, 13)));
            Assert.That(days.Max, Is.EqualTo(new DateOnly(2026, 10, 23)));
            Assert.That(latest.Items, Has.Count.EqualTo(12));
            Assert.That(parents, Has.Length.EqualTo(2));
            Assert.That(tasks, Has.Length.EqualTo(10));
            Assert.That(tasks.Count(IsClosed), Is.EqualTo(4));
            Assert.That(tasks.Count(item => !HasField(item, "Status")), Is.EqualTo(4), "Some tasks have no Status value (未設定).");
            Assert.That(tasks.Count(item => IsClosed(item) && OptionName(item, "Status") == "Done"), Is.EqualTo(4));
            Assert.That(tasks.Count(item => OptionName(item, "Status") == "In progress"), Is.EqualTo(1));
            Assert.That(tasks.Count(item => OptionName(item, "Status") == "Backlog"), Is.EqualTo(1));
            Assert.That(parents.Count(IsClosed), Is.EqualTo(0));
            Assert.That(tasks.All(item => HasField(item, "工程")), Is.True);
        }
    }

    private string RawRoot => Path.Combine(generated, "raw");

    private static SortedDictionary<string, byte[]> Files(string folder)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(folder, file).Replace('\\', '/')] = File.ReadAllBytes(file);
        return files;
    }

    private SortedSet<DateOnly> RecordedDays(int project)
    {
        var days = new SortedSet<DateOnly>();
        var folder = Path.Combine(RawRoot, "demo", project.ToString(CultureInfo.InvariantCulture));
        foreach (var file in Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            days.Add(DateOnly.ParseExact(name[(name.IndexOf('-') + 1)..], "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        return days;
    }

    private (DateOnly Day, string ProjectLine, List<JsonElement> Items) ReadDay(int project, DateOnly day)
    {
        var folder = RawStore.Folder(RawRoot, "demo", project, day);
        var text = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var file = Directory.EnumerateFiles(folder, "*" + text + ".jsonl").Single();
        var items = new List<JsonElement>();
        foreach (var line in RawStore.Read(folder, day).Values)
        {
            using var document = JsonDocument.Parse(line);
            items.Add(document.RootElement.Clone());
        }
        return (day, File.ReadLines(file).First(), items);
    }

    private static (JsonElement[] Parents, JsonElement[] Tasks) SplitLeafIssues(IReadOnlyList<JsonElement> items)
    {
        var parentReferences = items.Select(ParentReference).Where(reference => reference is not null).ToHashSet(StringComparer.Ordinal);
        var issues = items.Where(item => ItemType(item) == "ISSUE").ToArray();
        var parents = issues.Where(item => ItemReference(item) is { } reference && parentReferences.Contains(reference)).ToArray();
        var tasks = issues.Where(item => ItemReference(item) is not { } reference || !parentReferences.Contains(reference)).ToArray();
        return (parents, tasks);
    }

    private static string ItemType(JsonElement item) => item.GetProperty("type").GetString()!;

    private static JsonElement? ContentOf(JsonElement item)
        => item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object ? content : null;

    private static string? ItemReference(JsonElement item)
    {
        if (ContentOf(item) is not { } content
            || !content.TryGetProperty("repository", out var repository)
            || repository.ValueKind != JsonValueKind.String
            || !content.TryGetProperty("number", out var number)) return null;
        return repository.GetString() + "#" + number.GetInt32().ToString(CultureInfo.InvariantCulture);
    }

    private static string? ParentReference(JsonElement item)
    {
        if (ContentOf(item) is not { } content
            || !content.TryGetProperty("parent", out var parent)
            || parent.ValueKind != JsonValueKind.String) return null;
        return parent.GetString();
    }

    private static bool IsClosed(JsonElement item)
        => ContentOf(item) is { } content
            && content.TryGetProperty("state", out var state)
            && state.ValueKind == JsonValueKind.String
            && state.GetString() == "CLOSED";

    private static bool HasField(JsonElement item, string field)
        => item.GetProperty("fields").TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null;

    private static string? OptionName(JsonElement item, string field)
        => item.GetProperty("fields").TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;

    private static string IterationTitleOf(JsonElement item)
        => item.GetProperty("fields").GetProperty("Iteration").GetProperty("title").GetString()!;

    private static string PullRequestState(JsonElement item)
        => ContentOf(item)!.Value.GetProperty("state").GetString()!;

    private static JsonNode ProjectNode(string projectLine) => JsonNode.Parse(projectLine)!;

    private static string[] ProjectFieldNames(string projectLine)
        => ProjectNode(projectLine)["fields"]!.AsArray().Select(field => field!["name"]!.GetValue<string>()).ToArray();

    private static JsonNode ProjectField(string projectLine, string name)
        => ProjectNode(projectLine)["fields"]!.AsArray().Single(field => field!["name"]!.GetValue<string>() == name)!;

    private static string[] ProjectFieldOptions(string projectLine, string name)
        => ProjectField(projectLine, name)["options"]!.AsArray().Select(option => option!["name"]!.GetValue<string>()).ToArray();

    private static int ProjectNumber(string projectLine) => ProjectNode(projectLine)["project"]!["number"]!.GetValue<int>();
}
