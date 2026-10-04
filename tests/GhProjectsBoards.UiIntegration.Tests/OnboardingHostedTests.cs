using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
using System.Text.Json;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test, Category("Onboarding")]
    public async Task GettingStartedCanBeReopenedAndDismissedWithoutChangingCachedWork()
    {
        var selected = Workspace.Selected!.Snapshot.Id;
        await Ui.Run(() => {
            panel.OfferGettingStarted();
            Assert.That(Ui.Find<ScrollViewer>("GettingStartedGuide").Visibility, Is.EqualTo(Visibility.Collapsed));
        });
        await Ui.Ready<FrameworkElement>("GridCell0_0");
        await Ui.Run(() => FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>("GridCell0_0")).SetFocus());
        await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Text = "   ");
        await Ui.Run(() => Ui.Click("StartGuideButton"));
        await Ui.Until(() => Ui.Find<ScrollViewer>("GettingStartedGuide").Visibility == Visibility.Visible);
        await Ui.Ready<TextBlock>("GuideContext");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GuideContext").Text, Does.Contain("Project 1"));
            Ui.Click("GuideLaterButton");
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("GettingStartedGuide").Visibility == Visibility.Collapsed);
        await Ui.Ready<FrameworkElement>("GridCell0_0");
        await Ui.Run(() => {
            Assert.That(Workspace.Selected!.Snapshot.Id, Is.EqualTo(selected));
            Assert.That(Work.Fields.Single(f => f.Key == new GhProjectsBoards.Core.Projects.FieldKey("Title", "I1")).Buffer,
                Is.EqualTo("   "));
            Assert.That(Ui.Tree(panel).OfType<EditingGrid>().Single().Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(h.Writes, Is.Empty);
            Ui.Click("StartGuideButton");
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("GettingStartedGuide").Visibility == Visibility.Visible);
        await Ui.Ready<Button>("GuideLaterButton");
        await Ui.Run(() => {
            Ui.Click("GuideLaterButton");
            Assert.That(Work.Fields.Single(f => f.Key == new GhProjectsBoards.Core.Projects.FieldKey("Title", "I1")).Buffer,
                Is.EqualTo("   "));
        });
        var savedProfile = Workspace.Profile;
        await Ui.Run(async () => await Workspace.SelectProfileAsync(null));
        await Ui.Ready<Button>("EmptyWorkspaceAction");
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("EmptyWorkspaceAction").Content, Is.EqualTo("保存済みProjectを開く"));
            Ui.Click("EmptyWorkspaceAction");
            Assert.That(Ui.Find<FormComboBox>("SavedProfiles").IsDropDownOpen, Is.True,
                "Opening saved work must show the account choices, rather than only moving focus.");
            Assert.That(Workspace.Profile, Is.Null, "The user must choose the saved identity.");
        });
        await Ui.Until(() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Ui.Root.XamlRoot)
            .SelectMany(popup => Ui.Tree(popup.Child)).OfType<ComboBoxItem>()
            .Any(item => item.IsLoaded && item.ActualHeight > 0));
        await Ui.Run(() => {
            var account = (ComboBoxItem)Ui.Find<FormComboBox>("SavedProfiles").ContainerFromIndex(0);
            Assert.That(account.IsLoaded && account.IsEnabled && account.ActualHeight > 0, Is.True);
            Ui.Find<FormComboBox>("SavedProfiles").SelectedIndex = 0;
        });
        await Ui.Until(() => Workspace.Profile == savedProfile);
        await Ui.Run(() => {
            Ui.Find<FormComboBox>("SavedProfiles").IsDropDownOpen = false;
            Assert.That(Workspace.Selected, Is.Null, "Account selection must not choose a Project on the user's behalf.");
            Assert.That(Work.Fields.Single(f => f.Key == new GhProjectsBoards.Core.Projects.FieldKey("Title", "I1")).Buffer,
                Is.EqualTo("   "));
            Assert.That(h.Writes, Is.Empty);
        });
    }
}

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class FirstProjectGuideHostedTests
{
    private RegistrationWorkspace workspace = null!;
    private RegistrationPanel projects = null!;
    private ConnectionPanel connection = null!;
    private ProjectReaderTests.ProjectBoundary boundary = null!;
    private Grid content = null!;
    private string root = null!;

    [SetUp]
    public async Task SetUp()
    {
        Ui.Check();
        root = Path.Combine(Path.GetTempPath(), "ghpb-onboarding-" + Guid.NewGuid().ToString("N"));
        workspace = new(new RegistrationStore(root));
        await workspace.RestoreAsync();
        boundary = new();
        boundary.Override = (query, variables) => RegistrationResponses.Query(query, variables, itemCount: 2) is { } result
            ? ScriptedRunner.Http(JsonSerializer.Serialize(result)) : null;
        var service = new GhConnectionService("fixture-gh.exe", "github.com", boundary.Runner);
        await Ui.Run(() => {
            projects = new(); projects.Initialize(workspace);
            connection = new() { Visibility = Visibility.Collapsed };
            connection.Initialize(workspace, new ConnectionViewModel((_, _) => service) { ExecutablePath = "" });
            projects.ConnectionRequested += (_, _) => { projects.Visibility = Visibility.Collapsed; connection.Visibility = Visibility.Visible; };
            connection.ReturnRequested += (_, _) => { connection.Visibility = Visibility.Collapsed; projects.Visibility = Visibility.Visible; projects.Update(); projects.ReturnFromConnection(); };
            content = new Grid(); content.Children.Add(projects); content.Children.Add(connection);
        });
        await Ui.Mount(content);
        TestContext.Out.WriteLine($"Isolated store: {root}; production: {typeof(RegistrationPanel).Assembly.Location}");
    }

    [TearDown]
    public async Task TearDown()
    {
        await Ui.Run(async () => { await connection.StopAsync(); await workspace.StopAsync(); Assert.That(await workspace.FlushDraftsAsync(), Is.True); }, check: false);
        await Ui.Unmount(content, check: false);
        await Ui.Idle();
        boundary.AssertQueriesOnly();
    }

    [Test, Category("Onboarding"), Category("HostedWorkflow")]
    public async Task MissingCliAndInvalidProjectCanBeCorrectedBeforeOpeningTheFirstBoard()
    {
        await Ui.Run(() => projects.OfferGettingStarted());
        await Ui.Until(() => Ui.Find<ScrollViewer>("GettingStartedGuide").Visibility == Visibility.Visible);
        await Ui.Ready<Button>("GuideLaterButton");
        await Ui.Run(() => {
            Ui.Click("GuideLaterButton");
            Assert.That(Ui.Find<ScrollViewer>("GettingStartedGuide").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(workspace.Registrations, Is.Empty);
            Ui.Click("StartGuideButton");
        });
        await Ui.Ready<Button>("GuideNextButton");
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("GuideNextButton").Content, Is.EqualTo("GitHubに接続"));
            Ui.Click("GuideNextButton");
        });
        await Ui.Until(() => connection.Visibility == Visibility.Visible);
        await Ui.Ready<Button>("CheckConnectionButton");
        await Ui.Run(() => Ui.Click("CheckConnectionButton"));
        await Ui.Until(() => Ui.Find<Button>("CheckConnectionButton").IsEnabled
            && Ui.Find<TextBlock>("ConnectionStatus").Text.Contains("gh.exe"));
        await Ui.Run(() => {
            Assert.That(workspace.CanRead, Is.False);
            Ui.Click("ProjectsPageButton");
        });
        await Ui.Until(() => projects.Visibility == Visibility.Visible);
        await Ui.Ready<TextBlock>("WorkspaceIdentity");
        await Ui.Run(() => {
            Assert.That(workspace.CanRead, Is.False);
            Assert.That(Ui.Find<Button>("GuideNextButton").Content, Is.EqualTo("GitHubに接続"));
            Ui.Click("GuideNextButton");
        });
        await Ui.Until(() => connection.Visibility == Visibility.Visible);
        await Ui.Ready<Expander>("ConnectionDetails");
        await Ui.Run(() => Ui.Find<Expander>("ConnectionDetails").IsExpanded = true);
        await Ui.Ready<TextBox>("ExecutablePath");
        await Ui.Run(() => {
            Ui.Find<TextBox>("ExecutablePath").Text = "fixture-gh.exe";
            Ui.Click("CheckConnectionButton");
        });
        await Ui.Until(() => workspace.CanRead && Ui.Find<Button>("CheckConnectionButton").IsEnabled);
        await Ui.Run(() => Ui.Click("ProjectsPageButton"));
        await Ui.Until(() => projects.Visibility == Visibility.Visible);
        await Ui.Ready<TextBlock>("WorkspaceIdentity");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("WorkspaceIdentity").Text, Does.Contain("sample-user").And.Contain("github.com"));
            Assert.That(Ui.Find<Button>("GuideNextButton").Content, Is.EqualTo("Projectを追加"));
            Ui.Click("GuideNextButton");
        });
        await Ui.Ready<TextBox>("RegistrationUrl");
        await Ui.Until(() => Ui.Find<TextBox>("RegistrationUrl").ActualWidth > 0);
        await Ui.Run(() => {
            Ui.Find<TextBox>("RegistrationUrl").Text = "not-a-project-url";
            Ui.Click("ResolveProjectButton");
        });
        await Ui.Until(() => !workspace.IsBusy && workspace.Status.Length > 0);
        await Ui.Run(() => {
            Assert.That(workspace.Registrations, Is.Empty);
            Assert.That(Ui.Find<TextBox>("RegistrationUrl").Text, Is.EqualTo("not-a-project-url"));
            Assert.That(Ui.Find<Button>("RegisterProjectButton").IsEnabled, Is.False);
            Ui.Click("StartGuideButton");
        });
        await Ui.Ready<Button>("GuideLaterButton");
        await Ui.Run(() => { Ui.Click("StartGuideButton"); Ui.Click("GuideLaterButton"); });
        await Ui.Ready<TextBox>("RegistrationUrl");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("RegistrationUrl").Text, Is.EqualTo("not-a-project-url"));
            Assert.That(Ui.Find<Button>("CloseProjectDiscoveryButton").Content, Is.EqualTo("戻る"));
            Ui.Click("CloseProjectDiscoveryButton");
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("GettingStartedGuide").Visibility == Visibility.Visible);
        await Ui.Ready<Button>("GuideNextButton");
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("GuideNextButton").Content, Is.EqualTo("Projectを追加"));
            Ui.Click("GuideNextButton");
        });
        await Ui.Ready<TextBox>("RegistrationUrl");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("RegistrationUrl").Text, Is.EqualTo("not-a-project-url"));
            Ui.Find<TextBox>("RegistrationUrl").Text = "https://github.com/users/sample-user/projects/1";
            Ui.Click("ResolveProjectButton");
        });
        await Ui.Until(() => Ui.Find<Button>("RegisterProjectButton").IsEnabled);
        await Ui.Run(() => Ui.Click("RegisterProjectButton"));
        await Ui.Until(() => !workspace.IsBusy && workspace.Selected?.Snapshot.Id.NodeId == "P1"
            && Ui.Find<ScrollViewer>("GettingStartedGuide").Visibility == Visibility.Visible);
        await Ui.Ready<TextBlock>("GuideContext");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GuideContext").Text, Does.Contain("Project 1"));
            Assert.That(Ui.Find<Button>("GuideNextButton").Content, Is.EqualTo("Boardsで開く"));
            Ui.Click("GuideNextButton");
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("GettingStartedGuide").Visibility == Visibility.Collapsed
            && Ui.Tree(projects).OfType<EditingGrid>().Any(grid => grid.IsLoaded));
        var saved = await new RegistrationStore(root).LoadAsync();
        Assert.That(saved.Registrations.Single().Snapshot.Id.NodeId, Is.EqualTo("P1"));
        Assert.That(workspace.Drafts!.Workspace.Journal, Is.Empty);
        boundary.AssertQueriesOnly();
    }

    [Test, Category("WorkspaceRefinement")]
    public async Task EmptyWorkspaceOffersConnectionWithoutProjectCommandsOrIdleDiagnostics()
    {
        await Ui.Run(() => {
            var next = Ui.Find<Button>("EmptyWorkspaceAction");
            Assert.That(next.IsLoaded && next.IsEnabled && next.ActualWidth > 0, Is.True);
            Assert.That(next.Content, Is.EqualTo("GitHubに接続"));
            Assert.That(Ui.Find<FrameworkElement>("ProjectCommandBar").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Ui.Find<FrameworkElement>("WorkspaceStatusBar").Visibility, Is.EqualTo(Visibility.Collapsed));
            var manual = Ui.Find<HyperlinkButton>("UserManualButton");
            Assert.That(manual.NavigateUri, Is.EqualTo(new Uri("https://github.com/fukuda-yuki/gh-projects-boards/blob/main/docs/user-manual.md")));
            Ui.Click(next);
        });
        await Ui.Until(() => connection.Visibility == Visibility.Visible);
        Assert.That(workspace.Registrations, Is.Empty);
        boundary.AssertQueriesOnly();
    }

    [Test, Category("WorkspaceRefinement")]
    public async Task FirstStartOffersOnlyTheCurrentActionAndKeepsAuthenticationRecovery()
    {
        await Ui.Run(() => projects.OfferGettingStarted());
        await Ui.Until(() => Ui.Find<ScrollViewer>("GettingStartedGuide").ActualWidth > 0);
        await Ui.Run(() => {
            var start = Ui.Find<ScrollViewer>("GettingStartedGuide");
            var actions = Ui.Tree(start).OfType<Button>().Where(button => button.IsEnabled
                && button.Visibility == Visibility.Visible && button.ActualWidth > 0).ToArray();
            Assert.That(actions.Select(button => button.Content), Is.EquivalentTo(new[] { "GitHubに接続", "後で" }));
            Assert.That(Ui.Tree(start).OfType<TextBlock>().Select(text => text.Text),
                Has.None.Contains("PowerShell"));
            Ui.Click("GuideNextButton");
        });
        await Ui.Until(() => connection.Visibility == Visibility.Visible);
        await Ui.Run(() => TestContext.Out.WriteLine($"Connection entry: loaded={connection.IsLoaded}, visibility={connection.Visibility}; realized controls="
            + string.Join(",", Ui.Tree(connection).OfType<FrameworkElement>()
                .Select(element => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(element)).Where(id => id.Length > 0))));
        await Ui.Ready<Button>("CheckConnectionButton");
        await Ui.Run(() => Ui.Click("CheckConnectionButton"));
        await Ui.Until(() => Ui.Find<Button>("CheckConnectionButton").IsEnabled
            && Ui.Find<TextBlock>("ConnectionStatus").Text.Contains("gh.exe"));
        await Ui.Run(() => Ui.Click("ProjectsPageButton"));
        await Ui.Until(() => projects.Visibility == Visibility.Visible);
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("GuideNextButton").Content, Is.EqualTo("GitHubに接続"));
            Assert.That(workspace.CanRead, Is.False);
            Assert.That(workspace.Registrations, Is.Empty);
        });
    }
}
