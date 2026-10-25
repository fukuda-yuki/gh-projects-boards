using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GhProjectsBoards.Core.Reports;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #271: the management progress site reads only the #272 raw record and the site configuration. The fixtures below
// write real raw days with RawRecord.Build + RawStore.Write, then assert the counts and the rendered pages.
[TestFixture]
internal sealed class ManagementSiteTests
{
    private string root = "";
    private static readonly DateTimeOffset ReadAt = new(2026, 10, 20, 21, 0, 0, TimeSpan.Zero);
    private static readonly string[] StatusOptions = ["Backlog", "Ready", "In progress", "Done"];
    private static readonly string[] PhaseOptions = ["SA", "UI", "PG"];
    private static readonly string[] Forbidden = ["遅れ", "見込", "要確認", "現在の工程", "予測", "判定"];

    [SetUp]
    public void Create() => root = Path.Combine(Path.GetTempPath(), "ghpb-site-" + Guid.NewGuid().ToString("N"));
    [TearDown]
    public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    // ---- raw record fixtures ----

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonObject Field(string name, string dataType, string typeName, JsonArray? options = null, JsonObject? configuration = null)
    {
        var field = new JsonObject
        {
            ["__typename"] = typeName, ["id"] = "F_" + name, ["name"] = name, ["dataType"] = dataType, ["isIssueField"] = false,
        };
        if (options is not null) field["options"] = options;
        if (configuration is not null) field["configuration"] = configuration;
        return field;
    }

    private static JsonObject Select(string name, params string[] options) => Field(name, "SINGLE_SELECT", "ProjectV2SingleSelectField", new JsonArray(
        options.Select((option, index) => (JsonNode)new JsonObject { ["id"] = "F_" + name + "_" + index.ToString(CultureInfo.InvariantCulture), ["name"] = option }).ToArray()));

    private static JsonObject Number(string name) => Field(name, "NUMBER", "ProjectV2Field");

    private static JsonObject Date(string name) => Field(name, "DATE", "ProjectV2Field");

    private static JsonObject Builtin(string name, string dataType) => Field(name, dataType, "ProjectV2Field");

    private static JsonObject Sprint(string id, string title, string start)
        => new() { ["id"] = id, ["title"] = title, ["startDate"] = start, ["duration"] = 14 };

    private static JsonObject Iteration(string name, JsonObject[] current, JsonObject[] completed)
        => Field(name, "ITERATION", "ProjectV2IterationField", configuration: new JsonObject
        {
            ["duration"] = 14, ["startDay"] = 1,
            ["iterations"] = new JsonArray(current.Cast<JsonNode>().ToArray()),
            ["completedIterations"] = new JsonArray(completed.Cast<JsonNode>().ToArray()),
        });

    private static JsonElement Project(params JsonObject[] fields)
    {
        var project = new JsonObject
        {
            ["__typename"] = "ProjectV2", ["id"] = "PVT_1", ["number"] = 1, ["title"] = "デモ",
            ["url"] = "https://github.com/users/demo/projects/1", ["closed"] = false,
            ["owner"] = new JsonObject { ["__typename"] = "User", ["login"] = "demo" },
            ["fields"] = new JsonObject { ["totalCount"] = fields.Length, ["nodes"] = new JsonArray(fields.Cast<JsonNode>().ToArray()) },
        };
        return Json(project.ToJsonString());
    }

    private static JsonObject Reference(string reference)
    {
        var hash = reference.LastIndexOf('#');
        return new JsonObject
        {
            ["number"] = int.Parse(reference[(hash + 1)..], CultureInfo.InvariantCulture),
            ["repository"] = new JsonObject { ["nameWithOwner"] = reference[..hash] },
        };
    }

    private static JsonObject Logins(params string[] logins)
        => new() { ["totalCount"] = logins.Length, ["nodes"] = new JsonArray(logins.Select(l => (JsonNode)new JsonObject { ["login"] = l }).ToArray()) };

    private static JsonObject Labels(params string[] names)
        => new() { ["totalCount"] = names.Length, ["nodes"] = new JsonArray(names.Select(n => (JsonNode)new JsonObject { ["name"] = n }).ToArray()) };

    private static JsonObject Empty() => new() { ["totalCount"] = 0, ["nodes"] = new JsonArray() };

    private static JsonObject OptionValue(string field, int index, string name)
        => new()
        {
            ["__typename"] = "ProjectV2ItemFieldSingleSelectValue", ["name"] = name,
            ["optionId"] = "F_" + field + "_" + index.ToString(CultureInfo.InvariantCulture),
            ["field"] = new JsonObject { ["id"] = "F_" + field, ["name"] = field },
        };

