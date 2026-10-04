using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [TestCase(null, "github.com"), TestCase("example.test", "example.test"), Category("ConnectionEntry")]
    public async Task ConnectionEntryUsesTheCachedHostWithoutTreatingItAsAuthentication(string? cachedHost, string expectedHost)
    {
        var model = new ConnectionViewModel((_, _) => h.Existing.Service) { ExecutablePath = "explicit-gh.exe" };
        var connection = await MountConnectionEntry(model);
        await Ui.Run(async () => {
            Workspace.SuspendConnection();
            if (cachedHost is not null) await SelectCachedConnectionProfile(new(cachedHost, 42));
            else await Workspace.SelectProfileAsync(null);
            Ui.Click("ConnectionPageButton");
        });
        await Ui.Until(() => connection.Visibility == Visibility.Visible);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("HostInput", connection).Text, Is.EqualTo(expectedHost));
            Assert.That(Ui.Find<TextBox>("ExecutablePath", connection).Text, Is.EqualTo("explicit-gh.exe"));
            var account = Ui.Find<TextBlock>("AccountValue", connection).Text;
            Assert.That(account, Does.Contain("未確認"));
            if (cachedHost is null) Assert.That(account, Is.EqualTo("未確認"));
            else Assert.That(account, Does.Contain("保存済み：cached-viewer").And.Contain("ID 42").And.Contain(expectedHost));
            Assert.That(model.Connection, Is.Null);
            Assert.That(Workspace.CanRead, Is.False);
            Assert.That(h.Writes, Is.Empty);
        });
    }

    [Test, Category("ConnectionEntry")]
    public async Task ReturningToTheSameProfilePreservesUnfinishedHostAndExecutableInput()
    {
        var model = new ConnectionViewModel((_, _) => h.Existing.Service) { ExecutablePath = "explicit-gh.exe" };
        var connection = await MountConnectionEntry(model);
        await Ui.Run(() => Ui.Click("ConnectionPageButton"));
        await Ui.Until(() => connection.Visibility == Visibility.Visible);
        await Ui.Run(() => {
            Ui.Find<TextBox>("HostInput", connection).Text = "enterprise-in-progress.";
            Ui.Find<TextBox>("ExecutablePath", connection).Text = @"C:\unfinished path\gh";
            Ui.Click(Ui.Find<Button>("ProjectsPageButton", connection));
        });
        await Ui.Until(() => panel.Visibility == Visibility.Visible);
        await Ui.Run(() => Ui.Click("ConnectionPageButton"));
        await Ui.Until(() => connection.Visibility == Visibility.Visible);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("HostInput", connection).Text, Is.EqualTo("enterprise-in-progress."));
            Assert.That(Ui.Find<TextBox>("ExecutablePath", connection).Text, Is.EqualTo(@"C:\unfinished path\gh"));
            Assert.That(model.Host, Is.EqualTo("enterprise-in-progress."));
            Assert.That(model.ExecutablePath, Is.EqualTo(@"C:\unfinished path\gh"));
            Assert.That(Workspace.Profile, Is.EqualTo(new ConnectionScope("github.com", 42)));
            Assert.That(Workspace.CanRead, Is.False);
            Assert.That(h.Writes, Is.Empty);
        });
    }

    [Test, Category("ConnectionEntry")]
    public async Task AnotherCachedAccountOnTheSameHostCannotInheritTheEarlierAuthenticationDisplay()
    {
        var model = new ConnectionViewModel((_, _) => h.Existing.Service) { ExecutablePath = "explicit-gh.exe" };
        var connection = await MountConnectionEntry(model);
        await Ui.Run(() => Ui.Click("ConnectionPageButton"));
        await Ui.Until(() => connection.Visibility == Visibility.Visible);
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("CheckConnectionButton", connection)));
        await Ui.Until(() => Workspace.CanRead && Ui.Find<Button>("CheckConnectionButton", connection).IsEnabled);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("AccountValue", connection).Text, Does.Contain("ID 42"));
            Ui.Click(Ui.Find<Button>("ProjectsPageButton", connection));
        });
        await Ui.Until(() => panel.Visibility == Visibility.Visible);
        await Ui.Run(async () => {
            await SelectCachedConnectionProfile(new("github.com", 99));
            Ui.Click("ConnectionPageButton");
        });
        await Ui.Until(() => connection.Visibility == Visibility.Visible);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("HostInput", connection).Text, Is.EqualTo("github.com"));
            Assert.That(Ui.Find<TextBlock>("AccountValue", connection).Text,
                Does.Contain("保存済み：cached-viewer").And.Contain("ID 99").And.Contain("現在の認証：未確認").And.Not.Contain("ID 42"));
            Assert.That(model.Connection, Is.Null);
            Assert.That(Workspace.Profile, Is.EqualTo(new ConnectionScope("github.com", 99)));
            Assert.That(Workspace.CanRead, Is.False);
            Assert.That(h.Writes, Is.Empty);
        });
    }

    [TestCase(42), TestCase(99), Category("ConnectionEntry")]
    public async Task CheckingACachedDestinationKeepsItsWorkUntilAnotherAccountIsExplicitlyAccepted(long authenticatedViewer)
    {
        var selected = Workspace.Selected!;
        await Ui.Ready<FrameworkElement>("GridCell0_0");
        await Ui.Run(() => FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>("GridCell0_0")).SetFocus());
        await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() => {
            Ui.Find<TextBox>("GridCell0_0").Text = "Retain this cached work";
            Workspace.SuspendConnection();
        });
        var model = new ConnectionViewModel((_, _) => h.Existing.Service) { ExecutablePath = "explicit-gh.exe" };
        var connection = await MountConnectionEntry(model);
        h.Existing.Boundary.ViewerId = authenticatedViewer;
        await Ui.Run(() => { Ui.Click("ConnectionPageButton"); Ui.Click(Ui.Find<Button>("CheckConnectionButton", connection)); });
        await Ui.Until(() => model.Connection is not null && Ui.Find<Button>("CheckConnectionButton", connection).IsEnabled);
        await Ui.Run(() => {
            Assert.That(Workspace.Profile, Is.EqualTo(selected.Snapshot.Id.Scope));
            Assert.That(Workspace.Selected?.Snapshot.Id, Is.EqualTo(selected.Snapshot.Id));
            Assert.That(Work.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("Retain this cached work"));
            Assert.That(Workspace.CanRead, Is.EqualTo(authenticatedViewer == 42));
            Assert.That(model.Connection!.Result.Failure, Is.EqualTo(authenticatedViewer == 42 ? FailureKind.None : FailureKind.IdentityChanged));
            Assert.That(h.Writes, Is.Empty);
        });
        if (authenticatedViewer == 42) return;
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("ConnectionStatus", connection).Text, Does.Contain("新しい接続として確認"));
            Assert.That(Ui.Find<Button>("NewConnectionButton", connection).IsEnabled, Is.True);
            Ui.Click(Ui.Find<Button>("NewConnectionButton", connection));
        });
        await Ui.Until(() => Workspace.CanRead && Workspace.Profile?.ViewerId == 99 && Ui.Find<Button>("CheckConnectionButton", connection).IsEnabled);
        var retained = await new DraftStore(h.Existing.Root).LoadAsync(selected.Snapshot.Id.Scope);
        Assert.That(retained!.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("Retain this cached work"));
        Assert.That(h.Writes, Is.Empty);
    }

    private async Task<ConnectionPanel> MountConnectionEntry(ConnectionViewModel model)
    {
        ConnectionPanel connection = null!;
        await Ui.Run(() => {
            connection = new ConnectionPanel { ConfirmWorkspaceChangeAsync = scope => panel.ConfirmPlanningNavigationAsync(scope) };
            connection.Initialize(Workspace, model);
            panel.ConnectionRequested += (_, _) => { panel.Visibility = Visibility.Collapsed; connection.Visibility = Visibility.Visible; };
            connection.ReturnRequested += (_, _) => { connection.Visibility = Visibility.Collapsed; panel.Visibility = Visibility.Visible; panel.Update(); panel.ReturnFromConnection(); };
        });
        await Ui.Mount(connection);
        await Ui.Run(() => Ui.Find<Expander>("ConnectionDetails", connection).IsExpanded = true);
        await Ui.Ready<TextBox>("ExecutablePath");
        await Ui.Run(() => connection.Visibility = Visibility.Collapsed);
        return connection;
    }

    private async Task SelectCachedConnectionProfile(ConnectionScope scope)
    {
        Assert.That(await Workspace.FlushDraftsAsync(), Is.True);
        var project = new ProjectRegistration("cached-viewer", "owner", [], null, DateTimeOffset.UtcNow,
            new(new(scope, "Cached"), new(scope, "Owner"), "User", 3,
                $"https://{scope.Host}/users/owner/projects/3", "Cached Project", [],
                new Dictionary<ScopedId, IssueReadModel>(), [], true, true));
        await new RegistrationStore(h.Existing.Root).SaveAsync(project);
        await Workspace.RestoreAsync();
        await Workspace.SelectProfileAsync(scope);
        Assert.That(await Workspace.SelectAsync(project.Snapshot.Id), Is.True);
    }
}
