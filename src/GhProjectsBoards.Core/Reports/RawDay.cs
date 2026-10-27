using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GhProjectsBoards.Core.Reports;

// #271: the reading side of the #272 raw record. A day is read with RawStore, which already applies the month's full
// file and the changes files up to that day, so nothing here re-implements the storage format.
internal sealed record RawOption(string Id, string Name);

internal sealed record RawIteration(string Id, string Title, DateOnly StartDate, int Duration);

// A field definition from the Project line. Value types (TEXT, NUMBER, DATE, SINGLE_SELECT, ITERATION) carry values in
// the item lines; the built-in columns (TITLE, ASSIGNEES, ...) repeat the Issue's own data and never do.
internal sealed record RawField(
    string Id, string Name, string DataType, bool IsIssueField,
    IReadOnlyList<RawOption> Options, IReadOnlyList<RawIteration> Iterations, IReadOnlyList<RawIteration> CompletedIterations);

internal sealed record RawProject(
    string Id, string Owner, int Number, string Title, string Url, bool Closed,
    IReadOnlyList<RawField> Fields, DateOnly Date, string TimeZone, int ItemsRead, int? ItemsTotalCount);

internal sealed record RawMilestone(string Title, DateOnly? DueOn);

// Drafts hold only type, id, title and createdAt; PRs have mergedAt instead of the Issue-only keys; a REDACTED item
// has no content at all. Absent keys are null rather than default values.
internal sealed record RawContent(
    string Type, string Id, string Title, DateTimeOffset CreatedAt,
    string? Repository, int? Number, string? Url, string? State, DateTimeOffset? ClosedAt,
    IReadOnlyList<string> Assignees, IReadOnlyList<string> Labels, RawMilestone? Milestone,
    string? StateReason, string? IssueType, string? Parent, IReadOnlyList<string> BlockedBy, DateTimeOffset? MergedAt);

// #308: one recorded Issue timeline fact. The record keeps what the CSV needs (who, when, which Status moved where,
// why an Issue closed); a line written before this change has no events and reads as no recorded history.
internal sealed record RawEvent(
    string Kind, DateTimeOffset At, string? Actor, bool Automated, string? From, string? To, string? StateReason);

internal sealed record RawItem(
    string Id, string Type, bool Archived, DateTimeOffset AddedAt, RawContent? Content,
    IReadOnlyDictionary<string, JsonNode?> Fields, IReadOnlyList<RawEvent> Events);

// The values the site uses. Value types only; anything else in the record is a built-in column or unknown to the site.
internal static class RawDay
{
    internal static readonly HashSet<string> ValueTypes = new(StringComparer.Ordinal) { "TEXT", "NUMBER", "DATE", "SINGLE_SELECT", "ITERATION" };

    internal static SortedSet<DateOnly> Days(string dataRoot, string owner, int number) => RawStore.Days(dataRoot, owner, number);

