using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
[Category("Integration")]
internal sealed class ProjectReaderTests
{
    private static readonly ConnectionScope Scope = new("github.com", 42);

    [Test]
    public async Task SameIssueAcrossProjectsKeepsOneIdentityAndIndependentStatus()
    {
        var boundary = new ProjectBoundary();
        var service = new GhConnectionService("gh.exe", Scope.Host, boundary.Runner);
        var context = (await service.ConnectAsync()).Context!;
        var reader = new ProjectReader(service);
        var first = await reader.ReadAsync(context, new(Scope, "P1"));
        var second = await reader.ReadAsync(context, new(Scope, "P2"));
        Assert.Multiple(() =>
        {
            Assert.That(first.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
            Assert.That(second.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
        });
        var firstIssue = first.Project!.Issues.Values.Single();
        var secondIssue = second.Project!.Issues.Values.Single();
        Assert.Multiple(() =>
        {
            Assert.That(firstIssue.Id, Is.EqualTo(secondIssue.Id));
            Assert.That(firstIssue.Repository.Id.NodeId, Is.EqualTo("R1"));
            Assert.That(firstIssue.State.Value, Is.EqualTo(IssueState.Open));
            Assert.That(firstIssue.Title.Value, Is.EqualTo("Synthetic issue"));
            Assert.That(first.Project.Items.Single().ContentId, Is.EqualTo(firstIssue.Id));
            Assert.That(first.Project.Items.Single().Values.Single().OptionId, Is.EqualTo("todo"));
            Assert.That(second.Project.Items.Single().Values.Single().OptionId, Is.EqualTo("done"));
            Assert.That(first.Project.Fields.Single().Id, Is.Not.EqualTo(second.Project.Fields.Single().Id));
        });
        boundary.AssertQueriesOnly();
    }


    [Test]
    public async Task TraversesMoreThan100ItemsFieldsAndNestedValues()
    {
        var boundary = new ProjectBoundary();
        boundary.Override = (query, variables) =>
        {
            var after = variables.GetProperty("after").ValueKind == JsonValueKind.Null ? null : variables.GetProperty("after").GetString();
            if (query.Contains("ProjectFields"))
                return Response(Project("P1", "fields", after is null
                    ? Page(Enumerable.Range(1, 100).Select(n => Field("P1", "F" + n)).ToArray(), 101, true, "fields-next")
                    : Page([Field("P1", "F101")], 101)));
            if (query.Contains("ProjectItems"))
                return Response(Project("P1", "items", after is null
                    ? Page(Enumerable.Range(1, 100).Select(n => Item("P1", "T" + n,
                        n == 1 ? Page(Enumerable.Range(1, 100).Select(f => Value("P1", "F" + f)).ToArray(), 101, true, "values-next")
                            : Page([], 0), Issue("I" + n, n))).ToArray(), 101, true, "items-next")
                    : Page([Item("P1", "T101", Page([], 0), Issue("I101", 101))], 101)));
            if (query.Contains("ProjectItemValues"))
            {
                Assert.That(variables.GetProperty("id").GetString(), Is.EqualTo("T1"));
                Assert.That(after, Is.EqualTo("values-next"));
                return Response(new { __typename = "ProjectV2Item", id = "T1", project = new { id = "P1" },
                    fieldValues = Page([Value("P1", "F101")], 101) });
            }
            return null;
        };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), result.ToString());
        Assert.Multiple(() =>
        {
            Assert.That(result.Project!.Items, Has.Count.EqualTo(101));
            Assert.That(result.Project.Fields, Has.Count.EqualTo(101));
            Assert.That(result.Project.Issues, Has.Count.EqualTo(101));
            Assert.That(result.Project.Items[0].Values.Count(v => v.Availability == ValueAvailability.Present), Is.EqualTo(101));
            Assert.That(result.Project.Items[100].Values.All(v => v.Availability == ValueAvailability.Empty), Is.True);
        });
        boundary.AssertQueriesOnly();
    }

