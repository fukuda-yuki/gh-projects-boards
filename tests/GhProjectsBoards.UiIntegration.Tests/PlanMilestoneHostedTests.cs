using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanWorkspaceHostedTests
{
    private async Task MilestonePlan(bool sameDate = false)
    {
        readDay = new(2026, 10, 5);
        FakePlanEditor.Save(root, new([
            new(new("I1", "Late", "acme/repo") { MilestoneTitle = "Design", Phase = "SA", Start = readDay, End = readDay.AddDays(4) }, "", true) { Phase = "SA" },
            new(new("I2", "On time", "acme/repo") { MilestoneTitle = sameDate ? "Review" : "Design", Phase = "SA", Start = readDay, End = sameDate ? readDay.AddDays(3) : readDay }, "", true) { Phase = "SA" }], 3) {
                Phases = ["SA"], Milestones = [new("M1", "acme/repo", "Design", readDay.AddDays(1)), new("M2", "acme/repo", sameDate ? "Review" : "Empty", sameDate ? readDay.AddDays(1) : readDay.AddDays(40))] });
        await Open("Late");
    }

    private async Task ReadyMilestone(string id)
    {
        await Ui.Ready<Button>(id);
        await Ui.Until(() => {
            var marker = Ui.Find<Button>(id);
            return marker.IsLoaded && marker.IsEnabled && marker.ActualWidth > 0 && marker.ActualHeight > 0;
        });
        await Ui.Run(() => {
            var marker = Ui.Find<Button>(id);
            var header = Ui.Find<Canvas>("PlanGanttHeader");
            var bounds = marker.TransformToVisual(header).TransformBounds(new Windows.Foundation.Rect(0, 0, marker.ActualWidth, marker.ActualHeight));
            Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(0));
            Assert.That(bounds.Right, Is.LessThanOrEqualTo(header.ActualWidth));
            Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0));
            Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(header.ActualHeight));
        });
    }

    [TestCase(1), TestCase(3)]
    public async Task MilestoneDateCoordinateAndInvocationUseExactLateSetWithoutEditing(int scale)
    {
        await MilestonePlan();
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedIndex = scale);
        await Ui.Idle();
        await ReadyMilestone("PlanMilestone_0");
        var before = PlanJson.Text(workspace.Session!.Document); var undo = workspace.Session.UndoCount;
        var unpublished = workspace.Session.Changes(readDay).TaskCount;
        await Ui.Run(() => {
            var sheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            var columnHeaders = Ui.Tree(view).OfType<TextBlock>().Where(t => AutomationProperties.GetAutomationId(t).StartsWith("PlanHeader")).ToArray();
            Assert.That(columnHeaders, Is.Not.Empty);
            foreach (var header in columnHeaders) {
                Assert.That(header.ActualHeight, Is.GreaterThan(0));
                for (var parent = VisualTreeHelper.GetParent(header); parent is FrameworkElement ancestor; parent = VisualTreeHelper.GetParent(parent)) {
                    if (ancestor.Clip is not RectangleGeometry clip) continue;
                    var bounds = header.TransformToVisual(ancestor).TransformBounds(new Windows.Foundation.Rect(0, 0, header.ActualWidth, header.ActualHeight));
                    Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(clip.Rect.Top - .1), header.Text);
                    Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(clip.Rect.Bottom + .1), header.Text);
                }
            }
            var marker = Ui.Find<Button>("PlanMilestone_0");
            Assert.That(Canvas.GetLeft(marker) + marker.Width / 2, Is.EqualTo(sheet.X(readDay.AddDays(1))).Within(.1));
            Assert.That(AutomationProperties.GetName(marker), Does.Contain("+3日"));
            Assert.That(Ui.Find<TextBlock>("PlanMilestoneLabel_0").Text, Does.Contain("+3日"));
            Assert.That(Ui.Find<Microsoft.UI.Xaml.Shapes.Line>("PlanMilestoneLine_0").X1, Is.EqualTo(sheet.X(readDay.AddDays(1))).Within(.1));
            Assert.That(((SolidColorBrush)marker.Foreground).Color, Is.EqualTo(((SolidColorBrush)PlanSheetView.Brush("SystemFillColorCriticalBrush")).Color));
            Assert.That(marker.Focus(FocusState.Keyboard), Is.True);
            if (scale == 3) Assert.That(sheet.X(readDay.AddDays(40)), Is.InRange(0, sheet.ChartViewport));
        });
        await ReadyMilestone("PlanMilestone_0");
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("PlanMilestone_0").FocusState, Is.EqualTo(FocusState.Keyboard));
            Ui.Click("PlanMilestone_0");
        });
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<PlanSheetView>().Single().RowIds.Where(i => i.Length > 0), Is.EqualTo(new[] { "I1" }));
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("1 件"));
        });
        Assert.That(PlanJson.Text(workspace.Session.Document), Is.EqualTo(before));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        Assert.That(workspace.Session.Changes(readDay).TaskCount, Is.EqualTo(unpublished));
    }

    [TestCase(0, "Design", "I1", "+3日"), TestCase(1, "Review", "I2", "+2日")]
    public async Task SameDateMilestonesOfferEachKeyboardReachableSelection(int index, string title, string row, string difference)
    {
        await MilestonePlan(sameDate: true);
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedIndex = 3);
        await Ui.Idle();
        await ReadyMilestone("PlanMilestone_0");
        var session = workspace.Session!; var before = PlanJson.Text(session.Document);
        var undo = session.UndoCount; var unpublished = session.Changes(readDay).TaskCount;
        await Ui.Run(() => {
            var markers = Ui.Tree(view).OfType<Button>().Where(b => AutomationProperties.GetAutomationId(b).StartsWith("PlanMilestone_")).ToArray();
            Assert.That(markers, Has.Length.EqualTo(1));
            Assert.That(markers[0].Focus(FocusState.Keyboard), Is.True);
        });
        await ReadyMilestone("PlanMilestone_0");
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("PlanMilestone_0").FocusState, Is.EqualTo(FocusState.Keyboard));
            Ui.Click("PlanMilestone_0");
        });
        await Ui.Ready<MenuFlyoutItem>("PlanMilestoneChoice_" + index);
        await Ui.Run(() => {
            var choice = Ui.Find<MenuFlyoutItem>("PlanMilestoneChoice_" + index);
            Assert.That(choice.Text, Does.Contain(title).And.Contain("完了見込 10/").And.Contain(difference));
            Assert.That(choice.Focus(FocusState.Keyboard), Is.True);
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(choice).GetPattern(PatternInterface.Invoke)).Invoke();
        });
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Tree(view).OfType<PlanSheetView>().Single().RowIds.Where(i => i.Length > 0), Is.EqualTo(new[] { row })));
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
        Assert.That(session.Changes(readDay).TaskCount, Is.EqualTo(unpublished));
    }

    [Test]
    public async Task PhaseMappingControlIsOneUndoStepAndDoesNotReleaseHeldDates()
    {
        await MilestonePlan();
        var session = workspace.Session!; var undo = session.UndoCount;
        var schedule = session.Schedule(readDay).Select(t => (t.Start, t.End)).ToArray();
        var unpublished = session.Changes(readDay).TaskCount;
        await Ui.Run(() => Ui.Click("PlanShowSettings"));
        await Ui.Ready<ComboBox>("PlanPhaseMilestone_SA");
        await Ui.Run(() => Ui.Find<ComboBox>("PlanPhaseMilestone_SA").SelectedIndex = 1);
        await Ui.Until(() => session.Document.State.Settings.PhaseMilestones.GetValueOrDefault("SA") == "Design");
        Assert.That(session.UndoCount, Is.EqualTo(undo + 1));
        Assert.That(session.Schedule(readDay).Select(t => (t.Start, t.End)), Is.EqualTo(schedule));
        Assert.That(session.Changes(readDay).TaskCount, Is.EqualTo(unpublished));
        await Ui.Run(() => Ui.Click("PlanUndo")); await Ui.Idle();
        Assert.That(session.Document.State.Settings.PhaseMilestones, Is.Empty);
        await Ui.Run(() => Assert.That(Ui.Find<ComboBox>("PlanPhaseMilestone_SA").SelectedIndex, Is.Zero));
    }

    [Test]
    public async Task EmptyMilestoneLeavesCurrentFilterAndExplainsThereAreNoLateTasks()
    {
        await MilestonePlan();
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedIndex = 3); await Ui.Idle();
        await ReadyMilestone("PlanMilestone_0");
        await Ui.Run(() => Ui.Click("PlanMilestone_0")); await Ui.Idle();
        await ReadyMilestone("PlanMilestone_1");
        await Ui.Run(() => Ui.Click("PlanMilestone_1")); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<PlanSheetView>().Single().RowIds.Where(i => i.Length > 0), Is.EqualTo(new[] { "I1" }));
            Assert.That(Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("Empty · 期日を過ぎるタスクはありません")), Is.True);
        });
    }

    [Test]
    public async Task NoMilestonesAddsNoChartOrSummaryAndOnlyNoneInSettings()
    {
        await SummaryPlan();
        await Ui.Run(() => Assert.That(Ui.Tree(view).Any(e => AutomationProperties.GetAutomationId(e) == "SummaryMilestoneTable"), Is.False));
        await Ui.Run(() => Ui.Click("PlanShowTasks")); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<Canvas>("PlanGanttHeader").Height, Is.EqualTo(48));
            Assert.That(Ui.Tree(view).Any(e => AutomationProperties.GetAutomationId(e).StartsWith("PlanMilestone_")), Is.False);
            Ui.Click("PlanShowSettings");
        });
        await Ui.Ready<ComboBox>("PlanPhaseMilestone_SA");
        await Ui.Run(() => Assert.That(Ui.Find<ComboBox>("PlanPhaseMilestone_SA").Items.Cast<string>(), Is.EqualTo(new[] { "なし" })));
    }

    [Test]
    public async Task SummaryShowsMilestonesAndMappedPhaseTargetsAndLateDetails()
    {
        await MilestonePlan();
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            ProjectStart = readDay, ProjectEnd = readDay.AddDays(30), PhaseMilestones = ImmutableDictionary<string, string>.Empty.Add("SA", "Design") }), readDay);
        await Ui.Run(() => Ui.Click("PlanShowSummary"));
        await Ui.Ready<Button>("SummaryMilestone_0");
        await Ui.Run(() => {
            Assert.That(AutomationProperties.GetName(Ui.Find<Grid>("SummaryPhaseRow_0")), Does.Contain("目標日").And.Contain("差 +3日"));
            Assert.That(Ui.Find<FrameworkElement>("SummaryPhaseTarget_0"), Is.Not.Null);
            Ui.Click("SummaryMilestone_0");
        });
        await Ui.Ready<Button>("SummaryFilter");
        await Ui.Run(() => Assert.That(AutomationProperties.GetName(Ui.Find<Button>("SummaryFilter")), Is.EqualTo("この 1 タスクで計画を絞り込む")));
    }
}
