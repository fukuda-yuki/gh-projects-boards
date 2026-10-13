using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
using static GhProjectsBoards.Tests.ProjectReaderTests;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ProjectReaderPipelineTests
{
    private static ProjectBoundary Paged(int count = 130) => new() { Override = (query, variables) =>
    {
        if (!query.Contains("ProjectItems")) return null;
        var offset = variables.GetProperty("after").ValueKind == JsonValueKind.String ? int.Parse(variables.GetProperty("after").GetString()!) : 0;
        return Response(Project("P1", "items", Page(Enumerable.Range(offset, Math.Min(100, count - offset))
            .Select(i => Item("P1", "T" + i, Page([Value("P1", "P1-status", id: "V" + i)], 1), Issue("I" + i, i + 1))).ToArray(),
            count, offset + 100 < count, offset + 100 < count ? (offset + 100).ToString() : null)));
    } };

    private static async Task<ProjectReadResult> Read(ProjectBoundary boundary, CancellationToken token = default, bool retry = false,
        IProgress<RemoteProgress>? progress = null)
    {
        var service = new GhConnectionService("gh.exe", "github.com", boundary.Runner);
        var context = (await service.ConnectAsync()).Context!;
        using var lease = await service.BeginOperationAsync(context, token, mutation: false);
        return retry ? await PlanSnapshot.ReadConsistentAsync(service, lease, context, new(new("github.com", 42), "P1"), token, progress)
            : await new ProjectReader(service, lease).ReadAsync(context, new(new("github.com", 42), "P1"), token, progress);
    }

    [TestCase(130), TestCase(260)]
    public async Task PagedDetailsOverlapWithinLimitsAndRetainTraversalOrderAndValues(int count)
    {
        var boundary = Paged(count);
        var active = 0; var maximum = 0; var batches = 0;
        var firstWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        boundary.Runner.BeforeResponse = async (command, token) =>
        {
            if (command.StandardInput?.Contains("ProjectItemDetails") != true) return;
            using var json = JsonDocument.Parse(command.StandardInput);
            Assert.That(json.RootElement.GetProperty("variables").GetProperty("ids").GetArrayLength(), Is.InRange(1, 25));
            var query = json.RootElement.GetProperty("query").GetString()!;
            Assert.That(query, Does.Contain("subIssues(first:100)"));
            Assert.That(query, Does.Contain("viewer { databaseId }"));
            Assert.That(query, Does.Contain("milestone { id title dueOn state }"));
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            Assert.That(current, Is.LessThanOrEqualTo(6));
            Interlocked.Increment(ref batches);
            try { await firstWave.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
            finally { Interlocked.Decrement(ref active); }
        };
        using var trace = new PerformanceTrace();
        var reading = Read(boundary);
        var startedBeforeRelease = batches;
        firstWave.TrySetResult();
        var result = await reading;
        Assert.That(startedBeforeRelease, Is.EqualTo(6), "Hold every response to prove that a seventh request cannot start.");
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
        Assert.That(result.Project!.Items.Select(i => i.Id.NodeId), Is.EqualTo(Enumerable.Range(0, count).Select(i => "T" + i)));
        Assert.That(result.Project.Items.All(i => i.Values.Single().OptionId == "todo"), Is.True);
        Assert.That(maximum, Is.InRange(2, 6));
        Assert.That(active, Is.Zero);
        Assert.That(batches, Is.EqualTo((count + 24) / 25));
        Assert.That(trace.Samples.Count(s => s.Kind == "process-query"), Is.EqualTo(boundary.Runner.Commands.Count(c => c.Arguments.Contains("graphql"))));
    }

    [TestCase(false), TestCase(true)]
    public async Task DisappearedDetailIsConcurrentChangeAndConsistentSnapshotRetries(bool retry)
    {
        var boundary = Paged(1); var attempts = 0;
        boundary.DetailsOverride = (_, _) => ++attempts == 1
            ? ScriptedRunner.Http("{\"data\":{\"nodes\":[null]}}") : null;
        var result = await Read(boundary, retry: retry);
        Assert.That(result.Outcome, Is.EqualTo(retry ? ProjectReadOutcome.Complete : ProjectReadOutcome.Partial));
        Assert.That(attempts, Is.EqualTo(retry ? 2 : 1));
        if (!retry) Assert.That(result.Problems.Select(p => p.Kind), Is.EqualTo(new[] { ReadProblemKind.ConcurrentChange }));
    }

    [Test]
    public async Task RemovedDetailWithNotFoundErrorRetriesToComplete()
    {
        var boundary = Paged(1); var attempts = 0;
        boundary.DetailsOverride = (_, _) => ++attempts == 1
            ? ScriptedRunner.Http("""{"data":{"nodes":[null]},"errors":[{"type":"NOT_FOUND","path":["nodes",0]}]}""", exit: 1) : null;
        var result = await Read(boundary, retry: true);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
        Assert.That(attempts, Is.EqualTo(2));
    }

    [TestCase("FORBIDDEN", "[\"nodes\",0]", false)]
    [TestCase("NOT_FOUND", "[\"nodes\",1]", false)]
    [TestCase("NOT_FOUND", "[\"nodes\",-1]", false)]
    [TestCase("NOT_FOUND", "[\"nodes\",\"0\"]", false)]
    [TestCase("NOT_FOUND", "[\"nodes\",0,\"content\"]", false)]
    [TestCase("NOT_FOUND", "[\"other\",0]", false)]
    [TestCase("NOT_FOUND", "[\"nodes\",0]", true)]
    [TestCase("NOT_FOUND", "[\"nodes\",0]", false, true)]
    public async Task OtherDetailErrorsRemainApiFailuresWithoutRetry(string type, string path, bool present, bool mixed = false)
    {
        var boundary = Paged(1); var attempts = 0;
        var node = present ? JsonSerializer.Serialize(Item("P1", "T0")) : "null";
        var extra = mixed ? ",{\"type\":\"FORBIDDEN\",\"path\":[\"nodes\",0]}" : "";
        boundary.DetailsOverride = (_, _) =>
        {
            attempts++;
            return ScriptedRunner.Http($"{{\"data\":{{\"nodes\":[{node}]}},\"errors\":[{{\"type\":\"{type}\",\"path\":{path}}}{extra}]}}", exit: 1);
        };
        var result = await Read(boundary, retry: true);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial));
        Assert.That(result.Problems.Any(p => p.Kind == ReadProblemKind.Api), Is.True);
        Assert.That(attempts, Is.EqualTo(1));
    }

    [TestCase("id"), TestCase("__typename")]
    public async Task MismatchedDetailIdentityRejectsSnapshot(string property)
    {
        var boundary = Paged(1);
        var node = JsonSerializer.SerializeToNode(Item("P1", "T0"))!; node[property] = "wrong";
        boundary.DetailsOverride = (_, _) => ScriptedRunner.Http(JsonSerializer.Serialize(new { data = new { nodes = new[] { node } } }));
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial));
        Assert.That(result.Problems.Select(p => p.Kind), Does.Contain(ReadProblemKind.ScopeMismatch));
    }

    [Test]
    public async Task FailedDetailStopsDispatchAndNeverInventsEmptyValues()
    {
        var boundary = Paged(); var sent = 0;
        boundary.DetailsOverride = (_, _) => { sent++; return ScriptedRunner.Http("{}", 503, 1); };
        var result = await Read(boundary);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial));
        Assert.That(result.Problems.Any(p => p.Failure == FailureKind.Network && p.HttpStatus == 503), Is.True);
        Assert.That(result.Project!.Items.SelectMany(i => i.Values).Any(v => v.Availability == ValueAvailability.Empty), Is.False);
        Assert.That(sent, Is.EqualTo(1), "A synchronous failure must be observed before dispatching another batch.");
    }

    [Test]
    public async Task LaterBatchFailureStopsNewSendsAndWaitsForEarlierInFlightDetails()
    {
        var boundary = Paged(260); var sent = 0; var active = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        boundary.Runner.BeforeResponse = async (command, token) =>
        {
            if (command.StandardInput?.Contains("ProjectItemDetails") != true) return;
            var number = Interlocked.Increment(ref sent);
            if (number == 6) return;
            Interlocked.Increment(ref active);
            try { await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
            finally { Interlocked.Decrement(ref active); }
        };
        boundary.DetailsOverride = (_, variables) =>
        {
            if (variables.GetProperty("ids")[0].GetString() != "T125") return null;
            failed.TrySetResult();
            return ScriptedRunner.Http("{}", 503, 1);
        };
        var reading = Read(boundary);
        try
        {
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(reading.IsCompleted, Is.False, "The read must await its earlier in-flight requests before returning.");
        }
        finally { release.TrySetResult(); }
        var result = await reading;
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Partial));
        Assert.That(result.Problems.Any(p => p.HttpStatus == 503), Is.True);
        Assert.That(sent, Is.EqualTo(6));
        Assert.That(active, Is.Zero);
        Assert.That(result.Project!.Items.SelectMany(i => i.Values).Any(v => v.Availability == ValueAvailability.Empty), Is.False);
    }

    [Test]
    public async Task AcceptedDetailsReportProgressWhileTheNextLightPageIsStillWaiting()
    {
        var boundary = Paged();
        var pageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        boundary.Runner.BeforeResponse = async (command, token) =>
        {
            if (command.StandardInput is not { } input) return;
            using var json = JsonDocument.Parse(input);
            var query = json.RootElement.GetProperty("query").GetString()!;
            if (query.Contains("ProjectItemDetails"))
                await pageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            if (query.Contains("ProjectItems") && json.RootElement.GetProperty("variables").GetProperty("after").ValueKind == JsonValueKind.String)
            {
                pageStarted.TrySetResult();
                await progressObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        };
        var counts = new List<int>();
        var result = await Read(boundary, progress: new InlineProgress(value =>
        {
            if (value.Stage != "プロジェクトの項目を取得" || value.Completed is not { } count) return;
            counts.Add(count);
            if (count == 25) progressObserved.TrySetResult();
        }));
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
        Assert.That(progressObserved.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(counts, Is.EqualTo(Enumerable.Range(0, 131)));
    }

    private sealed class InlineProgress(Action<RemoteProgress> report) : IProgress<RemoteProgress>
    {
        public void Report(RemoteProgress value) => report(value);
    }

    [Test]
    public async Task CancellationDuringDetailsReturnsCancelledAfterAllSendsEnd()
    {
        var boundary = Paged(); using var cancellation = new CancellationTokenSource(); var active = 0;
        boundary.Runner.BeforeResponse = async (command, token) =>
        {
            if (command.StandardInput?.Contains("ProjectItemDetails") != true) return;
            Interlocked.Increment(ref active);
            cancellation.Cancel();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Interlocked.Decrement(ref active); }
        };
        var result = await Read(boundary, cancellation.Token);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Cancelled));
        Assert.That(active, Is.Zero);
    }
}
