using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
using static GhProjectsBoards.Tests.ProjectReaderTests;

namespace GhProjectsBoards.Tests;

// #319: the bounded Project read observes each Issue's organization Issue type and pages the catalogs of the
// repository owner organizations once, so no row costs a request and a personal repository contributes none.
[TestFixture]
[Category("Integration")]
internal sealed class PlanIssueTypeReaderTests
{
    private static readonly ConnectionScope Scope = new("github.com", 42);
    private static readonly ScopedId Project = new(Scope, "P1");

    // An Issue detail as the item-detail request returns it. The owner's __typename is how the read knows
    // whether the repository can carry an organization Issue type at all.
    private static JsonObject Content(string id = "I1", int number = 1, string repository = "example/repository",
        string ownerId = "RO1", string ownerType = "Organization", string? typeName = null, string? typeId = null,
        bool observed = true)
    {
        var node = new JsonObject
        {
            ["__typename"] = "Issue", ["id"] = id, ["number"] = number,
            ["url"] = "https://github.com/" + repository + "/issues/" + number,
            ["title"] = "Synthetic issue", ["state"] = "OPEN", ["closedAt"] = null,
            ["updatedAt"] = "2026-10-20T00:00:00Z",
            ["labels"] = JsonSerializer.SerializeToNode(Page([], 0)),
            ["milestone"] = null,
            ["assignees"] = JsonSerializer.SerializeToNode(Page([], 0)),
            ["blockedBy"] = JsonSerializer.SerializeToNode(Page([], 0)),
            ["subIssues"] = JsonSerializer.SerializeToNode(Page([], 0)),
            ["parent"] = null,
            ["repository"] = new JsonObject
            {
                ["id"] = "R-" + repository, ["nameWithOwner"] = repository,
                ["owner"] = new JsonObject { ["id"] = ownerId, ["__typename"] = ownerType }
            }
        };
        if (observed)
            node["issueType"] = typeName is null ? null : new JsonObject { ["id"] = typeId ?? "IT-" + typeName, ["name"] = typeName };
        return node;
    }

    private static object ItemFor(string id, JsonObject content, object? values = null, string type = "ISSUE")
        => new { __typename = "ProjectV2Item", id, type, isArchived = false, project = new { id = "P1" },
            content, fieldValues = values ?? Page([], 0) };

    private static object IssueTypeNode(string id, string name = "Task", bool enabled = true) => new { id, name, isEnabled = enabled };

    private static object OrganizationCatalog(string ownerId, object[] nodes, int total, bool next = false, string? cursor = null)
        => new { __typename = "Organization", id = ownerId, issueTypes = Page(nodes, total, next, cursor) };

    private static int OrganizationQueries(ProjectBoundary boundary)
        => boundary.Runner.Commands.Count(c => c.StandardInput?.Contains("ProjectIssueTypes", StringComparison.Ordinal) == true);

    private static async Task<ProjectReadResult> Read(ProjectBoundary boundary)
    {
        var service = new GhConnectionService("gh.exe", Scope.Host, boundary.Runner);
        var context = (await service.ConnectAsync()).Context!;
        return await new ProjectReader(service).ReadAsync(context, Project);
    }

