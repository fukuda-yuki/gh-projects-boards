using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = System.IO.Path;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class GanttHostedTests
{
    private EditingGrid grid = null!;
    private DraftSession session = null!;
    private ProjectRegistration project = null!;
    private string root = null!;
    [SetUp]
    public async Task Setup()
    {
        project = PlanningPathTests.Registration(4); var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        work.CommitPlanning(project, PlanningPathTests.Plan() with { People = [new("U1", "Owner", 80)], Tasks = [
            new("I1", PlanningMode.Auto, "U1"), new("I2", PlanningMode.Manual, ManualStart: At("2026-10-05 09:00"), ManualFinish: At("2026-10-05 13:00")),
            new("I3", PlanningMode.Auto, LocalLinks: [new("I2")])] }, work.Revision,
            [new("P1T1", "Estimate", "16"), new("P1T3", "Estimate", "4")]);
        root = Path.Combine(Path.GetTempPath(), "ghpb-gantt-ui-" + Guid.NewGuid().ToString("N"));
        session = new(new DraftStore(root), work, 0);
        await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true)));
        await Ui.Run(() => Ui.Window.AppWindow.Resize(new(1400, 1000)));
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridCell0_2"); await Ui.Idle();
    }
    [TearDown]
    public async Task Teardown()
    {
        await Ui.Run(() => Ui.Dialog("PlanningDialog")?.Hide()); await Ui.Unmount(grid);
        Assert.That(await session.FlushAsync(), Is.True); await Ui.Idle();
    }
    [Test]
    public async Task BoardsPendingTextAndExactPlanRemainOneWorkspaceDuringViewAndEditRoundtrip()
    {
        var work = session.Workspace;
        await Ui.Run(() => {
            var title = Ui.Find<TextBox>("GridCell0_0"); title.Focus(FocusState.Keyboard); title.Text = "未確定のタイトル";
        });
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T1");
        await Ui.Run(() => Views().SelectedItem = Views().Items[1]);
        await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => {
            var gantt = Ui.Find<GanttView>("GanttView");
            Assert.That(gantt.SelectedRowId, Is.EqualTo("P1T1"));
            Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Contain("2026-10-07 13:00"));
            Assert.That(Ui.Find<ListView>("GanttTasks").Items, Has.Count.EqualTo(4));
            Assert.That(work.Buffer(work.Open(project)[0].Cells[0]), Is.EqualTo("未確定のタイトル"));
            Assert.That(Views().Items[2].IsEnabled, Is.False);
        });
        await Ui.Run(async () => await ApplyInformationEvidence.Capture(grid, "gantt-early-connected"));
        await Ui.ClickCommand("GanttEdit"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => {
            Ui.Find<TextBox>("PlanTaskFinish", Ui.Dialog("PlanningDialog")).Text = "2026-10-08 12:07";
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Until(() => Ui.Find<TextBlock>("GanttSelected").Text.Contains("2026-10-08 12:07"));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Contain("Manual").And.Contain("2026-10-08 12:07"));
            Assert.That(work.Buffer(work.Open(project)[0].Cells[0]), Is.EqualTo("未確定のタイトル"));
        });
        await Ui.ClickCommand("GanttUndo");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Contain("Auto").And.Contain("2026-10-07 13:00"));
            Views().SelectedItem = Views().Items[0];
        });
        await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("GridCell0_0").Text, Is.EqualTo("未確定のタイトル"));
            Assert.That(grid.SelectionIdentity?.Item, Is.EqualTo("P1T1")); Assert.That(work.Journal, Is.Empty);
        });
    }
    private static SelectorBar Views() => Ui.Find<SelectorBar>("ProjectViews");
    private static DateTime At(string text) => PlanningContractTests.At(text);

    [Test]
    public async Task ReturningFromBoardsRevealsItsSelectedTaskOutsideTheRetainedGanttSearch()
    {
        await Ui.Run(() => Views().SelectedItem = Views().Items[1]); await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => { Ui.Find<TextBox>("GanttSearch").Text = "#1"; Ui.Find<ListView>("GanttTasks").SelectedIndex = 0; Views().SelectedItem = Views().Items[0]; });
        await Ui.Ready<TextBox>("GridCell1_0");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell1_0").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T2");
        await Ui.Run(() => Views().SelectedItem = Views().Items[1]);
        await Ui.Run(() => {
            Assert.That(Ui.Find<GanttView>("GanttView").SelectedRowId, Is.EqualTo("P1T2"));
            Assert.That(Ui.Find<TextBox>("GanttSearch").Text, Is.Empty);
            Assert.That(Ui.Find<ListView>("GanttTasks").Items, Has.Count.EqualTo(4));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }

    [Test]
    public async Task F6StaysWithinVisibleGanttRegions()
    {
        await Ui.Run(() => Views().SelectedItem = Views().Items[1]); await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 0);
        await SheetNativeInput.Click("GanttSearch"); await SheetNativeInput.Press(Windows.System.VirtualKey.F6);
        await Ui.Until(() => Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(grid.XamlRoot) is not TextBox);
        await SheetNativeInput.Press(Windows.System.VirtualKey.F6);
        await Ui.Until(() => ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(grid.XamlRoot), Ui.Find<AppBarButton>("GanttEdit")));
        await SheetNativeInput.Press(Windows.System.VirtualKey.F6);
        await Ui.Until(() => ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(grid.XamlRoot), Ui.Find<AppBarButton>("GanttDetails")));
    }

    [Test]
    public async Task SaveFailureRemainsVisibleInGanttAndRetryPersistsTheRetainedEdit()
    {
        await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
        using (var locked = new FileStream(Path.Combine(root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Ui.Run(() => { Ui.Find<TextBox>("GridCell0_0").Text = "保存を再試行する文字"; Views().SelectedItem = Views().Items[1]; });
            await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.False));
            await Ui.Until(() => Ui.Find<InfoBar>("GanttOperationStatus").IsOpen);
            await Ui.Ready<Button>("GanttRetrySave");
            await Ui.Run(() => {
                Assert.That(Ui.Find<InfoBar>("GanttOperationStatus").Message, Does.Contain("ローカル保存失敗"));
                Assert.That(Ui.Find<Button>("GanttRetrySave").Visibility, Is.EqualTo(Visibility.Visible));
            });
        }
        await Ui.Run(() => Ui.Click("GanttRetrySave"));
        await Ui.Until(() => !Ui.Find<InfoBar>("GanttOperationStatus").IsOpen && session.DurableRevision == session.Workspace.Revision);
        var saved = (await new DraftStore(root).LoadAsync(session.Workspace.Scope))!;
        Assert.That(EditingWorkspace.Restore(saved).Buffer(session.Workspace.Open(project)[0].Cells[0]), Is.EqualTo("保存を再試行する文字"));
    }

    [Test]
    public async Task RenderedBarsAndLinksMatchIndependentMinuteGeometryInLightAndDark()
    {
        await Ui.Run(() => Views().SelectedItem = Views().Items[1]); await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 2);
        var backgrounds = new List<Windows.UI.Color>();
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            await Ui.Run(() => grid.RequestedTheme = theme); await SheetNativeInput.Rendered();
            await Ui.Run(async () => {
                var a = Ui.Find<Rectangle>("GanttBar-P1T2"); var b = Ui.Find<Rectangle>("GanttBar-P1T3");
                // Monday is x=0, 96 DIPs/day: 09/13/14/18 are 36/52/56/72.
                Assert.That(Canvas.GetLeft(a), Is.EqualTo(36)); Assert.That(a.Width, Is.EqualTo(16));
                Assert.That(Canvas.GetLeft(b), Is.EqualTo(56)); Assert.That(b.Width, Is.EqualTo(16));
                Assert.That(Ui.Find<Rectangle>("GanttBar-P1T1").Width, Is.EqualTo(208));
                var path = Ui.Find<Polyline>("GanttLink-I2-I3");
                var endA = a.TransformToVisual(path).TransformPoint(new(a.Width, 8));
                var startB = b.TransformToVisual(path).TransformPoint(new(0, 8));
                // Native layout rounds each element to device pixels at fractional DPI.
                var pixel = 1 / grid.XamlRoot.RasterizationScale;
                Assert.That(path.Points[0].X, Is.EqualTo(endA.X).Within(pixel)); Assert.That(path.Points[0].Y, Is.EqualTo(endA.Y).Within(pixel));
                Assert.That(path.Points[^1].X, Is.EqualTo(startB.X).Within(pixel)); Assert.That(path.Points[^1].Y, Is.EqualTo(startB.Y).Within(pixel));
                var monday = Ui.Find<Rectangle>("GanttDay-P1T1-20261005"); var saturday = Ui.Find<Rectangle>("GanttDay-P1T1-20261010");
                Assert.That(Canvas.GetLeft(saturday) - Canvas.GetLeft(monday), Is.EqualTo(480));
                Assert.That(((SolidColorBrush)monday.Fill).Color, Is.Not.EqualTo(((SolidColorBrush)saturday.Fill).Color));
                backgrounds.Add(((SolidColorBrush)Ui.Find<GanttView>("GanttView").Background).Color);
                await ApplyInformationEvidence.Capture(grid, "gantt-geometry-" + theme);
            });
        }
        Assert.That(backgrounds[0], Is.Not.EqualTo(backgrounds[1]));
        await Ui.Run(() => {
            var gantt = Ui.Find<GanttView>("GanttView"); var p = gantt.AdoptedProjection;
            // The UI must display uncertainty supplied at its boundary, not invent a 100% owner.
            var row = p.Rows[0]; row = row with { Input = row.Input! with { Task = row.Input.Task with { OwnerId = "missing-owner" } } };
            gantt.Present(p with { Rows = [row] }, row.RowId);
        });
        await Ui.ClickCommand("GanttDetails");
        await Ui.Until(() => Details() is not null);
        await Ui.Run(() => {
            var text = string.Join("\n", Ui.Tree(Details()!).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain("missing-owner（未確認） / 配賦: 未確認").And.Not.Contain("配賦: 100%"));
            Ui.Find<AppBarButton>("GanttDetails").Flyout.Hide();
        });
    }
    private DependencyObject? Details() => VisualTreeHelper.GetOpenPopupsForXamlRoot(grid.XamlRoot)
        .Select(p => p.Child).FirstOrDefault(c => Ui.Tree(c).Any(e => AutomationProperties.GetAutomationId(e) == "GanttTaskDetails"));

    [Test]
    public async Task AdoptedHolidayAndPersonalExceptionAgreeAcrossAxisDetailsAndBoards()
    {
        await Ui.Run(() => {
            var work = session.Workspace; var plan = work.Planning("P1")!;
            work.CommitPlanning(project, plan with { Start = At("2026-10-12 09:00"), People = [new("U1", "Owner", 100)], Tasks = [new("I1", PlanningMode.Auto, "U1")],
                Calendar = plan.Calendar with { Exceptions = [new(new(2026, 10, 13), "U1", [new(600, 720)])] } }, work.Revision, [new("P1T1", "Estimate", "2")]);
            Views().SelectedItem = Views().Items[1]; Ui.Find<ListView>("GanttTasks").SelectedIndex = 0;
        });
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Contain("2026-10-13 10:00").And.Contain("2026-10-13 12:00"));
            var holiday = ((SolidColorBrush)Ui.Find<Rectangle>("GanttDay-P1T1-20261012").Fill).Color;
            var weekday = ((SolidColorBrush)Ui.Find<Rectangle>("GanttDay-P1T1-20261013").Fill).Color;
            var saturday = ((SolidColorBrush)Ui.Find<Rectangle>("GanttDay-P1T1-20261017").Fill).Color;
            Assert.That(holiday, Is.EqualTo(saturday).And.Not.EqualTo(weekday));
        });
        await Ui.ClickCommand("GanttDetails"); await Ui.Until(() => Details() is not null);
        await Ui.Run(() => {
            var text = string.Join("\n", Ui.Tree(Details()!).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain("配賦: 100%").And.Contain("2026-10-13: 10:00–12:00").And.Contain("official-2025-2027"));
            Ui.Find<AppBarButton>("GanttDetails").Flyout.Hide();
        });
        await Ui.ClickCommand("GanttBoards"); await Ui.ClickCommand("GridPlanning"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanTaskStart", Ui.Dialog("PlanningDialog")).Text, Is.EqualTo("2026-10-13 10:00"));
            Assert.That(Ui.Find<TextBox>("PlanTaskFinish", Ui.Dialog("PlanningDialog")).Text, Is.EqualTo("2026-10-13 12:00"));
            Ui.DialogButton("PlanningDialog", "CloseButton"); Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }

    [Test]
    public async Task ManualOverrideSurvivesWeightCalendarPredecessorAndReplanThenAutoAndUndoStayCoherent()
    {
        await Ui.Run(() => {
            var work = session.Workspace; var plan = work.Planning("P1")!;
            work.CommitPlanning(project, plan with { Tasks = plan.Tasks.Select(t => t.Id == "I3" ? t with { LocalLinks = [new("I1")] } : t).ToArray() }, work.Revision);
        });
        await Ui.Run(() => Views().SelectedItem = Views().Items[1]); await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 0);
        await Ui.ClickCommand("GanttEdit"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => {
            var d = Ui.Dialog("PlanningDialog");
            Ui.Find<TextBox>("PlanTaskStart", d).Text = "2026-10-05 12:07"; Ui.Find<TextBox>("PlanTaskFinish", d).Text = "2026-10-05 13:00";
            Ui.Tree(d!).OfType<Expander>().Single(e => (string)e.Header == "先行Issue（終了→開始）").IsExpanded = true;
        });
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).Any(c => AutomationProperties.GetAutomationId(c) == "PlanPredecessors" && c is FrameworkElement { IsLoaded: true }));
        await Ui.Run(() => { var links = Ui.Find<ListView>("PlanPredecessors", Ui.Dialog("PlanningDialog")); links.SelectedItems.Add(links.Items[0]); Ui.DialogButton("PlanningDialog", "PrimaryButton"); });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null && Ui.Find<TextBlock>("GanttSelected").Text.Contains("12:07"));
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("GanttNotice").Text, Does.StartWith("注意:")));
        await Ui.ClickCommand("GanttSettings"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => { foreach (var e in Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<Expander>().ToArray()) e.IsExpanded = true; });
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).Any(c => AutomationProperties.GetAutomationId(c) == "PlanWeight-U1" && c is FrameworkElement { IsLoaded: true }));
        await Ui.Run(() => {
            var d = Ui.Dialog("PlanningDialog"); Ui.Find<TextBox>("PlanWeight-U1", d).Text = "50";
            Ui.Find<CheckBox>("PlanIgnoreHolidays", d).IsChecked = true; Ui.Find<TextBox>("PlanCutoff", d).Text = "2026-10-05 09:00";
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Contain("Manual").And.Contain("12:07").And.Contain("13:00"));
            Ui.Find<ListView>("GanttTasks").SelectedIndex = 2;
            Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Contain("2026-10-05 14:00").And.Contain("18:00"));
            Ui.Find<ListView>("GanttTasks").SelectedIndex = 0;
        });
        await Ui.ClickCommand("GanttEdit"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => { Ui.Find<ComboBox>("PlanMode", Ui.Dialog("PlanningDialog")).SelectedIndex = (int)PlanningMode.Auto; Ui.DialogButton("PlanningDialog", "PrimaryButton"); });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null && Ui.Find<TextBlock>("GanttSelected").Text.Contains("Auto"));
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Contain("2026-10-09 13:00")));
        await Ui.ClickCommand("GanttUndo");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Contain("Manual").And.Contain("12:07").And.Contain("13:00"));
            Assert.That(session.Workspace.Planning("P1")!.People[0].WeightPercent, Is.EqualTo(50));
            Assert.That(session.Workspace.PlanFor(project).Tasks.Single(t => t.Id == "I3").Start, Is.EqualTo(At("2026-10-05 14:00")));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }

    [Test]
    public async Task DependenciesRevealTheSameTaskAndInvalidDateDoesNotAlterAdoptedPlan()
    {
        await Ui.Run(() => Views().SelectedItem = Views().Items[1]); await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 2);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Contain("2026-10-05 14:00").And.Contain("18:00"));
            Ui.Find<ComboBox>("GanttRelated").SelectedIndex = 0;
        });
        await Ui.Until(() => Ui.Find<GanttView>("GanttView").SelectedRowId == "P1T2");
        await Ui.Run(async () => await ApplyInformationEvidence.Capture(grid, "gantt-selected-dependency"));
        await Ui.ClickCommand("GanttEdit"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => {
            Ui.Find<TextBox>("PlanTaskFinish", Ui.Dialog("PlanningDialog")).Text = "2026-10-04 09:00";
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Run(() => {
            Assert.That(Ui.Dialog("PlanningDialog"), Is.Not.Null);
            Assert.That(session.Workspace.PlanFor(project).Tasks.Single(t => t.Id == "I2").Finish, Is.EqualTo(At("2026-10-05 13:00")));
            Ui.DialogButton("PlanningDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.ClickCommand("GanttBoards");
        await Ui.Until(() => !grid.ShowingGantt && grid.SelectionIdentity?.Item == "P1T2");
    }

    [Test, Category("PlanningPerformance")]
    public async Task ThousandTaskReplanPublishesExpectedBarAndReportsCalculationAndSaveSeparately()
    {
        await Ui.Unmount(grid); var fixture = GanttWorkload.Create(); project = fixture.Project;
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-gantt-replan-" + Guid.NewGuid().ToString("N"))), fixture.Work, 0);
        await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Run(() => Views().SelectedItem = Views().Items[1]); await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 0);
        var visible = new List<double>(); var calculation = new List<double>(); var saves = new List<double>();
        var changedEndpoints = new List<int>();
        for (var i = -2; i < 10; i++)
        {
            var eight = i % 2 == 0;
            var before = session.Workspace.PlanFor(project).Tasks;
            await Ui.ClickCommand("GanttEdit"); await Ui.DialogReady("PlanningDialog");
            await Ui.Run(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<Expander>().Single(e => (string)e.Header == "工数・進捗・実績").IsExpanded = true);
            await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).Any(c => AutomationProperties.GetAutomationId(c) == "PlanWork-Estimate" && c is FrameworkElement { IsLoaded: true }));
            await Ui.Run(() => Ui.Find<TextBox>("PlanWork-Estimate", Ui.Dialog("PlanningDialog")).Text = eight ? "8" : "4");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await Ui.Run(() => Ui.DialogButton("PlanningDialog", "PrimaryButton"));
            await Ui.Until(() => Ui.Dialog("PlanningDialog") is null && Ui.Find<TextBlock>("GanttSelected").Text.Contains(eight ? "2026-10-05 18:00" : "2026-10-05 13:00"));
            await SheetNativeInput.Rendered(); timer.Stop();
            await Ui.Run(() => Assert.That(Ui.Find<Rectangle>("GanttBar-P1T1").Width, Is.EqualTo(eight ? 36 : 16)));
            if (i >= 0) visible.Add(timer.Elapsed.TotalMilliseconds);
            await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
            var plan = session.Workspace.PlanFor(project);
            if (i >= 0) changedEndpoints.Add(plan.Tasks.Zip(before).Count(p => p.First.Start != p.Second.Start || p.First.Finish != p.Second.Finish));
            timer.Restart(); var calculated = PlanningEngine.Calculate(plan.Configuration!, plan.Inputs!, plan.SourceRevision); timer.Stop();
            Assert.That(calculated.Tasks[0].Finish, Is.EqualTo(At(eight ? "2026-10-05 18:00" : "2026-10-05 13:00")));
            if (i >= 0) calculation.Add(timer.Elapsed.TotalMilliseconds);
            var snapshot = session.Workspace.Snapshot();
            var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-gantt-save-sample-" + Guid.NewGuid().ToString("N")));
            timer.Restart(); await store.SaveAsync(snapshot, 0); timer.Stop();
            if (i >= 0) saves.Add(timer.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine("Gantt 1000 replan measurement: " + System.Text.Json.JsonSerializer.Serialize(new {
            warmups = 2, samples = 10, changedInputs = 1, changedEndpoints, calculationMs = calculation, commitThroughRenderEventsMs = visible, newCheckpointSaveMs = saves,
            boundary = "PrimaryButton public invoke through dialog close, independent expected text and two render events; includes planning and UI rebuild. New checkpoint save is isolated and sequential. Neither measurement is physical scanout." }));
        await Ui.Run(async () => await ApplyInformationEvidence.Capture(grid, "gantt-1000-replanned"));
        Assert.That(visible.Max(), Is.LessThanOrEqualTo(1000));
    }

    [Test, Category("PlanningPerformance")]
    public async Task ThousandTasksSupportBothAxesLongLinksShortBarsAndNarrowWindow()
    {
        await Ui.Unmount(grid);
        var fixture = GanttWorkload.Create(); project = fixture.Project;
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-gantt-scale-" + Guid.NewGuid().ToString("N"))), fixture.Work, 0);
        await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Run(() => Views().SelectedItem = Views().Items[1]); await Ui.Ready<ListView>("GanttTasks");
        var samples = new List<double>();
        for (var i = -2; i < 10; i++)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await Ui.Run(() => {
                var list = Ui.Find<ListView>("GanttTasks"); list.SelectedIndex = i % 2 == 0 ? 999 : 0;
            });
            await Ui.ClickCommand("GanttReveal");
            await VisibleRow(i % 2 == 0 ? 999 : 0);
            await Ui.Run(async () => {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var frames = 0; EventHandler<object> frame = (_, _) => { if (++frames >= 2) done.TrySetResult(); };
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += frame;
                try { await done.Task.WaitAsync(TimeSpan.FromSeconds(5)); } finally { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= frame; }
            });
            timer.Stop(); if (i >= 0) samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        await Ui.Run(() => {
            Ui.Find<ListView>("GanttTasks").SelectedIndex = 999;
            Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Contain("2027-03-15 12:07").And.Contain("13:00"));
        });
        await Ui.ClickCommand("GanttReveal");
        await VisibleRow(999);
        await Ui.Run(async () => await ApplyInformationEvidence.Capture(grid, "gantt-1000-last-day"));
        await Ui.ClickCommand("GanttEdit"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => { Ui.Find<TextBox>("PlanTaskFinish", Ui.Dialog("PlanningDialog")).Text = "2027-03-15 16:19"; Ui.DialogButton("PlanningDialog", "PrimaryButton"); });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null && Ui.Find<TextBlock>("GanttSelected").Text.Contains("16:19"));
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("GanttSearch").Text, Is.Empty);
            var selected = (ListViewItem)Ui.Find<ListView>("GanttTasks").ContainerFromIndex(999);
            Assert.That(selected, Is.Not.Null, "Editing an unfiltered distant task must retain its visible row.");
            Assert.That(Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(selected).IsOffscreen(), Is.False);
        });
        await Ui.Run(() => { Ui.Find<ComboBox>("GanttScale").SelectedIndex = 1; Ui.Find<ListView>("GanttTasks").SelectedIndex = 989; });
        await Ui.ClickCommand("GanttReveal");
        await VisibleRow(989); await SheetNativeInput.Rendered();
        await Ui.Run(() => Assert.That(Ui.Find<ComboBox>("GanttRelated").Items.Count, Is.GreaterThanOrEqualTo(12)));
        await Ui.Run(async () => await ApplyInformationEvidence.Capture(grid, "gantt-1000-fan-in-week"));
        await Ui.Run(() => {
            var dpi = grid.XamlRoot.RasterizationScale; Ui.Window.AppWindow.Resize(new((int)(960 * dpi), (int)(600 * dpi)));
        });
        await Ui.Until(() => grid.ActualWidth <= 960);
        await Ui.ClickCommand("GanttReveal"); await VisibleRow(989); await SheetNativeInput.Rendered();
        await Ui.Run(async () => await ApplyInformationEvidence.Capture(grid, "gantt-1000-narrow"));
        await Ui.ClickCommand("GanttBoards");
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T990" && !grid.ShowingGantt);
        await Ui.Until(() => Ui.Tree(grid).OfType<TextBox>().Any(c => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(c) == "GridCell989_0"
            && !Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(c).IsOffscreen()));
        await Ui.Run(() => {
            Assert.That(session.Workspace.Buffer(session.Workspace.Open(project)[999].Cells.First(c => c.Key?.FieldId == "F-Estimate")), Is.EqualTo("24未確定"));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
        Console.WriteLine("Gantt navigation command-to-render-events ms: " + System.Text.Json.JsonSerializer.Serialize(samples));
        Assert.That(samples.Max(), Is.LessThanOrEqualTo(1000), "Normal-scale navigation engineering budget; not physical scanout or inherited selection/menu evidence.");
    }
    private Task VisibleRow(int index) => Ui.Until(() => Ui.Find<ListView>("GanttTasks").ContainerFromIndex(index) is ListViewItem { IsLoaded: true } item
        && !Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(item).IsOffscreen());
}

