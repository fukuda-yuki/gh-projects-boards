using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test]
    public async Task LinkedRepositoriesDoNotDuplicateTheProjectOrChangeItsWholeWorkspace()
    {
        var selected = Workspace.Selected!;
        var rows = Work.Open(selected).Select(row => row.ItemId).ToArray();
        await FocusFirstTitle();
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Text = "keep this pending title");
        await OpenNavigation(SplitViewDisplayMode.Inline);
        await Ui.Run(() => {
            var tree = Ui.Find<TreeView>("ProjectNavigation");
            Assert.That(tree.RootNodes.Single().Children.Select(node => node.Content.ToString()),
                Is.EqualTo(new[] { "Project 1" }), "A linked repository is a discovery association, not a second workspace.");
        });
        await Ui.Run(async () => Assert.That(await Workspace.FlushDraftsAsync(), Is.True));
        h.Existing.ChangeResponse = (query, response) => {
            if (!query.Contains("RegistrationLinks")) return;
            var repositories = response["data"]!["node"]!["repositories"]!;
            repositories["nodes"] = new System.Text.Json.Nodes.JsonArray();
            repositories["totalCount"] = 0;
        };

        await Ui.Run(() => {
            Ui.Find<TreeView>("ProjectNavigation").RootNodes.Single().IsExpanded = false;
            Assert.That(Ui.Find<Button>("ProjectIdentityButton").Focus(FocusState.Keyboard), Is.True);
            Ui.Click("RefreshProjectButton");
        });
        await Ui.Until(() => !Workspace.IsBusy && Workspace.Selected!.Repositories.Count == 0);
        await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TreeView>("ProjectNavigation").RootNodes.Single().Children.Select(node => node.Content.ToString()), Is.EqualTo(new[] { "Project 1" }));
            Assert.That(Workspace.Selected!.Snapshot.Id, Is.EqualTo(selected.Snapshot.Id));
            Assert.That(Work.Open(Workspace.Selected!).Select(row => row.ItemId), Is.EqualTo(rows));
            Assert.That(Ui.Find<TextBox>("GridCell0_0").Text, Is.EqualTo("keep this pending title"));
            Assert.That(Ui.Find<TreeView>("ProjectNavigation").RootNodes.Single().IsExpanded, Is.False);
            Assert.That(FocusManager.GetFocusedElement(panel.XamlRoot), Is.SameAs(Ui.Find<Button>("ProjectIdentityButton")));
            Assert.That(h.Writes, Is.Empty);
        });
    }

    [Test]
    public async Task RepositorySearchFiltersOnlyProjectChoicesAndClearRestoresUnlinkedWork()
    {
        await Ui.Run(async () => {
            var choice = await new ProjectDiscovery(h.Existing.Service).ResolveAsync(h.Existing.Context,
                "https://github.com/users/sample-user/projects/2", default);
            await Workspace.RegisterAsync(choice, "sample-user/second");
        });
        await OpenNavigation(SplitViewDisplayMode.Inline);
        await FocusFirstTitle();
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Text = "pending in the open project");
        await Ui.Run(async () => Assert.That(await Workspace.FlushDraftsAsync(), Is.True));
        var original = Workspace.Selected!;
        var items = Work.Open(original).Select(row => row.ItemId).ToArray();

        await Ui.Run(() => {
            var filter = Ui.Find<ComboBox>("NavigationRepositoryFilter");
            filter.SelectedItem = filter.Items.Single(item => item.ToString() == "sample-user/first");
        });
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TreeView>("ProjectNavigation").RootNodes.Single().Children.Select(node => node.Content.ToString()), Is.EqualTo(new[] { "Project 1" }));
            Assert.That(Workspace.Selected!.Snapshot.Id, Is.EqualTo(original.Snapshot.Id));
            Assert.That(Workspace.Selected.DefaultRepository, Is.EqualTo("sample-user/second"));
            Assert.That(Ui.Find<TextBlock>("NavigationFilterNotice").Text, Does.Contain("表示中のProjectは一覧の条件外"));
            Assert.That(Work.Open(Workspace.Selected).Select(row => row.ItemId), Is.EqualTo(items));
            Assert.That(Ui.Find<TextBox>("GridCell0_0").Text, Is.EqualTo("pending in the open project"));
            Assert.That(Ui.Find<Button>("ClearNavigationRepositoryFilter").Focus(FocusState.Keyboard), Is.True);
            Ui.Click("ClearNavigationRepositoryFilter");
        });
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TreeView>("ProjectNavigation").RootNodes.Single().Children.Select(node => node.Content.ToString()), Is.EqualTo(new[] { "Project 1", "Project 2" }));
            Assert.That(Ui.Find<TextBlock>("NavigationFilterNotice").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(FocusManager.GetFocusedElement(panel.XamlRoot), Is.SameAs(Ui.Find<ComboBox>("NavigationRepositoryFilter")));
            Assert.That(Workspace.Selected!.Snapshot.Id, Is.EqualTo(original.Snapshot.Id));
            Assert.That(h.Writes, Is.Empty);
        });
    }

    [Test]
    public async Task LeavingTheSelectedProjectClosesAndClearsItsIdentityFlyout()
    {
        await Ui.Run(() => Ui.Click("ProjectIdentityButton"));
        await Ui.Until(() => IdentityContent() is not null);
        await Ui.Run(async () => await Workspace.BindAsync(null));
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(IdentityContent(), Is.Null);
            Assert.That(Ui.Find<Button>("ProjectIdentityButton").Content.ToString(), Is.Empty);
            Assert.That(Ui.Find<Grid>("ProjectIdentityContext").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Workspace.Selected, Is.Null);
            Assert.That(h.Writes, Is.Empty);
        });
    }

    [Test]
    public async Task SameNamedProjectsRemainDistinctAndSwitchingRetainsScopedWorkAndVisibleIdentity()
    {
        h.Existing.ChangeResponse = (query, response) => {
            if (query.Contains("ProjectFields")) response["data"]!["node"]!["title"] = "Delivery";
            if (query.Contains("RegistrationResolve")) response["data"]!["user"]!["projectV2"]!["title"] = "Delivery";
        };
        await Ui.Run(async () => {
            var first = Workspace.Selected!; var snapshot = first.Snapshot;
            await Workspace.RegisterAsync(new(snapshot.Id, snapshot.OwnerId, first.OwnerLogin, snapshot.OwnerType,
                snapshot.Number, snapshot.Url, snapshot.Title), first.DefaultRepository, true);
            var choice = await new ProjectDiscovery(h.Existing.Service).ResolveAsync(h.Existing.Context,
                "https://github.com/users/sample-user/projects/2", default);
            await Workspace.RegisterAsync(choice, null);
            await Workspace.SelectAsync(new(Work.Scope, "P1"));
        });
        await OpenNavigation(SplitViewDisplayMode.Inline);
        await Ui.Ready<Button>("GridCell0_1");
        await Ui.ChooseCell("GridCell0_1", "done");
        await Ui.Run(() => {
            var labels = Ui.Find<TreeView>("ProjectNavigation").RootNodes.Single().Children.Select(node => node.Content.ToString()).ToArray();
            Assert.That(labels, Is.EquivalentTo(new[] { "Delivery · #1", "Delivery · #2" }));
        });

        await InvokeProjectNode("Delivery · #2");
        await Ui.Until(() => Workspace.Selected?.Snapshot.Id.NodeId == "P2");
        await Ui.Ready<Button>("GridCell0_1");
        await Ui.Run(() => {
            Assert.That(Work.Value(Work.Open(Workspace.Selected!)[0].Cells[1]), Is.EqualTo("todo"));
            Assert.That(Ui.Find<Button>("ProjectIdentityButton").Content.ToString(), Is.EqualTo("sample-user / Project #2"));
            Ui.Click("ProjectIdentityButton");
        });
        await Ui.Until(() => IdentityContent() is not null);
        await Ui.Run(() => {
            var url = Ui.Find<TextBox>("ProjectIdentityUrl", IdentityContent()!);
            Assert.That(url.Text, Is.EqualTo("https://github.com/users/sample-user/projects/2"));
            Assert.That(url.IsReadOnly, Is.True);
            Assert.That(url.Focus(FocusState.Keyboard), Is.True);
            url.SelectAll(); Assert.That(url.SelectedText, Is.EqualTo(url.Text));
            Ui.Find<Button>("ProjectIdentityButton").Flyout.Hide();
        });
        await InvokeProjectNode("Delivery · #1");
        await Ui.Until(() => Workspace.Selected?.Snapshot.Id.NodeId == "P1");
        await Ui.Ready<Button>("GridCell0_1");
        await Ui.Run(() => {
            Assert.That(Work.Value(Work.Open(Workspace.Selected!)[0].Cells[1]), Is.EqualTo("done"));
            Assert.That(Work.Fields.Single(field => field.Change is not null).Key, Is.EqualTo(new FieldKey("Select", "P1-T1", "P1", "P1-status")));
            Assert.That(Ui.Find<Button>("ProjectIdentityButton").Content.ToString(), Is.EqualTo("sample-user / Project #1"));
            Assert.That(h.Writes, Is.Empty);
        });
    }

    private DependencyObject? IdentityContent() => Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(panel.XamlRoot)
        .Select(popup => popup.Child).FirstOrDefault(child => Ui.Tree(child).OfType<TextBox>().Any(text => text.Name == "ProjectIdentityUrl" && text.IsLoaded));
}
