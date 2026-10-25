using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
namespace GhProjectsBoards.UiIntegration.Tests;

// #230: the person load matrix reads as two columns, states overload in text, opens its drill-down
// beside the clicked cell, and links each person name to 計画.
internal sealed partial class PlanWorkspaceHostedTests
{
    private static string Runs(TextBlock text) => string.Concat(text.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Select(run => run.Text));
    private static TextBlock CellText(FrameworkElement cell) => Ui.Tree(cell).OfType<TextBlock>()
        .Single(t => AutomationProperties.GetAutomationId(t).EndsWith("Text", StringComparison.Ordinal));

    [Test]
    public async Task SummaryReservesTheVerdictLineAboveTheBandAndColumns()
    {
        await OpenVersionSummary();
        await Ui.Run(() => {
            var summary = Ui.Find<PlanSummaryView>("PlanSummary");
            var verdict = Ui.Find<Grid>("SummaryVerdict");
            var band = Ui.Find<TextBlock>("SummaryProgress");
            var load = Ui.Find<TextBlock>("SummaryLoadHeading");
            Assert.That(verdict.TransformToVisual(summary).TransformPoint(new()).Y,
                Is.LessThan(band.TransformToVisual(summary).TransformPoint(new()).Y), "The verdict line comes before the Project band (#260 slot).");
            Assert.That(load.TransformToVisual(summary).TransformPoint(new()).Y,
                Is.LessThan(band.TransformToVisual(summary).TransformPoint(new()).Y + band.ActualHeight + 220), "The load column starts beside the phase table, not below the fold.");
        });
    }

    [Test]
    public async Task SummaryMonthCellShowsTheLoadRateAndDaysOverTheCapWithoutColour()
    {
        await OpenVersionSummary();
        await Ui.Run(() => Ui.Find<ComboBox>("SummaryMode").SelectedIndex = 1);
        await Ui.Ready<Button>("SummaryMonth_U7_3"); await Ui.Idle();
        await Ui.Run(() => {
            var summary = Ui.Find<PlanSummaryView>("PlanSummary");
            var overloaded = Ui.Find<Button>("SummaryMonth_U7_3");
            var text = CellText(overloaded);
            // 「131% 9日」: the day count is text, so the overload survives greyscale (#230 acceptance).
            Assert.That(Runs(text), Is.EqualTo("131% 9日"));
            var critical = PlanSheetView.Brush("SystemFillColorCriticalBrush");
            Assert.That(text.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().All(run => ReferenceEquals(run.Foreground, critical)), Is.True);
            Assert.That(AutomationProperties.GetName(overloaded), Does.Contain("負荷率 131%").And.Contain("上限を超える日 9日"));
            // 最大日次負荷 moved out of the cell into the tooltip/accessible name and the drill-down.
            Assert.That(Runs(text), Does.Not.Contain("325"));
            Assert.That(AutomationProperties.GetName(overloaded), Does.Contain("最大日次負荷 325%（1/20 (水)）"));
            // A month under the cap keeps the rate in secondary text and names no day count.
            var calm = CellText(Ui.Find<Button>("SummaryMonth_U7_4"));
            Assert.That(Runs(calm), Does.Not.Contain("日"));
            // Months with work behind the 基準日 read 完了; months without work read —, never 0.
            var cells = Ui.Tree(summary).OfType<Button>().Where(b => AutomationProperties.GetAutomationId(b).StartsWith("SummaryMonth_")).ToArray();
            Assert.That(cells.Any(cell => Runs(CellText(cell)) == "完了"), Is.True, "A finished month before the 基準日 reads 完了.");
            Assert.That(cells.Any(cell => Runs(CellText(cell)).Contains('0') && Runs(CellText(cell)).EndsWith('日') && Runs(CellText(cell)).StartsWith('0')), Is.False,
                "No month cell reads a bare 0 load.");
        });
    }

