using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test]
    public async Task InvokingAnotherProjectDuringFillCancelsBeforeChangingEitherProject()
    {
        var first = Workspace.Selected!;
        await Ui.Run(async () => {
            var choice = await new GhProjectsBoards.Core.Projects.ProjectDiscovery(h.Existing.Service).ResolveAsync(h.Existing.Context,
                "https://github.com/users/sample-user/projects/2", default);
            await Workspace.RegisterAsync(choice, null); await Workspace.SelectAsync(first.Snapshot.Id);
        });
        await OpenNavigation(SplitViewDisplayMode.Inline); await Ui.Ready<Button>("GridCell0_1");
        await Ui.ChooseCell("GridCell0_1", "done");
        await SheetNativeInput.Drag("GridFillHandle0_1", "GridCell1_1", async () => {
            await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("2行へコピー予定"));
            await InvokeProjectNode("Project 2"); await Ui.Until(() => Workspace.Selected?.Snapshot.Id.NodeId == "P2");
        });
        await Ui.Run(() => {
            Assert.That(Work.DifferenceCount, Is.EqualTo(1));
            Assert.That(Work.Fields.Single(f => f.Change is not null).Key, Is.EqualTo(new GhProjectsBoards.Core.Projects.FieldKey("Select", "P1-T1", "P1", "P1-status")));
            Assert.That(Work.Snapshot().History, Has.Length.EqualTo(1)); Assert.That(h.Writes, Is.Empty);
        });
    }

    [TestCase(SplitViewDisplayMode.Overlay)]
    [TestCase(SplitViewDisplayMode.Inline)]
    public async Task InvokingCachedProjectRevealsWorkspaceAndClosesOnlyOverlayNavigation(SplitViewDisplayMode mode)
    {
        var selected = Workspace.Selected!;
        await Ui.Run(async () => await Workspace.SelectProfileAsync(selected.Snapshot.Id.Scope));
        await OpenNavigation(mode);
        await InvokeProjectNode(selected.Snapshot.Title);
        await Ui.Until(() => Workspace.Selected?.Snapshot.Id == selected.Snapshot.Id);
        await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Idle();
        await Ui.Run(() =>
        {
            Assert.That(Ui.Find<TextBlock>("ProjectSummary").Text, Does.StartWith(selected.Snapshot.Title));
            Assert.That(Ui.Find<TextBox>("GridCell0_0").IsLoaded, Is.True);
            Assert.That(Ui.Tree(panel).OfType<SplitView>().Single().IsPaneOpen, Is.EqualTo(mode == SplitViewDisplayMode.Inline));
            if (mode == SplitViewDisplayMode.Overlay)
            {
                var toggle = Ui.Find<Button>("ToggleProjectNavigation");
                Assert.That(FocusManager.GetFocusedElement(panel.XamlRoot), Is.SameAs(toggle));
                Assert.That(AutomationProperties.GetName(toggle), Is.EqualTo("Project一覧を表示"));
            }
            Assert.That(h.Writes, Is.Empty);
        });
    }

    [Test]
    public async Task FailedSaveWhileInvokingActiveProjectKeepsOverlayAndPendingWork()
    {
        var selected = Workspace.Selected!;
        await Ui.Run(async () => Assert.That(await Workspace.FlushDraftsAsync(), Is.True));
        await OpenNavigation(SplitViewDisplayMode.Overlay);
        using (var competing = new FileStream(Path.Combine(h.Existing.Root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Text = "retain this pending title");
            await InvokeProjectNode(selected.Snapshot.Title);
            await Ui.Until(() => Workspace.Status.StartsWith("ローカル保存失敗"));
            await Ui.Idle();
            await Ui.Run(() =>
            {
                Assert.That(Workspace.Selected?.Snapshot.Id, Is.EqualTo(selected.Snapshot.Id));
                Assert.That(Ui.Tree(panel).OfType<SplitView>().Single().IsPaneOpen, Is.True);
                Assert.That(Ui.Find<TextBox>("GridCell0_0").Text, Is.EqualTo("retain this pending title"));
                Assert.That(Work.Buffer(Work.Open(selected)[0].Cells[0]), Is.EqualTo("retain this pending title"));
                Assert.That(h.Writes, Is.Empty);
            });
        }
    }

    private async Task OpenNavigation(SplitViewDisplayMode mode)
    {
        await Ui.Run(() => panel.Width = mode == SplitViewDisplayMode.Overlay ? 800 : 1100);
        await Ui.Until(() => Ui.Tree(panel).OfType<SplitView>().Single().DisplayMode == mode);
        await Ui.Run(() =>
        {
            if (!Ui.Tree(panel).OfType<SplitView>().Single().IsPaneOpen) Ui.Click("ToggleProjectNavigation");
        });
    }
    private async Task InvokeProjectNode(string title)
    {
        TreeViewItem? item = null;
        await Ui.Until(() =>
        {
            var tree = Ui.Find<TreeView>("ProjectNavigation");
            var node = tree.RootNodes.SelectMany(owner => owner.Children).SelectMany(repository => repository.Children)
                .First(candidate => candidate.Content.ToString() == title);
            item = tree.ContainerFromNode(node) as TreeViewItem;
            return item?.IsLoaded == true;
        });
        await Ui.Run(() =>
        {
            Assert.That(item!.Focus(FocusState.Keyboard), Is.True);
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
            var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
            Assert.That(invoke, Is.Not.Null, "The loaded native Project tree item must expose its public invoke route.");
            invoke!.Invoke();
        });
    }

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
            Assert.That(Ui.Find<TextBlock>("ProjectCacheDestination").Text, Does.Contain("キャッシュ"));
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
