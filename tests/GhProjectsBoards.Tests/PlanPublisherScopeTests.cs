using System.Diagnostics;
using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

internal sealed partial class PlanPublisherTests
{
    [Test]
    public async Task EffortEditWritesItsChangedDatesAndDependentDatesButLeavesUnrelatedIssueUntouched()
    {
        await Start(3, state => state with { Issues = state.Issues.Select((i, n) => i with { Row = i.Row with {
            Estimate = 8, Remaining = 8, Start = n == 1 ? Today.AddDays(1) : Today,
            End = n == 1 ? Today.AddDays(1) : Today, Predecessors = n == 1 ? ["I1"] : []
        } }).ToImmutableArray() });
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Remaining, 16m)]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var writes = File.ReadAllLines(Path.Combine(root, "plan-mutations.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .SelectMany(r => r.GetProperty("variables").EnumerateObject().Select(v => v.Value)).ToArray();
        Assert.That(writes.Select(w => (w.GetProperty("itemId").GetString(), w.GetProperty("fieldId").GetString())),
            Is.EquivalentTo(new[] { ("T-I1", "F-Remaining"), ("T-I1", "F-End"), ("T-I2", "F-End") }));
        // The stored start is an explicit plan date. Only the dependent end moves.
        Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I2").Row.Start, Is.EqualTo(Today.AddDays(1)));
        Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I3").Row,
            Is.EqualTo(new PlanRow("I3", "Task 3", "acme/repo") { Estimate = 8, Remaining = 8, Start = Today, End = Today }));
        TestContext.Out.WriteLine("Dependent schedule mutation audit: " + string.Join('\n', writes.Select(w => w.GetRawText())));
    }

    [Test]
    public async Task MovingLastOfTwentyFiveChildrenToFirstDoesNotRewriteTheOtherChildren()
    {
        await Start(26, state => state with { SubOrders = state.SubOrders.Add("I1", Enumerable.Range(2, 25).Select(i => "I" + i).ToImmutableArray()), Issues = state.Issues.Select((i, n) => n == 0 ? i : i with { Row = i.Row with { Parent = "I1" } }).ToImmutableArray() });
        await session.Execute(new MovePlanSubtrees(["I26"], "I2"), Today);
        var watch = Stopwatch.StartNew();
        var result = await publisher.PublishAsync(session, Today);
        TestContext.Out.WriteLine($"25-child reorder publish: {watch.Elapsed.TotalMilliseconds:F1} ms; batches={FakePlanEditor.Load(root).MutationBatches}");
        Assert.That(result.Succeeded, Is.True, result.Error);
        var expected = new[] { "I26" }.Concat(Enumerable.Range(2, 24).Select(i => "I" + i));
        Assert.That(FakePlanEditor.Load(root).SubOrders["I1"], Is.EqualTo(expected));
        var writes = File.ReadAllLines(Path.Combine(root, "plan-mutations.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(r => r.GetProperty("query").GetString()!.Contains("reprioritizeSubIssue"))
            .SelectMany(r => r.GetProperty("variables").EnumerateObject().Select(v => v.Value)).ToArray();
        Assert.That(writes.Select(w => w.GetProperty("subIssueId").GetString()), Is.EqualTo(new[] { "I26" }));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }

    [Test]
    public async Task VersionScaleTitleEditWritesOnlyThatTitleAndSecondPublishWritesNothing()
    {
        await Start(1040);
        var initial = FakePlanEditor.Load(root);
        var watch = Stopwatch.StartNew();
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I501", PlanField.Title, "週次計画の更新")]), Today);
        TestContext.Out.WriteLine($"1040-row title edit and durable save: {watch.Elapsed.TotalMilliseconds:F1} ms");
        var stages = new List<string>();
        var before = runner.Count;
        watch.Restart();
        var result = await publisher.PublishAsync(session, Today, progressReporter: new ScopeProgress(p =>
            stages.Add($"{watch.Elapsed.TotalMilliseconds:F1} ms: {p}")));
        TestContext.Out.WriteLine($"1040-row title publish: {watch.Elapsed.TotalMilliseconds:F1} ms, {runner.Count - before} gh processes\n" + string.Join('\n', stages));
        Assert.That(result.Succeeded, Is.True, result.Error);
        var writes = File.ReadAllLines(Path.Combine(root, "plan-mutations.jsonl")).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            TestContext.Out.WriteLine("Mutation audit: " + string.Join('\n', writes.Select(w => w.RootElement.GetRawText())));
            Assert.That(result.Review!.Writes.Select(w => (w.Identity, w.Mutation)), Is.EqualTo(new[] { ("I501", "updateIssue") }));
            Assert.That(writes.Length, Is.EqualTo(1));
            var after = FakePlanEditor.Load(root);
            Assert.That(after.Issues.Select(i => i.Row), Is.EqualTo(initial.Issues.Select(i => i.Row.Identity == "I501" ? i.Row with { Title = "週次計画の更新" } : i.Row)));
            var count = after.MutationBatches;
            watch.Restart();
            Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(count));
            TestContext.Out.WriteLine($"Unchanged second publish: {watch.Elapsed.TotalMilliseconds:F1} ms, zero mutations");
        }
        finally { foreach (var write in writes) write.Dispose(); }
    }

    private sealed class ScopeProgress(Action<GhProjectsBoards.Core.Projects.RemoteProgress> report) : IProgress<GhProjectsBoards.Core.Projects.RemoteProgress>
    {
        public void Report(GhProjectsBoards.Core.Projects.RemoteProgress value) => report(value);
    }
}
