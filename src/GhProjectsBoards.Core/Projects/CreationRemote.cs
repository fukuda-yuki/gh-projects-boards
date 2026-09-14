using System.Text.Json;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.Projects;

internal sealed partial class ApplyRemote
{
    private const string IssueSelection = "__typename id number url title repository{id nameWithOwner}";
    public async Task<CreationRepository?> ResolveCreationRepositoryAsync(ScopedId project, string name, CancellationToken token)
    {
        if (project.Scope != ConnectionScope.From(context) || name.Split('/') is not [var owner, var repo]) return null;
        var check = await service.RecheckAsync(context, token);
        if (!check.IsConnected || check.Authentication?.Store != CredentialStore.Keyring
            || check.Authentication.HasScope("repo") != true || check.Authentication.HasScope("project") != true) return null;
        var result = await service.SendAsync(context, ApiRequest.GraphQl(
            "query CreationRepository($owner:String!,$name:String!){repository(owner:$owner,name:$name,followRenames:false){id nameWithOwner hasIssuesEnabled isArchived viewerCanCreateIssues}}",
            new { owner, name = repo }), token);
        if (!result.IsSuccess) return null;
        try
        {
            var r = result.Data!.Value.GetProperty("data").GetProperty("repository");
            var resolved = new CreationRepository(r.GetProperty("id").GetString()!, r.GetProperty("nameWithOwner").GetString()!,
                r.GetProperty("hasIssuesEnabled").GetBoolean(), r.GetProperty("isArchived").GetBoolean(),
                r.GetProperty("viewerCanCreateIssues").GetBoolean(), DateTimeOffset.UtcNow);
            return !string.IsNullOrWhiteSpace(resolved.Id) && resolved.Name == name ? resolved : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or JsonException) { return null; }
    }
    private CreatedIssue? ParseIssue(JsonElement element, string repositoryId)
    {
        try
        {
            var issue = new CreatedIssue(element.GetProperty("id").GetString()!, element.GetProperty("repository").GetProperty("id").GetString()!,
                element.GetProperty("number").GetInt32(), element.GetProperty("url").GetString()!, element.GetProperty("title").GetString()!, DateTimeOffset.UtcNow);
            return element.GetProperty("__typename").GetString() == "Issue" && !string.IsNullOrWhiteSpace(issue.Id)
                && issue.RepositoryId == repositoryId && issue.Number > 0 && !string.IsNullOrWhiteSpace(issue.Title)
                && Uri.TryCreate(issue.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == context.Host
                ? issue : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or JsonException) { return null; }
    }
    public async Task<(CreatedIssue? Evidence, string? ReceivedId, ApiResult Result)> CreateAsync(ApplyBatch b, CreationOperation c, CancellationToken token)
    {
        if (b.Project.Scope != ConnectionScope.From(context)) return (null, null, new(ApiOutcome.Failed, FailureKind.IdentityChanged));
        var result = await service.SendAsync(context, ApiRequest.GraphQl(
            "mutation CreateWorkspaceIssue($input:CreateIssueInput!){createIssue(input:$input){issue{" + IssueSelection + "}}}",
            new { input = new { repositoryId = c.Repository.Id, title = c.Title } }), token, "repo");
        // A partial GraphQL response can contain the only recoverable identity evidence.
        CreatedIssue? evidence = null;
        string? receivedId = null;
        try { if (result.Data is { } data) {
            var node = data.GetProperty("data").GetProperty("createIssue").GetProperty("issue");
            if (node.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString())) receivedId = id.GetString();
            evidence = ParseIssue(node, c.Repository.Id);
        } }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or JsonException) { }
        return (evidence, receivedId, result);
    }
    public async Task<CreatedIssue?> ObserveCreatedIssueAsync(ApplyBatch b, CreationOperation c, string? url, CancellationToken token)
    {
        if (b.Project.Scope != ConnectionScope.From(context)) return null;
        ApiRequest request;
        if (url is not null)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != context.Host || uri.Scheme != "https"
                || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0
                || uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is not [var owner, var repo, "issues", var number]
                || !int.TryParse(number, out var n) || n <= 0) return null;
            request = ApiRequest.GraphQl("query BindWorkspaceIssue($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name,followRenames:false){issue(number:$number){" + IssueSelection + "}}}", new { owner, name = repo, number = n });
        }
        else
        {
            var id = c.Verified?.Id ?? c.Received?.Id ?? c.ReceivedId;
            if (id is null) return null;
            request = ApiRequest.GraphQl("query ObserveWorkspaceIssue($id:ID!){node(id:$id){... on Issue{" + IssueSelection + "}}}", new { id });
        }
        var result = await service.SendAsync(context, request, token);
        if (!result.IsSuccess) return null;
        try
        {
            var data = result.Data!.Value.GetProperty("data");
            var issue = ParseIssue(url is null ? data.GetProperty("node") : data.GetProperty("repository").GetProperty("issue"), c.Repository.Id);
            return url is not null || issue?.Id == (c.Verified?.Id ?? c.Received?.Id ?? c.ReceivedId) ? issue : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or JsonException) { return null; }
    }
    public async Task<ProjectReadModel?> ObserveCreationProjectAsync(ApplyBatch b, CancellationToken token)
    {
        var result = await new ProjectReader(service).ReadAsync(context, b.Project, token);
        return result.Outcome == ProjectReadOutcome.Complete && result.Project?.Capability?.CanUpdate == true ? result.Project : null;
    }
    public async Task<(string? ItemId, ApiResult Result)> AddCreatedIssueAsync(ApplyBatch b, CreationOperation c, CancellationToken token)
    {
        if (c.Verified is null) return (null, new(ApiOutcome.Failed, FailureKind.InvalidInput));
        var result = await service.SendAsync(context, ApiRequest.GraphQl(
            "mutation AddWorkspaceIssue($input:AddProjectV2ItemByIdInput!){addProjectV2ItemById(input:$input){item{id project{id} content{__typename ... on Issue{id}}}}}",
            new { input = new { projectId = b.Project.NodeId, contentId = c.Verified.Id } }), token, "project");
        try
        {
            var item = result.Data!.Value.GetProperty("data").GetProperty("addProjectV2ItemById").GetProperty("item");
            if (item.GetProperty("project").GetProperty("id").GetString() == b.Project.NodeId
                && item.GetProperty("content").GetProperty("__typename").GetString() == "Issue"
                && item.GetProperty("content").GetProperty("id").GetString() == c.Verified.Id)
                return (item.GetProperty("id").GetString(), result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or JsonException) { }
        return (null, result);
    }
}
