using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class PlanningHostedTests
{
    private static void SelectDailyOption(string controlId, string? id, ContentDialog dialog)
    {
        var box = Ui.Find<ComboBox>(controlId, dialog);
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().Single(item => (string?)item.Tag == id);
    }
    private async Task CloseDailyAndWaitForSave(string button)
    {
        await Ui.Run(() => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            Ui.DialogButton("DailyProgressDialog", button);
            if (button == "PrimaryButton")
            {
                var status = Ui.Find<TextBlock>("DailyProgressStatus", dialog);
                Assert.That(status.Visibility, Is.EqualTo(Visibility.Collapsed), status.Text);
            }
        });
        await Ui.Until(() => Ui.Dialog("DailyProgressDialog") is null);
        await Ui.Until(() => Ui.Find<CommandBar>("GanttCommands").PrimaryCommands.OfType<AppBarButton>()
            .Single(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button) == "GanttProgress").IsEnabled);
    }

    [Test, Category("DailyProjectField")]
    public async Task DailyProjectOptionIsExplicitAndConfirmsWithProgressAsOneUndo()
    {
        await PrepareDailyProgress();
        var work = session.Workspace; var row = work.Open(project)[0]; var option = row.Cells[1];
        var beforePlan = JsonSerializer.Serialize(work.Planning("P1")); var beforeHistory = work.Snapshot().History.Length;
        await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            Assert.That(Ui.Find<ComboBox>("DailyProgress", dialog).Header, Is.EqualTo("計画上の進捗（日程計算）"));
            Assert.That(((ComboBoxItem)Ui.Find<ComboBox>("DailyProjectField", dialog).SelectedItem).Tag, Is.Null);
            SelectDailyOption("DailyProjectField", "P1-status", dialog);
            Assert.That(((ComboBoxItem)Ui.Find<ComboBox>("DailyProjectField", dialog).SelectedItem).Content, Is.EqualTo("Renamed workflow"));
            Assert.That(Ui.Find<ComboBox>("DailyProjectOption", dialog).Header, Is.EqualTo("値"));
            Assert.That(AutomationProperties.GetName(Ui.Find<ComboBox>("DailyProjectOption", dialog)), Does.Contain("Renamed workflow"));
            Assert.That(Ui.Find<TextBlock>("DailyProjectFieldHint", dialog).Text, Does.Contain("確定済みのローカル値"));
            Assert.That(((ComboBoxItem)Ui.Find<ComboBox>("DailyProjectOption", dialog).SelectedItem).Tag, Is.EqualTo("todo"));
            Ui.Find<ComboBox>("DailyProgress", dialog).SelectedIndex = (int)PlanningProgress.InProgress;
            Assert.That(((ComboBoxItem)Ui.Find<ComboBox>("DailyProjectOption", dialog).SelectedItem).Tag, Is.EqualTo("todo"),
                "Planning progress must not guess a Project workflow mapping.");
            SelectDailyOption("DailyProjectOption", "done", dialog);
            Assert.That(Ui.Find<TextBlock>("DailyProjectFieldHint", dialog).Text, Does.Contain("変更候補").And.Contain("Todo").And.Not.Contain("現在のローカル値です"));
            Ui.Find<TextBox>("DailyActual", dialog).Text = "7";
            Ui.Find<TextBox>("DailyRemaining", dialog).Text = "3";
            Assert.That(Ui.Find<ComboBox>("DailyProjectField", dialog).IsEnabled, Is.False);
            Assert.That(work.Value(option), Is.EqualTo("todo"), "The optional selection remains a candidate until Confirm.");
        });
        await CloseDailyAndWaitForSave("PrimaryButton");
        await Ui.Run(() => {
            Assert.That(work.Value(option), Is.EqualTo("done")); Assert.That(work.Snapshot().History, Has.Length.EqualTo(beforeHistory + 1));
            Assert.That(work.Planning("P1")!.Tasks.Single(task => task.Id == "I1").Progress, Is.EqualTo(PlanningProgress.InProgress));
        });
        await Ui.ClickCommand("GanttUndo");
        await Ui.Run(() => {
            Assert.That(work.Value(option), Is.EqualTo("todo"));
            Assert.That(work.Value(row.Cells.Single(cell => cell.Key?.FieldId == "F-Actual")), Is.EqualTo("5"));
            Assert.That(work.Value(row.Cells.Single(cell => cell.Key?.FieldId == "F-Remaining")), Is.EqualTo("4"));
            Assert.That(JsonSerializer.Serialize(work.Planning("P1")), Is.EqualTo(beforePlan));
            Assert.That(work.Journal, Is.Empty);
            Ui.Find<ListView>("GanttTasks").SelectedIndex = 1;
        });
        await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            Assert.That(((ComboBoxItem)Ui.Find<ComboBox>("DailyProjectField", dialog).SelectedItem).Tag, Is.EqualTo("P1-status"));
            SelectDailyOption("DailyProjectOption", "done", dialog);
        });
        await CloseDailyAndWaitForSave("CloseButton");
        await Ui.Run(() => Assert.That(work.Value(work.Open(project)[1].Cells[1]), Is.EqualTo("todo")));
    }

    [Test, Category("DailyProjectField")]
    public async Task DailyFieldOnlyConfirmationAndResetKeepPlanningIndependent()
    {
        await PrepareDailyProgress(); var work = session.Workspace; var row = work.Open(project)[0];
        var beforePlan = JsonSerializer.Serialize(work.Planning("P1"));
        await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            SelectDailyOption("DailyProjectField", "P1-status", dialog); SelectDailyOption("DailyProjectOption", "done", dialog);
            Ui.Click(Ui.Find<Button>("DailyProjectFieldReset", dialog));
            Assert.That(Ui.Find<ComboBox>("DailyProjectField", dialog).IsEnabled, Is.True);
            Assert.That(((ComboBoxItem)Ui.Find<ComboBox>("DailyProjectOption", dialog).SelectedItem).Tag, Is.EqualTo("todo"));
            Assert.That(Ui.Find<TextBlock>("DailyProjectFieldHint", dialog).Text, Does.Contain("確定済みのローカル値").And.Not.Contain("変更候補"));
            SelectDailyOption("DailyProjectOption", "done", dialog);
        });
        await CloseDailyAndWaitForSave("PrimaryButton");
        await Ui.Run(() => {
            Assert.That(work.Value(row.Cells[1]), Is.EqualTo("done"));
            Assert.That(JsonSerializer.Serialize(work.Planning("P1")), Is.EqualTo(beforePlan));
        });
        await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => Assert.That(((ComboBoxItem)Ui.Find<ComboBox>("DailyProjectOption", Ui.Dialog("DailyProgressDialog")!).SelectedItem).Tag, Is.EqualTo("done")));
        await CloseDailyAndWaitForSave("PrimaryButton");
        await Ui.ClickCommand("GanttUndo");
        await Ui.Run(() => {
            Assert.That(work.Value(row.Cells[1]), Is.EqualTo("todo"));
            Assert.That(JsonSerializer.Serialize(work.Planning("P1")), Is.EqualTo(beforePlan)); Assert.That(work.Journal, Is.Empty);
        });
    }

    [TestCase(false), TestCase(true), Category("DailyProjectField")]
    public async Task UnavailableRememberedProjectFieldDoesNotRetargetOrBlockPlanning(bool conflict)
    {
        await PrepareDailyProgress(); var work = session.Workspace; var option = work.Open(project)[0].Cells[1];
        if (conflict)
        {
            await Ui.Unmount(grid);
            work.Commit("P1", option, "done", optionId: true);
            var current = project with { Snapshot = project.Snapshot with { Items = project.Snapshot.Items.Select(item => item.Id.NodeId == "P1T1"
                ? item with { Values = item.Values.Select(value => value.FieldId?.NodeId == option.Key!.FieldId ? value with { OptionId = "dup1" } : value).ToArray() }
                : item).ToArray() } };
            work.Reconcile(project, current); work.SetRegistrations([current]); project = current;
            Assert.That(work.Field(option)!.Conflict, Is.True);
            Assert.That(work.Field(option)!.Observation, Is.Not.Null);
            Assert.That(work.Field(option)!.Change?.Value, Is.EqualTo("done"));
            Assert.That(work.Value(option), Is.EqualTo("done"));
            session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-daily-field-" + Guid.NewGuid())), work, 0);
            await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)));
            await Ui.Mount(grid); await Ui.Ready<SelectorBar>("ProjectViews");
            await Ui.Run(() => { var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1]; });
            await Ui.Ready<ListView>("GanttTasks"); await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 0);
        }
        var retainedOption = work.Value(option);
        var selectedId = conflict ? "P1-status" : "removed-field";
        await Ui.Run(() => grid.DailyProjectFieldId = selectedId);
        await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            Assert.That(((ComboBoxItem)Ui.Find<ComboBox>("DailyProjectField", dialog).SelectedItem).Tag, Is.EqualTo(selectedId));
            if (!conflict) Assert.That(((ComboBoxItem)Ui.Find<ComboBox>("DailyProjectField", dialog).SelectedItem).Content,
                Is.EqualTo("確認できない項目 [" + selectedId + "]"), "An unavailable remembered ID stays visible without guessing its former name.");
            Assert.That(Ui.Find<ComboBox>("DailyProjectOption", dialog).IsEnabled, Is.False);
            Assert.That(Ui.Find<TextBlock>("DailyProjectFieldHint", dialog).Text, Does.Contain("変更せずに"));
            Ui.Find<ComboBox>("DailyProgress", dialog).SelectedIndex = (int)PlanningProgress.InProgress;
        });
        await CloseDailyAndWaitForSave("PrimaryButton");
        await Ui.Run(() => {
            Assert.That(work.Planning("P1")!.Tasks.Single(task => task.Id == "I1").Progress, Is.EqualTo(PlanningProgress.InProgress));
            Assert.That(work.Value(option), Is.EqualTo(retainedOption)); Assert.That(grid.DailyProjectFieldId, Is.EqualTo(selectedId)); Assert.That(work.Journal, Is.Empty);
        });
    }
}
