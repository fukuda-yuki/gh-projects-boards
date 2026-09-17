using System.Text.Json;

namespace GhProjectsBoards.Tests;

// Synthetic data shared only by tests and the external fake-gh executable.
internal static class RegistrationResponses
{
    public static object? Query(string query, JsonElement variables, string host = "github.com", int itemCount = 101, bool columns = false, bool bulk = false)
    {
        var next = variables.TryGetProperty("after", out var after) && after.ValueKind == JsonValueKind.String;
        object Page(object[] nodes, bool more = false, int? total = null) => new { nodes, totalCount = total ?? nodes.Length, pageInfo = new { hasNextPage = more, endCursor = more ? "next" : null } };
        object Repo(string name) => new { id = "R-" + name, nameWithOwner = "sample-user/" + name, owner = new { id = "O1" } };
        object Choice(int number) => new { id = "P" + number, number, title = "Project " + number, url = $"https://{host}/users/sample-user/projects/{number}", owner = new { id = "O1", login = "sample-user", __typename = "User" } };
        if (query.Contains("RegistrationOwners")) return new { data = new { viewer = new { organizations = Page([new { login = next ? "organization-2" : "organization-1" }], !next) } } };
        if (query.Contains("RegistrationRepositories")) return new { data = new { repositoryOwner = new { repositories = Page([Repo(next ? "second" : "first")], !next) } } };
        if (query.Contains("RegistrationResolve"))
        {
            var number = variables.GetProperty("number").GetInt32();
            return new { data = new { user = new { projectV2 = Choice(number) } } };
        }
        if (query.Contains("RegistrationProjects"))
        {
            if (variables.TryGetProperty("name", out _)) return new { data = new { repository = new { projectsV2 = Page([Choice(1)]) } } };
            return new { data = new { repositoryOwner = new { projectsV2 = Page([Choice(next ? 2 : 1)], !next) } } };
        }
        if (!variables.TryGetProperty("id", out var idProperty)) return null;
        var id = idProperty.GetString()!;
        if (query.Contains("RegistrationLinks")) return new { data = new { node = new { repositories = Page(id == "P1" ? [Repo("first"), Repo("second")] : []) } } };
        if (query.Contains("ProjectFields")) return new { data = new { node = new { __typename = "ProjectV2", viewerCanUpdate = true, id, number = id == "P1" ? 1 : 2,
            title = "Project " + (id == "P1" ? "1" : "2"), url = $"https://{host}/users/sample-user/projects/{(id == "P1" ? 1 : 2)}", owner = new { id = "O1", __typename = "User" },
            fields = Page(bulk ? Enumerable.Range(0, 12).Select(c => ProjectReaderTests.Field(id, id + (c == 0 ? "-status" : "-field-" + c), c == 0 ? "Status" : "Field " + (c + 1),
                options: [new { id = "todo", name = "Backlog" }, new { id = "done", name = "Ready" }])).ToArray()
                : columns ? new[] { "A", "B", "C" }.Select(c => ProjectReaderTests.Field(id, id + c, "Same name")).ToArray()
                : [ProjectReaderTests.Field(id, id + "-status"), ProjectReaderTests.Field(id, id + "-text", "Other", "TEXT")]) } } };
        if (query.Contains("ProjectItems") || query.Contains("ApplyItem"))
        {
            var projectId = query.Contains("ApplyItem") ? id.Split("-T")[0] : id;
            object Item(int number)
            {
                var repo = number % 2 == 0 ? "second" : "first";
                return ProjectReaderTests.Item(projectId, projectId + "-T" + number,
                    Page(bulk ? Enumerable.Range(0, 12).Select(c => ProjectReaderTests.Value(projectId, projectId + (c == 0 ? "-status" : "-field-" + c), id: projectId + "-V" + number + "-" + c)).ToArray()
                        : columns ? new[] { "A", "B", "C" }.Select(c => ProjectReaderTests.Value(projectId, projectId + c, id: projectId + c + "-V" + number)).ToArray()
                        : [ProjectReaderTests.Value(projectId, projectId + "-status", id: projectId + "-V" + number)]),
                    new { __typename = "Issue", viewerCanUpdate = true, id = "I" + number, number, title = "Issue " + number, state = number % 2 == 0 ? "CLOSED" : "OPEN",
                        url = $"https://{host}/sample-user/{repo}/issues/{number}", repository = Repo(repo) });
            }
            if (query.Contains("ApplyItem")) return new { data = new { node = Item(int.Parse(id.Split("-T")[1])) } };
            var offset = next ? int.Parse(after.GetString() == "next" ? "100" : after.GetString()!) : 0;
            return new { data = new { node = new { __typename = "ProjectV2", id,
                items = new { nodes = Enumerable.Range(offset + 1, Math.Min(100, itemCount - offset)).Select(Item).ToArray(), totalCount = itemCount, pageInfo = new { hasNextPage = offset + 100 < itemCount, endCursor = offset + 100 < itemCount ? (offset + 100).ToString() : null } } } } };
        }
        return null;
    }
}
