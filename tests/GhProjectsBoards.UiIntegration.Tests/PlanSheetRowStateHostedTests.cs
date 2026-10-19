using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

// #231 slice B: the row context menu, the pending-state presentation and the review wording. The commands
// themselves are covered by the Core and adapter suites; this file covers the surface the PMO operates.
internal sealed partial class PlanSheetHostedTests
{
    [Test]
    public async Task RowMenuOffersTheStateCommandsAndDisabledItemsExplainThemselves()
    {
        var published = new PlanRow("I1", "Published", "acme/repo") { Fixed = true };
        await MountPresentation([published], [published]);
        await Select(1, PlanField.Title);
        await Ui.Run(() => sheet.ShowRowMenu("I1", PlanField.Title, sheet.Realized.Single(r => r.Identity == "I1")));
        await Ui.Ready<MenuFlyoutItem>("PlanRowComplete");
        await Ui.Run(() => {
            foreach (var id in new[] { "PlanRowDelete", "PlanRowComplete", "PlanRowCancel", "PlanRowRemove", "PlanRowReopen" })
                Assert.That(Ui.Find<MenuFlyoutItem>(id), Is.Not.Null, id);
            Assert.That(Ui.Find<MenuFlyoutItem>("PlanRowComplete").IsEnabled, Is.True);
            Assert.That(Ui.Find<MenuFlyoutItem>("PlanRowCancel").IsEnabled, Is.True);
            Assert.That(Ui.Find<MenuFlyoutItem>("PlanRowRemove").IsEnabled, Is.True);
            // An open row cannot be reopened, and the reason is on the item, not only in a log.
            Assert.That(Ui.Find<MenuFlyoutItem>("PlanRowReopen").IsEnabled, Is.False);
            Assert.That(AutomationProperties.GetHelpText(Ui.Find<MenuFlyoutItem>("PlanRowReopen")), Is.EqualTo("未完了の行です"));
            // A published Issue cannot be deleted from the plan; the item points at the three commands.
            Assert.That(Ui.Find<MenuFlyoutItem>("PlanRowDelete").IsEnabled, Is.False);
            Assert.That(AutomationProperties.GetHelpText(Ui.Find<MenuFlyoutItem>("PlanRowDelete")), Does.Contain("完了・中止・この版から外す"));
        });
    }

    [Test]
    public async Task AnUnpublishedRowCannotBeClosedAndSaysWhy()
    {
        var published = new PlanRow("I1", "Published", "acme/repo") { Fixed = true };
        await MountPresentation([published], [published]);
        await Ui.Run(async () => { await session.Execute(new InsertPlanRows([PlanRow.New("Local")]), Today); sheet.Refresh(); });
        var local = session.Document.State.Rows.Single(r => r.Identity.StartsWith("local:", StringComparison.Ordinal));
        await Ui.Until(() => sheet.Realized.Any(r => r.Identity == local.Identity));
        await Ui.Run(() => sheet.ShowRowMenu(local.Identity, PlanField.Title, sheet.Realized.Single(r => r.Identity == local.Identity)));
        await Ui.Ready<MenuFlyoutItem>("PlanRowComplete");
        await Ui.Run(() => {
            Assert.That(Ui.Find<MenuFlyoutItem>("PlanRowComplete").IsEnabled, Is.False, "a row with no Issue has nothing to close");
            Assert.That(AutomationProperties.GetHelpText(Ui.Find<MenuFlyoutItem>("PlanRowComplete")), Is.EqualTo("未発行の行です"));
        });
    }

    [Test]
    public async Task CancellingARowTagsDimsAndStrikesItAndSaysSoOnTheSelectionLine()
    {
        var published = new PlanRow("I1", "Published", "acme/repo") { Fixed = true, Estimate = 8 };
        await MountPresentation([published], [published]);
        await Select(1, PlanField.Title);
        await Ui.Run(async () => { await session.Execute(new ClosePlanRows(["I1"], Cancelled: true), Today); sheet.Refresh(); });
        await Ui.Until(() => sheet.Realized.Any(r => r.Identity == "I1" && r.StateTagText is not null));
        await Ui.Run(() => {
            var row = sheet.Realized.Single(r => r.Identity == "I1");
            // Both marks are built with the row, so a failure in one must not hide the other.
            Assert.Multiple(() => {
                Assert.That(row.StateTagText, Is.EqualTo("中止にする"));
                Assert.That(row.StruckThrough, Is.True, "中止 is struck through");
                Assert.That(row.Opacity, Is.LessThan(1), "中止 is dimmed");
            });
            var indicator = Ui.Find<FontIcon>("PlanIndicator1", row);
            Assert.That(AutomationProperties.GetName(indicator), Does.Contain("中止"));
            // The tag names the command; it is not one of the outlined lateness marks.
            Assert.That(Ui.Find<TextBlock>("PlanStartReason").Text, Does.Contain("中止にする（未発行）"));
        });
    }

    [Test]
    public async Task APendingCompletionTagsTheRowAndExplainsTheReadOnlyFields()
    {
        var published = new PlanRow("I1", "Published", "acme/repo") { Fixed = true, Estimate = 8, Remaining = 8 };
        await MountPresentation([published], [published]);
        await Select(1, PlanField.Title);
        await Ui.Run(async () => { await session.Execute(new ClosePlanRows(["I1"]), Today); sheet.Refresh(); });
        await Ui.Until(() => sheet.Realized.Any(r => r.Identity == "I1" && r.StateTagText is not null));
        await Ui.Run(() => {
            var row = sheet.Realized.Single(r => r.Identity == "I1");
            Assert.Multiple(() => {
                Assert.That(row.StateTagText, Is.EqualTo("完了にする"));
                Assert.That(row.Opacity, Is.EqualTo(1), "a completed row keeps its ordinary weight");
                Assert.That(row.StruckThrough, Is.False, "only 中止 is struck through");
            });
            // The GitHub-owned sentence would claim a close that has not been published.
            Assert.That(AutomationProperties.GetHelpText(Ui.Find<TextBox>("PlanCell1_Remaining", row)), Is.EqualTo("完了にする を発行すると確定します（未発行）"));
        });
    }

    [Test]
    public async Task RemovingARowTagsItForThisVersionOnly()
    {
        var published = new PlanRow("I1", "Published", "acme/repo") { Fixed = true };
        await MountPresentation([published], [published]);
        await Select(1, PlanField.Title);
        await Ui.Run(async () => { await session.Execute(new RemovePlanRows(["I1"]), Today); sheet.Refresh(); });
        await Ui.Until(() => sheet.Realized.Any(r => r.Identity == "I1" && r.StateTagText is not null));
        await Ui.Run(() => {
            var row = sheet.Realized.Single(r => r.Identity == "I1");
            Assert.Multiple(() => {
                Assert.That(row.StateTagText, Is.EqualTo("この版から外す"));
                Assert.That(row.Opacity, Is.LessThan(1));
                Assert.That(row.StruckThrough, Is.False, "only 中止 is struck through");
            });
            Assert.That(Ui.Find<MenuFlyoutItem>("PlanRowRemove"), Is.Null);
        });
    }
}
