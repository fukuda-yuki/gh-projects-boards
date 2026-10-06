using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GhProjectsBoards.Core.PlanEditor;

namespace GhProjectsBoards.Tests;

internal sealed record PlanFakeIssue(PlanRow Row, string Body, bool Added) { public bool Archived { get; init; } }
internal sealed record PlanFakeState(ImmutableArray<PlanFakeIssue> Issues, int NextId, int MutationBatches = 0) { public int Drafts { get; init; } public int PullRequests { get; init; } public int ReadAttempts { get; init; } public ImmutableArray<string> AddedFields { get; init; } = []; public ImmutableDictionary<string, ImmutableArray<string>> SubOrders { get; init; } = ImmutableDictionary<string, ImmutableArray<string>>.Empty; }
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
        if ((fault == "readfailure" || fault is "after-and-read" or "before-and-read" && state.MutationBatches > 0) && query.Contains("ProjectItems")) { Console.Write("HTTP/2 500 Failed\n\n{}"); return 1; }
        if (query.Contains("ProjectFields")) { state = state with { ReadAttempts = state.ReadAttempts + 1 }; Save(root, state); }
        if ((fault == "verificationfailure" && state.MutationBatches >= faultBatch ||
            fault == "uncertain-verification" && state.MutationBatches >= faultBatch) && query.Contains("ProjectItems"))
        { Console.Write("HTTP/2 500 Failed\n\n{}"); return 1; }
        if (fault == "uncertain-verification" && state.MutationBatches >= faultBatch && query.Contains("PlanCreationGuard"))
        { Console.Write("HTTP/2 500 Failed\n\n{}"); return 1; }
        if (fault == "read-once" && state.ReadAttempts == 1 && query.Contains("ProjectItems"))
        { Console.Write("HTTP/2 500 Failed\n\n{}"); return 1; }
        var workspace = scenario.TryGetProperty("workspace", out var ws) && ws.GetBoolean();
        if (workspace && query.Contains("Registration"))
        {
            object Choice(string projectId, string owner) => new { id = projectId, number = projectId == "P1" ? 3 : 4,
                url = "https://github.com/" + (owner == "fixture-user" ? "users/" : "orgs/") + owner + "/projects/" + (projectId == "P1" ? "3" : "4"),
                title = projectId == "P1" ? "開発計画" : "運用計画", owner = new { id = "O1", __typename = owner == "fixture-user" ? "User" : "Organization", login = owner } };
            if (query.Contains("RegistrationOwners")) Write(new { data = new { viewer = new { organizations = Page([new { login = "acme" }], 1) } } });
            else if (query.Contains("RegistrationProjects"))
            {
                var owner = variables.GetProperty("owner").GetString()!;
                Write(new { data = new { repositoryOwner = new { projectsV2 = Page([Choice(owner == "fixture-user" ? "P1" : "P2", owner)], 1) } } });
            }
            else if (query.Contains("RegistrationResolve"))
                Write(new { data = new { user = new { projectV2 = Choice("P1", "fixture-user") } } });
            else Write(new { data = new { node = new { repositories = Page([new { id = "R1", nameWithOwner = "acme/repo", owner = new { id = "O1" } }], 1) } } });
            return 0;
        }
        var projectId = workspace && variables.TryGetProperty("id", out var projectNode) && projectNode.GetString() == "P2" ? "P2" : "P1";
        var fieldRoles = new[] { PlanField.Estimate, PlanField.Remaining, PlanField.Actual, PlanField.Start, PlanField.End, PlanField.StartNoEarlierThan, PlanField.Fixed, PlanField.Status };
        string Type(PlanField role) => role is PlanField.Estimate or PlanField.Remaining or PlanField.Actual ? "NUMBER" : role is PlanField.Fixed or PlanField.Status ? "SINGLE_SELECT" : "DATE";
        object Field(PlanField role) => new { __typename = role is PlanField.Fixed or PlanField.Status ? "ProjectV2SingleSelectField" : "ProjectV2Field", id = "F-" + role, name = workspace && role == PlanField.Start ? "Start date" : workspace && role == PlanField.End ? "Target date" : state.AddedFields.Contains(role.ToString()) ? role == PlanField.Fixed ? "日程固定" : "開始日指定" : role.ToString(), dataType = Type(role), isIssueField = false, project = new { id = projectId }, options = role == PlanField.Status ? new[] { new { id = "progress", name = "In progress" }, new { id = "done", name = "Done" }, new { id = "backlog", name = "Backlog" } } : new[] { new { id = "fixed", name = "固定" } } };
        object Value(PlanRow row, PlanField role)
        {
            var scalar = JsonNode.Parse(PlanValues.Get(row, role));
            var value = new JsonObject { ["__typename"] = role is PlanField.Fixed or PlanField.Status ? "ProjectV2ItemFieldSingleSelectValue" : Type(role) == "NUMBER" ? "ProjectV2ItemFieldNumberValue" : "ProjectV2ItemFieldDateValue",
                ["id"] = row.Identity + "-" + role, ["field"] = JsonSerializer.SerializeToNode(new { id = "F-" + role, project = new { id = projectId } }) };
            value[role is PlanField.Fixed or PlanField.Status ? "optionId" : Type(role) == "NUMBER" ? "number" : "date"] = role == PlanField.Fixed ? JsonValue.Create("fixed") : role == PlanField.Status ? JsonValue.Create(row.Status == "Done" ? "done" : row.Status == "Backlog" ? "backlog" : "progress") : scalar;
            return value;
        }
        object Item(PlanFakeIssue issue)
        {
            var row = issue.Row;
            return new { __typename = "ProjectV2Item", id = "T-" + row.Identity, type = "ISSUE", isArchived = issue.Archived, project = new { id = projectId },
                content = new { __typename = "Issue", id = row.Identity, number = int.Parse(row.Identity[1..]), url = "https://github.com/" + row.Repository + "/issues/" + row.Identity[1..], title = row.Title,
                    state = row.Closed ? "CLOSED" : "OPEN", viewerCanUpdate = true, repository = new { id = row.Repository == "acme/other" ? "R2" : "R1", nameWithOwner = row.Repository, owner = new { id = "O1" } },
                    assignees = Page(row.Assignees.Select(id => (object)new { id, login = workspace ? "person-" + id : id }), row.Assignees.Length),
                    blockedBy = Page(row.Predecessors.Select(id => (object)new { id }), row.Predecessors.Length),
                    subIssues = Page(state.SubOrders.GetValueOrDefault(row.Identity, state.Issues.Where(i => i.Row.Parent == row.Identity).Select(i => i.Row.Identity).ToImmutableArray()).Select(id => (object)new { id }), state.Issues.Count(i => i.Row.Parent == row.Identity)),
                    parent = row.Parent is null ? null : new { id = row.Parent } },
                fieldValues = Page(fieldRoles.Where(f => PlanValues.Get(row, f) is not ("null" or "false")).Select(f => Value(row, f)), fieldRoles.Count(f => PlanValues.Get(row, f) is not ("null" or "false"))) };
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
            node = new { projectItems = Page(visible ? [new { id = "T-" + id, project = new { id = projectId } }] : [], visible ? 1 : 0) };
        }
        else if (query.Contains("ProjectFields"))
            node = new { __typename = "ProjectV2", id = projectId, number = 3, url = "https://github.com/users/acme/projects/3", title = "Plan", viewerCanUpdate = true,
                owner = new { __typename = "User", id = "O1" }, fields = Page(fieldRoles.Select(Field), fieldRoles.Length) };
        else if (query.Contains("ProjectItems"))
        {
            var all = state.Issues.Where(i => i.Added && !(fault == "membership-delay" && state.MutationBatches is > 0 and <= 4 && state.ReadAttempts < 2 && int.Parse(i.Row.Identity[1..]) % 2 == 0)).ToArray();
            var offset = variables.TryGetProperty("after", out var cursor) && cursor.ValueKind == JsonValueKind.String ? int.Parse(cursor.GetString()!) : 0;
            object Excluded(string id, string kind, string type) => new { __typename = "ProjectV2Item", id, type, isArchived = false,
                project = new { id = projectId }, content = new { __typename = kind, id = "content-" + id }, fieldValues = Page([], 0) };
            var nodes = all.Select(Item).Concat(Enumerable.Range(0, state.Drafts).Select(i => Excluded("D" + i, "DraftIssue", "DRAFT_ISSUE")))
                .Concat(Enumerable.Range(0, state.PullRequests).Select(i => Excluded("PR" + i, "PullRequest", "PULL_REQUEST"))).ToArray();
            node = new { __typename = "ProjectV2", id = projectId, items = Page(nodes.Skip(offset).Take(100), nodes.Length + (offset == 0 &&
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
            if (fault is "before" or "before-and-read" && state.MutationBatches == faultBatch) { Save(root, state); return 1; }
            var interruptedPositions = fault == "position-batch-interrupted" && query.Contains("updateProjectV2ItemPosition") && matches.Count > 1;
            foreach (Match match in matches)
            {
                if (interruptedPositions && data.Count >= 2 || fault == "creation-resource-uncertain" && query.Contains("createIssue") && matches.Count > 1 && data.Count >= 1) break;
                var alias = match.Groups[1].Value; var mutation = match.Groups[2].Value; var input = variables.GetProperty(match.Groups[3].Value);
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
                    issues.RemoveAt(index); issues.Add(addedIssue); id = "T-" + issueId; selection = "item";
                }
                else
                {
                    var identity = input.TryGetProperty("itemId", out var itemId) ? itemId.GetString()![2..] :
                        input.TryGetProperty("id", out var issueId) ? issueId.GetString()! : input.TryGetProperty("assignableId", out var assignable) ? assignable.GetString()! : input.GetProperty("issueId").GetString()!;
                    var index = issues.FindIndex(i => i.Row.Identity == identity); var issue = issues[index]; var row = issue.Row;
                    if (mutation == "updateIssue") row = row with { Title = input.GetProperty("title").GetString()! };
                    else if (mutation.Contains("ItemFieldValue"))
                    {
                        var role = Enum.Parse<PlanField>(input.GetProperty("fieldId").GetString()![2..]);
                        var json = mutation.StartsWith("clear") ? role == PlanField.Fixed ? "false" : "null" : role == PlanField.Fixed ? "true" : input.GetProperty("value").EnumerateObject().Single().Value.GetRawText();
                        if (role == PlanField.Status && !mutation.StartsWith("clear"))
                            json = JsonSerializer.Serialize(input.GetProperty("value").GetProperty("singleSelectOptionId").GetString() switch {
                                "done" => "Done", "progress" => "In progress", "backlog" => "Backlog", _ => throw new InvalidOperationException("Unknown Status option ID") });
                        if (fault != "mismatch") row = PlanValues.Set(row, role, json);
                        selection = "projectV2Item";
                    }
                    else if (mutation == "updateProjectV2ItemPosition")
                    {
                        issues.RemoveAt(index);
                        var afterId = input.GetProperty("afterId");
                        var place = afterId.ValueKind == JsonValueKind.Null ? 0 : issues.FindIndex(i => "T-" + i.Row.Identity == afterId.GetString()) + 1;
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
                    else throw new InvalidOperationException("Unsupported fake mutation: " + mutation);
                    issues[index] = issue with { Row = row }; id = selection == "projectV2Item" ? "T-" + identity : identity;
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
            if (fault == "creation-resource-uncertain" && query.Contains("createIssue") && matches.Count > 1)
            { Write(new { data = (object?)null, errors = new[] { new { type = "RESOURCE_LIMITS_EXCEEDED", message = "Synthetic resource failure with an uncertain creation" } } }); return 0; }
            if (interruptedPositions) return 1;
            if (fault is "after" or "autoadd-after" or "after-and-read" or "uncertain-verification" && state.MutationBatches == faultBatch) return 1;
            Write(new JsonObject { ["data"] = data, ["errors"] = errors.Count == 0 ? null : errors }); return 0;
        }
        if (node is null) throw new InvalidOperationException("Unsupported fake query: " + query);
        Write(new { data = new { node, viewer = new { databaseId = 42 } } });
        await Task.CompletedTask; return 0;
    }
    private static void Write(object payload) => Console.Write("HTTP/2 200 OK\n\n" + JsonSerializer.Serialize(payload, Json));
}