    private static JsonElement Item(
        string id, int number, string? status = "Backlog", string? phase = "PG", string state = "OPEN",
        string? parent = null, bool archived = false, string type = "ISSUE", string? title = null,
        string? closedAt = null, string? stateReason = null, string? mergedAt = null,
        (string Id, string Title, string Start)? iteration = null, decimal? estimate = null, string? start = null,
        (string Field, int Index, string Name)? custom = null, (string Title, string DueOn)? milestone = null)
    {
        var values = new List<JsonObject>();
        if (status is not null) values.Add(OptionValue("Status", Array.IndexOf(StatusOptions, status), status));
        if (phase is not null) values.Add(OptionValue("工程", Array.IndexOf(PhaseOptions, phase), phase));
        if (custom is { } value) values.Add(OptionValue(value.Field, value.Index, value.Name));
        if (iteration is { } sprint)
            values.Add(new JsonObject
            {
                ["__typename"] = "ProjectV2ItemFieldIterationValue", ["title"] = sprint.Title, ["iterationId"] = sprint.Id,
                ["startDate"] = sprint.Start, ["duration"] = 14,
                ["field"] = new JsonObject { ["id"] = "F_Iteration", ["name"] = "Iteration" },
            });
        if (estimate is { } amount)
            values.Add(new JsonObject
            {
                ["__typename"] = "ProjectV2ItemFieldNumberValue", ["number"] = amount,
                ["field"] = new JsonObject { ["id"] = "F_見積", ["name"] = "見積" },
            });
        if (start is not null)
            values.Add(new JsonObject
            {
                ["__typename"] = "ProjectV2ItemFieldDateValue", ["date"] = start,
                ["field"] = new JsonObject { ["id"] = "F_開始日", ["name"] = "開始日" },
            });
        var milestoneValue = milestone is { } due
            ? new JsonObject { ["title"] = due.Title, ["dueOn"] = due.DueOn }
            : null;
        var content = type switch
        {
            "REDACTED" => null,
            "DRAFT_ISSUE" => new JsonObject
            {
                ["__typename"] = "DraftIssue", ["id"] = "I_" + id, ["title"] = title ?? "下書き " + number,
                ["createdAt"] = "2026-09-01T00:00:00Z",
            },
            "PULL_REQUEST" => new JsonObject
            {
                ["__typename"] = "PullRequest", ["id"] = "I_" + id, ["number"] = number, ["title"] = title ?? "PR " + number,
                ["url"] = "https://github.com/demo/repo/pull/" + number.ToString(CultureInfo.InvariantCulture),
                ["state"] = state, ["closedAt"] = closedAt, ["mergedAt"] = mergedAt,
                ["createdAt"] = "2026-09-01T00:00:00Z", ["repository"] = new JsonObject { ["nameWithOwner"] = "demo/repo" },
                ["assignees"] = Empty(), ["labels"] = Empty(), ["milestone"] = milestoneValue,
            },
            _ => new JsonObject
            {
                ["__typename"] = "Issue", ["id"] = "I_" + id, ["number"] = number, ["title"] = title ?? "Item " + number,
                ["url"] = "https://github.com/demo/repo/issues/" + number.ToString(CultureInfo.InvariantCulture),
                ["state"] = state, ["closedAt"] = closedAt, ["stateReason"] = stateReason,
                ["createdAt"] = "2026-09-01T00:00:00Z", ["repository"] = new JsonObject { ["nameWithOwner"] = "demo/repo" },
                ["assignees"] = Logins("yamada"), ["labels"] = Labels("改善"), ["milestone"] = milestoneValue, ["issueType"] = null,
                ["parent"] = parent is null ? null : Reference(parent), ["blockedBy"] = Empty(),
            },
        };
        var item = new JsonObject
        {
            ["id"] = "PVTI_" + id, ["type"] = type, ["isArchived"] = archived, ["createdAt"] = "2026-09-01T00:00:00Z",
            ["content"] = content,
            ["fieldValues"] = new JsonObject { ["totalCount"] = values.Count, ["nodes"] = new JsonArray(values.Cast<JsonNode>().ToArray()) },
        };
        return Json(item.ToJsonString());
    }

    private void WriteDay(JsonElement project, DateOnly date, params JsonElement[] items)
        => RawStore.Write(root, RawRecord.Build(project, items, ReadAt, date, "Asia/Tokyo"));

