using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
using Windows.System;
namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanWorkspaceHostedTests
{
    private async Task OpenVersionSummary()
    {
        var plan = EvaluationFixture.ReadPlan();
        var snapshot = EvaluationFixture.Simulate(plan, [plan.StatusDate]).Single();
        readDay = snapshot.StatusDate;
        await PlanSession.CreateAsync(new(root), snapshot.Document, snapshot.StatusDate);
        await Open(snapshot.Rows[0].Title);
        await Ui.Run(() => { ResizeWorkspace(1920, 1032); Ui.Click("PlanShowSummary"); });
        await Ui.Ready<Button>("SummaryPhaseTotal"); await Ui.Idle();
    }
    [Test]
    public async Task SummaryCellsLookReadOnlyAndSelectionRevealsDetailsWithPlanRowIds()
    {
        await SummaryPlan();
        await Ui.Run(() => {
            var cell = Ui.Find<Button>("SummaryCell_U1_0");
            Assert.That(((SolidColorBrush)cell.Background).Color.A, Is.Zero);
            Assert.That(((SolidColorBrush)cell.BorderBrush).Color.A, Is.Zero);
            Assert.That(cell.UseSystemFocusVisuals, Is.True);
            Assert.That(Ui.Find<Button>("SummaryPhase_0").HorizontalContentAlignment, Is.EqualTo(HorizontalAlignment.Left));
            Ui.Click("SummaryCell_U1_0");
            Assert.That(AutomationProperties.GetItemStatus(cell), Is.EqualTo("選択中"));
            Assert.That(cell.Background, Is.EqualTo(Application.Current.Resources["SheetSelectionBrush"]));
            Assert.That(cell.BorderBrush, Is.EqualTo(Application.Current.Resources["SheetSelectionStrokeBrush"]));
        });
        await Ui.Ready<TextBlock>("SummaryTaskId_I1"); await Ui.Idle();
        // The details are brought into view after their layout, at low dispatcher priority.
        await Ui.Until(() => {
            var viewport = Ui.Find<ScrollViewer>("SummaryScroll");
            var command = Ui.Find<Button>("SummaryFilter");
            var top = command.TransformToVisual(viewport).TransformPoint(new()).Y;
            return top >= 0 && top + command.ActualHeight <= viewport.ActualHeight;
        });
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("SummaryTaskId_I1").Text, Is.EqualTo("#1"));
            Assert.That(Ui.Find<TextBlock>("SummaryTaskId_I2").Text, Is.EqualTo("#2"));
            Ui.Find<ComboBox>("SummaryMeasure").SelectedIndex = 2;
            Assert.That(AutomationProperties.GetItemStatus(Ui.Find<Button>("SummaryCell_U1_0")), Is.EqualTo("選択中"));
        });
    }
    [Test]
    public async Task SummaryEmptyPhaseAndUnallocatedMonthDoNotShowAZero()
    {
        await SummaryPlan();
        await Ui.Run(() => {
            foreach (var measure in Enumerable.Range(0, 4)) {
                Ui.Find<ComboBox>("SummaryMeasure").SelectedIndex = measure;
                Assert.That(AutomationProperties.GetName(Ui.Find<Button>("SummaryCell_U2_0")), Does.EndWith(" –"));
            }
        });
        await Settings(); await PickDate("PlanProjectEnd", new(2026, 11, 30));
        await Ui.Until(() => workspace.Session!.Document.State.Settings.ProjectEnd == new DateOnly(2026, 11, 30));
        await Ui.Run(() => Ui.Click("PlanSettingsBack"));
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => AutomationProperties.GetAutomationId(t) == "SummaryStatusDate" && t.Text.Contains("11/30")));
        await Ui.Run(() => {
            Ui.Find<ComboBox>("SummaryMode").SelectedIndex = 1;
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("SummaryMonth_U1_1")), Does.EndWith(" –"));
        });
    }
    [Test]
    public async Task SummaryVersionUsesWholePercentsCompactTableMonthAxisAndNeutralCaption()
    {
        await OpenVersionSummary();
        await Ui.Run(() => {
            var summary = Ui.Find<PlanSummaryView>("PlanSummary");
            var table = Ui.Find<FrameworkElement>("SummaryPhaseTable");
            var timeline = Ui.Find<FrameworkElement>("SummaryTimeline");
            Assert.That(timeline.ActualWidth, Is.GreaterThan(650));
            Assert.That(timeline.TransformToVisual(summary).TransformPoint(new()).X - (table.TransformToVisual(summary).TransformPoint(new()).X + table.ActualWidth), Is.InRange(15, 17));
            Assert.That(Ui.Find<TextBlock>("SummaryTimelineStatusDate").Text, Does.StartWith("基準日"));
            Assert.That(Ui.Find<TextBlock>("SummaryTimelineEndDate").Text, Does.StartWith("終了日"));
            Assert.That(Ui.Tree(timeline).OfType<TextBlock>().Count(t => AutomationProperties.GetAutomationId(t).StartsWith("SummaryMonthLabel_")), Is.GreaterThanOrEqualTo(6));
            var caption = Ui.Find<TextBlock>("SummaryFinishCaption");
            Assert.That(caption.Foreground, Is.EqualTo(Application.Current.Resources["TextFillColorSecondaryBrush"]));
            // The card names the limit in a few words; the full basis is its tooltip, and the capacity formula
            // is the 稼働可能 h header's tooltip instead of a line above the table.
            Assert.That(caption.Text, Is.EqualTo("担当者の負荷は含まない"));
            Assert.That(ToolTipService.GetToolTip(Ui.Find<Border>("SummaryFinishCard")), Is.EqualTo("先行関係と見込み残存工数から計算（担当者の負荷は含まない）"));
            Assert.That(Ui.Tree(summary).OfType<TextBlock>().Any(t => t.Text.StartsWith("稼働可能 =")), Is.False);
            Assert.That(ToolTipService.GetToolTip(Ui.Tree(summary).OfType<TextBlock>().First(t => t.Text == "稼働可能 h")), Is.EqualTo(PlanSummaryView.CapacityFormula));
            Assert.That(Ui.Find<TextBlock>("SummaryRemainingDays").Text.Split('\n')[^1], Does.Match(@"^[\d,]+ / [\d,]+ 稼働日が経過$"));
            foreach (var text in Ui.Tree(summary).OfType<TextBlock>()) Assert.That(text.Text, Does.Not.Match(@"\d+\.\d+%"));
            foreach (var text in Ui.Tree(table).OfType<TextBlock>().Where(t => t.Text.Contains(" / "))) {
                Assert.That(text.TextWrapping, Is.EqualTo(TextWrapping.NoWrap));
                Assert.That(text.IsTextTrimmed, Is.False, text.Text);
            }
            var attention = Ui.Find<Button>("SummaryOverEstimate");
            Assert.That(attention.HorizontalAlignment, Is.EqualTo(HorizontalAlignment.Left));
            Assert.That(Ui.Tree(attention).OfType<FontIcon>().Single().Glyph, Is.EqualTo("\uE7BA"));
            Ui.Find<ComboBox>("SummaryMode").SelectedIndex = 1;
            foreach (var text in Ui.Tree(summary).OfType<TextBlock>()) Assert.That(text.Text, Does.Not.Match(@"\d+\.\d+%"));
        });
    }
    [TestCase(false, 1), TestCase(true, 1), TestCase(false, 10), TestCase(true, 10), Category("PlanSheetNative")]
    public async Task SummaryHorizontalTablesPreserveOrdinaryVerticalWheelSpeed(bool phases, int notches)
    {
        await OpenVersionSummary();
        ScrollViewer scroll = null!;
        await Ui.Run(() => scroll = Ui.Find<ScrollViewer>("SummaryScroll"));
        SheetNativeInput.Move(await SheetNativeInput.PointFor("SummaryStatusDate"));
        await Wheel();
        double ordinary = 0;
        await Ui.Run(() => { ordinary = scroll.VerticalOffset; Assert.That(ordinary, Is.GreaterThan(0)); scroll.ChangeView(null, 0, null, true); });
        await Ui.Until(() => scroll.VerticalOffset == 0); await Ui.Idle();
        Button target = null!;
        await Ui.Run(() => target = phases ? Ui.Find<Button>("SummaryPhase_0") : Ui.Tree(view).OfType<Button>().First(b => AutomationProperties.GetAutomationId(b).StartsWith("SummaryCell_")));
        SheetNativeInput.Move(await SheetNativeInput.PointFor(target));
        await Wheel();
        await Ui.Run(() => Assert.That(scroll.VerticalOffset, Is.EqualTo(ordinary).Within(2)));

        async Task Wheel()
        {
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<ScrollViewerViewChangedEventArgs> handler = (_, args) => { if (!args.IsIntermediate) settled.TrySetResult(); };
            await Ui.Run(() => scroll.ViewChanged += handler);
            try {
                await SheetNativeInput.ScrollWheel(-120);
                for (var i = 1; i < notches; i++) SheetNativeInput.Wheel(-120);
                await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { await Ui.Run(() => scroll.ViewChanged -= handler); }
        }
    }
    [TestCase("tab"), TestCase("close"), TestCase("scroll")]
    public async Task InvalidAllowanceRevealsItsPersonAndReturnsFocusWithoutAnEdit(string navigation)
    {
        await SummaryPlan(); await Settings(); await Ui.Ready<TextBox>("PlanAllowanceU1");
        var before = PlanJson.Text(workspace.Session!.Document); var undo = workspace.Session.UndoCount;
        if (navigation == "scroll") {
            await Ui.Run(() => {
                var input = Ui.Find<TextBox>("PlanAllowanceU1"); var scroll = Ui.Find<ScrollViewer>("PlanSettingsScroll");
                var top = input.TransformToVisual(scroll).TransformPoint(new()).Y;
                scroll.ChangeView(null, scroll.VerticalOffset + top - 100, null, true);
            });
            await Ui.Until(() => {
                var input = Ui.Find<TextBox>("PlanAllowanceU1"); var scroll = Ui.Find<ScrollViewer>("PlanSettingsScroll");
                var top = input.TransformToVisual(scroll).TransformPoint(new()).Y;
                return top >= 0 && top + input.ActualHeight <= scroll.ActualHeight;
            });
        }
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("PlanAllowanceU1"); Assert.That(input.Focus(FocusState.Programmatic), Is.True); input.Text = "invalid";
        });
        if (navigation == "close") await Ui.Run(async () => Assert.That(await view.StopAsync(), Is.False));
        else await Ui.Run(() => {
            if (navigation == "tab") Ui.Click("PlanShowSummary");
            else {
                var scroll = Ui.Find<ScrollViewer>("PlanSettingsScroll");
                var input = Ui.Find<TextBox>("PlanAllowanceU1");
                Assert.That(scroll.VerticalOffset + input.TransformToVisual(scroll).TransformPoint(new()).Y, Is.GreaterThan(scroll.ActualHeight), "Scrolling to the top must move this field outside the viewport.");
                Assert.That(scroll.ChangeView(null, 0, null, true), Is.True);
            }
        });
        await Ui.Until(() => {
            var input = Ui.Find<TextBox>("PlanAllowanceU1"); var scroll = Ui.Find<ScrollViewer>("PlanSettingsScroll");
            var top = input.TransformToVisual(scroll).TransformPoint(new()).Y;
            return Ui.Popup<Border>("SettingsInputProblem") is { IsLoaded: true } && top >= 0 && top + input.ActualHeight <= scroll.ActualHeight;
        }); await Ui.Idle();
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("PlanAllowanceU1");
            Assert.That(input.Text, Is.EqualTo("invalid")); Assert.That(input.FocusState, Is.Not.EqualTo(FocusState.Unfocused));
            var scroll = Ui.Find<ScrollViewer>("PlanSettingsScroll"); var top = input.TransformToVisual(scroll).TransformPoint(new()).Y;
            Assert.That(top, Is.GreaterThanOrEqualTo(0)); Assert.That(top + input.ActualHeight, Is.LessThanOrEqualTo(scroll.ActualHeight));
            var message = ((TextBlock)Ui.Popup<Border>("SettingsInputProblem")!.Child).Text;
            Assert.That(message, Does.Contain(AutomationProperties.GetName(input)).And.Contain("0以上"));
        });
        Assert.That(PlanJson.Text(workspace.Session.Document), Is.EqualTo(before)); Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        await Ui.Run(() => { Ui.Find<TextBox>("PlanAllowanceU1").Text = "80"; Ui.Click("PlanSettingsBack"); }); await Ui.Idle();
        Assert.That(workspace.Session.Document.State.Settings.People.Single(p => p.Identity == "U1").Allowance, Is.EqualTo(80));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + 1));
        await Ui.Run(() => Assert.That(Ui.Popup<Border>("SettingsInputProblem"), Is.Null));
    }
    [TestCase(true), TestCase(false), Category("PlanSheetNative")]
    public async Task SettingsOrdinaryEnterCommitsAndEscapeRestoresTheAcceptedAllowance(bool commit)
    {
        await SummaryPlan(); await Settings();
        var undo = workspace.Session!.UndoCount;
        await Ui.Run(() => Ui.Find<TextBox>("PlanAllowanceU1").StartBringIntoView(new() { AnimationDesired = false })); await Ui.Idle();
        await SheetNativeInput.Click("PlanAllowanceU1");
        await Ui.Run(() => Ui.Find<TextBox>("PlanAllowanceU1").Text = "80");
        await SheetNativeInput.Press(commit ? VirtualKey.Enter : VirtualKey.Escape); await Ui.Idle();
        Assert.That(workspace.Session.Document.State.Settings.People.Single(p => p.Identity == "U1").Allowance, Is.EqualTo(commit ? 80m : (decimal?)null));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + (commit ? 1 : 0)));
    }
}
