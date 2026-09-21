using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable]
public sealed class PostMergeHostedTests
{
    private FrameworkElement? mounted;
    [TearDown] public async Task TearDown() { if (mounted is not null) await Ui.Unmount(mounted, check: false); }

    [Test]
    public async Task NativeNavigationAcrossUnseenColumnsAndRowsKeepsFocusOnTheSelectedIdentity()
    {
        var project = PlanningPathTests.Registration(80);
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]); work.Open(project);
        var session = new DraftSession(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-navigation-" + Guid.NewGuid().ToString("N"))), work, 0);
        EditingGrid grid = null!;
        await Ui.Run(() => { Ui.Window.AppWindow.Resize(new(1080, 760)); grid = new(project, session, () => Task.FromResult(true)); mounted = grid; });
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridCell0_0");
        _ = await SheetNativeInput.PointFor("GridCell0_0");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Focus(FocusState.Keyboard));
        async Task Move(Windows.System.VirtualKey key, int row, int column, params Windows.System.VirtualKey[] modifiers)
        {
            await SheetNativeInput.Press(key, modifiers);
            await Ui.Until(() => Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(grid.XamlRoot) is DependencyObject element
                && Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(element) == $"GridCell{row}_{column}");
            await Ui.Run(() => Assert.That(grid.SelectionIdentity?.Item, Is.EqualTo(project.Snapshot.Items[row].Id.NodeId)));
        }
        var last = work.ReadRows(project)[0].Cells.Length - 1;
        for (var column = 1; column <= last; column++) await Move(Windows.System.VirtualKey.Right, 0, column);
        await Move(Windows.System.VirtualKey.Tab, 1, 0);
        await Move(Windows.System.VirtualKey.Tab, 0, last, Windows.System.VirtualKey.Shift);
        await Move(Windows.System.VirtualKey.Tab, 1, 0);
        for (var row = 2; row <= 35; row++) await Move(Windows.System.VirtualKey.Down, row, 0);
        await Ui.Run(() => { Assert.That(work.DifferenceCount, Is.Zero); Assert.That(work.Fields.Any(f => f.Buffer is not null), Is.False); Assert.That(work.Journal, Is.Empty); });
    }

    [TestCase(false), TestCase(true)]
    public async Task DuplicateIssueAppearancesRemainSelectableInGanttWithoutDiscardingRows(bool conflict)
    {
        var (project, work) = GanttWorkload.Create(3);
        var first = project.Snapshot.Items[0]; var cells = work.ReadRows(project)[0].Cells;
        var duplicate = first with { Id = new(work.Scope, "duplicate"), Values = first.Values.Select(v =>
            v.FieldId is not null && cells.SingleOrDefault(c => c.Key?.FieldId == v.FieldId.NodeId) is { } cell
                ? v with { Scalar = work.Value(cell), Availability = work.Value(cell) is null ? ValueAvailability.Empty : ValueAvailability.Present } : v).ToArray() };
        if (conflict) duplicate = duplicate with { Values = duplicate.Values.Select(v => v.FieldId?.NodeId == "F-Estimate" ? v with { Scalar = "999" } : v).ToArray() };
        project = project with { Snapshot = project.Snapshot with { Items = project.Snapshot.Items.Append(duplicate).ToArray() } };
        work.SetRegistrations([project]); var projection = GanttProjection.Create(work, project, []);
        GanttView view = null!;
        await Ui.Run(() => { view = new(); mounted = view; view.Present(projection); });
        await Ui.Mount(view); await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("GanttTasks");
            list.SelectedItem = projection.Rows.Single(r => r.RowId == "duplicate");
            Assert.That(view.SelectedRowId, Is.EqualTo("duplicate"));
            Assert.That(list.Items.Count, Is.EqualTo(4));
            if (conflict) Assert.That(Ui.Find<TextBlock>("GanttNotice").Text, Does.Contain("重複"));
        });
        await Ui.ClickCommand("GanttReveal"); await Ui.ClickCommand("GanttDetails");
        await Ui.Until(() => Ui.Popup<StackPanel>("GanttTaskDetails") is { IsLoaded: true });
        await Ui.Run(() => Ui.Find<Button>("GanttDetails").Flyout.Hide());
        await Ui.Until(() => Ui.Popup<StackPanel>("GanttTaskDetails") is null);
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedItem = projection.Rows[1]);
    }

    [Test]
    public async Task SummaryKeepsTheRequestedAppearanceWhenAttributionChangesAndClearsMissingSelection()
    {
        var (project, work) = SummaryTests.Example();
        var first = project.Snapshot.Items[0]; var cells = work.ReadRows(project)[0].Cells;
        var duplicate = first with { Id = new(work.Scope, "duplicate"), Values = first.Values.Select(v =>
            v.FieldId is not null && cells.SingleOrDefault(c => c.Key?.FieldId == v.FieldId.NodeId) is { } cell
                ? v with { Scalar = work.Value(cell), Availability = work.Value(cell) is null ? ValueAvailability.Empty : ValueAvailability.Present } : v).ToArray() };
        project = project with { Snapshot = project.Snapshot with { Items = project.Snapshot.Items.Append(duplicate).ToArray() } };
        work.SetRegistrations([project]); var projection = SummaryProjection.Create(work, project, SummaryTests.Day);
        SummaryView view = null!; string? opened = null;
        await Ui.Run(() => { view = new(); mounted = view; view.TaskRequested += (id, _) => opened = id; view.Present(projection, selectedRow: "duplicate"); });
        await Ui.Mount(view); await Ui.Ready<ListView>("SummaryTasks");
        await Ui.Run(() => Assert.That(view.SelectedRowId, Is.EqualTo("duplicate")));
        await Ui.ClickCommand("SummaryBoards"); Assert.That(opened, Is.EqualTo("duplicate"));
        // A new adopted projection changes attribution while this view remains open.
        var moved = projection with { Contributions = projection.Contributions.Select(c => c.TaskId == "I1" ? c with { PersonId = "B" } : c).ToArray() };
        await Ui.Run(() => { view.Present(moved); Assert.That(view.SelectedPersonId, Is.EqualTo("B")); Assert.That(view.SelectedRowId, Is.EqualTo("duplicate")); });
        await Ui.Run(() => { view.Present(moved, selectedRow: "missing-row"); Assert.That(view.SelectedRowId, Is.Null); Assert.That(Ui.Find<Button>("SummaryBoards").IsEnabled, Is.False); });
    }
}
