using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.Core.Reports;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;

// #272: the raw daily record keeps GitHub's own values for every Project field, and stores a month as one full file
// plus the lines that changed each day.
[TestFixture]
internal sealed class RawRecordTests
{
    private string root = "";
    private static readonly DateTimeOffset ReadAt = new(2026, 10, 24, 21, 3, 0, TimeSpan.Zero);

    [SetUp]
    public void Create() => root = Path.Combine(Path.GetTempPath(), "ghpb-raw-" + Guid.NewGuid().ToString("N"));
    [TearDown]
    public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static readonly JsonElement Project = Json("""
        {"__typename":"ProjectV2","id":"PVT_1","number":3,"title":"Sandbox","url":"https://github.com/users/u/projects/3","closed":false,
         "owner":{"__typename":"User","login":"u"},
         "fields":{"totalCount":7,"nodes":[
           {"__typename":"ProjectV2Field","id":"F_title","name":"Title","dataType":"TITLE","isIssueField":false,"issueField":null},
           {"__typename":"ProjectV2Field","id":"F_est","name":"見積","dataType":"NUMBER","isIssueField":false,"issueField":null},
           {"__typename":"ProjectV2Field","id":"F_end","name":"終了日","dataType":"DATE","isIssueField":false,"issueField":null},
           {"__typename":"ProjectV2SingleSelectField","id":"F_phase","name":"工程","dataType":"SINGLE_SELECT","isIssueField":false,"options":[{"id":"o1","name":"PG"},{"id":"o2","name":"PT"}],"issueField":null},
           {"__typename":"ProjectV2IterationField","id":"F_it","name":"Iteration","dataType":"ITERATION","isIssueField":false,
            "configuration":{"duration":14,"startDay":1,"iterations":[{"id":"i2","title":"Sprint 2","startDate":"2026-10-19","duration":14}],"completedIterations":[{"id":"i1","title":"Sprint 1","startDate":"2026-10-05","duration":14}]}},
           {"__typename":"ProjectV2SingleSelectField","id":"F_org","name":"Priority","dataType":"SINGLE_SELECT","isIssueField":true,"options":[],
            "issueField":{"__typename":"IssueFieldSingleSelect","id":"IF_1","options":[{"id":"p1","name":"High"}]}},
           {"__typename":"ProjectV2Field","id":"F_labels","name":"Labels","dataType":"LABELS","isIssueField":false,"issueField":null}]}}
        """);

