using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class LivePerformanceStageTests
{
    [TestCase("Could not resolve to a node with the global id 'SENTINEL_PRIVATE_TITLE' ghp_SENTINEL_TOKEN Bearer SENTINEL_AUTH https://secret.example/SENTINEL_URL")]
    [TestCase("token = SENTINEL_TOKEN password:SENTINEL_PASSWORD user@SENTINEL_MAIL.example C:/SENTINEL_PATH private_title")]
    [TestCase("Issue already exists.\r\n\u001b[31mSENTINEL_CONTROL")]
    public async Task DiagnosticPersistenceContainsOnlyBoundedSafeWordsAndProtocolMetadata(string message)
    {
        var root = Temp();
        var runner = new LivePerformanceRunner(root, "ghpb-perf51-test", new ScriptedRunner(_ => new(ProcessCompletion.Exited, true, 1,
            "HTTP/2.0 200 OK\r\nX-GitHub-Request-Id: ABCD:1234\r\nX-RateLimit-Remaining: SENTINEL_HEADER\r\n\r\n"
            + JsonSerializer.Serialize(new { errors = new[] { new { type = "UNPROCESSABLE", message } }, data = new { title = "SENTINEL_DATA" } }), "SENTINEL_STDERR")));

        await runner.RunAsync(new(LivePerformanceRun.Gh, ["api", "graphql", "--hostname", "github.com", "--method", "POST"], "{\"query\":\"query{viewer{id}}\"}"));

        var retained = File.ReadAllText(Path.Combine(root, "processes.jsonl"));
        Assert.That(retained, Does.Not.Contain("SENTINEL").And.Not.Contain("private_title").And.Not.Contain("secret.example"));
        Assert.That(runner.Records.Single().Messages.Single().Length, Is.LessThanOrEqualTo(400));
        Assert.That(runner.Records.Single().ErrorTypes, Is.EqualTo(new[] { "UNPROCESSABLE" }));
        Assert.That(runner.Stopped, Is.True);
    }

    [Test]
    public void DiagnosticRetainsUsefulCauseWithoutRenderingUnknownContentsOrUnboundedInput()
    {
        Assert.That(SafeLiveDiagnostic.Render("Item already exists in the project: 'private content'"), Is.EqualTo("item already exists in the project [redacted]"));
        Assert.That(SafeLiveDiagnostic.Render(new string('x', 50000)), Is.EqualTo("[message omitted: too long]"));
    }

    [TestCase("password:\"project already exists\""), TestCase("token: project already exists"), TestCase("secret:\nproject already exists")]
    public void CredentialValuesCannotEscapeRedactionEvenWhenTheirWordsAreAllowlisted(string secret)
        => Assert.That(SafeLiveDiagnostic.Render(secret), Is.EqualTo("[redacted]"));

    [TestCase("Resource \"project's title already exists\" is invalid"), TestCase("Resource \"title already exists")]
    public void MixedOrUnclosedQuotesCannotExposeQuotedContents(string message)
        => Assert.That(SafeLiveDiagnostic.Render(message), Is.EqualTo("resource [redacted]"));

    [Test]
    public void RollingBudgetIncludesFailedIntentsCleanupAndReservedRestorationWithoutDoubleCountingRoots()
    {
        var root = Temp(); var now = DateTimeOffset.UtcNow;
        var child = Path.Combine(root, "stage"); Directory.CreateDirectory(child);
        File.WriteAllLines(Path.Combine(child, "mutation-intents.jsonl"), Enumerable.Range(0, 301).Select(_ => JsonSerializer.Serialize(new { at = now.AddMinutes(-10), operation = "attempt-or-cleanup" })));

        var deferred = LivePerformanceBudget.Check([root, child], 200, now);

        Assert.That(deferred.Allowed, Is.False);
        Assert.That(deferred.RecentWrites, Is.EqualTo(301));
        Assert.That(deferred.ResumeAfter, Is.EqualTo(now.AddMinutes(50).AddSeconds(1)));
        Assert.That(LivePerformanceBudget.Check([root], 200, now.AddHours(1)).Allowed, Is.True);
    }

    internal static string Temp() { var path = Path.Combine(Path.GetTempPath(), "ghpb-stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }

    [Test]
    public async Task SeparateStagesReuseOneVerifiedFixtureAndExactSeedAcrossFixedCounterbalancedSamples()
    {
        var (root, fixture, boundary) = Plan();
        async Task<int> Run(string stage, string label = "main") => await LivePerformanceRun.Stage(fixture, Path.Combine(root, Guid.NewGuid().ToString("N")), stage, label, boundary, false);
        Assert.That(await Run("Prepare"), Is.Zero);
        var owned = boundary.Issues.Keys.Single();
        Assert.That(await Run("Verify"), Is.Zero);
        foreach (var label in new[] { "main", "candidate", "candidate", "main" })
        {
            Assert.That(await Run("Measure", label), Is.Zero);
            Assert.That(boundary.Issues.Keys, Is.EqualTo(new[] { owned }));
            Assert.That(boundary.Issues[owned].Title, Does.EndWith("changed"));
            Assert.That(await Run("Restore"), Is.Zero);
            Assert.That(boundary.Issues[owned].Title, Does.EndWith("baseline"));
        }
        var samples = Directory.GetFiles(fixture, "sample-*.json").Select(p => JsonDocument.Parse(File.ReadAllText(p)).RootElement.GetProperty("initialSha256").GetString()).ToArray();
        Assert.That(samples, Has.Length.EqualTo(4)); Assert.That(samples.Distinct().Count(), Is.EqualTo(1));
        Assert.That(await Run("Cleanup"), Is.Zero); Assert.That(boundary.Issues, Is.Empty); Assert.That(boundary.Membership, Is.Empty);
        Assert.That(await Run("Measure"), Is.EqualTo(1), "Deleted IDs cannot become reusable fixture identities");
    }

    [Test]
    public async Task UncertainAdditionIsNotReplayedAndCanBeIndependentlyVerifiedAndCleaned()
    {
        var (root, fixture, boundary) = Plan(); boundary.FailAdditionAfterCommit = true;
        async Task<int> Run(string stage) => await LivePerformanceRun.Stage(fixture, Path.Combine(root, Guid.NewGuid().ToString("N")), stage, "main", boundary, false);
        Assert.That(await Run("Prepare"), Is.EqualTo(1));
        Assert.That(boundary.Membership, Has.Count.EqualTo(1));
        Assert.That(await Run("Prepare"), Is.EqualTo(1));
        Assert.That(await Run("Verify"), Is.Zero);
        Assert.That(await Run("Cleanup"), Is.Zero);
        Assert.That(boundary.Issues, Is.Empty);
        var adds = Directory.GetFiles(root, "mutation-intents.jsonl", SearchOption.AllDirectories).SelectMany(File.ReadLines).Count(l => l.Contains("PerformanceAdd"));
        Assert.That(adds, Is.EqualTo(1));
    }

    [Test]
    public async Task BudgetDeferralRetainsFixtureAndContinuationWithoutContactingService()
    {
        var (root, fixture, boundary) = Plan();
        File.WriteAllLines(Path.Combine(root, "mutation-intents.jsonl"), Enumerable.Range(0, 480).Select(_ => JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow })));
        var output = Path.Combine(root, "deferred");

        Assert.That(await LivePerformanceRun.Stage(fixture, output, "Prepare", "main", boundary, false), Is.EqualTo(3));

        Assert.That(boundary.Issues, Is.Empty);
        Assert.That(File.Exists(Path.Combine(output, "processes.jsonl")), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(output, "checkpoint.json")), Does.Contain("performance-stage").And.Contain("Deferred"));
    }

    private static (string Root, string Fixture, LiveFixtureBoundary Boundary) Plan()
    {
        var root = Temp(); TestContext.WriteLine("Retained lifecycle artifacts: " + root);
        var fixture = Path.Combine(root, "fixture"); var evidence = Path.Combine(root, "synthetic-gate.txt"); File.WriteAllText(evidence, "Controlled synthetic boundary only");
        var binary = new LivePerformanceRun.Binary(Path.Combine(root, "runner.exe"), new string('a', 40), new string('A', 64), new string('B', 64));
        var plan = new LivePerformanceRun.Plan(fixture, 1, ["main", "candidate", "candidate", "main"], binary, binary, [root], evidence);
        var config = Path.Combine(root, "config.json"); File.WriteAllText(config, JsonSerializer.Serialize(plan)); LivePerformanceRun.Initialize(config);
        return (root, fixture, new());
    }

    [TestCase("Restore"), TestCase("Cleanup")]
    public async Task InterruptedStagesReconcileCommittedEffectsWithoutReplayingMutation(string stage)
    {
        var (root, fixture, boundary) = Plan();
        async Task<int> Run(string action) => await LivePerformanceRun.Stage(fixture, Path.Combine(root, Guid.NewGuid().ToString("N")), action, "main", boundary, false);
        Assert.That(await Run("Prepare"), Is.Zero);
        if (stage == "Restore") { Assert.That(await Run("Measure"), Is.Zero); boundary.LoseRestoreResponseOnce = true; }
        else boundary.LoseDeleteResponseOnce = true;
        Assert.That(await Run(stage), Is.EqualTo(1));
        var operation = stage == "Restore" ? "ApplyTitle" : "PerformanceDelete";
        int Intents() => Directory.GetFiles(root, "mutation-intents.jsonl", SearchOption.AllDirectories).SelectMany(File.ReadLines).Count(l => l.Contains(operation));
        var before = Intents();

        Assert.That(await Run(stage), Is.Zero);

        Assert.That(Intents(), Is.EqualTo(before));
        if (stage == "Restore") Assert.That(await Run("Cleanup"), Is.Zero);
        Assert.That(boundary.Issues, Is.Empty);
    }

    [Test]
    public async Task ServiceCooldownSurvivesStageRestartAndBlocksEvenReadOnlyRequests()
    {
        var (root, fixture, boundary) = Plan(); var until = DateTimeOffset.UtcNow.AddMinutes(10);
        File.WriteAllText(Path.Combine(fixture, "cooldown.json"), JsonSerializer.Serialize(new { NotBefore = until }));
        var output = Path.Combine(root, "cooldown");
        Assert.That(await LivePerformanceRun.Stage(fixture, output, "Verify", "main", boundary, false), Is.EqualTo(3));
        Assert.That(File.Exists(Path.Combine(output, "processes.jsonl")), Is.False);
        Assert.That(JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "checkpoint.json"))).RootElement.GetProperty("resumeAfter").GetDateTimeOffset(), Is.EqualTo(until));
    }

    [Test]
    public void StageOutputCannotEscapeTheRecordedLifecycleBudget()
    {
        var (_, fixture, boundary) = Plan(); var outside = Path.Combine(Temp(), "outside");
        Assert.ThrowsAsync<InvalidOperationException>(() => LivePerformanceRun.Stage(fixture, outside, "Prepare", "main", boundary, false));
        Assert.That(Directory.Exists(outside), Is.False);
    }

    [TestCase("remove"), TestCase("delete")]
    public async Task StillPresentTargetCannotAuthorizeReplayingAnUncertainCleanupMutation(string operation)
    {
        var (root, fixture, boundary) = Plan();
        async Task<int> Run(string action) => await LivePerformanceRun.Stage(fixture, Path.Combine(root, Guid.NewGuid().ToString("N")), action, "main", boundary, false);
        Assert.That(await Run("Prepare"), Is.Zero);
        boundary.LoseRemoveBeforeCommitOnce = operation == "remove"; boundary.LoseDeleteBeforeCommitOnce = operation == "delete";
        Assert.That(await Run("Cleanup"), Is.EqualTo(1));
        int Intents() => Directory.GetFiles(root, "mutation-intents.jsonl", SearchOption.AllDirectories).SelectMany(File.ReadLines).Count();
        var before = Intents();

        Assert.That(await Run("Cleanup"), Is.EqualTo(1));

        Assert.That(Intents(), Is.EqualTo(before)); Assert.That(boundary.Issues, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task HttpDateRetryAfterIsNormalizedAndPersistsBeyondStageRestart()
    {
        var root = Temp(); var future = DateTimeOffset.UtcNow.AddMinutes(10);
        var runner = new LivePerformanceRunner(root, "ghpb-perf51-test", new ScriptedRunner(_ => new(ProcessCompletion.Exited, true, 1,
            $"HTTP/2.0 403 Forbidden\r\nRetry-After: {future:r}\r\n\r\n{{}}"))) { CooldownPath = Path.Combine(root, "cooldown.json") };

        await runner.RunAsync(new(LivePerformanceRun.Gh, ["api", "user", "--hostname", "github.com", "--method", "GET"]));

        Assert.That(runner.StopReason, Is.EqualTo("RateLimited"));
        Assert.That(LivePerformanceBudget.Cooldown([root]), Is.GreaterThanOrEqualTo(future.AddSeconds(-1)));
    }
}
