using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test]
    public async Task FilteredApplyCandidatesRequireExplicitHiddenInclusion()
    {
        await Ui.Run(async () => {
            Assert.That(await Workspace.PrepareLocalRowsAsync(), Is.True);
            var p = Workspace.Selected!; Work.Commit("P1", Work.Open(p)[1].Cells[1], "done", true);
            Assert.That(await Workspace.Drafts!.CommitAsync(w => { w.SaveRowView(w.PrepareRowView(p) with { Definition = new(Title: "Issue 1") }); return w; }, () => true), Is.True);
            Ui.Click("GridReapply"); Ui.Click("ReviewApplyButton");
        });
        await Ui.DialogReady("ApplySelectionDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("ApplySelectionDialog")!; var list = Ui.Find<ListView>("ApplyTargetRows", dialog);
            Assert.That(list.Items.Count, Is.EqualTo(1)); Assert.That(list.SelectedItems, Is.Empty);
            Assert.That(Ui.Find<TextBlock>("ApplyTargetCounts", dialog).Text, Does.Contain("非表示の作業 1"));
            var hidden = Ui.Find<CheckBox>("ApplyIncludeHidden", dialog); Assert.That(hidden.IsChecked, Is.False); hidden.IsChecked = true;
            Assert.That(list.Items.Count, Is.EqualTo(2));
        });
        await Ui.Until(() => Ui.Find<ListView>("ApplyTargetRows", Ui.Dialog("ApplySelectionDialog")).ContainerFromIndex(1) is ListViewItem { IsLoaded: true });
        await Ui.Run(() => { Ui.Select(Ui.Find<ListView>("ApplyTargetRows", Ui.Dialog("ApplySelectionDialog")), 1); Ui.DialogButton("ApplySelectionDialog", "PrimaryButton"); });
        await Ui.DialogReady("ApplyReviewDialog");
        await Ui.Run(() => { Assert.That(Ui.DialogText("ApplyReviewDialog"), Does.Contain("選択行 1 / 更新 1")); Ui.DialogButton("ApplyReviewDialog", "PrimaryButton"); });
        await Ui.Until(() => !Workspace.IsBusy && h.Writes.Count == 1); await Ui.Idle();
        await Ui.Run(() => Assert.That(h.Writes.Single().Input.GetProperty("itemId").GetString(), Is.EqualTo("P1-T2")));
    }
    [Test]
    public async Task PromotionNotificationKeepsDisplayedPositionAndActiveIdentity()
    {
        string local = "";
        await Ui.Run(async () => {
            local = h.Add("A"); var p = Workspace.Selected!;
            Assert.That(await Workspace.Drafts!.CommitAsync(w => { w.SaveRowView(w.PrepareRowView(p) with { Definition = new("Title", Title: "A") }); return w; }, () => true), Is.True);
            Ui.Click("GridReapply");
        });
        await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Focus(FocusState.Programmatic));
        await Ui.Run(async () => { await Workspace.PrepareApplyAsync(new HashSet<string> { local }); });
        await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Focus(FocusState.Programmatic));
        await Ui.Run(async () => {
            h.AfterCreate = () => { var cell = Work.Open(Workspace.Selected!).Single(r => r.ItemId == local).Cells[0]; Work.Commit("P1", cell, "different"); Work.SetBuffer(cell, "pending"); };
            await Workspace.ConfirmApplyAsync(Workspace.ApplyReview!);
        });
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Single().DisplayedRowIds.Contains("item-created1"));
        await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() => {
            var grid = Ui.Tree(panel).OfType<EditingGrid>().Single();
            Assert.That(grid.DisplayedRowIds, Is.EqualTo(new[] { "item-created1" }));
            Assert.That(grid.SelectionIdentity?.Field, Is.EqualTo(new FieldKey("Title", "created1")));
            Assert.That(Ui.Find<TextBox>("GridCell0_0").Text, Is.EqualTo("pending"));
            Assert.That(Work.Creations.Single().Completed, Is.True);
            Ui.Click("GridReapply"); Assert.That(grid.DisplayedRowIds, Is.Empty);
            Ui.Click("ApplyHistoryButton");
        });
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => { Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain(local)); Ui.DialogButton("ApplyHistoryDialog", "CloseButton"); });
    }
}