    private static JsonElement Issue(string id, int number, string state = "OPEN", decimal estimate = 8, string? iteration = "i2")
    {
        var iterationValue = iteration is null ? "" :
            """{"__typename":"ProjectV2ItemFieldIterationValue","title":"Sprint 2","iterationId":"@IT@","startDate":"2026-10-19","duration":14,"field":{"id":"F_it","name":"Iteration"}},""".Replace("@IT@", iteration, StringComparison.Ordinal);
        return Json("""
        {"id":"@ID@","type":"ISSUE","isArchived":false,"createdAt":"2026-10-20T00:00:00Z",
         "content":{"__typename":"Issue","id":"I_@N@","number":@N@,"title":"Task @N@","url":"https://github.com/u/r/issues/@N@",
           "state":"@STATE@","stateReason":@REASON@,"createdAt":"2026-10-20T00:00:00Z","closedAt":@CLOSED@,
           "repository":{"nameWithOwner":"u/r"},"assignees":{"totalCount":2,"nodes":[{"login":"zed"},{"login":"amy"}]},
           "labels":{"totalCount":0,"nodes":[]},"milestone":null,"issueType":{"name":"Task"},
           "parent":{"number":1,"repository":{"nameWithOwner":"u/r"}},"blockedBy":{"totalCount":1,"nodes":[{"number":2,"repository":{"nameWithOwner":"u/r"}}]}},
         "fieldValues":{"totalCount":@VALUES@,"nodes":[
           {"__typename":"ProjectV2ItemFieldTextValue","text":"Task @N@","field":{"id":"F_title","name":"Title"}},
           {"__typename":"ProjectV2ItemFieldNumberValue","number":@EST@,"field":{"id":"F_est","name":"見積"}},
           {"__typename":"ProjectV2ItemFieldDateValue","date":"2026-11-02","field":{"id":"F_end","name":"終了日"}},
           {"__typename":"ProjectV2ItemFieldSingleSelectValue","name":"PG","optionId":"o1","field":{"id":"F_phase","name":"工程"}},
           @ITERATION@
           {"__typename":"ProjectV2ItemIssueFieldValue","field":{"id":"F_org","name":"Priority"},"issueFieldValue":{"__typename":"IssueFieldSingleSelectValue","optionId":"p1"}},
           {"__typename":"ProjectV2ItemFieldLabelValue","field":{"id":"F_labels","name":"Labels"}}]}}
        """.Replace("@ID@", id, StringComparison.Ordinal).Replace("@N@", number.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@STATE@", state, StringComparison.Ordinal)
            .Replace("@REASON@", state == "CLOSED" ? "\"COMPLETED\"" : "null", StringComparison.Ordinal)
            .Replace("@CLOSED@", state == "CLOSED" ? "\"2026-10-24T03:00:00Z\"" : "null", StringComparison.Ordinal)
            .Replace("@EST@", estimate.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@VALUES@", iteration is null ? "6" : "7", StringComparison.Ordinal)
            .Replace("@ITERATION@", iterationValue, StringComparison.Ordinal));
    }

    private static RawSnapshot Snapshot(DateOnly date, params JsonElement[] items) => RawRecord.Build(Project, items, ReadAt, date, "Asia/Tokyo");

    [Test]
    public void EveryProjectFieldIsKeptByNameWithoutTheBuiltInColumns()
    {
        var snapshot = Snapshot(new(2026, 10, 25), Issue("PVTI_a", 10));
        var item = JsonNode.Parse(snapshot.Items["PVTI_a"])!.AsObject();
        var fields = item["fields"]!.AsObject();
        var project = JsonNode.Parse(snapshot.ProjectLine)!.AsObject();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fields.Select(p => p.Key), Is.EqualTo(new[] { "Iteration", "Priority", "工程", "終了日", "見積" }), "Title and Labels repeat the Issue's own data.");
            Assert.That(fields["見積"]!.GetValue<decimal>(), Is.EqualTo(8m));
            Assert.That(fields["工程"]!["name"]!.GetValue<string>(), Is.EqualTo("PG"));
            Assert.That(fields["Iteration"]!["iterationId"]!.GetValue<string>(), Is.EqualTo("i2"));
            Assert.That(fields["Priority"]!["name"]!.GetValue<string>(), Is.EqualTo("High"), "An organisation Issue Field's option name comes from its definition.");
            Assert.That(item["content"]!["assignees"]!.AsArray().Select(a => a!.GetValue<string>()), Is.EqualTo(new[] { "amy", "zed" }));
            Assert.That(item["content"]!["blockedBy"]![0]!.GetValue<string>(), Is.EqualTo("u/r#2"));
            Assert.That(item["content"]!["parent"]!.GetValue<string>(), Is.EqualTo("u/r#1"));
            Assert.That(project["schema"]!.GetValue<string>(), Is.EqualTo(RawRecord.Schema));
            Assert.That(project["fields"]!.AsArray().Select(f => f!["name"]!.GetValue<string>()),
                Is.EqualTo(new[] { "Iteration", "Labels", "Priority", "Title", "工程", "終了日", "見積" }), "Every field definition is recorded, sorted by name.");
            Assert.That(project["fields"]!.AsArray().Single(f => f!["name"]!.GetValue<string>() == "Iteration")!["completedIterations"]!.AsArray(), Has.Count.EqualTo(1));
            Assert.That(snapshot.ProjectLine, Does.Not.Contain("\\u"), "Japanese text stays readable in the files.");
        }
    }

