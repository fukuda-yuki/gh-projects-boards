using System.Text.Json.Nodes;
using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ObservationIdentityTests
{
    [TestCase("missing"), TestCase("null"), TestCase("string"), TestCase("zero"), TestCase("other")]
    public async Task ResponseMustProveTheBoundViewerBeforePublishingAnObservation(string proof)
    {
        var h = await ApplyTests.Harness.Create(1); var w = h.Workspace.Drafts!.Workspace;
        w.Commit("P1", w.Open(h.Workspace.Selected!)[0].Cells[0], "local");
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        h.Boundary.ChangeCombinedResponse = data => SetProof(data, proof);
        var batch = h.Workspace.ApplyReview!.Batch;

        var result = await new ApplyRemote(h.Service, h.Context).ObserveAsync(batch, batch.Operations.Single(), default);

        Assert.That(result.Observation, Is.Null);
        Assert.That(result.Result.Failure, Is.EqualTo(proof == "other" ? FailureKind.IdentityChanged : FailureKind.InvalidResponse));
        Assert.That(h.Context.IsInvalidated, Is.EqualTo(proof == "other"));
        Assert.That(h.Writes, Is.Empty);
    }

    [TestCase(false), TestCase(true)]
    public async Task CrossAccountConvergenceOrPostwriteReadbackCannotAcknowledgeSuccess(bool postwrite)
    {
        var h = await ApplyTests.Harness.Create(1); var w = h.Workspace.Drafts!.Workspace;
        var cell = w.Open(h.Workspace.Selected!)[0].Cells[0]; w.Commit("P1", cell, "intended");
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        h.Boundary.ChangeCombinedResponse = data =>
        {
            if (postwrite && h.Writes.Count == 0) return;
            // The final /user guard reported the original account. The target response
            // comes from another account, which may switch back before the next request.
            SetProof(data, "other"); data["data"]!["item"]!["content"]!["title"] = "intended";
        };

        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);

        var durable = (await new DraftStore(h.Root).LoadAsync(w.Scope))!;
        Assert.That(durable.Journal!.Single().Operations.Single().State, Is.EqualTo(postwrite ? ApplyState.Unknown : ApplyState.Waiting));
        Assert.That(durable.Fields.Single(f => f.Key == cell.Key).Change!.Value, Is.EqualTo("intended"));
        Assert.That(h.Writes.Count, Is.EqualTo(postwrite ? 1 : 0));
        Assert.That(h.Context.IsInvalidated, Is.True);
        h.Boundary.ChangeCombinedResponse = null;
        Assert.That((await h.Service.RecheckAsync(h.Context)).Result.Failure, Is.EqualTo(FailureKind.IdentityChanged));
    }

    private static void SetProof(JsonNode data, string proof)
    {
        if (proof == "missing") { data["data"]!.AsObject().Remove("viewer"); return; }
        data["data"]!["viewer"] = proof == "null" ? null : new JsonObject { ["databaseId"] = proof switch {
            "other" => JsonValue.Create(99), "zero" => JsonValue.Create(0), _ => JsonValue.Create("42") } };
    }

    [TestCase("fields"), TestCase("values")]
    public async Task CrossAccountContinuationCannotCompleteAnObservation(string page)
    {
        var h = await ApplyTests.Harness.Create(1); var w = h.Workspace.Drafts!.Workspace;
        w.Commit("P1", w.Open(h.Workspace.Selected!)[0].Cells[1], "done", true);
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        var prior = h.Boundary.Override!;
        h.Boundary.Override = (q, v) =>
        {
            if (page == "fields" && q.Contains("ProjectFields"))
            {
                var later = v.GetProperty("after").ValueKind == JsonValueKind.String;
                var project = ProjectReaderTests.Project("P1", "fields", ProjectReaderTests.Page(
                    [ProjectReaderTests.Field("P1", later ? "extra" : "P1-status")], 2, !later, later ? null : "next"));
                project["viewerCanUpdate"] = true;
                return ProjectReaderTests.Response(project);
            }
            if (page == "values" && q.Contains("ProjectItemValues"))
                return ProjectReaderTests.Response(new { __typename = "ProjectV2Item", id = "P1-T1", project = new { id = "P1" },
                    fieldValues = ProjectReaderTests.Page([ProjectReaderTests.Value("P1", "P1-status", "todo")], 2) });
            return prior(q, v);
        };
        if (page == "values") h.Boundary.ChangeCombinedResponse = data =>
            data["data"]!["item"]!["fieldValues"] = JsonSerializer.SerializeToNode(ProjectReaderTests.Page(
                [new { __typename = "ProjectV2ItemFieldTextValue", id = "other-value", field = new { id = "P1-text", project = new { id = "P1" } } }], 2, true, "later"));
        h.Boundary.ChangeObservationResponse = (q, data) => {
            if (q.Contains(page == "fields" ? "ProjectFields" : "ProjectItemValues")) SetProof(data, "other");
        };

        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);

        Assert.That(h.Writes, Is.Empty);
        Assert.That(h.Context.IsInvalidated, Is.True);
        var durable = (await new DraftStore(h.Root).LoadAsync(w.Scope))!;
        Assert.That(durable.Journal!.Single().Operations.Single().State, Is.EqualTo(ApplyState.Waiting));
    }

    [TestCase("version", FailureKind.InvalidResponse), TestCase("keyring", FailureKind.UnknownCredentialStore),
     TestCase("scope", FailureKind.PermissionDenied), TestCase("viewer", FailureKind.IdentityChanged), TestCase("cancel", FailureKind.Cancelled)]
    public async Task ImmediateReadRetainsVersionIdentityKeyringScopeAndCancellationRequirements(string defect, FailureKind expected)
    {
        var baseline = GhConnectionTests.ConnectedRunner(); var changed = false;
        var runner = new ScriptedRunner(command => changed && command.Arguments[0] == "--version" && defect == "version"
            ? new(ProcessCompletion.Exited, true, 0, "invalid version")
            : changed && command.Arguments[0] == "auth" && defect is "scope" or "keyring"
                ? GhConnectionTests.Auth(source: defect == "keyring" ? "unknown" : "keyring", scopes: defect == "scope" ? "repo" : "repo, project")
                : changed && command.Arguments.Contains("user") && defect == "viewer" ? ScriptedRunner.Http("{\"id\":99,\"login\":\"other\"}")
                    : baseline.RunAsync(command).GetAwaiter().GetResult());
        var service = new GhConnectionService("gh.exe", "github.com", runner);
        var context = (await service.ConnectAsync()).Context!; changed = true;

        var result = await service.RecheckAndReadAsync(context, ApiRequest.Rest("GET", "resource"), "project", new(defect == "cancel"));

        Assert.That(result.Failure, Is.EqualTo(expected));
        Assert.That(result.Data, Is.Null);
    }

    [TestCase("viewer"), TestCase("scope"), TestCase("keyring")]
    public async Task MutationCannotReuseCompletedObservationCredentials(string defect)
    {
        var h = await ApplyTests.Harness.Create(1); var w = h.Workspace.Drafts!.Workspace;
        w.Commit("P1", w.Open(h.Workspace.Selected!)[0].Cells[0], "local");
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        var batch = h.Workspace.ApplyReview!.Batch; var remote = new ApplyRemote(h.Service, h.Context);
        Assert.That((await remote.ObserveAsync(batch, batch.Operations.Single(), default)).Observation, Is.Not.Null);
        if (defect == "viewer") h.Boundary.ViewerId = 99;
        else h.Boundary.OverrideProcess = c => c.Arguments[0] == "auth"
            ? GhConnectionTests.Auth(source: defect == "keyring" ? "unknown" : "keyring", scopes: defect == "scope" ? "project" : "repo, project") : null;

        var result = await remote.MutateAsync(batch, batch.Operations.Single(), default);

        Assert.That(result.Failure, Is.EqualTo(defect == "viewer" ? FailureKind.IdentityChanged : defect == "scope" ? FailureKind.PermissionDenied : FailureKind.UnknownCredentialStore));
        Assert.That(h.Writes, Is.Empty);
    }
}
