using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;
// #332: a save must not replay the whole Undo history. At 1,040 rows every replayed state costs one full
// document validation (about 10 ms), so 200 retained steps made every row operation and every publish
// record save take 2-4 s. Counted, not timed, so the check does not depend on the machine.
[TestFixture]
internal sealed class PlanSaveCostTests
{
    private const int History = 30;
    private string root = null!;
    [SetUp] public void SetUp() => root = Path.Combine(Path.GetTempPath(), "ghpb-save-cost-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private static long Counted(PerformanceTrace trace, string kind) => trace.Samples.Where(s => s.Kind == kind).Sum(s => s.Count);

    private async Task<(PlanSession Session, DateOnly Day, PlanRow Anchor)> Version()
    {
        var plan = EvaluationFixture.ReadPlan();
        var snapshot = EvaluationFixture.Simulate(plan, [plan.StatusDate]).Single();
        var session = await PlanSession.CreateAsync(new(root), snapshot.Document, plan.StatusDate);
        var rows = session.Document.State.Rows;
        var leaves = rows.Where(r => r.Parent is not null && !r.Closed && !rows.Any(c => c.Parent == r.Identity)).ToList();
        for (var i = 0; i < History; i++)
            Assert.That((await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(leaves[i].Identity, PlanField.Title, "t" + i)]), plan.StatusDate)).Succeeded, Is.True);
        return (session, plan.StatusDate, leaves[^1]);
    }

    [Test]
    public async Task RowOperationsSaveWithoutReplayingTheHistory()
    {
        var (session, day, anchor) = await Version();
        var added = PlanRow.New("A") with { Parent = anchor.Parent, Estimate = 8 };
        var child = PlanRow.New("B") with { Parent = anchor.Parent, Estimate = 8 };
        var first = session.Document.State.Rows.First(r => r.Parent == anchor.Parent).Identity;
        using var trace = new PerformanceTrace();
        foreach (var command in new PlanCommand[] { new InsertPlanRows([added, child], anchor.Identity), new IndentPlanRows([child.Identity]),
            new IndentPlanRows([child.Identity], true), new MovePlanSubtrees([child.Identity], first) })
            Assert.That((await session.Execute(command, day)).Succeeded, Is.True);
        Assert.That((await session.Undo(day)).Succeeded, Is.True);
        Assert.That((await session.Redo(day)).Succeeded, Is.True);
        Assert.That(Counted(trace, "plan-history-state-validated"), Is.Zero, "Each command proves its own step; the save checks the current plan only.");
        Assert.That(session.UndoCount, Is.EqualTo(History + 4));
    }

    [Test]
    public async Task PublishRecordSavesCheckOnlyTheRecordsInEachHistoryState()
    {
        var (session, _, _) = await Version();
        using var trace = new PerformanceTrace();
        Assert.That((await session.SaveSync(session.Document.Sync)).Succeeded, Is.True);
        Assert.That(Counted(trace, "plan-history-state-validated"), Is.Zero);
        Assert.That(Counted(trace, "plan-history-records-checked"), Is.EqualTo(History));
        Assert.That(session.UndoCount, Is.EqualTo(History));
    }

    [Test]
    public async Task ARefreshRebasesEachHistoryStateOnceAndKeepsTheHistory()
    {
        var (session, day, _) = await Version();
        var before = session.Document;
        var remote = new PlanRemoteSnapshot(before.Baseline, before.Baseline.Rows.ToImmutableDictionary(r => r.Identity, r => "T-" + r.Identity), [], 0, 0);
        using var trace = new PerformanceTrace();
        Assert.That((await session.AcceptRefresh(remote, day)).Succeeded, Is.True);
        // One merge per reachable state: the current plan and the state before each of the 30 steps.
        Assert.That(Counted(trace, "plan-history-rebase-merge"), Is.EqualTo(History + 1));
        Assert.That(session.UndoCount, Is.EqualTo(History));
        Assert.That(session.Document.State.Rows.Select(r => r.Title), Is.EqualTo(before.State.Rows.Select(r => r.Title)));
        for (var i = 0; i < History; i++) Assert.That((await session.Undo(day)).Succeeded, Is.True);
        Assert.That(session.Document.State.Rows.Select(r => r.Title), Is.EqualTo(before.Baseline.Rows.Select(r => r.Title)),
            "Undo through the rebased history returns every title to the read value.");
    }

    [Test]
    public async Task ReopeningStillValidatesEveryHistoryState()
    {
        var (session, day, _) = await Version();
        var project = session.Document.Project;
        using var trace = new PerformanceTrace();
        var reopened = await PlanSession.OpenAsync(new(root), project, day);
        Assert.That(reopened.Status, Is.EqualTo(PlanLoadStatus.Loaded), reopened.Error);
        Assert.That(Counted(trace, "plan-history-state-validated"), Is.EqualTo(History));
        Assert.That(reopened.Session!.UndoCount, Is.EqualTo(History));
    }
}
