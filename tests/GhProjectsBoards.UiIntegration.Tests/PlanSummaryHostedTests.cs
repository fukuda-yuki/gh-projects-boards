using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;
namespace GhProjectsBoards.UiIntegration.Tests;
internal sealed partial class PlanWorkspaceHostedTests
{
    private async Task SummaryPlan()
    {
        readDay = new(2026, 10, 5);
        FakePlanEditor.Save(root, new([
            new(new("I1", "設計 A", "acme/repo") { Estimate = 16, Remaining = 16, Actual = 4, Assignees = ["U1"] }, "", true) { Phase = "SA" },
            new(new("I2", "設計 B", "acme/repo") { Estimate = 8, Remaining = 8, Actual = 0, Assignees = ["U1"] }, "", true) { Phase = "SA" },
            new(new("I3", "実装", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U2"] }, "", true) { Phase = "PG" }], 4) { Phases = ["SA", "PG"] });
        await Open("設計 A");
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            ProjectStart = readDay, ProjectEnd = new(2026, 10, 30), StatusDate = readDay }), readDay);
        await Ui.Run(() => Ui.Click("PlanShowSummary")); await Ui.Ready<TextBlock>("SummaryProgress");
    }
    [Test]
    public async Task SummaryBandMeasuresAndPhaseTotalsAreReadOnly()
    {
        await SummaryPlan();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("SummaryRequiredLoad").Text, Does.Contain("32h"));
            Assert.That(Ui.Find<TextBlock>("SummaryProgress").Text, Does.Contain("36h"));
            Assert.That(AutomationProperties.GetName(Ui.Find<Grid>("SummaryPhaseRow_0")), Does.Contain("残 h 24").And.Contain("見込 h 28"));
            Assert.That(AutomationProperties.GetName(Ui.Find<Grid>("SummaryPhaseRow_2")), Does.Contain("残 h 32").And.Contain("見込 h 36"));
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("SummaryPhase_0")), Does.Contain("SA"));
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("SummaryPhaseTotal")), Does.Contain("全体"));
            var summary = Ui.Find<PlanSummaryView>("PlanSummary");
            Assert.That(Ui.Tree(summary).OfType<TextBox>(), Is.Empty);
            Assert.That(Ui.Tree(summary).OfType<NumberBox>(), Is.Empty);
            Assert.That(Ui.Tree(summary).OfType<CalendarDatePicker>(), Is.Empty);
        });
    }
    [Test]
    public async Task SummaryMeasureSelectionAndExactFilterDoNotChangeThePlan()
    {
        await SummaryPlan();
        var before = PlanJson.Text(workspace.Session!.Document); var undo = workspace.Session.UndoCount;
        await Ui.Run(() => {
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("SummaryCell_U1_0")), Does.Contain("24"));
            Ui.Find<ComboBox>("SummaryMeasure").SelectedIndex = 1;
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("SummaryCell_U1_0")), Does.Contain("28"));
            Ui.Click("SummaryCell_U1_0");
            Assert.That(Ui.Tree(Ui.Find<FrameworkElement>("SummaryTasks")).OfType<TextBlock>().Count(t => AutomationProperties.GetAutomationId(t).StartsWith("SummaryTaskId_")), Is.EqualTo(2));
            Ui.Click("SummaryFilter");
        });
        await Ui.Idle();
        await Ui.Run(() => {
            var sheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            Assert.That(sheet.RowIds.Where(id => id.Length > 0), Is.EqualTo(new[] { "I1", "I2" }));
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("2 件"));
            Ui.Find<TextBox>("PlanSheetFilter").Text = "";
        });
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Tree(view).OfType<PlanSheetView>().Single().RowIds.Count(id => id.Length > 0), Is.EqualTo(3)));
        Assert.That(PlanJson.Text(workspace.Session.Document), Is.EqualTo(before));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
    }
    [Test]
    public async Task SummaryMonthShowsDailyOverloadAndItsContributors()
    {
        await SummaryPlan();
        await Ui.Run(() => {
            Ui.Find<ComboBox>("SummaryMode").SelectedIndex = 1;
            var cell = Ui.Find<Button>("SummaryMonth_U1_0");
            Assert.That(AutomationProperties.GetName(cell), Does.Contain("日超過"));
            Ui.Click("SummaryMonth_U1_0");
            Assert.That(Ui.Tree(Ui.Find<FrameworkElement>("SummaryTasks")).OfType<TextBlock>().Count(t => AutomationProperties.GetAutomationId(t).StartsWith("SummaryTaskId_")), Is.EqualTo(2));
        });
    }
    [Test]
    public async Task SummaryMissingPeriodAndMappingOfferSettings()
    {
        await Open();
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            Columns = workspace.Session.Document.State.Settings.Columns.Where(c => c.Role != PlanField.Phase).ToImmutableArray() }), readDay);
        await Ui.Run(() => Ui.Click("PlanShowSummary")); await Ui.Ready<Button>("SummaryPeriodSettings");
        await Ui.Run(() => { Assert.That(Ui.Find<TextBlock>("SummaryUnmapped").Text, Does.Contain("未設定")); Ui.Click("SummaryPhaseSettings"); });
        await Ui.Ready<TextBox>("PlanProjectEnd");
    }
    [Test]
    public async Task SummaryAttentionDirectlyFiltersWithoutUndoOrRemoteRequests()
    {
        await SummaryPlan();
        var before = PlanJson.Text(workspace.Session!.Document); var undo = workspace.Session.UndoCount;
        var calls = File.ReadAllText(Path.Combine(root, "calls.jsonl"));
        await Ui.Run(() => Ui.Click("SummaryOverEstimate")); await Ui.Idle();
        await Ui.Run(() => {
            var sheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            Assert.That(sheet.RowIds.Where(id => id.Length > 0), Is.EqualTo(new[] { "I1" }));
            Assert.That(sheet.IsSelectedRow("I1"), Is.True);
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("1 件"));
        });
        Assert.That(PlanJson.Text(workspace.Session.Document), Is.EqualTo(before));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        Assert.That(File.ReadAllText(Path.Combine(root, "calls.jsonl")), Is.EqualTo(calls));
    }
    [Test]
    public async Task SummaryMonthIncludesUnallocatedWorkWithItsReason()
    {
        readDay = new(2026, 10, 5);
        FakePlanEditor.Save(root, new([
            new(new("I1", "Known", "acme/repo") { Estimate = 8, Assignees = ["U1"] }, "", true) { Phase = "SA" },
            new(new("I2", "Unknown", "acme/repo") { Assignees = ["U1"] }, "", true) { Phase = "SA" }], 3) { Phases = ["SA"] });
        await Open("Known");
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            StatusDate = readDay, ProjectStart = readDay, ProjectEnd = new(2026, 10, 30) }), readDay);
        await Ui.Run(() => Ui.Click("PlanShowSummary")); await Ui.Ready<ComboBox>("SummaryMode");
        await Ui.Run(() => {
            Ui.Find<ComboBox>("SummaryMode").SelectedIndex = 1; Ui.Click("SummaryMonth_U1_0");
            Assert.That(Ui.Tree(Ui.Find<FrameworkElement>("SummaryTasks")).OfType<TextBlock>().Count(t => AutomationProperties.GetAutomationId(t).StartsWith("SummaryTaskId_")), Is.EqualTo(2));
        });
        await Ui.Ready<TextBlock>("SummaryTask_I2");
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("SummaryTask_I2").Text, Does.Contain("未配分: 見込み残存工数が未入力")));
    }
    [TestCase(1920, 1080), TestCase(1280, 720)]
    public async Task SummaryVersionScaleKeepsTablesInsideTheCardAndCachesTheSameDocument(int width, int height)
    {
        var plan = EvaluationFixture.ReadPlan();
        var snapshot = EvaluationFixture.Simulate(plan, [plan.StatusDate]).Single();
        Assert.That(snapshot.Rows.Length, Is.EqualTo(1040));
        readDay = snapshot.StatusDate;
        await PlanSession.CreateAsync(new(root), snapshot.Document, snapshot.StatusDate);
        await Open(snapshot.Rows[0].Title);
        await Ui.Run(() => { ResizeWorkspace(width, height); Ui.Click("PlanShowSummary"); });
        await Ui.Ready<Button>("SummaryPhaseTotal"); await Ui.Idle();
        await Ui.Run(async () => {
            var summary = Ui.Find<PlanSummaryView>("PlanSummary");
            var sheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            var band = Ui.Find<TextBlock>("SummaryProgress");
            summary.Refresh(sheet.Schedule, sheet.StatusDate);
            Assert.That(Ui.Find<TextBlock>("SummaryProgress"), Is.SameAs(band));
            Assert.That(Ui.Tree(summary).OfType<Button>().Count(b => AutomationProperties.GetAutomationId(b).StartsWith("SummaryPhase_")), Is.EqualTo(9));
            Assert.That(summary.ActualWidth, Is.LessThanOrEqualTo(width));
            if (width == 1920) {
                foreach (var element in new Microsoft.UI.Xaml.FrameworkElement[] { Ui.Find<Button>("SummaryPhaseTotal"), Ui.Find<ComboBox>("SummaryMeasure"), Ui.Tree(summary).OfType<Button>().First(b => AutomationProperties.GetAutomationId(b).StartsWith("SummaryCell_")) })
                    Assert.That(element.TransformToVisual(summary).TransformPoint(new()).Y + element.ActualHeight, Is.LessThan(summary.ActualHeight));
            }
            await RenderedEvidence.Capture(view, $"summary-{width}x{height}");
        });
    }

    [Test]
    public async Task SummaryRebuildsAfterSettingsChangeAndRetainsReadOnlyNavigation()
    {
        await SummaryPlan();
        await Ui.Run(() => Ui.Click("SummaryCell_U1_0"));
        await Settings();
        await CommitText("PlanProjectEnd", "2026-11-30");
        await Ui.Run(() => Ui.Click("PlanSettingsBack"));
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => AutomationProperties.GetAutomationId(t) == "SummaryStatusDate" && t.Text.Contains("11/30")));
        await Ui.Run(() => {
            Ui.Find<ComboBox>("SummaryMode").SelectedIndex = 1;
            Assert.That(Ui.Find<Button>("SummaryMonth_U1_1"), Is.Not.Null);
        });
    }

}
