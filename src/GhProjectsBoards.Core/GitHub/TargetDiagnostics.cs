using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GhProjectsBoards.App.GitHub;

internal sealed record TargetReport(bool? CanRead = null, bool? CanUpdate = null,
    string? RequiredWriteScope = null, bool? HasWriteScope = null, FailureKind Failure = FailureKind.None);

internal sealed class TargetDiagnostics(GhConnectionService service)
{
    public async Task<TargetReport> IssueAsync(ConnectionContext context, AuthenticationInfo authentication, string url, CancellationToken cancellationToken = default)
    {
        var match = MatchUrl(context, url, @"\A/([A-Za-z0-9_-]+)/([A-Za-z0-9_.-]+)/issues/([0-9]+)/?\z");
        if (match is null || !PositiveNumber(match.Groups[3].Value, out var number))
            return new TargetReport(Failure: FailureKind.InvalidInput);
        var request = ApiRequest.GraphQl("""
            query($owner: String!, $name: String!, $number: Int!) {
              repository(owner: $owner, name: $name) {
                isPrivate
                issue(number: $number) { id viewerCanUpdate }
              }
            }
            """, new { owner = match.Groups[1].Value, name = match.Groups[2].Value, number });
        var result = await service.SendAsync(context, request, cancellationToken);
        if (!result.IsSuccess) return Failure(result.Failure);
        var repositoryFailure = ObjectAt(result.Data, out var repository, "data", "repository");
        if (repositoryFailure != FailureKind.None) return Failure(repositoryFailure);
        var issueFailure = ObjectAt(repository, out var issue, "issue");
        if (issueFailure != FailureKind.None) return Failure(issueFailure);
        if (!BooleanAt(repository, "isPrivate", out var isPrivate) || !BooleanAt(issue, "viewerCanUpdate", out var canUpdate))
            return Failure(FailureKind.InvalidResponse);
        return new TargetReport(true, canUpdate, isPrivate ? "repo" : "repo / public_repo",
            authentication.Scopes is null ? null : authentication.Scopes.Contains("repo") || (!isPrivate && authentication.Scopes.Contains("public_repo")));
    }

    public async Task<TargetReport> ProjectAsync(ConnectionContext context, AuthenticationInfo authentication, string url, CancellationToken cancellationToken = default)
    {
        var match = MatchUrl(context, url, @"\A/(users|orgs)/([A-Za-z0-9_-]+)/projects/([0-9]+)(?:/views/[0-9]+)?/?\z");
        if (match is null || !PositiveNumber(match.Groups[3].Value, out var number))
            return new TargetReport(Failure: FailureKind.InvalidInput);
        var ownerType = match.Groups[1].Value == "users" ? "user" : "organization";
        var document = "query($owner: String!, $number: Int!) { " + ownerType
            + "(login: $owner) { projectV2(number: $number) { id viewerCanUpdate } } }";
        var result = await service.SendAsync(context, ApiRequest.GraphQl(document, new { owner = match.Groups[2].Value, number }), cancellationToken);
        if (!result.IsSuccess) return Failure(result.Failure);
        var projectFailure = ObjectAt(result.Data, out var project, "data", ownerType, "projectV2");
        if (projectFailure != FailureKind.None) return Failure(projectFailure);
        return BooleanAt(project, "viewerCanUpdate", out var canUpdate)
            ? new TargetReport(true, canUpdate, "project", authentication.HasScope("project"))
            : Failure(FailureKind.InvalidResponse);
    }

    private static TargetReport Failure(FailureKind failure)
        => new(failure is FailureKind.PermissionDenied or FailureKind.NotFoundOrInaccessible ? false : null, Failure: failure);

    private static Match? MatchUrl(ConnectionContext context, string value, string pattern)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Host != context.Host)
            return null;
        var match = Regex.Match(uri.AbsolutePath, pattern);
        return match.Success ? match : null;
    }

    private static bool PositiveNumber(string text, out int number)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

    private static FailureKind ObjectAt(JsonElement? root, out JsonElement value, params string[] names)
    {
        value = root ?? default;
        foreach (var name in names)
        {
            if (value.ValueKind == JsonValueKind.Null) return FailureKind.NotFoundOrInaccessible;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out value)) return FailureKind.InvalidResponse;
        }
        return value.ValueKind switch { JsonValueKind.Object => FailureKind.None, JsonValueKind.Null => FailureKind.NotFoundOrInaccessible, _ => FailureKind.InvalidResponse };
    }

    private static bool BooleanAt(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean();
        return true;
    }
}
