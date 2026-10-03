using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [TestCase(0), TestCase(1), Category("GanttContext")]
    public async Task ReturningToProjectRetainsPannedDateScaleAndRowWithoutCenteringItsSelection(int scaleIndex)
    {
        await PrepareGanttContext();
        await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            var chooser = Ui.Find<ComboBox>("DailyProjectField", Ui.Dialog("DailyProgressDialog"));
            chooser.SelectedItem = chooser.Items.Cast<ComboBoxItem>().Single(item => (string?)item.Tag == "P1-status");
            Ui.DialogButton("DailyProgressDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("DailyProgressDialog") is null && Ui.Find<Button>("GanttProgress").IsEnabled);
        await Ui.Run(() => Ui.Find<ComboBox>("GanttScale").SelectedIndex = scaleIndex);
        await SheetNativeInput.Rendered();
        var horizontalOffset = scaleIndex == 0 ? 2160d : 630d; // October 27, 12:00 at either scale.
        await Ui.Run(() => {
            Ui.Find<ScrollViewer>("GanttHorizontal").ChangeView(horizontalOffset, null, null, true);
            GanttVertical().ChangeView(null, 790, null, true);
        });
        await Ui.Until(() => Math.Abs(Ui.Find<ScrollViewer>("GanttHorizontal").HorizontalOffset - horizontalOffset) < 1
            && Math.Abs(GanttVertical().VerticalOffset - 790) < 1);
        var revision = Work.Revision;
        var pending = Work.Fields.Single(field => field.Key == new FieldKey("Title", "I2")).Buffer;
        (string Id, double Top) originalTop = default;
        await Ui.Run(async () => { originalTop = GanttTopRow(); await ApplyInformationEvidence.Capture(panel, "gantt-context-before-" + scaleIndex); });

        await InvokeProjectNode("Project 2");
        await Ui.Until(() => Workspace.Selected?.Snapshot.Id.NodeId == "P2");
        await Ui.Run(() => Assert.That(Ui.Tree(panel).OfType<EditingGrid>().Single().DailyProjectFieldId, Is.Null,
            "An explicit field choice belongs to one scoped Project."));
        await InvokeProjectNode("Project 1");
        await Ui.Until(() => Workspace.Selected?.Snapshot.Id.NodeId == "P1" && Ui.Tree(panel).OfType<GanttView>().Any());
        await Ui.Ready<ListView>("GanttTasks");
        await Ui.Until(() => Ui.Tree(Ui.Find<ListView>("GanttTasks")).OfType<ScrollViewer>().Any(scroll => scroll.ViewportHeight > 0));
        await Ui.Until(() => Math.Abs(Ui.Find<ScrollViewer>("GanttHorizontal").HorizontalOffset - horizontalOffset) < 1
            && Math.Abs(GanttVertical().VerticalOffset - 790) < 1);
        await SheetNativeInput.Rendered();
        await Ui.Run(async () => {
            Assert.That(Ui.Find<ComboBox>("GanttScale").SelectedIndex, Is.EqualTo(scaleIndex));
            Assert.That(Ui.Find<GanttView>("GanttView").SelectedRowId, Is.EqualTo("P1-T1"));
            var top = GanttTopRow();
            Assert.That(top.Id, Is.EqualTo(originalTop.Id));
            Assert.That(top.Top, Is.EqualTo(originalTop.Top).Within(1), "The prior first row and its partial offset remain visible.");
            Assert.That(Work.Revision, Is.EqualTo(revision));
            Assert.That(Work.Fields.Single(field => field.Key == new FieldKey("Title", "I2")).Buffer, Is.EqualTo(pending));
            Assert.That(h.Writes, Is.Empty); Assert.That(Work.Journal, Is.Empty);
            await ApplyInformationEvidence.Capture(panel, "gantt-context-returned-" + scaleIndex);
        });
        await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            var selected = (ComboBoxItem)Ui.Find<ComboBox>("DailyProjectField", Ui.Dialog("DailyProgressDialog")).SelectedItem;
            Assert.That(selected.Tag, Is.EqualTo("P1-status"));
            Ui.DialogButton("DailyProgressDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("DailyProgressDialog") is null && Ui.Find<Button>("GanttProgress").IsEnabled);
        await Ui.Run(() => Ui.Find<ScrollViewer>("GanttHorizontal").ChangeView(120, null, null, true));
        await Ui.Until(() => Math.Abs(Ui.Find<ScrollViewer>("GanttHorizontal").HorizontalOffset - 120) < 1);
        await SheetNativeInput.Rendered();
        await Ui.Run(() => Assert.That(Ui.Find<ScrollViewer>("GanttHorizontal").HorizontalOffset, Is.EqualTo(120).Within(1),
            "Returning context is consumed once and cannot override later panning or dialog close."));
    }

    [Test, Category("GanttContext")]
    public async Task RestoringOutdatedViewportClampsToCurrentHorizonAndRows()
    {
        await PrepareGanttContext();
        await Ui.Run(() => {
            // This saved date and row no longer exist in the current projection.
            Ui.Find<GanttView>("GanttView").RestorePosition(new(new(2027, 12, 1), false, "removed-row", 10, 99999));
        });
        await Ui.Until(() => Math.Abs(Ui.Find<ScrollViewer>("GanttHorizontal").HorizontalOffset
                - Ui.Find<ScrollViewer>("GanttHorizontal").ScrollableWidth) < 1
            && Math.Abs(GanttVertical().VerticalOffset - GanttVertical().ScrollableHeight) < 1);
        await Ui.Run(() => {
            Assert.That(Ui.Find<GanttView>("GanttView").SelectedRowId, Is.EqualTo("P1-T1"));
            Ui.Find<GanttView>("GanttView").RestorePosition(new(new(2025, 1, 1), true, "removed-row", 0, -100));
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("GanttHorizontal").HorizontalOffset < 1 && GanttVertical().VerticalOffset < 1);
        await Ui.Run(() => Assert.That(Ui.Find<ComboBox>("GanttScale").SelectedIndex, Is.EqualTo(1)));
    }

    [Test, Category("GanttContext")]
    public async Task ExplicitTaskRevealWinsOverAQueuedReturnPosition()
    {
        await PrepareGanttContext();
        await Ui.Run(() => {
            Ui.Find<GanttView>("GanttView").RestorePosition(new(new(2026, 12, 1), false, "P1-T20", 10, 998));
            Ui.Click("GanttReveal");
        });
        await SheetNativeInput.Rendered();
        await Ui.Until(() => GanttBarIsVisible("P1-T1") && GanttVertical().VerticalOffset < 1);
        double revealedOffset = 0;
        await Ui.Run(() => revealedOffset = Ui.Find<ScrollViewer>("GanttHorizontal").HorizontalOffset);
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(GanttBarIsVisible("P1-T1"), Is.True, "The selected task remains visible after the queued layout restore is canceled.");
            Assert.That(Ui.Find<ScrollViewer>("GanttHorizontal").HorizontalOffset, Is.EqualTo(revealedOffset).Within(1));
            Assert.That(GanttVertical().VerticalOffset, Is.Zero.Within(1));
            Assert.That(Ui.Find<GanttView>("GanttView").SelectedRowId, Is.EqualTo("P1-T1"));
            Assert.That(h.Writes, Is.Empty);
        });
    }

    [Test, Category("GanttCalendar")]
    public async Task TaskReasonShowsTheAdoptedPersonalExceptionBehindTheGap()
    {
        await PrepareGanttContext(calendarGap: true);
        var adoptedRevision = Work.PlanFor(Workspace.Selected!).SourceRevision;
        await Ui.Until(() => Ui.Find<GanttView>("GanttView").AdoptedProjection.Plan.SourceRevision == adoptedRevision);
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 1);
        await Ui.ClickCommand("GanttDetails");
        await Ui.Until(() => Ui.Popup<StackPanel>("GanttTaskDetails") is { IsLoaded: true });
        await Ui.Run(async () => {
            var popup = Ui.Popup<StackPanel>("GanttTaskDetails")!;
            var explanation = Ui.Find<TextBlock>("GanttCalendarExplanation", popup);
            Assert.That(explanation.IsLoaded && explanation.Visibility == Visibility.Visible, Is.True);
            Assert.That(explanation.Text, Does.Contain("Owner 2").And.Contain("個人例外").And.Contain("2026-10-13")
                .And.Contain("10:00–12:00").And.Contain("残る稼働区間がありません").And.Contain("2026-10-14 09:00"));
            Assert.That(Ui.Tree(popup).OfType<TextBlock>().Select(text => text.Text), Has.Some.Contains("計画上の進捗"));
            Assert.That(Work.PlanFor(Workspace.Selected!).Tasks.Single(task => task.Id == "I2").Start,
                Is.EqualTo(new DateTime(2026, 10, 14, 9, 0, 0)));
            Assert.That(h.Writes, Is.Empty);
            await ApplyInformationEvidence.Capture(panel, "gantt-adopted-personal-calendar-gap");
        });
    }

    private async Task PrepareGanttContext(bool calendarGap = false)
    {
        await Ui.Unmount(panel);
        await Ui.Run(async () => { await Workspace.StopAsync(); Assert.That(await Workspace.FlushDraftsAsync(), Is.True); });
        h = await CreationHarness.Create(30, planning: true);
        var choice = await new ProjectDiscovery(h.Existing.Service).ResolveAsync(h.Existing.Context,
            "https://github.com/users/sample-user/projects/2", default);
        await Workspace.RegisterAsync(choice, null);
        await Workspace.SelectAsync(new(Work.Scope, "P2"));
        // Initialize both observed baselines before measuring view-only navigation.
        Work.Open(Workspace.Selected!);
        await Workspace.SelectAsync(new(Work.Scope, "P1"));
        var project = Workspace.Selected!;
        var start = new DateTime(2026, 10, 5, 9, 0, 0);
        var plan = PlanningPathTests.Plan();
        if (calendarGap)
            Work.CommitPlanning(project, plan with {
                People = [new("U1", "Owner 1", 100), new("U2", "Owner 2", 100)],
                Calendar = plan.Calendar with { Exceptions = [new(new(2026, 10, 13), null, [new(540, 1080)]),
                    new(new(2026, 10, 13), "U2", [new(600, 720)])] },
                Tasks = [new("I1", PlanningMode.Manual, "U1", ManualStart: new(2026, 10, 13, 9, 0, 0),
                    ManualFinish: new(2026, 10, 13, 12, 0, 0)), new("I2", PlanningMode.Auto, "U2", LocalLinks: [new("I1")])]
            }, Work.Revision, [new("P1-T1", "Estimate", "3"), new("P1-T2", "Estimate", "4")]);
        else Work.CommitPlanning(project, plan with { Tasks = Enumerable.Range(0, 30).Select(i =>
            new PlanningTask("I" + (i + 1), PlanningMode.Manual, ManualStart: start.AddDays(i * 3),
                ManualFinish: start.AddDays(i * 3).AddHours(3))).ToArray() }, Work.Revision);
        Work.SetBuffer(Work.Open(project)[1].Cells[0], "pending title stays in P1");
        await Ui.Run(() => { panel.Width = 1100; panel.Height = 700; panel.Initialize(Workspace); });
        await Ui.Mount(panel); await OpenNavigation(SplitViewDisplayMode.Inline);
        await Ui.Run(() => Ui.Find<SelectorBar>("ProjectViews").SelectedItem = Ui.Find<SelectorBar>("ProjectViews").Items[1]);
        await Ui.Ready<ListView>("GanttTasks");
        await Ui.Until(() => Ui.Tree(Ui.Find<ListView>("GanttTasks")).OfType<ScrollViewer>().Any(scroll => scroll.ViewportHeight > 0));
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 0);
    }

    private static ScrollViewer GanttVertical() => Ui.Tree(Ui.Find<ListView>("GanttTasks")).OfType<ScrollViewer>().Single();
    private static bool GanttBarIsVisible(string rowId)
    {
        var bar = Ui.Tree(Ui.Find<ListView>("GanttTasks")).OfType<Rectangle>()
            .SingleOrDefault(value => AutomationProperties.GetAutomationId(value) == "GanttBar-" + rowId);
        return bar is { IsLoaded: true, ActualWidth: > 0 }
            && VisualTreeHelper.GetParent(bar) is Canvas { Clip: RectangleGeometry clip }
            && Canvas.GetLeft(bar) >= 0 && Canvas.GetLeft(bar) + bar.ActualWidth <= clip.Rect.Width;
    }
    private static (string Id, double Top) GanttTopRow()
    {
        var viewport = GanttVertical();
        return Ui.Tree(Ui.Find<ListView>("GanttTasks")).OfType<ListViewItem>().Where(row => row.IsLoaded)
            .Select(row => (Id: AutomationProperties.GetAutomationId(row), Bounds: row.TransformToVisual(viewport)
                .TransformBounds(new(0, 0, row.ActualWidth, row.ActualHeight))))
            .Where(row => row.Bounds.Bottom > 0 && row.Bounds.Top < viewport.ViewportHeight)
            .OrderBy(row => row.Bounds.Top).Select(row => (row.Id, row.Bounds.Top)).First();
    }
}
