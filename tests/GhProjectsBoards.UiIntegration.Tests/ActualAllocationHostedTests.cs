using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class PlanningHostedTests
{
    [TestCase(1280, 800), TestCase(960, 600), Category("NextRoadmap")]
    public async Task QuickAllocationInputsAndFixedActionsFitTheVisiblePopupAtNormalAndNarrowSizes(int width, int height)
    {
        await Ui.Unmount(grid);
        var work = session.Workspace; var plan = work.Planning("P1")!;
        work.CommitPlanning(project, plan with { People = [new("U1", "First worker"), new("U2", "Second worker")],
            Tasks = [plan.Tasks[0] with { Actuals = [new("U1", 3, new(2026, 10, 6)), new("U2", 2, new(2026, 10, 7))] }] },
            work.Revision, [new("P1T1", "Remaining", "4")]);
        Windows.Graphics.SizeInt32 originalSize = default;
        await Ui.Run(() => {
            originalSize = Ui.Window.AppWindow.Size;
            var scale = Ui.Root.XamlRoot.RasterizationScale;
            Ui.Window.AppWindow.Resize(new((int)(width * scale), (int)(height * scale)));
            grid = new(project, session, () => Task.FromResult(true));
        });
        await Ui.Mount(grid);
        try
        {
            await Ui.Until(() => grid.ActualWidth <= width && grid.ActualWidth >= width - 40);
            await OpenQuickAllocation();
            await Ui.Run(() => {
                var editor = Ui.Popup<StackPanel>("ActualReportsEditor")!;
                editor.UpdateLayout();
                foreach (var id in new[] { "ActualReportsRemainingTotal", "ActualReportHours-U1", "ActualReportRemaining-U1",
                    "ActualReportDate-U1", "ActualReportRemove-U1", "ActualReportHours-U2", "ActualReportRemaining-U2",
                    "ActualReportDate-U2", "ActualReportRemove-U2" })
                    AssertVisibleBounds(id, vertical: false);
                foreach (var id in new[] { "ActualReportsTotal", "ActualReportAddWorker", "ActualReportAdd", "ActualReportsUpdate", "ActualReportsCancel" })
                    AssertVisibleBounds(id, vertical: true);

                void AssertVisibleBounds(string id, bool vertical)
                {
                    var control = Ui.Find<FrameworkElement>(id, editor);
                    Assert.That(control.ActualWidth > 0 && control.ActualHeight > 0, Is.True, id);
                    for (var parent = VisualTreeHelper.GetParent(control); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                    {
                        if (parent is not FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } frame) continue;
                        var bounds = control.TransformToVisual(frame).TransformBounds(new(0, 0, control.ActualWidth, control.ActualHeight));
                        var visibleWidth = frame is ScrollViewer scroll ? scroll.ViewportWidth : frame.ActualWidth;
                        var visibleHeight = frame is ScrollViewer verticalScroll ? verticalScroll.ViewportHeight : frame.ActualHeight;
                        TestContext.Out.WriteLine($"{width}x{height}: {id} in {frame.GetType().Name}={bounds}; viewport={visibleWidth}x{visibleHeight}");
                        Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(-1), id + " left edge");
                        Assert.That(bounds.Right, Is.LessThanOrEqualTo(visibleWidth + 1), id + " must not require horizontal scrolling");
                        if (!vertical) continue;
                        Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(-1), id + " top edge");
                        Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(visibleHeight + 1), id + " must stay visible while worker rows scroll");
                    }
                }
            });
        }
        finally
        {
            await Ui.Run(() => {
                if (Ui.Popup<StackPanel>("ActualReportsEditor") is { } editor) Ui.Click(Ui.Find<Button>("ActualReportsCancel", editor));
                Ui.Window.AppWindow.Resize(originalSize);
            });
            await Ui.Until(() => Ui.Popup<StackPanel>("ActualReportsEditor") is null);
        }
    }

    [Test, Category("NextRoadmap")]
    public async Task QuickAllocationEditsTwoWorkersTogetherAndCancelAndUndoPreserveBothTotals()
    {
        await Ui.Unmount(grid);
        var work = session.Workspace; var plan = work.Planning("P1")!;
        var task = plan.Tasks[0] with { Actuals = [new("U1", 3, new(2026, 10, 6)), new("U2", 2, new(2026, 10, 7))],
            Contributions = [new("U1", 5, 2), new("U2", 3, 2)] };
        work.CommitPlanning(project, plan with { People = [new("U1", "First worker"), new("U2", "Second worker")], Tasks = [task] }, work.Revision,
            [new("P1T1", "Estimate", "8"), new("P1T1", "Remaining", "4")]);
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await OpenQuickAllocation();
        var before = JsonSerializer.Serialize(work.Snapshot());
        StackPanel allocationEditor = null!;
        await Ui.Run(() => {
            var editor = allocationEditor = Ui.Popup<StackPanel>("ActualReportsEditor")!;
            Ui.Find<TextBox>("ActualReportHours-U1", editor).Text = "5";
            Ui.Find<TextBox>("ActualReportRemaining-U1", editor).Text = "1";
            Ui.Find<TextBox>("ActualReportRemaining-U2", editor).Text = "1.5";
            Ui.Find<TextBox>("ActualReportsRemainingTotal", editor).Text = "3";
        });
        var reportedMissingPopup = false;
        await Ui.Until(() => {
            if (Ui.Popup<StackPanel>("ActualReportsEditor") is { } editor)
                return Ui.Find<TextBlock>("ActualReportsTotal", editor).Text.Contains("未配分 0.5");
            if (!reportedMissingPopup)
            {
                reportedMissingPopup = true;
                var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Ui.Root.XamlRoot) as DependencyObject;
                TestContext.Out.WriteLine($"Allocation popup absent after editing: previousLoaded={allocationEditor.IsLoaded}, "
                    + $"focusedType={focused?.GetType().Name}, focusedId={(focused is null ? null : Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(focused))}");
            }
            return false;
        });
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("ActualReportsEditor")!;
            Assert.That(Ui.Find<TextBlock>("ActualReportsTotal", editor).Text, Does.Contain("未配分 0.5"));
            Ui.Click(Ui.Find<Button>("ActualReportsCancel", editor));
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("ActualReportsEditor") is null);
        Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
        await OpenQuickAllocation();
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("ActualReportsEditor")!;
            Ui.Find<TextBox>("ActualReportHours-U1", editor).Text = "5";
            Ui.Find<TextBox>("ActualReportRemaining-U1", editor).Text = "1";
            Ui.Find<TextBox>("ActualReportRemaining-U2", editor).Text = "1.5";
            Ui.Find<TextBox>("ActualReportsRemainingTotal", editor).Text = "3";
            Ui.Click(Ui.Find<Button>("ActualReportsUpdate", editor));
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("ActualReportsEditor") is null);
        await Ui.Run(() => {
            var row = work.Open(project)[0];
            Assert.That(work.Value(row.Cells[4]), Is.EqualTo("7")); Assert.That(work.Value(row.Cells[3]), Is.EqualTo("3"));
            var edited = work.Planning("P1")!.Tasks.Single(t => t.Id == task.Id);
            Assert.That(edited.Actuals![1], Is.EqualTo(task.Actuals![1]));
            Assert.That(edited.Contributions, Is.EqualTo(new[] { new WorkContribution("U1", 5, 1), new WorkContribution("U2", 3, 1.5m) }));
            Assert.That(work.Journal, Is.Empty);
        });
        await Ui.ClickCommand("GridUndo");
        await Ui.Run(() => {
            var row = work.Open(project)[0];
            Assert.That(work.Value(row.Cells[4]), Is.EqualTo("5")); Assert.That(work.Value(row.Cells[3]), Is.EqualTo("4"));
            Assert.That(work.Planning("P1")!.Tasks.Single(t => t.Id == task.Id).Contributions, Is.EqualTo(task.Contributions));
        });
    }

    [Test, Category("NextRoadmap")]
    public async Task QuickAllocationWithoutRemainingMappingRetainsTheExistingActualOnlyEditor()
    {
        var plan = PlanningPathTests.Plan();
        await ReviewFixture(project, plan with { Fields = plan.Fields.Where(f => f.Role != "Remaining").ToArray(),
            Tasks = [new("I1", Actuals: [new("U1", 3, new(2026, 10, 6))])] });
        await OpenQuickAllocation();
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("ActualReportsEditor")!;
            Assert.That(Ui.Tree(editor).OfType<TextBox>().Any(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t) == "ActualReportsRemainingTotal"), Is.False);
            Ui.Find<TextBox>("ActualReportHours-U1", editor).Text = "5";
            Ui.Click(Ui.Find<Button>("ActualReportsUpdate", editor));
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("ActualReportsEditor") is null);
        await Ui.Run(() => {
            var row = session.Workspace.Open(project)[0];
            Assert.That(session.Workspace.Value(row.Cells.Single(c => c.Key?.FieldId == "F-Actual")), Is.EqualTo("5"));
            Assert.That(session.Workspace.Value(row.Cells[0]), Is.EqualTo(project.Snapshot.Issues.Values.First().Title.Value));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }

    [Test, Category("NextRoadmap")]
    public async Task QuickAllocationKeepsInvalidSharesInPlaceAndScrollsManyWorkersWithActionsAvailable()
    {
        await Ui.Unmount(grid); var work = session.Workspace; var plan = work.Planning("P1")!;
        var people = Enumerable.Range(1, 12).Select(n => new PlanningPerson("U" + n, "Worker " + n)).ToArray();
        work.CommitPlanning(project, plan with { People = people, Tasks = [plan.Tasks[0] with {
            Actuals = people.Select(p => new ActualContribution(p.Id, 1, new(2026, 10, 6))).ToArray() }] }, work.Revision,
            [new("P1T1", "Remaining", "4")]);
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await OpenQuickAllocation();
        var before = JsonSerializer.Serialize(work.Snapshot());
        await Ui.Run(() => Ui.Find<TextBox>("ActualReportRemaining-U1", Ui.Popup<StackPanel>("ActualReportsEditor")).Text = "5");
        await Ui.Until(() => Ui.Find<TextBlock>("ActualReportsTotal", Ui.Popup<StackPanel>("ActualReportsEditor")).Text.Contains("配分済み 5"));
        ScrollViewer? scroller = null;
        try
        {
            await Ui.Run(() => {
                var editor = Ui.Popup<StackPanel>("ActualReportsEditor")!;
                Ui.Click(Ui.Find<Button>("ActualReportsUpdate", editor));
                Assert.That(Ui.Find<TextBlock>("ActualReportsError", editor).Text, Does.Contain("合計"));
                Assert.That(Ui.Find<TextBox>("ActualReportRemaining-U1", editor).Text, Is.EqualTo("5"));
                editor.UpdateLayout();
                scroller = Ui.Find<ScrollViewer>("ActualReportsRows", editor);
                Assert.That(scroller.ScrollableHeight, Is.GreaterThan(0));
                scroller.ViewChanged += ObserveScroll;
                TestContext.Out.WriteLine($"Allocation scroll requested: offset={scroller.VerticalOffset}, target={scroller.ScrollableHeight}, viewport={scroller.ViewportHeight}");
                scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
            });
            await Ui.Until(() => {
                var last = Ui.Find<TextBox>("ActualReportHours-U12", Ui.Popup<StackPanel>("ActualReportsEditor"));
                var bounds = last.TransformToVisual(scroller).TransformBounds(new(0, 0, last.ActualWidth, last.ActualHeight));
                return Math.Abs(scroller!.VerticalOffset - scroller.ScrollableHeight) <= 1
                    && bounds.Top >= -1 && bounds.Bottom <= scroller.ViewportHeight + 1;
            });
            await Ui.Run(() => {
                var editor = Ui.Popup<StackPanel>("ActualReportsEditor")!;
                var last = Ui.Find<TextBox>("ActualReportHours-U12", editor);
                var bounds = last.TransformToVisual(scroller).TransformBounds(new(0, 0, last.ActualWidth, last.ActualHeight));
                TestContext.Out.WriteLine($"Allocation scroll settled: offset={scroller!.VerticalOffset}, target={scroller.ScrollableHeight}, last={bounds}, viewport={scroller.ViewportHeight}");
                Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(-1));
                Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(scroller.ViewportHeight + 1));
                Assert.That(Ui.Find<Button>("ActualReportsUpdate", editor).IsLoaded, Is.True);
                Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
                Ui.Click(Ui.Find<Button>("ActualReportsCancel", editor));
            });
        }
        finally { await Ui.Run(() => { if (scroller is not null) scroller.ViewChanged -= ObserveScroll; }); }

        void ObserveScroll(object? sender, ScrollViewerViewChangedEventArgs args) =>
            TestContext.Out.WriteLine($"Allocation scroll changed: intermediate={args.IsIntermediate}, offset={scroller!.VerticalOffset}, target={scroller.ScrollableHeight}, viewport={scroller.ViewportHeight}");
    }

    private async Task OpenQuickAllocation()
    {
        var column = Array.FindIndex(session.Workspace.Open(project)[0].Cells, c => c.Key?.FieldId == "F-Actual");
        await Ui.Ready<FrameworkElement>("GridCell0_" + column);
        await Ui.Run(() => FocusCell("GridCell0_" + column));
        await Ui.Until(() => grid.SelectionIdentity?.Field?.FieldId == "F-Actual");
        await Ui.Run(() => { if (Ui.Find<StackPanel>("ActualCellEditor").Visibility != Visibility.Visible) Ui.Click("ActualContext"); });
        await Ui.Ready<Button>("ActualDetails"); await Ui.Run(() => Ui.Click("ActualDetails"));
        await Ui.Until(() => Ui.Popup<StackPanel>("ActualReportsEditor") is { IsLoaded: true } e && Ui.Find<Button>("ActualReportsUpdate", e).IsLoaded);
    }
}