    [Test]
    public void TheSameValuesAlwaysGiveTheSameLine()
    {
        var a = Snapshot(new(2026, 10, 25), Issue("PVTI_a", 10)).Items["PVTI_a"];
        var b = Snapshot(new(2026, 10, 26), Issue("PVTI_a", 10)).Items["PVTI_a"];
        Assert.That(b, Is.EqualTo(a));
        Assert.That(a, Does.Not.Contain("\n"));
    }

    [Test]
    public void ATruncatedListFailsInsteadOfRecordingTooLittle()
    {
        var text = Issue("PVTI_a", 10).GetRawText().Replace("\"totalCount\":2", "\"totalCount\":101", StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Snapshot(new(2026, 10, 25), Json(text)));
    }

    [Test]
    public void AMonthIsOneFullFileThenOnlyTheChangedLines()
    {
        var day1 = new DateOnly(2026, 10, 25);
        var first = RawStore.Write(root, Snapshot(day1, Issue("PVTI_a", 10), Issue("PVTI_b", 11), Issue("PVTI_c", 12)));
        var second = RawStore.Write(root, Snapshot(day1.AddDays(1), Issue("PVTI_a", 10, state: "CLOSED"), Issue("PVTI_b", 11), Issue("PVTI_d", 13)));
        var third = RawStore.Write(root, Snapshot(day1.AddDays(2), Issue("PVTI_a", 10, state: "CLOSED"), Issue("PVTI_b", 11), Issue("PVTI_d", 13)));
        var folder = RawStore.Folder(root, "u", 3, day1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Path.GetFileName(first.Path), Is.EqualTo("full-2026-10-25.jsonl"));
            Assert.That(File.ReadAllLines(first.Path), Has.Length.EqualTo(4), "The Project line and every item.");
            Assert.That(Path.GetFileName(second.Path), Is.EqualTo("changes-2026-10-26.jsonl"));
            Assert.That((second.Items, second.Removed), Is.EqualTo((2, 1)), "a closed, d added, c removed; b unchanged is not written.");
            Assert.That(File.ReadAllLines(third.Path), Has.Length.EqualTo(1), "A day with no change still proves it was read.");
            Assert.That(RawStore.Read(folder, day1).Keys, Is.EqualTo(new[] { "PVTI_a", "PVTI_b", "PVTI_c" }));
            Assert.That(RawStore.Read(folder, day1.AddDays(2)).Keys, Is.EqualTo(new[] { "PVTI_a", "PVTI_b", "PVTI_d" }));
            Assert.That(RawStore.Read(folder, day1.AddDays(2))["PVTI_a"], Does.Contain("\"state\":\"CLOSED\""));
        }
    }

    [Test]
    public void ARunAgainOnTheSameDayReplacesThatDay()
    {
        var day1 = new DateOnly(2026, 10, 25);
        RawStore.Write(root, Snapshot(day1, Issue("PVTI_a", 10)));
        RawStore.Write(root, Snapshot(day1.AddDays(1), Issue("PVTI_a", 10, estimate: 12)));
        var again = RawStore.Write(root, Snapshot(day1.AddDays(1), Issue("PVTI_a", 10, estimate: 16)));

        Assert.That(again.Items, Is.EqualTo(1));
        Assert.That(RawStore.Read(RawStore.Folder(root, "u", 3, day1), day1.AddDays(1))["PVTI_a"], Does.Contain("\"見積\":16"));
    }

    [Test]
    public void ANewMonthStartsWithAFullFileAndAnEarlierDayIsRefused()
    {
        RawStore.Write(root, Snapshot(new(2026, 10, 31), Issue("PVTI_a", 10)));
        var november = RawStore.Write(root, Snapshot(new(2026, 11, 1), Issue("PVTI_a", 10)));

        Assert.That(Path.GetFileName(november.Path), Is.EqualTo("full-2026-11-01.jsonl"));
        Assert.Throws<InvalidOperationException>(() => RawStore.Write(root, Snapshot(new(2026, 10, 30), Issue("PVTI_a", 10))));
    }
}