    [Test]
    public async Task SummaryMonthDrillDownOpensBesideTheClickedCellAndKeepsItVisible()
    {
        await OpenVersionSummary();
        await Ui.Run(() => Ui.Find<ComboBox>("SummaryMode").SelectedIndex = 1);
        await Ui.Ready<Button>("SummaryMonth_U7_3"); await Ui.Idle();
        Button cell = null!;
        await Ui.Run(() => { cell = Ui.Find<Button>("SummaryMonth_U7_3"); Ui.Click("SummaryMonth_U7_3"); });
        await Ui.Ready<FrameworkElement>("SummaryLoadPanel"); await Ui.Idle();
        // The panel and the narrowed matrix settle one layout pass after the click.
        await Ui.Until(() => {
            var summary = Ui.Find<PlanSummaryView>("PlanSummary");
            var panel = Ui.Find<FrameworkElement>("SummaryLoadPanel");
            var matrix = Ui.Find<FrameworkElement>("SummaryMatrixScroll");
            var panelLeft = panel.TransformToVisual(summary).TransformPoint(new()).X;
            var matrixRight = matrix.TransformToVisual(summary).TransformPoint(new()).X + matrix.ActualWidth;
            return panel.Visibility == Visibility.Visible && panelLeft >= matrixRight - 2;
        });
        await Ui.Run(() => {
            var summary = Ui.Find<PlanSummaryView>("PlanSummary");
            var viewport = Ui.Find<ScrollViewer>("SummaryScroll");
            var panel = Ui.Find<FrameworkElement>("SummaryLoadPanel");
            var matrix = Ui.Find<FrameworkElement>("SummaryMatrixScroll");
            Assert.That(panel.Visibility, Is.EqualTo(Visibility.Visible));
            // The panel sits to the right of the table, not below the page.
            var panelLeft = panel.TransformToVisual(summary).TransformPoint(new()).X;
            var matrixRight = matrix.TransformToVisual(summary).TransformPoint(new()).X + matrix.ActualWidth;
            Assert.That(panelLeft, Is.GreaterThanOrEqualTo(matrixRight - 2));
            // The clicked cell stays selected and inside the viewport; the page does not jump to it.
            Assert.That(AutomationProperties.GetItemStatus(cell), Is.EqualTo("選択中"));
            var cellTop = cell.TransformToVisual(viewport).TransformPoint(new()).Y;
            var cellLeft = cell.TransformToVisual(viewport).TransformPoint(new()).X;
            Assert.That(cellTop, Is.GreaterThanOrEqualTo(0));
            Assert.That(cellTop + cell.ActualHeight, Is.LessThanOrEqualTo(viewport.ActualHeight));
            Assert.That(cellLeft, Is.GreaterThanOrEqualTo(0));
            Assert.That(cellLeft + cell.ActualWidth, Is.LessThanOrEqualTo(viewport.ActualWidth));
            Assert.That(viewport.VerticalOffset, Is.Zero, "The drill-down is not a jump to the bottom of the page.");
            Assert.That(Ui.Tree(summary).OfType<FrameworkElement>().Any(e => AutomationProperties.GetAutomationId(e) == "SummaryTasks"), Is.False);
            // Person, month and the three measures, 最大 with its date.
            Assert.That(Ui.Find<TextBlock>("SummaryLoadTitle").Text, Is.EqualTo("person-U7 · 2027/1"));
            Assert.That(Ui.Find<TextBlock>("SummaryLoadPercent").Text, Does.Contain("131%"));
            Assert.That(Ui.Find<TextBlock>("SummaryLoadOverDays").Text, Is.EqualTo("上限を超える日 9日"));
            Assert.That(Ui.Find<TextBlock>("SummaryLoadMax").Text, Does.Contain("325%").And.Contain("1/20"));
            // A day-by-day bar with its cap line, and the overloaded days marked.
            Assert.That(Ui.Find<Border>("SummaryLoadCapLine"), Is.Not.Null);
            var bars = Ui.Tree(Ui.Find<FrameworkElement>("SummaryLoadDays")).OfType<Border>()
                .Where(b => AutomationProperties.GetAutomationId(b).StartsWith("SummaryLoadDay_")).ToArray();
            var critical = PlanSheetView.Brush("SystemFillColorCriticalBrush");
            Assert.That(bars.Length, Is.GreaterThanOrEqualTo(9));
            Assert.That(bars.Count(b => ReferenceEquals(b.Background, critical)), Is.GreaterThanOrEqualTo(9));
            // The month's tasks with their state, dates and 残.
            Assert.That(Ui.Tree(Ui.Find<FrameworkElement>("SummaryLoadTasks")).OfType<TextBlock>()
                .Count(t => AutomationProperties.GetAutomationId(t).StartsWith("SummaryLoadTask_")) , Is.GreaterThan(0));
            Assert.That(Ui.Find<Button>("SummaryPlanOpen"), Is.Not.Null);
            // Another cell updates the same panel.
            Ui.Click("SummaryMonth_U7_4");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("SummaryLoadTitle").Text == "person-U7 · 2027/2");
        await Ui.Run(() => Ui.Click("SummaryPlanOpen"));
        await Ui.Idle();
        await Ui.Run(() => {
            var sheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            Assert.That(sheet.RowIds.Count(id => id.Length > 0), Is.GreaterThan(0));
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Does.EndWith(" 件"));
        });
    }

