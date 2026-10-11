using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using NUnit.Framework;
using Windows.Foundation;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanSheetHostedTests
{
    private async Task SetLongCrossingDependencies()
    {
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [
            new("I90", PlanField.Predecessors, new[] { "I1" }),
            new("I3", PlanField.Predecessors, new[] { "I95" }),
            new("I1", PlanField.Title, "Edge start"), new("I90", PlanField.Title, "Edge finish"),
            new("I95", PlanField.Title, "Edge reverse start"), new("I3", PlanField.Title, "Edge reverse finish")]), Today);
        await Ui.Run(() => { sheet.Refresh(); Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "日"; });
        await Ui.Idle();
    }
    private async Task ShowDependencyRow(int number)
    {
        await Ui.Run(() => sheet.List.ScrollIntoView("I" + number));
        await Ui.Ready<TextBox>($"PlanCell{number}_Title");
        await Select(number, PlanField.Title);
    }

    [Test]
    public async Task LongCrossingDependenciesKeepTheirCurrentRowIdentityThroughBothScrollDirections()
    {
        await SetLongCrossingDependencies();
        foreach (var number in new[] { 30, 65, 90, 3, 55, 1 }) {
            await ShowDependencyRow(number);
            await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                foreach (var row in sheet.Realized.Where(row => row.IsLoaded && row.Identity.Length > 0)) {
                    var current = int.Parse(row.Identity[1..]);
                    var expected = new List<string>();
                    if (current <= 2) expected.Add($"PlanArrow1_2_{current}");
                    if (current <= 90) expected.Add($"PlanArrow1_90_{current}");
                    if (current is >= 3 and <= 95) expected.Add($"PlanArrow95_3_{current}");
                    var arrows = Ui.Tree(row).OfType<Polyline>().Select(AutomationProperties.GetAutomationId).ToArray();
                    Assert.That(arrows, Is.EquivalentTo(expected), $"Dependency identity on row {current}");
                    var heads = Ui.Tree(row).OfType<Polygon>().Select(AutomationProperties.GetAutomationId)
                        .Where(id => id.StartsWith("PlanArrowHead", StringComparison.Ordinal)).ToArray();
                    Assert.That(heads, Is.EquivalentTo(current switch {
                        2 => new[] { "PlanArrowHead1_2_2" }, 3 => ["PlanArrowHead95_3_3"],
                        90 => ["PlanArrowHead1_90_90"], _ => [] }));
                }
            });
        }
    }

    private void AssertDependencyEntersBar(int from, int to, int number, bool down)
    {
        var bar = Ui.Find<Shape>("PlanBar" + number);
        var head = Ui.Find<Polygon>($"PlanArrowHead{from}_{to}_{number}");
        var route = Ui.Find<Polyline>($"PlanArrow{from}_{to}_{number}");
        var tip = head.TransformToVisual(sheet).TransformPoint(new(3, down ? 6 : 0));
        var barBounds = bar.TransformToVisual(sheet).TransformBounds(new(0, 0, bar.ActualWidth, bar.ActualHeight));
        Assert.That(tip.X, Is.InRange(barBounds.Left, barBounds.Right));
        Assert.That(tip.Y, Is.EqualTo(down ? barBounds.Top : barBounds.Bottom).Within(1));
        var end = route.TransformToVisual(sheet).TransformPoint(route.Points[^1]);
        var headBase = head.TransformToVisual(sheet).TransformPoint(new(3, down ? 0 : 6));
        Assert.That(end.X, Is.EqualTo(headBase.X).Within(1));
        Assert.That(end.Y, Is.EqualTo(headBase.Y).Within(1));
    }

    [Test]
    public async Task HorizontalScrollThenPredecessorEditAndUndoKeepTheDistantArrowAttached()
    {
        await SetLongCrossingDependencies();
        await ShowDependencyRow(90);
        await Ui.Run(() => Ui.Find<ScrollViewer>("PlanGanttHorizontal").ChangeView(120, null, null, true));
        await Ui.Until(() => sheet.ChartOffset == 120);
        await SheetNativeInput.Rendered();
        double originalStart = 0;
        await Ui.Run(() => {
            AssertDependencyEntersBar(1, 90, 90, true);
            originalStart = Ui.Find<Shape>("PlanBar90").TransformToVisual(sheet).TransformPoint(new()).X;
        });
        await ShowDependencyRow(1);
        await Edit(1, PlanField.Remaining, "16");
        await ShowDependencyRow(90);
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            AssertDependencyEntersBar(1, 90, 90, true);
            Assert.That(Ui.Find<Shape>("PlanBar90").TransformToVisual(sheet).TransformPoint(new()).X,
                Is.EqualTo(originalStart + 24).Within(1));
        });
        await Ui.Run(() => sheet.KeyboardCommand(VirtualKey.Z));
        await Ui.Idle();
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            AssertDependencyEntersBar(1, 90, 90, true);
            Assert.That(Ui.Find<Shape>("PlanBar90").TransformToVisual(sheet).TransformPoint(new()).X,
                Is.EqualTo(originalStart).Within(1));
        });
    }

    [Test]
    public async Task FoldingAndFilteringRemoveHiddenDependenciesAndReindexVisibleArrowEnds()
    {
        await SetLongCrossingDependencies();
        await session.Execute(new IndentPlanRows(["I90"]), Today);
        await Ui.Run(() => sheet.Refresh());
        await Select(1, PlanField.Title);
        await Ui.Run(() => sheet.SetFold("I89", true));
        await Ui.Ready<TextBox>("PlanCell1_Title");
        await Ui.Run(() => Assert.That(Ui.Tree(sheet).Select(AutomationProperties.GetAutomationId)
            .Any(id => id.StartsWith("PlanArrow1_90_", StringComparison.Ordinal)), Is.False));

        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "Edge");
        await Ui.Until(() => sheet.List.Items.Count == 5);
        await Ui.Ready<TextBox>("PlanCell95_Title");
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(Ui.Tree(sheet).OfType<Polyline>().Select(AutomationProperties.GetAutomationId), Is.EquivalentTo(new[] {
                "PlanArrow1_3_1", "PlanArrow1_3_3", "PlanArrow1_3_90",
                "PlanArrow4_2_3", "PlanArrow4_2_90", "PlanArrow4_2_95" }));
            AssertDependencyEntersBar(1, 3, 90, true);
            AssertDependencyEntersBar(4, 2, 3, false);
        });
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "");
        await Ui.Until(() => sheet.List.Items.Count == 100);
        await Ui.Ready<TextBox>("PlanCell1_Title");
        await Ui.Run(() => Assert.That(Ui.Tree(sheet).Select(AutomationProperties.GetAutomationId)
            .Any(id => id.StartsWith("PlanArrow1_90_", StringComparison.Ordinal)), Is.False));
    }
}
