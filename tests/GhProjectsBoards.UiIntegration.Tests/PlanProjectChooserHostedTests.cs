using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
using Windows.Foundation;

namespace GhProjectsBoards.UiIntegration.Tests;
internal sealed partial class PlanWorkspaceHostedTests
{
    [TestCase("owner"), TestCase("reconnect"), TestCase("close"), TestCase("url"), Category("PlanWorkspaceReview")]
    public async Task ProjectChooserCancelsOldListingBeforeNavigationAndNeverShowsIt(string action)
    {
        File.WriteAllText(Path.Combine(root, "scenario.json"), """{"planEditor":true,"workspace":true,"holdQuery":"RegistrationProjects"}""");
        await Ui.Run(() => Ui.Click("PlanConnect"));
        var marker = Path.Combine(root, "held-gh.pid");
        int pid = 0;
        await Ui.Until(() => { try { return File.Exists(marker) && int.TryParse(File.ReadAllText(marker), out pid); } catch (IOException) { return false; } });
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        var previousContext = workspace.Context;
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanProjectListingStatus").Text, Is.EqualTo("読み込み中…"));
            Assert.That(Ui.Find<ContentControl>("PlanBusyPanel").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Ui.Find<ListView>("PlanProjectOwners").IsEnabled, Is.True);
            Assert.That(Ui.Find<ListView>("AvailableProjects").Items, Is.Empty);
        });
        File.WriteAllText(Path.Combine(root, "scenario.json"), """{"planEditor":true,"workspace":true}""");
        if (action == "owner") {
            await ProjectChooserTestActions.SelectOwner("acme");
            await Ui.Run(() => Assert.That(Ui.Find<ListView>("AvailableProjects").Items.Cast<ProjectChoice>().Select(p => p.Id.NodeId), Is.EqualTo(new[] { "P2" })));
        } else if (action == "reconnect") {
            await PickerCommand("PlanConnection");
            await Ui.Run(() => Ui.Click("PlanConnect"));
            await Ui.Until(() => !ReferenceEquals(workspace.Context, previousContext) && Ui.Find<ListView>("AvailableProjects").Items.Count == 1);
            await Ui.Run(() => Assert.That(Ui.Find<ListView>("AvailableProjects").Items.Cast<ProjectChoice>().Single().Id.NodeId, Is.EqualTo("P1")));
            Assert.That(File.ReadAllLines(Path.Combine(root, "listing-requests.txt")), Is.EqualTo(new[] { "fixture-user" }), "Only the new connection's response completes.");
        } else if (action == "url") {
            await Ui.Run(() => Ui.Tree(view).OfType<Expander>().Single(e => Equals(e.Header, "URLで開く")).IsExpanded = true);
            await Ui.Ready<Button>("PlanOpenUrl");
            await Ui.Run(() => { Ui.Find<TextBox>("PlanProjectUrl").Text = "https://github.com/users/fixture-user/projects/3"; Ui.Click("PlanOpenUrl"); });
            await Ui.Until(() => workspace.Selected?.Id.NodeId == "P1");
        } else await Ui.Run(async () => Assert.That(await view.StopAsync(), Is.True));
        process.Refresh(); Assert.That(process.HasExited, Is.True);
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty));
    }

    [Test, Category("PlanWorkspaceReview")]
    public async Task ProjectChooserFiltersOwnersRepositoriesSearchClosedAndRecoversInPlace()
    {
        File.WriteAllText(Path.Combine(root, "scenario.json"), """{"planEditor":true,"workspace":true,"chooserCatalog":true}""");
        await Ui.Run(() => { view.Width = 1920; view.Height = 1032; Ui.Click("PlanConnect"); });
        await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Count == 60);
        await Ui.Until(() => Ui.Tree(Ui.Find<ListView>("AvailableProjects")).OfType<ScrollViewer>().Any(s => s.ScrollableHeight > 0));
        await Ui.Run(() => {
            Assert.That(((OwnerChoice)Ui.Find<ListView>("PlanProjectOwners").SelectedItem).Login, Is.EqualTo("fixture-user"));
            Assert.That(Ui.Find<ListView>("AvailableProjects").Items.Cast<ProjectChoice>().Select(p => p.Number),
                Is.EqualTo(Enumerable.Range(1, 80).Where(i => i % 4 != 0).Select(i => i == 1 ? 3 : i + 10).Reverse()));
            Assert.That(Ui.Find<CheckBox>("PlanProjectShowClosed").Content, Is.EqualTo("終了済みも表示（20）"));
            foreach (var id in new[] { "PlanProjectOwners", "PlanProjectRepositories", "AvailableProjects", "PlanProjectSearch", "PlanProjectShowClosed" }) {
                var element = Ui.Find<FrameworkElement>(id);
                var bounds = element.TransformToVisual(view).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(0)); Assert.That(bounds.Right, Is.LessThanOrEqualTo(1920));
                Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0)); Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(1032));
            }
            Assert.That(Ui.Tree(Ui.Find<ListView>("AvailableProjects")).OfType<ScrollViewer>().Any(s => s.ScrollableHeight > 0), Is.True);
            Ui.Find<CheckBox>("PlanProjectShowClosed").IsChecked = true;
        });
        await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Count == 80);
        await Ui.Until(() => Ui.Tree(Ui.Find<ListView>("AvailableProjects")).OfType<TextBlock>().Any(t => t.Text == "終了"));
        await Ui.Run(() => Assert.That(Ui.Find<ListView>("AvailableProjects").Items.Cast<ProjectChoice>().Select(p => p.Number),
            Is.EqualTo(Enumerable.Range(1, 80).Select(i => i == 1 ? 3 : i + 10).Reverse())));
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "project-chooser"));
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("PlanProjectRepositories");
            list.SelectedItem = list.Items.Cast<ProjectRepositoryChoice>().Single(r => r.NameWithOwner == "fixture-user/alpha");
            Assert.That(Ui.Find<ListView>("AvailableProjects").Items.Cast<ProjectChoice>().Select(p => p.Number),
                Is.EquivalentTo(Enumerable.Range(1, 80).Where(i => i % 3 == 1).Select(i => i == 1 ? 3 : i + 10)));
            Assert.That(Ui.Find<CheckBox>("PlanProjectShowClosed").Content, Is.EqualTo("終了済みも表示（7）"));
            list.SelectedItem = list.Items.Cast<ProjectRepositoryChoice>().Single(r => r.Kind == ProjectRepositoryKind.Unlinked);
            Assert.That(Ui.Find<ListView>("AvailableProjects").Items.Cast<ProjectChoice>().Select(p => p.Number),
                Is.EquivalentTo(Enumerable.Range(1, 80).Where(i => i % 3 == 0).Select(i => i + 10)));
            Ui.Find<TextBox>("PlanProjectSearch").Text = " #13 ";
        });
        await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Cast<ProjectChoice>().Select(p => p.Number).SequenceEqual(new[] { 13 }));
        await Ui.Run(() => {
            Assert.That(Ui.Find<ListView>("AvailableProjects").Items.Cast<ProjectChoice>().Single().Number, Is.EqualTo(13));
            Ui.Find<TextBox>("PlanProjectSearch").Text = "no match";
        });
        await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Count == 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<ListView>("AvailableProjects").Items, Is.Empty);
            Assert.That(Ui.Find<TextBlock>("PlanProjectListingStatus").Text, Is.EqualTo("検索条件に一致するプロジェクトがありません。"));
            Assert.That(Ui.Find<CheckBox>("PlanProjectShowClosed").Content, Is.EqualTo("終了済みも表示（6）"));
            Ui.Find<TextBox>("PlanProjectSearch").Text = "";
        });
        await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Count == 26);
        var reads = File.ReadAllLines(Path.Combine(root, "listing-requests.txt")).Length;
        await Ui.Run(() => Ui.Click("PlanProjectsReload"));
        await Ui.Until(() => File.ReadAllLines(Path.Combine(root, "listing-requests.txt")).Length == reads + 2 && Ui.Find<ListView>("AvailableProjects").Items.Count == 26);
        await Ui.Run(() => Assert.That(((ProjectRepositoryChoice)Ui.Find<ListView>("PlanProjectRepositories").SelectedItem).Kind, Is.EqualTo(ProjectRepositoryKind.Unlinked)));
        await ProjectChooserTestActions.SelectOwner("acme");
        await Ui.Run(() => {
            Assert.That(((ProjectRepositoryChoice)Ui.Find<ListView>("PlanProjectRepositories").SelectedItem).Kind, Is.EqualTo(ProjectRepositoryKind.All));
            Assert.That(Ui.Find<CheckBox>("PlanProjectShowClosed").IsChecked, Is.True);
            var owners = Ui.Find<ListView>("PlanProjectOwners"); owners.SelectedItem = owners.Items.Cast<OwnerChoice>().Single(o => o.Login == "broken");
        });
        // The retry button is part of the loaded tree while collapsed; gh retries a 500 before the failure is shown.
        await Ui.Until(() => Ui.Find<Button>("PlanProjectsRetry").Visibility == Visibility.Visible);
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanProjectListingStatus").Text, Does.Contain("取得できません")));
        File.WriteAllText(Path.Combine(root, "recover-owner"), "");
        await Ui.Run(() => Ui.Click("PlanProjectsRetry"));
        await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Count == 1);
        await ProjectChooserTestActions.SelectOwner("fixture-user");
        await Ui.Run(() => Ui.Find<TextBox>("PlanProjectSearch").Text = "#3");
        await Ui.Until(() => Ui.Find<ListView>("AvailableProjects") is var list && list.ContainerFromIndex(0) is ListViewItem { IsLoaded: true, IsEnabled: true } item && list.ItemFromContainer(item) is ProjectChoice { Number: 3 });
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P1"); await Ui.Idle();
        var requests = File.ReadAllLines(Path.Combine(root, "listing-requests.txt")).Length;
        await PickerCommand("PlanChooseProject");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanProjectSearch").Text, Is.EqualTo("#3"));
            Assert.That(Ui.Find<ListView>("PlanRecentProjects").Items.Cast<ProjectChoice>().Select(p => p.Id.NodeId), Is.EqualTo(new[] { "P1" }));
        });
        Assert.That(File.ReadAllLines(Path.Combine(root, "listing-requests.txt")).Length, Is.EqualTo(requests));
        await Ui.Run(() => Ui.Find<TextBox>("PlanProjectSearch").Text = "");
        await ProjectChooserTestActions.SelectOwner("acme");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P2"); await Ui.Idle();
        await PickerCommand("PlanChooseProject");
        await Ui.Run(() => {
            var recent = Ui.Find<ListView>("PlanRecentProjects");
            Assert.That(recent.Items.Cast<ProjectChoice>().Select(p => p.Id.NodeId), Is.EqualTo(new[] { "P2", "P1" }));
            recent.SelectedIndex = 1;
        });
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P1");
        await Ui.Idle(); await PickerCommand("PlanChooseProject");
        await Ui.Run(() => Assert.That(Ui.Find<ListView>("PlanRecentProjects").Items.Cast<ProjectChoice>().Select(p => p.Id.NodeId), Is.EqualTo(new[] { "P1", "P2" })));
    }

    [Test, Category("PlanSheetNative")]
    public async Task ProjectChooserArrowFocusDoesNotOpenProjectOrReadAnotherOwner()
    {
        File.WriteAllText(Path.Combine(root, "scenario.json"), """{"planEditor":true,"workspace":true,"chooserCatalog":true}""");
        await Ui.Run(() => Ui.Click("PlanConnect"));
        await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Count == 60);
        await SheetNativeInput.ActivateWindow();
        await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").ContainerFromIndex(0) is ListViewItem { IsLoaded: true, IsEnabled: true });
        await Ui.Run(() => ((ListViewItem)Ui.Find<ListView>("AvailableProjects").ContainerFromIndex(0)).Focus(FocusState.Keyboard));
        await SheetNativeInput.Press(Windows.System.VirtualKey.Down);
        await Ui.Until(() => (DependencyObject?)Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(view.XamlRoot) == Ui.Find<ListView>("AvailableProjects").ContainerFromIndex(1));
        await Ui.Run(() => Assert.That(workspace.Session, Is.Null));
        var before = File.ReadAllLines(Path.Combine(root, "listing-requests.txt")).Length;
        await Ui.Run(() => ((ListViewItem)Ui.Find<ListView>("PlanProjectOwners").ContainerFromIndex(0)).Focus(FocusState.Keyboard));
        await SheetNativeInput.Press(Windows.System.VirtualKey.Down);
        await Ui.Run(() => Assert.That(((OwnerChoice)Ui.Find<ListView>("PlanProjectOwners").SelectedItem).Login, Is.EqualTo("fixture-user")));
        Assert.That(File.ReadAllLines(Path.Combine(root, "listing-requests.txt")).Length, Is.EqualTo(before));
    }
}