    [Test]
    public async Task SummaryPersonNameOpensThePlanFilteredToThatPerson()
    {
        await SummaryPlan();
        await Ui.Run(() => Ui.Click("SummaryPerson_U1"));
        await Ui.Idle();
        await Ui.Run(() => {
            var sheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            Assert.That(sheet.RowIds.Where(id => id.Length > 0), Is.EqualTo(new[] { "I1", "I2" }));
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("2 件"));
        });
    }

    [Test]
    public async Task SummaryFinishedPhaseCellsReadDashesWithUnitsAndNoWarningGlyphOnZero()
    {
        readDay = new(2026, 10, 5);
        FakePlanEditor.Save(root, new([
            new(new("I1", "Finished SA", "acme/repo") { Estimate = 8, Actual = 8, Remaining = 0, Closed = true, CloseDate = readDay,
                Assignees = ["U1"], Start = readDay.AddDays(-3), End = readDay.AddDays(-1) }, "", true) { Phase = "SA" },
            new(new("I2", "Zero remaining open", "acme/repo") { Estimate = 8, Actual = 0, Remaining = 0, Assignees = ["U1"], Start = readDay }, "", true) { Phase = "PG" },
            new(new("I3", "Open", "acme/repo") { Estimate = 8, Actual = 0, Remaining = 8, Assignees = ["U1"], Start = readDay }, "", true) { Phase = "PG" }], 4) { Phases = ["SA", "PG"] });
        await Open("Finished SA");
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            StatusDate = readDay, ProjectStart = readDay, ProjectEnd = new(2026, 10, 30) }), readDay);
        await Ui.Run(() => Ui.Click("PlanShowSummary")); await Ui.Ready<Button>("SummaryCell_U1_0");
        // A finished cell reads — for every measure and says 完了 in its accessible name; no 0 leaks.
        foreach (var measure in Enumerable.Range(0, 4)) {
            await Ui.Run(() => Ui.Find<ComboBox>("SummaryMeasure").SelectedIndex = measure);
            await Ui.Until(() => Ui.Tree(Ui.Find<Button>("SummaryCell_U1_0")).OfType<TextBlock>().Any(t => t.Text.Length > 0));
            await Ui.Run(() => {
                var finished = Ui.Find<Button>("SummaryCell_U1_0");
                Assert.That(((TextBlock)Ui.Tree(finished).OfType<TextBlock>().Single()).Text, Is.EqualTo("—"), $"measure {measure}");
                if (measure == 0) Assert.That(AutomationProperties.GetName(finished), Does.Contain("完了"));
            });
        }
        // Effort cells carry h, the overdue measure carries 件, and a zero value carries no glyph.
        await Ui.Run(() => Ui.Find<ComboBox>("SummaryMeasure").SelectedIndex = 0);
        await Ui.Until(() => Ui.Tree(Ui.Find<Button>("SummaryCell_U1_1")).OfType<TextBlock>().Any(t => t.Text.Length > 0));
        await Ui.Run(() => Assert.That(((TextBlock)Ui.Tree(Ui.Find<Button>("SummaryCell_U1_1")).OfType<TextBlock>().Single()).Text, Does.EndWith(" h")));
        await Ui.Run(() => Ui.Find<ComboBox>("SummaryMeasure").SelectedIndex = 3);
        await Ui.Until(() => Ui.Tree(Ui.Find<Button>("SummaryCell_U1_1")).OfType<TextBlock>().Any(t => t.Text.Length > 0));
        await Ui.Run(() => {
            var overdue = (TextBlock)Ui.Tree(Ui.Find<Button>("SummaryCell_U1_1")).OfType<TextBlock>().Single();
            Assert.That(overdue.Text, Is.EqualTo("0 件"));
            // No row in the person matrix shows a warning glyph; the day count and colour carry the state.
            var matrix = Ui.Find<FrameworkElement>("SummaryMatrixRows");
            Assert.That(Ui.Tree(matrix).OfType<TextBlock>().Any(t => t.Text.Contains('⚠')), Is.False);
            Assert.That(Ui.Tree(matrix).OfType<FontIcon>().Any(i => i.Glyph is "\uE7BA" or "\uE814"), Is.False);
        });
    }

    [Test]
    public async Task SummaryMatrixHeaderStaysVisibleWhileItsRowsScroll()
    {
        readDay = new(2026, 10, 5);
        // 25 people make the matrix taller than its viewport, so its body scrolls under a fixed header.
        FakePlanEditor.Save(root, new(Enumerable.Range(1, 25).Select(i => new PlanFakeIssue(
            new("I" + i, "Load task " + i, "acme/repo") { Estimate = 8, Actual = 0, Remaining = 8, Assignees = ["U" + i.ToString("00")], Start = readDay }, "", true) { Phase = "SA" }).ToImmutableArray(), 26) { Phases = ["SA"] });
        await Open("Load task 1");
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            StatusDate = readDay, ProjectStart = readDay, ProjectEnd = new(2026, 10, 30) }), readDay);
        await Ui.Run(() => { ResizeWorkspace(1920, 1032); Ui.Click("PlanShowSummary"); });
        await Ui.Ready<Button>("SummaryCell_U01_0"); await Ui.Idle();
        await Ui.Until(() => Ui.Find<HyperlinkButton>("SummaryPerson_U25").IsLoaded);
        await Ui.Until(() => Ui.Find<ScrollViewer>("SummaryMatrixBody").ScrollableHeight > 0);
        var headerBefore = 0d;
        await Ui.Run(() => {
            var summary = Ui.Find<PlanSummaryView>("PlanSummary");
            var body = Ui.Find<ScrollViewer>("SummaryMatrixBody");
            var header = Ui.Find<FrameworkElement>("SummaryMatrixHeader");
            headerBefore = header.TransformToVisual(summary).TransformPoint(new()).Y;
            Assert.That(body.ChangeView(null, 160, null, true), Is.True);
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("SummaryMatrixBody").VerticalOffset > 0);
        await Ui.Run(() => {
            var summary = Ui.Find<PlanSummaryView>("PlanSummary");
            var body = Ui.Find<ScrollViewer>("SummaryMatrixBody");
            var header = Ui.Find<FrameworkElement>("SummaryMatrixHeader");
            var top = header.TransformToVisual(summary).TransformPoint(new()).Y;
            Assert.That(top, Is.EqualTo(headerBefore).Within(1), "The header stays where it was while the rows scroll.");
            Assert.That(top, Is.GreaterThanOrEqualTo(0));
            Assert.That(top + header.ActualHeight, Is.LessThanOrEqualTo(summary.ActualHeight));
            Assert.That(body.VerticalOffset, Is.GreaterThan(0));
        });
    }
}




