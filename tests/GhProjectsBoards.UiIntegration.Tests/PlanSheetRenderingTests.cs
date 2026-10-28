using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using NUnit.Framework;
using Path = System.IO.Path;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanSheetHostedTests
{
    [Test, Category("SheetRowPresentation")]
    public async Task ScrollingAwayAndBackKeepsTheCurrentEstimateAndUndoRemovesItsOverrun()
    {
        await Edit(1, PlanField.Remaining, "16");
        await SelectFocusedCell(2, PlanField.Title);
        await Ui.Run(() => sheet.List.ScrollIntoView("I75"));
        await Ui.Until(() => sheet.Realized.Any(row => row.IsLoaded && row.Identity == "I75"
            && row.Cells.Single(cell => cell.Field == PlanField.Title).Text == "Task 75"));
        await Ui.Run(() => sheet.List.ScrollIntoView("I1"));
        await Ui.Until(() => sheet.Realized.Any(row => row.IsLoaded && row.Identity == "I1"
            && row.Cells.Single(cell => cell.Field == PlanField.Title).Text == "Task 1"));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanEstimateDisplay1").Text, Is.EqualTo("8h +8h"));
            Assert.That(AutomationProperties.GetName(Ui.Find<PlanSheetCell>("PlanCell1_Estimate")),
                Does.Contain("見積超過: 見込 16h（見積 8h）"));
        });
        await Ui.Run(async () => { await session.Undo(Today); sheet.Refresh(); });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanEstimateDisplay1").Text == "8h");
        await Ui.Run(() => {
            var cell = Ui.Find<PlanSheetCell>("PlanCell1_Estimate");
            Assert.That(AutomationProperties.GetName(cell), Does.Not.Contain("見積超過"));
            Assert.That(ToolTipService.GetToolTip(cell), Is.EqualTo("見積工数 · 8h"));
            Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(8));
        });
    }

    [TestCase(false), TestCase(true), Category("SheetRowPresentation")]
    public async Task OrdinaryCellTextSharesTitleAndAssigneeVerticalCentre(bool editing)
    {
        await SelectFocusedCell(1, PlanField.Estimate);
        if (editing) await Ui.Run(() => Ui.Find<PlanSheetCell>("PlanCell1_Estimate").BeginEditing());
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            var estimate = Ui.Find<PlanSheetCell>("PlanCell1_Estimate");
            var title = Ui.Find<TextBlock>("PlanTitleDisplay1");
            var assignee = Ui.Find<TextBlock>("PlanAssigneeName1");
            Assert.That(estimate.Editing, Is.EqualTo(editing));
            Assert.That(title.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(TextCentre(assignee), Is.EqualTo(TextCentre(title)).Within(1));
            // Not editing, the overlay is the visible estimate. Editing shows the text host again.
            var estimateCentre = editing ? EditorTextCentre(estimate) : TextCentre(estimate.EstimateDisplay!);
            Assert.That(estimateCentre, Is.EqualTo(TextCentre(title)).Within(1), "Estimate text must share the display overlays' vertical centre.");
        });
    }

    [TestCase(PlanField.Title), TestCase(PlanField.Assignees), TestCase(PlanField.Remaining), TestCase(PlanField.Actual), TestCase(PlanField.Start), TestCase(PlanField.End), Category("SheetRowPresentation")]
    public async Task DisplayOverlaySwitchToEditorPreservesVerticalTextCentre(string field)
    {
        await SelectFocusedCell(1, field);
        double centre = 0;
        await Ui.Run(() => {
            var cell = Ui.Find<PlanSheetCell>($"PlanCell1_{field}");
            centre = field is PlanField.Title or PlanField.Assignees
                ? TextCentre(field == PlanField.Title ? cell.TitleDisplay! : cell.AssigneeNameDisplay!)
                : TextCentre(Ui.Tree((FrameworkElement)cell.Parent).OfType<TextBlock>().Single(t => t.Text == cell.Text && t.Visibility == Visibility.Visible));
            cell.BeginEditing();
        });
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            var cell = Ui.Find<PlanSheetCell>($"PlanCell1_{field}");
            Assert.That(cell.Editing, Is.True);
            Assert.That(cell.Opacity, Is.EqualTo(1));
            if (field is PlanField.Title or PlanField.Assignees)
                Assert.That((field == PlanField.Title ? (FrameworkElement)cell.TitleDisplay! : cell.AssigneeDisplay!).Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(EditorTextCentre(cell), Is.EqualTo(centre).Within(1), "Entering edit mode must not move text vertically.");
        });
    }

    [Test, Category("SheetRowPresentation")]
    public async Task RequirementCellTextSharesItsRowTitleVerticalCentre()
    {
        var parent = new PlanRow("I1", "Requirement", "acme/repo");
        var child = new PlanRow("I2", "Task", "acme/repo") { Parent = "I1", Estimate = 8, Remaining = 8, Assignees = ["U1"] };
        await MountPresentation([parent, child], [parent, child]);
        await SelectFocusedCell(1, PlanField.Estimate);
        await Ui.Run(() => {
            var title = Ui.Find<TextBlock>("PlanTitleDisplay1");
            var estimate = Ui.Find<PlanSummaryCell>("PlanCell1_Estimate");
            Assert.That(estimate.Text.Text, Is.Not.Empty);
            Assert.That(TextCentre(estimate.Text), Is.EqualTo(TextCentre(title)).Within(1.5), "Summary text may round one physical pixel differently from the title overlay.");
            Assert.That(TextCentre(estimate.Text), Is.EqualTo(TextCentre(estimate)).Within(1), "Requirement text stays centred in its row.");
        });
    }

    private double TextCentre(FrameworkElement element)
    {
        Assert.That(element.ActualHeight, Is.GreaterThan(0), "Text must be laid out before comparing centres.");
        return element.TransformToVisual(sheet).TransformPoint(new(0, element.ActualHeight / 2)).Y * sheet.XamlRoot.RasterizationScale;
    }

    private double EditorTextCentre(PlanSheetCell cell)
    {
        Assert.That(cell.Text, Is.Not.Empty);
        var content = Ui.Tree(cell).OfType<ScrollViewer>().Single(v => v.Name == "ContentElement");
        var text = (FrameworkElement)content.Content;
        Assert.That(Ui.Tree(content), Does.Contain(text), "Measure the native text element inside the rendered template.");
        Assert.That(text.DesiredSize.Height, Is.GreaterThan(0));
        // The native text view can stretch to the viewport; its desired height is the single text line.
        return text.TransformToVisual(sheet).TransformPoint(new(0, text.DesiredSize.Height / 2)).Y * sheet.XamlRoot.RasterizationScale;
    }

    [Test, Category("PlanSheetPerformance")]
    public async Task VersionRowsCommitToRenderedDatesAndBarsRecordsTwentySamples()
    {
        Assert.That(session.Document.State.Rows.Length, Is.EqualTo(1040));
        await Ui.Run(() => Ui.Window.AppWindow.Resize(new(1920, 1080)));
        await Ui.Until(() => Ui.Root.ActualWidth * sheet.XamlRoot.RasterizationScale > 1850);
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "日");
        await Ui.Idle();
        var target = session.Document.State.Rows.First(r => r.Parent is not null && !r.Closed && r.Remaining > 0);
        var number = session.Document.State.Rows.IndexOf(target) + 1;
        await Select(number, PlanField.Remaining);
        await Ui.Ready<TextBox>($"PlanCell{number}_Remaining");
        for (var i = 0; i < 20; i++)
        {
            var remaining = target.Remaining!.Value + (i % 2 == 0 ? 8 : 0);
            await Edit(number, PlanField.Remaining, remaining.ToString(CultureInfo.InvariantCulture));
            await Ui.Until(() => File.Exists(metricsPath) && File.ReadAllLines(metricsPath!).Count(line => line.Contains("\"boundary\":\"rendered\"")) == i + 1);
            await Ui.Run(() => {
                Assert.That(session.Document.State.Rows[number - 1].Remaining, Is.EqualTo(remaining));
                var end = Ui.Find<TextBox>($"PlanCell{number}_End");
                var bar = Ui.Find<Shape>("PlanBar" + number);
                Assert.That(end.Text, Is.Not.Empty);
                var endDate = Regex.Match(AutomationProperties.GetName(end), @"\d{4}-\d{2}-\d{2}").Value;
                Assert.That(endDate, Is.Not.Empty);
                Assert.That(AutomationProperties.GetName(bar), Does.Contain(endDate));
                Assert.That(bar.Width, Is.GreaterThan(0));
            });
        }
        var records = File.ReadAllLines(metricsPath!).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var samples = records.Where(r => r.GetProperty("boundary").GetString() == "rendered").Select(r => r.GetProperty("elapsedMs").GetDouble()).Order().ToArray();
        Assert.That(samples.Length, Is.EqualTo(20));
        Assert.That(records.Length, Is.EqualTo(20), "Rejected, superseded and missing frames are not valid timing samples.");
        var evidence = Environment.GetEnvironmentVariable("GHPB_PLAN_EVIDENCE") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(evidence);
        File.Copy(metricsPath!, Path.Combine(evidence, "plan-version-frames.jsonl"), true);
        double clientWidth = 0, clientHeight = 0, scale = 0;
        await Ui.Run(() => { clientWidth = Ui.Root.ActualWidth; clientHeight = Ui.Root.ActualHeight; scale = sheet.XamlRoot.RasterizationScale; });
        var summary = JsonSerializer.Serialize(new { version = EvaluationFixture.DefaultVersion, rows = 1040, samples = samples.Length,
            target.Identity, number, medianMs = (samples[9] + samples[10]) / 2, maxMs = samples[^1], targetMs = 200,
            withinTarget = samples.All(ms => ms <= 200), clientWidth, clientHeight, scale,
            environment = Environment.OSVersion.ToString(),
            boundary = "Version-fixture cell commit through Core acceptance and visible dates/bars to CompositionTarget.Rendered; not physical display latency" });
        File.WriteAllText(Path.Combine(evidence, "plan-version-measurement.json"), summary);
        TestContext.Out.WriteLine(summary);
        await Ui.Run(async () => await RenderedEvidence.Capture(sheet, "sheet-version-edit"));
    }
}
