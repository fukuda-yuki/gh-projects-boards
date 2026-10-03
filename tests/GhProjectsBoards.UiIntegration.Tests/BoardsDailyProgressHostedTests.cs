using System.Text.Json;
using GhProjectsBoards.App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class PlanningHostedTests
{
    [Test, Category("DailyProgress")]
    public async Task BoardsProgressEntryEditsTheSelectedTaskLocallyAndReopensTheSameTaskInGantt()
    {
        await PrepareDailyProgress();
        var work = session.Workspace;
        var rows = work.Open(project);
        var target = rows[1].Cells.Single(cell => cell.Key?.FieldId == "F-Remaining");
        var untouched = rows[0].Cells.Single(cell => cell.Key?.FieldId == "F-Remaining");
        var beforePlan = JsonSerializer.Serialize(work.Planning("P1"));
        var beforeHistory = work.Snapshot().History.Length;
        await Ui.Run(() => { var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[0]; });
        await Ui.Ready<FrameworkElement>("GridCell1_0");
        await Ui.Run(() => FocusCell("GridCell1_0"));
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T2");
        var selected = grid.SelectionIdentity;
        AppBarButton entry = null!;
        await Ui.Run(() => {
            entry = Ui.Find<CommandBar>("GridCommandBar").PrimaryCommands.OfType<AppBarButton>().Single(button =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button) == "GridDailyProgress");
            Assert.That(entry.Label, Is.EqualTo("実績・進捗")); Assert.That(entry.IsEnabled, Is.True);
            Assert.That(grid.CurrentProjectView, Is.EqualTo(ProjectView.Boards));
        });
        await Ui.ClickCommand("GridDailyProgress"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            Assert.That(Ui.Find<TextBlock>("DailyTaskIdentity", dialog).Text, Does.Contain("#2"));
            Assert.That(Ui.Find<TextBox>("DailyActual", dialog).IsReadOnly, Is.False);
            Assert.That(Ui.Find<ComboBox>("DailyProgress", dialog).Header, Is.EqualTo("計画上の進捗（日程計算）"));
            Assert.That(entry.IsEnabled, Is.False, "The command remains owned until its modal/save continuation finishes.");
            Ui.Find<TextBox>("DailyRemaining", dialog).Text = "2";
            Ui.DialogButton("DailyProgressDialog", "PrimaryButton");
            var status = Ui.Find<TextBlock>("DailyProgressStatus", dialog);
            Assert.That(status.Visibility, Is.EqualTo(Visibility.Collapsed), status.Text);
        });
        await Ui.Until(() => Ui.Dialog("DailyProgressDialog") is null && entry.IsEnabled && session.DurableRevision == work.Revision);
        await Ui.Run(() => {
            Assert.That(grid.CurrentProjectView, Is.EqualTo(ProjectView.Boards));
            Assert.That(grid.SelectionIdentity, Is.EqualTo(selected));
            Assert.That(work.Value(target), Is.EqualTo("2")); Assert.That(work.Value(untouched), Is.EqualTo("4"));
            Assert.That(work.Snapshot().History, Has.Length.EqualTo(beforeHistory + 1)); Assert.That(work.Journal, Is.Empty);
            var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1];
        });
        await Ui.Ready<ListView>("GanttTasks");
        await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            Assert.That(Ui.Find<TextBlock>("DailyTaskIdentity", dialog).Text, Does.Contain("#2"));
            Assert.That(Ui.Find<TextBox>("DailyRemaining", dialog).Text, Is.EqualTo("2"));
        });
        await CloseDailyAndWaitForSave("CloseButton");
        await Ui.ClickCommand("GanttUndo");
        await Ui.Run(() => {
            Assert.That(work.Value(target), Is.Null); Assert.That(work.Value(untouched), Is.EqualTo("4"));
            Assert.That(JsonSerializer.Serialize(work.Planning("P1")), Is.EqualTo(beforePlan)); Assert.That(work.Journal, Is.Empty);
        });
    }
}
