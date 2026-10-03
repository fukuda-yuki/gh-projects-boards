using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class PlanningHostedTests
{
    [Test, Category("DailyProjectField")]
    public async Task DuplicateDailyProjectNamesAreDistinguishedBeforeSelectionAndKeepTheirExactTarget()
    {
        await PrepareDailyProgress(); await Ui.Unmount(grid);
        var work = session.Workspace;
        var original = project.Snapshot.Fields.Single(field => field.Id.NodeId == "P1-status");
        var other = original with { Id = new(original.Id.Scope, "P1-other-status"),
            Options = original.Options.Select(option => new SelectOption(option.Id + "-other", option.Name)).ToArray() };
        project = project with { Snapshot = project.Snapshot with {
            Fields = project.Snapshot.Fields.Append(other).ToArray(),
            Items = project.Snapshot.Items.Select(item => item with { Values = item.Values.Append(
                new(other.Id, "other-" + item.Id.NodeId, "ProjectV2ItemFieldSingleSelectValue", ValueAvailability.Present, "todo-other")).ToArray() }).ToArray()
        } };
        work.SetRegistrations([project]);
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<SelectorBar>("ProjectViews");
        await Ui.Run(() => { var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1]; });
        await Ui.Ready<ListView>("GanttTasks"); await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 0);
        var row = work.Open(project)[0];
        var firstCell = row.Cells.Single(cell => cell.Key?.FieldId == original.Id.NodeId);
        var otherCell = row.Cells.Single(cell => cell.Key?.FieldId == other.Id.NodeId);
        var beforePlan = JsonSerializer.Serialize(work.Planning("P1"));
        var beforeHistory = work.Snapshot().History.Length;
        await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
        ContentDialog dialog = null!;
        ComboBox fields = null!; ComboBox options = null!;
        await Ui.Run(() => { dialog = Ui.Dialog("DailyProgressDialog")!; fields = Ui.Find<ComboBox>("DailyProjectField", dialog); });
        await OpenChoices(fields);
        await Ui.Run(() => {
            Assert.That(fields.IsDropDownOpen, Is.True);
            Assert.That(((ComboBoxItem)fields.SelectedItem).Tag, Is.Null);
            Assert.That(fields.Items.Cast<ComboBoxItem>().Single(item => (string?)item.Tag == original.Id.NodeId).Content,
                Is.EqualTo("Renamed workflow [P1-status]"));
            Assert.That(fields.Items.Cast<ComboBoxItem>().Single(item => (string?)item.Tag == other.Id.NodeId).Content,
                Is.EqualTo("Renamed workflow [P1-other-status]"));
        });
        await SheetNativeInput.Press(VirtualKey.Escape);
        await Ui.Until(() => !fields.IsDropDownOpen);
        await Ui.Run(() => {
            SelectDailyOption("DailyProjectField", other.Id.NodeId, dialog);
            options = Ui.Find<ComboBox>("DailyProjectOption", dialog);
        });
        await OpenChoices(options);
        await Ui.Run(() => {
            Assert.That(options.IsDropDownOpen, Is.True);
            Assert.That(options.Items.Cast<ComboBoxItem>().Single(item => (string?)item.Tag == "dup1-other").Content,
                Is.EqualTo("Duplicate [dup1-other]"));
            Assert.That(options.Items.Cast<ComboBoxItem>().Single(item => (string?)item.Tag == "dup2-other").Content,
                Is.EqualTo("Duplicate [dup2-other]"));
            Assert.That(options.Header, Is.EqualTo("値"));
            Assert.That(AutomationProperties.GetName(options), Does.Contain("Renamed workflow").And.Contain(other.Id.NodeId));
        });
        await SheetNativeInput.Press(VirtualKey.Escape);
        await Ui.Until(() => !options.IsDropDownOpen);
        await SheetNativeInput.ActivateWindow();
        await Ui.Run(() => Assert.That(Ui.Find<Button>("DailyProjectFieldDetails", dialog).Focus(FocusState.Keyboard), Is.True));
        await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(dialog.XamlRoot), Ui.Find<Button>("DailyProjectFieldDetails", dialog)));
        await SheetNativeInput.Press(VirtualKey.Enter);
        await Ui.Until(() => Ui.Popup<TextBlock>("DailyProjectFieldIdentity")?.IsLoaded == true);
        await Ui.Run(() => {
            Assert.That(Ui.Popup<TextBlock>("DailyProjectFieldIdentity")!.Text, Does.Contain(other.Id.NodeId));
            Ui.Find<Button>("DailyProjectFieldDetails", dialog).Flyout.Hide();
        });
        await Ui.Until(() => !Ui.Find<Button>("DailyProjectFieldDetails", dialog).Flyout.IsOpen);
        await Ui.Run(() => {
            SelectDailyOption("DailyProjectOption", "dup2-other", dialog);
            Assert.That(Ui.Find<TextBlock>("DailyProjectFieldHint", dialog).Text, Does.Contain("変更候補").And.Contain("Todo"));
            Assert.That(work.Value(otherCell), Is.EqualTo("todo-other"));
            Assert.That(work.Value(firstCell), Is.EqualTo("todo"));
        });
        await CloseDailyAndWaitForSave("PrimaryButton");
        await Ui.Run(() => {
            Assert.That(work.Value(otherCell), Is.EqualTo("dup2-other")); Assert.That(work.Value(firstCell), Is.EqualTo("todo"));
            Assert.That(work.Snapshot().History, Has.Length.EqualTo(beforeHistory + 1));
            Assert.That(JsonSerializer.Serialize(work.Planning("P1")), Is.EqualTo(beforePlan)); Assert.That(work.Journal, Is.Empty);
        });
        await Ui.ClickCommand("GanttUndo");
        await Ui.Run(() => {
            Assert.That(work.Value(otherCell), Is.EqualTo("todo-other")); Assert.That(work.Value(firstCell), Is.EqualTo("todo"));
            Assert.That(JsonSerializer.Serialize(work.Planning("P1")), Is.EqualTo(beforePlan)); Assert.That(work.Journal, Is.Empty);
        });

        async Task OpenChoices(ComboBox box)
        {
            // Loaded item containers can survive a closed dropdown. Establish
            // native focus and open state before treating their labels as visible.
            await SheetNativeInput.Rendered();
            await SheetNativeInput.ActivateWindow();
            await Ui.Run(() => Assert.That(box.Focus(FocusState.Keyboard), Is.True));
            await Ui.Until(() => Ui.Tree(box).Contains(FocusManager.GetFocusedElement(dialog.XamlRoot) as DependencyObject));
            await SheetNativeInput.Press(VirtualKey.F4);
            await Ui.Until(() => box.IsDropDownOpen && box.Items.Cast<ComboBoxItem>().All(item => item.IsLoaded && item.ActualHeight > 0));
            await SheetNativeInput.Rendered();
        }
    }
}
