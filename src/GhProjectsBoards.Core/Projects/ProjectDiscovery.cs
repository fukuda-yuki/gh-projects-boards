using System.Text.Json;
using System.Collections.Immutable;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.Projects;

internal sealed record ProjectListing(ProjectChoice Choice, bool Closed, DateTimeOffset UpdatedAt, ImmutableArray<string> Repositories);

internal sealed record ProjectChoice(ScopedId Id, ScopedId OwnerId, string OwnerLogin, string OwnerType, int Number, string Url, string Title)
{
    public override string ToString() => $"{Title}  ({OwnerLogin} #{Number})";
}
internal sealed record OwnerChoice(string Login, string Type)
{
    public override string ToString() => Login;
}
internal sealed class DiscoveryException(FailureKind failure) : Exception("Project discovery failed")
{
    public FailureKind Failure { get; } = failure;
}

internal sealed class ProjectDiscovery(GhConnectionService service)
{
    private const string Identity = "id number url title owner { id __typename ... on User { login } ... on Organization { login } }";
    private const string PageInfo = "pageInfo { hasNextPage endCursor }";

    public async Task<IReadOnlyList<OwnerChoice>> OwnersAsync(ConnectionContext context, CancellationToken token)
    {
        var nodes = await Pages(context, "query RegistrationOwners($after:String) { viewer { organizations(first:100,after:$after) { nodes { login } " + PageInfo + " } } }",
            new(), ["viewer", "organizations"], token);
        return new[] { new OwnerChoice(context.Login, "User") }.Concat(nodes.Select(n => new OwnerChoice(Text(n, "login"), "Organization"))).ToArray();
    }
    public async Task<IReadOnlyList<ProjectListing>> ListingsAsync(ConnectionContext context, string owner, CancellationToken token)
    {
        var connection = "projectsV2(first:50,after:$after,minPermissionLevel:READ,orderBy:{field:UPDATED_AT, direction:DESC}) { nodes { " + Identity + " closed updatedAt repositories(first:100) { nodes { nameWithOwner } } } " + PageInfo + " }";
        var nodes = await Pages(context, "query RegistrationProjects($owner:String!,$after:String) { repositoryOwner(login:$owner) { ... on User { " + connection + " } ... on Organization { " + connection + " } } }",
            new() { ["owner"] = owner }, ["repositoryOwner", "projectsV2"], token);
        var results = nodes.Select(n => Listing(context, n)).ToArray();
        if (results.Select(p => p.Choice.Id).Distinct().Count() != results.Length) throw new DiscoveryException(FailureKind.InvalidResponse);
        return results;
    }
    private static ProjectListing Listing(ConnectionContext context, JsonElement node)
    {
        try {
            var closed = At(node, "closed");
            var updated = At(node, "updatedAt");
            var repositories = At(node, "repositories", "nodes");
            if (closed.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || updated.ValueKind != JsonValueKind.String ||
                !updated.TryGetDateTimeOffset(out var date) || repositories.ValueKind != JsonValueKind.Array)
                throw new DiscoveryException(FailureKind.InvalidResponse);
            // GitHub returns null for a linked repository the viewer cannot read; the Project itself stays listed.
            var names = repositories.EnumerateArray().Where(r => r.ValueKind != JsonValueKind.Null).Select(r => Text(r, "nameWithOwner")).ToImmutableArray();
            if (names.Any(n => n.Split('/') is not [var owner, var name] || string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name)))
                throw new DiscoveryException(FailureKind.InvalidResponse);
            return new(Choice(context, node), closed.GetBoolean(), date, names.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray());
        } catch (DiscoveryException) { throw new DiscoveryException(FailureKind.InvalidResponse); }
        catch (InvalidOperationException) { throw new DiscoveryException(FailureKind.InvalidResponse); }
        catch (FormatException) { throw new DiscoveryException(FailureKind.InvalidResponse); }
    }
    public async Task<ProjectChoice> ResolveAsync(ConnectionContext context, string url, CancellationToken token)
    {
        var match = TargetDiagnostics.MatchUrl(context, url, @"\A/(users|orgs)/([A-Za-z0-9_-]+)/projects/([0-9]+)(?:/views/[0-9]+)?/?\z");
        if (match is null || !TargetDiagnostics.PositiveNumber(match.Groups[3].Value, out var number)) throw new DiscoveryException(FailureKind.InvalidInput);
        var type = match.Groups[1].Value == "users" ? "user" : "organization";
        var data = await Send(context, "query RegistrationResolve($owner:String!,$number:Int!) { " + type + "(login:$owner) { projectV2(number:$number) { " + Identity + " } } }",
            new { owner = match.Groups[2].Value, number }, token);
        var choice = Choice(context, At(data, type, "projectV2"));
        if (choice.Number != number || !choice.OwnerLogin.Equals(match.Groups[2].Value, StringComparison.OrdinalIgnoreCase)
            || choice.OwnerType != (type == "user" ? "User" : "Organization")) throw new DiscoveryException(FailureKind.InvalidResponse);
        return choice;
    }
    private async Task<List<JsonElement>> Pages(ConnectionContext context, string query, Dictionary<string, object?> variables, string[] path, CancellationToken token)
    {
        var result = new List<JsonElement>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            token.ThrowIfCancellationRequested();
            variables["after"] = cursor;
            var connection = At(await Send(context, query, variables, token), path);
            var nodes = At(connection, "nodes");
            if (nodes.ValueKind != JsonValueKind.Array) throw new DiscoveryException(FailureKind.InvalidResponse);
            result.AddRange(nodes.EnumerateArray().Select(n => n.Clone()));
            var page = At(connection, "pageInfo");
            var next = At(page, "hasNextPage");
            if (next.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new DiscoveryException(FailureKind.InvalidResponse);
            if (!next.GetBoolean()) return result;
            cursor = Text(page, "endCursor");
            if (nodes.GetArrayLength() == 0 || !cursors.Add(cursor)) throw new DiscoveryException(FailureKind.InvalidResponse);
        } while (true);
    }
    private async Task<JsonElement> Send(ConnectionContext context, string query, object variables, CancellationToken token)
    {
        var result = await service.SendAsync(context, ApiRequest.GraphQl(query, variables), token);
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess) throw new DiscoveryException(result.Failure);
        return At(result.Data ?? default, "data");
    }
    private static JsonElement At(JsonElement value, params string[] path)
    {
        foreach (var name in path)
        {
            if (value.ValueKind == JsonValueKind.Null) throw new DiscoveryException(FailureKind.NotFoundOrInaccessible);
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out value)) throw new DiscoveryException(FailureKind.InvalidResponse);
        }
        if (value.ValueKind == JsonValueKind.Null) throw new DiscoveryException(FailureKind.NotFoundOrInaccessible);
        return value;
    }
    private static string Text(JsonElement node, string property) => At(node, property).ValueKind == JsonValueKind.String && At(node, property).GetString() is { Length: > 0 } text
        ? text : throw new DiscoveryException(FailureKind.InvalidResponse);
    private static ProjectChoice Choice(ConnectionContext context, JsonElement n)
    {
        var owner = At(n, "owner");
        var number = At(n, "number");
        var url = Text(n, "url");
        if (!number.TryGetInt32(out var value) || value <= 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Host != context.Host || Text(owner, "__typename") is not ("User" or "Organization"))
            throw new DiscoveryException(FailureKind.InvalidResponse);
        var scope = ConnectionScope.From(context);
        return new(new(scope, Text(n, "id")), new(scope, Text(owner, "id")), Text(owner, "login"), Text(owner, "__typename"), value, url, Text(n, "title"));
    }
}
