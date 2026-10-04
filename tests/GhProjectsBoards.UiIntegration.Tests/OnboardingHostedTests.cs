using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
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
        await Ui.Ready<TextBlock>("GuideProjectStatus");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GuideProjectStatus").Text, Does.Contain("Project 1"));
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
        await Ui.Ready<Button>("GuideRegisterButton");
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("GuideRegisterButton").IsEnabled, Is.False);
            Assert.That(Ui.Find<Button>("GuideOpenBoardButton").IsEnabled, Is.False);
            Ui.Click("GuideConnectionButton");
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
        await Ui.Ready<TextBlock>("GuideConnectionStatus");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GuideConnectionStatus").Text, Does.Contain("未確認"));
            Assert.That(Ui.Find<Button>("GuideRegisterButton").IsEnabled, Is.False);
            Ui.Click("GuideConnectionButton");
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
        await Ui.Ready<TextBlock>("GuideConnectionStatus");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GuideConnectionStatus").Text, Does.Contain("sample-user").And.Contain("github.com"));
            Ui.Click("GuideRegisterButton");
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
            Assert.That(Ui.Find<Button>("CloseProjectDiscoveryButton").Content, Is.EqualTo("ガイドへ戻る"));
            Ui.Click("CloseProjectDiscoveryButton");
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("GettingStartedGuide").Visibility == Visibility.Visible);
        await Ui.Ready<Button>("GuideRegisterButton");
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("GuideOpenBoardButton").IsEnabled, Is.False);
            Ui.Click("GuideRegisterButton");
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
        await Ui.Ready<TextBlock>("GuideProjectStatus");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GuideProjectStatus").Text, Does.Contain("Project 1").And.Contain("登録済み"));
            Ui.Click("GuideOpenBoardButton");
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("GettingStartedGuide").Visibility == Visibility.Collapsed
            && Ui.Tree(projects).OfType<EditingGrid>().Any(grid => grid.IsLoaded));
        var saved = await new RegistrationStore(root).LoadAsync();
        Assert.That(saved.Registrations.Single().Snapshot.Id.NodeId, Is.EqualTo("P1"));
        Assert.That(workspace.Drafts!.Workspace.Journal, Is.Empty);
        boundary.AssertQueriesOnly();
    }
}
