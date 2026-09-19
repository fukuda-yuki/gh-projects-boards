using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
using static GhProjectsBoards.Tests.ProjectReaderTests;

namespace GhProjectsBoards.Tests;

[TestFixture, Category("Integration")]
internal sealed class PlanningReaderTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ReadsEveryNativeRelationshipPageOrRejectsIncompleteObservation(bool broken)
    {
        var boundary = new ProjectBoundary();
        boundary.Override = (query, variables) =>
        {
            if (query.Contains("ProjectItems"))
            {
                var content = JsonSerializer.SerializeToNode(Issue())!;
                content["assignees"] = JsonSerializer.SerializeToNode(Page([new { id = "U1", login = "first" }], 2, true, "people-next"));
                content["blockedBy"] = JsonSerializer.SerializeToNode(Page([new { id = "A" }], 2, true, "links-next"));
                content["parent"] = new JsonObject { ["id"] = "PARENT" };
                return Response(Project("P1", "items", Page([Item("P1", content: content)], 1)));
            }
            if (query.Contains("PlanningAssignees"))
            {
                Assert.That(variables.GetProperty("after").GetString(), Is.EqualTo("people-next"));
                return Response(new { __typename = "Issue", id = "I1", assignees = Page([new { id = "U2", login = "second" }], 2) });
            }
            if (query.Contains("PlanningPredecessors"))
            {
                Assert.That(variables.GetProperty("after").GetString(), Is.EqualTo("links-next"));
                return Response(new { __typename = "Issue", id = "I1", blockedBy = Page([new { id = broken ? "A" : "B" }], 2) });
            }
            return null;
        };
        var service = new GhConnectionService("gh.exe", "github.com", boundary.Runner);
        var context = (await service.ConnectAsync()).Context!;
        var result = await new ProjectReader(service).ReadAsync(context, new(new("github.com", 42), "P1"));
        if (broken) { Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial)); return; }
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
        var native = result.Project!.Issues.Values.Single().Native;
        Assert.That(native, Is.Not.Null);
        Assert.That(native!.Complete, Is.True);
        Assert.That(native.Assignees.Select(a => a.Id.NodeId), Is.EqualTo(new[] { "U1", "U2" }));
        Assert.That(native.Predecessors.Select(a => a.NodeId), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(native.Parent.Value!.NodeId, Is.EqualTo("PARENT"));
        boundary.AssertQueriesOnly();
    }
}