    [Test]
    public async Task AnIssueTypeIsPresentOrEmptyAndNoTypeIsNotAFailure()
    {
        var boundary = new ProjectBoundary { Override = (query, _) =>
            query.Contains("ProjectItems")
                ? Response(Project("P1", "items", Page([
                    ItemFor("T1", Content("I1", typeName: "Task", typeId: "IT1")),
                    ItemFor("T2", Content("I2", number: 2))], 2)))
                : query.Contains("ProjectIssueTypes")
                    ? Response(OrganizationCatalog("RO1", [IssueTypeNode("IT1")], 1))
                    : null };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), result.ToString());
        var issues = result.Project!.Issues.Values.ToDictionary(i => i.Id.NodeId);
        Assert.Multiple(() =>
        {
            Assert.That(issues["I1"].IssueType, Is.EqualTo(new ReadValue<SelectOption>(ValueAvailability.Present, new SelectOption("IT1", "Task"))));
            Assert.That(issues["I2"].IssueType, Is.EqualTo(new ReadValue<SelectOption>(ValueAvailability.Empty)));
        });
        boundary.AssertQueriesOnly();
    }

    [Test]
    public async Task AResponseWithoutTheObservationReportsNotLoaded()
    {
        var content = Content("I1");
        content.Remove("issueType");
        var boundary = new ProjectBoundary { Override = (query, _) =>
            query.Contains("ProjectItems") ? Response(Project("P1", "items", Page([ItemFor("T1", content)], 1)))
            : query.Contains("ProjectIssueTypes") ? Response(OrganizationCatalog("RO1", [IssueTypeNode("IT1")], 1)) : null };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), result.ToString());
        Assert.That(result.Project!.Issues.Values.Single().IssueType.Availability, Is.EqualTo(ValueAvailability.NotLoaded));
    }

    [Test]
    public async Task AnIssueTypeThatCannotBeInterpretedIsUnavailableAndTheReadIsPartial()
    {
        var content = Content("I1");
        content["issueType"] = "Task";
        var boundary = new ProjectBoundary { Override = (query, _) =>
            query.Contains("ProjectItems") ? Response(Project("P1", "items", Page([ItemFor("T1", content)], 1)))
            : query.Contains("ProjectIssueTypes") ? Response(OrganizationCatalog("RO1", [IssueTypeNode("IT1")], 1)) : null };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial));
        Assert.That(result.Project!.Issues.Values.Single().IssueType.Availability, Is.EqualTo(ValueAvailability.Unavailable));
    }

    [Test]
    public async Task OrganizationCatalogIsReadOncePerOwnerAndOnlyOrganizationRepositoriesContribute()
    {
        var boundary = new ProjectBoundary { Override = (query, variables) =>
        {
            if (query.Contains("ProjectItems"))
                return Response(Project("P1", "items", Page([
                    ItemFor("T1", Content("I1", repository: "example/one", ownerId: "RO1")),
                    ItemFor("T2", Content("I2", number: 2, repository: "example/one", ownerId: "RO1")),
                    ItemFor("T3", Content("I3", number: 3, repository: "personal/two", ownerId: "RU2", ownerType: "User")),
                    ItemFor("T4", Content("I4", number: 4, repository: "other/three", ownerId: "RO2"))], 4)));
            if (!query.Contains("ProjectIssueTypes")) return null;
            return variables.GetProperty("id").GetString() == "RO1"
                ? Response(OrganizationCatalog("RO1", [
                    IssueTypeNode("IT1", "Task"), IssueTypeNode("IT2", "Bug", enabled: false), IssueTypeNode("IT3", "Feature")], 3))
                : Response(OrganizationCatalog("RO2", [IssueTypeNode("IT4", "Bug")], 1));
        } };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), result.ToString());
        Assert.Multiple(() =>
        {
            Assert.That(result.Project!.IssueTypes, Is.EqualTo(new[] { new SelectOption("IT1", "Task"), new SelectOption("IT3", "Feature"), new SelectOption("IT4", "Bug") }),
                "Only enabled types are offered, and both organizations contribute.");
            Assert.That(result.Project.IssueTypeRepositories, Is.EqualTo(new[] { "example/one", "other/three" }),
                "A personal repository's Issues cannot carry a type.");
            Assert.That(OrganizationQueries(boundary), Is.EqualTo(2), "One catalog read per owner, not per row.");
        });
    }

    [Test]
    public async Task PagedOrganizationCatalogIsFollowedToTheEnd()
    {
        var boundary = new ProjectBoundary { Override = (query, variables) =>
        {
            if (query.Contains("ProjectItems"))
                return Response(Project("P1", "items", Page([ItemFor("T1", Content("I1"))], 1)));
            if (!query.Contains("ProjectIssueTypes")) return null;
            var second = variables.GetProperty("after").ValueKind == JsonValueKind.String;
            return Response(second
                ? OrganizationCatalog("RO1", [IssueTypeNode("IT3", "Feature")], 3)
                : OrganizationCatalog("RO1", [IssueTypeNode("IT1", "Task"), IssueTypeNode("IT2", "Bug")], 3, true, "c1"));
        } };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), result.ToString());
        Assert.That(result.Project!.IssueTypes, Is.EqualTo(new[] {
            new SelectOption("IT1", "Task"), new SelectOption("IT2", "Bug"), new SelectOption("IT3", "Feature") }));
    }

    [TestCase("total-mismatch")]
    [TestCase("next-empty")]
    [TestCase("repeated-cursor")]
    [TestCase("duplicate-id")]
    [TestCase("missing-name")]
    [TestCase("invalid-enabled")]
    [TestCase("api-error")]
    [TestCase("node-null")]
    public async Task IncompleteOrMalformedOrganizationCatalogMakesTheReadPartial(string fault)
    {
        var boundary = new ProjectBoundary { Override = (query, variables) =>
        {
            if (query.Contains("ProjectItems"))
                return Response(Project("P1", "items", Page([ItemFor("T1", Content("I1"))], 1)));
            if (!query.Contains("ProjectIssueTypes")) return null;
            var second = variables.GetProperty("after").ValueKind == JsonValueKind.String;
            return fault switch
            {
                "total-mismatch" => Response(OrganizationCatalog("RO1", [IssueTypeNode("IT1")], 2)),
                "next-empty" => Response(OrganizationCatalog("RO1", [IssueTypeNode("IT1"), IssueTypeNode("IT2")], 2, true, "c1")),
                "repeated-cursor" => Response(second
                    ? OrganizationCatalog("RO1", [IssueTypeNode("IT2")], 2, true, "c1")
                    : OrganizationCatalog("RO1", [IssueTypeNode("IT1")], 2, true, "c1")),
                "duplicate-id" => Response(OrganizationCatalog("RO1", [IssueTypeNode("IT1"), IssueTypeNode("IT1")], 2)),
                "missing-name" => Response(OrganizationCatalog("RO1", [new JsonObject { ["id"] = "IT1", ["isEnabled"] = true }], 1)),
                "invalid-enabled" => Response(OrganizationCatalog("RO1", [new { id = "IT1", name = "Task", isEnabled = "yes" }], 1)),
                "api-error" => ScriptedRunner.Http("{}", 500),
                _ => Response(null)
            };
        } };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial), result.ToString());
        Assert.That(result.Project!.IssueTypesComplete, Is.False, "A catalog this read could not prove is never reusable.");
        Assert.That(result.Problems, Is.Not.Empty);
    }

    [Test]
    public async Task AnOrganizationWithoutTypesOffersNoneButStillMarksItsRepositories()
    {
        var boundary = new ProjectBoundary { Override = (query, _) =>
            query.Contains("ProjectItems")
                ? Response(Project("P1", "items", Page([ItemFor("T1", Content("I1"))], 1)))
                : query.Contains("ProjectIssueTypes")
                    ? Response(OrganizationCatalog("RO1", [], 0))
                    : null };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), result.ToString());
        Assert.Multiple(() =>
        {
            Assert.That(result.Project!.IssueTypes, Is.Empty);
            Assert.That(result.Project.IssueTypeRepositories, Is.EqualTo(new[] { "example/repository" }),
                "The organization offers Issue types as a feature even when its catalog is empty.");
        });
    }

    [Test]
    public async Task AnOrganizationWithoutTheConnectionContributesNothing()
    {
        var boundary = new ProjectBoundary { Override = (query, _) =>
            query.Contains("ProjectItems")
                ? Response(Project("P1", "items", Page([ItemFor("T1", Content("I1"))], 1)))
                : query.Contains("ProjectIssueTypes")
                    ? Response(new { __typename = "Organization", id = "RO1", issueTypes = (object?)null })
                    : null };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), result.ToString());
        Assert.Multiple(() =>
        {
            Assert.That(result.Project!.IssueTypes, Is.Empty);
            Assert.That(result.Project.IssueTypeRepositories, Is.Empty);
        });
    }

    [Test]
    public async Task APersonalRepositoryIsNeverQueriedForIssueTypes()
    {
        var boundary = new ProjectBoundary { Override = (query, _) =>
            query.Contains("ProjectItems")
                ? Response(Project("P1", "items", Page([ItemFor("T1", Content("I1", ownerType: "User"))], 1)))
                : null };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), result.ToString());
        Assert.Multiple(() =>
        {
            Assert.That(result.Project!.IssueTypes, Is.Empty);
            Assert.That(result.Project.IssueTypeRepositories, Is.Empty);
            Assert.That(OrganizationQueries(boundary), Is.Zero, "A personal owner has no organization catalog to read.");
        });
    }

    [Test]
    public async Task TheScopedPublishReadReusesTheAdoptedCatalogWithoutAskingAgain()
    {
        var boundary = new ProjectBoundary { Override = (query, variables) =>
        {
            if (query.Contains("ProjectItems"))
                return Response(Project("P1", "items", Page([
                    ItemFor("T1", Content("I1", typeName: "Task", typeId: "IT1")),
                    ItemFor("T2", Content("I2", number: 2))], 2)));
            if (query.Contains("ProjectIssueTypes"))
                return Response(OrganizationCatalog("RO1", [IssueTypeNode("IT1")], 1));
            if (!query.Contains("PlanPublishItemDetails")) return null;
            // The scoped publication read resolves the written Issue to its Project item in one request.
            var scoped = new JsonObject
            {
                ["__typename"] = "ProjectV2Item", ["id"] = "T1", ["type"] = "ISSUE", ["isArchived"] = false,
                ["project"] = new JsonObject { ["id"] = "P1" },
                ["content"] = Content("I1", typeName: "Task", typeId: "IT1"),
                ["fieldValues"] = JsonSerializer.SerializeToNode(Page([], 0))
            };
            return ScriptedRunner.Http(JsonSerializer.Serialize(new { data = new { nodes = new object[] {
                new { __typename = "Issue", id = "I1", projectItems = Page([scoped], 1) } } } }));
        } };
        var service = new GhConnectionService("gh.exe", Scope.Host, boundary.Runner);
        var context = (await service.ConnectAsync()).Context!;
        var reader = new ProjectReader(service);
        var full = await reader.ReadAsync(context, Project);
        Assert.That(full.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), full.ToString());
        var scoped = await reader.ReadItemsAsync(context, Project, ["I1"], metadata: full.Project);
        Assert.That(scoped.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), scoped.ToString());
        Assert.Multiple(() =>
        {
            Assert.That(scoped.Project!.IssueTypes, Is.EqualTo(full.Project!.IssueTypes), "The catalog a publish already read is adopted.");
            Assert.That(scoped.Project.IssueTypeRepositories, Is.EqualTo(full.Project.IssueTypeRepositories));
            Assert.That(scoped.Project.Issues.Values.Single(i => i.Id.NodeId == "I1").IssueType, Is.EqualTo(
                new ReadValue<SelectOption>(ValueAvailability.Present, new SelectOption("IT1", "Task"))));
            Assert.That(OrganizationQueries(boundary), Is.EqualTo(1), "The scoped read does not ask for the organization catalog again.");
        });
    }

    [Test]
    public async Task TheFakeGhExecutableServesTheCatalogAndEachIssuesType()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-issue-type-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true}");
            FakePlanEditor.Save(root, new([
                new(new("I1", "Task", "acme/repo") { IssueType = "Task" }, "", true),
                new(new("I2", "Other", "acme/repo") { IssueType = "Bug" }, "", true)], 3) {
                OrganizationRepositories = true,
                IssueTypes = [new("IT1", "Task"), new("IT2", "Bug", false), new("IT3", "Feature")] });
            var runner = new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root });
            var service = new GhConnectionService(GhProcessTests.FakeExecutable, "github.com", runner);
            var result = await new ProjectReader(service).ReadAsync(
                new("github.com", 42, "fixture-user", GhProcessTests.FakeExecutable), Project);
            Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), result.ToString());
            Assert.Multiple(() =>
            {
                Assert.That(result.Project!.Issues.Values.Single(i => i.Id.NodeId == "I1").IssueType.Value, Is.EqualTo(new SelectOption("IT1", "Task")));
                Assert.That(result.Project.Issues.Values.Single(i => i.Id.NodeId == "I2").IssueType.Value, Is.EqualTo(new SelectOption("IT2", "Bug")),
                    "A disabled type can still be the Issue's current type; it is only not offered for editing.");
                Assert.That(result.Project.IssueTypes, Is.EqualTo(new[] { new SelectOption("IT1", "Task"), new SelectOption("IT3", "Feature") }));
                Assert.That(result.Project.IssueTypeRepositories, Is.EqualTo(new[] { "acme/repo" }));
            });
        }
        finally { Directory.Delete(root, true); }
    }
}
