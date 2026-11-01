using System.Collections.Immutable;
using System.Diagnostics;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;
// #332: what one publish-record save costs while a large reorder publishes. The record is saved before
// dispatch and after the result of every request, after the first request has been sent. Explicit: a measurement.
[Explicit, Category("Performance")]
internal sealed class PlanPublishRecordProbe
{
    [TestCase(0, 1000)][TestCase(200, 100)][TestCase(200, 1000)]
    public async Task OrderWriteRecordSaves(int history, int count)
    {
        var plan = EvaluationFixture.ReadPlan();
        var snapshot = EvaluationFixture.Simulate(plan, [plan.StatusDate]).Single();
        var day = plan.StatusDate;
        var root = Path.Combine(Path.GetTempPath(), "ghpb-record-probe-" + Guid.NewGuid().ToString("N"));
        var session = await PlanSession.CreateAsync(new(root), snapshot.Document, day);
        var rows = session.Document.State.Rows;
        var leaves = rows.Where(r => !rows.Any(c => c.Parent == r.Identity) && r.Parent is not null && !r.Closed).ToList();
        for (var i = 0; i < history; i++)
            await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(leaves[i % leaves.Count].Identity, PlanField.Title, "t" + i)]), day);
        var project = session.Document.Project.NodeId;
        var targets = session.Document.Baseline.Rows.Take(count).ToArray();
        var writes = targets.Select((r, i) => new PlanWrite(i.ToString(), r.Identity, PlanPublishStage.Order, "updateProjectV2ItemPosition",
            "UpdateProjectV2ItemPositionInput", PlanJson.Text(new { projectId = project, itemId = "item:" + r.Identity, afterId = i == 0 ? null : "item:" + targets[i - 1].Identity }),
            "items { totalCount }")).ToImmutableArray();
        var progress = new PlanPublishProgress(Guid.NewGuid().ToString("N"), writes) { DispatchStarted = true };
        var sw = Stopwatch.StartNew();
        Assert.That((await session.SaveSync(session.Document.Sync with { Publish = progress })).Succeeded, Is.True);
        var first = sw.ElapsedMilliseconds;
        var samples = new List<long>();
        for (var i = 0; i < 10; i++)
        {
            foreach (var state in new[] { PlanWriteState.Dispatched, PlanWriteState.Succeeded })
            {
                progress = progress with { Writes = progress.Writes.SetItem(i, progress.Writes[i] with { State = state }) };
                sw.Restart();
                Assert.That((await session.SaveSync(session.Document.Sync with { Publish = progress })).Succeeded, Is.True);
                samples.Add(sw.ElapsedMilliseconds);
            }
        }
        TestContext.Out.WriteLine($"history={history} writes={count}: first record save {first} ms; per record save median {samples.Order().ElementAt(samples.Count / 2)} ms, max {samples.Max()} ms; per order write (2 saves) ~{2 * samples.Order().ElementAt(samples.Count / 2)} ms");
        Directory.Delete(root, true);
    }
}