public sealed partial class HostedTests
{
    [Test]
    public async Task GanttSelectionOutsideBoardsFilterSurvivesProjectRoundtripAndReturnsToThatRow()
    {
        var first = Workspace.Selected!;
        await Ui.Run(async () => {
            var choice = await new ProjectDiscovery(h.Existing.Service).ResolveAsync(h.Existing.Context, "https://github.com/users/sample-user/projects/2", default);
            await Workspace.RegisterAsync(choice, null); await Workspace.SelectAsync(first.Snapshot.Id);
        });
        await Ui.ClickCommand("GridRowSettings"); await Ui.DialogReady("RowSettingsDialog");
        await Ui.Run(() => { Ui.Find<TextBox>("RowTitleFilter", Ui.Dialog("RowSettingsDialog")).Text = "Issue 1"; Ui.DialogButton("RowSettingsDialog", "PrimaryButton"); });
        await Ui.Until(() => Ui.Dialog("RowSettingsDialog") is null);
        await Ui.Run(() => Ui.Find<SelectorBar>("ProjectViews").SelectedItem = Ui.Find<SelectorBar>("ProjectViews").Items[1]);
        await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 1);
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("GanttNotice").Text, Does.Contain("表のフィルター外")));
        await OpenNavigation(SplitViewDisplayMode.Inline); await InvokeProjectNode("Project 2");
        await Ui.Until(() => Workspace.Selected?.Snapshot.Id.NodeId == "P2");
        await InvokeProjectNode(first.Snapshot.Title);
        await Ui.Until(() => Workspace.Selected?.Snapshot.Id.NodeId == "P1" && Ui.Find<GanttView>("GanttView").SelectedRowId == "P1-T2");
        await Ui.ClickCommand("GanttBoards");
        await Ui.Run(() => { Assert.That(Ui.Tree(panel).OfType<EditingGrid>().Single().SelectionIdentity?.Item, Is.EqualTo("P1-T2")); Assert.That(h.Writes, Is.Empty); });
    }
}
