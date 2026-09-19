using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ApplyObservationTests
{
    [TestCase("missing-project"), TestCase("missing-item"), TestCase("partial")]
    public async Task UntrustedCombinedObservationRetainsDraftWithoutDispatch(string defect)
    {
        var h = await ApplyTests.Harness.Create(1); var w = h.Workspace.Drafts!.Workspace;
        w.Commit("P1", w.Open(h.Workspace.Selected!)[0].Cells[0], "must remain local");
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        h.Boundary.ChangeCombinedResponse = data => {
            if (defect == "partial") data["errors"] = JsonSerializer.SerializeToNode(new[] { new { type = "INTERNAL", message = "partial synthetic response" } });
            else data["data"]![defect == "missing-project" ? "project" : "item"] = null;
        };

        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);

        Assert.That(h.Writes, Is.Empty);
        var durable = (await new DraftStore(h.Root).LoadAsync(w.Scope))!;
        Assert.That(durable.Journal!.Single().Operations.Single().State, Is.EqualTo(ApplyState.Waiting));
        Assert.That(durable.Fields.Single(f => f.Key.Kind == "Title").Change!.Value, Is.EqualTo("must remain local"));
    }

    [TestCase(false), TestCase(true)]
    public async Task CombinedFirstPageStillRequiresEveryDefinitionPage(bool failLater)
    {
        var h = await ApplyTests.Harness.Create(1); var w = h.Workspace.Drafts!.Workspace;
        w.Commit("P1", w.Open(h.Workspace.Selected!)[0].Cells[1], "done", true);
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        var prior = h.Boundary.Override!;
        h.Boundary.Override = (query, values) =>
        {
            if (!query.Contains("ProjectFields")) return prior(query, values);
            var later = values.GetProperty("after").ValueKind == JsonValueKind.String;
            if (later && failLater) return ScriptedRunner.Http("{}", 503);
            var definitions = later ? new[] { ProjectReaderTests.Field("P1", "last-field") }
                : Enumerable.Range(0, 100).Select(i => ProjectReaderTests.Field("P1", i == 0 ? "P1-status" : "extra-" + i)).ToArray();
            var project = ProjectReaderTests.Project("P1", "fields", ProjectReaderTests.Page(definitions, 101, !later, later ? null : "next"));
            project["viewerCanUpdate"] = true;
            return ProjectReaderTests.Response(project);
        };

        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);

        Assert.That(h.Writes.Count, Is.EqualTo(failLater ? 0 : 1));
        var durable = (await new DraftStore(h.Root).LoadAsync(w.Scope))!;
        Assert.That(durable.Journal!.Single().Operations.Single().State, Is.EqualTo(failLater ? ApplyState.Waiting : ApplyState.Succeeded));
    }
}
