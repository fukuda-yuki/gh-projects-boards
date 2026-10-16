using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.PlanEditor;

internal sealed record IssueDetailComment(string Author, DateTimeOffset CreatedAt, string Url, string Body, string BodyHtml);
internal sealed record IssueDetail(int Number, string Repository, string Title, string Url, string State, string? StateReason,
    string Body, string BodyHtml, DateTimeOffset UpdatedAt, int CommentCount, ImmutableArray<IssueDetailComment> Comments)
{
    internal bool ViewerCanUpdate { get; init; }
    internal string Reference => $"{Repository}#{Number}";
}
internal sealed record IssueDetailRead(IssueDetail? Issue, ApiResult Result)
{
    internal string FailureReason => Result.Failure is FailureKind.NotFoundOrInaccessible or FailureKind.PermissionDenied
        ? "Issue が見つからないか、アクセスできません" : Result.FailureReason();
}

internal sealed class IssueDetailReader(GhConnectionService service)
{
    private const string Query = "query PlanIssueDetail($id:ID!){node(id:$id){... on Issue{number title url state stateReason body bodyHTML updatedAt viewerCanUpdate repository{nameWithOwner} comments(last:5){totalCount nodes{author{login} createdAt url body bodyHTML}}}}}";

    internal async Task<IssueDetailRead> Read(ConnectionContext context, string nodeId, CancellationToken token = default)
    {
        var response = await service.SendAsync(context, ApiRequest.GraphQl(Query, new { id = nodeId }), token).ConfigureAwait(false);
        if (token.IsCancellationRequested) return new(null, new(ApiOutcome.Cancelled, FailureKind.Cancelled));
        if (!response.IsSuccess) {
            if (response.Failure == FailureKind.GraphQl && response.GraphQlErrors.Count > 0 &&
                response.GraphQlErrors.All(code => code is "NOT_FOUND" or "FORBIDDEN"))
                return new(null, new(ApiOutcome.Failed, FailureKind.NotFoundOrInaccessible));
            return new(null, response);
        }
        try {
            var node = response.Data!.Value.GetProperty("data").GetProperty("node");
            if (node.ValueKind == JsonValueKind.Null) return new(null, new(ApiOutcome.Failed, FailureKind.NotFoundOrInaccessible));
            var connection = node.GetProperty("comments");
            var comments = connection.GetProperty("nodes").EnumerateArray().Select(c => new IssueDetailComment(
                c.GetProperty("author").ValueKind == JsonValueKind.Null ? "ghost" : Text(c.GetProperty("author"), "login"),
                c.GetProperty("createdAt").GetDateTimeOffset(), Text(c, "url"), Text(c, "body"), Text(c, "bodyHTML")))
                .OrderBy(c => c.CreatedAt).ToImmutableArray();
            var detail = new IssueDetail(node.GetProperty("number").GetInt32(), Text(node.GetProperty("repository"), "nameWithOwner"),
                Text(node, "title"), Text(node, "url"), Text(node, "state"), node.GetProperty("stateReason").GetString(),
                Text(node, "body"), Text(node, "bodyHTML"), node.GetProperty("updatedAt").GetDateTimeOffset(), connection.GetProperty("totalCount").GetInt32(), comments) { ViewerCanUpdate = node.GetProperty("viewerCanUpdate").GetBoolean() };
            if (detail.Number <= 0 || detail.State is not ("OPEN" or "CLOSED") || detail.CommentCount < comments.Length || comments.Length > 5)
                return new(null, new(ApiOutcome.Failed, FailureKind.InvalidResponse));
            return new(detail, response);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) {
            return new(null, new(ApiOutcome.Failed, FailureKind.InvalidResponse));
        }
    }
    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()
        ?? throw new JsonException("Missing Issue text.");
}
