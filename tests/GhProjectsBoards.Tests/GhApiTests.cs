using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
[Category("Unit")]
internal sealed class GhApiTests
{
    [TestCase("https://other.example/repos/x/y")]
    [TestCase("--hostname")]
    [TestCase("repos/{owner}/{repo}/issues")]
    [TestCase("../graphql")]
    [TestCase("graphql")]
    [TestCase("graphql?query=mutation")]
    [TestCase("g%72aphql")]
    public void RejectsInferredOrAmbiguousRestDestinations(string endpoint)
        => Assert.Throws<ArgumentException>(() => ApiRequest.Rest("GET", endpoint));

    [TestCase("github.com --hostname evil.example")]
    [TestCase("https://github.com")]
    [TestCase("github.com/other")]
    public async Task RejectsInvalidHostsBeforeStartingGh(string host)
    {
        var runner = new ScriptedRunner(_ => ScriptedRunner.Http("{}"));
        var result = await new GhApiTransport(runner, "gh.exe").SendAsync(host, ApiRequest.Rest("GET", "user"));
        Assert.That(result.Failure, Is.EqualTo(FailureKind.InvalidInput));
        Assert.That(runner.Commands, Is.Empty);
    }

    [Test]
    public void ClassifiesGraphQlFromTheOperationAndKeepsValuesInVariables()
    {
        Assert.That(ApiRequest.GraphQl("  mutation M($input: X!) { change(input:$input) { id } }", new { input = "query text" }).IsMutation, Is.True);
        Assert.That(ApiRequest.GraphQl("query { viewer { id } }").IsMutation, Is.False);
        Assert.Throws<ArgumentException>(() => ApiRequest.GraphQl("# misleading query\nmutation { change { id } }"));
        Assert.Throws<ArgumentException>(() => ApiRequest.Rest("GET", "user", new { query = "mutation" }));
    }

    [Test]
    public async Task GraphQlErrorsWithPartialDataAreNotACompleteSuccess()
    {
        var runner = new ScriptedRunner(_ => ScriptedRunner.Http("""
            {"data":{"first":{"id":"ok"},"second":null},"errors":[{"type":"FORBIDDEN","message":"synthetic-secret-and-body"}]}
            """));
        var result = await new GhApiTransport(runner, "gh.exe").SendAsync("github.com",
            ApiRequest.GraphQl("mutation { first: updateIssue(input: {}) { clientMutationId } }"));
        Assert.That(result.Outcome, Is.EqualTo(ApiOutcome.Unknown));
        Assert.That(result.Failure, Is.EqualTo(FailureKind.GraphQl));
        Assert.That(result.GraphQlErrors, Is.EqualTo(new[] { "FORBIDDEN" }));
        Assert.That(result.Data!.Value.GetProperty("data").GetProperty("first").GetProperty("id").GetString(), Is.EqualTo("ok"));
        Assert.That(result.ToString(), Does.Not.Contain("synthetic-secret"));
        Assert.That(runner.Commands, Has.Count.EqualTo(1), "Never automatically resend a partially applied mutation.");
    }

    [TestCase(401, FailureKind.AuthenticationExpired)]
    [TestCase(403, FailureKind.PermissionDenied)]
    [TestCase(404, FailureKind.NotFoundOrInaccessible)]
    [TestCase(410, FailureKind.NotFoundOrInaccessible)]
    [TestCase(429, FailureKind.RateLimited)]
    [TestCase(503, FailureKind.Network)]
    public async Task ClassifiesHttpFailuresWithoutEchoingRemoteMessages(int status, FailureKind expected)
    {
        var runner = new ScriptedRunner(_ => ScriptedRunner.Http("{\"message\":\"synthetic-secret\"}", status, 1));
        var result = await new GhApiTransport(runner, "gh.exe").SendAsync("github.com", ApiRequest.Rest("GET", "user"));
        Assert.That(result.Outcome, Is.EqualTo(ApiOutcome.Failed));
        Assert.That(result.Failure, Is.EqualTo(expected));
        Assert.That(result.ToString(), Does.Not.Contain("synthetic-secret"));
    }

