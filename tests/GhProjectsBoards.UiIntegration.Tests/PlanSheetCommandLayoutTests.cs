using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
using Windows.Foundation;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanSheetHostedTests
{
    [TestCase(640), TestCase(900), TestCase(1280), TestCase(1920), Category("CommandLayout")]
    public async Task GanttToggleAndVisibleCommandsHaveSeparatePointerTargets(double width)
    {
        await Ui.Run(() => {
            Ui.Window.AppWindow.Resize(new((int)(2000 * Ui.Root.XamlRoot.RasterizationScale), 1000));
            sheetHost.Width = width;
            sheetHost.HorizontalAlignment = HorizontalAlignment.Left;
        });
        await Ui.Until(() => Math.Abs(sheet.ActualWidth - width) < 1);
        await Ui.Idle();
        var rows = sheet.RowIds.ToArray();
        var undo = session.UndoCount;
        foreach (var shown in new[] { true, false, true })
        {
            await Ui.Run(() => {
                var toggle = Ui.Find<CheckBox>("PlanSheetShowGantt");
                if (toggle.IsChecked != shown) Ui.Toggle(toggle);
            });
            await Ui.Until(() => sheet.GanttVisible == shown);
            await Ui.Run(() => sheet.UpdateLayout());
            await Ui.Idle();
            await Ui.Run(() => {
                var toggle = Ui.Find<CheckBox>("PlanSheetShowGantt");
                var commands = Ui.Find<CommandBar>("PlanSheetCommands");
                var targets = commands.PrimaryCommands.OfType<AppBarButton>().Where(b => !b.IsInOverflow)
                    .Cast<FrameworkElement>().Append(Ui.Find<AppBarButton>("PlanSheetMore")).Append(toggle).ToList();
                if (shown) {
                    targets.Add(Ui.Find<Button>("PlanGanttGoToStatusDate"));
                    targets.Add(Ui.Find<ComboBox>("PlanGanttZoom"));
                }
                foreach (var target in targets) {
                    var bounds = Bounds(target);
                    Assert.That(bounds.Width, Is.GreaterThan(0), Name(target));
                    Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(-0.5), Name(target));
                    Assert.That(bounds.Right, Is.LessThanOrEqualTo(sheet.ActualWidth + 0.5), Name(target));
                    foreach (var other in targets.Where(t => !ReferenceEquals(t, target))) {
                        var intersection = bounds;
                        intersection.Intersect(Bounds(other));
                        Assert.That(intersection.IsEmpty || intersection.Width <= 0 || intersection.Height <= 0,
                            Is.True, $"{Name(target)} overlaps {Name(other)} at width {width}, Gantt {shown}.");
                    }
                }
                var center = toggle.TransformToVisual(null).TransformPoint(new(toggle.ActualWidth / 2, toggle.ActualHeight / 2));
                var hit = VisualTreeHelper.FindElementsInHostCoordinates(center, Ui.Root).FirstOrDefault();
                Assert.That(hit is not null && IsWithin(hit, toggle), Is.True,
                    $"The checkbox must receive its own pointer click at {center}; hit {hit?.GetType().Name}.");
                Assert.That(sheet.RowIds, Is.EqualTo(rows), "Changing chart visibility must not insert a row.");
                Assert.That(session.UndoCount, Is.EqualTo(undo), "Changing chart visibility must not edit the plan.");
            });
        }

        Rect Bounds(FrameworkElement element) => element.TransformToVisual(sheet).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        static string Name(FrameworkElement element) => AutomationProperties.GetAutomationId(element);
        static bool IsWithin(DependencyObject element, DependencyObject ancestor)
        {
            for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
                if (ReferenceEquals(current, ancestor)) return true;
            return false;
        }
    }
}
