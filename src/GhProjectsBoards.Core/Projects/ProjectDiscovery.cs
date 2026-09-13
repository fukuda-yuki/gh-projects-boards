using System.Text.Json;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.Projects;

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
    private const string Repository = "id nameWithOwner owner { id }";

    public async Task<IReadOnlyList<OwnerChoice>> OwnersAsync(ConnectionContext context, CancellationToken token)
    {
        var nodes = await Pages(context, "query RegistrationOwners($after:String) { viewer { organizations(first:100,after:$after) { nodes { login } " + PageInfo + " } } }",
            new(), ["viewer", "organizations"], token);
        return new[] { new OwnerChoice(context.Login, "User") }.Concat(nodes.Select(n => new OwnerChoice(Text(n, "login"), "Organization"))).ToArray();
    }
    public async Task<IReadOnlyList<RepositoryReadModel>> RepositoriesAsync(ConnectionContext context, string owner, CancellationToken token)
    {
        var nodes = await Pages(context, "query RegistrationRepositories($owner:String!,$after:String) { repositoryOwner(login:$owner) { repositories(first:100,after:$after) { nodes { " + Repository + " } " + PageInfo + " } } }",
            new() { ["owner"] = owner }, ["repositoryOwner", "repositories"], token);
        return nodes.Select(n => ReadRepository(context, n)).ToArray();
    }
    public async Task<IReadOnlyList<ProjectChoice>> ProjectsAsync(ConnectionContext context, string owner, string? repository, string search, CancellationToken token)
    {
        var variables = new Dictionary<string, object?> { ["owner"] = owner, ["search"] = search };
        string query;
        string[] path;
        if (repository is not null)
        {
            variables["name"] = repository;
            query = "query RegistrationProjects($owner:String!,$name:String!,$search:String,$after:String) { repository(owner:$owner,name:$name) { projectsV2(first:100,after:$after,query:$search,minPermissionLevel:READ) { nodes { " + Identity + " } " + PageInfo + " } } }";
            path = ["repository", "projectsV2"];
        }
        else
        {
            query = "query RegistrationProjects($owner:String!,$search:String,$after:String) { repositoryOwner(login:$owner) { ... on User { projectsV2(first:100,after:$after,query:$search,minPermissionLevel:READ) { nodes { " + Identity + " } " + PageInfo + " } } ... on Organization { projectsV2(first:100,after:$after,query:$search,minPermissionLevel:READ) { nodes { " + Identity + " } " + PageInfo + " } } } }";
            path = ["repositoryOwner", "projectsV2"];
        }
        var nodes = await Pages(context, query, variables, path, token);
        var results = nodes.Select(n => Choice(context, n)).ToArray();
        if (results.Select(p => p.Id).Distinct().Count() != results.Length) throw new DiscoveryException(FailureKind.InvalidResponse);
        return results;
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
    public async Task<IReadOnlyList<RepositoryReadModel>> LinksAsync(ConnectionContext context, ScopedId project, CancellationToken token)
    {
        if (project.Scope != ConnectionScope.From(context)) throw new DiscoveryException(FailureKind.IdentityChanged);
        var nodes = await Pages(context, "query RegistrationLinks($id:ID!,$after:String) { node(id:$id) { ... on ProjectV2 { repositories(first:100,after:$after) { nodes { " + Repository + " } " + PageInfo + " } } } }",
            new() { ["id"] = project.NodeId }, ["node", "repositories"], token);
        return nodes.Select(n => ReadRepository(context, n)).ToArray();
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
    private static RepositoryReadModel ReadRepository(ConnectionContext context, JsonElement n)
    {
        var scope = ConnectionScope.From(context);
        return new(new(scope, Text(n, "id")), new(scope, Text(At(n, "owner"), "id")), Text(n, "nameWithOwner"));
    }
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
