using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

// #233: a notice must not move the rows under the pointer, and leaving the publish review must give the keyboard
// back to the cell the PMO was working in.
internal sealed partial class PlanWorkspaceHostedTests
{
    [Test, Category("PlanNotices")]
    public async Task OpeningANoticeLeavesTheSheetRowsWhereTheyAre()
    {
        await Open();
        double before = 0;
        await Ui.Run(() => {
            Assert.That(Ui.Find<InfoBar>("PlanRefreshResult").IsOpen, Is.False);
            before = Ui.Find<FrameworkElement>("PlanCell1_Title").TransformToVisual(view).TransformPoint(new()).Y;
            Ui.Click("PlanRefresh");
        });
        await Ui.Until(() => Ui.Find<InfoBar>("PlanRefreshResult").IsOpen && Ui.Find<SplitButton>("PlanRefresh").IsEnabled);
        await Ui.Idle();
        await Ui.Run(() => {
            var cell = Ui.Find<FrameworkElement>("PlanCell1_Title");
            var bar = Ui.Find<InfoBar>("PlanRefreshResult");
            Assert.That(cell.TransformToVisual(view).TransformPoint(new()).Y, Is.EqualTo(before).Within(0.5), "The notice must not push the rows down.");
            Assert.That(bar.ActualHeight, Is.GreaterThan(0), "The notice is rendered.");
            Assert.That(bar.TransformToVisual(view).TransformPoint(new()).Y, Is.GreaterThan(Ui.Find<ListView>("PlanTasks").TransformToVisual(view).TransformPoint(new()).Y),
                "The notice sits below the rows, not above them.");
            bar.IsOpen = false;
        });
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<FrameworkElement>("PlanCell1_Title").TransformToVisual(view).TransformPoint(new()).Y, Is.EqualTo(before).Within(0.5),
            "Closing the notice must not move the rows either."));
    }

    [TestCase("close"), TestCase("tab"), Category("PlanPublishFocus")]
    public async Task LeavingThePublishReviewReturnsKeyboardFocusToTheSelectedCell(string route)
    {
        await Open();
        await Ui.Run(async () => await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Remaining, 5m)]), readDay));
        await Ui.Run(() => Ui.Tree(view).OfType<PlanSheetView>().Single().Refresh()); await Ui.Idle();
        await SheetNativeInput.ActivateWindow();
        await Ui.Run(() => { var sheet = Ui.Tree(view).OfType<PlanSheetView>().Single(); sheet.Select("I1", PlanField.Remaining, false); sheet.FocusSelected(); });
        await Ui.Until(() => FocusManager.GetFocusedElement(view.XamlRoot) == Ui.Find<Control>("PlanCell1_Remaining"));
        // The PMO clicks 発行…, which takes focus, and the review replaces the sheet.
        await Ui.Run(() => { Ui.Find<Button>("PlanPublish").Focus(FocusState.Pointer); Ui.Click("PlanPublish"); });
        await Ui.Until(() => Ui.Find<Button>("PlanPublishClose") is { IsLoaded: true, ActualWidth: > 0 });
        await Ui.Run(() => {
            if (route == "close") { Ui.Find<Button>("PlanPublishClose").Focus(FocusState.Pointer); Ui.Click("PlanPublishClose"); }
            else { Ui.Find<SelectorBarItem>("PlanShowTasks").Focus(FocusState.Pointer); Ui.Find<SelectorBarItem>("PlanShowTasks").IsSelected = true; }
        });
        await Ui.Until(() => FocusManager.GetFocusedElement(view.XamlRoot) == Ui.Find<Control>("PlanCell1_Remaining"),
            () => "focus=" + (FocusManager.GetFocusedElement(view.XamlRoot) as FrameworkElement)?.Name + "/" + FocusManager.GetFocusedElement(view.XamlRoot)?.GetType().Name);
        // Ctrl+Z works at once, without a click back into the sheet.
        await SheetNativeInput.Press(VirtualKey.Z, VirtualKey.Control);
        await Ui.Until(() => workspace.Session!.Document.State.Rows[0].Remaining == 8m);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }
}
