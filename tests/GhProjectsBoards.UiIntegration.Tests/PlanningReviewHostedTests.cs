using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class PlanningHostedTests
{
    [Test, Category("ReviewFinal")]
    public async Task ChoosingTimeFirstCompletesTheVisibleKnownDayWithoutCreatingAClearIntent()
    {
        await ReviewFixture(PlanningReviewRegressionTests.Observed("Finish", "2026-10-06"), PlanningPathTests.Plan());
        await Schedule();
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("SchedulingEditor");
            Assert.That(Ui.Find<TextBox>("ScheduleFinish", editor).Text, Is.EqualTo("2026-10-06"));
            Ui.Find<TimePicker>("ScheduleFinish-Time", editor).SelectedTime = new TimeSpan(16, 43, 0);
            Assert.That(Ui.Find<TextBox>("ScheduleFinish", editor).Text, Is.EqualTo("2026-10-06 16:43"));
        });
        await Ui.Until(() => Ui.Find<Button>("ScheduleApply", Ui.Popup<StackPanel>("SchedulingEditor")!).IsLoaded);
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ScheduleApply", Ui.Popup<StackPanel>("SchedulingEditor"))));
        await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor") is null);
        await Ui.Run(() => {
            Assert.That(session.Workspace.Planning("P1")!.Tasks.Single().ManualFinish, Is.EqualTo(PlanningContractTests.At("2026-10-06 16:43")));
            Assert.That(session.Workspace.Value(session.Workspace.Open(project)[0].Cells[6]), Is.EqualTo("2026-10-06"));
        });
    }
    [Test, Category("ReviewFinal")]
    public async Task ClearingAndReselectingADayInTheSameEditorDoesNotReuseItsOldTime()
    {
        await ReviewFixture(project, PlanningPathTests.Plan() with { Tasks = [new("I1", PlanningMode.Manual, "U1",
            PlanningContractTests.At("2026-10-05 10:17"), PlanningContractTests.At("2026-10-06 16:43"))] });
        await Schedule();
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("SchedulingEditor");
            var date = Ui.Find<CalendarDatePicker>("ScheduleStart-Date", editor);
            date.Date = null;
            Assert.That(Ui.Find<TextBox>("ScheduleStart", editor).Text, Is.Empty);
            date.Date = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.FromHours(9));
            Assert.That(Ui.Find<TextBox>("ScheduleStart", editor).Text, Is.EqualTo("2026-10-05"));
            Assert.That(Ui.Find<TimePicker>("ScheduleStart-Time", editor).SelectedTime, Is.Null);
        });
    }
    [Test, Category("ReviewRegression")]
    public async Task DelayedSchedulingPreparationDoesNotOpenAnEditorOnTheViewThatWasLeft()
    {
        await Ui.Unmount(grid);
        var prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Ui.Run(() => grid = new(project, session, () => prepared.Task));
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T1");
        await Ui.ClickCommand("GridPlanning");
        await Ui.Run(() => { var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1]; prepared.SetResult(true); });
        await Ui.Idle();
        await Ui.Run(() => { Assert.That(grid.ShowingGantt, Is.True); Assert.That(Ui.Popup<StackPanel>("SchedulingEditor"), Is.Null); });
    }
    [Test, Category("ReviewRetention")]
    public async Task DistantBoardsGanttRoundtripsReleaseInactiveEditorsWhileKeepingTheOriginalPendingRow()
    {
        var (p, work) = GanttWorkload.Create(1000);
        await ReviewFixture(p, work.Planning("P1")!);
        TextBox original = null!;
        await Ui.Run(() => { original = Ui.Find<TextBox>("GridCell0_0"); original.Text = "original pending row"; original.SelectionStart = 9; original.SelectionLength = 0; });
        var references = new List<WeakReference<TextBox>>();
        for (var i = 1; i <= 80; i++)
        {
            var index = i * 12;
            await Ui.Run(() => { var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1]; });
            await Ui.Ready<ListView>("GanttTasks");
            await Ui.Run(() => { var tasks = Ui.Find<ListView>("GanttTasks"); tasks.SelectedItem = tasks.Items[index]; });
            await Ui.ClickCommand("GanttBoards"); await Ui.Ready<TextBox>($"GridCell{index}_0");
            await Ui.Run(() => {
                var input = Ui.Find<TextBox>($"GridCell{index}_0"); references.Add(new(input));
                Assert.That(grid.SelectionIdentity?.Item, Is.EqualTo(p.Snapshot.Items[index].Id.NodeId));
            });
        }
        // Observe retention after the native unload callbacks and finalizers,
        // outside any latency measurement. Weak references do not retain controls.
        await Ui.Idle();
        await Ui.Run(async () => {
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.That(Ui.Queue.TryEnqueue(() => drained.TrySetResult()), Is.True);
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(10));
        });
        await Task.Run(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
        await Ui.Run(() => {
            var unloaded = references.Count(r => r.TryGetTarget(out var input) && !input.IsLoaded);
            Console.WriteLine($"Inactive sampled editors after 80 distinct roundtrips: {unloaded}");
            Assert.That(unloaded, Is.LessThanOrEqualTo(65), "At most the 64 dormant rows plus one current row remain protected without pending work.");
            Assert.That(session.Workspace.Buffer(session.Workspace.Open(project)[0].Cells[0]), Is.EqualTo("original pending row"));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
        await Ui.Run(() => { var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1]; });
        await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => { var tasks = Ui.Find<ListView>("GanttTasks"); tasks.SelectedItem = tasks.Items[0]; });
        await Ui.ClickCommand("GanttBoards"); await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("GridCell0_0"), Is.SameAs(original));
            Assert.That(original.Text, Is.EqualTo("original pending row")); Assert.That(original.SelectionStart, Is.EqualTo(9));
        });
    }
    private async Task ReviewFixture(ProjectRegistration p, ProjectPlanning plan)
    {
        await Ui.Unmount(grid); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
        project = p; var work = new EditingWorkspace(p.Snapshot.Id.Scope); work.SetRegistrations([p]); work.SetPlanning(plan, 0);
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-review-ui-" + Guid.NewGuid())), work, 0);
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T1");
    }
    private async Task Schedule()
    {
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T1");
        await Ui.ClickCommand("GridPlanning"); await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor")?.IsLoaded == true);
    }
    private async Task Expand(string header, string readyId)
    {
        await Ui.Run(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<Expander>().Single(e => (string)e.Header == header).IsExpanded = true);
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<FrameworkElement>()
            .Any(c => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(c) == readyId && c.IsLoaded));
    }
    [Test, Category("ReviewRegression")]
    public async Task UnknownActualBreakdownUpdateShowsAnErrorAndPreservesTheObservedTotal()
    {
        await ReviewFixture(PlanningReviewRegressionTests.Observed("Actual", "5"), PlanningPathTests.Plan());
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_4").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Field?.FieldId == "F-Actual"); await Ui.Ready<Button>("ActualDetails");
        await Ui.Run(() => Ui.Click("ActualDetails")); await Ui.Until(() => Ui.Popup<StackPanel>("ActualReportsEditor")?.IsLoaded == true);
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ActualReportsUpdate", Ui.Popup<StackPanel>("ActualReportsEditor"))));
        await Ui.Run(() => {
            Assert.That(Ui.Popup<StackPanel>("ActualReportsEditor"), Is.Not.Null);
            Assert.That(Ui.Find<TextBlock>("ActualReportsError", Ui.Popup<StackPanel>("ActualReportsEditor")).Text, Does.Contain("実績を削除"));
            Assert.That(session.Workspace.Value(session.Workspace.Open(project)[0].Cells[4]), Is.EqualTo("5"));
            Assert.That(session.Workspace.Planning("P1")!.Tasks, Is.Empty);
            Ui.Click(Ui.Find<Button>("ActualReportsCancel", Ui.Popup<StackPanel>("ActualReportsEditor")));
        });
    }
    [Test, Category("ReviewRegression")]
    public async Task UnknownEndpointDayIsVisibleAndRequiresAnExactTimeBeforeTheOtherEndpointCanBeAdopted()
    {
        await ReviewFixture(PlanningReviewRegressionTests.Observed("Finish", "2026-10-06"), PlanningPathTests.Plan());
        await Schedule();
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("SchedulingEditor")!;
            Assert.That(Ui.Find<TextBox>("ScheduleFinish", editor).Text, Is.EqualTo("2026-10-06"));
            Ui.Find<TextBox>("ScheduleStart", editor).Text = "2026-10-05 10:17";
            Ui.Click(Ui.Find<Button>("ScheduleApply", editor));
        });
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("SchedulingEditor"); Assert.That(editor, Is.Not.Null);
            Assert.That(session.Workspace.Value(session.Workspace.Open(project)[0].Cells[6]), Is.EqualTo("2026-10-06"));
            Ui.Find<TextBox>("ScheduleFinish", editor).Text = "2026-10-06 16:43";
            Ui.Click(Ui.Find<Button>("ScheduleApply", editor));
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor") is null);
        await Ui.Run(() => Assert.That(session.Workspace.Planning("P1")!.Tasks.Single().ManualFinish,
            Is.EqualTo(PlanningContractTests.At("2026-10-06 16:43"))));
    }
    [Test, Category("ReviewRegression")]
    public async Task SelectingAnOutOfDomainObservedActualKeepsItsTextAndShowsACorrectionProblem()
    {
        await ReviewFixture(PlanningReviewRegressionTests.Observed("Actual", "-1"), PlanningPathTests.Plan());
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_4").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Field?.FieldId == "F-Actual");
        await Ui.Ready<TextBlock>("ActualInputHeading");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("GridCell0_4").Text, Is.EqualTo("-1"));
            Assert.That(Ui.Find<TextBlock>("ActualInputHeading").Text, Does.Contain("範囲外"));
        });
    }
    [Test, Category("ReviewRegression")]
    public async Task DetailsSaveRetainsSparseWorkerIdentityAndCannotImplicitlyRemoveOneHistoricalReport()
    {
        var task = new PlanningTask("I1", Actuals: [new("U1", 3, new(2026, 10, 5)), new("U-old", 2, new(2026, 10, 4))],
            Contributions: [new("U-old", null, null)]);
        await ReviewFixture(project, PlanningPathTests.Plan() with { Tasks = [task] });
        await Ui.ClickCommand("GridTaskDetails"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => Ui.DialogButton("PlanningDialog", "PrimaryButton")); await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Run(() => Assert.That(session.Workspace.Planning("P1")!.Tasks.Single().Contributions, Is.EqualTo(task.Contributions)));
        await Ui.ClickCommand("GridTaskDetails"); await Ui.DialogReady("PlanningDialog");
        await Expand("工数・進捗・実績", "PlanProgress"); await Expand("累積実績・担当者別内訳", "PlanActualHours-U-old");
        await Ui.Run(() => {
            Ui.Find<TextBox>("PlanActualHours-U-old", Ui.Dialog("PlanningDialog")).Text = "";
            Ui.Find<TextBox>("PlanReportedThrough-U-old", Ui.Dialog("PlanningDialog")).Text = "";
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Run(() => {
            Assert.That(Ui.Dialog("PlanningDialog"), Is.Not.Null);
            Assert.That(Ui.Find<TextBlock>("PlanningStatus", Ui.Dialog("PlanningDialog")).Text, Does.Contain("内訳"));
            Assert.That(session.Workspace.Planning("P1")!.Tasks.Single().Actuals, Is.EqualTo(task.Actuals));
        });
    }
    [Test, Category("ReviewRegression")]
    public async Task ClosingMinuteInputShowsTheLatestDurableBufferInTheBoardsCell()
    {
        await Schedule();
        await Ui.Run(() => Ui.Find<TextBox>("ScheduleStart", Ui.Popup<StackPanel>("SchedulingEditor")).Text = "2026");
        await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
        await Ui.Run(() => Ui.Find<TextBox>("ScheduleStart", Ui.Popup<StackPanel>("SchedulingEditor")).Text = "2026-10-05 12:07");
        await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ScheduleClose", Ui.Popup<StackPanel>("SchedulingEditor"))));
        await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor") is null);
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("GridCell0_5").Text, Is.EqualTo("2026-10-05 12:07")));
    }
    [Test, Category("ReviewRegression")]
    public async Task ClearingTheNativeDatePickerClearsTheEndpointTextAndRetainsTheOtherMinute()
    {
        await ReviewFixture(project, PlanningPathTests.Plan() with { Tasks = [new("I1", PlanningMode.Manual, "U1",
            PlanningContractTests.At("2026-10-05 10:17"), PlanningContractTests.At("2026-10-06 16:43"))] });
        await Schedule();
        await Ui.Run(() => Ui.Find<CalendarDatePicker>("ScheduleStart-Date", Ui.Popup<StackPanel>("SchedulingEditor")).Date = null);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("ScheduleStart", Ui.Popup<StackPanel>("SchedulingEditor")).Text, Is.Empty);
            Ui.Click(Ui.Find<Button>("ScheduleApply", Ui.Popup<StackPanel>("SchedulingEditor")));
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor") is null);
        await Ui.Run(() => {
            var task = session.Workspace.Planning("P1")!.Tasks.Single(); Assert.That(task.ManualStart, Is.Null);
            Assert.That(task.ManualFinish, Is.EqualTo(PlanningContractTests.At("2026-10-06 16:43")));
        });
        await Schedule();
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("SchedulingEditor");
            Ui.Find<CalendarDatePicker>("ScheduleStart-Date", editor).Date = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.FromHours(9));
            Assert.That(Ui.Find<TextBox>("ScheduleStart", editor).Text, Is.EqualTo("2026-10-05"), "Choosing a day alone must not fabricate a time or clear the endpoint.");
            Ui.Click(Ui.Find<Button>("ScheduleApply", editor));
        });
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("SchedulingEditor"); Assert.That(editor, Is.Not.Null);
            Ui.Find<TimePicker>("ScheduleStart-Time", editor).SelectedTime = new TimeSpan(10, 17, 0);
            Ui.Click(Ui.Find<Button>("ScheduleApply", editor));
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor") is null);
        await Ui.Run(() => Assert.That(session.Workspace.Planning("P1")!.Tasks.Single().ManualStart, Is.EqualTo(PlanningContractTests.At("2026-10-05 10:17"))));
    }
    [TestCase(true), TestCase(false), Category("ReviewRegression")]
    public async Task ModernUnknownAssignmentNeverShowsProvisionalCapacityInGanttDetails(bool complete)
    {
        await ReviewFixture(PlanningAssignmentTests.Assigned(), PlanningPathTests.Plan() with { Version = 3,
            Tasks = [new("I1", PlanningMode.Auto, Assignment: new([], complete))] });
        await Ui.Run(() => { var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1]; });
        await Ui.Ready<GanttView>("GanttView"); await Ui.ClickCommand("GanttDetails");
        await Ui.Until(() => Ui.Popup<StackPanel>("GanttTaskDetails")?.IsLoaded == true);
        await Ui.Run(() => {
            var text = string.Join("\n", Ui.Tree(Ui.Popup<StackPanel>("GanttTaskDetails")!).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Not.Contain("共通・暫定").And.Not.Contain("配賦: 100%"));
            Ui.Find<AppBarButton>("GanttDetails").Flyout.Hide();
        });
    }
    [TestCase("PullRequest"), TestCase("Draft"), Category("ReviewNonIssue")]
    public async Task NonIssuePlanningCommandReportsItsUnavailableTargetWithoutThrowing(string kind)
    {
        var p = project with { Snapshot = project.Snapshot with { Items = project.Snapshot.Items.Select(i => i with
            { Kind = Enum.Parse<ProjectItemKind>(kind), ContentId = kind == "Draft" ? null : i.ContentId }).ToArray() } };
        await ReviewFixture(p, PlanningPathTests.Plan()); await Ui.ClickCommand("GridPlanning"); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Popup<StackPanel>("SchedulingEditor"), Is.Null);
            Assert.That(Ui.Tree(grid).OfType<TextBlock>().Any(t => t.Text.Contains("Issueまたは新規行")), Is.True);
        });
    }
}
