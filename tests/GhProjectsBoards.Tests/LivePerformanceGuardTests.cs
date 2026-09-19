using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class LivePerformanceGuardTests
{
    [TestCase("{ \"fields\":1, \"items\":[{\"id\":\"one\",\"title\":\"a\"},{\"id\":\"two\",\"title\":\"b\"}] }", true)]
    [TestCase("{\"items\":[{\"title\":\"a\",\"id\":\"one\"},{\"title\":\"b\",\"id\":\"two\"}],\"fields\":1}", true)]
    [TestCase("{\"fields\":1,\"items\":[{\"id\":\"one\",\"title\":\"changed\"},{\"id\":\"two\",\"title\":\"b\"}]}", false)]
    [TestCase("{\"fields\":1,\"items\":[{\"id\":\"other\",\"title\":\"a\"},{\"id\":\"two\",\"title\":\"b\"}]}", false)]
    [TestCase("{\"fields\":1,\"items\":[{\"id\":\"two\",\"title\":\"b\"},{\"id\":\"one\",\"title\":\"a\"}]}", false)]
    public void SnapshotEqualityPreservesValuesIdentitiesAndArrayOrderWithoutDependingOnJsonFormatting(string current, bool same)
    {
        using var expected = JsonDocument.Parse("{\"fields\":1,\"items\":[{\"id\":\"one\",\"title\":\"a\"},{\"id\":\"two\",\"title\":\"b\"}]}");
        using var observed = JsonDocument.Parse(current);
        Assert.That(LivePerformanceRun.SameSnapshot(expected.RootElement, observed.RootElement), Is.EqualTo(same));
    }

    [TestCase(429, "", "{}", true)]
    [TestCase(403, "Retry-After: 60\r\n", "{}", true)]
    [TestCase(403, "X-RateLimit-Remaining: 0\r\n", "{}", true)]
    [TestCase(200, "", "{\"errors\":[{\"type\":\"RATE_LIMITED\"}]}", true)]
    [TestCase(200, "", "{\"errors\":[{\"extensions\":{\"code\":\"RATE_LIMITED\"}}]}", true)]
    [TestCase(403, "", "{\"message\":\"You have exceeded a secondary rate limit\"}", true)]
    [TestCase(403, "", "{\"message\":\"You have triggered an abuse detection mechanism\"}", true)]
    [TestCase(200, "X-RateLimit-Remaining: 0\r\n", "{\"data\":{}}", true)]
    [TestCase(403, "X-RateLimit-Remaining: 10\r\n", "{}", false)]
    [TestCase(200, "X-RateLimit-Remaining: 100\r\n", "{\"data\":{}}", false)]
    [TestCase(200, "", "{\"data\":{\"issue\":{\"body\":\"RATE_LIMITED and secondary rate limit documentation\"}}}", false)]
    [TestCase(200, "", "{\"bio\":\"API rate limit exceeded is an example error\"}", false)]
    public void RateLimitClassificationRetainsQuotaAndStopsOnlyOnLimitEvidence(int status, string headers, string body, bool stop)
    {
        var result = new GhProcessResult(ProcessCompletion.Exited, true, 0, $"HTTP/2.0 {status} OK\r\n{headers}\r\n{body}");
        var classified = LivePerformanceRunner.Classify(result);
        Assert.That(classified.Status, Is.EqualTo(status));
        Assert.That(classified.Throttled, Is.EqualTo(stop));
    }

    [Test]
    public async Task ThrottleStopsSubsequentDispatchAndKeepsSafeProcessEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-perf-guard-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var boundary = new ScriptedRunner(_ => new(ProcessCompletion.Exited, true, 1, "HTTP/2.0 429 Limited\r\nRetry-After: 60\r\n\r\n{}"));
        var runner = new LivePerformanceRunner(root, "ghpb-perf51-owned", boundary);
        var command = new GhCommand(LivePerformanceRun.Gh, ["api", "user", "--hostname", "github.com", "--method", "GET"]);
        await runner.RunAsync(command);
        var next = await runner.RunAsync(command);
        Assert.That(next.Started, Is.False); Assert.That(next.Completion, Is.EqualTo(ProcessCompletion.Cancelled));
        Assert.That(runner.Stopped, Is.True); Assert.That(runner.Mutations, Is.Zero);
        Assert.That(runner.Records.Single().Quota["retry-after"], Is.EqualTo("60"));
        Assert.That(File.ReadAllText(Path.Combine(root, "processes.jsonl")), Does.Not.Contain("StandardOutput"));
    }

    [Test]
    public async Task FailedGraphQlResponseKeepsDiagnosticCodesWithoutContentOrCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-perf-error-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var boundary = new ScriptedRunner(_ => new(ProcessCompletion.Exited, true, 1,
            "HTTP/2.0 200 OK\r\nX-GitHub-Request-Id: ABCD:1234\r\n\r\n" +
            "{\"errors\":[{\"type\":\"UNPROCESSABLE\",\"message\":\"private title\"},{\"type\":\"ghp_secret\"}],\"data\":{\"title\":\"private title\"}}",
            "private stderr"));
        var runner = new LivePerformanceRunner(root, "ghpb-perf51-owned", boundary);
        await runner.RunAsync(new(LivePerformanceRun.Gh, ["api", "graphql", "--hostname", "github.com", "--method", "POST"], "{\"query\":\"query{viewer{id}}\"}"));
        var evidence = File.ReadAllText(Path.Combine(root, "processes.jsonl"));
        Assert.That(evidence, Does.Contain("ABCD:1234").And.Contain("UNPROCESSABLE").And.Contain("Unrecognized"));
        Assert.That(evidence, Does.Not.Contain("private").And.Not.Contain("ghp_secret"));
    }

    [TestCase(200, "{\"errors\":[{\"type\":\"UNPROCESSABLE\",\"message\":\"request rejected\"}]}", true)]
    [TestCase(403, "{\"message\":\"request rejected\"}", true)]
    [TestCase(410, "{\"message\":\"Gone\"}", false)]
    public async Task UnclassifiedApiErrorsStopFurtherWorkWithoutAssumingTheirCause(int status, string body, bool stop)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-perf-unclassified-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var runner = new LivePerformanceRunner(root, "ghpb-perf51-owned", new ScriptedRunner(_ =>
            new(ProcessCompletion.Exited, true, 1, $"HTTP/2.0 {status} Response\r\n\r\n{body}")));
        var request = new GhCommand(LivePerformanceRun.Gh, ["api", "resource", "--hostname", "github.com", "--method", "GET"]);
        await runner.RunAsync(request);
        Assert.That(runner.Stopped, Is.EqualTo(stop));
        Assert.That(runner.Records.Single().Throttled, Is.False);
        if (stop)
        {
            Assert.That(runner.StopReason, Is.EqualTo("UnverifiedApiOutcome"));
            Assert.That((await runner.RunAsync(request)).Started, Is.False);
        }
    }

    [TestCase(100, 5000, false)]
    [TestCase(5000, 100, false)]
    [TestCase(5000, 5000, true)]
    public async Task PrimaryBudgetRequiresHeadroomInBothLatestResponseHeaders(int coreRemaining, int graphqlRemaining, bool allowed)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-perf-quota-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var runner = new LivePerformanceRunner(root, "ghpb-perf51-owned", new ScriptedRunner(c =>
        {
            var core = c.Arguments[1] != "graphql";
            return new(ProcessCompletion.Exited, true, 0, $"HTTP/2.0 200 OK\r\nX-RateLimit-Resource: {(core ? "core" : "graphql")}\r\nX-RateLimit-Remaining: {(core ? coreRemaining : graphqlRemaining)}\r\n\r\n{{}}");
        }));
        await runner.RunAsync(new(LivePerformanceRun.Gh, ["api", "user", "--hostname", "github.com", "--method", "GET"]));
        await runner.RunAsync(new(LivePerformanceRun.Gh, ["api", "graphql", "--hostname", "github.com", "--method", "POST"], "{\"query\":\"query{viewer{id}}\"}"));
        using var resources = JsonDocument.Parse("{\"core\":{\"remaining\":5000},\"graphql\":{\"remaining\":5000}}");
        if (allowed) Assert.DoesNotThrow(() => runner.RequirePrimaryBudget(resources.RootElement, 1000, 1000));
        else Assert.Throws<InvalidOperationException>(() => runner.RequirePrimaryBudget(resources.RootElement, 1000, 1000));
        Assert.That(runner.Mutations, Is.Zero);
    }

    [TestCase("throttle"), TestCase("malformed"), TestCase("missing-frame"), TestCase("null-data"), TestCase("missing-data")]
    public async Task LiveProtocolStopSettlesTheRealApplyWithoutSendingRemainingFields(string failure)
    {
        var fixture = await ApplyTests.Harness.Create(1);
        const string marker = "ghpb-perf51-owned";
        fixture.Titles["I1"] = marker + " baseline";
        var root = Path.Combine(Path.GetTempPath(), "ghpb-perf-apply-guard-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var process = new ScriptedRunner(c => c.StandardInput?.Contains("mutation ApplyTitle", StringComparison.Ordinal) == true
            ? failure == "throttle" ? new(ProcessCompletion.Exited, true, 1, "HTTP/2.0 429 Limited\r\nRetry-After: 60\r\n\r\n{}")
                : new(ProcessCompletion.Exited, true, 0, failure switch { "malformed" => "HTTP/2.0 200 OK\r\n\r\n{", "missing-frame" => "{}",
                    "null-data" => "HTTP/2.0 200 OK\r\n\r\n{\"data\":null}", _ => "HTTP/2.0 200 OK\r\n\r\n{}" })
            : fixture.Boundary.Runner.RunAsync(c).GetAwaiter().GetResult());
        var runner = new LivePerformanceRunner(root, marker, process);
        runner.AllowIssue("I1", marker + " baseline", marker + " changed");
        var service = new GhConnectionService(LivePerformanceRun.Gh, "github.com", runner);
        var context = (await service.ConnectAsync()).Context!;
        var workspace = new RegistrationWorkspace(new(root)); await workspace.BindAsync(context, service);
        var choice = await new ProjectDiscovery(service).ResolveAsync(context, "https://github.com/users/sample-user/projects/1", default);
        await workspace.RegisterAsync(choice, null);
        var row = workspace.Drafts!.Workspace.Open(workspace.Selected!).Single();
        workspace.Drafts.Workspace.Commit("P1", row.Cells[0], marker + " changed");
        workspace.Drafts.Workspace.Commit("P1", row.Cells[1], "done", true);
        await workspace.PrepareApplyAsync(new HashSet<string> { row.ItemId });
        runner.OnStop = workspace.Cancel;
        var apply = workspace.ConfirmApplyAsync(workspace.ApplyReview!);
        var timeout = Task.Delay(TimeSpan.FromSeconds(3));
        var finished = await Task.WhenAny(apply, timeout);
        if (finished != apply) workspace.Cancel();
        await apply;
        var durable = (await new DraftStore(root).LoadAsync(ConnectionScope.From(context)))!;
        Assert.That(finished, Is.SameAs(apply), "The measurement runner must cancel the product's ordinary rate-limit wait.");
        Assert.That(runner.Stopped, Is.True); Assert.That(runner.Mutations, Is.EqualTo(1));
        Assert.That(fixture.Writes, Is.Empty);
        Assert.That(durable.Journal!.Single().Operations.Where(o => o.Key.Kind == "Select").All(o => o.State == ApplyState.Cancelled), Is.True);
        Assert.That(durable.Journal!.Single().Operations.Single(o => o.Key.Kind == "Title").State, Is.EqualTo(failure == "throttle" ? ApplyState.Cancelled : ApplyState.Unknown));
        Assert.That(durable.Fields.Count(f => f.Change is not null), Is.EqualTo(2));
    }

    [Test]
    public async Task UnclassifiedAuthenticationFailureStopsDispatchWithoutClaimingAThrottle()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-perf-auth-guard-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var runner = new LivePerformanceRunner(root, "ghpb-perf51-owned", new ScriptedRunner(_ =>
            new(ProcessCompletion.Exited, true, 0, "[{\"state\":\"error\"}]")));
        var command = new GhCommand(LivePerformanceRun.Gh, ["auth", "status", "--hostname", "github.com"]);
        await runner.RunAsync(command);
        Assert.That(runner.Stopped, Is.True);
        Assert.That(runner.StopReason, Is.EqualTo("AuthenticationUnverified"));
        Assert.That(runner.Records.Single().Throttled, Is.False);
        Assert.That((await runner.RunAsync(command)).Started, Is.False);
    }

    [TestCase("foreign", "ghpb-perf51-owned 00 changed", false)]
    [TestCase("owned", "unexpected title", false)]
    [TestCase("owned", "ghpb-perf51-owned 00 changed", true)]
    public async Task OnlyOwnedApprovedTitleMayDispatchAndItsIntentIsAlreadyDurable(string id, string title, bool allowed)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-perf-guard-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var boundary = new ScriptedRunner(_ =>
        {
            var intent = File.ReadAllText(Path.Combine(root, "mutation-intents.jsonl"));
            Assert.That(intent, Does.Contain("ApplyTitle").And.Contain("owned"));
            return ScriptedRunner.Http("{\"data\":{}}");
        });
        var runner = new LivePerformanceRunner(root, "ghpb-perf51-owned", boundary);
        runner.AllowIssue("owned", "ghpb-perf51-owned 00 baseline", "ghpb-perf51-owned 00 changed");
        var payload = JsonSerializer.Serialize(new { query = "mutation ApplyTitle($input:UpdateIssueInput!){updateIssue(input:$input){issue{id title}}}", variables = new { input = new { id, title } } });
        var command = new GhCommand(LivePerformanceRun.Gh, ["api", "graphql", "--hostname", "github.com", "--method", "POST"], payload);
        if (allowed) Assert.That((await runner.RunAsync(command)).Started, Is.True);
        else Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(command));
        Assert.That(runner.Mutations, Is.EqualTo(allowed ? 1 : 0));
        Assert.That(File.Exists(Path.Combine(root, "mutation-intents.jsonl")), Is.EqualTo(allowed));
    }

    [Test]
    public async Task LiveMutationCeilingStopsBeforeRecordingOrSendingAnotherWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-perf-ceiling-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var runner = new LivePerformanceRunner(root, "ghpb-perf51-owned", new ScriptedRunner(_ => ScriptedRunner.Http("{\"data\":{}}"))) { MutationCeiling = 1 };
        runner.AllowIssue("owned", "ghpb-perf51-owned baseline", "ghpb-perf51-owned changed");
        var payload = JsonSerializer.Serialize(new { query = "mutation PerformanceDelete($issue:ID!){deleteIssue(input:{issueId:$issue}){clientMutationId}}", variables = new { issue = "owned" } });
        var command = new GhCommand(LivePerformanceRun.Gh, ["api", "graphql", "--hostname", "github.com", "--method", "POST"], payload);
        await runner.RunAsync(command);
        Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(command));
        Assert.That(File.ReadAllLines(Path.Combine(root, "mutation-intents.jsonl")), Has.Length.EqualTo(1));
        Assert.That(runner.Mutations, Is.EqualTo(1));
    }
}
