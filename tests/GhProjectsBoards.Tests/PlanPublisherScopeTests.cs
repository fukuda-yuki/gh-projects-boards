using System.Diagnostics;
using System.Text.Json;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

internal sealed partial class PlanPublisherTests
{
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
