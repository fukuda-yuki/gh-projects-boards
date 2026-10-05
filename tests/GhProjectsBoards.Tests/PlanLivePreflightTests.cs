using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using GhProjectsBoards.App.GitHub;
using NUnit.Framework;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
namespace GhProjectsBoards.Tests;
[TestFixture, Category("Unit")]
internal sealed class PlanLivePreflightTests
{
    [TestCase("refresh-complete-project")]
    [TestCase("create-50")]
    public void RefreshRatesExcludeOtherRequestsAndPublishTime(string workload)
    {
        var start = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
        PlanPublisherLive.RequestRecord Record(double offset, double seconds, string stage) =>
            new(start.AddSeconds(offset), start.AddSeconds(offset + seconds), stage == "write", ProcessCompletion.Exited, stage);
        var result = PlanPublisherLive.RefreshRates(workload,
            [Record(0, 3.3, "items-page"), Record(4, 2.3, "items-page"), Record(10, 10.7, "write"), Record(25, 4, "read")], 105);
        Assert.That(result.ItemPages, Is.EqualTo(2));
        if (workload == "refresh-complete-project")
        {
            Assert.That(result.SecondsPerItemPage, Is.EqualTo(2.8).Within(.000001));
            Assert.That(result.SecondsPerItem, Is.EqualTo(5.6 / 105).Within(.000001));
        }
        else { Assert.That(result.SecondsPerItemPage, Is.Null); Assert.That(result.SecondsPerItem, Is.Null); }
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task IndependentReadsWaitForStableCompleteMembershipAndKeepSafeDiagnostics(bool neverSettles)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-settle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var scope = new ConnectionScope("github.com", 42); var id = new ScopedId(scope, "P1");
        var model = new ProjectReadModel(id, new(scope, "O1"), "User", 3, "https://github.com/users/acme/projects/3", "private-title",
            [], ImmutableDictionary<ScopedId, IssueReadModel>.Empty, [], true, true);
        ProjectReadResult Complete(int count) => new(ProjectReadOutcome.Complete, model with { Items = Enumerable.Range(1, count).Select(i =>
            new ProjectItemReadModel(new(scope, "T" + i), ProjectItemKind.Issue, "Issue", new(scope, "I" + i), false, [], true)).ToArray() }, []);
        var calls = 0;
        Task<ProjectReadResult> Read()
        {
            calls++;
            return Task.FromResult(calls == 1 || neverSettles ? new ProjectReadResult(ProjectReadOutcome.Partial, model,
                [new(ReadProblemKind.IncompleteTraversal, "items", FailureKind.InvalidResponse)]) : Complete(calls == 2 ? 1 : 2));
        }
        try
        {
            if (neverSettles)
            {
                var error = Assert.ThrowsAsync<InvalidOperationException>(async () => await PlanPublisherLive.ReadSettled(Read, root, _ => Task.CompletedTask));
                Assert.That(error!.Message, Does.Contain("Partial").And.Contain("IncompleteTraversal").And.Contain("items"));
                Assert.That(await File.ReadAllTextAsync(Path.Combine(root, "failure.txt")), Does.Contain("Partial").And.Not.Contain("private-title"));
            }
            else
            {
                var result = await PlanPublisherLive.ReadSettled(Read, root, _ => Task.CompletedTask);
                Assert.That(result.Items.Select(i => i.Id.NodeId), Is.EqualTo(new[] { "T1", "T2" }));
                var evidence = await File.ReadAllTextAsync(Path.Combine(root, "settling.jsonl"));
                Assert.That(evidence, Does.Contain("Partial").And.Contain("Complete").And.Not.Contain("private-title"));
            }
        }
        finally { Directory.Delete(root, true); }
    }
    [Test]
    public void LiveMappingsUseNamesAndTypesIncludingBothDateColumns()
    {
        var scope = new ConnectionScope("github.com", 42); var project = new ScopedId(scope, "P1");
        var fields = new[] { ("Cost", "NUMBER"), ("Actual", "NUMBER"), ("Target date", "DATE"), ("Remaining", "NUMBER"), ("Estimate", "NUMBER"), ("Start date", "DATE") }
            .Select(f => new ProjectFieldDefinition(new(scope, "F-" + f.Item1), project, f.Item1, "ProjectV2Field", f.Item2, FieldOwner.ProjectItem, [], ValueAvailability.Present)).ToArray();
        var settings = PlanPublisherLive.CreateSettings(fields, new(2026, 10, 6));
        Assert.That(settings.Columns.Select(c => (c.Role, c.FieldId)), Is.EquivalentTo(new[]
        {
            (PlanField.Estimate, "F-Estimate"), (PlanField.Remaining, "F-Remaining"), (PlanField.Actual, "F-Actual"),
            (PlanField.Start, "F-Start date"), (PlanField.End, "F-Target date")
        }));
    }
    [Test]
    public void LiveWorkloadPreservesBaselineDatesAndIncludesDerivedDateWrites()
    {
        var day = new DateOnly(2026, 10, 6); var scope = new ConnectionScope("github.com", 42); var project = new ScopedId(scope, "P1");
        var fields = new[] { ("Estimate", "NUMBER"), ("Remaining", "NUMBER"), ("Actual", "NUMBER"), ("Start date", "DATE"), ("Target date", "DATE") }
            .Select(f => new ProjectFieldDefinition(new(scope, "F-" + f.Item1), project, f.Item1, "ProjectV2Field", f.Item2, FieldOwner.ProjectItem, [], ValueAvailability.Present)).ToImmutableArray();
        var settings = PlanPublisherLive.CreateSettings(fields, day);
        var originals = new[] { new PlanRow("scope", "Scope", settings.DefaultRepository!),
            new PlanRow("original", "Original", settings.DefaultRepository!) { Estimate = 16, Remaining = 3, Actual = 7, Start = day.AddDays(7), End = day.AddDays(8) } };
        var owned = Enumerable.Range(0, 100).Select(i => new PlanRow("owned-" + i, "Owned", settings.DefaultRepository!)).ToArray();
        var baseline = new PlanBaseline(originals.Concat(owned).ToImmutableArray(), []);
        var pinned = PlanPublisherLive.PreserveBaselineDates(baseline.Rows, originals.Select(r => r.Identity).ToHashSet());
        var rows = pinned.Select(r => r.Identity.StartsWith("owned-", StringComparison.Ordinal) ? r with { Estimate = 11, Remaining = 12, Actual = 13 } : r).ToImmutableArray();
        var document = new PlanDocument(project, baseline, new(rows, settings));
        var review = PlanPublishPlan.Build(document, new(baseline, ImmutableDictionary<string, string>.Empty, fields, 0, 0), day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Changes.All(c => c.Identity.StartsWith("owned-", StringComparison.Ordinal)), Is.True);
        Assert.That(review.Changes.Count(c => c.Field is PlanField.Estimate or PlanField.Remaining or PlanField.Actual), Is.EqualTo(300));
        Assert.That(review.Changes.Count(c => c.Field is PlanField.Start or PlanField.End), Is.EqualTo(200));
        Assert.That(PlanPublishPlan.Batches(review.Writes).Count(), Is.EqualTo(10));
        Assert.That(baseline.Rows[1].Fixed, Is.False);
    }
    [Test, Category("Integration")]
    public async Task LiveCliFailureWritesAnArtifactAndExitsNormallyWithoutGitHub()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-live-exit-" + Guid.NewGuid().ToString("N"));
        var runner = new GhProcessRunner(new Dictionary<string, string?>
        {
            ["GHPB_RUN_PLAN_PUBLISH_PROOF"] = "1", ["GHPB_LIVE_GH_PATH"] = Path.Combine(root, "missing-gh.exe")
        });
        try
        {
            var result = await runner.RunAsync(new GhCommand(GhProcessTests.FakeExecutable, ["--plan-publish-live", "Run", root], timeout: TimeSpan.FromSeconds(10)));
            Assert.That(result.Completion, Is.EqualTo(ProcessCompletion.Exited));
            Assert.That(result.ExitCode, Is.EqualTo(1));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(root, "failure.txt")), Does.Contain("allowlisted github.com account"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Test]
    public async Task SchemaPreflightAcceptsTheTwoTypeLimitAndChecksEveryRequiredInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-schema-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var expected = new Dictionary<string, string[]>
        {
            ["AddSubIssueInput"] = ["issueId", "subIssueId"], ["RemoveSubIssueInput"] = ["issueId", "subIssueId"],
            ["ReprioritizeSubIssueInput"] = ["issueId", "subIssueId", "afterId", "beforeId"],
            ["AddBlockedByInput"] = ["issueId", "blockingIssueId"], ["RemoveBlockedByInput"] = ["issueId", "blockingIssueId"],
            ["CreateProjectV2FieldInput"] = ["projectId", "name", "dataType", "singleSelectOptions"],
            ["UpdateProjectV2ItemPositionInput"] = ["projectId", "itemId", "afterId"],
            ["AddAssigneesToAssignableInput"] = ["assignableId", "assigneeIds"]
        };
        var observed = new HashSet<string>();
        Task<JsonElement> Send(ApiRequest request)
        {
            using var payload = JsonDocument.Parse(request.Payload!);
            var query = payload.RootElement.GetProperty("query").GetString()!;
            var types = Regex.Matches(query, "(t[0-9]+):__type\\(name:\"([^\"]+)\"\\)");
            if (types.Count > 2) throw new InvalidOperationException("INTROSPECTION_LIMIT_EXCEEDED: __Type.inputFields may only be used twice.");
            var data = types.Cast<Match>().ToDictionary(m => m.Groups[1].Value, m =>
            {
                var name = m.Groups[2].Value; observed.Add(name);
                return new { inputFields = expected[name].Select(field => new { name = field }).ToArray() };
            });
            return Task.FromResult(JsonSerializer.SerializeToElement(new { data }));
        }
        try { await PlanPublisherLive.CheckSchema(Send, root); Assert.That(observed, Is.EquivalentTo(expected.Keys)); }
        finally { Directory.Delete(root, true); }
    }
    [Test]
    public void LiveFailureIncludesTheActionableGitHubMessageWithoutThePayload()
    {
        var data = JsonSerializer.SerializeToElement(new { errors = new[] { new { type = "INTROSPECTION_LIMIT_EXCEEDED", message = "Introspection fields may only be used 2 times.", extensions = new { payload = "private-payload" } } } });
        var response = new ApiResult(ApiOutcome.Failed, FailureKind.GraphQl, 1, 200, data);
        var reason = response.FailureReason();
        Assert.That(reason, Does.Contain("INTROSPECTION_LIMIT_EXCEEDED").And.Contain("Introspection fields may only be used 2 times."));
        Assert.That(reason, Does.Not.Contain("private-payload"));
    }
    [Test]
    public void FailureMessageIsShortSingleLineAndRedactsCredentialText()
    {
        var data = JsonSerializer.SerializeToElement(new { errors = new[] { new { type = "FORBIDDEN", message = "Cannot update this Issue.\nAuthorization: Bearer github_pat_syntheticcredential\n" + new string('x', 400) } } });
        var reason = new ApiResult(ApiOutcome.Failed, FailureKind.GraphQl, 1, 200, data).FailureReason();
        Assert.That(reason, Does.Contain("Cannot update this Issue."));
        Assert.That(reason, Does.Not.Contain("syntheticcredential").And.Not.Contain("\n"));
        Assert.That(reason.Length, Is.LessThanOrEqualTo(240));
    }
}
