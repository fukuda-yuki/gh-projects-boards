using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.App;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class PlanningHostedTests
{
    [TestCase("PlanActualStart", "工数・進捗・実績", "実績開始", "2026-10-05 09:", "2026-10-05 09:00")]
    [TestCase("PlanFixedStart", "日程の制約", "固定開始", "2026-10-05 09:", "2026-10-05 09:00")]
    [TestCase("PlanWork-Remaining", "工数・進捗・実績", "残時間", "3x", "3")]
    [Category("WeeklyRecovery")]
    public async Task TaskCorrectionKeepsCandidatesAndShowsTheInvalidFieldWithoutSearching(string id, string section, string label, string invalid, string corrected)
    {
        await Ui.Run(() => FocusCell("GridCell0_2"));
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T1");
        await Ui.ClickCommand("GridTaskDetails"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<Expander>().Single(e => (string)e.Header == section).IsExpanded = true);
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<TextBox>().Any(c => AutomationProperties.GetAutomationId(c) == id && c.IsLoaded));
        var before = session.Workspace.Revision;
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            Ui.Find<TextBox>(id, dialog).Text = invalid;
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanningStatus", Ui.Dialog("PlanningDialog")).Text.Length > 0);
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            var input = Ui.Find<TextBox>(id, dialog); var status = Ui.Find<TextBlock>("PlanningStatus", dialog);
            Assert.Multiple(() => {
                Assert.That(session.Workspace.Revision, Is.EqualTo(before));
                Assert.That(input.Text, Is.EqualTo(invalid));
                Assert.That(status.Text, Does.Contain(label));
                Assert.That(input.Description?.ToString() ?? "", Does.Contain(label));
                Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(input));
                var bounds = status.TransformToVisual(dialog).TransformBounds(new(0, 0, status.ActualWidth, status.ActualHeight));
                Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0));
                Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(dialog.ActualHeight), "The error must be visible without discovering it below the scroll viewport.");
            });
            Ui.Find<TextBox>(id == "PlanFixedStart" ? "PlanEarliest" : "PlanActualFinish", dialog).Text = "2026-10-06 09:00";
            Assert.That(status.Visibility, Is.EqualTo(Visibility.Visible));
            input.Text = invalid + "x";
        });
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            Assert.That(Ui.Find<TextBlock>("PlanningStatus", dialog).Visibility, Is.EqualTo(Visibility.Visible));
            Ui.Find<TextBox>(id, dialog).Text = corrected;
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanningStatus", Ui.Dialog("PlanningDialog")).Visibility == Visibility.Collapsed);
        await Ui.Run(() => {
            Assert.That(session.Workspace.Revision, Is.EqualTo(before), "Correction is still a candidate until Save.");
            Ui.DialogButton("PlanningDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Run(() => Assert.That(session.Workspace.Revision, Is.EqualTo(before)));
    }

    private async Task OpenWeeklyProgressCorrection(bool retainedTotal = false)
    {
        if (retainedTotal) await ReviewFixture(PlanningReviewRegressionTests.Observed("Actual", "7"), PlanningPathTests.Plan() with {
            Version = 3, Tasks = [new("I1", PlanningMode.Auto, "U1", Assignment: new(["U1"], true))] });
        await Ui.Unmount(grid);
        var work = session.Workspace; var plan = work.Planning("P1")!;
        work.CommitPlanning(project, plan with { Version = 3, Cutoff = new(2026, 10, 9, 18, 0, 0), Tasks = [plan.Tasks[0] with {
            Assignment = new(["U1"], true), Actuals = retainedTotal ? null : [new("U1", 7, new(2026, 10, 8))] }] }, work.Revision,
            [new("P1T1", "Estimate", "4"), new("P1T1", "Remaining", "3")]);
        work.SetBuffer(work.Open(project)[0].Cells.Single(c => c.Key?.FieldId == "F-Remaining"), "3x");
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<SelectorBar>("ProjectViews");
        await Ui.Run(() => { var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1]; });
        await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 0);
        await Ui.ClickCommand("GanttDetails");
        await Ui.Until(() => Ui.Popup<StackPanel>("GanttTaskDetails")?.IsLoaded == true);
        await Ui.Until(() => Ui.Popup<Button>("GanttExplanationProgress") is { IsLoaded: true, IsEnabled: true });
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("GanttExplanationProgress", Ui.Popup<StackPanel>("GanttTaskDetails"))));
        await Ui.DialogReady("PlanningDialog");
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<ComboBox>().Any(c => AutomationProperties.GetAutomationId(c) == "PlanProgress" && c.IsLoaded));
        await Ui.Until(() => Ui.Tree(Ui.Find<ComboBox>("PlanProgress", Ui.Dialog("PlanningDialog"))).Contains(FocusManager.GetFocusedElement(grid.XamlRoot) as DependencyObject));
        // A transient Loaded focus can be replaced by the modal opening transition.
        await Task.Delay(400);
    }

    [Test, Category("WeeklyRecovery")]
    public async Task ReasonLeadsToProgressAndSavingUsesConfirmedRemainingWithoutConsumingPendingText()
    {
        await OpenWeeklyProgressCorrection();
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!; var progress = Ui.Find<ComboBox>("PlanProgress", dialog);
            var identity = Ui.Find<TextBlock>("PlanTaskIdentity", dialog);
            var title = session.Workspace.Value(session.Workspace.Open(project)[0].Cells[0]);
            Assert.That(identity.Text, Does.Contain(title).And.Contain("#1").And.Contain("owner/repo"));
            var identityBounds = identity.TransformToVisual(dialog).TransformBounds(new(0, 0, identity.ActualWidth, identity.ActualHeight));
            Assert.That(identityBounds.Top, Is.GreaterThanOrEqualTo(0));
            Assert.That(identityBounds.Bottom, Is.LessThan(progress.TransformToVisual(dialog).TransformPoint(new(0, 0)).Y));
            Assert.That(Ui.Tree(progress).Contains(FocusManager.GetFocusedElement(grid.XamlRoot) as DependencyObject), Is.True,
                "Focus must be on the progress control or its native input template.");
            Assert.That(Ui.Find<TextBlock>("PlanPending-Remaining", dialog).Text, Does.Contain("3人時").And.Contain("3x").And.Contain("計算対象外"));
            Assert.That(Ui.Find<TextBox>("PlanWork-Remaining", dialog).IsReadOnly, Is.True);
            Assert.That(Ui.Tree(dialog).OfType<Expander>().Single(e => (string)e.Header == "日程の制約").IsExpanded, Is.False);
            var bounds = progress.TransformToVisual(dialog).TransformBounds(new(0, 0, progress.ActualWidth, progress.ActualHeight));
            Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0)); Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(dialog.ActualHeight));
            progress.SelectedIndex = (int)PlanningProgress.InProgress;
            Assert.That(Ui.Find<TextBlock>("PlanProgressEffect", dialog).Text, Does.Contain("確定済みの残時間 3人時").And.Contain("2026-10-09 18:00"));
            Ui.Find<TextBox>("PlanActualStart", dialog).Text = "2026-10-05 09:00";
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Run(() => {
            var w = session.Workspace; var task = w.PlanFor(project).Tasks.Single(t => t.Id == "I1"); var row = w.Open(project)[0];
            Assert.That(task.Start, Is.EqualTo(new DateTime(2026, 10, 13, 9, 0, 0)));
            Assert.That(task.Finish, Is.EqualTo(new DateTime(2026, 10, 13, 12, 0, 0)));
            Assert.That(w.Buffer(row.Cells[3]), Is.EqualTo("3x")); Assert.That(w.Value(row.Cells[3]), Is.EqualTo("3"));
            Assert.That(w.Value(row.Cells[2]), Is.EqualTo("4")); Assert.That(w.Value(row.Cells[4]), Is.EqualTo("7"));
            Assert.That(w.Planning("P1")!.Tasks[0].Actuals![0].ReportedThrough, Is.EqualTo(new DateOnly(2026, 10, 8)));
            Assert.That(w.Journal, Is.Empty);
        });
        await Ui.ClickCommand("GanttUndo");
        await Ui.Run(() => {
            var w = session.Workspace;
            Assert.That(w.Planning("P1")!.Tasks[0].Progress, Is.EqualTo(PlanningProgress.Unstarted));
            Assert.That(w.Planning("P1")!.Tasks[0].ActualStart, Is.Null);
            Assert.That(w.Buffer(w.Open(project)[0].Cells[3]), Is.EqualTo("3x"));
        });
    }

    [Test, Category("WeeklyRecovery")]
    public async Task ReturningToThePendingCellRequiresExplicitDiscardAndPreservesTheSameTaskAndInput()
    {
        await OpenWeeklyProgressCorrection(); var before = session.Workspace.Revision;
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            Ui.Find<ComboBox>("PlanProgress", dialog).SelectedIndex = (int)PlanningProgress.InProgress;
            Ui.Find<TextBox>("PlanActualStart", dialog).Text = "2026-10-05 09:";
            Ui.Click(Ui.Find<Button>("PlanPendingEdit-Remaining", dialog));
        });
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "PlanKeepEditing" && b.IsLoaded));
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            Assert.That(session.Workspace.Revision, Is.EqualTo(before)); Assert.That(dialog.IsPrimaryButtonEnabled, Is.False);
            Ui.Click(Ui.Find<Button>("PlanKeepEditing", dialog));
            Assert.That(Ui.Find<TextBox>("PlanActualStart", dialog).Text, Is.EqualTo("2026-10-05 09:"));
            Assert.That(Ui.Find<ComboBox>("PlanProgress", dialog).SelectedIndex, Is.EqualTo((int)PlanningProgress.InProgress));
            Ui.Click(Ui.Find<Button>("PlanPendingEdit-Remaining", dialog));
            Ui.Click(Ui.Find<Button>("PlanDiscardToCell", dialog));
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null && !grid.ShowingGantt);
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T1" && grid.SelectionIdentity?.Field?.FieldId == "F-Remaining");
        await Ui.Run(() => {
            Assert.That(CellText("GridCell0_3"), Is.EqualTo("3x"));
            Assert.That(session.Workspace.Revision, Is.EqualTo(before));
            Assert.That(session.Workspace.Planning("P1")!.Tasks[0].Progress, Is.EqualTo(PlanningProgress.Unstarted));
            Assert.That(session.Workspace.Planning("P1")!.Tasks[0].ActualStart, Is.Null);
        });
    }

    [Test, Category("WeeklyRecovery")]
    public async Task RemovingARetainedActualTotalIsACandidateEvenWhenItsBreakdownWasNotEntered()
    {
        await OpenWeeklyProgressCorrection(retainedTotal: true); var before = session.Workspace.Revision;
        await Ui.Run(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<Expander>().Single(e => (string)e.Header == "累積実績・担当者別内訳").IsExpanded = true);
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "PlanRemoveReports" && b.IsLoaded));
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            Ui.Click(Ui.Find<Button>("PlanRemoveReports", dialog));
            Ui.Click(Ui.Find<Button>("PlanPendingEdit-Remaining", dialog));
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is { IsPrimaryButtonEnabled: false });
        await Ui.Run(() => {
            Assert.That(session.Workspace.Planning("P1")!.Tasks[0].Actuals, Is.Null);
            Assert.That(session.Workspace.Value(session.Workspace.Open(project)[0].Cells[4]), Is.EqualTo("7"));
            Ui.Click(Ui.Find<Button>("PlanDiscardToCell", Ui.Dialog("PlanningDialog")));
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null && !grid.ShowingGantt);
        await Ui.Run(() => {
            Assert.That(session.Workspace.Revision, Is.EqualTo(before));
            Assert.That(session.Workspace.Value(session.Workspace.Open(project)[0].Cells[4]), Is.EqualTo("7"));
            Assert.That(CellText("GridCell0_3"), Is.EqualTo("3x"));
        });
    }

    [Test, Category("WeeklyRecovery")]
    public async Task CorrectingOneErrorRetainsTheOtherAndDateOrderAcceptsACorrectionToEitherEndpoint()
    {
        await Ui.Run(() => FocusCell("GridCell0_2")); await Ui.ClickCommand("GridTaskDetails"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<Expander>().Single(e => (string)e.Header == "工数・進捗・実績").IsExpanded = true);
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<TextBox>().Any(c => AutomationProperties.GetAutomationId(c) == "PlanActualStart" && c.IsLoaded));
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            Ui.Find<TextBox>("PlanActualStart", dialog).Text = "2026-10-06 09:00";
            Ui.Find<TextBox>("PlanActualFinish", dialog).Text = "2026-10-05 09:00";
            Ui.Find<TextBox>("PlanWork-Remaining", dialog).Text = "3x";
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanningStatus", Ui.Dialog("PlanningDialog")).Text.Contains("2項目"));
        await Ui.Run(() => Ui.Find<TextBox>("PlanWork-Remaining", Ui.Dialog("PlanningDialog")).Text = "3");
        await Ui.Until(() => Ui.Find<TextBlock>("PlanningStatus", Ui.Dialog("PlanningDialog")).Text.Contains("実績終了"));
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            Assert.That(Ui.Find<TextBox>("PlanActualFinish", dialog).Description?.ToString(), Does.Contain("実績開始以降"));
            Ui.Find<TextBox>("PlanActualStart", dialog).Text = "2026-10-04 09:00";
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanningStatus", Ui.Dialog("PlanningDialog")).Visibility == Visibility.Collapsed);
        await Ui.Run(() => Ui.DialogButton("PlanningDialog", "CloseButton"));
    }

    [Test, Category("WeeklyRecovery")]
    public async Task CompletingAManualTaskExplainsAndRetainsItsSpecifiedDates()
    {
        await Ui.Run(() => {
            var w = session.Workspace; var p = w.Planning("P1")!;
            w.CommitPlanning(project, p with { Tasks = [p.Tasks[0] with { Mode = PlanningMode.Manual,
                ManualStart = new(2026, 10, 5, 9, 0, 0), ManualFinish = new(2026, 10, 5, 13, 0, 0) }] }, w.Revision,
                [new("P1T1", "Remaining", "0")]);
            FocusCell("GridCell0_2");
        });
        await Ui.ClickCommand("GridTaskDetails"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<Expander>().Single(e => (string)e.Header == "工数・進捗・実績").IsExpanded = true);
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<ComboBox>().Any(c => AutomationProperties.GetAutomationId(c) == "PlanProgress" && c.IsLoaded));
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            Ui.Find<ComboBox>("PlanProgress", dialog).SelectedIndex = (int)PlanningProgress.Completed;
            Assert.That(Ui.Find<TextBlock>("PlanProgressEffect", dialog).Text, Does.Contain("指定した日程を保持").And.Not.Contain("実績開始・終了を採用"));
            Ui.Find<TextBox>("PlanActualStart", dialog).Text = "2026-10-05 09:00";
            Ui.Find<TextBox>("PlanActualFinish", dialog).Text = "2026-10-05 12:00";
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Run(() => {
            var w = session.Workspace;
            Assert.That(w.Planning("P1")!.Tasks[0].Progress, Is.EqualTo(PlanningProgress.Completed));
            Assert.That(w.PlanFor(project).Tasks.Single(t => t.Id == "I1").Finish, Is.EqualTo(new DateTime(2026, 10, 5, 13, 0, 0)));
            var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1];
        });
        await Ui.Ready<ListView>("GanttTasks");
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 0);
        await Ui.ClickCommand("GanttDetails"); await Ui.Until(() => Ui.Popup<StackPanel>("GanttTaskDetails")?.IsLoaded == true);
        await Ui.Run(() => {
            var text = string.Join("\n", Ui.Tree(Ui.Popup<StackPanel>("GanttTaskDetails")!).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain("指定した開始・終了を採用").And.Not.Contain("完了：実績開始・終了日時を採用"));
            Ui.Find<AppBarButton>("GanttDetails").Flyout.Hide();
        });
    }
}
