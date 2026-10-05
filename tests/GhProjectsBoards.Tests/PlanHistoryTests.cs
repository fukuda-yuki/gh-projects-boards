using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class PlanHistoryTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");
    private string root = null!;
    [SetUp] public void Setup() { root = Path.Combine(Path.GetTempPath(), "ghpb-history-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TearDown] public void Cleanup() => Directory.Delete(root, true);
    private static PlanDocument Initial()
    {
        ImmutableArray<PlanRow> rows = [new("A", "A", "acme/repo"), new("B", "B", "acme/repo"), new("C", "C", "acme/repo")];
        return new(Project, new(rows, []), new(rows, new() { DefaultRepository = "acme/repo", StatusDate = Today }));
    }
    private static PlanRemoteSnapshot Remote(PlanDocument document) => new(document.Baseline,
        document.Baseline.Rows.ToImmutableDictionary(r => r.Identity, r => "T-" + r.Identity), [], 0, 0);
    private static EditPlanCells Edit(string id, PlanField field, object? value) => new(PlanOperationKind.Cell, [new(id, field, value)]);
    [TestCase(PlanField.Parent, false)]
    [TestCase(PlanField.Parent, true)]
    [TestCase(PlanField.Predecessors, false)]
    [TestCase(PlanField.Predecessors, true)]
    public async Task RemoteRebaseRetainsOnlyReachableHistoryInEachDirection(PlanField relation, bool redo)
    {
        var initial = Initial(); var session = await PlanSession.CreateAsync(new(root), initial, Today);
        await session.Execute(Edit("C", PlanField.Title, "Earlier"), Today);
        await session.Execute(Edit("A", relation, relation == PlanField.Parent ? "B" : ImmutableArray.Create("B")), Today);
        await session.Execute(Edit("A", relation, relation == PlanField.Parent ? null : ImmutableArray<string>.Empty), Today);
        await session.Execute(Edit("C", PlanField.Title, "Latest"), Today);
        if (redo) { await session.Undo(Today); await session.Undo(Today); await session.Undo(Today); await session.Undo(Today); }
        var remoteRows = initial.Baseline.Rows.SetItem(1, relation == PlanField.Parent ? initial.Baseline.Rows[1] with { Parent = "A" } : initial.Baseline.Rows[1] with { Predecessors = ["A"] });
        var save = await session.AcceptRefresh(Remote(initial with { Baseline = initial.Baseline with { Rows = remoteRows } }), Today);
        Assert.That(save.Succeeded, Is.True, save.Error);
        var loaded = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(loaded.Status, Is.EqualTo(PlanLoadStatus.Loaded), loaded.Error);
        session = loaded.Session!;
        Assert.That(redo ? session.RedoCount : session.UndoCount, Is.EqualTo(1), "Keep the valid nearest title step, prune the cycle and all steps beyond it.");
        Assert.That((await (redo ? session.Redo(Today) : session.Undo(Today))).Succeeded, Is.True);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "C").Title, Is.EqualTo("Earlier"));
        Assert.That((await (redo ? session.Undo(Today) : session.Redo(Today))).Succeeded, Is.True);
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task RemoteAdoptionPrunesInvalidUndoButKeepsIndependentRedo(bool published)
    {
        var initial = Initial(); var session = await PlanSession.CreateAsync(new(root), initial, Today);
        await session.Execute(Edit("C", PlanField.Title, "Earlier"), Today);
        await session.Execute(Edit("A", PlanField.Parent, "B"), Today);
        await session.Execute(Edit("A", PlanField.Parent, null), Today);
        await session.Execute(Edit("C", PlanField.Title, "Middle"), Today);
        await session.Execute(Edit("C", PlanField.Title, "Future"), Today);
        await session.Undo(Today);
        var rows = initial.Baseline.Rows.SetItem(1, initial.Baseline.Rows[1] with { Parent = "A" });
        if (published)
        {
            var write = new PlanWrite("0", "C", PlanPublishStage.Fields, "updateIssue", "UpdateIssueInput",
                "{\"id\":\"C\",\"title\":\"Middle\"}", "issue { id }") { State = PlanWriteState.Succeeded };
            Assert.That((await session.SaveSync(session.Document.Sync with { Publish = new(Guid.NewGuid().ToString("N"), [write]) })).Succeeded, Is.True);
            rows = rows.SetItem(2, rows[2] with { Title = "Middle" });
        }
        var remote = Remote(initial with { Baseline = initial.Baseline with { Rows = rows } });
        var save = await (published ? session.AcceptPublished(remote, Today) : session.AcceptRefresh(remote, Today));
        Assert.That(save.Succeeded, Is.True, save.Error);
        Assert.That(session.UndoCount, Is.EqualTo(1)); Assert.That(session.RedoCount, Is.EqualTo(1));
        Assert.That((await session.Undo(Today)).Succeeded, Is.True);
        Assert.That(session.Document.State.Rows[2].Title, Is.EqualTo("Earlier"));
        Assert.That((await session.Redo(Today)).Succeeded, Is.True);
        Assert.That((await session.Redo(Today)).Succeeded, Is.True);
        Assert.That(session.Document.State.Rows[2].Title, Is.EqualTo("Future"));
        Assert.That((await PlanSession.OpenAsync(new(root), Project, Today)).Status, Is.EqualTo(PlanLoadStatus.Loaded));
    }
    [Test]
    public async Task CreationReservationsSurviveUndoRedoAndReopen()
    {
        var session = await PlanSession.CreateAsync(new(root), Initial(), Today);
        await session.Execute(Edit("A", PlanField.Title, "Changed"), Today);
        var start = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
        var reservations = Enumerable.Range(0, 6).Select(i => new PlanCreationStart(start.AddSeconds(i * 10), 10)).ToImmutableArray();
        Assert.That((await session.SaveSync(session.Document.Sync with { CreationStarts = reservations })).Succeeded, Is.True);
        await session.Undo(Today); await session.Redo(Today);
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error);
        Assert.That(opened.Session!.Document.Sync.CreationStarts, Is.EqualTo(reservations));
        Assert.That(PlanCreationPacing.NextStart(opened.Session.Document.Sync.CreationStarts, 10, start.AddSeconds(53)), Is.EqualTo(start.AddSeconds(60)));
    }
    [Test]
    public async Task SaveRejectsUnreplayableCheckpointWithoutReplacingLastGoodFile()
    {
        var store = new PlanStore(root); var initial = Initial();
        await PlanSession.CreateAsync(store, initial, Today);
        var loaded = await store.LoadAsync(Project);
        var invalid = initial.State with { Rows = initial.State.Rows.SetItem(0, initial.State.Rows[0] with { Parent = "B" }).SetItem(1, initial.State.Rows[1] with { Parent = "A" }) };
        var patch = PlanOperations.Difference(invalid, initial.State, PlanOperationKind.Cell);
        var before = await File.ReadAllBytesAsync(store.FileFor(Project));
        var saved = await store.SaveAsync(loaded.Checkpoint! with { Revision = 1, Undo = [patch] }, loaded.Fingerprint);
        Assert.That(saved.Succeeded, Is.False);
        Assert.That(saved.Failure, Is.EqualTo(PlanSaveFailure.InvalidFile));
        Assert.That(await File.ReadAllBytesAsync(store.FileFor(Project)), Is.EqualTo(before));
        Assert.That((await store.LoadAsync(Project)).Status, Is.EqualTo(PlanLoadStatus.Loaded));
    }
}
