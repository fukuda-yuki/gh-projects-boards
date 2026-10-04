using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class PlanningHostedTests
{
    [Test, Category("WorkflowPlanning")]
    public async Task PartialManualDatesAskForTheMissingEndpointWithoutRequiringAutomaticEffort()
    {
        await Ui.Run(() => {
            var work = session.Workspace;
            work.CommitPlanning(project, work.Planning("P1")! with { Tasks = [new("I1", PlanningMode.Manual,
                ManualStart: new(2026, 10, 5, 9, 0, 0))] }, work.Revision);
            var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1];
        });
        await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 0);
        await Ui.Until(() => Ui.Find<TextBlock>("GanttSelected").Text.Contains("片側のみ"));
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("GanttNotice").Text, Does.Contain("終了日時が未設定")));
        await Ui.ClickCommand("GanttEdit");
        await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor")?.IsLoaded == true);
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("SchedulingEditor")!;
            Assert.That(Ui.Find<TextBlock>("SchedulePreview", editor).Text, Does.Contain("終了日時が未設定").And.Not.Contain("見積工数"));
            Ui.Find<TextBox>("ScheduleFinish", editor).Text = "2026-10-05 13:00";
        });
        await Ui.Until(() => Ui.Find<TextBlock>("SchedulePreview", Ui.Popup<StackPanel>("SchedulingEditor")).Text.Contains("13:00"));
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("SchedulingEditor")!;
            Assert.That(Ui.Find<TextBlock>("SchedulePreview", editor).Text, Does.Not.Contain("見積工数"));
            Ui.Click(Ui.Find<Button>("ScheduleApply", editor));
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor") is null);
        await Ui.Run(() => Assert.That(session.Workspace.PlanFor(project).Tasks.Single(t => t.Id == "I1").Resolved, Is.True));
    }

    [Test, Category("WorkflowPlanning")]
    public async Task DateChoicesAndDirectTextUseOneValueAndNeverInventTheMissingTime()
    {
        MinuteEditor editor = null!;
        await Ui.Run(() => editor = new("日時", "WorkflowMinute", "")); await Ui.Mount(editor);
        await Ui.Ready<CalendarDatePicker>("WorkflowMinute-Date");
        void SwitchInput() => ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<HyperlinkButton>("WorkflowMinute-Direct")).GetPattern(PatternInterface.Invoke)).Invoke();
        await Ui.Run(() => {
            Assert.That(editor.Input.Visibility, Is.EqualTo(Visibility.Collapsed));
            Ui.Find<CalendarDatePicker>("WorkflowMinute-Date").Date = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.FromHours(9));
        });
        await Ui.Until(() => editor.Text == "2026-10-05");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TimePicker>("WorkflowMinute-Time").SelectedTime, Is.Null);
            SwitchInput();
        });
        await Ui.Until(() => editor.Input.Visibility == Visibility.Visible);
        await Ui.Run(() => { editor.Input.Text = "2026-10-05 09:"; SwitchInput(); });
        await Ui.Until(() => editor.Input.Description is not null);
        await Ui.Run(() => {
            Assert.That(editor.Input.Visibility, Is.EqualTo(Visibility.Visible)); Assert.That(editor.Text, Is.EqualTo("2026-10-05 09:"));
            editor.Input.Text = "2026-10-05 09:07"; SwitchInput();
        });
        await Ui.Until(() => editor.Input.Visibility == Visibility.Collapsed);
        await Ui.Run(() => Assert.That(Ui.Find<TimePicker>("WorkflowMinute-Time").SelectedTime, Is.EqualTo(new TimeSpan(9, 7, 0))));
        await Ui.Unmount(editor);
    }

    [TestCase("Unstarted", "未着手", "見積 4人時", false)]
    [TestCase("Unstarted", "未着手", "見積 4人時", true)]
    [TestCase("InProgress", "進行中", "残時間 3人時", true)]
    [Category("WorkflowPlanning")]
    public async Task GanttExplainsTheEffortUsedWithoutTreatingActualAsAProgressChange(string progressName, string label, string effort, bool hasActual)
    {
        var progress = Enum.Parse<PlanningProgress>(progressName);
        await Ui.Run(() => {
            var w = session.Workspace; var p = w.Planning("P1")!;
            w.CommitPlanning(project, p with { Version = 3, Tasks = [p.Tasks[0] with { Progress = progress,
                Assignment = new(["U1"], true), ActualStart = progress == PlanningProgress.InProgress ? p.Start : null,
                Actuals = hasActual ? [new("U1", 2, new(2026, 10, 6))] : null }] }, w.Revision,
                [new("P1T1", "Estimate", "4"), new("P1T1", "Remaining", "3")]);
            var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1];
        });
        await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 0);
        await Ui.ClickCommand("GanttDetails"); await Ui.Until(() => Ui.Popup<StackPanel>("GanttTaskDetails")?.IsLoaded == true);
        await Ui.Run(() => {
            var details = Ui.Popup<StackPanel>("GanttTaskDetails")!;
            var text = string.Join("\n", Ui.Tree(details).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain(label).And.Contain(effort));
            Assert.That(text.Contains("実績の入力だけでは計画上の進捗を変更しません"), Is.EqualTo(hasActual && progress == PlanningProgress.Unstarted));
            Assert.That(Ui.Find<TextBlock>("GanttNotice").Text, Does.Not.Contain("日程の注意"), "Unallocated effort does not invalidate the adopted dates.");
            Assert.That(Ui.Find<Button>("GanttExplanationSettings", details).IsEnabled, Is.True);
            Assert.That(Ui.Tree(details).OfType<Expander>().Single(e => (string)e.Header == "計算の記録").IsExpanded, Is.False);
            Assert.That(session.Workspace.Planning("P1")!.Tasks[0].Progress, Is.EqualTo(progress));
            Ui.Find<AppBarButton>("GanttDetails").Flyout.Hide();
        });
    }

    [Test, Category("WorkflowPlanning")]
    public async Task InvalidProjectStartKeepsTheInputAndShowsTheCorrectionWithoutScrolling()
    {
        await Ui.ClickCommand("GridPlanningSettings"); await Ui.Ready<TextBox>("PlanProjectStart"); await Ui.Ready<Button>("PlanSettingsSave");
        var before = session.Workspace.Revision;
        await Ui.Run(() => {
            var dialog = Ui.Find<Grid>("PlanningSettingsPage");
            Ui.Find<TextBox>("PlanProjectStart", dialog).Text = "2026/10/01";
            Ui.Click("PlanSettingsSave");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanningStatus", Ui.Find<Grid>("PlanningSettingsPage")).Text.Length > 0);
        await Ui.Run(() => {
            var dialog = Ui.Find<Grid>("PlanningSettingsPage");
            var input = Ui.Find<TextBox>("PlanProjectStart", dialog);
            var message = Ui.Find<TextBlock>("PlanningStatus", dialog);
            Assert.Multiple(() => {
                Assert.That(session.Workspace.Revision, Is.EqualTo(before));
                Assert.That(input.Text, Is.EqualTo("2026/10/01"));
                Assert.That(message.Text, Does.Contain("Project開始"));
                Assert.That(Ui.Tree(dialog).OfType<Expander>().Any(e => e.IsExpanded), Is.False, "Unrelated sections must stay closed.");
                Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(input));
                var bounds = message.TransformToVisual(dialog).TransformBounds(new(0, 0, message.ActualWidth, message.ActualHeight));
                Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0));
                Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(dialog.ActualHeight), "The correction must be visible without scrolling to discover it.");
            });
            Ui.Find<TextBox>("PlanCutoff", dialog).Text = "2026-10-09 18:01";
            Assert.That(message.Visibility, Is.EqualTo(Visibility.Visible), "An unrelated edit does not resolve the start correction.");
            input.Text = "2026-10-01 09:";
        });
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("PlanProjectStart");
            Assert.That(Ui.Find<TextBlock>("PlanningStatus").Visibility, Is.EqualTo(Visibility.Visible), "Still-invalid edits retain the correction.");
            input.Text = "2026-10-01 09:00";
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanningStatus").Visibility == Visibility.Collapsed);
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanProjectStart").Description, Is.Null));
    }

    [TestCase("Issue", "Issue情報を確認できません"), TestCase("Draft", "この行は計画対象外"), Category("WorkflowPlanning")]
    public async Task SelectingAnUnavailableItemDoesNotExposeAnIssueScheduleOrChangeThePlan(string kindName, string message)
    {
        await Ui.Unmount(grid);
        project = project with { Snapshot = project.Snapshot with { Items = project.Snapshot.Items.Select(i => i.Id.NodeId == "P1T2" ? i with { Kind = Enum.Parse<ProjectItemKind>(kindName), ContentId = null } : i).ToArray() } };
        session.Workspace.SetRegistrations([project]);
        var before = session.Workspace.Revision;
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<FrameworkElement>("GridCell1_0");
        await Ui.Run(() => FocusCell("GridCell1_0"));
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T2");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("DateInputState").Text, Does.Contain(message).And.Not.Contain("採用済み"));
            Assert.That(Ui.Find<Button>("DateInputEditor").IsEnabled, Is.False);
            Assert.That(session.Workspace.Revision, Is.EqualTo(before));
        });
    }

    [Test, Category("WorkflowPlanning")]
    public async Task LocalNewTaskKeepsItsAdoptedDatesAndEditorWithoutAGitHubContentId()
    {
        await Ui.Unmount(grid);
        var work = session.Workspace; var id = work.AddRow(project);
        work.CommitPlanning(project, work.Planning("P1")! with { Tasks = [new(id, PlanningMode.Manual,
            ManualStart: new(2026, 10, 5, 9, 0, 0), ManualFinish: new(2026, 10, 5, 13, 0, 0))] }, work.Revision);
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<FrameworkElement>("GridCell2_0");
        await Ui.Run(() => FocusCell("GridCell2_0"));
        await Ui.Until(() => grid.SelectionIdentity?.Item == id);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("DateInputState").Text, Does.Contain("採用済み").And.Contain("13:00"));
            Assert.That(Ui.Find<Button>("DateInputEditor").IsEnabled, Is.True);
            Assert.That(work.Journal, Is.Empty);
        });
    }

    [Test, Category("WorkflowPlanning")]
    public async Task FirstTaskPlanningReturnsFromProjectSetupToTheSameTaskAndClearsTheOldSelectionError()
    {
        await Ui.Unmount(grid);
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-workflow-first-" + Guid.NewGuid())), work, 0);
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)) { Width = 750, Height = 480 });
        await Ui.Mount(grid); await Ui.Ready<FrameworkElement>("GridCell0_0");
        await Ui.Ready<Button>("GridStartPlanning");
        await Ui.Run(() => {
            var entry = Ui.Find<Button>("GridStartPlanning");
            var bounds = entry.TransformToVisual(grid).TransformBounds(new(0, 0, entry.ActualWidth, entry.ActualHeight));
            Assert.That(bounds.Width, Is.GreaterThan(0));
            Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(0)); Assert.That(bounds.Right, Is.LessThanOrEqualTo(grid.ActualWidth));
            Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(grid.ActualHeight));
            Assert.That(grid.SelectionIdentity, Is.Null); Ui.Click(entry);
        });
        await Ui.Ready<Button>("PlanSettingsCancel");
        await Ui.Run(() => Ui.Click("PlanSettingsCancel"));
        await Ui.Until(() => !grid.PlanningSettingsOpen);
        await Ui.Run(() => Assert.That(grid.SelectionIdentity, Is.Null));
        await Ui.ClickCommand("GridPlanning");
        await Ui.Run(() => FocusCell("GridCell0_0"));
        await Ui.ClickCommand("GridPlanning"); await Ui.Ready<TextBox>("PlanProjectStart"); await Ui.Ready<Button>("PlanSettingsSave");
        await Ui.Run(() => {
            var dialog = Ui.Find<Grid>("PlanningSettingsPage");
            Assert.That(Ui.Tree(dialog).OfType<TextBlock>().Any(t => t.Text.Contains("P1") && t.Text.Contains("Project")), Is.True,
                "Setup must identify its Project-wide scope before saving.");
            Ui.Click("PlanSettingsSave");
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor")?.IsLoaded == true);
        await Ui.Run(() => {
            Assert.That(grid.SelectionIdentity?.Item, Is.EqualTo("P1T1"));
            Assert.That(Ui.Find<TextBlock>("DraftStatus").Text, Does.Not.Contain("選択してください"));
            var editor = Ui.Popup<StackPanel>("SchedulingEditor")!;
            Ui.Find<TextBox>("ScheduleStart", editor).Text = "2026-10-05 09:00";
            Ui.Find<TextBox>("ScheduleFinish", editor).Text = "2026-10-05 13:00";
        });
        await Ui.Until(() => Ui.Find<RadioButtons>("ScheduleMethod", Ui.Popup<StackPanel>("SchedulingEditor")).SelectedIndex == 1);
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ScheduleSettings", Ui.Popup<StackPanel>("SchedulingEditor"))));
        await Ui.Ready<Button>("PlanSettingsCancel");
        await Ui.Run(() => Ui.Click("PlanSettingsCancel"));
        await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor")?.IsLoaded == true);
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("SchedulingEditor")!;
            Assert.That(Ui.Find<TextBox>("ScheduleStart", editor).Text, Is.EqualTo("2026-10-05 09:00"));
            Assert.That(Ui.Find<TextBox>("ScheduleFinish", editor).Text, Is.EqualTo("2026-10-05 13:00"));
            Ui.Click(Ui.Find<Button>("ScheduleApply", editor));
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor") is null);
        await Ui.Run(() => {
            var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1];
        });
        await Ui.Ready<TextBlock>("GanttSelected");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GanttSelected").Text, Does.Contain("2026-10-05 09:00").And.Contain("2026-10-05 13:00"));
            Assert.That(session.Workspace.Planning("P1")!.Tasks.Single(t => t.Id == "I1").Mode, Is.EqualTo(PlanningMode.Manual));
            Assert.That(work.Journal, Is.Empty);
        });
    }
}
