using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class PlanningHostedTests
{
    private async Task PrepareDailyProgress(ActualContribution[]? actuals = null)
    {
        await Ui.Unmount(grid);
        var work = session.Workspace; var plan = work.Planning("P1")!;
        work.CommitPlanning(project, plan with {
            People = Enumerable.Range(1, 20).Select(i => new PlanningPerson("U" + i, "Owner " + i, 100)).ToArray(),
            Cutoff = new(2026, 12, 1, 9, 0, 0),
            Tasks = [plan.Tasks[0] with { Actuals = actuals ?? [new("U1", 5, new(2026, 10, 8))] }, new("I2", PlanningMode.Auto, "U1")]
        }, work.Revision, [new("P1T1", "Estimate", "8"), new("P1T1", "Remaining", "4"), new("P1T2", "Estimate", "4")]);
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<SelectorBar>("ProjectViews");
        await Ui.Run(() => { var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1]; });
        await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 0);
    }

    [Test, Category("DailyProgress")]
    public async Task GanttProgressConfirmsEachTaskAsOneUndoAndOffersOffscreenScheduleWithoutMovingViewport()
    {
        await PrepareDailyProgress();
        var work = session.Workspace; var row = work.Open(project)[0];
        var actual = row.Cells.Single(c => c.Key?.FieldId == "F-Actual");
        var remaining = row.Cells.Single(c => c.Key?.FieldId == "F-Remaining");
        await Ui.Run(() => { work.SetPlanningBuffer(actual, "7"); work.SetBuffer(remaining, "3"); work.SetBuffer(row.Cells[0], "unrelated pending title"); });
        DateTime viewport = default;
        AppBarButton progressCommand = null!;
        await Ui.Run(() => {
            var view = Ui.Find<GanttView>("GanttView"); var horizontal = Ui.Find<ScrollViewer>("GanttHorizontal");
            viewport = view.Axis.Origin.AddDays(horizontal.HorizontalOffset / view.Axis.DayWidth);
            progressCommand = Ui.Find<CommandBar>("GanttCommands").PrimaryCommands.OfType<AppBarButton>()
                .Single(b => AutomationProperties.GetAutomationId(b) == "GanttProgress");
            Assert.That(progressCommand.IsEnabled, Is.True);
        });
        await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(async () => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            Assert.That(progressCommand.IsEnabled, Is.False, "The command remains owned until the dialog's close/save operation finishes.");
            Assert.That(Ui.Find<TextBox>("DailyActual", dialog).Text, Is.EqualTo("7"));
            Assert.That(Ui.Find<TextBox>("DailyRemaining", dialog).Text, Is.EqualTo("3"));
            Assert.That(Ui.Find<TextBlock>("DailyAdoptedSchedule", dialog).Text, Does.Contain("2026-10-05"));
            Ui.Find<ComboBox>("DailyProgress", dialog).SelectedIndex = (int)PlanningProgress.InProgress;
            Ui.Find<TextBox>("DailyActualStart", dialog).Text = "2026-10-05 09:00";
            Ui.Find<CalendarDatePicker>("DailyReportedThrough", dialog).Date = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.FromHours(9));
            await ApplyInformationEvidence.Capture(dialog, "daily-progress-candidate");
            Ui.DialogButton("DailyProgressDialog", "PrimaryButton");
            var error = Ui.Find<TextBlock>("DailyProgressStatus", dialog);
            Assert.That(error.Visibility, Is.EqualTo(Visibility.Collapsed), error.Text);
        });
        await Ui.Until(() => Ui.Dialog("DailyProgressDialog") is null);
        await Ui.Until(() => progressCommand.IsEnabled);
        await Ui.Run(() => {
            var task = work.PlanFor(project).Tasks.Single(t => t.Id == "I1");
            Assert.That(task.Start, Is.EqualTo(new DateTime(2026, 12, 1, 9, 0, 0)));
            Assert.That(task.Finish, Is.EqualTo(new DateTime(2026, 12, 1, 12, 0, 0)));
            Assert.That(work.Value(actual), Is.EqualTo("7")); Assert.That(work.Value(remaining), Is.EqualTo("3"));
            Assert.That(work.Buffer(actual), Is.Null); Assert.That(work.Buffer(remaining), Is.Null);
            Assert.That(work.Buffer(row.Cells[0]), Is.EqualTo("unrelated pending title"));
            var view = Ui.Find<GanttView>("GanttView"); var horizontal = Ui.Find<ScrollViewer>("GanttHorizontal");
            Assert.That(view.SelectedRowId, Is.EqualTo("P1T1"));
            Assert.That((view.Axis.Origin.AddDays(horizontal.HorizontalOffset / view.Axis.DayWidth) - viewport).TotalMinutes, Is.EqualTo(0).Within(1));
            Assert.That(Ui.Find<Button>("GanttChangedSchedule").Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(work.Journal, Is.Empty);
            Ui.Click("GanttChangedSchedule");
        });
        await Ui.Until(() => {
            var view = Ui.Find<GanttView>("GanttView"); var horizontal = Ui.Find<ScrollViewer>("GanttHorizontal");
            var position = view.Axis.Position(new(2026, 12, 1, 9, 0, 0)) - horizontal.HorizontalOffset;
            return position >= 0 && position <= horizontal.ViewportWidth;
        });
        await Ui.Run(() => {
            var view = Ui.Find<GanttView>("GanttView"); var horizontal = Ui.Find<ScrollViewer>("GanttHorizontal");
            Assert.That(view.Axis.Position(new(2026, 12, 1, 9, 0, 0)) - horizontal.HorizontalOffset, Is.InRange(0, horizontal.ViewportWidth));
            Ui.Find<ListView>("GanttTasks").SelectedIndex = 1;
        });
        await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            Ui.Find<TextBox>("DailyRemaining", dialog).Text = "2";
            Ui.Find<ComboBox>("DailyProgress", dialog).SelectedIndex = (int)PlanningProgress.InProgress;
            Ui.DialogButton("DailyProgressDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("DailyProgressDialog") is null);
        await Ui.Until(() => progressCommand.IsEnabled);
        await Ui.Run(() => {
            Assert.That(work.Value(work.Open(project)[1].Cells.Single(c => c.Key?.FieldId == "F-Remaining")), Is.EqualTo("2"));
            Assert.That(Ui.Find<GanttView>("GanttView").SelectedRowId, Is.EqualTo("P1T2"));
            Assert.That(work.Value(actual), Is.EqualTo("7")); Assert.That(work.Value(remaining), Is.EqualTo("3"));
        });
        await Ui.ClickCommand("GanttUndo");
        await Ui.Run(() => {
            Assert.That(work.Value(work.Open(project)[1].Cells.Single(c => c.Key?.FieldId == "F-Remaining")), Is.Null);
            Assert.That(work.Planning("P1")!.Tasks.Single(t => t.Id == "I2").Progress, Is.EqualTo(PlanningProgress.Unstarted));
            Assert.That(work.Value(actual), Is.EqualTo("7")); Assert.That(work.Value(remaining), Is.EqualTo("3"));
            Ui.Find<ListView>("GanttTasks").SelectedIndex = 0;
        });
        await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => Ui.DialogButton("DailyProgressDialog", "PrimaryButton"));
        await Ui.Until(() => Ui.Dialog("DailyProgressDialog") is null);
        await Ui.Until(() => progressCommand.IsEnabled);
        // Reopening and confirming an unchanged form must not put an empty
        // transaction ahead of the previous task update in Undo.
        await Ui.ClickCommand("GanttUndo");
        await Ui.Run(() => {
            Assert.That(work.Value(actual), Is.EqualTo("5")); Assert.That(work.Value(remaining), Is.EqualTo("4"));
            Assert.That(work.Planning("P1")!.Tasks.Single(t => t.Id == "I1").Progress, Is.EqualTo(PlanningProgress.Unstarted));
            Assert.That(work.Buffer(actual), Is.Null); Assert.That(work.Buffer(remaining), Is.Null);
            Assert.That(work.Buffer(row.Cells[0]), Is.EqualTo("unrelated pending title"));
        });
    }

    [Test, Category("DailyProgress")]
    public async Task InvalidDailyInputAndDirtyDetailsNavigationRetainCandidatesUntilExplicitCancel()
    {
        await PrepareDailyProgress();
        var work = session.Workspace; var row = work.Open(project)[0];
        var actual = row.Cells.Single(c => c.Key?.FieldId == "F-Actual");
        var remaining = row.Cells.Single(c => c.Key?.FieldId == "F-Remaining");
        var before = work.PlanFor(project).Tasks.Single(t => t.Id == "I1");
        await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            Assert.That(Ui.Find<TextBlock>("DailyActualPending", dialog).Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Ui.Find<TextBlock>("DailyRemainingPending", dialog).Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Ui.Find<TextBlock>("DailyCancelHint", dialog).Visibility, Is.EqualTo(Visibility.Collapsed));
            Ui.Find<TextBox>("DailyActual", dialog).Text = "7x";
            Ui.Find<TextBox>("DailyRemaining", dialog).Text = "2";
            Assert.That(Ui.Find<TextBlock>("DailyActualPending", dialog).Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(Ui.Find<TextBlock>("DailyActualPending", dialog).Text, Is.EqualTo("入力途中 · 確定済み 5"));
            Assert.That(Ui.Find<TextBlock>("DailyRemainingPending", dialog).Text, Is.EqualTo("入力途中 · 確定済み 4"));
            Ui.Find<ComboBox>("DailyProgress", dialog).SelectedIndex = (int)PlanningProgress.InProgress;
            Assert.That(work.Buffer(actual), Is.EqualTo("7x"), "The final raw input is retained before an immediate confirmation.");
            Assert.That(work.Buffer(remaining), Is.EqualTo("2"));
            Assert.That(Ui.Find<TextBlock>("DailyCancelHint", dialog).Visibility, Is.EqualTo(Visibility.Collapsed),
                "Input started in this form must not be described as an entry draft.");
            Ui.DialogButton("DailyProgressDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("DailyProgressStatus", Ui.Dialog("DailyProgressDialog")).Text.Length > 0);
        await Ui.Until(() => session.DurableRevision == work.Revision);
        await Ui.Run(() => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            Assert.That(work.Buffer(actual), Is.EqualTo("7x")); Assert.That(work.Buffer(remaining), Is.EqualTo("2"));
            Assert.That(work.Value(actual), Is.EqualTo("5")); Assert.That(work.Value(remaining), Is.EqualTo("4"));
            Assert.That(work.PlanFor(project).Tasks.Single(t => t.Id == "I1").Finish, Is.EqualTo(before.Finish));
            Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(Ui.Find<TextBox>("DailyActual", dialog)));
            Ui.Click(Ui.Find<Button>("DailyTaskDetails", dialog));
        });
        await Ui.Until(() => Ui.Find<Button>("DailyKeepEditing", Ui.Dialog("DailyProgressDialog")).IsLoaded);
        await Ui.Run(() => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            Assert.That(dialog.IsPrimaryButtonEnabled, Is.False);
            Ui.Click(Ui.Find<Button>("DailyKeepEditing", dialog));
            Assert.That(Ui.Find<TextBox>("DailyActual", dialog).Text, Is.EqualTo("7x"));
            Assert.That(Ui.Find<ComboBox>("DailyProgress", dialog).SelectedIndex, Is.EqualTo((int)PlanningProgress.InProgress));
            Ui.DialogButton("DailyProgressDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("DailyProgressDialog") is null);
        await Ui.Until(() => work.Buffer(actual) is null && work.Buffer(remaining) is null && session.DurableRevision == work.Revision);
        await Ui.Run(() => {
            Assert.That(work.Value(actual), Is.EqualTo("5")); Assert.That(work.Value(remaining), Is.EqualTo("4"));
            Assert.That(work.Buffer(actual), Is.Null); Assert.That(work.Buffer(remaining), Is.Null);
            Assert.That(work.Planning("P1")!.Tasks.Single(t => t.Id == "I1").Progress, Is.EqualTo(PlanningProgress.Unstarted));
            Assert.That(Ui.Find<GanttView>("GanttView").SelectedRowId, Is.EqualTo("P1T1"));
            Assert.That(work.Journal, Is.Empty);
        });
    }

    [Test, Category("DailyProgress")]
    public async Task ActualBreakdownShowsOnlyExistingReportsIncludingZeroAndAddsAWorkerExplicitly()
    {
        await PrepareDailyProgress([new("U1", 0, new(2026, 10, 8)), new(null, 3, new(2026, 10, 8))]);
        await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            Assert.That(Ui.Find<TextBox>("DailyActual", dialog).Text, Is.EqualTo("3"));
            Assert.That(Ui.Find<TextBox>("DailyActual", dialog).IsReadOnly, Is.True);
            Assert.That(Ui.Find<Grid>("DailyReportContext", dialog).Visibility, Is.EqualTo(Visibility.Collapsed),
                "Several reports do not have one common worker or reported-through date.");
            Assert.That(Ui.Tree(dialog).OfType<TextBlock>().Any(text => text.Text.Contains("複数人の実績")), Is.True);
            Ui.DialogButton("DailyProgressDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("DailyProgressDialog") is null);
        await Ui.ClickCommand("GanttTaskDetailsEdit"); await Ui.DialogReady("PlanningDialog");
        await Expand("工数・進捗・実績", "PlanProgress");
        await Expand("累積実績・担当者別内訳", "PlanActualHours-U1");
        await Ui.Run(async () => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            var reports = Ui.Tree(dialog).OfType<TextBox>().Where(t => AutomationProperties.GetAutomationId(t).StartsWith("PlanActualHours-")).ToArray();
            Assert.That(reports.Select(AutomationProperties.GetAutomationId), Is.EquivalentTo(new[] { "PlanActualHours-U1", "PlanActualHours-Unattributed" }));
            Assert.That(Ui.Find<TextBox>("PlanActualHours-U1", dialog).Text, Is.EqualTo("0"));
            Ui.Find<ComboBox>("PlanActualAddWorker", dialog).SelectedIndex = 1;
            Ui.Click(Ui.Find<Button>("PlanActualAdd", dialog));
            Assert.That(Ui.Find<TextBox>("PlanActualHours-U2", dialog).Text, Is.Empty);
            await ApplyInformationEvidence.Capture(dialog, "daily-progress-sparse-actual-reports");
            Ui.DialogButton("PlanningDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Run(() => Assert.That(session.Workspace.Planning("P1")!.Tasks.Single(t => t.Id == "I1").Actuals, Has.Length.EqualTo(2)));
    }
}
