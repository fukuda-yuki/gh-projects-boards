using System.Text.Json;
using System.Text.Json.Nodes;

namespace GhProjectsBoards.Tests;

// External gh boundary for ordinary-executable tests. Never linked into production.
internal static class FakeCreation
{
    private static string PathFor(string root) => Path.Combine(root, "creation-state.json");
    private static JsonArray Read(string root) => File.Exists(PathFor(root)) ? JsonNode.Parse(File.ReadAllText(PathFor(root)))!.AsArray() : [];
    private static object Issue(JsonNode r, string host) => new { __typename = "Issue", id = r["id"]!.ToString(),
        number = r["number"]!.GetValue<int>(), title = r["title"]!.ToString(), state = "OPEN", viewerCanUpdate = true,
        url = $"https://{host}/sample-user/{r["repository"]}/issues/{r["number"]}",
        repository = new { id = "R-" + r["repository"], nameWithOwner = "sample-user/" + r["repository"], owner = new { id = "O1" } } };
    public static (bool Handled, object? Response) Handle(string query, JsonElement v, string root, string host)
    {
        var rows = Read(root);
        if (query.Contains("ApplyItem") && v.GetProperty("id").GetString() is { } observedItemId && observedItemId.StartsWith("item-created"))
        {
            var row = rows.SingleOrDefault(r => "item-" + r!["id"] == observedItemId && r["member"]!.GetValue<bool>());
            var option = row?["option"]?.ToString();
            return (true, new { data = new { node = row is null ? null : ProjectReaderTests.Item("P1", observedItemId,
                ProjectReaderTests.Page(option is null ? [] : [ProjectReaderTests.Value("P1", "P1-status", option)], option is null ? 0 : 1), Issue(row, host)) } });
        }
        if (query.Contains("CreationRepository")) return (true, new { data = new { repository = new {
            id = "R-" + v.GetProperty("name").GetString(), nameWithOwner = v.GetProperty("owner").GetString() + "/" + v.GetProperty("name").GetString(),
            hasIssuesEnabled = true, isArchived = false, viewerCanCreateIssues = true } } });
        if (query.Contains("ObserveWorkspaceIssue") || query.Contains("BindWorkspaceIssue"))
        {
            var row = rows.SingleOrDefault(r => query.Contains("BindWorkspaceIssue")
                ? r!["number"]!.GetValue<int>() == v.GetProperty("number").GetInt32() && r["repository"]!.ToString() == v.GetProperty("name").GetString()
                : r!["id"]!.ToString() == v.GetProperty("id").GetString());
            object? issue = row is null ? null : Issue(row, host);
            return (true, query.Contains("BindWorkspaceIssue") ? (object)new { data = new { repository = new { issue } } } : new { data = new { node = issue } });
        }
        if (!query.StartsWith("mutation")) return (false, null);
        var input = v.GetProperty("input"); object? response;
        if (query.Contains("CreateWorkspaceIssue"))
        {
            var n = rows.Count + 1; var row = new JsonObject { ["id"] = "created" + n, ["number"] = 1000 + n,
                ["title"] = input.GetProperty("title").GetString(), ["repository"] = input.GetProperty("repositoryId").GetString()![2..],
                ["member"] = false, ["option"] = "todo" };
            rows.Add(row); response = new { data = new { createIssue = new { issue = Issue(row, host) } } };
        }
        else if (query.Contains("AddWorkspaceIssue"))
        {
            var id = input.GetProperty("contentId").GetString()!; var row = rows.Single(r => r!["id"]!.ToString() == id)!;
            row["member"] = true; response = new { data = new { addProjectV2ItemById = new { item = new {
                id = "item-" + id, project = new { id = "P1" }, content = new { __typename = "Issue", id } } } } };
        }
        else if (input.TryGetProperty("itemId", out var itemId) && itemId.GetString()!.StartsWith("item-created"))
        {
            var row = rows.Single(r => "item-" + r!["id"] == itemId.GetString())!;
            row["option"] = query.Contains("ApplyClear") ? null : input.GetProperty("value").GetProperty("singleSelectOptionId").GetString();
            response = query.Contains("ApplyClear") ? (object)new { data = new { clearProjectV2ItemFieldValue = new { projectV2Item = new { id = itemId.GetString() } } } }
                : new { data = new { updateProjectV2ItemFieldValue = new { projectV2Item = new { id = itemId.GetString() } } } };
        }
        else return (false, null);
        File.WriteAllText(PathFor(root), rows.ToJsonString());
        File.AppendAllText(Path.Combine(root, "creation-requests.jsonl"), JsonSerializer.Serialize(new { query, input }) + "\n");
        return (true, response);
    }
    public static object Augment(object response, string root, string host)
    {
        var json = JsonSerializer.SerializeToNode(response)!; var p = json["data"]!["node"]!;
        if (p["id"]!.ToString() != "P1") return response;
        var rows = Read(root).Where(r => r!["member"]!.GetValue<bool>()).ToArray();
        var page = p["items"]!; page["totalCount"] = 101 + rows.Length;
        if (page["pageInfo"]!["hasNextPage"]!.GetValue<bool>()) return json;
        foreach (var r in rows)
        {
            var option = r!["option"]?.ToString();
            var values = new { totalCount = option is null ? 0 : 1, pageInfo = new { hasNextPage = false, endCursor = (string?)null },
                nodes = option is null ? Array.Empty<object>() : [ProjectReaderTests.Value("P1", "P1-status", option: option, id: "v-" + r["id"])] };
            page["nodes"]!.AsArray().Add(JsonSerializer.SerializeToNode(ProjectReaderTests.Item("P1", "item-" + r["id"], values, Issue(r, host))));
        }
        return json;
    }
}
