using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [TestCase(960, 600), TestCase(1280, 800)]
    public async Task InterruptedApplyExplainsRecoveryAndRechecksInPlaceWithoutSending(int width, int height)
    {
        await Ui.Run(async () =>
        {
            var scale = Ui.Root.XamlRoot.RasterizationScale;
            Ui.Window.AppWindow.Resize(new((int)(width * scale), (int)(height * scale)));
            var rows = Work.Open(Workspace.Selected!);
            Work.Commit("P1", rows[0].Cells[0], "Applied title");
            Work.Commit("P1", rows[0].Cells[1], "done", true);
            Work.Commit("P1", rows[1].Cells[0], "Remaining title");
            h.Existing.OnMutation = () => { if (h.Existing.Writes.Count == 2) Workspace.Cancel(); };
            await h.Apply("P1-T1", "P1-T2"); h.Existing.OnMutation = null;
            Ui.Click("ReviewApplyButton");
        });
        await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Workspace.IsBusy);
        ContentDialog dialog = null!;
        await Ui.Run(() =>
        {
            dialog = Ui.Dialog("ApplyReviewDialog")!;
            Assert.That(Ui.Find<TextBlock>("ApplyBlockReason", dialog).Text,
                Does.Contain("前回").And.Contain("未送信 1フィールド").And.Contain("要確認 1フィールド"));
            Assert.That(dialog.IsPrimaryButtonEnabled, Is.False);
            Assert.That(Ui.Find<Button>("ApplyRestartReview", dialog).IsEnabled, Is.True);
            Ui.Toggle(Ui.Find<CheckBox>("ApplySelectAll", dialog));
        });
        await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(async () =>
        {
            await ApplyInformationEvidence.Capture(dialog, "interrupted-apply-recovery");
            Ui.Click(Ui.Find<Button>("ApplyRestartReview", dialog));
        });
        await Ui.Until(() => dialog.IsPrimaryButtonEnabled);
        await Ui.Run(async () =>
        {
            Assert.That(Ui.Dialog("ApplyReviewDialog"), Is.SameAs(dialog));
            Assert.That(Workspace.ApplyReview!.Batch.Operations.Single().Intended.Value, Is.EqualTo("Remaining title"));
            Assert.That(Ui.Find<ListView>("ApplyTargetRows", dialog).SelectedItems, Has.Count.EqualTo(1));
            Assert.That(h.Existing.Writes, Has.Count.EqualTo(2), "Review recovery must not dispatch.");
            await ApplyInformationEvidence.Capture(dialog, "interrupted-apply-reviewed");
            Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
    }
}
