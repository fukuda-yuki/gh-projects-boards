using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class IssueDetailReaderTests
{
    [TestCase("OPEN"), TestCase("CLOSED")]
    public async Task ReadsOneNodeWithoutMutationAndParsesBodyIdentityAndOrderedComments(string state)
    {
        var runner = new DetailRunner { Response = Response(state: state) };
        var result = await Read(runner);
        Assert.That(result.Result.IsSuccess, Is.True);
        var issue = result.Issue!;
        Assert.Multiple(() => {
            Assert.That(issue.Reference, Is.EqualTo("acme/repo#17"));
            Assert.That(issue.Title, Is.EqualTo("Planning evidence"));
            Assert.That(issue.Url, Is.EqualTo("https://github.com/acme/repo/issues/17"));
            Assert.That(issue.State, Is.EqualTo(state));
            Assert.That(issue.StateReason, Is.EqualTo("COMPLETED"));
            Assert.That(issue.Body, Is.EqualTo("**Body**"));
            Assert.That(issue.BodyHtml, Is.EqualTo("<p><strong>Body</strong></p>"));
            Assert.That(issue.UpdatedAt, Is.EqualTo(DateTimeOffset.Parse("2026-10-15T08:30:00Z")));
            Assert.That(issue.CommentCount, Is.EqualTo(7));
            Assert.That(issue.Comments.Select(c => c.Author), Is.EqualTo(new[] { "ghost", "alice" }));
            Assert.That(issue.Comments[0].Body, Is.EqualTo("Earlier"));
            Assert.That(issue.Comments[0].BodyHtml, Is.EqualTo("<p>Earlier</p>"));
            Assert.That(issue.Comments[0].Url, Is.EqualTo("https://github.com/acme/repo/issues/17#issuecomment-1"));
            Assert.That(issue.Comments[0].CreatedAt, Is.EqualTo(DateTimeOffset.Parse("2026-10-14T00:00:00Z")));
        });
        var request = runner.Calls.Single(c => c.Arguments.Contains("graphql"));
        using var json = JsonDocument.Parse(request.StandardInput!);
        var query = json.RootElement.GetProperty("query").GetString()!;
        Assert.That(query, Does.StartWith("query ").And.Contain("node(id:$id)").And.Contain("comments(last:5)"));
        Assert.That(json.RootElement.GetProperty("variables").GetProperty("id").GetString(), Is.EqualTo("I17"));
        Assert.That(runner.Calls.Any(c => c.StandardInput?.Contains("mutation") == true), Is.False);
    }

    [Test]
    public async Task EmptyBodyAndZeroCommentsRemainKnownEmptyValues()
    {
        var result = await Read(new() { Response = Response(empty: true) });
        Assert.That(result.Result.IsSuccess, Is.True);
        Assert.That(result.Issue!.Body, Is.Empty);
        Assert.That(result.Issue.BodyHtml, Is.Empty);
        Assert.That(result.Issue.Comments, Is.Empty);
        Assert.That(result.Issue.CommentCount, Is.Zero);
    }

    [TestCase("""{"data":{"node":null}}""", FailureKind.NotFoundOrInaccessible)]
    [TestCase("""{"data":{"node":{}}}""", FailureKind.InvalidResponse)]
    [TestCase("""{"data":{"node":null},"errors":[{"type":"NOT_FOUND","message":"missing"}]}""", FailureKind.NotFoundOrInaccessible)]
    [TestCase("""{"data":null,"errors":[{"type":"FORBIDDEN","message":"private"}]}""", FailureKind.PermissionDenied)]
    public async Task MissingOrInaccessibleIssueHasJapaneseReasonAndMalformedResponseFails(string response, FailureKind failure)
    {
        var result = await Read(new() { Response = response });
        Assert.That(result.Issue, Is.Null);
        Assert.That(result.Result.Failure, Is.EqualTo(failure));
        if (failure != FailureKind.InvalidResponse)
            Assert.That(result.FailureReason, Is.EqualTo("Issue が見つからないか、アクセスできません"));
    }

    [Test]
    public async Task GraphQlFailureUsesOnlyBoundedSanitizedReason()
    {
        var runner = new DetailRunner { Response = JsonSerializer.Serialize(new { data = new { node = (object?)null },
            errors = new[] { new { type = "INTERNAL", message = "token=synthetic-secret " + new string('x', 500) } } }) };
        var result = await Read(runner);
        Assert.That(result.Issue, Is.Null);
        Assert.That(result.Result.Failure, Is.EqualTo(FailureKind.GraphQl));
        Assert.That(result.FailureReason.Length, Is.LessThanOrEqualTo(240));
        Assert.That(result.FailureReason, Does.Not.Contain("synthetic-secret"));
    }

    [Test]
    public async Task NetworkFailureDoesNotExposeProcessOutput()
    {
        var result = await Read(new() { NetworkFailure = true });
        Assert.That(result.Result.Failure, Is.EqualTo(FailureKind.Network));
        Assert.That(result.FailureReason, Is.EqualTo("Network"));
    }

    [TestCase(false), TestCase(true)]
    public async Task CancellationReturnsNoIssueAndReleasesConnectionForNextRead(bool running)
    {
        var runner = new DetailRunner { Hold = running };
        var service = new GhConnectionService("fake-gh", "github.com", runner);
        var reader = new IssueDetailReader(service);
        var context = new ConnectionContext("github.com", 42, "fixture-user", "fake-gh");
        using var cancellation = new CancellationTokenSource();
        if (!running) cancellation.Cancel();
        var read = reader.Read(context, "I17", cancellation.Token);
        if (running) { await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); }
        var result = await read;
        Assert.That(result.Issue, Is.Null);
        Assert.That(result.Result.Outcome, Is.EqualTo(ApiOutcome.Cancelled));
        runner.Hold = false;
        Assert.That((await reader.Read(context, "I17")).Result.IsSuccess, Is.True);
    }

    private static Task<IssueDetailRead> Read(DetailRunner runner) =>
        new IssueDetailReader(new("fake-gh", "github.com", runner)).Read(new("github.com", 42, "fixture-user", "fake-gh"), "I17");

    private static string Response(string state = "OPEN", bool empty = false) => JsonSerializer.Serialize(new { data = new { node = new {
        number = 17, title = "Planning evidence", url = "https://github.com/acme/repo/issues/17", state, stateReason = "COMPLETED",
        body = empty ? "" : "**Body**", bodyHTML = empty ? "" : "<p><strong>Body</strong></p>", updatedAt = "2026-10-15T08:30:00Z",
        repository = new { nameWithOwner = "acme/repo" },
        comments = new { totalCount = empty ? 0 : 7, nodes = empty ? [] : new[] {
            new { author = (object?)new { login = "alice" }, createdAt = "2026-10-15T00:00:00Z", url = "https://github.com/acme/repo/issues/17#issuecomment-2", body = "Later", bodyHTML = "<p>Later</p>" },
            new { author = (object?)null, createdAt = "2026-10-14T00:00:00Z", url = "https://github.com/acme/repo/issues/17#issuecomment-1", body = "Earlier", bodyHTML = "<p>Earlier</p>" } } }
    } } });

    private sealed class DetailRunner : IGhProcessRunner
    {
        internal string Response = IssueDetailReaderTests.Response();
        internal bool NetworkFailure, Hold;
        internal List<GhCommand> Calls = [];
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken token = default)
        {
            Calls.Add(command);
            if (command.Arguments[0] == "auth")
                return Exited("""[{"host":"github.com","login":"fixture-user","active":true,"state":"success","tokenSource":"keyring","scopes":"repo,project"}]""");
            if (command.Arguments.Contains("user")) return Http("""{"id":42,"login":"fixture-user"}""");
            if (command.Arguments.Contains("graphql")) {
                Started.TrySetResult();
                if (Hold) {
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { return new(ProcessCompletion.Cancelled); }
                }
                if (NetworkFailure) return new(ProcessCompletion.Exited, true, 1, "unbounded process data", "private stderr");
                return Http(Response);
            }
            throw new AssertionException("Unexpected gh request.");
        }
        private static GhProcessResult Exited(string value) => new(ProcessCompletion.Exited, true, 0, value);
        private static GhProcessResult Http(string value) => Exited("HTTP/2.0 200 OK\r\n\r\n" + value);
    }
}
