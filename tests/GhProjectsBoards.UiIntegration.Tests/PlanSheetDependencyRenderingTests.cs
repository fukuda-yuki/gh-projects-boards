using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NUnit.Framework;
using Windows.Foundation;
using Windows.System;
using NativePath = Microsoft.UI.Xaml.Shapes.Path;

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
        await Ui.Until(() => CurrentDependencyRow("I" + number) is not null);
        await Ui.Ready<TextBox>($"PlanCell{number}_Title");
        await Select(number, PlanField.Title);
    }
    private PlanSheetRow? CurrentDependencyRow(string identity)
    {
        var row = (sheet.List.ContainerFromItem(identity) as ListViewItem)?.ContentTemplateRoot as PlanSheetRow;
        return row is { IsLoaded: true } && row.Identity == identity && row.Owner == sheet ? row : null;
    }
    private async Task WaitForDependencyPresentation(int from, int to, int number)
    {
        var routeId = $"PlanArrow{from}_{to}_{number}";
        try {
            await Ui.Until(() => {
                if (CurrentDependencyRow("I" + number) is not { } row) return false;
                var shapes = Ui.Tree(row).OfType<Shape>().Where(shape => shape.IsLoaded).ToArray();
                var bar = shapes.SingleOrDefault(shape => AutomationProperties.GetAutomationId(shape) == "PlanBar" + number);
                var route = shapes.OfType<Polyline>().SingleOrDefault(shape => AutomationProperties.GetAutomationId(shape) == routeId);
                var head = shapes.OfType<Polygon>().SingleOrDefault(shape => AutomationProperties.GetAutomationId(shape) == $"PlanArrowHead{from}_{to}_{number}");
                // A passive line's layout width is not its point geometry. Wait for
                // the bound line payload; the assertions below verify its screen coordinates.
                return bar is { ActualWidth: > 0, ActualHeight: > 0 } && route is { Points.Count: >= 2 }
                    && head is { ActualWidth: > 0, ActualHeight: > 0 };
            });
        } catch (TimeoutException) {
            await Ui.Run(() => WriteDependencyPresentationDiagnostic(number, "timeout " + routeId));
            throw;
        }
        await Ui.Run(() => {
            var route = Ui.Find<Polyline>(routeId, CurrentDependencyRow("I" + number)!);
            if (route.ActualWidth <= 0) WriteDependencyPresentationDiagnostic(number, "ready with zero layout-width " + routeId);
        });
    }

    private void WriteDependencyPresentationDiagnostic(int number, string reason)
    {
        var container = sheet.List.ContainerFromItem("I" + number) as ListViewItem;
        var row = container?.ContentTemplateRoot as PlanSheetRow;
        TestContext.Out.WriteLine($"Dependency presentation ({reason}): item=I{number}; container={container is not null}; "
            + $"context={row?.DataContext ?? "<null>"}; loaded={row?.IsLoaded}; owner={row?.Owner == sheet}; realized={row is not null && sheet.Realized.Contains(row)}; "
            + $"row-size={row?.ActualWidth}x{row?.ActualHeight}; chart-offset={sheet.ChartOffset}; chart-viewport={sheet.ChartViewport}");
        if (row is null) return;
        foreach (var element in Ui.Tree(row).OfType<FrameworkElement>().Where(element => element is Canvas or Shape)) {
            var points = element switch {
                Polyline line => string.Join(";", line.Points.Select(point => $"{point.X},{point.Y}")),
                Polygon polygon => string.Join(";", polygon.Points.Select(point => $"{point.X},{point.Y}")),
                _ => ""
            };
            TestContext.Out.WriteLine($"  {element.GetType().Name}/{AutomationProperties.GetAutomationId(element)}: loaded={element.IsLoaded}; "
                + $"actual={element.ActualWidth}x{element.ActualHeight}; requested={element.Width}x{element.Height}; "
                + $"visibility={element.Visibility}; opacity={element.Opacity}; canvas={Canvas.GetLeft(element)},{Canvas.GetTop(element)}; points=[{points}]");
        }
    }

    [Test]
    public async Task LongCrossingDependenciesKeepTheirCurrentRowIdentityThroughBothScrollDirections()
    {
        await SetLongCrossingDependencies();
        var forwardX = await DependencyEntryX(1, 90, 90, true);
        var reverseX = await DependencyEntryX(95, 3, 3, false);
        foreach (var number in new[] { 30, 65, 90, 3, 55, 1 }) {
            await ShowDependencyRow(number);
            await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                foreach (var row in sheet.Realized.Where(row => row.IsLoaded && row.Identity.Length > 0)) {
                    var current = int.Parse(row.Identity[1..]);
                    var expected = new List<string>();
                    if (current is 1 or 2) expected.Add($"PlanArrow1_2_{current}");
                    if (current is 1 or 90) expected.Add($"PlanArrow1_90_{current}");
                    if (current is 3 or 95) expected.Add($"PlanArrow95_3_{current}");
                    var arrows = Ui.Tree(row).OfType<Polyline>().Select(AutomationProperties.GetAutomationId).ToArray();
                    Assert.That(arrows, Is.EquivalentTo(expected), $"Dependency identity on row {current}");
                    var crossings = new List<double>();
                    if (current is > 1 and < 90) crossings.Add(forwardX);
                    if (current is > 3 and < 95) crossings.Add(reverseX);
                    AssertCrossingGeometry(row, crossings);
                    var heads = Ui.Tree(row).OfType<Polygon>().Select(AutomationProperties.GetAutomationId)
                        .Where(id => id.StartsWith("PlanArrowHead", StringComparison.Ordinal)).ToArray();
                    Assert.That(heads, Is.EquivalentTo(current switch {
                        2 => new[] { "PlanArrowHead1_2_2" }, 3 => ["PlanArrowHead95_3_3"],
                        90 => ["PlanArrowHead1_90_90"], _ => [] }));
                }
            });
            if (number == 55) await Ui.Run(() => RenderedEvidence.Capture(sheet, "dependency-interior-crossings"));
        }
    }

    private async Task<double> DependencyEntryX(int from, int to, int number, bool down)
    {
        await ShowDependencyRow(number);
        await WaitForDependencyPresentation(from, to, number);
        double result = 0;
        await Ui.Run(() => result = Ui.Find<Polygon>($"PlanArrowHead{from}_{to}_{number}", CurrentDependencyRow("I" + number)!)
            .TransformToVisual(sheet).TransformPoint(new(3, down ? 6 : 0)).X);
        return result;
    }

    private void AssertCrossingGeometry(PlanSheetRow row, IEnumerable<double> expectedScreenXs)
    {
        var chartLeft = row.TransformToVisual(sheet).TransformPoint(new(sheet.SheetViewport, 0)).X;
        var expected = expectedScreenXs.Distinct().Where(x => x >= chartLeft - 1 && x <= chartLeft + sheet.ChartViewport + 1)
            .Order().ToArray();
        var paths = Ui.Tree(row).OfType<NativePath>().ToArray();
        Assert.That(paths.Length, Is.LessThanOrEqualTo(1), "Each row has one bounded crossing surface.");
        if (expected.Length == 0) {
            Assert.That(paths.All(path => path.Visibility == Visibility.Collapsed && path.Data is null), Is.True,
                "No offscreen or obsolete crossing geometry remains active.");
            return;
        }
        Assert.That(paths, Has.Length.EqualTo(1));
        var path = paths[0];
        Assert.That(path.Visibility, Is.EqualTo(Visibility.Visible));
        Assert.That(AutomationProperties.GetAutomationId(path), Is.EqualTo("PlanArrowCrossings" + sheet.PlanIds[row.Identity]));
        Assert.That(path.StrokeThickness, Is.EqualTo(1));
        Assert.That(path.Data, Is.TypeOf<PathGeometry>());
        var geometry = (PathGeometry)path.Data;
        var actual = new List<double>();
        var localXs = new List<double>();
        foreach (var figure in geometry.Figures) {
            Assert.That(figure.IsClosed, Is.False);
            Assert.That(figure.IsFilled, Is.False);
            Assert.That(figure.Segments, Has.Count.EqualTo(1));
            Assert.That(figure.Segments[0], Is.TypeOf<LineSegment>());
            var end = ((LineSegment)figure.Segments[0]).Point;
            Assert.That(end.X, Is.EqualTo(figure.StartPoint.X));
            Assert.That(figure.StartPoint.Y, Is.EqualTo(-2));
            Assert.That(end.Y, Is.EqualTo(sheet.RowHeight + 2));
            actual.Add(path.TransformToVisual(sheet).TransformPoint(figure.StartPoint).X);
            localXs.Add(figure.StartPoint.X);
        }
        Assert.That(actual.Order().ToArray(), Is.EqualTo(expected).Within(0.01), "Crossings align with their endpoint tips.");
        Assert.That(localXs.Min(), Is.Zero.Within(0.01));
        Assert.That(localXs.Max() - localXs.Min(), Is.LessThanOrEqualTo(sheet.ChartViewport + 2));
    }

    private void AssertDependencyEntersBar(int from, int to, int number, bool down)
    {
        var row = CurrentDependencyRow("I" + number);
        Assert.That(row, Is.Not.Null, "Geometry must belong to the currently bound task container.");
        var bar = Ui.Find<Shape>("PlanBar" + number, row!);
        var head = Ui.Find<Polygon>($"PlanArrowHead{from}_{to}_{number}", row!);
        var route = Ui.Find<Polyline>($"PlanArrow{from}_{to}_{number}", row!);
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
        await WaitForDependencyPresentation(1, 90, 90);
        double originalStart = 0;
        await Ui.Run(() => {
            AssertDependencyEntersBar(1, 90, 90, true);
            originalStart = Ui.Find<Shape>("PlanBar90", CurrentDependencyRow("I90")!).TransformToVisual(sheet).TransformPoint(new()).X;
        });
        var forwardX = await DependencyEntryX(1, 90, 90, true);
        var reverseX = await DependencyEntryX(95, 3, 3, false);
        await ShowDependencyRow(55);
        await Ui.Run(() => AssertCrossingGeometry(CurrentDependencyRow("I55")!, [forwardX, reverseX]));
        await Ui.Run(() => Ui.Find<ScrollViewer>("PlanGanttHorizontal").ChangeView(0, null, null, true));
        await Ui.Until(() => sheet.ChartOffset == 0);
        await SheetNativeInput.Rendered();
        await Ui.Run(() => AssertCrossingGeometry(CurrentDependencyRow("I55")!, [forwardX + 120, reverseX + 120]));
        await Ui.Run(() => RenderedEvidence.Capture(sheet, "dependency-crossings-horizontal-origin"));
        await Ui.Run(() => Ui.Find<ScrollViewer>("PlanGanttHorizontal").ChangeView(120, null, null, true));
        await Ui.Until(() => sheet.ChartOffset == 120);
        await ShowDependencyRow(1);
        await Edit(1, PlanField.Remaining, "16");
        await ShowDependencyRow(90);
        await SheetNativeInput.Rendered();
        await WaitForDependencyPresentation(1, 90, 90);
        await Ui.Run(() => {
            AssertDependencyEntersBar(1, 90, 90, true);
            Assert.That(Ui.Find<Shape>("PlanBar90", CurrentDependencyRow("I90")!).TransformToVisual(sheet).TransformPoint(new()).X,
                Is.EqualTo(originalStart + 24).Within(1));
        });
        await ShowDependencyRow(55);
        await Ui.Run(() => AssertCrossingGeometry(CurrentDependencyRow("I55")!, [forwardX + 24, reverseX]));
        await Ui.Run(() => sheet.KeyboardCommand(VirtualKey.Z));
        await Ui.Idle();
        await Ui.Run(() => AssertCrossingGeometry(CurrentDependencyRow("I55")!, [forwardX, reverseX]));
        await ShowDependencyRow(90);
        await SheetNativeInput.Rendered();
        await WaitForDependencyPresentation(1, 90, 90);
        await Ui.Run(() => {
            AssertDependencyEntersBar(1, 90, 90, true);
            Assert.That(Ui.Find<Shape>("PlanBar90", CurrentDependencyRow("I90")!).TransformToVisual(sheet).TransformPoint(new()).X,
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
        await Ui.Until(() => sheet.List.Items.Cast<string>().All(identity => CurrentDependencyRow(identity) is not null));
        await WaitForDependencyPresentation(1, 3, 90);
        await WaitForDependencyPresentation(4, 2, 3);
        await Ui.Run(() => {
            string[] expectedArrows = [
                "PlanArrow1_3_1", "PlanArrow1_3_90", "PlanArrow4_2_3", "PlanArrow4_2_95" ];
            var currentRows = sheet.List.Items.Cast<string>().Select(identity => {
                var row = (sheet.List.ContainerFromItem(identity) as ListViewItem)?.ContentTemplateRoot as PlanSheetRow;
                Assert.That(row, Is.Not.Null, $"Current filtered row {identity}");
                Assert.That(row!.IsLoaded, Is.True);
                Assert.That(row.Owner, Is.SameAs(sheet));
                Assert.That(row.Identity, Is.EqualTo(identity));
                return row;
            }).ToArray();
            Assert.That(Ui.Find<TextBox>("PlanCell0_Title", CurrentDependencyRow("")!).Text, Is.Empty,
                "The current empty append item exposes its native editor after filtering.");
            Assert.That(currentRows.SelectMany(row => Ui.Tree(row).OfType<Polyline>()).Select(AutomationProperties.GetAutomationId),
                Is.EquivalentTo(expectedArrows), "Every active dependency belongs to the current filtered topology.");
            var forwardX = Ui.Find<Polygon>("PlanArrowHead1_3_90", CurrentDependencyRow("I90")!)
                .TransformToVisual(sheet).TransformPoint(new(3, 6)).X;
            var reverseX = Ui.Find<Polygon>("PlanArrowHead4_2_3", CurrentDependencyRow("I3")!)
                .TransformToVisual(sheet).TransformPoint(new(3, 0)).X;
            foreach (var row in currentRows)
                AssertCrossingGeometry(row, row.Identity switch { "I3" => [forwardX], "I90" => [reverseX], _ => [] });
            var allPaths = Ui.Tree(sheet).OfType<NativePath>().ToArray();
            Assert.That(allPaths.Select(AutomationProperties.GetAutomationId).Where(id => id.Length > 0),
                Is.EquivalentTo(currentRows.SelectMany(row => Ui.Tree(row).OfType<NativePath>())
                    .Select(AutomationProperties.GetAutomationId).Where(id => id.Length > 0)),
                "Recycled crossing paths must not expose old row identities.");
            var allArrows = Ui.Tree(sheet).OfType<Polyline>().ToArray();
            Assert.That(allArrows.Select(AutomationProperties.GetAutomationId).Where(id => id.Length > 0),
                Is.EquivalentTo(expectedArrows), "Recycled rows must not expose old dependency identities.");
            Assert.That(Ui.Tree(sheet).OfType<Polygon>().Select(AutomationProperties.GetAutomationId)
                .Where(id => id.StartsWith("PlanArrowHead", StringComparison.Ordinal)),
                Is.EquivalentTo(new[] { "PlanArrowHead1_3_90", "PlanArrowHead4_2_3" }));
            var retainedCounts = new Dictionary<PlanSheetRow, int>();
            foreach (var line in allArrows.Where(line => AutomationProperties.GetAutomationId(line).Length == 0)) {
                PlanSheetRow? retainedRow = null;
                var hidden = false;
                for (DependencyObject? parent = line; parent is not null && !ReferenceEquals(parent, sheet); parent = VisualTreeHelper.GetParent(parent)) {
                    if (parent is UIElement element && (element.Visibility == Visibility.Collapsed || element.Opacity == 0)) hidden = true;
                    if (parent is PlanSheetRow row) retainedRow = row;
                }
                Assert.That(retainedRow, Is.Not.Null);
                Assert.That(retainedRow!.Owner, Is.Null, "Only recycled rows may retain unidentified dependency geometry.");
                Assert.That(hidden, Is.True, "Retained geometry must be hidden, not merely missing an automation ID.");
                retainedCounts[retainedRow] = retainedCounts.GetValueOrDefault(retainedRow) + 1;
            }
            // At most two of this fixture's dependencies cross any one task row.
            Assert.That(retainedCounts.Values, Is.All.LessThanOrEqualTo(2), "Recycled rows retain one bounded row slice.");
            foreach (var path in allPaths.Where(path => AutomationProperties.GetAutomationId(path).Length == 0)) {
                var hidden = false;
                for (DependencyObject? parent = path; parent is not null && !ReferenceEquals(parent, sheet); parent = VisualTreeHelper.GetParent(parent))
                    if (parent is UIElement element && (element.Visibility == Visibility.Collapsed || element.Opacity == 0)) hidden = true;
                Assert.That(hidden, Is.True, "Unidentified retained crossing paths must not paint old geometry.");
            }
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
