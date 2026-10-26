using System.Text.Json;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.Projects;

// #308: the Issue timeline facts behind 着手日 and 完了日. GitHub keeps every Status change of a Project item
// and every close and reopen of an Issue as immutable events; this reader fetches them for a bounded set of
// Issues and returns them as read observations. A refresh asks only for rows whose Project item or Issue
// changed since the stored read, so a version-scale refresh does not re-read every timeline.
internal sealed record IssueHistoryStatusChange(DateTimeOffset At, string Actor, bool Automated, string? Previous, string New);
internal sealed record IssueHistoryClose(DateTimeOffset At, string Actor, bool Automated, string? StateReason);
internal sealed record IssueHistoryReopen(DateTimeOffset At, string Actor, bool Automated);
internal sealed record IssueHistoryRead(string IssueId, DateTimeOffset UpdatedAt,
    IReadOnlyList<IssueHistoryStatusChange> StatusChanges, IReadOnlyList<IssueHistoryClose> Closes,
    IReadOnlyList<IssueHistoryReopen> Reopens);
internal sealed record ProjectHistoryResult(ProjectReadOutcome Outcome, IReadOnlyDictionary<string, IssueHistoryRead>? History,
    IReadOnlyList<ReadProblem> Problems);

internal sealed class ProjectHistoryReader(GhConnectionService service, GhConnectionService.OperationLease? lease = null)
{
    private const int BatchSize = 25;
    private const int Concurrency = 6;
    private const int MaxPages = 20;
    private const string Events = "timelineItems(first: 100, itemTypes: [PROJECT_V2_ITEM_STATUS_CHANGED_EVENT, CLOSED_EVENT, REOPENED_EVENT])";
    private const string EventSelection = """
        nodes {
          __typename
          ... on ProjectV2ItemStatusChangedEvent { createdAt actor { __typename login } previousStatus status project { id } }
          ... on ClosedEvent { createdAt actor { __typename login } stateReason }
          ... on ReopenedEvent { createdAt actor { __typename login } }
        }
        """;
    private static readonly string Histories = "query PlanIssueHistories($ids:[ID!]!){nodes(ids:$ids){__typename ... on Issue{id updatedAt " + Events +
        " { totalCount pageInfo { hasNextPage endCursor } " + EventSelection + "}}}}";
    private static readonly string Page = "query PlanIssueHistoryPage($id:ID!,$after:String){node(id:$id){__typename ... on Issue{id " + Events +
        "(after:$after) { totalCount pageInfo { hasNextPage endCursor } " + EventSelection + "}}}}";

