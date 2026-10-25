using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

// #224: the review puts the PMO's own edit before the dates it moved, groups those dates under that edit,
// and shows a parent's new child order only around the inserted row.
internal sealed partial class PlanPublishReviewHostedTests
{
    private async Task OpenChain(int count)
    {
        readDay = new(2026, 10, 6);
        FakePlanEditor.Save(root, new(Enumerable.Range(1, count).Select(i => new PlanFakeIssue(
            new("I" + i, "要求タスク " + i, "acme/repo") { Estimate = 8, Remaining = 8, Actual = 0, Start = readDay.AddDays(i - 1), End = readDay.AddDays(i - 1),
                Predecessors = i == 1 ? [] : ["I" + (i - 1)] }, "", true)).ToImmutableArray(), count + 1));
        await Ui.Run(() => Ui.Click("PlanConnect"));
        await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Count == 1 || Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
        await Ui.Until(() => workspace.Session is not null);
        await Ui.Ready<PlanSheetCell>("PlanCell1_Title");
        await Ui.Idle();
    }
    private static string[] ReviewItems() => Ui.Find<ListView>("PlanPublishLines").Items.Cast<PlanPublishReviewGroup>()
        .Select(g => g.Kind + ":" + g.Identity).ToArray();

    [Test, Category("PublishOutcome")]
    public async Task OneRemainingEditComesFirstAndItsRecalculatedDatesAreGroupedUnderIt()
    {
        await OpenChain(4);
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Remaining, 24m)]), Today);
            Ui.Click("PlanPublish");
        });
        await Ui.Ready<PlanPublishGroupView>("PlanPublishGroupI2");
        await Ui.Run(() => {
            // D1: own edits before recalculated dates; the groups start closed.
            Assert.That(ReviewItems(), Is.EqualTo(new[] { "Section:§Own", "Task:I2", "Section:§Recalculated", "Cause:§CauseI2" }));
            var own = string.Join(" ", Ui.Tree(Ui.Find<PlanPublishGroupView>("PlanPublishGroupI2")).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(own, Does.Contain("見込み残存工数  8h → 24h").And.Not.Contain("終了日"));
            var cause = Ui.Find<Button>("PlanPublishCauseI2");
            Assert.That(AutomationProperties.GetName(cause), Is.EqualTo("#2 の見込み残存工数の変更で 3 タスク"));
            Assert.That(Ui.Tree(Ui.Find<PlanPublishGroupView>("PlanPublishSectionRecalculated")).OfType<TextBlock>().First().Text, Is.EqualTo("再計算で動いた日程  3 タスク"));
            Assert.That(Ui.Find<TextBlock>("PlanPublishSummary").Text, Is.EqualTo("3 タスクを発行"));
            Ui.Click("PlanPublishCauseI2");
        });
        await Ui.Ready<PlanPublishGroupView>("PlanPublishRecalculatedI4");
        await Ui.Run(async () => {
            Assert.That(ReviewItems(), Is.EqualTo(new[] { "Section:§Own", "Task:I2", "Section:§Recalculated", "Cause:§CauseI2",
                "Recalculated:I2", "Recalculated:I3", "Recalculated:I4" }));
            var moved = string.Join(" ", Ui.Tree(Ui.Find<PlanPublishGroupView>("PlanPublishRecalculatedI4")).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(moved, Does.Contain("終了日").And.Contain(" → "));
            await RenderedEvidence.Capture(view, "issue224-grouped-recalculation");
            Ui.Click("PlanPublishExpandAll");
        });
        await Ui.Run(() => Assert.That(ReviewItems(), Has.Length.EqualTo(4), "すべて閉じる closes the open group."));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PublishOutcome")]
    public async Task AnInsertedRowShowsOnlyItselfAndItsNeighboursInTheParentOrder()
    {
        readDay = new(2026, 10, 6);
        FakePlanEditor.Save(root, new([new(new("I1", "要求", "acme/repo"), "", true),
            .. Enumerable.Range(2, 6).Select(i => new PlanFakeIssue(new("I" + i, "子 " + i, "acme/repo") { Parent = "I1", Estimate = 8, Remaining = 8 }, "", true))], 8));
        await Ui.Run(() => Ui.Click("PlanConnect"));
        await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Count == 1);
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
        await Ui.Until(() => workspace.Session is not null);
        await Ui.Ready<PlanSheetCell>("PlanCell1_Title");
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new InsertPlanRows([PlanRow.New("追加した子", "acme/repo")], "I4"), Today);
            Ui.Click("PlanPublish");
        });
        await Ui.Ready<PlanPublishGroupView>("PlanPublishGroupI1");
        await Ui.Run(async () => {
            var text = string.Join(" ", Ui.Tree(Ui.Find<PlanPublishGroupView>("PlanPublishGroupI1")).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain("子タスクの順序  新1 の追加による  #3, 新1, #4").And.Not.Contain("#6"));
            Assert.That(Ui.Find<TextBlock>("PlanPublishCounts").Text, Does.EndWith("新しい Issue 1"));
            await RenderedEvidence.Capture(view, "issue224-child-order-excerpt");
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PublishOutcome")]
    public async Task ThePlanTabStaysSelectedAndUndoAndRefreshWaitWhileTheReviewIsOpen()
    {
        await Open(2);
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "変更")]), Today);
            Ui.Click("PlanPublish");
        });
        await Ui.Ready<PlanPublishGroupView>("PlanPublishGroupI1");
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<SelectorBar>().Single().SelectedItem, Is.SameAs(Ui.Find<SelectorBarItem>("PlanShowTasks")));
            Assert.That(Ui.Find<Button>("PlanUndo").IsEnabled, Is.False);
            Assert.That(Ui.Find<SplitButton>("PlanRefresh").IsEnabled, Is.False);
            Ui.Click("PlanPublishClose");
        });
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("PlanUndo").IsEnabled, Is.True);
            Assert.That(Ui.Find<SplitButton>("PlanRefresh").IsEnabled, Is.True);
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }
}