    [Test]
    public async Task SameDisplayNameDoesNotConfuseOwnershipOrFieldIds()
    {
        var boundary = new ProjectBoundary { Override = (query, _) => query.Contains("ProjectFields")
            ? Response(Project("P1", "fields", Page([
                Field("P1", "P1-status"), Field("P1", "custom", issueField: true),
                Field("P1", "title", name: "Status", type: "TITLE")], 3)))
            : null };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
        Assert.Multiple(() =>
        {
            Assert.That(result.Project!.Fields.Select(f => f.Name), Is.All.EqualTo("Status"));
            Assert.That(result.Project.Fields.Select(f => f.Id.NodeId), Is.EquivalentTo(new[] { "P1-status", "custom", "title" }));
            Assert.That(result.Project.Fields.Select(f => f.ValueOwner),
                Is.EqualTo(new[] { FieldOwner.ProjectItem, FieldOwner.Issue, FieldOwner.Issue }));
            Assert.That(result.Project.Items.Single().Values.Single(v => v.FieldId!.NodeId == "custom").Availability,
                Is.EqualTo(ValueAvailability.Unsupported));
            Assert.That(result.Project.Issues.Values.Single().Title.Value, Is.EqualTo("Synthetic issue"));
        });
    }

    [Test]
    public async Task EmptyUnsupportedUnavailableAndUnloadedValuesRemainDifferent()
    {
        var boundary = new ProjectBoundary();
        boundary.Override = (query, _) =>
        {
            if (query.Contains("ProjectFields"))
                return Response(Project("P1", "fields", Page([Field("P1", "empty"), Field("P1", "text", type: "TEXT"),
                    Field("P1", "unknown-option"), Field("P1", "unloaded")], 4)));
            if (query.Contains("ProjectItems"))
                return Response(Project("P1", "items", Page([Item("P1", values: Page([
                    Value("P1", "empty", null), Value("P1", "unknown-option", "unrecognized")], 2))], 1)));
            return null;
        };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial));
        var values = result.Project!.Items.Single().Values.ToDictionary(v => v.FieldId!.NodeId);
        Assert.Multiple(() =>
        {
            Assert.That(values["text"].Availability, Is.EqualTo(ValueAvailability.Unsupported));
            Assert.That(values["unknown-option"].Availability, Is.EqualTo(ValueAvailability.Unavailable));
            Assert.That(values["unknown-option"].OptionId, Is.EqualTo("unrecognized"));
            Assert.That(values["unloaded"].Availability, Is.EqualTo(ValueAvailability.NotLoaded));
            Assert.That(values["empty"].Availability, Is.EqualTo(ValueAvailability.Unavailable), "Partial results do not infer clearing.");
        });
        boundary.Override = (query, _) => query.Contains("ProjectItems")
            ? Response(Project("P1", "items", Page([Item("P1", values: Page([Value("P1", "P1-status", null)], 1))], 1))) : null;
        var complete = await Read(boundary);
        Assert.That(complete.Project!.Items.Single().Values.Single().Availability, Is.EqualTo(ValueAvailability.Empty));
    }

    [Test]
    public async Task PullRequestsDraftsAndRedactedContentAreNotOrdinaryIssues()
    {
        var redacted = new { __typename = "ProjectV2Item", id = "redacted", type = "REDACTED", isArchived = true,
            project = new { id = "P1" }, content = (object?)null, fieldValues = Page([], 0) };
        var boundary = new ProjectBoundary { Override = (query, _) => query.Contains("ProjectItems")
            ? Response(Project("P1", "items", Page([
                Item("P1", "pr", Page([], 0), new { __typename = "PullRequest", id = "PR1" }, "PULL_REQUEST"),
                Item("P1", "draft", Page([], 0), new { __typename = "DraftIssue", id = "D1" }, "DRAFT_ISSUE"),
                redacted], 3))) : null };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
        Assert.Multiple(() =>
        {
            Assert.That(result.Project!.Issues, Is.Empty);
            Assert.That(result.Project.Items.Select(i => i.Kind),
                Is.EqualTo(new[] { ProjectItemKind.PullRequest, ProjectItemKind.Draft, ProjectItemKind.Unavailable }));
            Assert.That(result.Project.Items[2].Values.Single().Availability, Is.EqualTo(ValueAvailability.Unavailable));
            Assert.That(result.Project.Items[2].IsArchived, Is.True);
            Assert.That(result.Project.Items[0].ContentId!.NodeId, Is.EqualTo("PR1"));
            Assert.That(result.Project.Items[1].ContentId!.NodeId, Is.EqualTo("D1"));
        });
    }

    [TestCase("field")]
    [TestCase("option")]
    [TestCase("item")]
    [TestCase("issue")]
    [TestCase("value-id")]
    [TestCase("value-field")]
    public async Task DuplicateIdentitiesRejectCompleteness(string duplicate)
    {
        var boundary = new ProjectBoundary { Override = (query, _) =>
        {
            if (query.Contains("ProjectFields") && duplicate is "field" or "option")
                return Response(Project("P1", "fields", duplicate == "field"
                    ? Page([Field("P1", "F"), Field("P1", "F")], 2)
                    : Page([Field("P1", "F", options: [new { id = "x", name = "a" }, new { id = "x", name = "b" }])], 1)));
            if (query.Contains("ProjectItems"))
                return Response(Project("P1", "items", duplicate switch
                {
                    "item" => Page([Item("P1"), Item("P1")], 2),
                    "issue" => Page([Item("P1"), Item("P1", "second")], 2),
                    "value-id" => Page([Item("P1", values: Page([Value("P1", "P1-status", id: "V"),
                        Value("P1", "different-field", id: "V")], 2))], 1),
                    "value-field" => Page([Item("P1", values: Page([Value("P1", "P1-status", id: "V1"),
                        Value("P1", "P1-status", id: "V2")], 2))], 1),
                    _ => Page([], 0)
                }));
            return null;
        }};
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial));
        Assert.That(result.Problems.Select(p => p.Kind), Does.Contain(ReadProblemKind.DuplicateIdentity));
        Assert.That(boundary.Runner.Commands.Count, Is.LessThan(30));
    }

    [TestCase("fields")]
    [TestCase("items")]
    [TestCase("values")]
    public async Task RepeatedCursorsStopWithoutOverwritingEarlierData(string stage)
    {
        var pageNumber = 0;
        var boundary = new ProjectBoundary { Override = (query, _) =>
        {
            var match = stage switch { "fields" => query.Contains("ProjectFields"), "items" => query.Contains("ProjectItems"),
                _ => query.Contains("ProjectItems") || query.Contains("ProjectItemValues") };
            if (!match) return null;
            pageNumber++;
            if (stage == "fields")
                return Response(Project("P1", "fields", Page([Field("P1", "F" + pageNumber)], 10, true, "repeated")));
            if (stage == "items")
                return Response(Project("P1", "items", Page([Item("P1", "T" + pageNumber, Page([], 0), Issue("I" + pageNumber, pageNumber))], 10, true, "repeated")));
            var values = Page([Value("P1", "F" + pageNumber)], 10, true, "repeated");
            return query.Contains("ProjectItems") ? Response(Project("P1", "items", Page([Item("P1", values: values)], 1)))
                : Response(new { __typename = "ProjectV2Item", id = "P1-item", project = new { id = "P1" }, fieldValues = values });
        }};
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial));
        Assert.That(result.Problems.Select(p => p.Kind), Does.Contain(ReadProblemKind.RepeatedCursor));
        Assert.That(pageNumber, Is.EqualTo(2));
    }

    [TestCase("missing-cursor")]
    [TestCase("null-node")]
    [TestCase("short-total")]
    [TestCase("changed-total")]
    [TestCase("missing-page-info")]
    public async Task IncompleteTraversalNeverBecomesCompleteOrEmpty(string defect)
    {
        var pageNumber = 0;
        var boundary = new ProjectBoundary { Override = (query, _) =>
        {
            if (!query.Contains("ProjectItems")) return null;
            pageNumber++;
            object page = defect switch
            {
                "missing-cursor" => Page([Item("P1")], 2, true),
                "null-node" => Page([null], 1),
                "short-total" => Page([Item("P1")], 2),
                "changed-total" when pageNumber == 1 => Page([Item("P1")], 2, true, "next"),
                "changed-total" => Page([Item("P1", "T2", Page([], 0), Issue("I2", 2))], 3),
                _ => new { nodes = new[] { Item("P1") }, totalCount = 1 }
            };
            return Response(Project("P1", "items", page));
        }};
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial));
        Assert.That(result.Project!.ItemsComplete, Is.False);
        Assert.That(result.Problems, Is.Not.Empty);
    }

    [TestCase("fields")]
    [TestCase("items")]
    [TestCase("values")]
    public async Task LaterPageFailureRetainsDataAndFailureClassification(string stage)
    {
        var boundary = new ProjectBoundary { Override = (query, variables) =>
        {
            var after = variables.GetProperty("after");
            if (after.ValueKind == JsonValueKind.String) return ScriptedRunner.Http("{}", 503, 1);
            if (query.Contains("ProjectFields") && stage == "fields")
                return Response(Project("P1", "fields", Page([Field("P1", "P1-status")], 2, true, "next")));
            if (query.Contains("ProjectItems"))
                return Response(Project("P1", "items", stage == "items"
                    ? Page([Item("P1")], 2, true, "next")
                    : Page([Item("P1", values: Page([Value("P1", "P1-status")], 2, true, "next"))], 1)));
            return null;
        }};
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial));
        Assert.That(result.Project!.Fields, Has.Count.EqualTo(1));
        Assert.That(result.Problems.Any(p => p.Failure == FailureKind.Network && p.HttpStatus == 503 && p.Stage == stage), Is.True);
        if (stage != "fields")
        {
            Assert.That(result.Project.Items, Has.Count.EqualTo(1));
            Assert.That(result.Project.Items.Single().Values.Single().OptionId, Is.EqualTo("todo"));
        }
        boundary.AssertQueriesOnly();
    }

    [Test]
    public async Task GraphQlPartialDataIsRetainedWithoutTreatingMissingValuesAsEmpty()
    {
        var boundary = new ProjectBoundary { Override = (query, _) => query.Contains("ProjectItems")
            ? Response(Project("P1", "items", Page([Item("P1", values: Page([], 0))], 1)), errors: true) : null };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial));
        Assert.That(result.Project!.Items.Single().Values.Single().Availability, Is.EqualTo(ValueAvailability.NotLoaded));
        Assert.That(result.Project.ItemsComplete, Is.False);
        Assert.That(result.ToString(), Does.Not.Contain("synthetic-private-error"));
        Assert.That(result.Problems.Single().Failure, Is.EqualTo(FailureKind.GraphQl));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellationStopsAndRetainsOnlyObservedData(bool duringRead)
    {
        using var cancellation = new CancellationTokenSource();
        var boundary = new ProjectBoundary { Override = (query, _) =>
        {
            if (query.Contains("ProjectFields") && duringRead) cancellation.Cancel();
            return null;
        }};
        if (!duringRead) cancellation.Cancel();
        var result = await Read(boundary, cancellation.Token);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Cancelled));
        Assert.That(result.Project?.Items ?? [], Is.Empty);
        Assert.That(boundary.Runner.Commands.Count(c => c.Arguments.Contains("graphql")), Is.EqualTo(duringRead ? 1 : 0));
    }

    [TestCase("scope-host")]
    [TestCase("scope-account")]
    [TestCase("service-host")]
    [TestCase("account-change")]
    [TestCase("returned-project")]
    public async Task HostAndAccountIsolationSurviveEveryRead(string mismatch)
    {
        var boundary = new ProjectBoundary();
        var service = new GhConnectionService("gh.exe", Scope.Host, boundary.Runner);
        var context = (await service.ConnectAsync()).Context!;
        boundary.Runner.Commands.Clear();
        var id = new ScopedId(mismatch switch { "scope-host" => new("other.example", 42),
            "scope-account" => new(Scope.Host, 99), _ => Scope }, "P1");
        if (mismatch == "service-host") service = new GhConnectionService("gh.exe", "other.example", boundary.Runner);
        if (mismatch == "account-change") boundary.ViewerId = 99;
        if (mismatch == "returned-project") boundary.Override = (_, _) => Response(Project("P2", "fields", Page([], 0)));
        var result = await new ProjectReader(service).ReadAsync(context, id);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Failed));
        Assert.That(result.Project, Is.Null);
        Assert.That(boundary.Runner.Commands.Count(c => c.Arguments.Contains("graphql")), Is.EqualTo(mismatch == "returned-project" ? 1 : 0));
        if (mismatch is "account-change" or "service-host") Assert.That(context.IsInvalidated, Is.True);
    }

    [Test]
    public async Task IdentityRecheckedBeforeEveryPageAndAccountChangeStopsTraversal()
    {
        var boundary = new ProjectBoundary();
        boundary.Override = (query, _) =>
        {
            if (!query.Contains("ProjectItems")) return null;
            boundary.ViewerId = 99;
            return Response(Project("P1", "items", Page([Item("P1")], 2, true, "next")));
        };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial));
        Assert.That(result.Problems.Any(p => p.Failure == FailureKind.IdentityChanged), Is.True);
        var calls = boundary.Runner.Commands.Skip(3).Select(c => c.Arguments[0] == "auth" ? "auth" : c.Arguments[1]).ToArray();
        Assert.That(calls, Is.EqualTo(new[] { "auth", "user", "graphql", "auth", "user", "graphql", "auth", "user" }));
    }

    [TestCase(403, FailureKind.PermissionDenied)]
    [TestCase(404, FailureKind.NotFoundOrInaccessible)]
    [TestCase(429, FailureKind.RateLimited)]
    public async Task InitialFailureHasNoInventedSnapshot(int status, FailureKind failure)
    {
        var boundary = new ProjectBoundary { Override = (_, _) => ScriptedRunner.Http("{}", status, 1) };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Failed));
        Assert.That(result.Project, Is.Null);
        Assert.That(result.Problems.Single().Failure, Is.EqualTo(failure));
    }

    [Test]
    public async Task TimeoutRemainsDistinctFromFailureWithAvailableData()
    {
        var boundary = new ProjectBoundary { Override = (query, _) => query.Contains("ProjectItems")
            ? new(ProcessCompletion.TimedOut, true) : null };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.TimedOut));
        Assert.That(result.Project!.Fields, Has.Count.EqualTo(1));
        Assert.That(result.Project.ItemsComplete, Is.False);
    }


    [TestCase("CREATED")]
    [TestCase("CLOSED")]
    [TestCase("UPDATED")]
    [TestCase("PARENT_ISSUE")]
    [TestCase("SUB_ISSUES_PROGRESS")]
    [TestCase("TRACKED_BY")]
    [TestCase("TRACKS")]
    public async Task IssueDerivedFieldsKeepIssueOwnershipEvenWhenValuesAreUnsupported(string type)
    {
        var boundary = new ProjectBoundary { Override = (query, _) => query.Contains("ProjectFields")
            ? Response(Project("P1", "fields", Page([Field("P1", "F", type: type)], 1)))
            : query.Contains("ProjectItems") ? Response(Project("P1", "items", Page([], 0))) : null };
        var result = await Read(boundary);
        Assert.That(result.Project!.Fields.Single().ValueOwner, Is.EqualTo(FieldOwner.Issue));
        Assert.That(result.Project.Fields.Single().Availability, Is.EqualTo(ValueAvailability.Unsupported));
    }

    [Test]
    public async Task UnknownItemWithoutContentIsExplicitlyUnsupported()
    {
        var boundary = new ProjectBoundary { Override = (query, _) => query.Contains("ProjectItems")
            ? Response(Project("P1", "items", Page([new { __typename = "ProjectV2Item", id = "T1", type = "FUTURE_KIND",
                isArchived = false, project = new { id = "P1" }, content = (object?)null, fieldValues = Page([], 0) }], 1))) : null };
        var result = await Read(boundary);
        Assert.That(result.Project!.Items.Single().Kind, Is.EqualTo(ProjectItemKind.Unsupported));
        Assert.That(result.Project.Items.Single().Values.Single().Availability, Is.EqualTo(ValueAvailability.NotLoaded));
    }


    [Test]
    public async Task IncompleteIssueMetadataDoesNotLeaveAnOrdinaryIssueWithADanglingReference()
    {
        var boundary = new ProjectBoundary { Override = (query, _) => query.Contains("ProjectItems")
            ? Response(Project("P1", "items", Page([Item("P1", content: new { __typename = "Issue", id = "I1",
                number = 1, url = "https://github.com/example/repository/issues/1", title = "Synthetic", state = "OPEN",
                repository = (object?)null })], 1))) : null };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial));
        Assert.That(result.Project!.Issues, Is.Empty);
        Assert.That(result.Project.Items.Single().Kind, Is.EqualTo(ProjectItemKind.Unavailable));
        Assert.That(result.Project.Items.Single().ContentId!.NodeId, Is.EqualTo("I1"));
    }

    [Test]
    public async Task DuplicateValueNodeAcrossDifferentItemsIsDetected()
    {
        var boundary = new ProjectBoundary { Override = (query, _) => query.Contains("ProjectItems")
            ? Response(Project("P1", "items", Page([
                Item("P1", "T1", Page([Value("P1", "P1-status", id: "shared-value")], 1), Issue("I1", 1)),
                Item("P1", "T2", Page([Value("P1", "P1-status", id: "shared-value")], 1), Issue("I2", 2))], 2))) : null };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial));
        Assert.That(result.Problems.Any(p => p.Kind == ReadProblemKind.DuplicateIdentity), Is.True);
    }

    private static async Task<ProjectReadResult> Read(ProjectBoundary boundary, CancellationToken cancellation = default)
    {
        var service = new GhConnectionService("gh.exe", Scope.Host, boundary.Runner);
        var context = (await service.ConnectAsync()).Context!;
        return await new ProjectReader(service).ReadAsync(context, new(Scope, "P1"), cancellation);
    }

    internal sealed class ProjectBoundary
    {
        public ScriptedRunner Runner { get; }
        public Func<string, JsonElement, GhProcessResult?>? Override { get; set; }
        public Action<System.Text.Json.Nodes.JsonNode>? ChangeCombinedResponse { get; set; }
        public Action<string, System.Text.Json.Nodes.JsonNode>? ChangeObservationResponse { get; set; }
        public Func<GhCommand, GhProcessResult?>? OverrideProcess { get; set; }
        public long ViewerId { get; set; } = 42;
        public ProjectBoundary()
        {
            Runner = new ScriptedRunner(command =>
            {
                if (OverrideProcess?.Invoke(command) is { } overridden) return overridden;
                if (command.Arguments[0] == "--version")
                    return new(ProcessCompletion.Exited, true, 0, "gh version 2.100.0");
                if (command.Arguments[0] == "auth") return GhConnectionTests.Auth();
                if (command.Arguments[1] == "user")
                    return ScriptedRunner.Http(JsonSerializer.Serialize(new { id = ViewerId, login = "sample-user" }));
                using var json = JsonDocument.Parse(command.StandardInput!);
                var query = json.RootElement.GetProperty("query").GetString()!;
                var variables = json.RootElement.GetProperty("variables");
                if (query.Contains("ApplyObservation"))
                {
                    var fields = Respond(ProjectQueries.Fields, JsonSerializer.SerializeToElement(new { id = variables.GetProperty("id").GetString(), after = (string?)null }));
                    if (!fields.StandardOutput.StartsWith("HTTP/2.0 200")) return fields;
                    var project = Body(fields);
                    if (project["errors"] is System.Text.Json.Nodes.JsonArray { Count: > 0 }) return fields;
                    var itemResult = Respond(ProjectQueries.ApplyItem, JsonSerializer.SerializeToElement(new { id = variables.GetProperty("item").GetString() }));
                    if (!itemResult.StandardOutput.StartsWith("HTTP/2.0 200")) return itemResult;
                    var item = Body(itemResult);
                    if (item["errors"] is System.Text.Json.Nodes.JsonArray { Count: > 0 }) return itemResult;
                    var combined = new System.Text.Json.Nodes.JsonObject { ["data"] = new System.Text.Json.Nodes.JsonObject {
                        ["project"] = project["data"]!["node"]?.DeepClone(), ["item"] = item["data"]!["node"]?.DeepClone() } };
                    if (query.Contains("viewer { databaseId }")) combined["data"]!["viewer"] = JsonSerializer.SerializeToNode(new { databaseId = ViewerId });
                    ChangeCombinedResponse?.Invoke(combined);
                    ChangeObservationResponse?.Invoke(query, combined);
                    return ScriptedRunner.Http(combined.ToJsonString());
                }
                var response = Respond(query, variables);
                if (query.Contains("viewer { databaseId }") && response.StandardOutput.StartsWith("HTTP/2.0 200"))
                {
                    var data = Body(response);
                    if (data["data"] is System.Text.Json.Nodes.JsonObject fields)
                    {
                        fields["viewer"] = JsonSerializer.SerializeToNode(new { databaseId = ViewerId });
                        ChangeObservationResponse?.Invoke(query, data);
                        return ScriptedRunner.Http(data.ToJsonString());
                    }
                }
                return response;
            });
            static System.Text.Json.Nodes.JsonNode Body(GhProcessResult response) => System.Text.Json.Nodes.JsonNode.Parse(
                response.StandardOutput[(response.StandardOutput.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..])!;
            GhProcessResult Respond(string query, JsonElement variables)
            {
                var custom = Override?.Invoke(query, variables);
                if (custom is not null) return custom;
                var id = variables.GetProperty("id").GetString()!;
                if (query.Contains("ProjectFields"))
                    return Response(Project(id, "fields", Page([Field(id, id + "-status")], 1)));
                if (query.Contains("ProjectItems"))
                    return Response(Project(id, "items", Page([Item(id)], 1)));
                throw new AssertionException("Unexpected query operation.");
            }
        }
        public void AssertQueriesOnly()
        {
            foreach (var command in Runner.Commands.Where(c => c.Arguments[0] == "api"))
            {
                Assert.That(command.Arguments[command.Arguments.ToList().IndexOf("--hostname") + 1], Is.EqualTo(Scope.Host));
                if (command.Arguments[1] == "user") { Assert.That(command.Arguments, Does.Contain("GET")); continue; }
                Assert.That(command.Arguments[1], Is.EqualTo("graphql"));
                using var json = JsonDocument.Parse(command.StandardInput!);
                Assert.That(json.RootElement.GetProperty("query").GetString()!.TrimStart(), Does.StartWith("query"));
                Assert.That(json.RootElement.GetProperty("query").GetString(), Does.Not.Contain("mutation"));
            }
        }
    }

    internal static GhProcessResult Response(object? node, bool errors = false)
        => ScriptedRunner.Http(JsonSerializer.Serialize(new { data = new { node },
            errors = errors ? new[] { new { type = "FORBIDDEN", message = "synthetic-private-error" } } : [] }));
    internal static Dictionary<string, object?> Project(string id, string connection, object page)
        => new() { ["__typename"] = "ProjectV2", ["id"] = id, ["number"] = id == "P1" ? 1 : 2,
            ["title"] = "Synthetic project", ["url"] = "https://github.com/users/sample-user/projects/1",
            ["owner"] = new { __typename = "User", id = "O1" }, [connection] = page };
    internal static object Page(object?[] nodes, int total, bool next = false, string? cursor = null)
        => new { nodes, totalCount = total, pageInfo = new { hasNextPage = next, endCursor = cursor } };
    internal static object Field(string project, string id, string name = "Status", string type = "SINGLE_SELECT",
        bool issueField = false, object[]? options = null)
        => new { __typename = type == "SINGLE_SELECT" ? "ProjectV2SingleSelectField" : "ProjectV2Field",
            id, name, dataType = type, isIssueField = issueField, project = new { id = project },
            options = options ?? [new { id = "todo", name = "Todo" }, new { id = "done", name = "Done" }] };
    internal static object Value(string project, string field, string? option = "todo", string? id = null)
        => new { __typename = "ProjectV2ItemFieldSingleSelectValue", id = id ?? project + "-value-" + field,
            optionId = option, field = new { id = field, project = new { id = project } } };
    internal static object Issue(string id = "I1", int number = 1) => new { __typename = "Issue", id, number,
        url = $"https://github.com/example/repository/issues/{number}", title = "Synthetic issue", state = "OPEN",
        assignees = Page([], 0), blockedBy = Page([], 0), parent = (object?)null,
        repository = new { id = "R1", nameWithOwner = "example/repository", owner = new { id = "RO1" } } };
    internal static object Item(string project, string? id = null, object? values = null, object? content = null,
        string type = "ISSUE")
        => new { __typename = "ProjectV2Item", id = id ?? project + "-item", type, isArchived = false,
            project = new { id = project }, content = content ?? Issue(),
            fieldValues = values ?? Page([Value(project, project + "-status", project == "P1" ? "todo" : "done")], 1) };
}
