using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed partial class SummaryHostedTests
{
    private EditingGrid grid = null!;
    private DraftSession session = null!;
    private ProjectRegistration project = null!;
    private string root = null!;
    [SetUp]
    public async Task Setup()
    {
        var fixture = SummaryTests.Example(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(9))); project = fixture.Project; var work = fixture.Work;
        work.SetAllowance(project, "A", 160, work.Revision); work.SetAllowance(project, "B", 80, work.Revision);
        root = Path.Combine(Path.GetTempPath(), "ghpb-summary-ui-" + Guid.NewGuid().ToString("N"));
        session = new(new DraftStore(root), work, 0); Assert.That(await session.FlushAsync(), Is.True);
        await Ui.Run(() => { Ui.Window.AppWindow.Resize(new(1400, 1000)); grid = new EditingGrid(project, session, () => Task.FromResult(true)); });
        await Ui.Mount(grid); await Ui.Ready<FrameworkElement>("GridCell0_0"); await Ui.Idle();
    }
    [TearDown]
    public async Task Teardown()
    {
        await Ui.Run(() => { foreach (var id in new[] { "PlanningDialog", "DailyProgressDialog", "SummaryAllowanceDialog", "SummaryBaselineDialog" }) Ui.Dialog(id)?.Hide(); });
        await Ui.Unmount(grid); Assert.That(await session.FlushAsync(), Is.True); await Ui.Idle();
    }
    private static SelectorBar Views() => Ui.Find<SelectorBar>("ProjectViews");
    private async Task Open() { await Ui.Run(() => Views().SelectedItem = Views().Items[2]); await Ui.Ready<ListView>("SummaryPeople"); }

    [Test]
    public async Task WeeklyReportCommandCopiesTheDisplayedProjectToNativeClipboardWithoutChangingWork()
    {
        GhProjectsBoards.E2E.Tests.NativeClipboardScope saved = null!;
        await Ui.Run(() => saved = new());
        try
        {
            await Open();
            string expected = "";
            await Ui.Run(() => {
                Ui.Find<TextBox>("SummaryFilter").Text = "no matching task";
                expected = WeeklySummaryReport.Create(Ui.Find<SummaryView>("SummaryView").AdoptedSummary!);
            });
            var before = System.Text.Json.JsonSerializer.Serialize(session.Workspace.Snapshot());
            await Ui.Run(() => GhProjectsBoards.E2E.Tests.NativeClipboardScope.WriteTestFormats("sentinel"));

            await Ui.ClickCommand("SummaryCopyReport");

            await Ui.Until(() => GhProjectsBoards.E2E.Tests.NativeClipboardScope.ReadText() == expected);
            await Ui.Run(() => Assert.That(Ui.Find<AppBarButton>("SummaryCopyReport").Label, Is.EqualTo("コピー済み")));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(session.Workspace.Snapshot()), Is.EqualTo(before));
        }
        finally { await Ui.Run(() => saved.Dispose()); }
    }

    [TestCase(ElementTheme.Light), TestCase(ElementTheme.Dark)]
    public async Task NegativeAndUnknownHeadroomHaveDistinctVisibleMarkersAndThemeColors(ElementTheme theme)
    {
        await Ui.Run(() => grid.RequestedTheme = theme); await Open(); await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            var people = Ui.Find<ListView>("SummaryPeople");
            var presenters = Ui.Tree(people).OfType<SummaryPersonPresenter>().ToArray();
            var overload = presenters.Single(p => ((PersonSummary)p.DataContext).Id == "B");
            var neutral = presenters.Single(p => ((PersonSummary)p.DataContext).Id == "A");
            var negativeText = Ui.Tree(overload).OfType<TextBlock>().Single(t => t.Text.Contains("超過"));
            var neutralText = Ui.Tree(neutral).OfType<TextBlock>().Single(t => t.Text.Contains("余裕"));
            Assert.That(negativeText.Text, Does.Contain("⚠ 超過 2"));
            var highlight = Ui.Tree(overload).OfType<Border>().SingleOrDefault(b =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b) == "SummaryHeadroomHighlight");
            Assert.That(highlight, Is.Not.Null, "Known overload has a visible background beside its warning text.");
            Assert.That(((Microsoft.UI.Xaml.Media.SolidColorBrush)highlight!.Background).Color.A, Is.GreaterThan(0));
            Assert.That(((Microsoft.UI.Xaml.Media.SolidColorBrush)negativeText.Foreground).Color,
                Is.Not.EqualTo(((Microsoft.UI.Xaml.Media.SolidColorBrush)neutralText.Foreground).Color));
            var person = (PersonSummary)overload.DataContext;
            overload.DataContext = person with { Forecast = EffortValue.Missing };
            overload.UpdateLayout();
            var missing = Ui.Tree(overload).OfType<TextBlock>().Single(t => t.Text.Contains("比較未完"));
            Assert.That(missing.Text, Is.EqualTo("△ 比較未完"));
            Assert.That(Ui.Tree(overload).OfType<Border>().Any(b =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b) == "SummaryHeadroomHighlight"), Is.False,
                "A recycled unknown row must not retain known-overload emphasis.");
            Assert.That(((Microsoft.UI.Xaml.Media.SolidColorBrush)missing.Foreground).Color,
                Is.Not.EqualTo(((Microsoft.UI.Xaml.Media.SolidColorBrush)negativeText.Foreground).Color));
            overload.DataContext = person with { Allowance = person.Forecast.Hours };
            overload.UpdateLayout();
            var zero = Ui.Tree(overload).OfType<TextBlock>().Single(t => t.Text.Contains("余裕"));
            Assert.That(zero.Text, Is.EqualTo("余裕 0"));
            Assert.That(Ui.Tree(overload).OfType<Border>().Any(b =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b) == "SummaryHeadroomHighlight"), Is.False);
        });
    }

    [Test]
    public async Task OrdinarySummaryEntryOpensPersonAndContributingTaskWithoutChangingWork()
    {
        await Ui.Unmount(grid);
        await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<FrameworkElement>("GridCell0_0");
        var before = System.Text.Json.JsonSerializer.Serialize(session.Workspace.Snapshot());
        await Ui.Run(() => {
            Assert.That(Views().Items[2].IsEnabled, Is.True);
            Assert.That(Views().Items[2].Text, Is.EqualTo("Summary"));
            grid.ShowProjectView(ProjectView.Summary, "P1T2", "B");
            Assert.That(grid.CurrentProjectView, Is.EqualTo(ProjectView.Summary));
            Assert.That(grid.SummaryPersonId, Is.EqualTo("B"));
            Assert.That(Ui.Find<SummaryView>("SummaryView").SelectedRowId, Is.EqualTo("P1T2"));
        });
        Assert.That(System.Text.Json.JsonSerializer.Serialize(session.Workspace.Snapshot()), Is.EqualTo(before));
    }

    [Test, Category("PmoWorkflow")]
    public async Task OverloadedPersonRevealsTheirTaskAndRemainingCorrectionUpdatesForecastWithUndo()
    {
        await Open();
        await Ui.Run(() => {
            var people = Ui.Find<ListView>("SummaryPeople");
            Assert.That(((PersonSummary)people.Items[0]).Id, Is.EqualTo("B"));
            people.SelectedItem = people.Items.Cast<PersonSummary>().Single(p => p.Id == "B");
            Assert.That(Ui.Find<SummaryView>("SummaryView").SelectedRowId, Is.EqualTo("P1T2"));
            Ui.Find<TextBox>("SummaryRemaining").Text = "8"; Ui.Click("SummaryRemainingUpdate");
        });
        await Ui.Run(() => {
            var person = Ui.Find<SummaryView>("SummaryView").AdoptedSummary!.People.Single(p => p.Id == "B");
            Assert.That(person.Forecast.Hours, Is.EqualTo(64)); Assert.That(person.Headroom, Is.EqualTo(16));
            Assert.That(Ui.Find<TextBlock>("SummaryPersonDetail").Text, Does.Contain("余裕 2 人日"));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
        await Ui.ClickCommand("SummaryUndo");
        await Ui.Run(() => Assert.That(Ui.Find<SummaryView>("SummaryView").AdoptedSummary!.People.Single(p => p.Id == "B").Headroom, Is.EqualTo(-16)));
    }

    [Test]
    public async Task NonFirstBoardsAndGanttTaskEntersSummaryWithItsActualPerson()
    {
        await SheetNativeInput.Click("GridCell1_0");
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T2");
        await Open();
        await Ui.Run(() => {
            Assert.That(grid.SummaryPersonId, Is.EqualTo("B"));
            Assert.That(Ui.Find<SummaryView>("SummaryView").SelectedRowId, Is.EqualTo("P1T2"));
        });
        await Ui.ClickCommand("SummaryGantt"); await Ui.Ready<ListView>("GanttTasks");
        await Open();
        await Ui.Run(() => Assert.That(Ui.Find<SummaryView>("SummaryView").SelectedRowId, Is.EqualTo("P1T2")));
    }

    [Test]
    public async Task UnknownHoursAndPastReportingHeaderCannotClaimZeroOrToday()
    {
        var (p, w) = SummaryTests.Example();
        await Open();
        await Ui.Run(() => {
            var view = Ui.Find<SummaryView>("SummaryView");
            view.Present(SummaryProjection.Create(w, p, SummaryTests.Day.AddDays(1)));
            Assert.That(Ui.Tree(view).OfType<TextBlock>().Select(t => t.Text), Does.Contain("実績\n（2026-09-18 時点）"));
            Assert.That(SummaryText.Exact(EffortValue.Missing), Does.Contain("不明 人時").And.Not.Contain("0 人時"));
            Assert.That(SummaryText.Exact(EffortValue.Sum([new(8), EffortValue.Missing])), Does.Contain("小計"));
        });
    }

    [Test]
    public async Task ViewNavigationCommitsValidTitleAndRetainsTheSameTaskAcrossAllThreeViews()
    {
        var work = session.Workspace;
        await SheetNativeInput.Click("GridCell0_0");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Text = "未確定のタイトル");
        await Open();
        await Ui.Run(() => {
            Assert.That(Ui.Find<ListView>("SummaryPeople").Items, Has.Count.EqualTo(2));
            Assert.That(Ui.Find<TextBlock>("SummaryTotals").Text, Does.Contain("見積 26 / 実績 13"));
            Assert.That(Ui.Find<TextBlock>("SummaryPersonDetail").Text, Does.Contain("9 人日 / 72 人時"));
            Assert.That(work.Buffer(work.Open(project)[0].Cells[0]), Is.Null);
            Assert.That(work.Value(work.Open(project)[0].Cells[0]), Is.EqualTo("未確定のタイトル"));
            Ui.Find<TextBox>("SummaryFilter").Text = "no matching task";
        });
        await Ui.Until(() => Ui.Find<ListView>("SummaryTasks").Items.Count == 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<ListView>("SummaryTasks").Items, Is.Empty);
            Assert.That(Ui.Find<TextBlock>("SummaryTotals").Text, Does.Contain("見積 26"));
            Ui.Find<TextBox>("SummaryFilter").Text = "";
        });
        await Ui.Until(() => Ui.Find<ListView>("SummaryTasks").Items.Count == 1);
        await Ui.ClickCommand("SummaryGantt"); await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => Assert.That(Ui.Find<GanttView>("GanttView").SelectedRowId, Is.EqualTo("P1T1")));
        await Open(); await Ui.ClickCommand("SummaryBoards"); await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("GridCell0_0").Text, Is.EqualTo("未確定のタイトル")));
        await Open();
        await Ui.Run(() => { Assert.That(grid.SummaryPersonId, Is.EqualTo("A")); Assert.That(work.Journal, Is.Empty); });
    }
    [Test]
    public async Task ProtectedSummarySurvivesContextualGanttDateEditAndReturnsToTheSameTask()
    {
        await Open(); await Ui.Run(() => { var list = Ui.Find<ListView>("SummaryPeople"); list.SelectedItem = list.Items.Cast<PersonSummary>().Single(p => p.Id == "A"); }); await Ui.ClickCommand("SummaryEstablish"); await Ui.DialogReady("SummaryBaselineDialog");
        await Ui.Run(() => Ui.DialogButton("SummaryBaselineDialog", "PrimaryButton"));
        await Ui.Until(() => Ui.Dialog("SummaryBaselineDialog") is null);
        var baseline = System.Text.Json.JsonSerializer.Serialize(session.Workspace.Planning("P1")!.Summary);
        await Ui.ClickCommand("SummaryGantt"); await Ui.Ready<ListView>("GanttTasks");
        await Ui.ClickCommand("GanttEdit");
        await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor") is { IsLoaded: true } editor
            && Ui.Find<Button>("ScheduleApply", editor).IsLoaded);
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("SchedulingEditor")!;
            Ui.Find<RadioButtons>("ScheduleMethod", editor).SelectedIndex = 1;
            Ui.Find<TextBox>("ScheduleStart", editor).Text = "2026-10-05 12:07";
            Ui.Find<TextBox>("ScheduleFinish", editor).Text = "2026-10-06 16:19";
        });
        await Ui.Until(() => Ui.Find<Button>("ScheduleApply", Ui.Popup<StackPanel>("SchedulingEditor")) is { IsLoaded: true, IsEnabled: true });
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ScheduleApply", Ui.Popup<StackPanel>("SchedulingEditor"))));
        await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor") is null);
        await Ui.Run(() => {
            Assert.That(Ui.Find<GanttView>("GanttView").SelectedRowId, Is.EqualTo("P1T1"));
            var plan = session.Workspace.Planning("P1")!;
            Assert.That(plan.Version, Is.EqualTo(4));
            Assert.That(plan.Tasks.Single(t => t.Id == "I1").ManualFinish, Is.EqualTo(PlanningContractTests.At("2026-10-06 16:19")));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(plan.Summary), Is.EqualTo(baseline));
        });
        await Open();
        await Ui.Run(() => Assert.That(grid.SummaryPersonId, Is.EqualTo("A")));
        await Ui.ClickCommand("SummaryUndo");
        await Ui.Run(() => {
            Assert.That(session.Workspace.Planning("P1")!.Tasks.Single(t => t.Id == "I1").Mode, Is.EqualTo(PlanningMode.Unplanned));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(session.Workspace.Planning("P1")!.Summary), Is.EqualTo(baseline));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }

    [Test]
    public async Task AllowanceSaveFailureRetainsCandidateAndRetryPersistsOnlyAllowanceThenUndo()
    {
        await Open(); await Ui.Run(() => { var list = Ui.Find<ListView>("SummaryPeople"); list.SelectedItem = list.Items.Cast<PersonSummary>().Single(p => p.Id == "A"); }); await Ui.ClickCommand("SummaryAllowance"); await Ui.DialogReady("SummaryAllowanceDialog");
        await Ui.Run(() => Ui.Find<TextBox>("SummaryAllowanceHours", Ui.Dialog("SummaryAllowanceDialog")).Text = "0");
        using (var locked = new FileStream(Path.Combine(root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Ui.Run(() => Ui.DialogButton("SummaryAllowanceDialog", "PrimaryButton"));
            await Ui.Until(() => Ui.Find<TextBlock>("SummaryDialogError", Ui.Dialog("SummaryAllowanceDialog")).Text.Contains("失敗"));
            await Ui.Run(() => {
                Assert.That(session.Workspace.Planning("P1")!.Summary!.Allowances.Single(a => a.PersonId == "A").Hours, Is.EqualTo(160));
                Assert.That(Ui.Find<TextBox>("SummaryAllowanceHours", Ui.Dialog("SummaryAllowanceDialog")).Text, Is.EqualTo("0"));
            });
        }
        await Ui.Run(() => Ui.DialogButton("SummaryAllowanceDialog", "PrimaryButton"));
        await Ui.Until(() => Ui.Dialog("SummaryAllowanceDialog") is null);
        await Ui.Run(() => {
            var a = Ui.Find<SummaryView>("SummaryView").AdoptedSummary!.People.Single(p => p.Id == "A");
            Assert.That(a.Allowance, Is.Zero); Assert.That(a.Estimate.Hours, Is.EqualTo(144));
        });
        await Ui.ClickCommand("SummaryUndo"); await Ui.Run(() => Assert.That(session.Workspace.Planning("P1")!.Summary!.Allowances.Single(a => a.PersonId == "A").Hours, Is.EqualTo(160)));
    }
    [Test]
    public async Task ExplicitBaselineControlsKeepProtectedComparisonThroughTaskEditReplacementAndUndo()
    {
        await Open(); await Ui.Run(() => { var list = Ui.Find<ListView>("SummaryPeople"); list.SelectedItem = list.Items.Cast<PersonSummary>().Single(p => p.Id == "A"); }); await Ui.ClickCommand("SummaryEstablish"); await Ui.DialogReady("SummaryBaselineDialog");
        await Ui.Run(() => { Assert.That(Ui.DialogText("SummaryBaselineDialog"), Does.Contain("2タスク")); Ui.DialogButton("SummaryBaselineDialog", "PrimaryButton"); });
        await Ui.Until(() => Ui.Dialog("SummaryBaselineDialog") is null);
        var baseline = session.Workspace.Planning("P1")!.Summary!.Baseline!;
        await Ui.ClickCommand("SummaryTaskEdit"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<Expander>().Single(e => (string)e.Header == "工数・進捗・実績").IsExpanded = true);
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<TextBox>().Any(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t) == "PlanWork-Remaining"));
        await Ui.Run(() => {
            Ui.Find<TextBox>("PlanWork-Remaining", Ui.Dialog("PlanningDialog")).Text = "96";
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Until(() => Ui.Find<SummaryView>("SummaryView").AdoptedSummary!.People.Single(p => p.Id == "A").Remaining.Hours == 96);
        await Ui.Run(() => {
            var a = Ui.Find<SummaryView>("SummaryView").AdoptedSummary!.People.Single(p => p.Id == "A");
            Assert.That(a.Forecast.Days, Is.EqualTo(18)); Assert.That(session.Workspace.Planning("P1")!.Summary!.Baseline!.Id, Is.EqualTo(baseline.Id));
        });
        await Ui.ClickCommand("SummaryReplace"); await Ui.DialogReady("SummaryBaselineDialog");
        await Ui.Run(() => Ui.DialogButton("SummaryBaselineDialog", "CloseButton")); await Ui.Until(() => Ui.Dialog("SummaryBaselineDialog") is null);
        Assert.That(session.Workspace.Planning("P1")!.Summary!.Baseline!.Id, Is.EqualTo(baseline.Id));
        await Ui.ClickCommand("SummaryReplace"); await Ui.DialogReady("SummaryBaselineDialog");
        await Ui.Run(() => Ui.DialogButton("SummaryBaselineDialog", "PrimaryButton"));
        await Ui.Until(() => Ui.Dialog("SummaryBaselineDialog") is null || Ui.Find<TextBlock>("SummaryDialogError", Ui.Dialog("SummaryBaselineDialog")).Text.Length > 0);
        await Ui.Run(() => Assert.That(Ui.Dialog("SummaryBaselineDialog"), Is.Null, Ui.Dialog("SummaryBaselineDialog") is { } failed ? Ui.Find<TextBlock>("SummaryDialogError", failed).Text : ""));
        Assert.That(session.Workspace.Planning("P1")!.Summary!.Baseline!.Id, Is.Not.EqualTo(baseline.Id));
        await Ui.ClickCommand("SummaryUndo"); Assert.That(session.Workspace.Planning("P1")!.Summary!.Baseline!.Id, Is.EqualTo(baseline.Id));
    }
    [TestCase(ElementTheme.Light, 1400, 1000)]
    [TestCase(ElementTheme.Dark, 1000, 750)]
    public async Task NativeTableAndContextRemainReachableAtNormalAndNarrowBounds(ElementTheme theme, int width, int height)
    {
        await Ui.Run(() => { Ui.Window.AppWindow.Resize(new(width, height)); grid.RequestedTheme = theme; });
        await Open(); await SheetNativeInput.Rendered();
        await Ui.Run(async () => {
            var list = Ui.Find<ListView>("SummaryPeople"); var person = EditingGrid.Descendants(list).OfType<SummaryPersonPresenter>().First();
            Assert.That(person.ActualHeight, Is.GreaterThan(32));
            var scroll = EditingGrid.Descendants(list).OfType<ScrollViewer>().First();
            scroll.ChangeView(scroll.ScrollableWidth, null, null, true);
            Assert.That(Ui.Find<ListView>("SummaryTasks").ActualHeight, Is.GreaterThan(60));
            await ApplyInformationEvidence.Capture(grid, $"summary-{theme}-{width}");
        });
        await Ui.ClickCommand("SummaryTaskDetails");
        await Ui.Until(() => Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(grid.XamlRoot).Any());
        await SheetNativeInput.Press(Windows.System.VirtualKey.Escape);
    }
}
