using System.Text.Json;
using GhProjectsBoards.App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test]
    public async Task ReopenedViewUndoRestoresRemovedLocalRowTemporarilyWithoutRevealingOtherHiddenWork()
    {
        string hiddenId = "", restoredId = "", preferences = "";
        var expectedExisting = new[] { "P1-T2", "P1-T1" };
        await Ui.Run(async () =>
        {
            Assert.That(await Workspace.PrepareLocalRowsAsync(), Is.True);
            hiddenId = h.Add("Already hidden");
            var p = Workspace.Selected!;
            Assert.That(await Workspace.Drafts!.CommitAsync(w =>
            {
                w.SaveRowView(w.PrepareRowView(p) with { Definition = new("Title", true, Title: "Issue") });
                return w;
            }, () => true), Is.True);
            Ui.Click("GridReapply");
            Assert.That(Ui.Tree(panel).OfType<EditingGrid>().Single().DisplayedRowIds, Is.EqualTo(expectedExisting));
            var snapshot = Work.Snapshot();
            preferences = JsonSerializer.Serialize(new { snapshot.RowPreferences, snapshot.ColumnPreferences });
            Ui.Click("GridAddRow");
        });
        await Ui.Until(() => Work.LocalRows.Count == 2);
        await Ui.Ready<TextBox>("GridCell2_0");
        await Ui.Run(() =>
        {
            restoredId = Work.LocalRows.Single(r => r.Id != hiddenId).Id;
            var cell = Work.Open(Workspace.Selected!).Single(r => r.ItemId == restoredId).Cells[0];
            Work.Commit("P1", cell, "Restored local title");
            var editor = Ui.Find<TextBox>("GridCell2_0");
            Assert.That(editor.Focus(FocusState.Programmatic), Is.True);
            editor.Text = "Unfinished restored title";
        });
        await Ui.Until(() => Work.LocalRows.Single(r => r.Id == restoredId).TitleBuffer == "Unfinished restored title");
        await Ui.ClickCommand("GridRemoveRows");
        await Ui.Until(() => Work.LocalRows.All(r => r.Id != restoredId));
        await Ui.Run(async () => Assert.That(await Workspace.FlushDraftsAsync(), Is.True));
        await Ui.Unmount(panel);
        await Ui.Idle();

        // Reload the real checkpoint and construct a fresh view while the removed ID is absent.
        await Ui.Run(async () =>
        {
            await h.Restart();
            panel = new RegistrationPanel();
            panel.Initialize(Workspace);
        });
        await Ui.Mount(panel);
        await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() =>
        {
            Assert.That(Work.LocalRows.Select(r => r.Id), Is.EqualTo(new[] { hiddenId }));
            Assert.That(Ui.Tree(panel).OfType<EditingGrid>().Single().DisplayedRowIds, Is.EqualTo(expectedExisting));
            Ui.Click("GridUndo");
        });
        await Ui.Until(() => Work.LocalRows.Any(r => r.Id == restoredId));
        await Ui.Run(() =>
        {
            var grid = Ui.Tree(panel).OfType<EditingGrid>().Single();
            Assert.That(grid.DisplayedRowIds, Is.EqualTo(expectedExisting.Append(restoredId).ToArray()),
                "Undo must make its restored local row reachable without revealing already-hidden rows or reordering the view.");
            var local = Work.LocalRows.Single(r => r.Id == restoredId);
            Assert.That(local.Title, Is.EqualTo("Restored local title"));
            Assert.That(local.TitleBuffer, Is.EqualTo("Unfinished restored title"));
            Assert.That(Ui.Find<TextBlock>("RowViewStatus").Text, Does.Contain("一時表示 1"));
            var snapshot = Work.Snapshot();
            Assert.That(JsonSerializer.Serialize(new { snapshot.RowPreferences, snapshot.ColumnPreferences }), Is.EqualTo(preferences));
            Assert.That(h.Writes, Is.Empty);
        });
        await Ui.Ready<TextBox>("GridCell2_0");
        await Ui.Run(() =>
        {
            Assert.That(Ui.Find<TextBox>("GridCell2_0").Text, Is.EqualTo("Unfinished restored title"));
            Ui.Click("GridReapply");
            Assert.That(Ui.Tree(panel).OfType<EditingGrid>().Single().DisplayedRowIds, Is.EqualTo(expectedExisting));
            Assert.That(Work.LocalRows.Single(r => r.Id == restoredId).TitleBuffer, Is.EqualTo("Unfinished restored title"));
            var snapshot = Work.Snapshot();
            Assert.That(JsonSerializer.Serialize(new { snapshot.RowPreferences, snapshot.ColumnPreferences }), Is.EqualTo(preferences));
            Assert.That(h.Writes, Is.Empty);
        });
    }
}
