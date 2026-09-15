using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test]
    public async Task DraftSavePreservesCollapsedNavigationActiveProjectAndKeyboardFocus()
    {
        var selectedProject = Workspace.Selected!.Snapshot.Id;
        var selectedTitle = Workspace.Selected.Snapshot.Title;
        object? focused = null;
        string? collapsedSelection = null;
        await Ui.Until(() => Ui.Find<TreeView>("ProjectNavigation").ContainerFromNode(Ui.Find<TreeView>("ProjectNavigation").RootNodes[0]) is TreeViewItem { IsLoaded: true });
        await Ui.Run(() =>
        {
            var navigation = Ui.Find<TreeView>("ProjectNavigation");
            navigation.RootNodes[0].IsExpanded = false;
            Assert.That(((TreeViewItem)navigation.ContainerFromNode(navigation.RootNodes[0])).Focus(FocusState.Programmatic), Is.True);
            focused = FocusManager.GetFocusedElement(panel.XamlRoot);
            Assert.That(focused, Is.Not.Null);
        });
        await Ui.Idle();
        await Ui.Run(() =>
        {
            collapsedSelection = Ui.Find<TreeView>("ProjectNavigation").SelectedNode?.Content.ToString();
            Ui.Find<TextBox>("GridCell0_0").Text = "pending local text";
        });
        TestContext.Out.WriteLine($"Native TreeView selection after collapsing the ancestor, before editing: {collapsedSelection ?? "(none)"}");

        await Ui.Run(async () => Assert.That(await Workspace.FlushDraftsAsync(), Is.True));
        await Ui.Idle();

        await Ui.Run(() =>
        {
            var navigation = Ui.Find<TreeView>("ProjectNavigation");
            Assert.That(navigation.RootNodes[0].IsExpanded, Is.False);
            // Native TreeView clears a selected child when its ancestor collapses.
            // The active Project and the visible focus must survive draft persistence.
            Assert.That(Workspace.Selected?.Snapshot.Id, Is.EqualTo(selectedProject));
            Assert.That(Ui.Find<TextBlock>("ProjectSummary").Text, Does.StartWith(selectedTitle));
            Assert.That(FocusManager.GetFocusedElement(panel.XamlRoot), Is.SameAs(focused));
            Assert.That(h.Writes, Is.Empty);
        });
    }

    [Test]
    public async Task ProjectSettingsFlyoutSavesDefaultDestinationWithoutChangingExistingRows()
    {
        var before = Work.Open(Workspace.Selected!).Select(row => row.ItemId).ToArray();
        await Ui.Run(() => Ui.Click("ProjectSettingsButton"));
        await Ui.Until(() => SettingsContent() is not null);
        await Ui.Until(() => Ui.Find<Button>("SaveProjectSettingButton", SettingsContent()).IsLoaded);
        await Ui.Run(() =>
        {
            var content = SettingsContent()!;
            Assert.That(Ui.Find<TextBlock>("ProjectInformation", content).Text, Does.Contain("Project ID: P1").And.Contain("アカウント ID 42"));
            Ui.Find<TextBox>("DefaultRepository", content).Text = "owner/destination";
            Ui.Click(Ui.Find<Button>("SaveProjectSettingButton", content));
        });
        await Ui.Until(() => Workspace.Selected?.DefaultRepository == "owner/destination");
        await Ui.Idle();
        await Ui.Run(() =>
        {
            Assert.That(Ui.Find<TextBox>("DefaultRepository", SettingsContent()!).Text, Is.EqualTo("owner/destination"));
            Assert.That(Ui.Find<TextBlock>("ProjectCacheDestination").Text, Does.Contain("キャッシュ").And.Contain("owner/destination"));
            Assert.That(Work.Open(Workspace.Selected!).Select(row => row.ItemId), Is.EqualTo(before));
            Assert.That(h.Writes, Is.Empty);
        });
    }

    [Test]
    public async Task StatusDetailsRemainReadableWhenNoProjectIsSelected()
    {
        await Ui.Run(async () => await Workspace.BindAsync(null));
        await Ui.Run(() =>
        {
            Assert.That(Workspace.Selected, Is.Null);
            var button = Ui.Find<Button>("WorkspaceStatusDetailsButton");
            Assert.That(button.Focus(FocusState.Keyboard), Is.True);
            Ui.Click(button);
        });
        await Ui.Until(() => StatusContent() is not null);
        await Ui.Run(() =>
        {
            var message = Ui.Find<TextBox>("WorkspaceStatusDetails", StatusContent()!);
            Assert.That(message.Text, Is.EqualTo(Workspace.Status).And.Not.Empty);
            Assert.That(message.IsReadOnly, Is.True);
            Assert.That(message.Focus(FocusState.Keyboard), Is.True);
            message.SelectAll();
            Assert.That(message.SelectedText, Is.EqualTo(Workspace.Status));
        });
    }

    private DependencyObject? SettingsContent() => VisualTreeHelper.GetOpenPopupsForXamlRoot(panel.XamlRoot)
        .Select(popup => popup.Child).FirstOrDefault(child => Ui.Tree(child).OfType<TextBox>().Any(text => text.Name == "DefaultRepository" && text.IsLoaded));
    private DependencyObject? StatusContent() => VisualTreeHelper.GetOpenPopupsForXamlRoot(panel.XamlRoot)
        .Select(popup => popup.Child).FirstOrDefault(child => Ui.Tree(child).OfType<TextBox>().Any(text => text.Name == "StatusDetailsText" && text.IsLoaded));
}
