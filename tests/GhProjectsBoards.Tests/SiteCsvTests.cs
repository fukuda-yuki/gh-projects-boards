using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using GhProjectsBoards.Core.Reports;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #271: the management site's per-Project CSV prints the latest raw record as it stands: one row per item, the fixed
// identity columns first, then one column per Project field that holds a value, UTF-8 BOM and CRLF so Excel opens it.
// The fixtures go through the real parsers (RawRecord.Line + RawDay.ParseItem), never a mock.
[TestFixture]
internal sealed class SiteCsvTests
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static readonly string[] FixedHeaders =
    [
        "種類", "タスク", "リポジトリ", "番号", "タイトル", "状態", "閉じた理由", "閉じた日", "親",
        "担当者", "ラベル", "マイルストーン", "Issue の種類", "ブロック元", "アーカイブ", "追加日", "URL",
    ];
    private static readonly DateTimeOffset DefaultAddedAt = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    // The built-in definitions sit between value fields: they must not add a column of their own.
    private static readonly RawField TitleField = new("F_title", "Title", "TITLE", false, [], [], []);
    private static readonly RawField Estimate = new("F_est", "見積", "NUMBER", false, [], [], []);
    private static readonly RawField AssigneesField = new("F_assignees", "Assignees", "ASSIGNEES", false, [], [], []);
    private static readonly RawField EndDate = new("F_end", "終了日", "DATE", false, [], [], []);
    private static readonly RawField Phase = new("F_phase", "工程", "SINGLE_SELECT", false,
        [new RawOption("o1", "PG"), new RawOption("o2", "PT")], [], []);
    private static readonly RawField Sprint = new("F_it", "Iteration", "ITERATION", false, [],
        [new RawIteration("i2", "Sprint 2", new DateOnly(2026, 10, 19), 14),
         new RawIteration("i1", "Sprint 1", new DateOnly(2026, 10, 5), 14)], []);
    private static readonly RawField Note = new("F_note", "メモ", "TEXT", false, [], [], []);
    private static readonly IReadOnlyList<RawField> Fields = [TitleField, Estimate, AssigneesField, EndDate, Phase, Sprint, Note];

    [Test]
    public void TheFileIsUtf8WithBomAndEveryLineEndsWithCrlf()
    {
        var first = Issue("PVTI_a", 10);
        var second = Issue("PVTI_b", 11);
        var items = new[] { first, second };
        var bytes = SiteCsv.Render(Project(items), items, new HashSet<string>(StringComparer.Ordinal));

        Assert.That(bytes.Take(3), Is.EqualTo(Bom), "Excel needs the UTF-8 BOM.");
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.That(text, Does.EndWith("\r\n"), "the last row ends with CRLF.");
        AssertCrlf(text);
        Assert.That(Read(bytes).Rows, Has.Count.EqualTo(2));
    }

    [Test]
    public void TheHeaderIsTheSeventeenFixedColumnsThenTheValueFieldsInProjectOrder()
    {
        var item = Issue("PVTI_a", 10);
        var sheet = Read(Render(item));
        var expected = FixedHeaders.Concat(new[] { "見積", "終了日", "工程", "Iteration", "メモ" }).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sheet.Headers, Is.EqualTo(expected), "built-in Title and Assignees definitions add no column.");
            Assert.That(sheet.Headers, Has.Count.EqualTo(22));
            Assert.That(sheet.Headers, Does.Not.Contain("Title"));
            Assert.That(sheet.Headers, Does.Not.Contain("Assignees"));
        }
    }

    [Test]
    public void EveryItemGetsOneRowInRepositoryThenNumberOrderWithDraftsAndRedactedLast()
    {
        var ten = Issue("PVTI_ten", 10, title: "Ten");
        var seven = PullRequest("PVTI_seven", 7, mergedAt: Moment(2026, 10, 23, 1));
        var one = Issue("PVTI_one", 1, repository: "a/r");
        var two = Issue("PVTI_two", 2, repository: "z/r");
        var draft = Draft("PVTI_draft");
        var hidden = Redacted("PVTI_hidden");
        // Deliberately not in the file's order: the renderer sorts and never drops an item.
        var items = new[] { ten, seven, two, one, draft, hidden };
        var sheet = Read(SiteCsv.Render(Project(items), items, new HashSet<string>(StringComparer.Ordinal)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sheet.Rows, Has.Count.EqualTo(items.Length), "one row per item, drafts and REDACTED items included.");
            Assert.That(sheet.Rows.Select(row => Cell(sheet, row, "リポジトリ")), Is.EqualTo(new[] { "a/r", "u/r", "u/r", "z/r", "", "" }));
            Assert.That(sheet.Rows.Select(row => Cell(sheet, row, "番号")), Is.EqualTo(new[] { "1", "7", "10", "2", "", "" }));
            Assert.That(sheet.Rows.Select(row => Cell(sheet, row, "種類")), Is.EqualTo(new[] { "Issue", "PR", "Issue", "Issue", "下書き", "非公開" }));
            Assert.That(Cell(sheet, sheet.Rows[4], "タイトル"), Is.EqualTo("Idea"), "a draft keeps its title.");
            Assert.That(Cell(sheet, sheet.Rows[5], "タイトル"), Is.Empty, "a REDACTED item has no content to print.");
            Assert.That(Cell(sheet, sheet.Rows[5], "追加日"), Is.Not.Empty, "a REDACTED item still has addedAt.");
        }
    }

    [Test]
    public void ACommaQuoteOrLineBreakInATitleStaysInOneQuotedCell()
    {
        var item = Issue("PVTI_a", 10, title: "He said \"hi\", then\nmoved on");
        var bytes = Render(item);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.That(text, Does.Contain("\"He said \"\"hi\"\", then\r\nmoved on\""),
            "RFC 4180 quoting encloses the cell, doubles internal quotes and writes the break as CRLF.");

        var sheet = Read(bytes);
        Assert.That(sheet.Rows, Has.Count.EqualTo(1), "the embedded line break does not split the row.");
        Assert.That(Cell(sheet, sheet.Rows[0], "タイトル"), Is.EqualTo("He said \"hi\", then\r\nmoved on"));
    }

    [Test]
    public void StateIsTheRecordedOneWithMergedPullRequestsAndCloseReasons()
    {
        var open = Issue("PVTI_open", 11);
        var closed = Issue("PVTI_closed", 12, state: "CLOSED", stateReason: "COMPLETED", closedAt: Moment(2026, 10, 24, 3));
        var merged = PullRequest("PVTI_merged", 13, mergedAt: Moment(2026, 10, 23, 1));
        var draft = Draft("PVTI_draft");
        var items = new[] { open, closed, merged, draft };
        var sheet = Read(SiteCsv.Render(Project(items), items, new HashSet<string>(StringComparer.Ordinal)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sheet.Rows.Select(row => Cell(sheet, row, "状態")), Is.EqualTo(new[] { "OPEN", "CLOSED", "MERGED", "" }));
            Assert.That(sheet.Rows.Select(row => Cell(sheet, row, "閉じた理由")), Is.EqualTo(new[] { "", "COMPLETED", "", "" }));
        }
    }

    [Test]
    public void ValueFieldsPrintTheRecordedValueAndLeaveMissingCellsEmpty()
    {
        var fields = new JsonObject
        {
            ["見積"] = 8.5m,
            ["終了日"] = "2026-11-02",
            ["メモ"] = "memo",
            ["工程"] = new JsonObject { ["name"] = "PG", ["optionId"] = "o1" },
            ["Iteration"] = new JsonObject { ["title"] = "Sprint 2", ["iterationId"] = "i2", ["startDate"] = "2026-10-19", ["duration"] = 14 },
        };
        var withValues = Issue("PVTI_1", 1, fields: fields);
        var without = Issue("PVTI_2", 2);
        var sheet = Read(Render(withValues, without));
        var row = sheet.Rows[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Cell(sheet, row, "見積"), Is.EqualTo("8.5"), "a number uses the invariant decimal form.");
            Assert.That(Cell(sheet, row, "終了日"), Is.EqualTo("2026-11-02"));
            Assert.That(Cell(sheet, row, "メモ"), Is.EqualTo("memo"));
            Assert.That(Cell(sheet, row, "工程"), Is.EqualTo("PG"), "a single select prints the option name.");
            Assert.That(Cell(sheet, row, "Iteration"), Is.EqualTo("Sprint 2"), "an iteration prints its title.");
        }
        foreach (var header in new[] { "見積", "終了日", "メモ", "工程", "Iteration" })
            Assert.That(Cell(sheet, sheet.Rows[1], header), Is.Empty, "a missing value is an empty cell: " + header);
    }

    [Test]
    public void TaskAndArchiveColumnsSayYesOrNo()
    {
        var task = Issue("PVTI_task", 1, archived: true);
        var parent = Issue("PVTI_parent", 2);
        var items = new[] { task, parent };
        var tasks = new HashSet<string>(StringComparer.Ordinal) { task.Id };
        var sheet = Read(SiteCsv.Render(Project(items), items, tasks));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Cell(sheet, sheet.Rows[0], "タスク"), Is.EqualTo("はい"));
            Assert.That(Cell(sheet, sheet.Rows[0], "アーカイブ"), Is.EqualTo("はい"));
            Assert.That(Cell(sheet, sheet.Rows[1], "タスク"), Is.EqualTo("いいえ"));
            Assert.That(Cell(sheet, sheet.Rows[1], "アーカイブ"), Is.EqualTo("いいえ"));
        }
    }

    [Test]
    public void CloseDateAndAddedDateAreDatesInTheProjectTimeZone()
    {
        var closed = Issue("PVTI_closed", 1, state: "CLOSED", stateReason: "COMPLETED",
            closedAt: new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero),
            addedAt: new DateTimeOffset(2026, 10, 24, 16, 30, 0, TimeSpan.Zero));
        var open = Issue("PVTI_open", 2, addedAt: new DateTimeOffset(2026, 10, 25, 15, 0, 0, TimeSpan.Zero));
        var sheet = Read(Render(closed, open));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Cell(sheet, sheet.Rows[0], "閉じた日"), Is.EqualTo("2026-10-25"), "20:00 UTC is 2026-10-25 in JST.");
            Assert.That(Cell(sheet, sheet.Rows[0], "追加日"), Is.EqualTo("2026-10-25"), "16:30 UTC is 2026-10-25 in JST.");
            Assert.That(Cell(sheet, sheet.Rows[1], "追加日"), Is.EqualTo("2026-10-26"), "15:00 UTC is the next day in JST.");
            Assert.That(Cell(sheet, sheet.Rows[1], "閉じた日"), Is.Empty);
        }
    }

    [Test]
    public void IdentityColumnsPrintParentPeopleLabelsMilestoneIssueTypeBlockedByAndUrl()
    {
        var item = Issue("PVTI_a", 10, title: "Task 10", assignees: ["zed", "amy"], labels: ["bug", "ui"],
            milestone: "v1", issueType: "Task", parent: "u/r#1", blockedBy: ["u/r#2", "u/r#3"]);
        var sheet = Read(Render(item));
        var row = sheet.Rows[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Cell(sheet, row, "タイトル"), Is.EqualTo("Task 10"));
            Assert.That(Cell(sheet, row, "親"), Is.EqualTo("u/r#1"));
            Assert.That(Cell(sheet, row, "担当者"), Is.EqualTo("zed; amy"));
            Assert.That(Cell(sheet, row, "ラベル"), Is.EqualTo("bug; ui"));
            Assert.That(Cell(sheet, row, "マイルストーン"), Is.EqualTo("v1"));
            Assert.That(Cell(sheet, row, "Issue の種類"), Is.EqualTo("Task"));
            Assert.That(Cell(sheet, row, "ブロック元"), Is.EqualTo("u/r#2; u/r#3"));
            Assert.That(Cell(sheet, row, "URL"), Is.EqualTo("https://github.com/u/r/issues/10"));
        }
    }

    private sealed record Sheet(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

    private static byte[] Render(params RawItem[] items) => SiteCsv.Render(Project(items), items, new HashSet<string>(StringComparer.Ordinal));

    private static RawProject Project(params RawItem[] items)
        => new("PVT_1", "u", 3, "Sandbox", "https://github.com/users/u/projects/3", false,
            Fields, new DateOnly(2026, 10, 25), "Asia/Tokyo", items.Length, items.Length);

    private static RawItem Issue(string id, int number, string title = "Task", string repository = "u/r",
        string state = "OPEN", string? stateReason = null, DateTimeOffset? closedAt = null,
        JsonObject? fields = null, DateTimeOffset? addedAt = null, bool archived = false,
        IReadOnlyList<string>? assignees = null, IReadOnlyList<string>? labels = null, string? milestone = null,
        string? issueType = null, string? parent = null, IReadOnlyList<string>? blockedBy = null)
        => Item(id, "ISSUE", new JsonObject
        {
            ["type"] = "Issue",
            ["id"] = "I_" + number.ToString(CultureInfo.InvariantCulture),
            ["title"] = title,
            ["createdAt"] = Stamp(DefaultAddedAt),
            ["repository"] = repository,
            ["number"] = number,
            ["url"] = "https://github.com/" + repository + "/issues/" + number.ToString(CultureInfo.InvariantCulture),
            ["state"] = state,
            ["closedAt"] = closedAt is { } closed ? Stamp(closed) : null,
            ["assignees"] = Strings(assignees),
            ["labels"] = Strings(labels),
            ["milestone"] = milestone is null ? null : new JsonObject { ["title"] = milestone, ["dueOn"] = null },
            ["stateReason"] = stateReason,
            ["issueType"] = issueType,
            ["parent"] = parent,
            ["blockedBy"] = Strings(blockedBy),
            ["mergedAt"] = null,
        }, fields, addedAt, archived);

    private static RawItem PullRequest(string id, int number, string title = "Fix", string repository = "u/r",
        DateTimeOffset? mergedAt = null, JsonObject? fields = null, DateTimeOffset? addedAt = null, bool archived = false)
        => Item(id, "PULL_REQUEST", new JsonObject
        {
            ["type"] = "PullRequest",
            ["id"] = "PR_" + number.ToString(CultureInfo.InvariantCulture),
            ["title"] = title,
            ["createdAt"] = Stamp(DefaultAddedAt),
            ["repository"] = repository,
            ["number"] = number,
            ["url"] = "https://github.com/" + repository + "/pull/" + number.ToString(CultureInfo.InvariantCulture),
            ["state"] = mergedAt is null ? "OPEN" : "CLOSED",
            ["closedAt"] = mergedAt is { } merged ? Stamp(merged) : null,
            ["assignees"] = new JsonArray(),
            ["labels"] = new JsonArray(),
            ["milestone"] = null,
            ["mergedAt"] = mergedAt is { } value ? Stamp(value) : null,
        }, fields, addedAt, archived);

    private static RawItem Draft(string id, string title = "Idea", DateTimeOffset? addedAt = null, bool archived = false)
        => Item(id, "DRAFT_ISSUE", new JsonObject
        {
            ["type"] = "DraftIssue",
            ["id"] = "DI_1",
            ["title"] = title,
            ["createdAt"] = Stamp(DefaultAddedAt),
        }, null, addedAt, archived);

    private static RawItem Redacted(string id, DateTimeOffset? addedAt = null, bool archived = false)
        => Item(id, "REDACTED", null, null, addedAt, archived);

    private static RawItem Item(string id, string type, JsonObject? content, JsonObject? fields,
        DateTimeOffset? addedAt, bool archived)
        => RawDay.ParseItem(RawRecord.Line(new JsonObject
        {
            ["kind"] = "item",
            ["id"] = id,
            ["type"] = type,
            ["archived"] = archived,
            ["addedAt"] = Stamp(addedAt ?? DefaultAddedAt),
            ["content"] = content,
            ["fields"] = fields ?? new JsonObject(),
        }));

    private static JsonArray Strings(IReadOnlyList<string>? values)
    {
        var array = new JsonArray();
        foreach (var value in values ?? Array.Empty<string>()) array.Add(value);
        return array;
    }

    private static DateTimeOffset Moment(int year, int month, int day, int hour)
        => new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private static string Stamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static Sheet Read(byte[] bytes)
    {
        var text = Body(bytes);
        AssertCrlf(text);
        var rows = Parse(text);
        Assert.That(rows, Is.Not.Empty, "a CSV file holds at least its header row.");
        foreach (var row in rows.Skip(1))
            Assert.That(row, Has.Count.EqualTo(rows[0].Count), "every row has one cell per column.");
        return new(rows[0], rows.Skip(1).ToArray());
    }

    private static string Body(byte[] bytes)
    {
        Assert.That(bytes.Take(3), Is.EqualTo(Bom), "Excel needs the UTF-8 BOM.");
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
    }

    private static void AssertCrlf(string text)
    {
        for (var n = 0; n < text.Length; n++)
        {
            if (text[n] == '\n') Assert.That(n > 0 && text[n - 1] == '\r', Is.True, "a newline is part of CRLF: " + n);
            if (text[n] == '\r') Assert.That(n + 1 < text.Length && text[n + 1] == '\n', Is.True, "a carriage return is part of CRLF: " + n);
        }
    }

    private static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var n = 0; n < text.Length;)
        {
            var c = text[n];
            if (quoted)
            {
                if (c != '"')
                {
                    cell.Append(c);
                    n++;
                }
                else if (n + 1 < text.Length && text[n + 1] == '"')
                {
                    cell.Append('"');
                    n += 2;
                }
                else
                {
                    quoted = false;
                    n++;
                }
                continue;
            }
            switch (c)
            {
                case '"':
                    quoted = true;
                    n++;
                    break;
                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    n++;
                    break;
                case '\r':
                    Assert.That(n + 1 < text.Length && text[n + 1] == '\n', Is.True, "rows end with CRLF.");
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = [];
                    n += 2;
                    break;
                default:
                    cell.Append(c);
                    n++;
                    break;
            }
        }
        Assert.That(quoted, Is.False, "every quoted cell is closed.");
        Assert.That(cell.Length, Is.Zero, "the last row ends with CRLF.");
        Assert.That(row, Is.Empty);
        return rows;
    }

    private static string Cell(Sheet sheet, IReadOnlyList<string> row, string header)
    {
        var index = -1;
        for (var n = 0; n < sheet.Headers.Count; n++)
            if (sheet.Headers[n] == header) { index = n; break; }
        Assert.That(index, Is.GreaterThanOrEqualTo(0), "the CSV has the " + header + " column.");
        return row[index];
    }
}
