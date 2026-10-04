using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NUnit.Framework;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable]
public sealed class SummaryCompactHostedTests
{
    [Test, Category("SummaryHeader")]
    public async Task PersonComparisonKeepsColumnLabelsAlignedThroughVerticalAndHorizontalScrollingAndKeyboardNavigation()
    {
        var (project, work) = SummaryWorkload.Create();
        var projection = SummaryProjection.Create(work, project, DateOnly.FromDateTime(DateTime.UtcNow.AddHours(9)));
        SummaryView view = null!; SizeInt32 originalSize = default; double headerTop = 0;
        await Ui.Run(() => {
            originalSize = Ui.Window.AppWindow.Size;
            var scale = Ui.Root.XamlRoot.RasterizationScale;
            Ui.Window.AppWindow.Resize(new((int)(960 * scale), (int)(650 * scale)));
            view = new SummaryView { Width = 760, Height = 520, VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left };
            view.Present(projection);
        });
        await Ui.Mount(view);
        try
        {
            await Ui.Ready<ListView>("SummaryPeople"); await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                Assert.That(People().Items.Count, Is.GreaterThanOrEqualTo(20));
                headerTop = Bounds(Header("担当者"), view).Top;
                Assert.That(Vertical().ScrollableHeight, Is.GreaterThan(0));
                AssertVerticalScrollBarReachable();
                Vertical().ChangeView(null, Vertical().ScrollableHeight, null, true);
            });
            await Ui.Until(() => Vertical().VerticalOffset > 0 && People().ContainerFromIndex(People().Items.Count - 1) is FrameworkElement);
            await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                AssertHeaderAndRow();
                Assert.That(Horizontal().ScrollableWidth, Is.GreaterThan(100));
                Horizontal().ChangeView(Horizontal().ScrollableWidth, null, null, true);
            });
            await Ui.Until(() => Horizontal().HorizontalOffset > 100);
            await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                AssertHeaderAndRow();
                var lastLabel = Bounds(Header("余裕 / 超過"), view);
                Assert.That(lastLabel.Left >= 0 && lastLabel.Right <= view.ActualWidth + 1, Is.True,
                    "The last measure label must be fully readable at the horizontal end.");
                Horizontal().ChangeView(0, null, null, true);
            });
            await Ui.Until(() => Horizontal().HorizontalOffset == 0);
            await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                AssertHeaderAndRow();
                Assert.That(Bounds(Header("担当者"), view).Left, Is.GreaterThanOrEqualTo(0));
                AssertVerticalScrollBarReachable();
                People().Focus(FocusState.Programmatic);
            });
            await SheetNativeInput.ActivateWindow();
            await SheetNativeInput.Press(VirtualKey.End);
            await Ui.Until(() => view.SelectedPersonId == ((PersonSummary)People().Items[People().Items.Count - 1]).Id);
            await SheetNativeInput.Rendered();
            await Ui.Run(() => AssertHeaderAndRow());
            await SheetNativeInput.Press(VirtualKey.Home);
            await Ui.Until(() => view.SelectedPersonId == ((PersonSummary)People().Items[0]).Id && Vertical().VerticalOffset == 0);
            await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                Assert.That(Bounds(Header("担当者"), view).Top, Is.EqualTo(headerTop).Within(1));
                var first = (FrameworkElement)People().ContainerFromIndex(0);
                var bounds = Bounds(first, Vertical());
                Assert.That(bounds.Top >= -1 && bounds.Bottom <= Vertical().ViewportHeight + 1, Is.True);
                TestContext.Out.WriteLine($"First and last of {People().Items.Count} people remain reachable; header top={headerTop}; horizontal round trip completed.");
            });
        }
        finally
        {
            await Ui.Unmount(view);
            await Ui.Run(() => Ui.Window.AppWindow.Resize(originalSize));
            await Ui.Idle();
        }

        ListView People() => Ui.Find<ListView>("SummaryPeople", view);
        ScrollViewer Vertical() => Ui.Tree(People()).OfType<ScrollViewer>().First();
        ScrollViewer Horizontal() => Vertical();
        TextBlock Header(string label) => Ui.Tree(view).OfType<TextBlock>().Single(text => text.Text == label);
        void AssertVerticalScrollBarReachable()
        {
            var bar = Ui.Tree(Vertical()).OfType<ScrollBar>().Single(scroll => scroll.Orientation == Orientation.Vertical);
            var bounds = Bounds(bar, view);
            Assert.That(bar.ActualWidth > 0 && bar.ActualHeight > 0 && bounds.Left >= 0 && bounds.Right <= view.ActualWidth + 1, Is.True,
                $"The native vertical scrollbar must remain reachable at horizontal position zero. Bar={bounds}; Summary width={view.ActualWidth}");
        }
        void AssertHeaderAndRow()
        {
            var label = Bounds(Header("担当者"), view);
            Assert.That(label.Top, Is.EqualTo(headerTop).Within(1),
                "Person comparison labels must remain visible while the people list scrolls vertically.");
            var last = (FrameworkElement)People().ContainerFromIndex(People().Items.Count - 1);
            var rowBounds = Bounds(last, Vertical());
            Assert.That(rowBounds.Top >= -1 && rowBounds.Bottom <= Vertical().ViewportHeight + 1, Is.True,
                "The final person must fit completely inside the list viewport.");
            var name = Ui.Tree(last).OfType<TextBlock>().Single(text => text.Text == ((PersonSummary)People().Items[People().Items.Count - 1]).Name);
            var headroom = Ui.Tree(last).OfType<TextBlock>().Single(text => text.Text.StartsWith("余裕 ")
                || text.Text.StartsWith("⚠ 超過 ") || text.Text == "△ 比較未完");
            Assert.That(Bounds(name, view).Left, Is.EqualTo(label.Left).Within(1), "Names must stay under their column label.");
            Assert.That(Bounds(headroom, view).Left, Is.EqualTo(Bounds(Header("余裕 / 超過"), view).Left).Within(1),
                "Headroom must stay under its column label through horizontal scrolling.");
            TestContext.Out.WriteLine($"Header={label}; last row={rowBounds}; vertical={Vertical().VerticalOffset}/{Vertical().ScrollableHeight}; horizontal={Horizontal().HorizontalOffset}/{Horizontal().ScrollableWidth}");
        }
        static Rect Bounds(FrameworkElement control, FrameworkElement relativeTo) =>
            control.TransformToVisual(relativeTo).TransformBounds(new(0, 0, control.ActualWidth, control.ActualHeight));
    }

    [Test, Category("NextRoadmap")]
    public async Task CompactViewportShowsCompleteComparisonAndTaskRowsWithCorrectionAndFilterReachable()
    {
        var (project, work) = SummaryWorkload.Create();
        var projection = SummaryProjection.Create(work, project, DateOnly.FromDateTime(DateTime.UtcNow.AddHours(9)));
        SummaryView view = null!; SizeInt32 originalSize = default; string? editTarget = null;
        await Ui.Run(() => {
            originalSize = Ui.Window.AppWindow.Size;
            var scale = Ui.Root.XamlRoot.RasterizationScale;
            Ui.Window.AppWindow.Resize(new((int)(960 * scale), (int)(600 * scale)));
            // The ordinary 1200x750 app at 125% leaves this Summary viewport
            // below its Project header and above its persistent work status.
            view = new SummaryView { Width = 944, Height = 352, VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left };
            view.EditRequested += id => editTarget = id;
            view.Present(projection, "U2");
        });
        await Ui.Mount(view);
        try
        {
            await Ui.Ready<ListView>("SummaryPeople"); await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                view.UpdateLayout();
                Assert.That(view.ActualHeight, Is.EqualTo(352).Within(1));
                AssertCompleteItem("SummaryPeople", 0);
                AssertCompleteItem("SummaryTasks", 0);
                foreach (var id in new[] { "SummaryPersonDetail", "SummaryRemaining", "SummaryRemainingUpdate" })
                {
                    var control = Ui.Find<FrameworkElement>(id, view);
                    var bounds = Bounds(control, view);
                    TestContext.Out.WriteLine($"{id}: {bounds}; Summary={view.ActualWidth}x{view.ActualHeight}");
                    Assert.That(control.IsLoaded && control.Visibility == Visibility.Visible && bounds.Height > 0, Is.True, id);
                    Assert.That(bounds.Top >= 0 && bounds.Bottom <= view.ActualHeight + 1
                        && bounds.Left >= 0 && bounds.Right <= view.ActualWidth + 1, Is.True, id + " must remain reachable without whole-page scrolling.");
                }
                Assert.That(Ui.Find<TextBlock>("SummaryPersonDetail", view).Text, Does.Contain("B · ⚠ 超過 2"));
            });
            await Ui.ClickCommand("SummaryEdit");
            Assert.That(editTarget, Is.EqualTo("P1T2"));
            await Ui.ClickCommand("SummaryFilterOpen");
            await Ui.Until(() => Ui.Popup<TextBox>("SummaryFilter") is { IsLoaded: true });
            await Ui.Run(() => Ui.Popup<TextBox>("SummaryFilter")!.Text = "no matching task");
            await Ui.Until(() => Ui.Find<ListView>("SummaryTasks", view).Items.Count == 0);
            await Ui.Run(() => {
                Ui.Popup<TextBox>("SummaryFilter")!.Text = "";
                FilterButton().Flyout.Hide(); FilterCommands().IsOpen = false;
            });
            await Ui.Until(() => Ui.Find<ListView>("SummaryTasks", view).Items.Count == 1);
            await Ui.Run(async () => {
                AssertCompleteItem("SummaryPeople", 0); AssertCompleteItem("SummaryTasks", 0);
                await ApplyInformationEvidence.Capture(view, "summary-compact-comparison-and-task");
            });
            await Ui.Run(() => view.Height = 520);
            await Ui.Ready<TextBox>("SummaryFilter");
            await Ui.Run(() => {
                var visibleFilter = Ui.Find<TextBox>("SummaryFilter", view);
                Assert.That(Bounds(visibleFilter, view).Bottom, Is.LessThanOrEqualTo(view.ActualHeight));
                Assert.That(Ui.Find<TextBox>("SummaryRemaining", view).Header, Is.EqualTo("タスクの残時間（人時）"));
                visibleFilter.Text = "no matching task";
            });
            await Ui.Until(() => Ui.Find<ListView>("SummaryTasks", view).Items.Count == 0);
            await Ui.Run(() => view.Height = 352);
            await SheetNativeInput.Rendered();
            await Ui.ClickCommand("SummaryFilterOpen");
            await Ui.Until(() => Ui.Popup<TextBox>("SummaryFilter") is { IsLoaded: true });
            await Ui.Run(() => {
                Assert.That(Ui.Popup<TextBox>("SummaryFilter")!.Text, Is.EqualTo("no matching task"));
                Ui.Popup<TextBox>("SummaryFilter")!.Text = ""; FilterButton().Flyout.Hide(); FilterCommands().IsOpen = false;
            });
            await Ui.Until(() => Ui.Find<ListView>("SummaryTasks", view).Items.Count == 1);
        }
        finally
        {
            await Ui.Run(() => FilterButtonOrNull()?.Flyout?.Hide());
            await Ui.Unmount(view);
            await Ui.Run(() => Ui.Window.AppWindow.Resize(originalSize));
            await Ui.Idle();
        }

        void AssertCompleteItem(string id, int index)
        {
            var list = Ui.Find<ListView>(id, view);
            var item = list.ContainerFromIndex(index) as FrameworkElement;
            Assert.That(item, Is.Not.Null, id + " must realize its selected row.");
            var scroll = Ui.Tree(list).OfType<ScrollViewer>().First();
            var bounds = Bounds(item!, scroll);
            TestContext.Out.WriteLine($"{id}: row={bounds}; viewport={scroll.ViewportWidth}x{scroll.ViewportHeight}; list={list.ActualWidth}x{list.ActualHeight}");
            Assert.That(bounds.Height, Is.GreaterThan(30), id + " must retain its readable native row height.");
            Assert.That(bounds.Top >= -1 && bounds.Bottom <= scroll.ViewportHeight + 1, Is.True,
                $"{id} must show a complete data row, not only its header or a clipped fragment. Row={bounds}; viewport={scroll.ViewportWidth}x{scroll.ViewportHeight}; list={list.ActualWidth}x{list.ActualHeight}");
        }
        AppBarButton? FilterButtonOrNull() => Ui.Tree(view).OfType<CommandBar>()
            .SelectMany(bar => bar.PrimaryCommands.Concat(bar.SecondaryCommands)).OfType<AppBarButton>()
            .SingleOrDefault(button => AutomationProperties.GetAutomationId(button) == "SummaryFilterOpen");
        AppBarButton FilterButton() => FilterButtonOrNull()!;
        CommandBar FilterCommands() => Ui.Tree(view).OfType<CommandBar>().Single(bar => bar.SecondaryCommands.Contains(FilterButton()));
        static Rect Bounds(FrameworkElement control, FrameworkElement relativeTo) =>
            control.TransformToVisual(relativeTo).TransformBounds(new(0, 0, control.ActualWidth, control.ActualHeight));
    }
}
