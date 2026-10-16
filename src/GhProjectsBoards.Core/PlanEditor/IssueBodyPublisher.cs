using GhProjectsBoards.App.GitHub;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GhProjectsBoards.Core.PlanEditor;

internal static class IssueBodyText
{
    private static readonly Regex Marker = new(@"<!-- ghpb-plan:.*?-->", RegexOptions.Singleline);
    internal static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    internal static string EditorText(string body)
    {
        var text = Normalize(body);
        return Marker.IsMatch(text) ? Regex.Replace(Marker.Replace(text, ""), @"\A(?:[\t ]*\n)+|(?:\n[\t ]*)+\z", "") : text;
    }
    internal static string BodyToSend(string body, string draft)
    {
        var markers = string.Join("\n\n", Marker.Matches(Normalize(body)).Select(m => m.Value));
        var text = Normalize(draft);
        return markers.Length == 0 ? text : string.IsNullOrWhiteSpace(text) ? markers : text + "\n\n" + markers;
    }
    internal static bool Changed(string body, string draft) => EditorText(body) != Normalize(draft);
}
internal enum IssueBodyPublishOutcome { Published, AlreadyPublished, Conflict, Failed }
internal sealed record IssueBodyPublish(IssueBodyPublishOutcome Outcome, ApiResult Result, string? RemoteBody = null, bool UpdateDenied = false)
{
    internal string FailureReason => UpdateDenied ? "この Issue の本文を編集する権限がありません" : new IssueDetailRead(null, Result).FailureReason;
}
internal sealed class IssueBodyPublisher(GhConnectionService service)
{
    internal async Task<IssueBodyPublish> Publish(ConnectionContext context, string nodeId, string startingBody, string draftEditorText, CancellationToken token = default)
    {
        var sent = IssueBodyText.BodyToSend(startingBody, draftEditorText);
        var read = await service.SendAsync(context, ApiRequest.GraphQl(
            "query PlanIssueBody($id:ID!){node(id:$id){... on Issue{body viewerCanUpdate}}}", new { id = nodeId }), token).ConfigureAwait(false);
        if (!read.IsSuccess) return Failure(read);
        try {
            var node = read.Data!.Value.GetProperty("data").GetProperty("node");
            if (node.ValueKind == JsonValueKind.Null) return Failure(new(ApiOutcome.Failed, FailureKind.NotFoundOrInaccessible));
            var remote = node.GetProperty("body").GetString() ?? throw new JsonException();
            var allowed = node.GetProperty("viewerCanUpdate").GetBoolean();
            if (IssueBodyText.Normalize(remote) == sent) return new(IssueBodyPublishOutcome.AlreadyPublished, read);
            if (IssueBodyText.Normalize(remote) != IssueBodyText.Normalize(startingBody)) return new(IssueBodyPublishOutcome.Conflict, read, remote);
            if (!allowed) return new(IssueBodyPublishOutcome.Failed, new(ApiOutcome.Failed, FailureKind.PermissionDenied), UpdateDenied: true);
            // SendAsync holds its authenticated gate through the bound-viewer recheck and this single write.
            var write = await service.SendAsync(context, ApiRequest.GraphQl(
                "mutation PlanIssueBodyPublish($input:UpdateIssueInput!){updateIssue(input:$input){issue{body}}}",
                new { input = new { id = nodeId, body = sent } }), token, requiredScope: "repo").ConfigureAwait(false);
            if (!write.IsSuccess) return Failure(write);
            var returned = write.Data!.Value.GetProperty("data").GetProperty("updateIssue").GetProperty("issue").GetProperty("body").GetString();
            return returned is not null && IssueBodyText.Normalize(returned) == sent
                ? new(IssueBodyPublishOutcome.Published, write) : Failure(new(ApiOutcome.Failed, FailureKind.InvalidResponse));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) {
            return Failure(new(ApiOutcome.Failed, FailureKind.InvalidResponse));
        }
    }
    private static IssueBodyPublish Failure(ApiResult result) => new(IssueBodyPublishOutcome.Failed,
        result.Failure == FailureKind.GraphQl && result.GraphQlErrors.Count > 0 && result.GraphQlErrors.All(c => c is "NOT_FOUND" or "FORBIDDEN")
            ? new(ApiOutcome.Failed, FailureKind.NotFoundOrInaccessible) : result);
}
