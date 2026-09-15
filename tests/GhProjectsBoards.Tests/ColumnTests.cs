using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ColumnTests
{
    internal static ProjectRegistration Project(string id = "P1")
    {
        var p = EditingTests.Registration(id, count: 2);
        var fields = new[] { "A", "B", "C" }.Select(key => p.Snapshot.Fields[0] with {
            Id = new(p.Snapshot.Id.Scope, id + key), Name = "Duplicate name",
            Options = [new(key + "0", "Todo"), new(key + "1", "Done")]
        }).ToArray();
        return p with { DefaultRepository = "owner/repo", Snapshot = p.Snapshot with { Fields = fields,
            Items = p.Snapshot.Items.Select(i => i with { Values = fields.Select(f => i.Values[0] with {
                FieldId = f.Id, OptionId = f.Options[0].Id, ValueId = i.Id.NodeId + f.Id.NodeId
            }).ToArray() }).ToArray() } };
    }
    private static EditingWorkspace Work(params ProjectRegistration[] ps)
    { var w = new EditingWorkspace(ps[0].Snapshot.Id.Scope); w.SetRegistrations(ps); foreach (var p in ps) w.Open(p); return w; }
    internal static ColumnCandidate Reordered(EditingWorkspace w, ProjectRegistration p)
    {
        var c = w.PrepareColumns(p); var cols = c.Columns;
        return c with { Columns = [cols[0], cols[3], cols[1], cols[2] with { Visible = false }, cols[4]] };
    }
    private static string Json(object? value) => System.Text.Json.JsonSerializer.Serialize(value);
    [Test]
    public void CheckpointUsesExplicitColumnSchema()
    {
        var w = new EditingWorkspace(EditingTests.Registration().Snapshot.Id.Scope);
        Assert.That(w.Snapshot().Version, Is.EqualTo(7));
    }
    [TestCase(false), TestCase(true)]
    public void VisibleRectangleUsesOriginalKeysAndUndoAfterReordering(bool mixed)
    {
        var p = Project(); var w = Work(p); if (mixed) w.AddRow(p);
        var canonical = w.Open(p); w.SaveColumns(Reordered(w, p));
        var displayed = w.Columns(p).Resolve(canonical);
        Assert.That(displayed[0].Cells.Select(c => c.Key?.FieldId), Is.EqualTo(new string?[] { null, "P1C", "P1A", null }));
        Assert.That(w.Open(p)[0].Cells.Select(c => c.Key?.FieldId), Is.EqualTo(new string?[] { null, "P1A", "P1B", "P1C", null }));
        w.Paste("P1", displayed, mixed ? 1 : 0, 1, "Done\tDone\nDone\tDone");
        foreach (var row in canonical.Skip(mixed ? 1 : 0).Take(2))
        {
            Assert.That(w.Value(row.Cells[1]), Is.EqualTo("A1")); Assert.That(w.Value(row.Cells[3]), Is.EqualTo("C1"));
            Assert.That(w.Value(row.Cells[2]), Is.EqualTo(row.IsLocal ? null : "B0"));
        }
        var snapshot = w.Snapshot(); var before = Json(snapshot.History);
        w.SaveColumns(w.PrepareColumns(p) with { Columns = w.DefaultColumns(p) });
        Assert.That(Json(w.Snapshot().History), Is.EqualTo(before));
        w.Undo("P1");
        foreach (var row in canonical.Skip(mixed ? 1 : 0).Take(2))
        { Assert.That(w.Value(row.Cells[1]), Is.EqualTo(row.IsLocal ? null : "A0")); Assert.That(w.Value(row.Cells[3]), Is.EqualTo(row.IsLocal ? null : "C0")); }
    }
    [Test]
    public void AppendUsesVisibleInputOrderAndDuplicateCopiesHiddenCommittedValues()
    {
        var p = Project(); var w = Work(p); var canonical = w.Open(p);
        w.Commit("P1", canonical[0].Cells[2], "Done"); w.SaveColumns(Reordered(w, p));
        var appended = w.AppendRows(p, "New\tDone\tDone").Single();
        var row = w.LocalRows.Single(r => r.Id == appended);
        Assert.That(row.Selects.Select(s => (s.FieldId, s.OptionId, s.Intent)), Is.EquivalentTo(new[] { ("P1A", "A1", "Set"), ("P1B", (string?)null, "Unspecified"), ("P1C", "C1", "Set") }));
        var duplicate = w.DuplicateRows(p, [w.Columns(p).Resolve(canonical)[0]]).Single();
        Assert.That(w.LocalRows.Single(r => r.Id == duplicate).Selects.Single(s => s.FieldId == "P1B").OptionId, Is.EqualTo("B1"));
        var before = Json(w.Snapshot()); Assert.Throws<InvalidOperationException>(() => w.AppendRows(p, "Good\tDone\nBad\tMissing"));
        Assert.That(Json(w.Snapshot()), Is.EqualTo(before));
    }
    [Test]
    public void DefinitionsUseIdsRetainUnavailableAndRejectObsoleteCandidates()
    {
        var p = Project(); var w = Work(p); w.SaveColumns(Reordered(w, p)); var stale = w.PrepareColumns(p);
        var changed = p with { Snapshot = p.Snapshot with { Fields = [p.Snapshot.Fields[0] with { Name = "Renamed" }, p.Snapshot.Fields[1] with { DataType = "TEXT" },
            p.Snapshot.Fields[2], p.Snapshot.Fields[1] with { Id = new(w.Scope, "NEW") }] } };
        w.SetRegistrations([changed]);
        var layout = w.Columns(changed);
        Assert.That(layout.Visible.Select(c => c.Id.FieldId), Is.EqualTo(new string?[] { null, "P1C", "P1A", "NEW", null }));
        Assert.That(layout.Columns.Single(c => c.Id.FieldId == "P1B").Available, Is.False);
        Assert.That(layout.Columns.Single(c => c.Id.FieldId == "P1A").Name, Is.EqualTo("Renamed"));
        Assert.Throws<InvalidOperationException>(() => w.SaveColumns(stale));
        Assert.That(w.Snapshot().ColumnPreferences![0].Columns.Any(c => c.Id.FieldId == "P1B"), Is.True);
    }
    [Test]
    public void NewlyObservedDefaultRetainsUnavailablePreferenceWithoutAnotherSettingsSave()
    {
        var p = Project(); var w = Work(p); w.SaveColumns(Reordered(w, p));
        var added = p with { Snapshot = p.Snapshot with { Fields = p.Snapshot.Fields.Append(p.Snapshot.Fields[0] with { Id = new(w.Scope, "D") }).ToArray() } };
        w.SetRegistrations([added]);
        Assert.That(w.Columns(added).Visible.Any(c => c.Id.FieldId == "D"), Is.True);
        w.SetRegistrations([p]); w = EditingWorkspace.Restore(w.Snapshot());
        Assert.That(w.Columns(p).Columns.Any(c => c.Id.FieldId == "D" && !c.Available && c.Preference.Visible && c.Preference.Width == 200), Is.True);
    }
    [TestCase(79), TestCase(1201), TestCase(double.NaN), TestCase(double.PositiveInfinity)]
    public void InvalidWidthsRejectWholeCandidate(double width)
    {
        var p = Project(); var w = Work(p); var before = Json(w.Snapshot()); var c = w.PrepareColumns(p);
        c.Columns[1] = c.Columns[1] with { Width = width };
        Assert.Throws<InvalidDataException>(() => w.SaveColumns(c)); Assert.That(Json(w.Snapshot()), Is.EqualTo(before));
    }
    [Test]
    public async Task RealCheckpointSaveMergesLaterWorkFailureKeepsCandidateAndRestoresTwoProjects()
    {
        var p = Project(); var p2 = Project("P2"); var w = Work(p, p2);
        var root = Path.Combine(Path.GetTempPath(), "ghpb-columns-" + Guid.NewGuid().ToString("N")); var store = new DraftStore(root);
        var s = new DraftSession(store, w, 0); Assert.That(await s.FlushAsync(), Is.True);
        var candidate = Reordered(w, p); w.SetBuffer(w.Open(p)[0].Cells[0], "later pending"); w.AddRow(p2);
        var before = Json(w.Snapshot());
        using (var locked = new FileStream(Path.Combine(root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.That(await s.CommitAsync(c => { c.SaveColumns(candidate); return c; }, () => true), Is.False);
        Assert.That(Json(s.Workspace.Snapshot()), Is.EqualTo(before));
        Assert.That(await s.CommitAsync(c => { c.SaveColumns(candidate); return c; }, () => true), Is.True);
        var restored = EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!);
        Assert.That(restored.Buffer(restored.Open(p)[0].Cells[0]), Is.EqualTo("later pending"));
        Assert.That(restored.LocalRows.Single().ProjectId, Is.EqualTo("P2"));
        Assert.That(restored.Columns(p).Visible.Select(c => c.Id.FieldId), Is.EqualTo(new string?[] { null, "P1C", "P1A", null }));
        Assert.That(restored.Columns(p2).Visible.Select(c => c.Id.FieldId), Is.EqualTo(new string?[] { null, "P2A", "P2B", "P2C", null }));
        Assert.That(await s.CommitAsync(c => { c.SaveColumns(candidate); return c; }, () => false), Is.False);
        Assert.That(Json(await store.LoadAsync(w.Scope)), Is.EqualTo(Json(restored.Snapshot())));
    }
    [TestCase(1), TestCase(2), TestCase(3), TestCase(4), TestCase(5)]
    public async Task EarlierRecordsMigrateWithoutDroppingWork(int version)
    {
        var p = Project(); var w = Work(p); var row = w.Open(p)[0]; w.Commit("P1", row.Cells[1], "Done"); w.SetBuffer(row.Cells[0], "pending");
        if (version >= 4) w.AddRow(p);
        var original = w.Snapshot() with { Version = version, ColumnPreferences = null, LocalRows = version >= 4 ? w.Snapshot().LocalRows : null };
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-columns-migration-" + Guid.NewGuid().ToString("N")));
        await store.SaveAsync(original, 0); var restored = EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!);
        restored.SaveColumns(Reordered(restored, p)); await store.SaveAsync(restored.Snapshot(), original.Revision);
        var record = (await store.LoadAsync(w.Scope))!;
        Assert.That(record.Version, Is.EqualTo(7)); Assert.That(Json(record.Fields), Is.EqualTo(Json(original.Fields)));
        Assert.That(Json(record.History), Is.EqualTo(Json(original.History))); Assert.That(File.Exists(store.FileFor(w.Scope) + ".bak"), Is.True);
    }
    [Test]
    public async Task MalformedSettingsKeepCheckpointAndRecoveryMaterial()
    {
        var p = Project(); var w = Work(p); w.SaveColumns(Reordered(w, p)); w.SetBuffer(w.Open(p)[0].Cells[0], "Keep");
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-columns-corrupt-" + Guid.NewGuid().ToString("N")));
        await store.SaveAsync(w.Snapshot(), 0); var path = store.FileFor(w.Scope); var original = await File.ReadAllTextAsync(path);
        foreach (var record in new[] { w.Snapshot() with { ColumnPreferences = null }, w.Snapshot() with { Version = 5 },
            w.Snapshot() with { ColumnPreferences = [new("P1", [new(ColumnIdentity.Title, false, 320), new(ColumnIdentity.Reference, true, 200)])] },
            w.Snapshot() with { ColumnPreferences = [new("P1", [new(ColumnIdentity.Title, true, 320), new(new("Field", "A"), true, 200), new(new("Field", "A"), true, 200), new(ColumnIdentity.Reference, true, 200)])] } })
        {
            await File.WriteAllTextAsync(path, Json(record)); var corrupt = await File.ReadAllTextAsync(path);
            Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(w.Scope));
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(corrupt));
            var restored = await store.CheckpointsAsync(); Assert.That(restored.Problems.Any(p => p.Kind == "InvalidCheckpoint"), Is.True);
        }
        await File.WriteAllTextAsync(path, original);
        Assert.That(EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!).Buffer(w.Open(p)[0].Cells[0]), Is.EqualTo("Keep"));
    }
    [Test]
    public async Task HiddenExistingUpdateAndCreationKeepSyntheticPayloadsAndHistory()
    {
        var h = await CreationHarness.Create(2); var s = h.Session; var p = h.Workspace.Selected!;
        var rows = s.Workspace.Open(p); s.Workspace.Commit("P1", rows[0].Cells[1], "Done");
        var local = h.Add("Hidden setup"); s.Workspace.Commit("P1", s.Workspace.Open(p).Single(r => r.ItemId == local).Cells[1], "Done");
        var settings = s.Workspace.PrepareColumns(p); settings.Columns[1] = settings.Columns[1] with { Visible = false };
        Assert.That(await s.CommitAsync(w => { w.SaveColumns(settings); return w; }, () => true), Is.True);
        Assert.That(h.Writes, Is.Empty); Assert.That(h.Existing.Writes, Is.Empty);
        var reset = s.Workspace.PrepareColumns(p) with { Columns = s.Workspace.DefaultColumns(p) };
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { rows[0].ItemId, local }); var review = h.Workspace.ApplyReview!;
        Assert.That(review.Batch.Operations.Single().Key.FieldId, Is.EqualTo("P1-status"));
        Assert.That(review.Batch.Creations!.Single().Selects.Single().FieldId, Is.EqualTo("P1-status"));
        await h.Workspace.ConfirmApplyAsync(review);
        Assert.That(h.Existing.Writes.Where(v => v.TryGetProperty("fieldId", out _)).Select(v => v.GetProperty("fieldId").GetString()), Is.EqualTo(new[] { "P1-status", "P1-status" }));
        var history = Json(s.Workspace.Journal);
        Assert.That(await s.CommitAsync(w => { w.SaveColumns(reset); return w; }, () => true), Is.True);
        Assert.That(Json(s.Workspace.Journal), Is.EqualTo(history)); await h.Restart();
        Assert.That(Json(h.Session.Workspace.Journal), Is.EqualTo(history));
        var v5 = h.Session.Workspace.Snapshot() with { Version = 5, ColumnPreferences = null };
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-v5-columns-history-" + Guid.NewGuid().ToString("N")));
        await store.SaveAsync(v5, 0); var migrated = EditingWorkspace.Restore((await store.LoadAsync(s.Workspace.Scope))!);
        await store.SaveAsync(migrated.Snapshot(), v5.Revision);
        Assert.That(Json((await store.LoadAsync(s.Workspace.Scope))!.Journal), Is.EqualTo(history));
        Assert.That(Json(migrated.Snapshot().Fields), Is.EqualTo(Json(v5.Fields)));
        Assert.That(Json(migrated.Snapshot().LocalRows), Is.EqualTo(Json(v5.LocalRows)));
        Assert.That(Json(migrated.Snapshot().History), Is.EqualTo(Json(v5.History)));
    }
}
