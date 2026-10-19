using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

// #231 slice B: the row context menu, the pending-state presentation and the review wording. The commands
// themselves are covered by the Core and adapter suites; this file covers the surface the PMO operates.
internal sealed partial class PlanSheetHostedTests
{
    // The commands are chosen from the overflow, the route the other hosted cases already drive, so these
    // cases measure the mark and the status line; the row context menu's own reachability and reasons are
    // the subject of the menu case.
    private async Task ChooseRowCommand(string itemId)
    {
        await Ui.Run(() => Ui.Click("PlanSheetMore"));
        await InvokeRowItem(itemId);
    }
    // The passing permission case opens the row menu and settles the UI before it invokes an overflow item.
    // The mark cases do the same, which also states the command's state on the pointer route (#231).
    private async Task AssertRowMenuCommand(string identity, string itemId)
    {
        await Ui.Run(() => sheet.ShowRowMenu(identity, PlanField.Title, sheet.Realized.Single(r => r.Identity == identity)));
        await Ui.Ready<MenuFlyoutItem>(itemId);
        await Ui.Run(() => Assert.That(Ui.Find<MenuFlyoutItem>(itemId).IsEnabled, Is.True, itemId));
    }
    private static async Task InvokeRowItem(string itemId)
    {
        await Ui.Ready<MenuFlyoutItem>(itemId);
        await Ui.Run(() => ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(
            Ui.Popup<MenuFlyoutItem>(itemId)!).GetPattern(PatternInterface.Invoke)).Invoke());
    }
    // Wait for the command's own report first: that separates "the command did not run" from "the mark did
    // not appear", which a bare wait on the mark cannot. The assertion then names the layer that failed.
    private async Task<(PlanSheetRow Row, string? Tag)> RowMark(string identity, string expected)
    {
        await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Length > 0);
        PlanSheetRow? row = null; string? tag = null; var realized = 0; var bound = 0; string? model = null;
        var sameIdentity = 0; var tags = ""; var wiring = "";
        await Ui.Run(() => {
            realized = sheet.Realized.Count;
            var matches = sheet.Realized.Where(r => r.Identity == identity).ToArray();
            sameIdentity = matches.Length;
            // A recycled row can keep its identity after it left the tree; the tags of every instance say
            // whether the case read a stale one or the live row really is unmarked.
            tags = string.Join(",", matches.Select(r => (r.StateTagText ?? "null") + (r.IsBound ? "" : "(unbound)")));
            wiring = string.Join(" | ", matches.Select(r => r.TagDiagnostics));
            row = matches.FirstOrDefault();
            tag = row?.StateTagText;
            bound = sheet.Realized.Count(r => r.IsBound);
            model = sheet.RowStateTag(identity);
        });
        var status = "";
        await Ui.Run(() => status = Ui.Find<TextBlock>("PlanSheetError").Text);
        Assert.That(tag, Is.EqualTo(expected),
            $"status='{status}' model='{model}' realized={realized} bound={bound} sameIdentity={sameIdentity} tags=[{tags}] wiring=[{wiring}] rowFound={row is not null} rowBound={row?.IsBound}");
        return (row!, tag);
    }

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
        // The overflow carries the same four commands with the same reasons, so the keyboard route does not
        // depend on the row menu being reachable by pointer (#231).
        await Ui.Run(() => Ui.Click("PlanSheetMore"));
        await Ui.Ready<MenuFlyoutItem>("PlanSheetCompleteRows");
        await Ui.Run(() => {
            foreach (var id in new[] { "PlanSheetCompleteRows", "PlanSheetCancelRows", "PlanSheetRemoveRows", "PlanSheetReopenRows" })
                Assert.That(Ui.Find<MenuFlyoutItem>(id), Is.Not.Null, id);
            Assert.That(Ui.Find<MenuFlyoutItem>("PlanSheetCompleteRows").IsEnabled, Is.True);
            Assert.That(Ui.Find<MenuFlyoutItem>("PlanSheetReopenRows").IsEnabled, Is.False);
            Assert.That(AutomationProperties.GetHelpText(Ui.Find<MenuFlyoutItem>("PlanSheetReopenRows")), Is.EqualTo("未完了の行です"));
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
        await AssertRowMenuCommand("I1", "PlanRowCancel");
        await ChooseRowCommand("PlanSheetCancelRows");
        var (row, tag) = await RowMark("I1", "中止にする");
        // Both marks are built with the row, so a failure in one must not hide the other.
        Assert.Multiple(() => {
            Assert.That(tag, Is.EqualTo("中止にする"));
            Assert.That(row.StruckThrough, Is.True, "中止 is struck through");
            Assert.That(row.Opacity, Is.LessThan(1), "中止 is dimmed");
        });
        await Ui.Run(() => {
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
        await AssertRowMenuCommand("I1", "PlanRowComplete");
        await ChooseRowCommand("PlanSheetCompleteRows");
        var (row, tag) = await RowMark("I1", "完了にする");
        Assert.Multiple(() => {
            Assert.That(tag, Is.EqualTo("完了にする"));
            Assert.That(row.Opacity, Is.EqualTo(1), "a completed row keeps its ordinary weight");
            Assert.That(row.StruckThrough, Is.False, "only 中止 is struck through");
        });
        // The GitHub-owned sentence would claim a close that has not been published.
        await Ui.Run(() => Assert.That(AutomationProperties.GetHelpText(Ui.Find<TextBox>("PlanCell1_Remaining", row)),
            Is.EqualTo("完了にする を発行すると確定します（未発行）")));
    }

    [Test]
    public async Task RemovingARowTagsItForThisVersionOnly()
    {
        var published = new PlanRow("I1", "Published", "acme/repo") { Fixed = true };
        await MountPresentation([published], [published]);
        await Select(1, PlanField.Title);
        await AssertRowMenuCommand("I1", "PlanRowRemove");
        await ChooseRowCommand("PlanSheetRemoveRows");
        var (row, tag) = await RowMark("I1", "この版から外す");
        Assert.Multiple(() => {
            Assert.That(tag, Is.EqualTo("この版から外す"));
            Assert.That(row.Opacity, Is.LessThan(1));
            Assert.That(row.StruckThrough, Is.False, "only 中止 is struck through");
        });
        // この版から外す hides the row from the version; it does not delete the Issue or the row before publish.
        await Ui.Until(() => session.Document.State.Rows.Single(r => r.Identity == "I1").Removed);
    }
}