    public async Task<ProjectHistoryResult> ReadAsync(ConnectionContext context, ScopedId project, IReadOnlyList<string> issueIds,
        CancellationToken cancellationToken = default)
    {
        if (project.Scope != ConnectionScope.From(context) || string.IsNullOrWhiteSpace(project.NodeId))
            return new(ProjectReadOutcome.Failed, null, [new(ReadProblemKind.ScopeMismatch, "history", FailureKind.IdentityChanged)]);
        var ids = issueIds.Distinct(StringComparer.Ordinal).ToArray();
        var history = new Dictionary<string, IssueHistoryRead>(StringComparer.Ordinal);
        var problems = new List<ReadProblem>();
        ApiOutcome? interruption = null;
        var batches = new Queue<string[]>(ids.Chunk(BatchSize));
        var pending = new Queue<(string[] Ids, Task<ApiResult> Response)>();
        var stopped = false;
        try
        {
            while (!stopped && (batches.Count > 0 || pending.Count > 0))
            {
                while (batches.Count > 0 && pending.Count < Concurrency)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batch = batches.Dequeue();
                    pending.Enqueue((batch, SendAsync(context, Histories, new { ids = batch }, cancellationToken)));
                }
                var (current, response) = pending.Dequeue();
                var result = await response;
                if (!result.IsSuccess)
                {
                    stopped = true;
                    interruption = result.Outcome;
                    problems.Add(new(ReadProblemKind.Api, "history", result.Failure, result.Outcome, result.HttpStatus, result.RetryAfter));
                    continue;
                }
                var nodes = Property(Property(result.Data ?? default, "data"), "nodes");
                if (nodes.ValueKind != JsonValueKind.Array || nodes.GetArrayLength() != current.Length)
                    throw new ReadException(ReadProblemKind.IncompleteTraversal);
                for (var i = 0; i < current.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var node = nodes[i];
                    if (node.ValueKind == JsonValueKind.Null)
                    {
                        // The Issue disappeared between the item read and this request: concurrent change.
                        problems.Add(new(ReadProblemKind.ConcurrentChange, "history"));
                        continue;
                    }
                    if (Text(node, "__typename") != "Issue" || OptionalText(node, "id") != current[i])
                        throw new ReadException(ReadProblemKind.ScopeMismatch);
                    var issue = await ParseIssueAsync(context, project, node, cancellationToken);
                    if (!history.TryAdd(issue.IssueId, issue)) throw new ReadException(ReadProblemKind.DuplicateIdentity);
                }
            }
        }
        catch (ReadException error)
        {
            stopped = true;
            problems.Add(new(error.Kind, "history", FailureKind.InvalidResponse));
        }
        catch (OperationCanceledException)
        {
            interruption = ApiOutcome.Cancelled;
            problems.Add(new(ReadProblemKind.Api, "history", FailureKind.Cancelled, ApiOutcome.Cancelled));
        }
        var complete = !stopped && problems.Count == 0 && history.Count == ids.Length;
        var outcome = interruption switch
        {
            ApiOutcome.Cancelled => ProjectReadOutcome.Cancelled,
            ApiOutcome.TimedOut => ProjectReadOutcome.TimedOut,
            _ => complete ? ProjectReadOutcome.Complete : ProjectReadOutcome.Partial
        };
        return new(outcome, history, problems);
    }

    private async Task<IssueHistoryRead> ParseIssueAsync(ConnectionContext context, ScopedId project, JsonElement node,
        CancellationToken cancellationToken)
    {
        var issueId = Text(node, "id");
        var updated = At(node, "updatedAt");
        var connection = Property(node, "timelineItems");
        var total = NonnegativeInt(connection, "totalCount");
        var events = Property(connection, "nodes").EnumerateArray().ToList();
        var cursor = OptionalText(Property(connection, "pageInfo"), "endCursor");
        for (var page = 1; Boolean(Property(connection, "pageInfo"), "hasNextPage"); page++)
        {
            if (page >= MaxPages || cursor is null) throw new ReadException(ReadProblemKind.IncompleteTraversal);
            cancellationToken.ThrowIfCancellationRequested();
            var response = await SendAsync(context, Page, new { id = issueId, after = cursor }, cancellationToken);
            if (!response.IsSuccess) throw new ReadException(ReadProblemKind.Api);
            var next = Property(Property(response.Data ?? default, "data"), "node");
            if (next.ValueKind != JsonValueKind.Object || Text(next, "__typename") != "Issue" || OptionalText(next, "id") != issueId)
                throw new ReadException(ReadProblemKind.ConcurrentChange);
            connection = Property(next, "timelineItems");
            var pageTotal = NonnegativeInt(connection, "totalCount");
            if (pageTotal != total) throw new ReadException(ReadProblemKind.ConcurrentChange);
            events.AddRange(Property(connection, "nodes").EnumerateArray());
            if (events.Count > total) throw new ReadException(ReadProblemKind.IncompleteTraversal);
            cursor = OptionalText(Property(connection, "pageInfo"), "endCursor");
        }
        if (events.Count != total) throw new ReadException(ReadProblemKind.IncompleteTraversal);
        var status = new List<IssueHistoryStatusChange>();
        var closes = new List<IssueHistoryClose>();
        var reopens = new List<IssueHistoryReopen>();
        foreach (var item in events)
        {
            var type = Text(item, "__typename");
            var at = At(item, "createdAt");
            var (actor, automated) = Actor(item);
            switch (type)
            {
                case "ProjectV2ItemStatusChangedEvent":
                    var projectId = Property(item, "project");
                    // A Status change belongs to one Project: an Issue carried into the next version keeps both
                    // histories, and only this Project's changes can start this Project's task (R4).
                    if (Text(projectId, "id") != project.NodeId) continue;
                    status.Add(new(at, actor, automated, OptionalText(item, "previousStatus"), Text(item, "status", allowEmpty: false)));
                    break;
                case "ClosedEvent":
                    closes.Add(new(at, actor, automated, OptionalText(item, "stateReason")));
                    break;
                case "ReopenedEvent":
                    reopens.Add(new(at, actor, automated));
                    break;
                default:
                    throw new ReadException(ReadProblemKind.InvalidResponse);
            }
        }
        // GitHub returns the timeline in order; sorting keeps the stored observation independent of the page shape.
        status.Sort((a, b) => a.At.CompareTo(b.At));
        closes.Sort((a, b) => a.At.CompareTo(b.At));
        reopens.Sort((a, b) => a.At.CompareTo(b.At));
        return new(issueId, updated, status, closes, reopens);
    }

    private async Task<ApiResult> SendAsync(ConnectionContext context, string query, object variables, CancellationToken cancellationToken)
    {
        if (lease is not null)
        {
            var response = await lease.SendAsync(ApiRequest.GraphQl(query.Insert(query.LastIndexOf('}'), " viewer { databaseId } "), variables), cancellationToken);
            if (!response.IsSuccess) return response;
            if (response.Data is { } body && body.TryGetProperty("data", out var data) && data.TryGetProperty("viewer", out var viewer) &&
                viewer.TryGetProperty("databaseId", out var id) && id.TryGetInt64(out var actual) && actual == context.ViewerId)
                return response;
            context.Invalidate();
            return new(ApiOutcome.Failed, FailureKind.IdentityChanged);
        }
        return await service.SendAsync(context, ApiRequest.GraphQl(query, variables), cancellationToken);
    }

    // The actor of an event, and whether GitHub's Project automation made it. The timeline reports the
    // automation as a bot/app actor, which is what the sheet names 「自動（Project のワークフロー）」.
    private static (string Actor, bool Automated) Actor(JsonElement item)
    {
        var actor = Optional(item, "actor");
        if (actor.ValueKind != JsonValueKind.Object) return ("", false);
        var login = OptionalText(actor, "login") ?? "";
        var type = OptionalText(actor, "__typename");
        return (login, type is "Bot" or "App" || login == "github-project-automation");
    }

    private static DateTimeOffset At(JsonElement node, string name)
    {
        var value = Optional(node, name);
        if (value.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var at)) throw new ReadException();
        return at;
    }

    private sealed class ReadException(ReadProblemKind kind = ReadProblemKind.InvalidResponse) : Exception
    {
        public ReadProblemKind Kind { get; } = kind;
    }
    private static JsonElement Optional(JsonElement node, string name)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) ? value : default;
    private static JsonElement Property(JsonElement node, string name)
    {
        var value = Optional(node, name);
        return value.ValueKind != JsonValueKind.Undefined ? value : throw new ReadException();
    }
    private static string Text(JsonElement node, string name, bool allowEmpty = false)
    {
        var value = OptionalText(node, name);
        return value is not null && (allowEmpty || !string.IsNullOrWhiteSpace(value)) ? value : throw new ReadException();
    }
    private static string? OptionalText(JsonElement node, string name)
        => Optional(node, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static bool Boolean(JsonElement node, string name)
    {
        var value = Property(node, name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new ReadException();
    }
    private static int NonnegativeInt(JsonElement node, string name)
    {
        var value = Property(node, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0
            ? number : throw new ReadException();
    }
}
