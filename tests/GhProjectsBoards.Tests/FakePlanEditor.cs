using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GhProjectsBoards.Core.PlanEditor;

namespace GhProjectsBoards.Tests;

internal sealed record PlanFakeIssue(PlanRow Row, string Body, bool Added) { public bool Archived { get; init; } public string? Phase { get; init; } }
internal sealed record PlanFakeMilestone(string Id, string Repository, string Title, DateOnly? DueDate, bool Closed = false);
// #253: a repository's assignable people for the on-demand resolver. A fixture declares them, so it can
// stand for a 1,000-user repository that the resolver searches like GitHub does.
internal sealed record PlanFakeUser(string Id, string Login);
internal sealed record PlanFakeState(ImmutableArray<PlanFakeIssue> Issues, int NextId, int MutationBatches = 0) { public ImmutableArray<PlanColumnDefinition> ExtraFields { get; init; } = []; public ImmutableArray<PlanRepositoryOption> Labels { get; init; } = []; public ImmutableArray<PlanFakeMilestone> Milestones { get; init; } = []; public ImmutableArray<PlanFakeUser> AssignableUsers { get; init; } = []; public int HiddenItems { get; init; } public int Redacted { get; init; } public int Drafts { get; init; } public int PullRequests { get; init; } public int ReadAttempts { get; init; } public ImmutableArray<string> AddedFields { get; init; } = []; public ImmutableArray<string> Phases { get; init; } = []; public ImmutableDictionary<string, ImmutableArray<string>> SubOrders { get; init; } = ImmutableDictionary<string, ImmutableArray<string>>.Empty; }
internal static class FakePlanEditor
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static string StatePath(string root) => Path.Combine(root, "plan-state.json");
    internal static PlanFakeState Load(string root) => JsonSerializer.Deserialize<PlanFakeState>(File.ReadAllText(StatePath(root)), Json)!;
    internal static void Save(string root, PlanFakeState state) => File.WriteAllText(StatePath(root), JsonSerializer.Serialize(state, Json));
    internal static object Page(IEnumerable<object> values, int total, bool next = false, string? cursor = null) => new { totalCount = total, nodes = values.ToArray(), pageInfo = new { hasNextPage = next, endCursor = cursor } };
    internal static async Task<int> Handle(string query, JsonElement variables, string root, JsonElement scenario)
    {
        var state = Load(root);
        var fault = scenario.TryGetProperty("planFault", out var faultNode) ? faultNode.GetString() : null;
        var faultBatch = scenario.TryGetProperty("faultBatch", out var batchNode) ? batchNode.GetInt32() : 1;
        // #253: the default assignable people are the users the old catalog fixture served, so a case can type
        // a login that is not yet a Project person. A fixture replaces them with assignableUsers.
        var assignablePeople = state.AssignableUsers.IsDefaultOrEmpty
            ? [new PlanFakeUser("U1", "alice"), new PlanFakeUser("U2", "person-U2"), new PlanFakeUser("U101", "late-user")]
            : state.AssignableUsers;
        if (query.Contains("PlanResolveAssignee"))
        {
            if (fault == "resolve-network") { Console.Write("HTTP/2 502 Failed\n\n{}"); return 1; }
            var owner = variables.GetProperty("owner").GetString()!;
            var name = variables.GetProperty("name").GetString()!;
            var login = variables.GetProperty("login").GetString()!;
            if (fault == "resolve-denied")
            {
                Write(new { data = new { viewer = new { databaseId = 42 }, repository = (object?)null } });
                return 0;
            }
            // GitHub's assignableUsers(query:) is a substring search ordered by login, not an exact lookup:
            // `rob` also returns `a-rob` and `b-rob`, ahead of `rob` itself. totalCount counts the matches.
            var size = int.Parse(Regex.Match(query, @"assignableUsers\(first:(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
            var offset = variables.TryGetProperty("after", out var after) && after.ValueKind == JsonValueKind.String ? int.Parse(after.GetString()!, CultureInfo.InvariantCulture) : 0;
            var matches = assignablePeople.Where(u => u.Login.Contains(login, StringComparison.OrdinalIgnoreCase))
                .OrderBy(u => u.Login, StringComparer.OrdinalIgnoreCase).ToArray();
            var page = matches.Skip(offset).Take(size).ToArray();
            var next = offset + page.Length < matches.Length;
            Write(new { data = new { viewer = new { databaseId = fault == "resolve-identity" ? 99 : 42 },
                repository = new { nameWithOwner = owner + "/" + name, assignableUsers = Page(
                    page.Select(u => (object)new { id = u.Id, login = u.Login }), matches.Length, next,
                    next ? (offset + page.Length).ToString(CultureInfo.InvariantCulture) : null) } } });
            return 0;
        }
        // #201: a publish reads and verifies only the items it writes, so a fault aimed at the Project's
        // item read must reach both the complete item walk and the scoped publication read.
        var itemRead = query.Contains("ProjectItems") || query.Contains("PlanPublishItems") || query.Contains("ProjectItemDetails");
        if ((fault == "readfailure" || fault is "after-and-read" or "before-and-read" && state.MutationBatches > 0) && itemRead) { Console.Write("HTTP/2 500 Failed\n\n{}"); return 1; }
        if (query.Contains("ProjectFields")) { state = state with { ReadAttempts = state.ReadAttempts + 1 }; Save(root, state); }
        if ((fault == "verificationfailure" && state.MutationBatches >= faultBatch ||
            fault == "uncertain-verification" && state.MutationBatches >= faultBatch) && itemRead)
        { Console.Write("HTTP/2 500 Failed\n\n{}"); return 1; }
        if (fault == "uncertain-verification" && state.MutationBatches >= faultBatch && query.Contains("PlanCreationGuard"))
        { Console.Write("HTTP/2 500 Failed\n\n{}"); return 1; }
        if (fault == "read-once" && state.ReadAttempts == 1 && itemRead)
        { Console.Write("HTTP/2 500 Failed\n\n{}"); return 1; }
        var workspace = scenario.TryGetProperty("workspace", out var ws) && ws.GetBoolean();
        if (workspace && query.Contains("Registration"))
        {
            object Choice(string projectId, string owner) => new { id = projectId, number = projectId == "P1" ? 3 : 4,
                url = "https://github.com/" + (owner == "fixture-user" ? "users/" : "orgs/") + owner + "/projects/" + (projectId == "P1" ? "3" : "4"),
                    title = projectId == "P1" ? scenario.TryGetProperty("versionTitle", out var version) ? version.GetString() : "開発計画" : "運用計画", owner = new { id = "O1", __typename = owner == "fixture-user" ? "User" : "Organization", login = owner } };
            var large = scenario.TryGetProperty("chooserCatalog", out var catalog) && catalog.GetBoolean();
            if (query.Contains("RegistrationOwners")) Write(new { data = new { viewer = new { organizations = Page(large ? [new { login = "acme" }, new { login = "broken" }] : [new { login = "acme" }], large ? 2 : 1) } } });
            else if (query.Contains("RegistrationProjects"))
            {
                var owner = variables.GetProperty("owner").GetString()!;
                File.AppendAllText(Path.Combine(root, "listing-requests.txt"), owner + "\n");
                if (owner == "broken" && !File.Exists(Path.Combine(root, "recover-owner"))) { Console.Write("HTTP/2 500 Failed\n\n{}"); return 1; }
                var count = large && owner == "fixture-user" ? scenario.TryGetProperty("chooserProjectCount", out var projectCount) ? projectCount.GetInt32() : 80 : 1;
                var entries = Enumerable.Range(1, count).Select(i => {
                    var node = JsonSerializer.SerializeToNode(Choice(owner == "fixture-user" ? "P1" : "P2", owner))!.AsObject();
                    if (i > 1) { node["id"] = "PX" + i; node["number"] = i + 10; node["title"] = "計画 " + i; node["url"] = $"https://github.com/users/{owner}/projects/{i + 10}"; }
                    node["closed"] = large && i % 4 == 0;
                    node["updatedAt"] = "2026-10-17T00:00:00Z";
                    var names = !large ? new[] { "acme/repo" } : i % 3 == 0 ? [] : i % 3 == 1 ? new[] { owner + "/alpha", "other/shared" } : new[] { owner + "/beta" };
                    node["repositories"] = JsonSerializer.SerializeToNode(new { nodes = names.Select(n => new { nameWithOwner = n }) });
                    return (object)node;
                }).ToArray();
                var offset = variables.TryGetProperty("after", out var after) && after.ValueKind == JsonValueKind.String ? int.Parse(after.GetString()!) : 0;
                Write(new { data = new { repositoryOwner = new { projectsV2 = Page(entries.Skip(offset).Take(50), count, offset + 50 < count, offset + 50 < count ? (offset + 50).ToString() : null) } } });
            }
            else if (query.Contains("RegistrationResolve"))
                Write(new { data = new { user = new { projectV2 = Choice("P1", "fixture-user") } } });
            else Write(new { data = new { node = new { repositories = Page([new { id = "R1", nameWithOwner = "acme/repo", owner = new { id = "O1" } }], 1) } } });
            return 0;
        }
        var projectId = workspace && variables.TryGetProperty("id", out var projectNode) && projectNode.GetString() == "P2" ? "P2" : "P1";
        string ItemIdFor(string owner, string id) => workspace && owner == "P2" ? "P2:" + id : id;
        string ItemId(string id) => ItemIdFor(projectId, id);
        static string ItemIdentity(string id) => id[(id.LastIndexOf(':') + 1)..][2..];
        var fieldRoles = new[] { PlanField.Estimate, PlanField.Remaining, PlanField.Actual, PlanField.Start, PlanField.End, PlanField.StartNoEarlierThan, PlanField.Fixed, PlanField.Status };
        string Type(string role) => role is PlanField.Estimate or PlanField.Remaining or PlanField.Actual ? "NUMBER" : role is PlanField.Fixed or PlanField.Status ? "SINGLE_SELECT" : "DATE";
        object Field(string role) => new { __typename = role is PlanField.Fixed or PlanField.Status ? "ProjectV2SingleSelectField" : "ProjectV2Field", id = "F-" + role, name = workspace && role == PlanField.Start ? "Start date" : workspace && role == PlanField.End ? "Target date" : state.AddedFields.Contains(role.ToString()) ? role == PlanField.Fixed ? "日程固定" : "開始日指定" : role.ToString(), dataType = Type(role), isIssueField = false, project = new { id = projectId }, options = role == PlanField.Status ? new[] { new { id = "progress", name = "In progress" }, new { id = "done", name = "Done" }, new { id = "backlog", name = "Backlog" } } : new[] { new { id = "fixed", name = "固定" } } };
        object Value(PlanRow row, string role)
        {
            var scalar = JsonNode.Parse(PlanValues.Get(row, role));
            var value = new JsonObject { ["__typename"] = role is PlanField.Fixed or PlanField.Status ? "ProjectV2ItemFieldSingleSelectValue" : Type(role) == "NUMBER" ? "ProjectV2ItemFieldNumberValue" : "ProjectV2ItemFieldDateValue",
                ["id"] = row.Identity + "-" + role, ["field"] = JsonSerializer.SerializeToNode(new { id = "F-" + role, project = new { id = projectId } }) };
            value[role is PlanField.Fixed or PlanField.Status ? "optionId" : Type(role) == "NUMBER" ? "number" : "date"] = role == PlanField.Fixed ? JsonValue.Create("fixed") : role == PlanField.Status ? JsonValue.Create(row.Status == "Done" ? "done" : row.Status == "Backlog" ? "backlog" : "progress") : scalar;
            return value;
        }
        object Children(string identity, int offset = 0)
        {
            var children = state.SubOrders.GetValueOrDefault(identity, state.Issues.Where(i => i.Row.Parent == identity).Select(i => i.Row.Identity).ToImmutableArray());
            return Page(children.Skip(offset).Take(100).Select(id => (object)new { id }), children.Length,
                offset + 100 < children.Length, offset + 100 < children.Length ? (offset + 100).ToString() : null);
        }
        // 工程 is an ordinary single-select custom field; the app reads it like any other Project field.
        object PhaseField() => new { __typename = "ProjectV2SingleSelectField", id = "F-Phase", name = "工程", dataType = "SINGLE_SELECT", isIssueField = false, project = new { id = projectId },
            options = state.Phases.Select(p => new { id = "phase-" + p, name = p }).ToArray() };
        object[] Values(PlanFakeIssue issue)
        {
            var values = fieldRoles.Where(f => PlanValues.Get(issue.Row, f) is not ("null" or "false")).Select(f => Value(issue.Row, f));
            var result = (issue.Phase is null ? values : values.Append(new { __typename = "ProjectV2ItemFieldSingleSelectValue", id = issue.Row.Identity + "-Phase",
                field = new { id = "F-Phase", project = new { id = projectId } }, optionId = "phase-" + issue.Phase })).ToList();
            foreach (var definition in state.ExtraFields) {
                var raw = issue.Row.ExtraValues.GetValueOrDefault("Project:" + definition.Id)?.Json;
                if (raw is null or "null") continue;
                var type = definition.DataType switch { "TEXT" => "Text", "NUMBER" => "Number", "DATE" => "Date", "ITERATION" => "Iteration", _ => "SingleSelect" };
                var key = definition.DataType switch { "TEXT" => "text", "NUMBER" => "number", "DATE" => "date", "ITERATION" => "title", _ => "optionId" };
                result.Add(new JsonObject { ["__typename"] = "ProjectV2ItemField" + type + "Value", ["id"] = issue.Row.Identity + "-" + definition.Id,
                    ["field"] = JsonSerializer.SerializeToNode(new { id = definition.Id, project = new { id = projectId } }),
                    [key] = key == "optionId" ? JsonValue.Create("extra-" + JsonSerializer.Deserialize<string>(raw)) : JsonNode.Parse(raw) });
            }
            return result.ToArray();
        }
        object Milestone(PlanFakeMilestone m) => new { id = m.Id, title = m.Title,
            dueOn = m.DueDate.HasValue ? m.DueDate.Value.ToString("yyyy-MM-dd") + "T00:00:00Z" : null, state = m.Closed ? "CLOSED" : "OPEN" };
        object Item(PlanFakeIssue issue)
        {
            var row = issue.Row;
            var item = JsonSerializer.SerializeToNode(new { __typename = "ProjectV2Item", id = ItemId("T-" + row.Identity), type = "ISSUE", isArchived = issue.Archived, project = new { id = projectId },
                content = new { __typename = "Issue", id = row.Identity, number = int.Parse(row.Identity[1..]), url = "https://github.com/" + row.Repository + "/issues/" + row.Identity[1..], title = row.Title,
                    state = row.Closed ? "CLOSED" : "OPEN",
                    stateReason = row.Closed ? row.Cancelled ? "NOT_PLANNED" : "COMPLETED" : null,
                    labels = Page(state.Labels.Where(l => l.Repository == row.Repository && row.Labels.Contains(l.Name)).Take(100).Select(l => (object)new { id = l.Id, name = l.Name }), row.Labels.Length, row.Labels.Length>100, row.Labels.Length>100 ? "100" : null),
                    milestone = state.Milestones.SingleOrDefault(m => m.Repository == row.Repository && m.Title == row.MilestoneTitle) is { } member ? Milestone(member) : null,
                    closedAt = row.Closed && row.CloseDate is { } closeDate ? new DateTimeOffset(closeDate.ToDateTime(new TimeOnly(12, 0)),
                        TimeZoneInfo.Local.GetUtcOffset(closeDate.ToDateTime(new TimeOnly(12, 0)))).ToUniversalTime().ToString("O") : null,
                    // #231: only this fault denies Issue update permission, so the ordinary fixtures stay writable.
                    viewerCanUpdate = !(fault == "no-update-permission" && row.Identity == "I1"), repository = new { id = row.Repository == "acme/other" ? "R2" : "R1", nameWithOwner = row.Repository, owner = new { id = "O1" } },
                    assignees = Page(row.Assignees.Select(id => (object)new { id, login = workspace ? "person-" + id : id }), row.Assignees.Length),
                    blockedBy = Page(row.Predecessors.Select(id => (object)new { id }), row.Predecessors.Length),
                    subIssues = query.Contains("subIssues(", StringComparison.Ordinal) ? Children(row.Identity) : null,
                    parent = row.Parent is null ? null : new { id = row.Parent } },
                fieldValues = Page(Values(issue), Values(issue).Length) })!.AsObject();
            return item;
        }
        if (query.Contains("ProjectIssueLabels"))
        {
            var data=new JsonObject { ["viewer"]=new JsonObject { ["databaseId"]=42 } };
            foreach(var variable in variables.EnumerateObject().Where(p=>p.Name.StartsWith("id",StringComparison.Ordinal)))
            {
                var index=variable.Name[2..]; var id=variable.Value.GetString()!;
                var row=state.Issues.Single(i=>i.Row.Identity==id).Row;
                var offset=int.Parse(variables.GetProperty("after"+index).GetString()!);
                var all=state.Labels.Where(l=>l.Repository==row.Repository && row.Labels.Contains(l.Name)).ToArray();
                data["l"+index]=JsonSerializer.SerializeToNode(new { __typename="Issue", id, labels=Page(all.Skip(offset).Take(100).Select(l=>(object)new{id=l.Id,name=l.Name}),all.Length,offset+100<all.Length,offset+100<all.Length?(offset+100).ToString():null) });
            }
            Write(new JsonObject { ["data"]=data }); return 0;
        }
        if (query.Contains("PlanPublishItems"))
        {
            // #201: the publication read resolves only the requested Issue identities to their items.
            // A churned read cannot deliver one of them, exactly as a churned item walk changes its total.
            var churn = fault == "churn" && state.ReadAttempts <= faultBatch
                || fault == "verification-churn" && state.MutationBatches > 0 && state.ReadAttempts <= 3;
            var nodes = new List<object?>();
            foreach (var id in variables.GetProperty("ids").EnumerateArray().Select(n => n.GetString()!).ToArray())
            {
                var issue = state.Issues.FirstOrDefault(i => i.Row.Identity == id);
                if (issue is null || churn) { nodes.Add(null); churn = false; continue; }
                // The same membership visibility the item walk applies, so the scoped read observes what a
                // complete read would have observed for the same item.
                var visible = issue.Added && !(fault == "membership-delay" && state.MutationBatches is > 0 and <= 4
                    && state.ReadAttempts < 2 && int.Parse(issue.Row.Identity[1..]) % 2 == 0);
                var owners = visible ? workspace ? new[] { "P1", "P2" } : new[] { "P1" } : [];
                nodes.Add(new { __typename = "Issue", id,
                    projectItems = Page(owners.Select(owner => (object)new { id = ItemIdFor(owner, "T-" + id), project = new { id = owner } }), owners.Length) });
            }
            Write(new { data = new { nodes = nodes.ToArray(), viewer = new { databaseId = 42 } } });
            return 0;
        }
        object? node = null;
        if (query.Contains("PlanAddField"))
        {
            var input = variables.GetProperty("input"); var name = input.GetProperty("name").GetString()!;
            var role = name == "開始日指定" ? PlanField.StartNoEarlierThan : PlanField.Fixed;
            if (name == "日程固定" && (input.GetProperty("dataType").GetString() != "SINGLE_SELECT" || input.GetProperty("singleSelectOptions")[0].GetProperty("name").GetString() != "固定")) throw new InvalidOperationException("Invalid field options.");
            Save(root, state with { AddedFields = state.AddedFields.Add(role.ToString()), MutationBatches = state.MutationBatches + 1 });
            Write(new { data = new { createProjectV2Field = new { projectV2Field = new { id = "F-" + role, name, dataType = Type(role) } } } }); return 0;
        }
        if (query.Contains("PlanCsvRepository"))
        {
            if (fault == "csv-network") { Console.Write("HTTP/2 502 Failed\n\n{}"); return 1; }
            var name = variables.GetProperty("owner").GetString() + "/" + variables.GetProperty("name").GetString();
            if (name is not ("acme/repo" or "acme/other")) { Write(new { data = new { repository = (object?)null }, errors = new[] { new { type = "NOT_FOUND", path = new[] { "repository" }, message = "Could not resolve repository" } } }); return 1; }
            var second = variables.TryGetProperty("after", out var after) && after.ValueKind == JsonValueKind.String;
            Write(new { data = new { repository = new { nameWithOwner = name, hasIssuesEnabled = true, isArchived = false, viewerCanCreateIssues = true,
                assignableUsers = Page(second ? new object[] { new { id = "U101", login = "late-user" } } : [new { id = "U1", login = "alice" }, new { id = "U2", login = "person-U2" }], second ? 1 : 2, !second, second ? null : "next") } } }); return 0;
        }
        if (query.Contains("PlanPublishRepository"))
        {
            if (fault == "repository-missing") { Write(new { data = new { repository = (object?)null } }); return 0; }
            var name = variables.GetProperty("owner").GetString() + "/" + variables.GetProperty("name").GetString();
            Write(new { data = new { repository = new { id = name == "acme/other" ? "R2" : "R1", nameWithOwner = name, hasIssuesEnabled = fault != "repository-disabled",
                isArchived = fault == "repository-archived", viewerCanCreateIssues = fault != "repository-denied" && !(fault == "other-repository-denied" && name == "acme/other") } } }); return 0;
        }
        if (query.Contains("PlanCreationGuard"))
        {
            var all = state.Issues.Where(i => i.Row.Repository == (variables.GetProperty("id").GetString() == "R2" ? "acme/other" : "acme/repo")).ToArray();
            var offset = variables.TryGetProperty("after", out var cursor) && cursor.ValueKind == JsonValueKind.String ? int.Parse(cursor.GetString()!) : 0;
            node = new { issues = Page(all.Skip(offset).Take(100).Select(i => (object)new { id = i.Row.Identity, body = i.Body }), all.Length,
                offset + 100 < all.Length, offset + 100 < all.Length ? (offset + 100).ToString() : null) };
        }
        else if (query.Contains("PlanExistingItem"))
        {
            var id = variables.GetProperty("id").GetString(); var issue = state.Issues.Single(i => i.Row.Identity == id);
            var visible = issue.Added && !(fault == "membership-delay" && state.ReadAttempts < 2);
            var owners = visible ? workspace ? new[] { "P1", "P2" } : new[] { "P1" } : [];
            node = new { projectItems = Page(owners.Select(owner => (object)new { id = ItemIdFor(owner, "T-" + id), project = new { id = owner } }), owners.Length) };
        }
        else if (query.Contains("PlanSubIssues"))
        {
            var id = variables.GetProperty("id").GetString()!;
            var offset = variables.TryGetProperty("after", out var after) && after.ValueKind == JsonValueKind.String ? int.Parse(after.GetString()!) : 0;
            node = new { __typename = "Issue", id, subIssues = Children(id, offset) };
        }
        else if (query.Contains("ProjectLabels"))
        {
            var id = variables.GetProperty("id").GetString()!;
            var offset = variables.TryGetProperty("after",out var cursor) && cursor.ValueKind == JsonValueKind.String ? int.Parse(cursor.GetString()!) : 0;
            var all = state.Labels.Where(l => l.Repository == (id == "R2" ? "acme/other" : "acme/repo")).ToArray();
            node = new { __typename = "Repository", id, labels = Page(all.Skip(offset).Take(100).Select(l => (object)new { id = l.Id, name = l.Name }), all.Length, offset + 100 < all.Length, offset + 100 < all.Length ? (offset + 100).ToString() : null) };
        }
        else if (query.Contains("ProjectMilestones"))
        {
            var id = variables.GetProperty("id").GetString()!;
            var offset = variables.TryGetProperty("after", out var after) && after.ValueKind == JsonValueKind.String ? int.Parse(after.GetString()!) : 0;
            var all = state.Milestones.Where(m => m.Repository == (id == "R2" ? "acme/other" : "acme/repo")).ToArray();
            node = new { __typename = "Repository", id, milestones = Page(all.Skip(offset).Take(100).Select(Milestone),
                all.Length + (fault == "milestones-incomplete" ? 1 : 0), offset + 100 < all.Length,
                offset + 100 < all.Length ? (offset + 100).ToString() : null) };
        }
        else if (query.Contains("ProjectFields"))
            node = new { __typename = "ProjectV2", id = projectId, number = 3, url = "https://github.com/users/acme/projects/3", title = "Plan", viewerCanUpdate = true,
                owner = new { __typename = "User", id = "O1" }, fields = Page(fieldRoles.Select(Field).Concat(state.Phases.IsEmpty ? [] : [PhaseField()]).Concat(state.ExtraFields.Select(f => (object)new { __typename = f.DataType == "SINGLE_SELECT" ? "ProjectV2SingleSelectField" : f.DataType == "ITERATION" ? "ProjectV2IterationField" : "ProjectV2Field", id = f.Id, name = f.Name, dataType = f.DataType, isIssueField = false, project = new { id = projectId }, options = f.Options.Select(o => new { id = "extra-" + o, name = o }).ToArray() })), fieldRoles.Length + (state.Phases.IsEmpty ? 0 : 1) + state.ExtraFields.Length) };
        else if (query.Contains("ProjectItems") || query.Contains("ProjectItemDetails"))
        {
            var all = state.Issues.Where(i => i.Added && !(fault == "membership-delay" && state.MutationBatches is > 0 and <= 4 && state.ReadAttempts < 2 && int.Parse(i.Row.Identity[1..]) % 2 == 0)).ToArray();
            var offset = variables.TryGetProperty("after", out var cursor) && cursor.ValueKind == JsonValueKind.String ? int.Parse(cursor.GetString()!) : 0;
            object Excluded(string id, string kind, string type) => new { __typename = "ProjectV2Item", id = ItemId(id), type, isArchived = false,
                project = new { id = projectId }, content = new { __typename = kind, id = "content-" + id }, fieldValues = Page([], 0) };
            object[] Nodes() => all.Select(Item).Concat(Enumerable.Range(0, state.Drafts).Select(i => Excluded("D" + i, "DraftIssue", "DRAFT_ISSUE")))
                .Concat(Enumerable.Range(0, state.PullRequests).Select(i => Excluded("PR" + i, "PullRequest", "PULL_REQUEST")))
                .Concat(Enumerable.Range(0, state.Redacted).Select(i => (object)new { __typename = "ProjectV2Item", id = ItemId("REDACTED" + i), type = "REDACTED", isArchived = false, project = new { id = projectId }, content = (object?)null, fieldValues = Page([], 0) })).ToArray();
            if (query.Contains("ProjectItemDetails"))
            {
                var ids = variables.GetProperty("ids").EnumerateArray().Select(id => id.GetString()!).ToArray();
                var byId = new Dictionary<string, JsonElement>();
                foreach (var owner in ids.Select(id => workspace && id.StartsWith("P2:", StringComparison.Ordinal) ? "P2" : "P1").Distinct())
                {
                    projectId = owner;
                    foreach (var item in Nodes().Select(n => JsonSerializer.SerializeToElement(n)))
                        byId.Add(item.GetProperty("id").GetString()!, item);
                }
                Write(new { data = new { nodes = ids.Select(id => byId.TryGetValue(id, out var item) ? (JsonElement?)item : null).ToArray(), viewer = new { databaseId = 42 } } });
                return 0;
            }
            var nodes = Nodes();
            node = new { __typename = "ProjectV2", id = projectId, items = Page(nodes.Skip(offset).Take(100).Select(n => new { id = JsonSerializer.SerializeToElement(n).GetProperty("id").GetString() }), nodes.Length + state.HiddenItems + (offset == 0 &&
                (fault == "churn" && state.ReadAttempts <= faultBatch || fault == "verification-churn" && state.MutationBatches > 0 && state.ReadAttempts <= 3) ? 1 : 0),
                offset + 100 < nodes.Length, offset + 100 < nodes.Length ? (offset + 100).ToString() : null) };
        }
        else if (query.StartsWith("mutation PlanPublish", StringComparison.Ordinal))
        {
            state = state with { MutationBatches = state.MutationBatches + 1, ReadAttempts = 0 };
            if (fault is "rate" or "reset" && state.MutationBatches == faultBatch)
            {
                Save(root, state);
                var header = fault == "rate" ? "Retry-After: 1" : "X-RateLimit-Remaining: 0\nX-RateLimit-Reset: " + DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeSeconds();
                Console.Write("HTTP/2 429 Limited\n" + header + "\n\n{}"); return 1;
            }
            var data = new JsonObject(); var errors = new JsonArray(); var issues = state.Issues.ToList(); var nextId = state.NextId; var subOrders = state.SubOrders;
            File.AppendAllText(Path.Combine(root, "plan-mutations.jsonl"), JsonSerializer.Serialize(new { query, variables, started = DateTimeOffset.UtcNow }) + "\n");
            var matches = Regex.Matches(query, @"(w\d+):(\w+)\(input:\$(v\d+)\)");
            if (fault is "field-rate-rejected" or "field-rate-no-delay" && state.MutationBatches <= 4)
            {
                Save(root, state);
                foreach (Match match in matches) data[match.Groups[1].Value] = null;
                var headers = fault == "field-rate-no-delay" ? "" : "\nRetry-After: " + (5 - state.MutationBatches);
                Console.Write("HTTP/2 429 Limited" + headers + "\n\n" + new JsonObject { ["data"] = data }.ToJsonString());
                return 1;
            }
            if (fault is "before" or "before-and-read" && state.MutationBatches == faultBatch) { Save(root, state); return 1; }
            if (fault is "global-unknown" or "global-resource-unknown" && state.MutationBatches == faultBatch)
            {
                Save(root, state);
                if (fault == "global-resource-unknown")
                {
                    Write(new { data = (object?)null, errors = new object[] {
                        new { type = "RESOURCE_LIMITS_EXCEEDED", message = "Synthetic resource failure" },
                        new { message = "Something went wrong while executing your query." } } });
                    return 0;
                }
                Write(new { data = (object?)null, errors = new[] { new { message = "Something went wrong while executing your query." } } });
                return 0;
            }
            var interruptedPositions = fault == "position-batch-interrupted" && query.Contains("updateProjectV2ItemPosition") && matches.Count > 1;
            foreach (Match match in matches)
            {
                if (interruptedPositions && data.Count >= 2 || fault == "creation-resource-uncertain" && query.Contains("createIssue") && matches.Count > 1 && data.Count >= 1) break;
                var alias = match.Groups[1].Value; var mutation = match.Groups[2].Value; var input = variables.GetProperty(match.Groups[3].Value);
                if (workspace && input.TryGetProperty("projectId", out var destination)) projectId = destination.GetString()!;
                if (fault is "missing-alias" or "unknown-and-resource" && state.MutationBatches == faultBatch && alias != "w0")
                {
                    if (fault == "unknown-and-resource" && alias == "w1")
                    { errors.Add(JsonSerializer.SerializeToNode(new { path = new[] { alias }, type = "RESOURCE_LIMITS_EXCEEDED", message = "Synthetic resource failure" })); data[alias] = null; }
                    continue;
                }
                if (fault == "sibling-denied" && mutation == "reprioritizeSubIssue")
                { errors.Add(JsonSerializer.SerializeToNode(new { path = new[] { alias }, type = "FORBIDDEN", message = "Synthetic sibling failure" })); data[alias] = null; continue; }
                if (fault == "sibling-resource" && mutation == "reprioritizeSubIssue" && matches.Count > 1 && input.GetProperty("subIssueId").GetString() == "I4")
                { errors.Add(JsonSerializer.SerializeToNode(new { path = new[] { alias }, type = "RESOURCE_LIMITS_EXCEEDED", message = "Synthetic sibling move limit" })); data[alias] = null; continue; }
                if (fault == "resource-always" && mutation.Contains("ItemFieldValue") || fault == "parent-denied" && mutation == "addSubIssue" && input.GetProperty("subIssueId").GetString() == "I2" || fault == "mixed-resource" && query.Contains("ItemFieldValue") && matches.Count > 3 && alias != "w0" || fault == "field-denied" && mutation.Contains("ItemFieldValue") || fault == "forbid-mutation" || fault is "partial" or "resource" && state.MutationBatches == faultBatch && alias == "w1")
                { errors.Add(JsonSerializer.SerializeToNode(new { path = new[] { alias }, type = fault is "resource" or "mixed-resource" or "resource-always" ? "RESOURCE_LIMITS_EXCEEDED" : "FORBIDDEN", message = "Synthetic failure" })); data[alias] = null; continue; }
                string? id = null; var selection = "issue";
                if (mutation == "createIssue")
                {
                    if (input.GetProperty("title").GetString() == "Rejected") { errors.Add(JsonSerializer.SerializeToNode(new { path = new[] { alias }, type = "UNPROCESSABLE", message = "Synthetic invalid title" })); data[alias] = null; continue; }
                    id = "I" + nextId++; issues.Add(new(new(id, input.GetProperty("title").GetString()!, input.GetProperty("repositoryId").GetString() == "R2" ? "acme/other" : "acme/repo"), input.GetProperty("body").GetString()!, fault is "autoadd" or "autoadd-after" || fault == "membership-delay" && nextId % 2 == 1));
                }
                else if (mutation == "addProjectV2ItemById")
                {
                    var issueId = input.GetProperty("contentId").GetString()!; var index = issues.FindIndex(i => i.Row.Identity == issueId);
                    if (issues[index].Added)
                    { errors.Add(JsonSerializer.SerializeToNode(new { path = new[] { alias }, message = "Content already exists in this project" })); data[alias] = null; continue; }
                    var addedIssue = issues[index] with { Added = !(fault == "membership-pending" && issueId == "I2") };
                    if (fault is "workflow-status" or "workflow-backlog" or "mixed-resource") addedIssue = addedIssue with { Row = addedIssue.Row with { Status = fault == "workflow-status" ? "In progress" : "Backlog" } };
                    issues.RemoveAt(index); issues.Add(addedIssue); id = ItemId("T-" + issueId); selection = "item";
                }
                else
                {
                    var identity = input.TryGetProperty("itemId", out var itemId) ? ItemIdentity(itemId.GetString()!) :
                        input.TryGetProperty("labelableId", out var labelable) ? labelable.GetString()! : input.TryGetProperty("id", out var issueId) ? issueId.GetString()! : input.TryGetProperty("assignableId", out var assignable) ? assignable.GetString()! : input.GetProperty("issueId").GetString()!;
                    var index = issues.FindIndex(i => i.Row.Identity == identity); var issue = issues[index]; var row = issue.Row;
                    if (mutation == "updateIssue") row = input.TryGetProperty("milestoneId", out var milestone) ? row with { MilestoneTitle = milestone.ValueKind == JsonValueKind.Null ? null : state.Milestones.Single(m => m.Id == milestone.GetString() && m.Repository == row.Repository).Title } : row with { Title = input.GetProperty("title").GetString()! };
                    else if (mutation is "addLabelsToLabelable" or "removeLabelsFromLabelable")
                    {
                        var names = input.GetProperty("labelIds").EnumerateArray().Select(n => state.Labels.Single(l => l.Repository == row.Repository && l.Id == n.GetString()).Name).ToArray();
                        row = row with { Labels = (mutation == "addLabelsToLabelable" ? row.Labels.Union(names) : row.Labels.Except(names)).Order(StringComparer.Ordinal).ToImmutableArray() }; selection = "labelable";
                    }
                    else if (mutation.Contains("ItemFieldValue"))
                    {
                        var fieldId = input.GetProperty("fieldId").GetString()!;
                        var extra = state.ExtraFields.SingleOrDefault(f => f.Id == fieldId);
                        var role = extra is null ? fieldId[2..] : "Project:" + fieldId;
                        var json = mutation.StartsWith("clear") ? role == PlanField.Fixed ? "false" : "null" : role == PlanField.Fixed ? "true" : input.GetProperty("value").EnumerateObject().Single().Value.GetRawText();
                        if (extra?.DataType == "SINGLE_SELECT" && !mutation.StartsWith("clear")) json = JsonSerializer.Serialize(input.GetProperty("value").GetProperty("singleSelectOptionId").GetString()![6..]);
                        if (role == PlanField.Status && !mutation.StartsWith("clear"))
                            json = JsonSerializer.Serialize(input.GetProperty("value").GetProperty("singleSelectOptionId").GetString() switch {
                                "done" => "Done", "progress" => "In progress", "backlog" => "Backlog", _ => throw new InvalidOperationException("Unknown Status option ID") });
                        if (role == PlanField.Phase && !mutation.StartsWith("clear"))
                            json = JsonSerializer.Serialize(state.Phases.Single(p => "phase-" + p == input.GetProperty("value").GetProperty("singleSelectOptionId").GetString()));
                        if (fault != "mismatch") {
                            row = PlanValues.Set(row, role, json);
                            if (role == PlanField.Phase) issue = issue with { Phase = row.Phase };
                        }
                        selection = "projectV2Item";
                    }
                    else if (mutation == "updateProjectV2ItemPosition")
                    {
                        issues.RemoveAt(index);
                        var afterId = input.GetProperty("afterId");
                        var place = afterId.ValueKind == JsonValueKind.Null ? 0 : issues.FindIndex(i => ItemId("T-" + i.Row.Identity) == afterId.GetString()) + 1;
                        issues.Insert(place, issue); index = place; selection = "items";
                    }
                    else if (mutation is "addAssigneesToAssignable" or "removeAssigneesFromAssignable")
                    {
                        var ids = input.GetProperty("assigneeIds").EnumerateArray().Select(n => n.GetString()!).ToArray();
                        row = row with { Assignees = (mutation.StartsWith("add") ? row.Assignees.Union(ids) : row.Assignees.Except(ids)).ToImmutableArray() };
                    }
                    else if (mutation is "addBlockedBy" or "removeBlockedBy")
                    {
                        var idValue = input.GetProperty("blockingIssueId").GetString()!;
                        if (mutation == "addBlockedBy")
                        {
                            var pending = new Stack<string>(new[] { idValue }); var seen = new HashSet<string>();
                            while (pending.TryPop(out var dependency))
                            {
                                if (!seen.Add(dependency)) continue;
                                foreach (var previous in issues.SingleOrDefault(i => i.Row.Identity == dependency)?.Row.Predecessors ?? []) pending.Push(previous);
                            }
                            if (seen.Contains(identity))
                            {
                                errors.Add(JsonSerializer.SerializeToNode(new { path = new[] { alias }, type = "UNPROCESSABLE", message = "Adding this dependency would create a cycle." }));
                                data[alias] = null; continue;
                            }
                        }
                        row = row with { Predecessors = (mutation.StartsWith("add") ? row.Predecessors.Union([idValue]) : row.Predecessors.Except([idValue])).ToImmutableArray() };
                    }
                    else if (mutation is "addSubIssue" or "removeSubIssue")
                    {
                        var childId = input.GetProperty("subIssueId").GetString()!;
                        if (mutation == "addSubIssue")
                        {
                            var ancestor = identity;
                            while (ancestor is not null && ancestor != childId) ancestor = issues.SingleOrDefault(i => i.Row.Identity == ancestor)?.Row.Parent;
                            if (ancestor == childId)
                            {
                                errors.Add(JsonSerializer.SerializeToNode(new { path = new[] { alias }, type = "UNPROCESSABLE", message = "Adding this sub-issue would create a cycle." }));
                                data[alias] = null; continue;
                            }
                        }
                        var childIndex = issues.FindIndex(i => i.Row.Identity == childId);
                        var oldParent = issues[childIndex].Row.Parent;
                        if (oldParent is not null) subOrders = subOrders.SetItem(oldParent, subOrders.GetValueOrDefault(oldParent, []).Remove(childId));
                        issues[childIndex] = issues[childIndex] with { Row = issues[childIndex].Row with { Parent = mutation.StartsWith("add") ? identity : null } };
                        if (mutation.StartsWith("add")) subOrders = subOrders.SetItem(identity, subOrders.GetValueOrDefault(identity, []).Add(childId));
                    }
                    else if (mutation == "reprioritizeSubIssue")
                    {
                        var childId = input.GetProperty("subIssueId").GetString()!;
                        var children = subOrders.GetValueOrDefault(identity, []).Remove(childId).ToList();
                        var after = input.TryGetProperty("afterId", out var preceding);
                        var siblingId = after ? preceding.GetString() : input.GetProperty("beforeId").GetString();
                        children.Insert(children.IndexOf(siblingId!) + (after ? 1 : 0), childId);
                        subOrders = subOrders.SetItem(identity, children.ToImmutableArray());
                    }
                    // #231: a per-row state failure keeps that row's write pending and lets the rest publish.
                    if (fault == "close-denied" && mutation == "closeIssue" && input.GetProperty("issueId").GetString() == "I2")
                    { errors.Add(JsonSerializer.SerializeToNode(new { path = new[] { alias }, type = "FORBIDDEN", message = "Synthetic close failure" })); data[alias] = null; continue; }
                    // #231: 完了にする / 中止にする / 未完了に戻す / この版から外す.
                    if (mutation == "closeIssue")
                        row = row with { Closed = true, Cancelled = input.GetProperty("stateReason").GetString() == "NOT_PLANNED",
                            CloseDate = DateOnly.FromDateTime(DateTime.Now) };
                    else if (mutation == "reopenIssue") row = row with { Closed = false, Cancelled = false, CloseDate = null };
                    else if (mutation == "archiveProjectV2Item")
                    {
                        // GitHub requires the Project as well as the item; without it the input is rejected.
                        if (!input.TryGetProperty("projectId", out var project) || string.IsNullOrEmpty(project.GetString()))
                        { errors.Add(JsonSerializer.SerializeToNode(new { path = new[] { alias }, type = "INVALID", message = "Argument 'projectId' on InputObject 'ArchiveProjectV2ItemInput' is required." })); data[alias] = null; continue; }
                        issue = issue with { Archived = true }; selection = "item"; id = ItemId("T-" + identity);
                    }
                    else if (mutation is not ("updateIssue" or "addLabelsToLabelable" or "removeLabelsFromLabelable" or "updateProjectV2ItemPosition"
                        or "addAssigneesToAssignable" or "removeAssigneesFromAssignable" or "addBlockedBy" or "removeBlockedBy"
                        or "addSubIssue" or "removeSubIssue" or "reprioritizeSubIssue") && !mutation.Contains("ItemFieldValue"))
                        throw new InvalidOperationException("Unsupported fake mutation: " + mutation);
                    issues[index] = issue with { Row = row };
                    if (selection != "item") id = selection == "projectV2Item" ? ItemId("T-" + identity) : identity;
                }
                if (fault == "link-already-exists" && mutation is "addSubIssue" or "addBlockedBy")
                { errors.Add(JsonSerializer.SerializeToNode(new { path = new[] { alias }, type = "UNPROCESSABLE", message = "Relationship already exists" })); data[alias] = null; continue; }
                data[alias] = new JsonObject { [selection] = new JsonObject { ["id"] = id } };
            }
            if (state.MutationBatches == faultBatch)
            {
                if (fault == "removed-during-write") issues = issues.Select(i => i.Row.Identity == "I2" ? i with { Added = false } : i).ToList();
                if (fault == "summary-conflict-during-write") issues = issues.Select(i => i.Row.Identity == "I1" ? i with { Row = i.Row with { Estimate = 9 } } : i).ToList();
            }
            state = state with { Issues = issues.ToImmutableArray(), NextId = nextId, SubOrders = subOrders }; Save(root, state);
            // GitHub may apply the writes even when its gateway returns no GraphQL result.
            if (fault == "gateway" && matches.Count > scenario.GetProperty("gatewayAliasLimit").GetInt32())
            { Console.Write("HTTP/2 502 Bad Gateway\nContent-Type: text/html\n\n<html><body>Bad Gateway</body></html>"); return 1; }
            if (fault == "gateway-json" && matches.Count > scenario.GetProperty("gatewayAliasLimit").GetInt32())
            { Console.Write("HTTP/2 502 Bad Gateway\nContent-Type: application/json\n\n{\"message\":\"Server Error\"}"); return 1; }
            if (fault == "creation-resource-uncertain" && query.Contains("createIssue") && matches.Count > 1)
            { Write(new { data = (object?)null, errors = new[] { new { type = "RESOURCE_LIMITS_EXCEEDED", message = "Synthetic resource failure with an uncertain creation" } } }); return 0; }
            if (interruptedPositions) return 1;
            if (fault is "after" or "autoadd-after" or "after-and-read" or "uncertain-verification" && state.MutationBatches == faultBatch) return 1;
            if (fault is "field-pushback" or "window-retry-unknown" && state.MutationBatches <= 4)
            {
                var delay = 5 - state.MutationBatches;
                var payload = fault == "window-retry-unknown"
                    ? new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "Unknown outcome" }) }
                    : new JsonObject { ["data"] = data };
                Console.Write("HTTP/2 200 OK" +
                    "\nRetry-After: " + delay + "\n\n" + payload.ToJsonString());
                return 0;
            }
            Write(new JsonObject { ["data"] = data, ["errors"] = errors.Count == 0 ? null : errors }); return 0;
        }
        if (node is null) throw new InvalidOperationException("Unsupported fake query: " + query);
        Write(new { data = new { node, viewer = new { databaseId = 42 } } });
        await Task.CompletedTask; return 0;
    }
    private static void Write(object payload) => Console.Write("HTTP/2 200 OK\n\n" + JsonSerializer.Serialize(payload, Json));
}