    internal static RawProject ReadProject(string dataRoot, string owner, int number, DateOnly through)
    {
        var line = RawStore.ProjectLine(dataRoot, owner, number, through);
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        if (root.GetProperty("kind").GetString() != "project") throw new FormatException("Project の行ではありません: " + owner + "/" + number);
        var schema = root.GetProperty("schema").GetString();
        if (schema != RawRecord.Schema) throw new FormatException($"記録の形式が違います（{schema}）: {owner}/{number}");
        var project = root.GetProperty("project");
        var fields = new List<RawField>();
        if (root.TryGetProperty("fields", out var definitions) && definitions.ValueKind == JsonValueKind.Array)
            foreach (var field in definitions.EnumerateArray()) fields.Add(Field(field));
        var total = root.TryGetProperty("itemsTotalCount", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : (int?)null;
        return new RawProject(
            project.GetProperty("id").GetString()!, project.GetProperty("owner").GetString()!, project.GetProperty("number").GetInt32(),
            project.GetProperty("title").GetString()!, project.GetProperty("url").GetString()!, project.GetProperty("closed").GetBoolean(),
            fields, DateOnly.ParseExact(root.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            root.GetProperty("timeZone").GetString()!, root.GetProperty("itemsRead").GetInt32(), total);
    }

    internal static IReadOnlyList<RawItem> ReadItems(string dataRoot, string owner, int number, DateOnly through)
    {
        var folder = RawStore.Folder(dataRoot, owner, number, through);
        var items = new List<RawItem>();
        foreach (var line in RawStore.Read(folder, through).Values) items.Add(ParseItem(line));
        return items;
    }

    internal static RawItem ParseItem(string line)
    {
        using var json = JsonDocument.Parse(line);
        var item = json.RootElement;
        return new RawItem(
            item.GetProperty("id").GetString()!, item.GetProperty("type").GetString()!, item.GetProperty("archived").GetBoolean(),
            Stamp(item.GetProperty("addedAt")) ?? default,
            item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object ? Content(content) : null,
            Values(item), Events(item));
    }

    // #308: events are optional in a line. Missing keys read as absent values and an unknown kind still parses, so a
    // record written by this build stays readable if a later build adds a kind or a key; the site ignores both.
    private static IReadOnlyList<RawEvent> Events(JsonElement item)
    {
        if (!item.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) return [];
        var list = new List<RawEvent>();
        foreach (var change in events.EnumerateArray())
        {
            if (change.ValueKind != JsonValueKind.Object) continue;
            if (Text(change, "kind") is not { } kind || Stamp(change, "at") is not { } at) continue;
            list.Add(new RawEvent(kind, at, Text(change, "actor"),
                change.TryGetProperty("automated", out var automated) && automated.ValueKind == JsonValueKind.True,
                Text(change, "from"), Text(change, "to"), Text(change, "stateReason")));
        }
        return list;
    }

    private static RawField Field(JsonElement field)
    {
        List<RawOption> Options(JsonElement owner)
        {
            var options = new List<RawOption>();
            if (owner.TryGetProperty("options", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var option in array.EnumerateArray())
                    options.Add(new RawOption(option.GetProperty("id").GetString()!, option.GetProperty("name").GetString()!));
            return options;
        }
        List<RawIteration> Iterations(string name)
        {
            var iterations = new List<RawIteration>();
            if (field.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var iteration in array.EnumerateArray())
                    iterations.Add(new RawIteration(
                        iteration.GetProperty("id").GetString()!, iteration.GetProperty("title").GetString()!,
                        DateOnly.ParseExact(iteration.GetProperty("startDate").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                        iteration.GetProperty("duration").GetInt32()));
            return iterations;
        }
        var isIssueField = field.TryGetProperty("isIssueField", out var issue) && issue.ValueKind == JsonValueKind.True;
        return new RawField(
            field.GetProperty("id").GetString()!, field.GetProperty("name").GetString()!, field.GetProperty("dataType").GetString()!, isIssueField,
            Options(field), Iterations("iterations"), Iterations("completedIterations"));
    }

    private static RawContent Content(JsonElement content)
    {
        var type = content.GetProperty("type").GetString()!;
        string? Text(string name) => content.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        List<string> Names(string name)
        {
            var list = new List<string>();
            if (content.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var value in array.EnumerateArray()) if (value.ValueKind == JsonValueKind.String) list.Add(value.GetString()!);
            return list;
        }
        RawMilestone? milestone = content.TryGetProperty("milestone", out var m) && m.ValueKind == JsonValueKind.Object
            ? new RawMilestone(m.GetProperty("title").GetString()!,
                m.TryGetProperty("dueOn", out var due) && due.ValueKind == JsonValueKind.String ? DueDate(due.GetString()!) : null)
            : null;
        return new RawContent(
            type, Text("id") ?? "", Text("title") ?? "", Stamp(content.GetProperty("createdAt")) ?? default,
            Text("repository"), content.TryGetProperty("number", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetInt32() : null,
            Text("url"), Text("state"), Stamp(Text("closedAt")),
            Names("assignees"), Names("labels"), milestone,
            Text("stateReason"), Text("issueType"), Text("parent"), Names("blockedBy"), Stamp(Text("mergedAt")));
    }

    private static IReadOnlyDictionary<string, JsonNode?> Values(JsonElement item)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (item.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
            foreach (var field in fields.EnumerateObject()) values[field.Name] = JsonNode.Parse(field.Value.GetRawText());
        return values;
    }

    private static DateTimeOffset? Stamp(string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;

    // GraphQL returns a milestone's dueOn as an ISO timestamp (2026-10-30T00:00:00Z), not a date, so keep its date part.
    private static DateOnly? DueDate(string text)
        => text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;

    private static DateTimeOffset? Stamp(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? Stamp(element.GetString()) : null;

    private static DateTimeOffset? Stamp(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? Stamp(value) : null;

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // The site's task rule: a leaf Issue, meaning no other item in the same Project on that day carries it as its
    // parent. Parent Issues, PRs, drafts and REDACTED items are therefore not tasks; archived items are.
    internal static IReadOnlySet<string> TaskIds(IReadOnlyList<RawItem> items)
    {
        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items) if (item.Content?.Parent is { } parent) parents.Add(parent);
        var tasks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
            if (item.Type == "ISSUE" && Reference(item.Content) is { } reference && !parents.Contains(reference))
                tasks.Add(item.Id);
        return tasks;
    }

    internal static string? Reference(RawContent? content)
        => content?.Repository is { } repository && content.Number is { } number
            ? repository + "#" + number.ToString(CultureInfo.InvariantCulture) : null;

    internal static string? Text(RawItem item, string field)
        => item.Fields.GetValueOrDefault(field) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static string? OptionName(RawItem item, string field)
        => item.Fields.GetValueOrDefault(field) is JsonObject value && value["name"] is JsonValue name && name.TryGetValue<string>(out var text) ? text : null;

    internal static string? OptionId(RawItem item, string field)
        => item.Fields.GetValueOrDefault(field) is JsonObject value && value["optionId"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : null;

    internal static string? IterationTitle(RawItem item, string field)
        => item.Fields.GetValueOrDefault(field) is JsonObject value && value["title"] is JsonValue title && title.TryGetValue<string>(out var text) ? text : null;

    internal static string? IterationId(RawItem item, string field)
        => item.Fields.GetValueOrDefault(field) is JsonObject value && value["iterationId"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : null;

    // The CSV prints the recorded value as it stands; only NUMBER is reformatted so Excel reads the same number.
    internal static string? Value(RawItem item, RawField field)
        => field.DataType switch
        {
            "TEXT" or "DATE" => Text(item, field.Name),
            "NUMBER" => item.Fields.GetValueOrDefault(field.Name) is JsonValue number && number.TryGetValue<decimal>(out var value)
                ? value.ToString(CultureInfo.InvariantCulture) : null,
            "SINGLE_SELECT" => OptionName(item, field.Name),
            "ITERATION" => IterationTitle(item, field.Name),
            _ => null,
        };

    // The site's close date: closedAt in the record's time zone, not in the viewer's.
    internal static DateOnly? CloseDate(RawContent? content, TimeZoneInfo zone)
        => content?.ClosedAt is { } closed ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(closed, zone).DateTime) : null;

    internal static TimeZoneInfo Zone(RawProject project)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(project.TimeZone); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}