    private SiteProjectConfig Config(string stateField = "Status", string groupField = "工程", int number = 1, string name = "デモ")
        => new("https://github.com/users/demo/projects/" + number.ToString(CultureInfo.InvariantCulture), "demo", number, name,
            stateField, groupField, new DateOnly(2026, 10, 13), new DateOnly(2026, 11, 30));

    private ProjectProgress Progress(string stateField = "Status", string groupField = "工程")
        => SiteAnalysis.Compute(Config(stateField, groupField), root);

    private string WriteConfig(string stateField = "Status", string groupField = "工程", bool demo = true, bool noPeriod = false)
    {
        var project = new JsonObject
        {
            ["url"] = "https://github.com/users/demo/projects/1", ["name"] = "デモ", ["stateField"] = stateField, ["groupField"] = groupField,
        };
        if (!noPeriod)
        {
            project["periodStart"] = "2026-10-13";
            project["periodEnd"] = "2026-11-30";
        }
        var config = new JsonObject
        {
            ["title"] = "プロジェクト進捗", ["demo"] = demo,
            ["projects"] = new JsonArray(project),
        };
        var path = Path.Combine(root, "site.json");
        File.WriteAllText(path, config.ToJsonString());
        return path;
    }

    private string BuildSite(string stateField = "Status", string groupField = "工程", bool demo = true)
    {
        var output = Path.Combine(root, "site");
        SiteBuilder.Build(root, WriteConfig(stateField, groupField, demo), output);
        return output;
    }

    // ---- behavior ----

