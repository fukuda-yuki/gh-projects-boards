using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class EditingTests
{
    internal static ProjectRegistration Registration(string projectId = "P1", long viewer = 42, string titlePrefix = "Issue ")
    {
        var scope = new ConnectionScope("github.com", viewer);
        ScopedId Id(string value) => new(scope, value);
        var capability = new CapabilityObservation(true, DateTimeOffset.UtcNow);
        var issues = Enumerable.Range(1, 101).Select(n => new IssueReadModel(Id("I" + n), new(Id("R1"), Id("O1"), "owner/repo"), n,
            $"https://github.com/owner/repo/issues/{n}", new(ValueAvailability.Present, titlePrefix + n), new(ValueAvailability.Present, IssueState.Open), capability)).ToDictionary(i => i.Id);
        var field = new ProjectFieldDefinition(Id(projectId + "-status"), Id(projectId), "Renamed workflow", "ProjectV2SingleSelectField", "SINGLE_SELECT", FieldOwner.ProjectItem,
            [new("todo", "Todo"), new("done", "Done"), new("dup1", "Duplicate"), new("dup2", "Duplicate")], ValueAvailability.Present);
        var items = Enumerable.Range(1, 101).Select(n => new ProjectItemReadModel(Id(projectId + "T" + n), ProjectItemKind.Issue, "ISSUE", Id("I" + n), false,
            [new(field.Id, "V" + n, "ProjectV2ItemFieldSingleSelectValue", ValueAvailability.Present, "todo")], true)).ToArray();
        return new("viewer", "owner", [], null, DateTimeOffset.UtcNow, new(Id(projectId), Id("O1"), "User", projectId == "P1" ? 1 : 2,
            "https://github.com/users/owner/projects/1", projectId, [field], issues, items, true, true, capability));
    }
    [Test]
    public void TenTitlesAndReturnToBaselineProduceExactDifferences()
    {
        var w = new EditingWorkspace(new("github.com", 42)); var rows = w.Open(Registration());
        for (var i = 0; i < 10; i++) w.Commit("P1", rows[i * 10].Cells[0], "Changed " + i);
        Assert.That(w.DifferenceCount, Is.EqualTo(10));
        Assert.That(w.Fields.Where(f => f.Change != null).All(f => f.Key.Kind == "Title"), Is.True);
        for (var i = 0; i < 10; i++) w.Commit("P1", rows[i * 10].Cells[0], "Issue " + (i * 10 + 1));
        Assert.That(w.DifferenceCount, Is.Zero);
    }
    [Test]
    public void SharedTitlePinsFirstObservationButSelectsRemainIndependent()
    {
        var w = new EditingWorkspace(new("github.com", 42)); var a = w.Open(Registration());
        w.Commit("P1", a[0].Cells[0], "Shared"); w.Commit("P1", a[0].Cells[1], "Done");
        var b = w.Open(Registration("P2", titlePrefix: "Different "));
        Assert.That(w.Value(b[0].Cells[0]), Is.EqualTo("Shared"));
        Assert.That(w.Value(b[0].Cells[1]), Is.EqualTo("todo"));
        w.Commit("P2", b[0].Cells[0], "Issue 1");
        Assert.That(w.Changed(a[0].Cells[0]), Is.False);
        w.Undo("P1"); // An independent select operation remains safe to undo.
        Assert.Throws<InvalidOperationException>(() => w.Undo("P1"));
        Assert.That(w.Value(a[0].Cells[1]), Is.EqualTo("todo"));
    }
    [Test]
    public void RectangularPasteBlankClearAndUndoRestoreEarlierDraft()
    {
        var w = new EditingWorkspace(new("github.com", 42)); var r = w.Open(Registration());
        w.Commit("P1", r[0].Cells[0], "Earlier"); w.Clear("P1", [r[0].Cells[1]]);
        w.Paste("P1", r, 0, 0, "New\tDone\r\nSecond\t\r\n");
        Assert.That(w.DifferenceCount, Is.EqualTo(3));
        Assert.That(w.Value(r[1].Cells[1]), Is.EqualTo("todo"));
        w.Undo("P1");
        Assert.That(w.Value(r[0].Cells[0]), Is.EqualTo("Earlier"));
        Assert.That(w.Fields.Single(f => f.Key == r[0].Cells[1].Key).Change, Is.EqualTo(new LocalValue(null, true)));
        Assert.That(w.Value(r[1].Cells[0]), Is.EqualTo("Issue 2"));
    }
    [TestCase("New\tDuplicate")]
    [TestCase("New\tMissing")]
    [TestCase("New\tDone\treadonly")]
    [TestCase("New\tDone\nSecond")]
    public void InvalidBatchChangesNothing(string tsv)
    {
        var w = new EditingWorkspace(new("github.com", 42)); var r = w.Open(Registration()); var before = w.Snapshot();
        Assert.Throws<InvalidOperationException>(() => w.Paste("P1", r, 0, 0, tsv));
        Assert.That(w.Snapshot().Fields, Is.EqualTo(before.Fields)); Assert.That(w.Revision, Is.EqualTo(before.Revision));
    }
    [Test]
    public void RequiredTitleClearAndOutOfBoundsAreAtomic()
    {
        var w = new EditingWorkspace(new("github.com", 42)); var r = w.Open(Registration());
        Assert.Throws<InvalidOperationException>(() => w.Clear("P1", [r[0].Cells[1], r[0].Cells[0]]));
        Assert.Throws<InvalidOperationException>(() => w.Paste("P1", r, 100, 0, "First\nSecond"));
        Assert.That(w.DifferenceCount, Is.Zero);
    }
    [Test]
    public async Task PendingInvalidBufferAndAtomicUndoSurviveNewSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-draft-" + Guid.NewGuid());
        var store = new DraftStore(root); var w = new EditingWorkspace(new("github.com", 42)); var r = w.Open(Registration());
        w.Paste("P1", r, 0, 0, "A\tDone\nB\tDone"); w.SetBuffer(r[100].Cells[0], "");
        Assert.That(await new DraftSession(store, w, 0).FlushAsync(), Is.True);
        var restored = EditingWorkspace.Restore((await new DraftStore(root).LoadAsync(w.Scope))!); var rr = restored.Open(Registration());
        Assert.That(restored.Buffer(rr[100].Cells[0]), Is.EqualTo("")); Assert.That(restored.DifferenceCount, Is.EqualTo(4));
        restored.Undo("P1"); Assert.That(restored.DifferenceCount, Is.Zero);
        Assert.That(await store.LoadAsync(new("github.com", 43)), Is.Null);
    }
    [Test]
    public async Task FailedAndStaleSavesPreserveAcknowledgedRecord()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-draft-" + Guid.NewGuid()); var store = new DraftStore(root);
        var w = new EditingWorkspace(new("github.com", 42)); var r = w.Open(Registration());
        var session = new DraftSession(store, w, 0); Assert.That(await session.FlushAsync(), Is.True);
        var bytes = await File.ReadAllBytesAsync(store.FileFor(w.Scope));
        w.Commit("P1", r[0].Cells[0], "New");
        using (var gate = new FileStream(Path.Combine(root, "Drafts", ".writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.That(await session.FlushAsync(), Is.False);
        Assert.That(await File.ReadAllBytesAsync(store.FileFor(w.Scope)), Is.EqualTo(bytes));
        Assert.That(w.Value(r[0].Cells[0]), Is.EqualTo("New"));
        Assert.That(await session.FlushAsync(), Is.True);
        Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(w.Snapshot(), 0));
    }
    [Test]
    public void MissingCapabilityNeverGrantsEditing()
    {
        var reg = Registration(); var p = reg.Snapshot;
        p = p with { Capability = null, Issues = p.Issues.ToDictionary(x => x.Key, x => x.Value with { Capability = null }) };
        var w = new EditingWorkspace(p.Id.Scope); var rows = w.Open(reg with { Snapshot = p });
        Assert.That(rows.SelectMany(r => r.Cells).All(c => !c.Editable), Is.True);
        Assert.That(w.Fields, Is.Empty);
    }
    [Test]
    public async Task InterruptedReplacementRetainsAcknowledgedWholeTransaction()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-draft-" + Guid.NewGuid()); var store = new DraftStore(root);
        var w = new EditingWorkspace(new("github.com", 42)); var r = w.Open(Registration());
        w.Paste("P1", r, 0, 0, "A\tDone\nB\tDone"); await store.SaveAsync(w.Snapshot(), 0);
        var file = store.FileFor(w.Scope); var before = await File.ReadAllBytesAsync(file);
        await File.WriteAllTextAsync(file + ".interrupted.tmp", "{ incomplete");
        Assert.That(store.HasInterruptedSave(w.Scope), Is.True);
        var restored = EditingWorkspace.Restore((await new DraftStore(root).LoadAsync(w.Scope))!);
        Assert.That(restored.DifferenceCount, Is.EqualTo(4));
        restored.Undo("P1"); Assert.That(restored.DifferenceCount, Is.Zero);
        Assert.That(await File.ReadAllBytesAsync(file), Is.EqualTo(before));
        Assert.That(File.Exists(file + ".interrupted.tmp"), Is.True);
    }
    [TestCase("schema"), TestCase("scope"), TestCase("select-owner"), TestCase("history-owner"), TestCase("json")]
    public async Task CorruptionBlocksLoadAndOverwriteWithoutReset(string corruption)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-draft-" + Guid.NewGuid()); var store = new DraftStore(root);
        var w = new EditingWorkspace(new("github.com", 42)); var r = w.Open(Registration()); w.Commit("P1", r[0].Cells[1], "Done");
        await store.SaveAsync(w.Snapshot(), 0); var file = store.FileFor(w.Scope);
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        if (corruption == "schema") node["Version"] = 99;
        if (corruption == "scope") node["Scope"]!["ViewerId"] = 43;
        if (corruption == "select-owner") node["Fields"]![1]!["SourceProject"]!["NodeId"] = "P2";
        if (corruption == "history-owner") node["History"]![0]!["ProjectId"] = "P2";
        await File.WriteAllTextAsync(file, corruption == "json" ? "{" : node.ToJsonString()); var bytes = await File.ReadAllBytesAsync(file);
        Assert.That(async () => await store.LoadAsync(w.Scope), Throws.Exception);
        Assert.That(await new DraftSession(store, w, 0).FlushAsync(), Is.False);
        Assert.That(await File.ReadAllBytesAsync(file), Is.EqualTo(bytes));
    }
    [Test]
    public async Task ConcurrentFlushesPersistLatestRevisionAndBuffers()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-draft-" + Guid.NewGuid()); var store = new DraftStore(root);
        var w = new EditingWorkspace(new("github.com", 42)); var r = w.Open(Registration()); var session = new DraftSession(store, w, 0);
        var first = session.FlushAsync(); w.Commit("P1", r[0].Cells[0], "Later"); w.SetBuffer(r[1].Cells[0], "Pending");
        Assert.That(await Task.WhenAll(first, session.FlushAsync()), Is.All.True);
        var restored = EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!); var rows = restored.Open(Registration());
        Assert.That(restored.Value(rows[0].Cells[0]), Is.EqualTo("Later")); Assert.That(restored.Buffer(rows[1].Cells[0]), Is.EqualTo("Pending"));
        Assert.That(session.DurableRevision, Is.EqualTo(w.Revision));
    }
    [Test]
    public void UnregistrationDiscardPreservesSharedTitlesAndOtherProjectValues()
    {
        var w = new EditingWorkspace(new("github.com", 42)); var a = Registration(); var b = Registration("P2");
        var ar = w.Open(a); var br = w.Open(b);
        w.Commit("P1", ar[0].Cells[0], "Shared"); w.Commit("P1", ar[0].Cells[1], "Done"); w.Commit("P2", br[0].Cells[1], "Done");
        w.Discard(a.Snapshot, [b.Snapshot]);
        Assert.That(w.Value(br[0].Cells[0]), Is.EqualTo("Shared")); Assert.That(w.Value(br[0].Cells[1]), Is.EqualTo("done"));
        Assert.That(w.Fields.All(f => f.Key.ProjectId != "P1"), Is.True);
        Assert.That(w.Fields.Single(f => f.Key == br[0].Cells[0].Key).SourceProject, Is.EqualTo(a.Snapshot.Id));
    }
    [Test]
    public void ForeignCellCannotEditMatchingIdsInAnotherAccount()
    {
        var a = new EditingWorkspace(new("github.com", 42)); a.Open(Registration());
        var b = new EditingWorkspace(new("github.com", 43)); var br = b.Open(Registration(viewer: 43));
        Assert.Throws<InvalidOperationException>(() => a.Commit("P1", br[0].Cells[0], "Wrong account"));
        Assert.Throws<InvalidOperationException>(() => a.SetBuffer(br[0].Cells[0], "Wrong account"));
        Assert.That(a.DifferenceCount, Is.Zero);
    }
    [Test]
    public async Task CacheReplacementChecksDraftsAtAtomicCommitBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-draft-" + Guid.NewGuid()); var store = new RegistrationStore(root);
        var initial = Registration(); await store.SaveAsync(initial); var before = await File.ReadAllBytesAsync(store.FileFor(initial.Snapshot.Id));
        var w = new EditingWorkspace(initial.Snapshot.Id.Scope); var rows = w.Open(initial);
        Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(initial with { RetrievedAt = DateTimeOffset.UtcNow.AddMinutes(1) }, canCommit: () =>
        {
            w.SetBuffer(rows[0].Cells[0], "Typed during save");
            return !w.HasWork(initial.Snapshot);
        }));
        Assert.That(await File.ReadAllBytesAsync(store.FileFor(initial.Snapshot.Id)), Is.EqualTo(before));
        Assert.That(w.Buffer(rows[0].Cells[0]), Is.EqualTo("Typed during save"));
    }
    [Test]
    public async Task LegacyRegistrationWithoutCapabilityRemainsReadableAndUnknown()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-draft-" + Guid.NewGuid()); var store = new RegistrationStore(root);
        var initial = Registration(); await store.SaveAsync(initial); var file = store.FileFor(initial.Snapshot.Id);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        json["Snapshot"]!.AsObject().Remove("Capability");
        foreach (var issue in json["Snapshot"]!["Issues"]!.AsArray()) issue!.AsObject().Remove("Capability");
        await File.WriteAllTextAsync(file, json.ToJsonString());
        var loaded = await store.LoadAsync(); Assert.That(loaded.Problems, Is.Empty);
        var restored = loaded.Registrations.Single(); var w = new EditingWorkspace(restored.Snapshot.Id.Scope);
        Assert.That(w.Open(restored).SelectMany(r => r.Cells).All(c => !c.Editable), Is.True);
    }
}
