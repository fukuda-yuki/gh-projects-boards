using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.Reports;

// #272: the raw daily record of one Project. It keeps GitHub's own values for every item and every Project field,
// with no app interpretation and no settings file, because GitHub keeps no history of Project field values: a day
// that is not recorded is lost. Line 1 of every file is the Project, then one item per line.
internal sealed record RawSnapshot(DateOnly Date, string Owner, int Number, string ProjectLine, IReadOnlyDictionary<string, string> Items);

internal static class RawRecord
{
    internal const string Schema = "ghpb-raw/1";
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    // Value types a person sets on a Project. TITLE, ASSIGNEES, LABELS and the other built-in columns repeat the
    // Issue's own data, which the item line already holds.
    private static readonly HashSet<string> ValueTypes = new(StringComparer.Ordinal) { "TEXT", "NUMBER", "DATE", "SINGLE_SELECT", "ITERATION" };

    private const string IssueField = "issueField { __typename ... on IssueFieldDate { id } ... on IssueFieldNumber { id } ... on IssueFieldText { id } ... on IssueFieldSingleSelect { id options { id name } } }";
    internal static readonly string ProjectQuery = """
        query RawProject($id: ID!) {
          node(id: $id) {
            __typename
            ... on ProjectV2 {
              id number title url closed
              owner { __typename ... on User { login } ... on Organization { login } }
              fields(first: 100) {
                totalCount
                nodes {
                  __typename
                  ... on ProjectV2FieldCommon { id name dataType isIssueField }
                  ... on ProjectV2Field { ISSUE_FIELD }
                  ... on ProjectV2SingleSelectField { options { id name } ISSUE_FIELD }
                  ... on ProjectV2IterationField { configuration { duration startDay iterations { id title startDate duration } completedIterations { id title startDate duration } } }
                }
              }
            }
          }
        }
        """.Replace("ISSUE_FIELD", IssueField);
    private const string ContentFields = "number title url state createdAt closedAt repository { nameWithOwner } assignees(first: 100) { totalCount nodes { login } } labels(first: 100) { totalCount nodes { name } } milestone { title dueOn }";
    internal static readonly string ItemsQuery = """
        query RawItems($id: ID!, $after: String) {
          node(id: $id) {
            __typename
            ... on ProjectV2 {
              items(first: 50, after: $after) {
                totalCount pageInfo { hasNextPage endCursor }
                nodes {
                  id type isArchived createdAt
                  content {
                    __typename
                    ... on DraftIssue { id title createdAt }
                    ... on Issue { id CONTENT stateReason issueType { name } parent { number repository { nameWithOwner } } blockedBy(first: 100) { totalCount nodes { number repository { nameWithOwner } } } }
                    ... on PullRequest { id CONTENT mergedAt }
                  }
                  fieldValues(first: 100) {
                    totalCount
                    nodes {
                      __typename
                      ... on ProjectV2ItemFieldTextValue { text field { ...F } }
                      ... on ProjectV2ItemFieldNumberValue { number field { ...F } }
                      ... on ProjectV2ItemFieldDateValue { date field { ...F } }
                      ... on ProjectV2ItemFieldSingleSelectValue { name optionId field { ...F } }
                      ... on ProjectV2ItemFieldIterationValue { title iterationId startDate duration field { ...F } }
                      ... on ProjectV2ItemIssueFieldValue { field { ...F } issueFieldValue { __typename ... on IssueFieldDateValue { value } ... on IssueFieldNumberValue { value } ... on IssueFieldTextValue { value } ... on IssueFieldSingleSelectValue { optionId } } }
                    }
                  }
                }
              }
            }
          }
        }
        fragment F on ProjectV2FieldConfiguration { ... on ProjectV2FieldCommon { id name } }
        """.Replace("CONTENT", ContentFields);

    // #308: the Issue timeline facts kept with the item line. Status changes, closes and reopens are the only kinds
    // the record stores; itemTypes keeps a page to those three. GitHub returns the timeline oldest first.
    private const string TimelineFields = "totalCount pageInfo { hasNextPage endCursor } nodes { __typename ... on ProjectV2ItemStatusChangedEvent { createdAt actor { __typename login } project { id } previousStatus status } ... on ClosedEvent { createdAt actor { __typename login } stateReason } ... on ReopenedEvent { createdAt actor { __typename login } } }";
    internal static readonly string HistoriesQuery = """
        query RawIssueHistories($ids: [ID!]!, $after: String) {
          nodes(ids: $ids) {
            __typename
            ... on Issue {
              id
              timelineItems(first: 100, itemTypes: [PROJECT_V2_ITEM_STATUS_CHANGED_EVENT, CLOSED_EVENT, REOPENED_EVENT], after: $after) { TIMELINE }
            }
          }
        }
        """.Replace("TIMELINE", TimelineFields);

