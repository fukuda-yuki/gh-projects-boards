using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class PlanningHostedTests
{
    [Test, Category("PmoWorkflow")]
    public async Task OpeningActualDetailsKeepsTheCandidateForExplicitAttribution()
    {
        await SheetNativeInput.Click("GridCell0_4");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_4").Text = "7");
        await SheetNativeInput.Click("ActualContext");
        await Ui.Ready<Button>("ActualUpdate");
        await Ui.Run(() => {
            var cell = session.Workspace.Open(project)[0].Cells[4];
            Assert.That(session.Workspace.Buffer(cell), Is.EqualTo("7"));
            Assert.That(session.Workspace.Value(cell), Is.Null);
            Assert.That(Ui.Find<StackPanel>("ActualCellEditor").Visibility, Is.EqualTo(Visibility.Visible));
        });
    }

    [Test, Category("PmoWorkflow")]
    public async Task MissingEstimateBadgeReturnsToTheAffectedBoardsTask()
    {
        await Ui.Run(() => { var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1]; });
        await Ui.Ready<Button>("GanttResolve-P1T1");
        await Ui.Run(() => {
            var badge = Ui.Find<Button>("GanttResolve-P1T1");
            Assert.That(badge.Content.ToString(), Does.Contain("見積未入力"));
            Ui.Click(badge);
        });
        await Ui.Until(() => grid.CurrentProjectView == ProjectView.Boards && grid.SelectionIdentity?.Item == "P1T1");
        await Ui.Run(() => Assert.That(session.Workspace.Journal, Is.Empty));
    }

    [Test, Category("PmoWorkflow")]
    public async Task ActualEnterCommitsWithVisibleReportingDateAndMovesDownWithoutConfirmation()
    {
        await SheetNativeInput.ActivateWindow();
        await Ui.Run(() => FocusCell("GridCell0_4"));
        await Ui.Ready<CalendarDatePicker>("ActualReportedThrough");
        await Ui.Run(() => {
            Ui.Find<CalendarDatePicker>("ActualReportedThrough").Date = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.FromHours(9));
            FocusCell("GridCell0_4");
            Ui.Find<TextBox>("GridCell0_4").Text = "7";
        });
        await SheetNativeInput.Press(VirtualKey.Enter);
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T2");
        await Ui.Run(() => {
            var work = session.Workspace;
            Assert.That(work.Planning("P1")!.Tasks.Single(t => t.Id == "I1").Actuals,
                Is.EqualTo(new[] { new ActualContribution("U1", 7, new(2026, 10, 9)) }));
            Assert.That(work.Buffer(work.Open(project)[0].Cells[4]), Is.Null);
            Assert.That(Ui.Find<CalendarDatePicker>("ActualReportedThrough").Date?.Day, Is.EqualTo(9));
            Assert.That(work.Journal, Is.Empty);
        });
        await Ui.ClickCommand("GridUndo");
        await Ui.Run(() => Assert.That(session.Workspace.Value(session.Workspace.Open(project)[0].Cells[4]), Is.Null));
    }

    [Test, Category("PmoWorkflow")]
    public async Task PlanningSettingsCannotSaveAnEmptyStartAndKeepsTheCorrectionInPlace()
    {
        await Ui.ClickCommand("GridPlanningSettings"); await Ui.Ready<Button>("PlanSettingsSave");
        await Ui.Run(() => {
            Ui.Find<TextBox>("PlanProjectStart").Text = ""; Ui.Click("PlanSettingsSave");
        });
        await Ui.Run(() => {
            Assert.That(grid.PlanningSettingsOpen, Is.True);
            Assert.That(Ui.Find<TextBlock>("PlanningStatus").Text, Does.Contain("開始日時"));
            Assert.That(session.Workspace.Planning("P1")!.Start, Is.Not.Null);
            Ui.Click("PlanSettingsCancel");
        });
    }

    [Test, Category("PmoWorkflow")]
    public async Task LeavingValidEffortCellCommitsAndInvalidTextRemainsCorrectable()
    {
        await SheetNativeInput.Click("GridCell0_2");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Text = "8");
        await SheetNativeInput.Click("GridCell1_2");
        await Ui.Run(() => {
            var work = session.Workspace;
            Assert.That(work.Value(work.Open(project)[0].Cells[2]), Is.EqualTo("8"));
            Assert.That(work.Buffer(work.Open(project)[0].Cells[2]), Is.Null);
            Ui.Find<TextBox>("GridCell1_2").Text = "-";
        });
        await SheetNativeInput.Click("GridCell0_2");
        await Ui.Run(() => {
            var work = session.Workspace;
            Assert.That(work.Buffer(work.Open(project)[1].Cells[2]), Is.EqualTo("-"));
            Assert.That(work.Value(work.Open(project)[1].Cells[2]), Is.Null);
            Assert.That(work.Journal, Is.Empty);
        });
    }
}
