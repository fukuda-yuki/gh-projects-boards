using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class IssueBodyPublisherTests
{
    [TestCase("a\r\nb", "a\rb"), TestCase("a\rb", "a\nb")]
    public void LineEndingsAloneAreUnchanged(string body, string draft) =>
        Assert.That(IssueBodyText.Changed(body, draft), Is.False);

    [TestCase("<!-- ghpb-plan:one -->\n\nbody\n\n", "body", "edited\n\n<!-- ghpb-plan:one -->")]
    [TestCase("\n<!-- ghpb-plan:\nmultiple\n-->\nbody", "body", "edited\n\n<!-- ghpb-plan:\nmultiple\n-->")]
    [TestCase("<!-- ordinary -->body", "<!-- ordinary -->body", "edited")]
    [TestCase("body\n<!-- ghpb-plan:a -->\n<!-- ghpb-plan:b -->", "body", "edited\n\n<!-- ghpb-plan:a -->\n\n<!-- ghpb-plan:b -->")]
    [TestCase("\n  indented\n", "\n  indented\n", "edited")]
    [TestCase("<!-- ghpb-plan:a\r\nb -->\r  indented\r", "  indented", "edited\n\n<!-- ghpb-plan:a\nb -->")]
    public void OnlyPlanMarkersAreHiddenAndPreservedAtEnd(string body, string editor, string sent)
    {
        Assert.That(IssueBodyText.EditorText(body), Is.EqualTo(editor));
        Assert.That(IssueBodyText.BodyToSend(body, "edited"), Is.EqualTo(sent));
    }

    [TestCase(""), TestCase(" \r\n ")]
    public void EmptyDraftKeepsMarkerAlone(string draft) => Assert.That(
        IssueBodyText.BodyToSend("body\n<!-- ghpb-plan:a -->", draft), Is.EqualTo("<!-- ghpb-plan:a -->"));

    [Test]
    public void DraftLineEndingsAreNormalizedAndOtherWhitespaceIsPreserved() => Assert.That(
        IssueBodyText.BodyToSend("body", " \r\n  code\r\n"), Is.EqualTo(" \n  code\n"));

    [Test]
    public async Task PublishRechecksIdentityAndSendsOnlyIdAndNormalizedBodyOnce()
    {
        var runner = new BodyRunner();
        var result = await Publish(runner);
        Assert.That(result.Outcome, Is.EqualTo(IssueBodyPublishOutcome.Published));
        Assert.That(runner.Queries.Count, Is.EqualTo(2));
        Assert.That(runner.Queries[0].GetProperty("query").GetString(), Does.StartWith("query "));
        var mutation = runner.Queries[1];
        Assert.That(mutation.GetProperty("query").GetString(), Does.Contain("updateIssue(input:$input){issue{body}}"));
        var input = mutation.GetProperty("variables").GetProperty("input");
        Assert.That(input.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "id", "body" }));
        Assert.That(input.GetProperty("id").GetString(), Is.EqualTo("I17"));
        Assert.That(input.GetProperty("body").GetString(), Is.EqualTo("new\nbody"));
        Assert.That(runner.Events, Is.EqualTo(new[] { "viewer", "read", "viewer", "write" }));
    }

    [TestCase("other", true, IssueBodyPublishOutcome.Conflict)]
    [TestCase("new\rbody", false, IssueBodyPublishOutcome.AlreadyPublished)]
    [TestCase("old", false, IssueBodyPublishOutcome.Failed)]
    public async Task RereadPreventsUnnecessaryOrUnauthorizedWrites(string remote, bool allowed, IssueBodyPublishOutcome outcome)
    {
        var runner = new BodyRunner { Body = remote, Allowed = allowed };
        var result = await Publish(runner);
        Assert.That(result.Outcome, Is.EqualTo(outcome));
        Assert.That(runner.Queries.Count, Is.EqualTo(1));
        if (outcome == IssueBodyPublishOutcome.Conflict) Assert.That(result.RemoteBody, Is.EqualTo(remote));
        if (outcome == IssueBodyPublishOutcome.Failed) Assert.That(result.FailureReason, Is.EqualTo("この Issue の本文を編集する権限がありません"));
    }

    [TestCase("mutation"), TestCase("mismatch"), TestCase("missing"), TestCase("forbidden"), TestCase("identity")]
    public async Task FailedPublicationHasBoundedReasonAndNeverRetries(string fault)
    {
        var runner = new BodyRunner { Fault = fault };
        var result = await Publish(runner);
        Assert.That(result.Outcome, Is.EqualTo(IssueBodyPublishOutcome.Failed));
        Assert.That(result.Result.IsSuccess, Is.False);
        Assert.That(result.FailureReason, Is.Not.Empty.And.Length.LessThanOrEqualTo(240));
        Assert.That(result.FailureReason, Does.Not.Contain("secret"));
        Assert.That(runner.Queries.Count, Is.EqualTo(fault is "missing" or "forbidden" or "identity" ? 1 : 2));
        if (fault is "missing" or "forbidden") Assert.That(result.FailureReason, Is.EqualTo("Issue が見つからないか、アクセスできません"));
    }

    private static Task<IssueBodyPublish> Publish(BodyRunner runner) => new IssueBodyPublisher(new("fake-gh", "github.com", runner))
        .Publish(new("github.com", 42, "fixture-user", "fake-gh"), "I17", "old", "new\rbody");

    private sealed class BodyRunner : IGhProcessRunner
    {
        internal string Body = "old";
        internal bool Allowed = true;
        internal string? Fault;
        internal List<JsonElement> Queries = [];
        internal List<string> Events = [];
        public Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken token = default)
        {
            GhProcessResult Exited(string text) => new(ProcessCompletion.Exited, true, 0, text);
            GhProcessResult Http(object data) => Exited("HTTP/2.0 200 OK\r\n\r\n" + JsonSerializer.Serialize(data));
            if (command.Arguments[0] == "auth") return Task.FromResult(Exited("""[{"host":"github.com","login":"fixture-user","active":true,"state":"success","tokenSource":"keyring","scopes":"repo,project"}]"""));
            if (command.Arguments.Contains("user")) {
                Events.Add("viewer");
                return Task.FromResult(Http(new { id = Fault == "identity" && Queries.Count > 0 ? 43 : 42, login = "fixture-user" }));
            }
            using var json = JsonDocument.Parse(command.StandardInput!);
            var payload = json.RootElement.Clone(); Queries.Add(payload);
            var write = payload.GetProperty("query").GetString()!.StartsWith("mutation"); Events.Add(write ? "write" : "read");
            if (!write && Fault is "missing" or "forbidden" || write && Fault == "mutation")
                return Task.FromResult(Http(new { data = (object?)null, errors = new[] { new { type = Fault == "missing" ? "NOT_FOUND" : Fault == "forbidden" ? "FORBIDDEN" : "INTERNAL", message = "token=secret " + new string('x', 500) } } }));
            return Task.FromResult(write ? Http(new { data = new { updateIssue = new { issue = new { body = Fault == "mismatch" ? "different" : "new\nbody" } } } })
                : Http(new { data = new { node = new { body = Body, viewerCanUpdate = Allowed } } }));
        }
    }
}
