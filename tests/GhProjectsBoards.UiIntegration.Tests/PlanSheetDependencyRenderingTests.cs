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

    // #264: a link whose predecessor row is scrolled out of the window draws no line across the chart; the
    // successor row shows its `#1204 →` stub instead, so the rows read as rows and not as grid lines.
    [Test]
    public async Task AnOffscreenPredecessorShowsItsStubAndDrawsNoLineAcrossTheChart()
    {
        await SetLongCrossingDependencies();
        await ShowDependencyRow(90);
        await Ui.Until(() => Ui.Tree(sheet).OfType<TextBlock>().Any(element =>
            AutomationProperties.GetAutomationId(element) == "PlanPredStub90"));
        // The settled scroll clears the route the predecessor drew before it left the window.
        await Ui.Until(() => !Ui.Tree(sheet).OfType<Polyline>().Where(Visible)
            .Select(AutomationProperties.GetAutomationId)
            .Any(id => id.StartsWith("PlanArrow1_90_", StringComparison.Ordinal)));
        await Ui.Run(() => {
            var row = CurrentDependencyRow("I90")!;
            var stub = Ui.Find<TextBlock>("PlanPredStub90");
            Assert.That(stub.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(stub.Text, Does.StartWith("新1").And.EndWith("→"));
            Assert.That(AutomationProperties.GetName(stub), Is.EqualTo("先行 新1（表示されていません）"));
            Assert.That(ToolTipService.GetToolTip(stub), Is.EqualTo(AutomationProperties.GetName(stub)));
            // The forward link (1 → 90) draws nothing in this window: the successor shows the stub and no
            // visible route or head for that link. Geometry left in a recycled container by a scroll is not
            // on screen (its animation hides it) and must stay untraceable.
            Assert.That(Ui.Tree(row).OfType<Polyline>().Select(AutomationProperties.GetAutomationId)
                .Where(id => id.StartsWith("PlanArrow1_90_", StringComparison.Ordinal)), Is.Empty);
            Assert.That(Ui.Tree(row).OfType<Polygon>().Select(AutomationProperties.GetAutomationId)
                .Where(id => id.StartsWith("PlanArrowHead1_90_", StringComparison.Ordinal)), Is.Empty);
            var forwardRoutes = Ui.Tree(sheet).OfType<Polyline>().Where(Visible)
                .Select(AutomationProperties.GetAutomationId)
                .Where(id => id.StartsWith("PlanArrow1_90_", StringComparison.Ordinal)).ToArray();
            Assert.That(forwardRoutes, Is.Empty, "An off-screen predecessor must not draw a visible line: " + string.Join(",", forwardRoutes));
        });
        await Ui.Run(() => RenderedEvidence.Capture(sheet, "offscreen-predecessor-stub"));

        // Scrolling back to the predecessor draws its line again; the stub belongs to the successor window only.
        await ShowDependencyRow(1);
        await Ui.Until(() => Ui.Tree(sheet).OfType<Polyline>().Any(line =>
            AutomationProperties.GetAutomationId(line) == "PlanArrow1_90_1"));
        await Ui.Run(() => {
            var row = CurrentDependencyRow("I1")!;
            Assert.That(Ui.Tree(row).OfType<Polyline>().Select(AutomationProperties.GetAutomationId),
                Does.Contain("PlanArrow1_90_1"), "A visible predecessor draws the outgoing line.");
            Assert.That(Ui.Tree(row).OfType<TextBlock>().Any(element =>
                AutomationProperties.GetAutomationId(element).StartsWith("PlanPredStub", StringComparison.Ordinal)), Is.False);
        });
    }

    // A link whose both rows are visible keeps its orthogonal arrow, its crossing through the rows between
    // them and its head inside the successor bar; it survives horizontal scrolling.
    [Test]
    public async Task AVisiblePredecessorKeepsItsArrowCrossingAndHeadAcrossHorizontalScroll()
    {
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [
            new("I15", PlanField.Predecessors, new[] { "I1" }),
            new("I1", PlanField.Title, "Edge start"), new("I15", PlanField.Title, "Edge finish")]), Today);
        await Ui.Run(() => { sheet.Refresh(); Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "日"; });
        await Ui.Idle();
        await Ui.Run(() => sheet.List.ScrollIntoView("I1"));
        await Ui.Until(() => CurrentDependencyRow("I1") is not null && CurrentDependencyRow("I15") is not null);
        // Both endpoints stay in the window: selecting without scrolling keeps rows 1..15 realized.
        await Select(15, PlanField.Title);
        await WaitForDependencyPresentation(1, 15, 15);
        double headX = 0;
        await Ui.Run(() => {
            AssertDependencyEntersBar(1, 15, 15, true);
            headX = Ui.Find<Polygon>("PlanArrowHead1_15_15", CurrentDependencyRow("I15")!)
                .TransformToVisual(sheet).TransformPoint(new(3, 6)).X;
            var between = CurrentDependencyRow("I8");
            Assert.That(between, Is.Not.Null, "The rows between the visible endpoints stay in the window.");
            AssertCrossingGeometry(between!, [headX]);
            // A visible predecessor owns its line: no stub.
            Assert.That(Ui.Tree(CurrentDependencyRow("I15")!).OfType<TextBlock>().Any(element =>
                AutomationProperties.GetAutomationId(element).StartsWith("PlanPredStub", StringComparison.Ordinal)), Is.False);
        });
        await Ui.Run(() => Ui.Find<ScrollViewer>("PlanGanttHorizontal").ChangeView(120, null, null, true));
        await Ui.Until(() => sheet.ChartOffset == 120);
        await SheetNativeInput.Rendered();
        await WaitForDependencyPresentation(1, 15, 15);
        await Ui.Run(() => AssertDependencyEntersBar(1, 15, 15, true));
        await Ui.Run(() => RenderedEvidence.Capture(sheet, "visible-predecessor-arrow"));
    }

    // Whether the rendered slice shows this element: a container the scroller kept but hides must not count
    // as a drawn line.
    private bool Visible(DependencyObject element)
    {
        for (var parent = element; parent is not null && !ReferenceEquals(parent, sheet); parent = VisualTreeHelper.GetParent(parent))
            if (parent is UIElement ui && (ui.Visibility != Visibility.Visible || ui.Opacity == 0)) return false;
        return true;
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
        // One open stroke runs down each crossing and up the next; the runs between crossings lie at -2 and
        // RowHeight + 2, outside the row's clip, so what the row shows is the vertical strokes alone.
        Assert.That(geometry.Figures, Has.Count.EqualTo(1));
        var figure = geometry.Figures[0];
        Assert.That(figure.IsClosed, Is.False);
        Assert.That(figure.IsFilled, Is.False);
        Assert.That(figure.Segments, Has.Count.EqualTo(1));
        Assert.That(figure.Segments[0], Is.TypeOf<PolyLineSegment>());
        var points = new[] { figure.StartPoint }.Concat(((PolyLineSegment)figure.Segments[0]).Points).ToArray();
        Assert.That(points.Length % 2, Is.Zero);
        var actual = new List<double>();
        var localXs = new List<double>();
        for (var i = 0; i < points.Length; i += 2) {
            Assert.That(points[i + 1].X, Is.EqualTo(points[i].X), "Each crossing is a vertical stroke.");
            Assert.That(new[] { points[i].Y, points[i + 1].Y }.Order(), Is.EqualTo(new[] { -2, sheet.RowHeight + 2 }));
            if (i > 0) Assert.That(points[i].Y, Is.EqualTo(points[i - 1].Y), "The run to the next crossing stays outside the row.");
            actual.Add(path.TransformToVisual(sheet).TransformPoint(points[i]).X);
            localXs.Add(points[i].X);
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
    public async Task FoldingAndFilteringRemoveHiddenDependenciesAndReindexVisibleArrowEnds()
    {
        await SetLongCrossingDependencies();
        await session.Execute(new IndentPlanRows(["I90"]), Today);
        await Ui.Run(() => sheet.Refresh());
        await Select(1, PlanField.Title);
        await Ui.Run(() => sheet.SetFold("I89", true));
        await Ui.Ready<TextBox>("PlanCell1_Title");
        await Ui.Run(() => Assert.That(Ui.Tree(sheet).Select(AutomationProperties.GetAutomationId)
            .Any(id => id.StartsWith("PlanArrow", StringComparison.Ordinal) && id.Contains("_90_", StringComparison.Ordinal)), Is.False));

        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "Edge");
        await Ui.Until(() => sheet.List.Items.Count == 5);
        await Ui.Ready<TextBox>("PlanCell95_Title");
        await SheetNativeInput.Rendered();
        await Ui.Until(() => sheet.List.Items.Cast<string>().All(identity => CurrentDependencyRow(identity) is not null));
        // Filtered to five rows every endpoint is on screen, so the links between them are drawn again.
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
            var allPaths = Ui.Tree(sheet).OfType<PlanSheetRow>().SelectMany(row => Ui.Tree(row).OfType<NativePath>()).ToArray();
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
            .Any(id => id.StartsWith("PlanArrow", StringComparison.Ordinal) && id.Contains("_90_", StringComparison.Ordinal)), Is.False));
    }
}
