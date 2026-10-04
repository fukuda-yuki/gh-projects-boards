using GhProjectsBoards.App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test, Category("WorkspaceRefinement")]
    public async Task NarrowProjectCommandsStayReachableAndHistorySurvivesNoProjectSelection()
    {
        await Ui.Run(() => panel.Width = 760);
        await Ui.Until(() => panel.ActualWidth == 760);
        await Ui.Run(() => {
            var commands = Ui.Find<CommandBar>("ProjectCommandBar");
            Assert.That(commands.ActualHeight, Is.LessThanOrEqualTo(56), "Project commands must not consume multiple workspace rows.");
            commands.IsOpen = true;
        });
        await Ui.ClickCommand("ProjectPlanningSettings");
        await Ui.Ready<Button>("PlanSettingsCancel");
        await Ui.Run(() => Ui.Click("PlanSettingsCancel"));
        await Ui.Until(() => !Ui.Tree(panel).OfType<EditingGrid>().Single().PlanningSettingsOpen);
        await Ui.Run(async () => await Workspace.SelectProfileAsync(Workspace.Profile));
        await Ui.Run(() => {
            var commands = Ui.Find<CommandBar>("ProjectCommandBar");
            Assert.That(commands.Visibility, Is.EqualTo(Visibility.Visible));
            var buttons = commands.PrimaryCommands.Concat(commands.SecondaryCommands).OfType<AppBarButton>().ToArray();
            Assert.That(buttons.Where(button => button.Visibility == Visibility.Visible).Select(button => AutomationProperties.GetAutomationId(button)),
                Is.EquivalentTo(new[] { "ApplyHistoryButton" }));
            Assert.That(Workspace.Selected, Is.Null);
            Assert.That(Workspace.Drafts, Is.Not.Null);
            Assert.That(h.Writes, Is.Empty);
        });
        await Ui.ClickCommand("ApplyHistoryButton");
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => Ui.DialogButton("ApplyHistoryDialog", "CloseButton"));
    }

    [Test, Category("WorkspacePolish")]
    public async Task PlanningIsDirectlyAvailableWithoutOpeningInfrastructureSettingsAndReturnsToWork()
    {
        await Ui.ClickCommand("ProjectPlanningSettings");
        await Ui.Ready<Button>("PlanSettingsCancel");
        await Ui.Run(() => {
            Assert.That(Ui.ProjectCommand("ReviewWeeklyApplyButton").IsEnabled, Is.False);
            Ui.Click("PlanSettingsCancel");
        });
        await Ui.Until(() => !Ui.Tree(panel).OfType<EditingGrid>().Single().PlanningSettingsOpen);
        await Ui.Run(() => {
            Assert.That(Ui.ProjectCommand("ProjectPlanningSettings").IsEnabled, Is.True);
            Assert.That(Work.Journal, Is.Empty);
            Assert.That(h.Writes, Is.Empty);
        });
    }

    [Test, Category("WorkspacePolish")]
    public async Task KeyboardHelpDisclosesBulkEditingWithoutIdleInstructionParagraphs()
    {
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Not.Contain("Shiftで範囲選択"));
            Ui.Click("GridInputHelp");
        });
        await Ui.Until(() => Ui.Popup<TextBlock>("GridInputHelpText")?.IsLoaded == true);
        await Ui.Run(() => {
            var help = Ui.Popup<TextBlock>("GridInputHelpText")!;
            Assert.That(help.Text, Does.Contain("Ctrl+D"));
            Assert.That(help.Text, Does.Contain("TSV"));
            Assert.That(help.Text, Does.Contain("元に戻す"));
            Assert.That(h.Writes, Is.Empty);
        });
    }
}
