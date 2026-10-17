using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.UiIntegration.Tests;
internal static class ProjectChooserTestActions
{
    internal static async Task SelectOwner(string owner)
    {
        await Ui.Ready<ListView>("PlanProjectOwners");
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("PlanProjectOwners");
            list.SelectedItem = list.Items.Cast<OwnerChoice>().Single(o => o.Login == owner);
        });
        await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Cast<ProjectChoice>().Any(p => p.OwnerLogin == owner));
        await Ui.Ready<ListView>("AvailableProjects");
    }
}
