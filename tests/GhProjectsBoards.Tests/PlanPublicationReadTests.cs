using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #201: a publish reads only the items it is about to write, never the whole Project. These cases assert
// the queries the fake actually received, because the offline fake serves reads locally and a timing
// comparison cannot show the read scope.
internal sealed partial class PlanPublisherTests
{
    [Test]
    public async Task PublishingReadsOnlyTheWrittenItemAndNeverTheProjectItemConnection()
    {
        await Start(3);
        File.WriteAllText(Path.Combine(root, "calls.jsonl"), "");
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Estimate, 5m)]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var queries = RecordedQueries();
        Assert.That(queries, Has.None.Contains("ProjectItems("), "A publish must not walk the Project item connection.");
        Assert.That(queries, Has.Some.Contains("PlanPublishItemDetails"),
            "The publication read resolves the written rows by node ID and reads their details in the same request.");
        Assert.That(queries.Count(q => q.Contains("PlanPublishItemDetails", StringComparison.Ordinal)), Is.EqualTo(2),
            "One scoped read before the write and one verifying it.");
        Assert.That(ScopedIssueIds(), Is.EquivalentTo(new[] { "I2" }), "Only the written Issue is read, before and after the write.");
        Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I2").Row.Estimate, Is.EqualTo(5m));
    }

    // #201: the traffic of a one-task publish, the case whose latency the PMO waits for, asserted exactly.
    // The Connection preflight, one metadata read, the scoped read before the write, the write and the
    // verification read. Any extra request is a read that was already known; a missing one would skip
    // conflict detection or verification.
    [Test]
    public async Task OneTaskPublishReadsMetadataOnceAndSendsOnlyTheWrittenItem()
    {
        await Start(3);
        File.WriteAllText(Path.Combine(root, "calls.jsonl"), "");
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Estimate, 5m)]), Today);
        var processesBefore = runner.Count;
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var calls = RecordedCalls();
        TestContext.Out.WriteLine("One-task publish calls (" + calls.Length + " recorded, " + (runner.Count - processesBefore) + " processes): " + string.Join(" | ", calls));
        Assert.Multiple(() =>
        {
            // --version is not recorded by the fake, so it is covered by the process count.
            Assert.That(calls, Is.EqualTo(new[] {
                "auth status", "GET user",
                "query ProjectFields", "query PlanPublishItemDetails", "query ProjectMilestones", "query ProjectLabels",
                "mutation PlanPublish",
                "query PlanPublishItemDetails"
            }), "A one-task publish sends its connection preflight, one Project/field/option metadata read, the written item before and after the write and that write; no complete Project read, repeated metadata read or separate item-detail request.");
            Assert.That(runner.Count - processesBefore, Is.EqualTo(9), "Nine gh processes: version check, authentication, identity, the one metadata read, the two scope reads and the write.");
            Assert.That(RecordedQueries(), Has.None.Contains("ProjectItems("), "A publish must not read the whole Project.");
            Assert.That(calls.Count(call => call == "query ProjectFields"), Is.EqualTo(1), "Project, field and option metadata is read once per publish.");
            Assert.That(ScopedIssueIds(), Is.EquivalentTo(new[] { "I2" }), "Only the written Issue is resolved to its Project item.");
        });
    }

    // #201: the scope also carries the rows a write names. A child-order write names the parent and its
    // children, so those are read and verified; no other row of the Project is.
    [Test]
    public async Task AChildOrderPublishReadsTheParentAndItsChildrenOnly()
    {
        await Start(3, state => state with {
            Issues = state.Issues.Select(i => i.Row.Identity == "I1" ? i : i with { Row = i.Row with { Parent = "I1" } }).ToImmutableArray(),
            SubOrders = state.SubOrders.Add("I1", ["I2", "I3"])
        });
        await session.Execute(new MovePlanRows(["I3"], "I2"), Today);
        File.WriteAllText(Path.Combine(root, "calls.jsonl"), "");
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(RecordedQueries(), Has.None.Contains("ProjectItems("), "A publish must not read the whole Project.");
        Assert.That(ScopedIssueIds(), Is.EquivalentTo(new[] { "I1", "I2", "I3" }),
            "Only the parent whose child order is written and its children are resolved to their Project items.");
        Assert.That(FakePlanEditor.Load(root).SubOrders["I1"], Is.EqualTo(new[] { "I3", "I2" }));
    }

    // #201: GitHub refuses a query that could return more than 500,000 nodes, and the fake does too. A weekly
    // publish moves hundreds of successors, so its written items span many read batches; every one of them
    // is read before the writes and verified after them.
    [Test]
    public async Task APublishOfManyTasksReadsEveryWrittenItemWithinGitHubsQueryLimit()
    {
        await Start(60);
        File.WriteAllText(Path.Combine(root, "calls.jsonl"), "");
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell,
            Enumerable.Range(1, 60).Select(i => new PlanCellChange("I" + i, PlanField.Estimate, 5m)).ToImmutableArray()), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(ScopedIssueIds(), Is.EquivalentTo(Enumerable.Range(1, 60).Select(i => "I" + i)));
        Assert.That(FakePlanEditor.Load(root).Issues.Select(i => i.Row.Estimate), Is.All.EqualTo(5m));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero, "The verified publish leaves nothing unpublished.");
    }

    // #201: the read takes a few memberships per Issue to stay inside the limit, so an Issue that belongs to
    // many Projects (a task carried across versions) finds this Project's item on a later membership page.
    [Test]
    public async Task AnIssueInManyProjectsIsReadFromALaterMembershipPage()
    {
        await Start(3, state => state with { OtherProjects = 12 });
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Estimate, 5m)]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I2").Row.Estimate, Is.EqualTo(5m));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero, "The written item was found and verified.");
    }

    [Test]
    public async Task PublishingSeesARemoteChangeOnAWrittenItemItDidNotChange()
    {
        await Start(3);
        File.WriteAllText(Path.Combine(root, "calls.jsonl"), "");
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i =>
            i.Row.Identity == "I2" ? i with { Row = i.Row with { Title = "Changed elsewhere" } } : i).ToImmutableArray() });
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Estimate, 5m)]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I2").Title, Is.EqualTo("Changed elsewhere"),
            "The item being written is read before the write, so its other remote values are adopted as today.");
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I2").Row.Estimate, Is.EqualTo(5m));
        Assert.That(RecordedQueries(), Has.None.Contains("ProjectItems("));
    }

    [Test]
    public async Task PublishingDoesNotOverwriteARemoteChangeToTheFieldItWouldWrite()
    {
        await Start(3);
        File.WriteAllText(Path.Combine(root, "calls.jsonl"), "");
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i =>
            i.Row.Identity == "I2" ? i with { Row = i.Row with { Estimate = 9m } } : i).ToImmutableArray() });
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Estimate, 5m)]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(session.Document.Sync.Conflicts.Any(c => c.Identity == "I2" && c.Field == PlanField.Estimate), Is.True);
        Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I2").Row.Estimate, Is.EqualTo(9m),
            "The publish writes exactly what the review showed, and a remote change to a written item blocks it.");
        Assert.That(RecordedQueries(), Has.None.Contains("ProjectItems("));
    }

    [Test]
    public async Task AChangeToAnItemThePublishDoesNotWriteArrivesWithTheNextRefresh()
    {
        await Start(3);
        File.WriteAllText(Path.Combine(root, "calls.jsonl"), "");
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Estimate, 5m)]), Today);
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i =>
            i.Row.Identity == "I3" ? i with { Row = i.Row with { Title = "Changed elsewhere" } } : i).ToImmutableArray() });
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I3").Title, Is.EqualTo("Task 3"),
            "A publish does not refresh the plan: a change to an item it does not write is not seen.");
        Assert.That(session.Document.Sync.Conflicts, Is.Empty);
        var refresh = await publisher.RefreshAsync(session, Today);
        Assert.That(refresh.Succeeded, Is.True, refresh.Error);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I3").Title, Is.EqualTo("Changed elsewhere"),
            "Other GitHub changes arrive with 最新の情報に更新.");
    }

    private string[] RecordedQueries()
    {
        var path = Path.Combine(root, "calls.jsonl");
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Select(call => call.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String ? query.GetString()! : "")
                .Where(query => query.Length > 0).ToArray()
            : [];
    }

    // Each fake-gh invocation once, labelled by what it does: the connection preflight commands and the
    // GraphQL operation name. The call table of a publish is observable traffic, so it is asserted in order.
    private string[] RecordedCalls()
    {
        var path = Path.Combine(root, "calls.jsonl");
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Select(call =>
                {
                    var operation = call.GetProperty("operation").GetString()!;
                    if (operation != "api") return operation == "auth" ? "auth status" : operation;
                    var endpoint = call.TryGetProperty("endpoint", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
                    if (endpoint == "user") return "GET user";
                    var query = call.TryGetProperty("query", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
                    var match = System.Text.RegularExpressions.Regex.Match(query, @"\A\s*(query|mutation)\s+(\w+)");
                    return match.Success ? match.Groups[1].Value + " " + match.Groups[2].Value : "api " + endpoint;
                }).ToArray()
            : [];
    }

    // The Issue identities the scoped publication read requested, deduplicated across its requests.
    private string[] ScopedIssueIds()
    {
        var path = Path.Combine(root, "calls.jsonl");
        return File.ReadAllLines(path).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(call => call.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String &&
                query.GetString()!.Contains("PlanPublishItemDetails", StringComparison.Ordinal))
            .SelectMany(call => call.GetProperty("variables").GetProperty("ids").EnumerateArray().Select(id => id.GetString()!))
            .Distinct(StringComparer.Ordinal).ToArray();
    }
}
