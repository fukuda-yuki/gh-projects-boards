using System.Collections.Immutable;
using System.Diagnostics;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;
// #332: times row operations and publish-record saves at 0/50/200 retained Undo entries on the 1,040-row fixture.
// Explicit: a measurement, not a pass/fail check; the bounded regression lives in PlanSaveCostTests.
[Explicit, Category("Performance")]
internal sealed class PlanSaveLatencyProbe
{
    [TestCase(0)][TestCase(50)][TestCase(200)]
    public async Task RowOperations(int history)
    {
        var plan = EvaluationFixture.ReadPlan();
        var snapshot = EvaluationFixture.Simulate(plan, [plan.StatusDate]).Single();
        var day = plan.StatusDate;
        var root = Path.Combine(Path.GetTempPath(), "ghpb-probe-" + Guid.NewGuid().ToString("N"));
        var store = new PlanStore(root);
        // A real Project carries about a dozen further fields on every item; the fixture carries none.
        var extra = Enumerable.Range(1, 12).Select(i => new PlanColumnDefinition("X" + i, "Extra " + i, "TEXT")).ToImmutableArray();
        PlanRow WithExtras(PlanRow r) => r with { ExtraValues = extra.ToImmutableDictionary(c => "Project:" + c.Id, c => new PlanExtraValue(ValueAvailability.Present, "\"" + c.Name + " " + r.Identity + "\"")) };
        var document = snapshot.Document with {
            State = snapshot.Document.State with { Rows = snapshot.Document.State.Rows.Select(WithExtras).ToImmutableArray() },
            Baseline = snapshot.Document.Baseline with { Rows = snapshot.Document.Baseline.Rows.Select(WithExtras).ToImmutableArray(), Columns = snapshot.Document.Baseline.Columns.AddRange(extra) } };
        var session = await PlanSession.CreateAsync(store, document, day);
        var rows = session.Document.State.Rows;
        var leaves = rows.Where(r => !rows.Any(c => c.Parent == r.Identity) && r.Parent is not null && !r.Closed).ToList();
        for (var i = 0; i < history; i++)
            await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(leaves[i % leaves.Count].Identity, PlanField.Title, "t" + i)]), day);
        var file = store.FileFor(session.Document.Project);
        TestContext.Out.WriteLine($"rows={rows.Length} history={session.UndoCount} file={new FileInfo(file).Length / 1024} KB");
        // Two new rows next to an open leaf: the fixture chains its tasks, so existing rows refuse an indent.
        var anchor = leaves[leaves.Count / 2]; var parent = anchor.Parent;
        var sibling = rows.Where(r => r.Parent == parent).Select(r => r.Identity).ToList();
        async Task Time(string name, Func<Task<PlanSaveResult>> run)
        {
            var sw = Stopwatch.StartNew(); var task = run(); var accepted = sw.ElapsedMilliseconds; var r = await task; var total = sw.ElapsedMilliseconds;
            Assert.That(r.Succeeded, Is.True, r.Error);
            TestContext.Out.WriteLine($"{name,-26} command {accepted,6} ms  +save {total - accepted,6} ms  total {total,6} ms");
        }
        await Time("edit title", () => session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(anchor.Identity, PlanField.Title, "x")]), day));
        var na = PlanRow.New("A") with { Parent = parent, Estimate = 8 }; var nb = PlanRow.New("B") with { Parent = parent, Estimate = 8 };
        await Time("insert row", () => session.Execute(new InsertPlanRows([na, nb], anchor.Identity), day));
        await Time("indent", () => session.Execute(new IndentPlanRows([nb.Identity]), day));
        await Time("outdent", () => session.Execute(new IndentPlanRows([nb.Identity], true), day));
        await Time("move to top of siblings", () => session.Execute(new MovePlanSubtrees([nb.Identity], sibling[0]), day));
        await Time("undo", () => session.Undo(day));
        await Time("publish-record save", () => session.SaveSync(session.Document.Sync));
        // An unchanged read: the history is rebased onto the same baseline.
        var remote = new PlanRemoteSnapshot(session.Document.Baseline, session.Document.Baseline.Rows.ToImmutableDictionary(r => r.Identity, r => "T-" + r.Identity), [], 0, 0);
        using (var trace = new PerformanceTrace())
        {
            await Time("unchanged refresh (local)", () => session.AcceptRefresh(remote, day));
            TestContext.Out.WriteLine(string.Join(" | ", trace.Samples.GroupBy(x => x.Kind).Select(g =>
                $"{g.Key} {g.Count()}x {g.Sum(x => Stopwatch.GetElapsedTime(x.Start, x.End).TotalMilliseconds):0} ms")));
        }
        await Time("publish-record save 2", () => session.SaveSync(session.Document.Sync));
        // The parts one save and one command are made of.
        var doc = session.Document;
        var sw2 = Stopwatch.StartNew(); PlanOperations.ValidateDocument(doc, day); var v = sw2.ElapsedMilliseconds;
        sw2.Restart(); PlanOperations.Schedule(doc, day); var s = sw2.ElapsedMilliseconds;
        sw2.Restart(); PlanSheetColumns.ValidateOwnership(doc); TestContext.Out.WriteLine($"ValidateOwnership {sw2.ElapsedMilliseconds} ms");
        sw2.Restart(); PlanOperations.Apply(doc, new IndentPlanRows([nb.Identity]), day); var a = sw2.ElapsedMilliseconds;
        var mergedDoc = PlanMerge.Merge(doc, doc.Baseline); var merge = long.MaxValue;
        for (var k = 0; k < 5; k++) { sw2.Restart(); mergedDoc = PlanMerge.Merge(doc, doc.Baseline); merge = Math.Min(merge, sw2.ElapsedMilliseconds); }
        sw2.Restart(); PlanMerge.NativeOrder(doc, mergedDoc, doc.Sync.NativeOrders); var native = sw2.ElapsedMilliseconds;
        TestContext.Out.WriteLine($"Merge {merge} ms | NativeOrder {native} ms");
        var loaded = await store.LoadAsync(doc.Project);
        sw2.Restart(); PlanJson.Validate(loaded.Checkpoint!, doc.Project); var full = sw2.ElapsedMilliseconds;
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(loaded.Checkpoint!, PlanJson.Options);
        sw2.Restart(); for (var i = 0; i < 3; i++) System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(loaded.Checkpoint!, PlanJson.Options); var ser = sw2.ElapsedMilliseconds / 3;
        TestContext.Out.WriteLine($"ValidateDocument {v} ms | Schedule {s} ms | Apply(indent) {a} ms | Validate checkpoint incl. {loaded.Checkpoint!.Undo.Length} history {full} ms | serialize {ser} ms ({bytes.Length / 1024} KB)");
        Directory.Delete(root, true);
    }
}
