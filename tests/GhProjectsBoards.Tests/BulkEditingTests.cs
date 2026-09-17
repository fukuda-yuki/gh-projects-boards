using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class BulkEditingTests
{
    [TestCase("fill"), TestCase("paste"), TestCase("down")]
    public async Task TenRowsKeepSourceEditSeparateFromOneBulkUndoAfterDurableReload(string route)
    {
        var p = EditingTests.Registration(count: 10); var w = new EditingWorkspace(p.Snapshot.Id.Scope);
        w.SetRegistrations([p]); var rows = w.Open(p);
        w.Commit("P1", rows[0].Cells[1], "done", true);

        if (route == "paste") w.PasteSelection("P1", rows, new(0, 1, 10), "Done", w.CopyCells("P1", rows, new(0, 1)));
        else w.Fill("P1", rows, 0, 1, route == "fill" ? 1 : 0, 9);

        Assert.That(w.Fields.Where(f => f.Change is not null).Select(f => f.Key), Is.EquivalentTo(
            new[] { "P1T1", "P1T2", "P1T3", "P1T4", "P1T5", "P1T6", "P1T7", "P1T8", "P1T9", "P1T10" }.Select(id => new FieldKey("Select", id, "P1", "P1-status"))));
        Assert.That(rows.Select(r => w.Value(r.Cells[1])), Is.All.EqualTo("done"));
        Assert.That(w.Snapshot().History, Has.Length.EqualTo(2));
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-bulk-" + Guid.NewGuid()));
        Assert.That(await new DraftSession(store, w, 0).FlushAsync(), Is.True);
        var restored = EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!); var reopened = restored.Open(p);
        restored.Undo("P1");
        Assert.That(restored.DifferenceCount, Is.EqualTo(1));
        Assert.That(restored.Value(reopened[0].Cells[1]), Is.EqualTo("done"));
        Assert.That(reopened.Skip(1).Select(r => restored.Value(r.Cells[1])), Is.All.EqualTo("todo"));
        restored.Undo("P1"); Assert.That(restored.DifferenceCount, Is.Zero);
        Assert.That(restored.Snapshot().Journal, Is.Empty);
    }

    [TestCase(3, 1, "Done\nTodo", false)]
    [TestCase(2, 2, "Done", false)]
    [TestCase(2, 1, "Done\tTodo\nTodo\tDone", false)]
    [TestCase(2, 1, "Done\nTodo", true)]
    [TestCase(3, 1, "", true)]
    [TestCase(3, 1, "Done", true)]
    public void SelectedPasteRequiresMatchingShapeOrSingleColumnBroadcast(int height, int width, string text, bool accepted)
    {
        var p = EditingTests.Registration(count: 10); var w = new EditingWorkspace(p.Snapshot.Id.Scope); var rows = w.Open(p);
        var before = w.Snapshot();
        if (accepted)
        {
            w.PasteSelection("P1", rows, new(0, 1, height, width), text);
            Assert.That(w.DifferenceCount, Is.EqualTo(text == "" ? 0 : text == "Done" ? 3 : 1));
            Assert.That(w.Snapshot().History.Length, Is.EqualTo(text == "" ? 0 : 1));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => w.PasteSelection("P1", rows, new(0, 1, height, width), text));
            Assert.That(w.Snapshot().Fields, Is.EqualTo(before.Fields));
            Assert.That(w.Snapshot().History, Is.Empty);
        }
    }

    [TestCase("readonly"), TestCase("pending"), TestCase("conflict"), TestCase("option")]
    public void InvalidMiddleDestinationRejectsAllAndIdentifiesTheTarget(string problem)
    {
        var p = EditingTests.Registration(count: 10); var w = new EditingWorkspace(p.Snapshot.Id.Scope); var rows = w.Open(p);
        w.Commit("P1", rows[0].Cells[1], "done", true);
        if (problem == "readonly") rows[4].Cells[1] = rows[4].Cells[1] with { Reason = "更新権限なし" };
        if (problem == "option") rows[4].Cells[1] = rows[4].Cells[1] with { Options = [new("todo", "Todo")] };
        if (problem == "pending") w.SetBuffer(rows[4].Cells[1], "pending");
        if (problem == "conflict")
        {
            w.Commit("P1", rows[4].Cells[1], "done", true);
            var record = w.Snapshot();
            w = EditingWorkspace.Restore(record with { Fields = record.Fields.Select(f => f.Key == rows[4].Cells[1].Key ? f with {
                Conflict = true, Observation = new("observation", p.Snapshot.Id, p.RetrievedAt, "dup1", ValueAvailability.Present, null, rows[4].Cells[1].Options)
            } : f).ToArray() });
        }
        var before = w.Snapshot();

        var error = Assert.Throws<InvalidOperationException>(() => w.Fill("P1", rows, 0, 1, 1, 9));

        Assert.That(error!.Message, Does.Contain("P1T5"));
        Assert.That(w.Snapshot().Fields, Is.EqualTo(before.Fields));
        Assert.That(w.Snapshot().History, Is.EqualTo(before.History));
    }

    [Test]
    public void InternalDuplicateLabelCopiesTheOptionIdAndExternalLabelRemainsAmbiguous()
    {
        var p = EditingTests.Registration(count: 10); var w = new EditingWorkspace(p.Snapshot.Id.Scope); var rows = w.Open(p);
        w.Commit("P1", rows[0].Cells[1], "dup2", true);
        var copy = w.CopyCells("P1", rows, new(0, 1));
        w.PasteSelection("P1", rows, new(1, 1, 9), "Duplicate", copy);
        Assert.That(rows.Select(r => w.Value(r.Cells[1])), Is.All.EqualTo("dup2"));
        w.Undo("P1");
        Assert.Throws<InvalidOperationException>(() => w.PasteSelection("P1", rows, new(1, 1, 9), "Duplicate"));
        Assert.That(w.DifferenceCount, Is.EqualTo(1));
    }

    [Test]
    public void FilteredSortedHundredDestinationsAndHiddenUndoUseOriginalIds()
    {
        var p = EditingTests.Registration(count: 201); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); var rows = w.Open(p);
        foreach (var r in rows.Where((_, i) => i % 2 == 0)) w.Commit("P1", r.Cells[0], "keep " + r.ItemId);
        var view = w.PrepareRowView(p);
        w.SaveRowView(view with { Definition = new("Title", true, Title: "keep") });
        var projection = new RowProjection(p.Snapshot.Id); projection.Reapply(w, p);
        var displayed = projection.Resolve(w.Open(p));
        Assert.That(displayed.Length, Is.EqualTo(101));
        var expected = Enumerable.Range(1, 201).Where(i => i % 2 == 1).Select(i => "P1T" + i).ToHashSet();
        w.Commit("P1", displayed[0].Cells[1], "done", true);
        w.Fill("P1", displayed, 0, 1, 1, 100);
        Assert.That(w.Fields.Where(f => f.Key.Kind == "Select" && f.Change is not null).Select(f => f.Key.NodeId), Is.EquivalentTo(expected));
        var hidden = w.PrepareRowView(p); w.SaveRowView(hidden with { Definition = new(Title: "no matches") }); projection.Reapply(w, p);
        Assert.That(projection.Ids, Is.Empty);
        w.Undo("P1");
        Assert.That(w.Fields.Count(f => f.Key.Kind == "Select" && f.Change is not null), Is.EqualTo(1));
        Assert.That(w.Fields.Where(f => f.Key.Kind == "Select" && f.Key != displayed[0].Cells[1].Key).Select(f => f.Change), Is.All.Null);
    }

    [Test]
    public void EmptyOrPendingSourceCannotFillAndBaselineReturnRemovesDifferences()
    {
        var p = EditingTests.Registration(count: 3); var w = new EditingWorkspace(p.Snapshot.Id.Scope); var rows = w.Open(p);
        w.Clear("P1", [rows[0].Cells[1]]);
        Assert.That(Assert.Throws<InvalidOperationException>(() => w.Fill("P1", rows, 0, 1, 1, 2))!.Message, Does.Contain("値をクリア"));
        w.Undo("P1"); w.Commit("P1", rows[2].Cells[1], "done", true);
        w.SetBuffer(rows[0].Cells[1], "unfinished");
        Assert.Throws<InvalidOperationException>(() => w.Fill("P1", rows, 0, 1, 1, 2));
        w.SetBuffer(rows[0].Cells[1], null); w.Fill("P1", rows, 0, 1, 1, 2);
        Assert.That(w.DifferenceCount, Is.Zero);
        w.Undo("P1"); Assert.That(w.Value(rows[2].Cells[1]), Is.EqualTo("done"));
    }

    [Test]
    public void EqualCommittedValueDoesNotCreateDifferenceRevisionOrUndo()
    {
        var p = EditingTests.Registration(count: 10);
        var w = new EditingWorkspace(p.Snapshot.Id.Scope); var rows = w.Open(p);
        var before = w.Snapshot();

        w.Paste("P1", rows, 0, 1, "Todo\nTodo\nTodo");

        Assert.Multiple(() => {
            Assert.That(w.DifferenceCount, Is.Zero);
            Assert.That(w.Revision, Is.EqualTo(before.Revision));
            Assert.That(w.Snapshot().History, Is.Empty);
        });
    }

    [Test]
    public void SourceOrderWithoutFiltersDoesNotRequireReapplyAfterValueEdit()
    {
        var p = EditingTests.Registration(count: 10);
        var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]);
        var rows = w.Open(p); var projection = new RowProjection(p.Snapshot.Id); projection.Reapply(w, p);

        w.Commit("P1", rows[0].Cells[1], "done", true);

        Assert.That(projection.NeedsReapply(w, p), Is.False);
        Assert.That(projection.Ids, Is.EqualTo(new[] { "P1T1", "P1T2", "P1T3", "P1T4", "P1T5", "P1T6", "P1T7", "P1T8", "P1T9", "P1T10" }));
    }

    [Test]
    public void InternalCopyRejectsAnotherFieldEvenWhenLabelsAreEqual()
    {
        var p = ColumnTests.Project(); var w = new EditingWorkspace(p.Snapshot.Id.Scope); var rows = w.Open(p);
        w.Commit("P1", rows[0].Cells[1], "A1", true); var before = w.Snapshot();
        var copy = w.CopyCells("P1", rows, new(0, 1));
        Assert.Throws<InvalidOperationException>(() => w.PasteSelection("P1", rows, new(0, 2, 2), "Done", copy));
        Assert.That(w.Snapshot().Fields, Is.EqualTo(before.Fields)); Assert.That(w.Snapshot().History, Is.EqualTo(before.History));
    }

    [Test]
    public void LocalSourceBufferAddedAfterCopyRejectsBroadcastWithoutDiscardingIt()
    {
        var p = EditingTests.Registration(count: 2); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); w.Open(p);
        var id = w.AddRow(p); var rows = w.Open(p); var source = rows.Single(r => r.ItemId == id).Cells[0];
        w.Commit("P1", source, "new title"); var copy = w.CopyCells("P1", rows, new(2, 0));
        w.SetBuffer(source, "unfinished"); var before = w.Snapshot();
        Assert.Throws<InvalidOperationException>(() => w.PasteSelection("P1", rows, new(0, 0, 2), "new title", copy));
        Assert.That(w.Snapshot().Fields, Is.EqualTo(before.Fields)); Assert.That(w.Snapshot().LocalRows, Is.EqualTo(before.LocalRows));
        Assert.That(w.Snapshot().History, Is.EqualTo(before.History));
    }

    [Test]
    public void RepeatingLocalSelectValuePreservesOrderRevisionAndUndo()
    {
        var p = ColumnTests.Project(); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); w.Open(p); var id = w.AddRow(p);
        var row = w.Open(p).Single(r => r.ItemId == id);
        w.Commit("P1", row.Cells[1], "A1", true); w.Commit("P1", row.Cells[3], "C1", true); var before = w.Snapshot();
        w.Commit("P1", row.Cells[1], "A1", true);
        Assert.That(w.Revision, Is.EqualTo(before.Revision)); Assert.That(w.Snapshot().LocalRows, Is.EqualTo(before.LocalRows));
        Assert.That(w.Snapshot().History, Is.EqualTo(before.History));
    }

    [Test]
    public void MixedExistingAndLocalFillIsOneAtomicUndoAndRetainsLocalIdentity()
    {
        var p = EditingTests.Registration(count: 2); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); w.Open(p);
        var first = w.AddRow(p); var second = w.AddRow(p); var rows = w.Open(p);
        w.Commit("P1", rows[0].Cells[1], "done", true); var history = w.Snapshot().History.Length;
        w.Fill("P1", rows, 0, 1, 1, 3);
        Assert.That(rows.Select(r => w.Value(r.Cells[1])), Is.All.EqualTo("done"));
        Assert.That(w.Snapshot().History, Has.Length.EqualTo(history + 1));
        w.Undo("P1");
        Assert.That(rows.Select(r => w.Value(r.Cells[1])), Is.EqualTo(new string?[] { "done", "todo", null, null }));
        Assert.That(w.LocalRows.Select(r => r.Id), Is.EqualTo(new[] { first, second }));
    }
}
