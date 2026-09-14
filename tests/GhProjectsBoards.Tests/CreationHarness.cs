using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Tests;

internal sealed class CreationHarness
{
    public ApplyTests.Harness Existing = null!;
    public RegistrationWorkspace Workspace => Existing.Workspace;
    public DraftSession Session => Workspace.Drafts!;
    public readonly Dictionary<string, (string Title, string Repository)> Issues = [];
    public readonly HashSet<string> Members = [];
    public readonly List<(string Query, JsonElement Input)> Writes = [];
    public bool LoseCreate, LoseAdd, PartialCreate, Incomplete, AutoAdd, DenyCreate, IdOnlyResponse;
    public Action? AfterCreate, AfterAdd;
    private int serial;
    public object Issue(string id)
    {
        var value = Issues[id];
        return new { __typename = "Issue", id, number = 1000 + int.Parse(id[7..]),
            url = $"https://github.com/sample-user/{value.Repository}/issues/{1000 + int.Parse(id[7..])}",
            title = value.Title, state = "OPEN", viewerCanUpdate = true,
            repository = new { id = "R-" + value.Repository, nameWithOwner = "sample-user/" + value.Repository, owner = new { id = "O1" } } };
    }
    public static async Task<CreationHarness> Create(int itemCount = 100)
    {
        var h = new CreationHarness { Existing = await ApplyTests.Harness.Create(itemCount) };
        var prior = h.Existing.Boundary.Override!;
        h.Existing.Boundary.Override = (q, v) =>
        {
            if (q.Contains("CreationRepository")) return ScriptedRunner.Http(JsonSerializer.Serialize(new { data = new { repository = new {
                id = "R-" + v.GetProperty("name").GetString(), nameWithOwner = v.GetProperty("owner").GetString() + "/" + v.GetProperty("name").GetString(),
                hasIssuesEnabled = true, isArchived = false, viewerCanCreateIssues = !h.DenyCreate } } }));
            if (q.Contains("ObserveWorkspaceIssue") || q.Contains("BindWorkspaceIssue"))
            {
                var id = q.Contains("BindWorkspaceIssue") ? "created" + (v.GetProperty("number").GetInt32() - 1000) : v.GetProperty("id").GetString()!;
                object? issue = h.Issues.ContainsKey(id) ? h.Issue(id) : null;
                return ScriptedRunner.Http(JsonSerializer.Serialize(q.Contains("BindWorkspaceIssue") ? (object)new { data = new { repository = new { issue } } } : new { data = new { node = issue } }));
            }
            if (q.StartsWith("mutation"))
            {
                var input = v.GetProperty("input"); h.Writes.Add((q, input.Clone()));
                if (q.Contains("CreateWorkspaceIssue"))
                {
                    var id = "created" + ++h.serial; h.Issues[id] = (input.GetProperty("title").GetString()!, input.GetProperty("repositoryId").GetString()![2..]);
                    if (h.AutoAdd) h.Members.Add(id);
                    h.AfterCreate?.Invoke();
                    if (h.LoseCreate) return new(ProcessCompletion.TimedOut, true, null);
                    if (h.IdOnlyResponse) return ScriptedRunner.Http(JsonSerializer.Serialize(new { data = new { createIssue = new { issue = new { id } } }, errors = new[] { new { message = "projection failed", type = "INTERNAL" } } }));
                    return ScriptedRunner.Http(JsonSerializer.Serialize(new { data = new { createIssue = new { issue = h.Issue(id) } },
                        errors = h.PartialCreate ? new object[] { new { message = "partial", type = "INTERNAL" } } : [] }));
                }
                if (q.Contains("AddWorkspaceIssue"))
                {
                    var id = input.GetProperty("contentId").GetString()!; h.Members.Add(id);
                    h.AfterAdd?.Invoke();
                    if (h.LoseAdd) return new(ProcessCompletion.TimedOut, true, null);
                    return ScriptedRunner.Http(JsonSerializer.Serialize(new { data = new { addProjectV2ItemById = new { item = new {
                        id = "item-" + id, project = new { id = "P1" }, content = new { __typename = "Issue", id } } } } }));
                }
            }
            if (q.Contains("ApplyItem") && v.GetProperty("id").GetString() is { } itemId && itemId.StartsWith("item-created"))
            {
                var id = itemId[5..];
                if (h.Incomplete || !h.Members.Contains(id)) return ScriptedRunner.Http("{}", 503);
                var option = h.Existing.Selects.GetValueOrDefault(itemId, "todo");
                return ProjectReaderTests.Response(ProjectReaderTests.Item("P1", itemId,
                    ProjectReaderTests.Page(option is null ? [] : [ProjectReaderTests.Value("P1", "P1-status", option)], option is null ? 0 : 1), h.Issue(id)));
            }
            var result = prior(q, v);
            if (!q.Contains("ProjectItems") || result is null) return result;
            if (h.Incomplete) return ScriptedRunner.Http("{}", 503);
            var separator = result.StandardOutput.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var json = JsonNode.Parse(result.StandardOutput[(separator + 4)..])!;
            var page = json["data"]!["node"]!["items"]!;
            foreach (var id in h.Members)
            {
                var template = page["nodes"]![0]!.DeepClone(); template["id"] = "item-" + id;
                template["content"] = JsonSerializer.SerializeToNode(h.Issue(id));
                var option = h.Existing.Selects.GetValueOrDefault("item-" + id, "todo");
                if (option is null) { template["fieldValues"]!["nodes"] = new JsonArray(); template["fieldValues"]!["totalCount"] = 0; }
                else { template["fieldValues"]!["nodes"]![0]!["id"] = "value-" + id; template["fieldValues"]!["nodes"]![0]!["optionId"] = option; }
                page["nodes"]!.AsArray().Add(template);
            }
            page["totalCount"] = page["nodes"]!.AsArray().Count;
            return ScriptedRunner.Http(json.ToJsonString());
        };
        await h.Workspace.PrepareLocalRowsAsync();
        return h;
    }
    public string Add(string title = "A", string repository = "sample-user/first")
    {
        var p = Workspace.Selected!; var w = Session.Workspace; var id = w.AddRow(p with { DefaultRepository = repository });
        w.Commit("P1", w.Open(p).Single(r => r.ItemId == id).Cells[0], title); return id;
    }
    public async Task Apply(params string[] ids)
    { await Workspace.PrepareApplyAsync(ids.ToHashSet()); await Workspace.ConfirmApplyAsync(Workspace.ApplyReview!); }
    public async Task Restart()
    {
        var workspace = new RegistrationWorkspace(new(Existing.Root)); await workspace.RestoreAsync();
        await workspace.BindAsync(Existing.Context, Existing.Service); await workspace.SelectAsync(new(new("github.com", 42), "P1"));
        Existing.Workspace = workspace;
    }
}
