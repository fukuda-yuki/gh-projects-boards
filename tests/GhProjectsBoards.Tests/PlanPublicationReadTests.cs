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
        Assert.That(queries, Has.Some.Contains("PlanPublishItems"), "The publication read resolves the written rows by node ID.");
        Assert.That(queries, Has.Some.Contains("ProjectItemDetails"), "The publication read reads those items through the existing item path.");
        Assert.That(ReadItemIds(), Is.EquivalentTo(new[] { "T-I2" }), "Only the written item is read, before and after the write.");
        // The request shape of one publish attempt: one scoped item read before the write and one after it,
        // instead of the two complete traversals a 1,042-item Project needs (about 56 requests each).
        Assert.That(queries.Count(q => q.Contains("ProjectItemDetails", StringComparison.Ordinal)), Is.EqualTo(2));
        Assert.That(queries.Count(q => q.Contains("PlanPublishItems", StringComparison.Ordinal)), Is.EqualTo(2));
        Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I2").Row.Estimate, Is.EqualTo(5m));
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

    // The item node IDs the publication read requested, deduplicated across its requests.
    private string[] ReadItemIds()
    {
        var path = Path.Combine(root, "calls.jsonl");
        return File.ReadAllLines(path).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(call => call.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String &&
                query.GetString()!.Contains("ProjectItemDetails", StringComparison.Ordinal))
            .SelectMany(call => call.GetProperty("variables").GetProperty("ids").EnumerateArray().Select(id => id.GetString()!))
            .Distinct(StringComparer.Ordinal).ToArray();
    }
}