    [Test]
    public async Task RetainsRetryAfterWithoutSleepingOrRetrying()
    {
        var runner = new ScriptedRunner(_ => ScriptedRunner.Http("{}", 403, 1, "Retry-After: 120\r\n"));
        var result = await new GhApiTransport(runner, "gh.exe").SendAsync("github.com", ApiRequest.Rest("GET", "user"));
        Assert.That(result.Failure, Is.EqualTo(FailureKind.RateLimited));
        Assert.That(result.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(120)));
        Assert.That(runner.Commands, Has.Count.EqualTo(1));
    }

    [TestCase(ProcessCompletion.TimedOut, true, ApiOutcome.Unknown, FailureKind.TimedOut)]
    [TestCase(ProcessCompletion.Cancelled, true, ApiOutcome.Unknown, FailureKind.Cancelled)]
    [TestCase(ProcessCompletion.IoFailed, true, ApiOutcome.Unknown, FailureKind.Network)]
    [TestCase(ProcessCompletion.TimedOut, false, ApiOutcome.TimedOut, FailureKind.TimedOut)]
    [TestCase(ProcessCompletion.NotFound, true, ApiOutcome.Failed, FailureKind.MissingExecutable)]
    public async Task DistinguishesInterruptedWritesFromReads(ProcessCompletion completion, bool mutation, ApiOutcome outcome, FailureKind failure)
    {
        var runner = new ScriptedRunner(_ => new GhProcessResult(completion, completion != ProcessCompletion.NotFound));
        var result = await new GhApiTransport(runner, "gh.exe").SendAsync("github.com",
            ApiRequest.Rest(mutation ? "PATCH" : "GET", "repos/example/sandbox/issues/1"));
        Assert.That(result.Outcome, Is.EqualTo(outcome));
        Assert.That(result.Failure, Is.EqualTo(failure));
        Assert.That(runner.Commands, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task CancellationBeforeDispatchIsNotAnUnknownWrite()
    {
        var runner = new ScriptedRunner(_ => new GhProcessResult(ProcessCompletion.Cancelled));
        var result = await new GhApiTransport(runner, "gh.exe").SendAsync("github.com", ApiRequest.Rest("POST", "repos/example/sandbox/issues"));
        Assert.That(result.Outcome, Is.EqualTo(ApiOutcome.Cancelled));
    }

    [Test]
    public async Task SendsJsonOnStdinWithExplicitHostAndMethod()
    {
        const string title = "日本語の\"題名\" & $(never execute)";
        const string body = "本文\n二行目\r\n引用\"と\\記号";
        var runner = new ScriptedRunner(_ => ScriptedRunner.Http("{\"number\":9}", 201));
        var transport = new GhApiTransport(runner, "chosen-gh.exe");
        var request = ApiRequest.Rest("POST", "repos/example/sandbox/issues", new { title, body });
        var result = await transport.SendAsync("github.com", request);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.HttpStatus, Is.EqualTo(201));
        Assert.That(result.Data!.Value.GetProperty("number").GetInt32(), Is.EqualTo(9));
        Assert.That(runner.Commands, Has.Count.EqualTo(1));
        var command = runner.Commands.Single();
        Assert.That(command.Executable, Is.EqualTo("chosen-gh.exe"));
        Assert.That(command.Arguments, Is.EqualTo(new[] { "api", "repos/example/sandbox/issues", "--hostname", "github.com",
            "--method", "POST", "--include", "--input", "-" }));
        using var payload = JsonDocument.Parse(command.StandardInput!);
        Assert.That(payload.RootElement.GetProperty("title").GetString(), Is.EqualTo(title));
        Assert.That(payload.RootElement.GetProperty("body").GetString(), Is.EqualTo(body));
        Assert.That(command.Timeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
    }
}
