using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

// #231 task-19: a row whose Issue the viewer cannot update never offers a command that GitHub will reject.
// The permission is a read observation carried in PlanSync, so this is the wiring that turns it into a
// disabled item with its reason.
internal sealed partial class PlanSheetHostedTests
{
    private async Task MarkWithoutPermission(params string[] identities)
        => await Ui.Run(async () => { await session.SaveSync(session.Document.Sync with { CannotUpdate = [.. identities] }); sheet.Refresh(); });

    [Test]
    public async Task ARowWithoutUpdatePermissionDisablesTheStateCommandsWithTheReason()
    {
        var first = new PlanRow("I1", "Published", "acme/repo") { Fixed = true };
        var second = new PlanRow("I2", "Other", "acme/repo") { Fixed = true };
        await MountPresentation([first, second], [first, second]);
        await MarkWithoutPermission("I1");
        await Select(1, PlanField.Title);
        await Ui.Run(() => sheet.ShowRowMenu("I1", PlanField.Title, sheet.Realized.Single(r => r.Identity == "I1")));
        await Ui.Ready<MenuFlyoutItem>("PlanRowComplete");
        await Ui.Run(() => {
            foreach (var id in new[] { "PlanRowComplete", "PlanRowCancel", "PlanRowRemove" }) {
                Assert.That(Ui.Find<MenuFlyoutItem>(id).IsEnabled, Is.False, id);
                Assert.That(AutomationProperties.GetHelpText(Ui.Find<MenuFlyoutItem>(id)), Is.EqualTo(PlanSheetView.NoPermissionReason), id);
            }
            // Deleting a never-published row needs no Issue permission, so that item is unaffected.
            Assert.That(Ui.Find<MenuFlyoutItem>("PlanRowDelete").IsEnabled, Is.False, "this row is published, so it cannot be deleted either");
            Assert.That(AutomationProperties.GetHelpText(Ui.Find<MenuFlyoutItem>("PlanRowDelete")), Does.Contain("完了・中止・この版から外す"));
        });
    }

    [Test]
    public async Task AMixedPermissionSelectionStaysEnabledAndNamesTheSkippedRow()
    {
        var first = new PlanRow("I1", "Published", "acme/repo") { Fixed = true };
        var second = new PlanRow("I2", "Other", "acme/repo") { Fixed = true };
        await MountPresentation([first, second], [first, second]);
        await MarkWithoutPermission("I1");
        await Ui.Run(() => { sheet.Select("I1", PlanField.Title, false); sheet.Select("I2", PlanField.Title, true); });
        await Ui.Run(() => sheet.ShowRowMenu("I2", PlanField.Title, sheet.Realized.Single(r => r.Identity == "I2")));
        await Ui.Ready<MenuFlyoutItem>("PlanRowComplete");
        await Ui.Run(() => {
            Assert.That(Ui.Find<MenuFlyoutItem>("PlanRowComplete").IsEnabled, Is.True, "I2 may be completed");
            Assert.That(AutomationProperties.GetHelpText(Ui.Find<MenuFlyoutItem>("PlanRowComplete")),
                Is.EqualTo("1行を除外します（" + PlanSheetView.NoPermissionReason + "）"));
        });
        // Running it completes only the permitted row and says which one was left alone.
        await Ui.Run(() => Ui.Click("PlanRowComplete"));
        await Ui.Until(() => session.Document.State.Rows.Single(r => r.Identity == "I2").Closed);
        await Ui.Run(() => {
            Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I1").Closed, Is.False, "the denied row is untouched");
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.EqualTo("完了にしました。1行を除外します（" + PlanSheetView.NoPermissionReason + "）"));
        });
    }
}
