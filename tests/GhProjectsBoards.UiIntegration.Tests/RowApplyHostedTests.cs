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
        await Ui.DialogReady("ApplyReviewDialog");
        await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() => {
            var dialog = Ui.Dialog("ApplyReviewDialog")!; var list = Ui.Find<ListView>("ApplyTargetRows", dialog);
            Assert.That(list.Items.Count, Is.Zero); Assert.That(list.SelectedItems, Is.Empty);
            Assert.That(Ui.Find<TextBlock>("ApplyTargetCounts", dialog).Text, Does.Contain("非表示の作業 1"));
            var hidden = Ui.Find<CheckBox>("ApplyIncludeHidden", dialog); Assert.That(hidden.IsChecked, Is.False); hidden.IsChecked = true;
            Assert.That(list.Items.Count, Is.EqualTo(1));
            Assert.That(list.SelectedItems, Is.Empty);
        });
        await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() => { var list = Ui.Find<ListView>("ApplyTargetRows", Ui.Dialog("ApplyReviewDialog")); list.SelectedItems.Add(list.Items[0]); });
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
        await Ui.Run(() => { Assert.That(Ui.DialogText("ApplyReviewDialog"), Does.Contain("選択 1行").And.Contain("更新 1件")); Ui.DialogButton("ApplyReviewDialog", "PrimaryButton"); });
        await Ui.Until(() => !Workspace.IsBusy && h.Writes.Count == 1);
        await Ui.DialogReady("ApplyHistoryDialog"); await Ui.Run(() => Ui.DialogButton("ApplyHistoryDialog", "CloseButton")); await Ui.Idle();
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
        await Ui.Ready<Button>("GridReapply");
        await Ui.Run(() => {
            var grid = Ui.Tree(panel).OfType<EditingGrid>().Single();
            Assert.That(grid.DisplayedRowIds, Is.EqualTo(new[] { "item-created1" }));
            Assert.That(grid.SelectionIdentity?.Field, Is.EqualTo(new FieldKey("Title", "created1")));
            Assert.That(Ui.Find<TextBox>("GridCell0_0").Text, Is.EqualTo("pending"));
            Assert.That(Work.Creations.Single().Completed, Is.True);
            Ui.Click("GridReapply"); Assert.That(grid.DisplayedRowIds, Is.Empty);
        });
        await Ui.OpenHistory();
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => Ui.Find<Expander>("CreationHistoryDetails-" + Work.Creations.Single().Id, Ui.Dialog("ApplyHistoryDialog")).IsExpanded = true);
        await Ui.Until(() => Ui.DialogText("ApplyHistoryDialog").Contains(local));
        await Ui.Run(() => { Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain(local)); Ui.DialogButton("ApplyHistoryDialog", "CloseButton"); });
    }
}
