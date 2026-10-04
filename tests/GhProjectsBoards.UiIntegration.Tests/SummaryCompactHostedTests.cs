using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;
using Windows.Foundation;
using Windows.Graphics;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable]
public sealed class SummaryCompactHostedTests
{
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
