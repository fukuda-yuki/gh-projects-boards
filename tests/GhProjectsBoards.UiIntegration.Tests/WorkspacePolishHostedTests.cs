using GhProjectsBoards.App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test, Category("WorkspacePolish")]
    public async Task PlanningIsDirectlyAvailableWithoutOpeningInfrastructureSettingsAndReturnsToWork()
    {
        await Ui.Run(() => {
            var planning = Ui.Tree(panel).OfType<Button>().SingleOrDefault(b => AutomationProperties.GetAutomationId(b) == "ProjectPlanningSettings");
            Assert.That(planning, Is.Not.Null, "Planning is a visible Project command, not a settings flyout entry.");
            Assert.That(planning!.IsLoaded && planning.IsEnabled && planning.ActualWidth > 0, Is.True);
            Ui.Click(planning);
        });
        await Ui.Ready<Button>("PlanSettingsCancel");
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("ReviewWeeklyApplyButton").IsEnabled, Is.False);
            Ui.Click("PlanSettingsCancel");
        });
        await Ui.Until(() => !Ui.Tree(panel).OfType<EditingGrid>().Single().PlanningSettingsOpen);
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("ProjectPlanningSettings").IsEnabled, Is.True);
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