    [Test]
    public void TasksAreLeafIssuesAndArchivedItemsCount()
    {
        var project = Project(Select("Status", StatusOptions), Select("工程", PhaseOptions), Builtin("Title", "TITLE"));
        WriteDay(project, new DateOnly(2026, 10, 19),
            Item("parent", 1, status: "Ready", phase: null),
            Item("open", 2, parent: "demo/repo#1"),
            Item("closed", 3, parent: "demo/repo#1", status: "Done", state: "CLOSED", closedAt: "2026-10-18T03:00:00Z", stateReason: "COMPLETED"),
            Item("archived", 4, parent: "demo/repo#1", archived: true),
            Item("pr", 10, type: "PULL_REQUEST"),
            Item("draft", 0, type: "DRAFT_ISSUE"),
            Item("redacted", 0, type: "REDACTED"));

        var progress = Progress();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.TaskCount, Is.EqualTo(3), "The parent Issue, the PR, the draft and the REDACTED item are not tasks; the archived task counts.");
            Assert.That(progress.ClosedCount, Is.EqualTo(1));
            Assert.That(progress.LatestItems, Has.Count.EqualTo(7), "The CSV source keeps every item.");
            Assert.That(progress.LatestTaskIds, Does.Not.Contain("PVTI_parent"));
            Assert.That(progress.States, Is.EqualTo(StatusOptions), "The state order is the Project field's own option order.");
        }
    }

    [Test]
    public void WeeklyBarsUseTheLastRecordedDayAndLeaveAWeekWithoutRecordEmpty()
    {
        var project = Project(Select("Status", StatusOptions), Select("工程", PhaseOptions));
        WriteDay(project, new DateOnly(2026, 10, 21), Item("a", 1), Item("b", 2));
        WriteDay(project, new DateOnly(2026, 10, 23), Item("a", 1, status: "Ready"), Item("b", 2, status: "Ready"));
        WriteDay(project, new DateOnly(2026, 11, 4),
            Item("a", 1, status: "In progress", state: "CLOSED", closedAt: "2026-11-03T03:00:00Z", stateReason: "COMPLETED"),
            Item("b", 2, status: "In progress"));

        var progress = Progress();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.Weeks.Select(w => SiteAnalysis.Date(w.Monday)),
                Is.EqualTo(new[] { "2026-10-19", "2026-10-26", "2026-11-02" }), "The first drawn week is the first recorded week; weeks run Monday to Sunday.");
            Assert.That(SiteAnalysis.Date(progress.Weeks[0].Day!.Value), Is.EqualTo("2026-10-23"), "A bar is the last recorded day in its week.");
            Assert.That(progress.Weeks[0].Counts[Array.IndexOf(StatusOptions, "Ready")], Is.EqualTo(2));
            Assert.That(progress.Weeks[1].Day, Is.Null, "A week with no record is an empty slot, not the previous values.");
            Assert.That(progress.Weeks[1].Total, Is.EqualTo(0));
            Assert.That(SiteAnalysis.Date(progress.Weeks[2].Day!.Value), Is.EqualTo("2026-11-04"));
            Assert.That(progress.Weeks[2].Counts[Array.IndexOf(StatusOptions, "In progress")], Is.EqualTo(2), "A closed task stays under its recorded Status.");
            Assert.That(progress.ClosedCount, Is.EqualTo(1), "Closed comes from the Issue state, not from Status.");
        }
    }

    [Test]
    public void TasksWithoutAStateValueAreCountedLastAsUnset()
    {
        var project = Project(Select("Status", StatusOptions), Select("工程", PhaseOptions));
        WriteDay(project, new DateOnly(2026, 10, 19), Item("a", 1, status: null), Item("b", 2));

        var progress = Progress();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.States, Is.EqualTo(StatusOptions.Append(SiteAnalysis.UnsetState)), "未設定 is drawn last and only when a task has no value.");
            Assert.That(progress.Weeks[0].Counts[^1], Is.EqualTo(1));
            Assert.That(progress.Weeks[0].Total, Is.EqualTo(2));
        }
    }

    [Test]
    public void GroupRowsFollowTheOptionOrderAndLeaveUnsetLast()
    {
        var project = Project(Select("Status", StatusOptions), Select("工程", PhaseOptions));
        WriteDay(project, new DateOnly(2026, 10, 19),
            Item("a", 1, phase: "SA", status: "Done", state: "CLOSED", closedAt: "2026-10-18T03:00:00Z", stateReason: "COMPLETED"),
            Item("b", 2, phase: "SA"),
            Item("c", 3, phase: "PG"),
            Item("d", 4, phase: null));

        var progress = Progress();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.Groups.Select(g => g.Name), Is.EqualTo(new[] { "SA", "UI", "PG", SiteAnalysis.UnsetState }));
            Assert.That(progress.Groups.Select(g => (g.Tasks, g.Closed)), Is.EqualTo(new[] { (2, 1), (0, 0), (1, 0), (1, 0) }));
            Assert.That(progress.Groups.Sum(g => g.Tasks), Is.EqualTo(progress.TaskCount));
            Assert.That(progress.Groups.Sum(g => g.Closed), Is.EqualTo(progress.ClosedCount));
        }
    }

    [Test]
    public void IterationRowsFollowTheirStartDate()
    {
        var project = Project(Select("Status", StatusOptions),
            Iteration("Iteration", [Sprint("i2", "Sprint 2", "2026-10-19")], [Sprint("i1", "Sprint 1", "2026-10-05")]));
        WriteDay(project, new DateOnly(2026, 10, 19),
            Item("a", 1, phase: null, iteration: ("i2", "Sprint 2", "2026-10-19")),
            Item("b", 2, phase: null, iteration: ("i1", "Sprint 1", "2026-10-05")),
            Item("c", 3, phase: null));

        var progress = Progress(groupField: "Iteration");

        Assert.That(progress.Groups.Select(g => g.Name), Is.EqualTo(new[] { "Sprint 1", "Sprint 2", SiteAnalysis.UnsetState }),
            "Completed iterations are ordered by start date, and 未設定 is last.");
        Assert.That(progress.Groups.Select(g => g.Tasks), Is.EqualTo(new[] { 1, 1, 1 }));
    }

    [Test]
    public void TheStateFieldIsTakenFromTheConfiguration()
    {
        var project = Project(Select("進捗", ["On", "Hold", "Done"]), Select("工程", PhaseOptions));
        WriteDay(project, new DateOnly(2026, 10, 19), Item("a", 1, status: null, custom: ("進捗", 1, "Hold")));

        var progress = Progress(stateField: "進捗");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.States, Is.EqualTo(new[] { "On", "Hold", "Done" }));
            Assert.That(progress.Weeks[0].Counts[1], Is.EqualTo(1));
        }
        var parsed = SiteConfigReader.Parse("""{"title":"x","projects":[{"url":"https://github.com/users/demo/projects/1","name":"n","groupField":"工程"}]}""");
        Assert.That(parsed.Projects[0].StateField, Is.EqualTo("Status"), "Status is the default state field name.");
    }

    [Test]
    public void AProjectWithoutPeriodShowsNoPeriod()
    {
        var project = Project(Select("Status", StatusOptions), Select("工程", PhaseOptions));
        WriteDay(project, new DateOnly(2026, 10, 19), Item("a", 1));
        var config = SiteConfigReader.Parse("""{"title":"x","projects":[{"url":"https://github.com/users/demo/projects/1","name":"n","groupField":"工程"}]}""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.Projects[0].PeriodStart, Is.Null);
            Assert.That(config.Projects[0].PeriodEnd, Is.Null);
            Assert.That(SiteAnalysis.Period(config.Projects[0]), Is.EqualTo("—"));
        }
    }

    [Test]
    public void AMilestoneDueOnTimestampDoesNotBreakTheSite()
    {
        // The recorder stores GraphQL's dueOn as an ISO timestamp, not as a bare date, and real Projects use milestones.
        var project = Project(Select("Status", StatusOptions), Select("工程", PhaseOptions));
        WriteDay(project, new DateOnly(2026, 10, 19), Item("a", 1, milestone: ("SA 完了", "2026-10-30T00:00:00Z")));

        var progress = Progress();

        Assert.That(progress.LatestItems[0].Content!.Milestone!.Title, Is.EqualTo("SA 完了"));
        Assert.That(SiteAnalysis.Date(progress.LatestItems[0].Content!.Milestone!.DueOn!.Value), Is.EqualTo("2026-10-30"));
    }

    [Test]
    public void ConfigurationErrorsNameTheProjectAndField()
    {
        var project = Project(Select("Status", StatusOptions), Select("工程", PhaseOptions), Number("見積"));
        WriteDay(project, new DateOnly(2026, 10, 19), Item("a", 1));

        using (Assert.EnterMultipleScope())
        {
            var unknown = Assert.Throws<InvalidOperationException>(() => Progress(stateField: "状態"))!;
            Assert.That(unknown.Message, Does.Contain("デモ").And.Contain("状態"), "A missing field names the Project and the field.");
            var wrongType = Assert.Throws<InvalidOperationException>(() => Progress(groupField: "見積"))!;
            Assert.That(wrongType.Message, Does.Contain("デモ").And.Contain("見積").And.Contain("単一選択"));
            var empty = Assert.Throws<InvalidOperationException>(() => SiteAnalysis.Compute(Config(number: 9), root))!;
            Assert.That(empty.Message, Does.Contain("デモ").And.Contain("記録がありません"));
        }
    }

    [Test]
    public void TheRenderedSiteIsPrivateRelativeAndFreeOfJudgementWording()
    {
        var project = Project(Select("Status", StatusOptions), Select("工程", PhaseOptions));
        WriteDay(project, new DateOnly(2026, 10, 19), Item("a", 1), Item("b", 2, status: "Ready"));
        WriteDay(project, new DateOnly(2026, 10, 26), Item("a", 1, status: "Done", state: "CLOSED", closedAt: "2026-10-25T03:00:00Z", stateReason: "COMPLETED"));
        var site = BuildSite();
        var index = File.ReadAllText(Path.Combine(site, "index.html"));
        var detail = File.ReadAllText(Path.Combine(site, "p", "demo-1", "index.html"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(index, Does.Contain("デモデータ").And.Contain("noindex").And.Contain("p/demo-1/index.html"));
            Assert.That(detail, Does.Contain("状態の推移").And.Contain("工程ごとの完了"));
            Assert.That(index, Does.Not.Contain("class=\"legend\""), "The list page has no page-level legend.");
            Assert.That(index, Does.Contain("class=\"bar-labels\""));
            Assert.That(index, Does.Contain("Done 1"), "Each list row names the states with a count above zero.");
            Assert.That(detail, Does.Contain("class=\"legend\""), "The detail page keeps its one legend.");
            Assert.That(index, Does.Not.Contain("<script").And.Not.Contain("http://").And.Not.Contain("https://"));
            Assert.That(detail, Does.Not.Contain("<script").And.Not.Contain("http://").And.Not.Contain("https://"));
            Assert.That(Forbidden.Where(word => index.Contains(word, StringComparison.Ordinal) || detail.Contains(word, StringComparison.Ordinal)),
                Is.Empty, "No lateness, forecast, current-phase or verdict wording may appear.");
            foreach (var html in new[] { index, detail })
                foreach (var link in Regex.Matches(html, "(?:href|src)=\"([^\"]*)\"").Select(m => m.Groups[1].Value))
                    Assert.That(link, Does.Not.StartWith("http").And.Not.StartWith("/"), "Every link and resource is relative.");
            Assert.That(File.ReadAllText(Path.Combine(site, "robots.txt")), Does.Contain("Disallow: /"));
            var csv = File.ReadAllBytes(Path.Combine(site, "p", "demo-1", "items.csv"));
            Assert.That(csv.Take(3), Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }), "The CSV opens in Excel with Japanese intact.");
            Assert.That(detail, Does.Contain("download=\"デモ_2026-10-26.csv\""), "The CSV link offers the display name and record date.");
        }
    }
}
