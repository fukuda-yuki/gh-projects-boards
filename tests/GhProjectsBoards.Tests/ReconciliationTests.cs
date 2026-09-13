using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ReconciliationTests
{
    internal static ProjectRegistration Remote(ProjectRegistration original, string title = "Remote", string? option = "todo")
        => original with { RetrievedAt = original.RetrievedAt.AddMinutes(1), Snapshot = original.Snapshot with {
            Issues = original.Snapshot.Issues.ToDictionary(x => x.Key, x => x.Value with { Title = new(ValueAvailability.Present, title) }),
            Items = original.Snapshot.Items.Select(i => i with { Values = i.Values.Select(v => v with { OptionId = option,
                Availability = option is null ? ValueAvailability.Empty : ValueAvailability.Present }).ToArray() }).ToArray() } };

    [TestCase("Issue 1", "Remote", "Remote", 0)]
    [TestCase("Local", "Issue 1", "Local", 1)]
    [TestCase("Local", "Local", "Local", 0)]
    public void KnownThreeWayBranches(string local, string remote, string expected, int differences)
    {
        var a = EditingTests.Registration(count: 1); var w = new EditingWorkspace(a.Snapshot.Id.Scope); var rows = w.Open(a);
        if (local != "Issue 1") w.Commit("P1", rows[0].Cells[0], local);
        w.Reconcile(a, Remote(a, remote));
        Assert.That(w.Value(rows[0].Cells[0]), Is.EqualTo(expected));
        Assert.That(w.DifferenceCount, Is.EqualTo(differences));
    }

    [TestCase("remote"), TestCase("local"), TestCase("other")]
    public async Task RepeatedConflictResolutionAndGuardedUndoSurviveRestart(string choice)
    {
        var a = EditingTests.Registration(count: 1); var w = new EditingWorkspace(a.Snapshot.Id.Scope); var rows = w.Open(a);
        var cell = rows[0].Cells[0]; w.Commit("P1", cell, "Local");
        var remote = Remote(a); w.Reconcile(a, remote); w.Reconcile(remote, remote with { RetrievedAt = remote.RetrievedAt.AddMinutes(1) });
        var f = w.Field(cell)!;
        Assert.That(f.Conflict, Is.True); Assert.That(f.Baseline, Is.EqualTo("Issue 1")); Assert.That(f.Change!.Value, Is.EqualTo("Local"));
        var chosen = choice == "remote" ? "Remote" : choice == "local" ? "Local" : "Other";
        w.Resolve("P1", w.Decision(cell.Key!), new(chosen));
        Assert.That(w.Field(cell)!.Baseline, Is.EqualTo("Remote")); Assert.That(w.DifferenceCount, Is.EqualTo(choice == "remote" ? 0 : 1));
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-reconcile-" + Guid.NewGuid()));
        w.SetRegistrations([remote]); await store.SaveAsync(w.Snapshot(), 0);
        var restored = EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!);
        restored.Undo("P1"); Assert.That(restored.Field(cell)!.Conflict, Is.True);
        Assert.That(restored.Field(cell)!.Observation!.Value, Is.EqualTo("Remote"));
        restored.Resolve("P1", restored.Decision(cell.Key!), new(chosen)); restored.Reconcile(remote, Remote(remote));
        Assert.That(restored.Field(cell)!.Conflict, Is.False);
    }

    [Test]
    public void IndependentRemoteFieldKeepsTitleUndoAndClearConverges()
    {
        var a = EditingTests.Registration(count: 1); var w = new EditingWorkspace(a.Snapshot.Id.Scope); var rows = w.Open(a);
        w.Commit("P1", rows[0].Cells[0], "Local"); var r = Remote(a, "Issue 1", "done"); w.Reconcile(a, r);
        Assert.That(w.Value(rows[0].Cells[1]), Is.EqualTo("done")); Assert.That(w.DifferenceCount, Is.EqualTo(1));
        w.Undo("P1"); Assert.That(w.DifferenceCount, Is.Zero); Assert.That(w.Value(rows[0].Cells[1]), Is.EqualTo("done"));
        w.Clear("P1", [w.Open(r)[0].Cells[1]]); w.Reconcile(r, Remote(r, "Issue 1", null));
        Assert.That(w.Value(rows[0].Cells[1]), Is.Null); Assert.That(w.DifferenceCount, Is.Zero);
    }

    [TestCase("edit"), TestCase("refresh")]
    public void StaleResolutionIsRejectedWithoutChangingWork(string cause)
    {
        var a = EditingTests.Registration(count: 1); var w = new EditingWorkspace(a.Snapshot.Id.Scope); var rows = w.Open(a);
        w.Commit("P1", rows[0].Cells[0], "Local"); var r = Remote(a); w.Reconcile(a, r); var decision = w.Decision(rows[0].Cells[0].Key!);
        if (cause == "edit") w.Commit("P1", rows[0].Cells[1], "Done"); else w.Reconcile(r, Remote(r, "New remote"));
        var before = w.Snapshot(); Assert.Throws<InvalidOperationException>(() => w.Resolve("P1", decision, new("Local")));
        Assert.That(w.Snapshot().Fields, Is.EqualTo(before.Fields)); Assert.That(w.Revision, Is.EqualTo(before.Revision));
    }

    [TestCase("unknown"), TestCase("permission"), TestCase("removed"), TestCase("option"), TestCase("field"), TestCase("type"), TestCase("archived")]
    public void StructuralOrCapabilityLossRetainsIdentifiersDraftAndBuffer(string mode)
    {
        var a = EditingTests.Registration(count: 1); var w = new EditingWorkspace(a.Snapshot.Id.Scope); var rows = w.Open(a);
        w.Commit("P1", rows[0].Cells[0], "Local"); w.Commit("P1", rows[0].Cells[1], "Done"); w.SetBuffer(rows[0].Cells[0], "unfinished");
        var r = Remote(a); var p = r.Snapshot;
        p = mode switch {
            "unknown" => p with { Items = p.Items.Select(i => i with { Values = i.Values.Select(v => v with { Availability = ValueAvailability.Unavailable, OptionId = null }).ToArray() }).ToArray() },
            "permission" => p with { Capability = new(false, r.RetrievedAt) },
            "removed" => p with { Items = [], Issues = new Dictionary<ScopedId, IssueReadModel>() },
            "option" => p with { Fields = p.Fields.Select(f => f with { Options = f.Options.Where(o => o.Id != "done").ToArray() }).ToArray() },
            "field" => p with { Fields = [] },
            "type" => p with { Fields = p.Fields.Select(f => f with { DataType = "TEXT", Availability = ValueAvailability.Unsupported }).ToArray() },
            _ => p with { Items = p.Items.Select(i => i with { IsArchived = true }).ToArray() }
        };
        w.Reconcile(a, r with { Snapshot = p });
        Assert.That(w.Value(rows[0].Cells[0]), Is.EqualTo("Local")); Assert.That(w.Buffer(rows[0].Cells[0]), Is.EqualTo("unfinished"));
        Assert.That(w.Value(rows[0].Cells[1]), Is.EqualTo("done")); Assert.That(w.Field(rows[0].Cells[1])!.Observation!.Reason, Is.Not.Null);
        Assert.That(w.HasWork(p), Is.True); Assert.Throws<InvalidOperationException>(() => w.Commit("P1", rows[0].Cells[1], "Todo"));
    }

    [Test]
    public void PendingBufferIsNeverCommittedAndCanBeCommittedAfterRefresh()
    {
        var a = EditingTests.Registration(count: 1); var w = new EditingWorkspace(a.Snapshot.Id.Scope); var rows = w.Open(a);
        w.SetBuffer(rows[0].Cells[0], "unfinished"); var r = Remote(a); w.Reconcile(a, r);
        Assert.That(w.Value(rows[0].Cells[0]), Is.EqualTo("Issue 1")); Assert.That(w.Buffer(rows[0].Cells[0]), Is.EqualTo("unfinished"));
        w.Commit("P1", w.Open(r)[0].Cells[0], "Finished"); w.Reconcile(r, Remote(r));
        Assert.That(w.Field(rows[0].Cells[0])!.Conflict, Is.True);
    }

    [Test]
    public void SharedTitleAcceptedFromNewerProjectCannotBeReplacedByOldCaches()
    {
        var a = EditingTests.Registration(count: 1); var b = EditingTests.Registration("P2", count: 1) with { RetrievedAt = a.RetrievedAt.AddMinutes(1) };
        var w = new EditingWorkspace(a.Snapshot.Id.Scope); var ar = w.Open(a); w.Open(b);
        var r = Remote(b, "Accepted"); w.Reconcile(b, r);
        w.Open(a); w.Open(b); w.Reconcile(a, Remote(a, "Older") with { RetrievedAt = a.RetrievedAt });
        Assert.That(w.Value(ar[0].Cells[0]), Is.EqualTo("Accepted"));
        w.Commit("P1", ar[0].Cells[0], "Shared local"); w.Reconcile(r, Remote(r, "Newest"));
        Assert.That(w.Field(ar[0].Cells[0])!.Baseline, Is.EqualTo("Accepted")); Assert.That(w.Field(ar[0].Cells[0])!.Conflict, Is.True);
    }

    [Test]
    public void RenameKeepsOptionIdentityAndReportsAddedRemovedArchivedSeparately()
    {
        var a = EditingTests.Registration(count: 2); var w = new EditingWorkspace(a.Snapshot.Id.Scope); var rows = w.Open(a);
        var p = a.Snapshot; var r = Remote(a, "Issue 1") with { Snapshot = p with {
            Fields = p.Fields.Select(f => f with { Options = f.Options.Select(o => o with { Name = "Renamed " + o.Name }).ToArray() }).ToArray(),
            Items = [p.Items[0] with { IsArchived = true }, p.Items[1] with { Id = new(p.Id.Scope, "Added") }] } };
        w.Reconcile(a, r); Assert.That(w.Value(rows[1].Cells[1]), Is.EqualTo("todo"));
        Assert.That(w.StructuralChanges.Any(s => s.Contains("追加項目")), Is.True);
        Assert.That(w.StructuralChanges.Any(s => s.Contains("未観測")), Is.True);
        Assert.That(w.StructuralChanges.Any(s => s.Contains("アーカイブ")), Is.True);
        Assert.That(w.StructuralChanges.Any(s => s.Contains("選択肢名変更")), Is.True);
    }

    [TestCase("before"), TestCase("temporary"), TestCase("stale"), TestCase("writer")]
    public async Task FailedCheckpointNeverPairsNewCacheWithOldDrafts(string failure)
    {
        var a = EditingTests.Registration(count: 1); var store = new RegistrationStore(Path.Combine(Path.GetTempPath(), "ghpb-checkpoint-" + Guid.NewGuid()));
        await store.SaveAsync(a); var draftStore = new DraftStore(store.Root); var w = new EditingWorkspace(a.Snapshot.Id.Scope); var rows = w.Open(a);
        w.Commit("P1", rows[0].Cells[0], "Local"); var session = new DraftSession(draftStore, w, 0); await session.FlushAsync();
        var oldBytes = await File.ReadAllBytesAsync(draftStore.FileFor(w.Scope)); var remote = Remote(a);
        FileStream? writer = failure == "writer" ? new FileStream(Path.Combine(store.Root, ".writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        try {
            Assert.That(await session.CommitAsync(candidate => {
                if (failure == "before") throw new IOException("injected before candidate write");
                candidate.Reconcile(a, remote); candidate.SetRegistrations([remote]); return candidate;
            }, () => { if (failure == "stale") w.SetBuffer(rows[0].Cells[0], "Later input"); return failure != "temporary"; }), Is.False);
        } finally { writer?.Dispose(); }
        Assert.That(await File.ReadAllBytesAsync(draftStore.FileFor(w.Scope)), Is.EqualTo(oldBytes));
        Assert.That((await store.LoadAsync()).Registrations.Single().RetrievedAt, Is.EqualTo(a.RetrievedAt));
        Assert.That(session.Workspace.Value(rows[0].Cells[0]), Is.EqualTo("Local"));
        if (failure == "stale") Assert.That(session.Workspace.Buffer(rows[0].Cells[0]), Is.EqualTo("Later input"));
        Assert.That(await session.CommitAsync(candidate => { candidate.Reconcile(a, remote); candidate.SetRegistrations([remote]); return candidate; }, () => true), Is.True);
        var loaded = await store.LoadAsync(); Assert.That(loaded.Registrations.Single().RetrievedAt, Is.EqualTo(remote.RetrievedAt));
        var restored = EditingWorkspace.Restore((await draftStore.LoadAsync(w.Scope))!);
        Assert.That(restored.Value(rows[0].Cells[0]), Is.EqualTo("Local"));
        Assert.That(restored.Field(rows[0].Cells[0])!.Conflict || restored.Buffer(rows[0].Cells[0]) is not null, Is.True);
        Assert.That(File.Exists(store.FileFor(a.Snapshot.Id)), Is.True, "Legacy recovery file is retained.");
    }

    [TestCase("corrupt"), TestCase("missing")]
    public async Task DamagedOtherProfileCannotRegressValidCheckpointOrResurrectLegacy(string damage)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-profiles-" + Guid.NewGuid()); var store = new RegistrationStore(root); var ds = new DraftStore(root);
        foreach (var viewer in new long[] { 42, 43 })
        {
            var a = EditingTests.Registration(viewer: viewer, count: 1); await store.SaveAsync(a);
            var w = new EditingWorkspace(a.Snapshot.Id.Scope); w.Open(a); var r = Remote(a, "Accepted"); w.Reconcile(a, r); w.SetRegistrations([r]);
            await ds.SaveAsync(w.Snapshot(), 0);
        }
        var damaged = ds.FileFor(new("github.com", 43));
        if (damage == "corrupt") await File.WriteAllTextAsync(damaged, "{"); else File.Move(damaged, damaged + ".bak");
        var result = await store.LoadAsync();
        Assert.That(result.Problems, Is.Not.Empty); Assert.That(result.Registrations, Has.Count.EqualTo(1));
        Assert.That(result.Registrations.Single().Snapshot.Issues.Values.Single().Title.Value, Is.EqualTo("Accepted"));
        Assert.That(result.Registrations.Single().Snapshot.Id.Scope.ViewerId, Is.EqualTo(42));
    }

    [Test]
    public async Task LegacyWritersCannotReportSuccessAfterCheckpointMigration()
    {
        var store = new RegistrationStore(Path.Combine(Path.GetTempPath(), "ghpb-migrate-" + Guid.NewGuid()));
        var a = EditingTests.Registration(count: 1); await store.SaveAsync(a); var ds = new DraftStore(store.Root);
        var w = new EditingWorkspace(a.Snapshot.Id.Scope); w.Open(a); w.SetRegistrations([a]); await ds.SaveAsync(w.Snapshot(), 0);
        Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(a with { DefaultRepository = "owner/changed" }));
        Assert.ThrowsAsync<InvalidDataException>(() => store.RemoveAsync(a.Snapshot.Id));
        Assert.That((await store.LoadAsync()).Registrations.Single().DefaultRepository, Is.Null);
    }

    [Test]
    public void DetachedDiscardKeepsSharedValuesAndExplainsInvalidatedMixedUndo()
    {
        var a = EditingTests.Registration(count: 2); var b = EditingTests.Registration("P2", count: 1); var w = new EditingWorkspace(a.Snapshot.Id.Scope);
        var rows = w.Open(a); w.Open(b); w.Paste("P1", rows, 0, 0, "Shared\tDone\nDetached\tDone");
        var r = a with { RetrievedAt = a.RetrievedAt.AddMinutes(1), Snapshot = a.Snapshot with { Items = [], Issues = new Dictionary<ScopedId, IssueReadModel>() } };
        w.Reconcile(a, r); w.Discard(r.Snapshot, [b.Snapshot]);
        Assert.That(w.Fields.Any(f => f.Key.NodeId == "I2"), Is.False); Assert.That(w.Value(w.Open(b)[0].Cells[0]), Is.EqualTo("Shared"));
        Assert.That(w.UndoWarnings, Is.Not.Empty); DraftStore.Validate(w.Snapshot());
    }

    [Test]
    public async Task VersionOneDraftMigratesWithoutResetAndKeepsBackup()
    {
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-v1-" + Guid.NewGuid())); var a = EditingTests.Registration(count: 1);
        var w = new EditingWorkspace(a.Snapshot.Id.Scope); var rows = w.Open(a); w.Commit("P1", rows[0].Cells[0], "Original local");
        await store.SaveAsync(w.Snapshot() with { Version = 1 }, 0); var file = store.FileFor(w.Scope);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        json.AsObject().Remove("Registrations"); json.AsObject().Remove("StructuralChanges");
        foreach (var f in json["Fields"]!.AsArray()) { f!.AsObject().Remove("Observation"); f.AsObject().Remove("Conflict"); }
        await File.WriteAllTextAsync(file, json.ToJsonString()); var old = await File.ReadAllBytesAsync(file);
        var loaded = (await store.LoadAsync(w.Scope))!; var restored = EditingWorkspace.Restore(loaded); restored.SetRegistrations([a]);
        await store.SaveAsync(restored.Snapshot(), loaded.Revision);
        Assert.That(await File.ReadAllBytesAsync(file + ".bak"), Is.EqualTo(old)); Assert.That(restored.Value(rows[0].Cells[0]), Is.EqualTo("Original local"));
    }

    [TestCase("null-registration"), TestCase("unknown-conflict"), TestCase("wrong-owner")]
    public async Task MalformedCheckpointIsDiagnosedWithoutResolvingUnknownAsClear(string corruption)
    {
        var store = new RegistrationStore(Path.Combine(Path.GetTempPath(), "ghpb-malformed-" + Guid.NewGuid())); var ds = new DraftStore(store.Root);
        var a = EditingTests.Registration(count: 1); await store.SaveAsync(a); var w = new EditingWorkspace(a.Snapshot.Id.Scope); var rows = w.Open(a);
        w.Clear("P1", [rows[0].Cells[1]]); var r = Remote(a, "Issue 1", "done"); w.Reconcile(a, r); w.SetRegistrations([r]); await ds.SaveAsync(w.Snapshot(), 0);
        var file = ds.FileFor(w.Scope); var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        var observation = json["Fields"]!.AsArray().Single(f => f!["Key"]!["Kind"]!.ToString() == "Select")!["Observation"]!;
        if (corruption == "null-registration") json["Registrations"]![0] = null;
        if (corruption == "unknown-conflict") observation["Availability"] = (int)ValueAvailability.Unavailable;
        if (corruption == "wrong-owner") observation["Project"]!["NodeId"] = "P2";
        await File.WriteAllTextAsync(file, json.ToJsonString());
        var loaded = await store.LoadAsync(); Assert.That(loaded.Problems, Is.Not.Empty); Assert.That(loaded.Registrations, Is.Empty);
        Assert.ThrowsAsync<InvalidDataException>(() => ds.LoadAsync(w.Scope));
    }

    [Test]
    public void UndoCannotRestoreAnOptionDeletedWhileCurrentValueRemainsValid()
    {
        var a = EditingTests.Registration(count: 1); var w = new EditingWorkspace(a.Snapshot.Id.Scope); var rows = w.Open(a);
        w.Commit("P1", rows[0].Cells[1], "Done"); w.Commit("P1", rows[0].Cells[1], "Todo");
        var r = a with { RetrievedAt = a.RetrievedAt.AddMinutes(1), Snapshot = a.Snapshot with { Fields = a.Snapshot.Fields.Select(f => f with { Options = f.Options.Where(o => o.Id != "done").ToArray() }).ToArray() } };
        w.Reconcile(a, r); w.Undo("P1");
        Assert.That(w.Value(rows[0].Cells[1]), Is.EqualTo("todo")); Assert.That(w.UndoWarnings, Is.Not.Empty);
    }

    [Test]
    public void SharedTitleUndoRemainsUsableInSurvivingProjectAfterDiscard()
    {
        var a = EditingTests.Registration(count: 1); var b = EditingTests.Registration("P2", count: 1); var w = new EditingWorkspace(a.Snapshot.Id.Scope);
        var rows = w.Open(a); w.Open(b); w.Commit("P1", rows[0].Cells[0], "Shared"); w.Discard(a.Snapshot, [b.Snapshot]);
        w.Undo("P2"); Assert.That(w.Value(w.Open(b)[0].Cells[0]), Is.EqualTo("Issue 1")); DraftStore.Validate(w.Snapshot());
    }

    [TestCase("remote"), TestCase("local"), TestCase("other"), TestCase("clear")]
    public void SelectResolutionUsesOptionIdsAndExplicitClear(string choice)
    {
        var a = EditingTests.Registration(count: 1); var w = new EditingWorkspace(a.Snapshot.Id.Scope); var cell = w.Open(a)[0].Cells[1];
        w.Commit("P1", cell, "done", true); w.Reconcile(a, Remote(a, "Issue 1", "dup1"));
        var value = choice switch { "remote" => new LocalValue("dup1"), "local" => new("done"), "other" => new("dup2"), _ => new(null, true) };
        w.Resolve("P1", w.Decision(cell.Key!), value);
        Assert.That(w.Field(cell)!.Baseline, Is.EqualTo("dup1")); Assert.That(w.Value(cell), Is.EqualTo(value.Value));
        Assert.That(w.DifferenceCount, Is.EqualTo(choice == "remote" ? 0 : 1)); DraftStore.Validate(w.Snapshot());
    }

    [Test]
    public async Task RestartRecoversDurableCheckpointEvenBeforeSessionPublication()
    {
        var store = new RegistrationStore(Path.Combine(Path.GetTempPath(), "ghpb-publication-gap-" + Guid.NewGuid())); var ds = new DraftStore(store.Root);
        var a = EditingTests.Registration(count: 1); await store.SaveAsync(a); var w = new EditingWorkspace(a.Snapshot.Id.Scope); var cell = w.Open(a)[0].Cells[0];
        w.Commit("P1", cell, "Local B"); await ds.SaveAsync(w.Snapshot(), 0);
        var candidate = EditingWorkspace.Restore(w.Snapshot()); var remote = Remote(a, "External C"); candidate.Reconcile(a, remote); candidate.SetRegistrations([remote]);
        await ds.SaveAsync(candidate.Snapshot(), w.Revision);
        // Simulate losing the in-memory publication after the file commit: neither old workspace nor cache is updated.
        Assert.That(w.Field(cell)!.Conflict, Is.False);
        var restored = new RegistrationWorkspace(store); await restored.RestoreAsync(); await restored.SelectProfileAsync(w.Scope); await restored.SelectAsync(a.Snapshot.Id);
        Assert.That(restored.Selected!.Snapshot.Issues.Values.Single().Title.Value, Is.EqualTo("External C"));
        Assert.That(restored.Drafts!.Workspace.Field(cell)!.Baseline, Is.EqualTo("Issue 1"));
        Assert.That(restored.Drafts.Workspace.Field(cell)!.Observation!.Value, Is.EqualTo("External C"));
        Assert.That(restored.Drafts.Workspace.Value(cell), Is.EqualTo("Local B"));
    }
}