    // Reads the whole Project on a read-only lease. A page that fails transiently is retried; anything that would make
    // the record incomplete (a truncated list, an item count that moved during the read) fails the run, because a
    // missing item would be stored as removed.
    internal static async Task<RawSnapshot> ReadAsync(GhConnectionService service, string projectUrl, TimeZoneInfo timeZone,
        Func<DateTimeOffset> clock, CancellationToken token = default)
    {
        var connection = await service.ConnectAsync(token);
        if (!connection.IsConnected) throw new InvalidOperationException(ConnectionViewModel.FailureText(connection.Result.Failure));
        var context = connection.Context!;
        var project = await new ProjectDiscovery(service).ResolveAsync(context, projectUrl, token);
        using var lease = await service.BeginOperationAsync(context, token, mutation: false);
        for (var attempt = 1; ; attempt++)
        {
            var projectData = await Send(lease, ProjectQuery, new Dictionary<string, object?> { ["id"] = project.Id.NodeId }, token);
            var items = new List<JsonElement>();
            var counts = new List<int>();
            string? cursor = null;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                var page = await Send(lease, ItemsQuery, new Dictionary<string, object?> { ["id"] = project.Id.NodeId, ["after"] = cursor }, token);
                var connectionNode = page.GetProperty("node").GetProperty("items");
                counts.Add(connectionNode.GetProperty("totalCount").GetInt32());
                items.AddRange(connectionNode.GetProperty("nodes").EnumerateArray().Select(n => n.Clone()));
                var info = connectionNode.GetProperty("pageInfo");
                if (!info.GetProperty("hasNextPage").GetBoolean()) break;
                cursor = info.GetProperty("endCursor").GetString() ?? throw new InvalidOperationException("ページの続きを取得できませんでした。");
                if (!cursors.Add(cursor)) throw new InvalidOperationException("同じページが繰り返し返されました。");
            }
            // GitHub's totalCount can include items the connection never returns (Project 3 reports 1,042 and lists
            // 1,041 on every read), so only a count that moved during the read means the record may be incomplete.
            // Both numbers go into the Project line.
            var distinct = items.Select(i => i.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count();
            if (counts.Distinct().Count() == 1)
            {
                var node = projectData.GetProperty("node");
                var histories = await ReadHistories(lease, node.GetProperty("id").GetString()!, items, token);
                var readAt = clock();
                var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(readAt, timeZone).DateTime);
                return Build(node, WithHistories(items, histories), readAt, date, timeZone.Id, counts[0]);
            }
            if (attempt == 3) throw new InvalidOperationException($"読み込み中にアイテム数が変わり続けました（{string.Join(" / ", counts.Distinct())} 件、取得 {distinct} 件）。");
        }
    }

    private static async Task<JsonElement> Send(GhConnectionService.OperationLease lease, string query, Dictionary<string, object?> variables, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await lease.SendAsync(ApiRequest.GraphQl(query, variables), token);
            if (result.IsSuccess && result.Data is { } data && data.TryGetProperty("data", out var body)) return body.Clone();
            var transient = result.Failure is FailureKind.Network or FailureKind.RateLimited or FailureKind.TimedOut or FailureKind.InvalidResponse;
            if (!transient || attempt == 4) throw new InvalidOperationException("GitHub から読み込めませんでした: " + result.FailureReason());
            await Task.Delay(TimeSpan.FromSeconds(5 * attempt), token);
        }
    }

    private const int HistoryBatch = 25;

    // #308: reads every Issue's timeline in batches of at most 25 Issues. A batch shares its first page; an Issue
    // with more than 100 events continues from its own cursor, because a cursor belongs to one connection. A count
    // that moves or does not add up fails the run: a partial history is never recorded as a complete one.
    private static async Task<Dictionary<string, JsonObject>> ReadHistories(GhConnectionService.OperationLease lease, string projectId,
        IReadOnlyList<JsonElement> items, CancellationToken token)
    {
        var histories = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var ids = items.Where(IsIssue).Select(ContentId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return histories;
        var nodes = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        var cursors = new Queue<(string Id, string Cursor)>();
        foreach (var batch in ids.Chunk(HistoryBatch))
        {
            var body = await Send(lease, HistoriesQuery, new Dictionary<string, object?> { ["ids"] = batch, ["after"] = null }, token);
            var read = body.GetProperty("nodes");
            if (read.ValueKind != JsonValueKind.Array || read.GetArrayLength() != batch.Length)
                throw new InvalidOperationException("Issue の履歴を全部読めませんでした。");
            for (var n = 0; n < batch.Length; n++)
            {
                var (id, page, total, cursor) = HistoryPage(read[n], batch[n]);
                nodes[id] = page;
                totals[id] = total;
                if (cursor is not null) cursors.Enqueue((id, cursor));
            }
        }
        while (cursors.Count > 0)
        {
            var (id, after) = cursors.Dequeue();
            var body = await Send(lease, HistoriesQuery, new Dictionary<string, object?> { ["ids"] = new[] { id }, ["after"] = after }, token);
            var read = body.GetProperty("nodes");
            if (read.ValueKind != JsonValueKind.Array || read.GetArrayLength() != 1)
                throw new InvalidOperationException("Issue の履歴を全部読めませんでした: " + id);
            var (readId, page, total, cursor) = HistoryPage(read[0], id);
            if (readId != id) throw new InvalidOperationException("別の Issue の履歴が返されました: " + id);
            if (total != totals[id]) throw new InvalidOperationException("読み込み中に Issue の履歴が変わりました: " + id);
            nodes[id].AddRange(page);
            if (nodes[id].Count > totals[id]) throw new InvalidOperationException("Issue の履歴を全部読めませんでした: " + id);
            if (cursor is not null) cursors.Enqueue((id, cursor));
        }
        foreach (var (id, page) in nodes)
        {
            if (page.Count != totals[id]) throw new InvalidOperationException("Issue の履歴を全部読めませんでした: " + id);
            histories[id] = new JsonObject
            {
                ["totalCount"] = page.Count,
                ["nodes"] = new JsonArray(page.Select(eventNode => (JsonNode)JsonNode.Parse(eventNode.GetRawText())!).ToArray()),
            };
        }
        return histories;
    }

    private static (string Id, List<JsonElement> Page, int Total, string? Cursor) HistoryPage(JsonElement node, string expected)
    {
        if (node.ValueKind != JsonValueKind.Object || node.GetProperty("__typename").GetString() != "Issue"
            || node.GetProperty("id").GetString() != expected)
            throw new InvalidOperationException("Issue の履歴を読めませんでした: " + expected);
        var timeline = node.GetProperty("timelineItems");
        var total = timeline.GetProperty("totalCount").GetInt32();
        var page = timeline.GetProperty("nodes").EnumerateArray().Select(e => e.Clone()).ToList();
        var info = timeline.GetProperty("pageInfo");
        var cursor = info.GetProperty("hasNextPage").GetBoolean()
            ? info.GetProperty("endCursor").GetString() ?? throw new InvalidOperationException("Issue の履歴の続きを取得できませんでした: " + expected)
            : null;
        return (expected, page, total, cursor);
    }

    // The history read runs after the item pages, so it is merged into the item JSON the builder already consumes. An
    // Issue without events is left unchanged, so a quiet history keeps its item line byte-identical to the day before.
    private static IEnumerable<JsonElement> WithHistories(IReadOnlyList<JsonElement> items, IReadOnlyDictionary<string, JsonObject> histories)
    {
        foreach (var item in items)
        {
            if (ContentId(item) is not { } id || !histories.TryGetValue(id, out var history) || history["totalCount"]!.GetValue<int>() == 0)
            {
                yield return item;
                continue;
            }
            var node = JsonNode.Parse(item.GetRawText())!.AsObject();
            node["content"]!.AsObject()["timelineItems"] = history.DeepClone();
            yield return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
        }
    }

    private static bool IsIssue(JsonElement item)
        => item.GetProperty("type").GetString() == "ISSUE" && item.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Object && content.TryGetProperty("__typename", out var type)
            && type.ValueKind == JsonValueKind.String && type.GetString() == "Issue";

    private static string? ContentId(JsonElement item)
        => item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    internal static RawSnapshot Build(JsonElement project, IEnumerable<JsonElement> items, DateTimeOffset readAt, DateOnly date, string timeZone, int? totalCount = null)
    {
        items = items.ToArray();
        var fields = project.GetProperty("fields");
        var fieldNodes = fields.GetProperty("nodes").EnumerateArray().Where(f => f.TryGetProperty("id", out _)).ToArray();
        if (fieldNodes.Length != fields.GetProperty("totalCount").GetInt32()) throw new InvalidOperationException("プロジェクトの列を全部読めませんでした。");
        var definitions = fieldNodes.Select(Field).OrderBy(f => (string)f["name"]!, StringComparer.Ordinal).ThenBy(f => (string)f["id"]!, StringComparer.Ordinal).ToArray();
        var valueFields = fieldNodes.Where(f => ValueTypes.Contains(f.GetProperty("dataType").GetString()!))
            .ToDictionary(f => f.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var owner = project.GetProperty("owner").GetProperty("login").GetString()!;
        var number = project.GetProperty("number").GetInt32();
        var projectLine = Line(new JsonObject
        {
            ["kind"] = "project",
            ["schema"] = Schema,
            ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["readAt"] = readAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["timeZone"] = timeZone,
            ["project"] = new JsonObject
            {
                ["id"] = project.GetProperty("id").GetString(),
                ["owner"] = owner,
                ["ownerType"] = project.GetProperty("owner").GetProperty("__typename").GetString(),
                ["number"] = number,
                ["title"] = project.GetProperty("title").GetString(),
                ["url"] = project.GetProperty("url").GetString(),
                ["closed"] = project.GetProperty("closed").GetBoolean(),
            },
            ["fields"] = new JsonArray(definitions.Cast<JsonNode>().ToArray()),
            ["itemsRead"] = items.Count(),
            ["itemsTotalCount"] = totalCount,
        });
        var lines = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var projectId = project.GetProperty("id").GetString()!;
        foreach (var item in items) lines[item.GetProperty("id").GetString()!] = Line(Item(item, valueFields, projectId));
        return new(date, owner, number, projectLine, lines);
    }

    private static JsonObject Field(JsonElement f)
    {
        var o = new JsonObject
        {
            ["id"] = f.GetProperty("id").GetString(),
            ["name"] = f.GetProperty("name").GetString(),
            ["dataType"] = f.GetProperty("dataType").GetString(),
            ["isIssueField"] = f.TryGetProperty("isIssueField", out var issue) && issue.ValueKind == JsonValueKind.True,
        };
        var options = f.TryGetProperty("issueField", out var issueField) && issueField.ValueKind == JsonValueKind.Object && issueField.TryGetProperty("options", out var o1) ? o1
            : f.TryGetProperty("options", out var o2) ? o2 : default;
        if (options.ValueKind == JsonValueKind.Array)
            o["options"] = new JsonArray(options.EnumerateArray().Select(x => (JsonNode)new JsonObject { ["id"] = x.GetProperty("id").GetString(), ["name"] = x.GetProperty("name").GetString() }).ToArray());
        if (f.TryGetProperty("configuration", out var configuration) && configuration.ValueKind == JsonValueKind.Object)
        {
            JsonArray Iterations(string name) => new(configuration.GetProperty(name).EnumerateArray().Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.GetProperty("id").GetString(), ["title"] = i.GetProperty("title").GetString(),
                ["startDate"] = i.GetProperty("startDate").GetString(), ["duration"] = i.GetProperty("duration").GetInt32(),
            }).ToArray());
            o["iterations"] = Iterations("iterations");
            o["completedIterations"] = Iterations("completedIterations");
            o["duration"] = configuration.GetProperty("duration").GetInt32();
        }
        return o;
    }

    private static JsonObject Item(JsonElement item, IReadOnlyDictionary<string, JsonElement> valueFields, string projectId)
    {
        var o = new JsonObject
        {
            ["kind"] = "item",
            ["id"] = item.GetProperty("id").GetString(),
            ["type"] = item.GetProperty("type").GetString(),
            ["archived"] = item.GetProperty("isArchived").GetBoolean(),
            ["addedAt"] = item.GetProperty("createdAt").GetString(),
        };
        if (item.GetProperty("content") is { ValueKind: JsonValueKind.Object } content)
        {
            o["content"] = Content(content, projectId);
            if (content.TryGetProperty("__typename", out var contentType) && contentType.GetString() == "Issue"
                && Timeline(content, projectId) is { Count: > 0 } events) o["events"] = events;
        }
        var values = item.GetProperty("fieldValues");
        var nodes = values.GetProperty("nodes").EnumerateArray().ToArray();
        if (nodes.Length != values.GetProperty("totalCount").GetInt32()) throw new InvalidOperationException("アイテムの列の値を全部読めませんでした: " + o["id"]);
        var fields = new JsonObject();
        foreach (var value in nodes)
        {
            if (!value.TryGetProperty("field", out var field) || !field.TryGetProperty("id", out var id) || !valueFields.TryGetValue(id.GetString()!, out var definition)) continue;
            var name = definition.GetProperty("name").GetString()!;
            JsonNode? v = value.GetProperty("__typename").GetString() switch
            {
                "ProjectV2ItemFieldTextValue" => value.GetProperty("text").GetString(),
                "ProjectV2ItemFieldNumberValue" => value.GetProperty("number").GetDecimal(),
                "ProjectV2ItemFieldDateValue" => value.GetProperty("date").GetString(),
                "ProjectV2ItemFieldSingleSelectValue" => new JsonObject { ["name"] = value.GetProperty("name").GetString(), ["optionId"] = value.GetProperty("optionId").GetString() },
                "ProjectV2ItemFieldIterationValue" => new JsonObject
                {
                    ["title"] = value.GetProperty("title").GetString(), ["iterationId"] = value.GetProperty("iterationId").GetString(),
                    ["startDate"] = value.GetProperty("startDate").GetString(), ["duration"] = value.GetProperty("duration").GetInt32(),
                },
                "ProjectV2ItemIssueFieldValue" => IssueFieldValue(value.GetProperty("issueFieldValue"), definition),
                _ => null,
            };
            if (v is not null) fields[name] = v;
        }
        o["fields"] = fields;
        return o;
    }

    private static JsonNode? IssueFieldValue(JsonElement value, JsonElement definition)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (value.TryGetProperty("optionId", out var optionId))
        {
            var options = definition.TryGetProperty("issueField", out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty("options", out var o) ? o : default;
            var name = options.ValueKind == JsonValueKind.Array
                ? options.EnumerateArray().FirstOrDefault(x => x.GetProperty("id").GetString() == optionId.GetString()) is { ValueKind: JsonValueKind.Object } match ? match.GetProperty("name").GetString() : null
                : null;
            return new JsonObject { ["name"] = name, ["optionId"] = optionId.GetString() };
        }
        return value.TryGetProperty("value", out var v) ? JsonNode.Parse(v.GetRawText()) : null;
    }

    private static JsonObject Content(JsonElement c, string projectId)
    {
        var type = c.GetProperty("__typename").GetString()!;
        var o = new JsonObject { ["type"] = type, ["id"] = c.GetProperty("id").GetString(), ["title"] = c.GetProperty("title").GetString(), ["createdAt"] = c.GetProperty("createdAt").GetString() };
        if (type == "DraftIssue") return o;
        string Reference(JsonElement r) => r.GetProperty("repository").GetProperty("nameWithOwner").GetString() + "#" + r.GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture);
        JsonArray Names(string connection, Func<JsonElement, string> name)
        {
            var list = c.GetProperty(connection);
            var nodes = list.GetProperty("nodes").EnumerateArray().ToArray();
            if (nodes.Length != list.GetProperty("totalCount").GetInt32()) throw new InvalidOperationException($"{connection} を全部読めませんでした: " + Reference(c));
            return new(nodes.Select(name).Order(StringComparer.Ordinal).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        }
        o["repository"] = c.GetProperty("repository").GetProperty("nameWithOwner").GetString();
        o["number"] = c.GetProperty("number").GetInt32();
        o["url"] = c.GetProperty("url").GetString();
        o["state"] = c.GetProperty("state").GetString();
        o["closedAt"] = c.GetProperty("closedAt").GetString();
        o["assignees"] = Names("assignees", n => n.GetProperty("login").GetString()!);
        o["labels"] = Names("labels", n => n.GetProperty("name").GetString()!);
        o["milestone"] = c.GetProperty("milestone") is { ValueKind: JsonValueKind.Object } m
            ? new JsonObject { ["title"] = m.GetProperty("title").GetString(), ["dueOn"] = m.GetProperty("dueOn").GetString() } : null;
        if (type == "PullRequest")
        {
            o["mergedAt"] = c.GetProperty("mergedAt").GetString();
            return o;
        }
        o["stateReason"] = c.GetProperty("stateReason").GetString();
        o["issueType"] = c.GetProperty("issueType") is { ValueKind: JsonValueKind.Object } t ? t.GetProperty("name").GetString() : null;
        o["parent"] = c.GetProperty("parent") is { ValueKind: JsonValueKind.Object } p ? Reference(p) : null;
        o["blockedBy"] = Names("blockedBy", Reference);
        return o;
    }

    // #308: GitHub's timeline nodes become the record's own compact events, so the file does not depend on GitHub's
    // GraphQL shape. Only Status changes in this Project belong to this Project's item line; closes and reopens are
    // the Issue's own facts and stay the same in every Project that holds it. A timeline that does not add up or
    // reports another page fails the run like any other incomplete read.
    private static JsonArray Timeline(JsonElement content, string projectId)
    {
        var events = new JsonArray();
        if (!content.TryGetProperty("timelineItems", out var timeline) || timeline.ValueKind != JsonValueKind.Object) return events;
        var nodes = timeline.TryGetProperty("nodes", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToArray() : [];
        if (!timeline.TryGetProperty("totalCount", out var total) || total.ValueKind != JsonValueKind.Number || nodes.Length != total.GetInt32())
            throw new InvalidOperationException("Issue の履歴を全部読めませんでした。");
        if (timeline.TryGetProperty("pageInfo", out var page) && page.ValueKind == JsonValueKind.Object
            && page.TryGetProperty("hasNextPage", out var next) && next.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("Issue の履歴を全部読めませんでした。");
        var mapped = new List<(DateTimeOffset At, JsonObject Event)>(nodes.Length);
        foreach (var node in nodes)
        {
            if (node.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Issue の履歴を読めませんでした。");
            var at = Stamp(node, "createdAt") ?? throw new InvalidOperationException("Issue の履歴の時刻を読めませんでした。");
            var (actor, automated) = Actor(node);
            switch (node.GetProperty("__typename").GetString())
            {
                case "ProjectV2ItemStatusChangedEvent":
                    if (node.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object
                        && project.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        if (id.GetString() != projectId) continue;
                        mapped.Add((at, Event("status", at, actor, automated, new JsonObject
                        {
                            ["from"] = Optional(node, "previousStatus"),
                            ["to"] = Optional(node, "status"),
                        })));
                        break;
                    }
                    throw new InvalidOperationException("Issue の履歴のプロジェクトを読めませんでした。");
                case "ClosedEvent":
                    mapped.Add((at, Event("closed", at, actor, automated, new JsonObject { ["stateReason"] = Optional(node, "stateReason") })));
                    break;
                case "ReopenedEvent":
                    mapped.Add((at, Event("reopened", at, actor, automated, new JsonObject())));
                    break;
                default:
                    throw new InvalidOperationException("Issue の履歴に知らないイベントがあります。");
            }
        }
        foreach (var (_, change) in mapped.OrderBy(pair => pair.At)) events.Add(change);
        return events;
    }

    private static JsonObject Event(string kind, DateTimeOffset at, string? actor, bool automated, JsonObject fields)
    {
        var change = new JsonObject
        {
            ["kind"] = kind,
            ["at"] = at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["actor"] = actor,
            ["automated"] = automated,
        };
        foreach (var (name, value) in fields) change[name] = value?.DeepClone();
        return change;
    }

    // GitHub reports Project automation as a bot or app actor; the site names that 「自動（Project のワークフロー）」.
    private static (string? Actor, bool Automated) Actor(JsonElement node)
    {
        if (!node.TryGetProperty("actor", out var actor) || actor.ValueKind != JsonValueKind.Object) return (null, false);
        var login = Optional(actor, "login");
        var type = Optional(actor, "__typename");
        return (login, type is "Bot" or "App" || login == "github-project-automation");
    }

    private static string? Optional(JsonElement node, string name)
        => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text : null;

    private static DateTimeOffset? Stamp(JsonElement node, string name)
        => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    // One line per record, keys in ordinal order at every level, so the same values always give the same line and
    // Git stores a day's changes as a few changed lines.
    internal static string Line(JsonNode node) => JsonSerializer.Serialize(Sorted(node), Compact);
    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };
}

// #272 D4: data/<owner>/<project>/<yyyy-MM>/full-<date>.jsonl on the first recorded day of a month, then
// changes-<date>.jsonl with the Project line and only the items whose line changed (removed items as one line each).
// Any day is the month's full file plus that month's changes files up to the day.
internal static class RawStore
{
    internal static string Folder(string root, string owner, int number, DateOnly date)
        => Path.Combine(root, owner, number.ToString(CultureInfo.InvariantCulture), date.ToString("yyyy-MM", CultureInfo.InvariantCulture));
    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly? DateOf(string file)
        => Path.GetFileNameWithoutExtension(file).Split('-', 2) is [_, var day] && DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    // Returns the written file and how many item lines it holds.
    internal static (string Path, int Items, int Removed) Write(string root, RawSnapshot snapshot)
    {
        var folder = Folder(root, snapshot.Owner, snapshot.Number, snapshot.Date);
        Directory.CreateDirectory(folder);
        var existing = Directory.EnumerateFiles(folder, "*.jsonl").Where(f => DateOf(f) is not null).ToArray();
        if (existing.Any(f => DateOf(f) > snapshot.Date)) throw new InvalidOperationException($"{Day(snapshot.Date)} より後の記録があるため書けません: {folder}");
        var full = existing.Where(f => Path.GetFileName(f).StartsWith("full-", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
        string path;
        IEnumerable<string> lines;
        int count, removed = 0;
        if (full is null || DateOf(full) == snapshot.Date)
        {
            path = Path.Combine(folder, "full-" + Day(snapshot.Date) + ".jsonl");
            lines = snapshot.Items.Values;
            count = snapshot.Items.Count;
        }
        else
        {
            var before = Read(folder, snapshot.Date.AddDays(-1));
            var changed = snapshot.Items.Where(p => !before.TryGetValue(p.Key, out var line) || line != p.Value).Select(p => p.Value).ToArray();
            var gone = before.Keys.Where(id => !snapshot.Items.ContainsKey(id)).Select(id => RawRecord.Line(new JsonObject { ["kind"] = "removed", ["id"] = id })).ToArray();
            path = Path.Combine(folder, "changes-" + Day(snapshot.Date) + ".jsonl");
            lines = changed.Concat(gone);
            count = changed.Length;
            removed = gone.Length;
        }
        var text = string.Concat(new[] { snapshot.ProjectLine }.Concat(lines).Select(l => l + "\n"));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new System.Text.UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return (path, count, removed);
    }

    // Every recorded day of a Project, across its month folders. The site draws a week from its last recorded day,
    // so it needs the days themselves, not a calendar range: a day with no file was never read and stays missing.
    internal static SortedSet<DateOnly> Days(string root, string owner, int number)
    {
        var days = new SortedSet<DateOnly>();
        var project = Path.Combine(root, owner, number.ToString(CultureInfo.InvariantCulture));
        if (!Directory.Exists(project)) return days;
        foreach (var file in Directory.EnumerateFiles(project, "*.jsonl", SearchOption.AllDirectories))
            if (DateOf(file) is { } day) days.Add(day);
        return days;
    }

    // The Project line is line 1 of a file and holds the field definitions the whole record needs; the latest file
    // up to the day is the one whose definitions describe that day.
    internal static string ProjectLine(string root, string owner, int number, DateOnly through)
    {
        var day = Days(root, owner, number).Where(d => d <= through).DefaultIfEmpty().Max();
        if (day == default) throw new InvalidOperationException($"{through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} までの記録がありません: {owner}/{number}");
        var folder = Folder(root, owner, number, day);
        var file = Directory.EnumerateFiles(folder, "*.jsonl").Single(f => DateOf(f) == day);
        return File.ReadLines(file).First();
    }

    // The item lines as of the end of a day, from one month folder.
    internal static SortedDictionary<string, string> Read(string folder, DateOnly through)
    {
        var items = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.jsonl").Where(f => DateOf(f) is { } d && d <= through)
                .OrderBy(f => DateOf(f)).ThenBy(f => Path.GetFileName(f).StartsWith("full-", StringComparison.Ordinal) ? 0 : 1).ToArray()
            : [];
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file).Skip(1))
            {
                if (line.Length == 0) continue;
                using var json = JsonDocument.Parse(line);
                var id = json.RootElement.GetProperty("id").GetString()!;
                if (json.RootElement.GetProperty("kind").GetString() == "removed") items.Remove(id);
                else items[id] = line;
            }
        }
        return items;
    }
}
