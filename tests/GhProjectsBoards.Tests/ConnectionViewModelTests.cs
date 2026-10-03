using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
[Category("Integration")]
internal sealed class ConnectionViewModelTests
{
    [TestCase(false), TestCase(true)]
    public void EnteringAnUnverifiedWorkspaceExplainsTheConnectionWithoutClaimingAnInputEdit(bool saved)
    {
        var runner = GhConnectionTests.ConnectedRunner();
        var model = new ConnectionViewModel((path, host) => new GhConnectionService(path, host, runner));

        model.EnterWorkspace(saved ? new ConnectionScope("example.test", 42) : null, saved ? "cached-user" : null);

        Assert.Multiple(() => {
            Assert.That(model.StatusText, Is.EqualTo(saved
                ? "保存済みの接続先です。現在の接続状態を確認してください。"
                : "接続は未確認です。接続先とgh.exeを確認してください。"));
            Assert.That(model.AccountText, Is.EqualTo(saved
                ? "保存済み：cached-user  /  ID 42  /  example.test\n現在の認証：未確認"
                : "未確認"));
            Assert.That(model.Host, Is.EqualTo(saved ? "example.test" : "github.com"));
            Assert.That(model.Connection, Is.Null);
            Assert.That(model.CanCheck, Is.True);
            Assert.That(runner.Commands, Is.Empty, "Loading a saved destination must not authenticate it or contact GitHub.");
        });
    }

    [TestCase(null), TestCase("  ")]
    public void SavedDestinationWithoutAKnownLoginDoesNotInventAnAccount(string? login)
    {
        var runner = GhConnectionTests.ConnectedRunner();
        var model = new ConnectionViewModel((path, host) => new GhConnectionService(path, host, runner));

        model.EnterWorkspace(new ConnectionScope("example.test", 42), login);

        Assert.Multiple(() => {
            Assert.That(model.AccountText, Is.EqualTo("未確認"));
            Assert.That(model.Connection, Is.Null);
            Assert.That(runner.Commands, Is.Empty);
        });
    }

    [TestCase(false), TestCase(true)]
    public void EditingAnUncheckedSavedDestinationDoesNotPresentTheSavedAccountAsTheEditedDestination(bool editExecutable)
    {
        var runner = GhConnectionTests.ConnectedRunner();
        var model = new ConnectionViewModel((path, host) => new GhConnectionService(path, host, runner));
        model.EnterWorkspace(new ConnectionScope("example.test", 42), "cached-user");

        if (editExecutable) model.ExecutablePath = "other-gh.exe";
        else model.Host = "different.example.test";

        Assert.Multiple(() => {
            Assert.That(model.AccountText, Is.EqualTo("未確認"));
            Assert.That(model.StatusText, Is.EqualTo("接続先の入力が変わりました。再確認してください。"));
            Assert.That(model.Connection, Is.Null);
            Assert.That(runner.Commands, Is.Empty);
        });
    }

    [TestCase(false), TestCase(true)]
    public async Task EditingTheHostOrExecutableExplainsWhyDisplayedVerificationWasCleared(bool editExecutable)
    {
        var runner = GhConnectionTests.ConnectedRunner();
        var model = new ConnectionViewModel((path, host) => new GhConnectionService(path, host, runner)) { ExecutablePath = "gh.exe" };
        await model.CheckAsync();
        Assert.That(model.Connection?.IsConnected, Is.True);
        runner.Commands.Clear();

        if (editExecutable) model.ExecutablePath = "other-gh.exe";
        else model.Host = "example.test";

        Assert.Multiple(() => {
            Assert.That(model.StatusText, Is.EqualTo("接続先の入力が変わりました。再確認してください。"));
            Assert.That(model.AccountText, Is.EqualTo("未確認"));
            Assert.That(model.Connection, Is.Null);
            Assert.That(model.CanSwitch, Is.True, "The earlier binding still requires explicit replacement.");
            Assert.That(runner.Commands, Is.Empty, "Editing the destination must not implicitly check or send.");
        });
    }

    [TestCase(42), TestCase(99)]
    public async Task CheckingASavedDestinationRequiresItsIdentityOrExplicitReplacement(long viewer)
    {
        var baseline = GhConnectionTests.ConnectedRunner();
        var runner = new ScriptedRunner(command => command.Arguments[0] == "api" && command.Arguments[1] == "user"
            ? ScriptedRunner.Http(System.Text.Json.JsonSerializer.Serialize(new { id = viewer, login = "sample-user" }))
            : baseline.RunAsync(command).GetAwaiter().GetResult());
        var model = new ConnectionViewModel((path, host) => new GhConnectionService(path, host, runner)) { ExecutablePath = "gh.exe" };
        var expected = new ConnectionScope("github.com", 42);
        model.EnterWorkspace(expected, "cached-user");

        await model.CheckAsync();

        Assert.That(model.Connection?.IsConnected, Is.EqualTo(viewer == 42));
        if (viewer == 42)
        {
            var report = model.Connection;
            var status = model.StatusText;
            runner.Commands.Clear();
            model.EnterWorkspace(expected, "cached-user");
            Assert.Multiple(() => {
                Assert.That(model.Connection, Is.SameAs(report));
                Assert.That(model.StatusText, Is.EqualTo(status));
                Assert.That(model.AccountText, Does.Contain("sample-user").And.Contain("ID 42").And.Not.Contain("cached-user"));
                Assert.That(runner.Commands, Is.Empty);
            });
        }
        else
        {
            Assert.That(model.Connection!.Result.Failure, Is.EqualTo(FailureKind.IdentityChanged));
            Assert.That(model.CanSwitch, Is.True);
            await model.CheckAsync(newConnection: true);
            Assert.That(model.Connection?.IsConnected, Is.True);
            Assert.That(model.Connection!.Context!.ViewerId, Is.EqualTo(viewer));
        }
    }

    [Test]
    public async Task ExplicitCheckBindsTheConnectionAndDiagnosesTargetsWithoutWriting()
    {
        var runner = GhConnectionTests.ConnectedRunner(_ => ScriptedRunner.Http("{\"data\":{\"repository\":{\"isPrivate\":true,\"issue\":{\"id\":\"I1\",\"viewerCanUpdate\":true}}}}"));
        var model = new ConnectionViewModel((path, host) => new GhConnectionService(path, host, runner))
        {
            ExecutablePath = "gh.exe", IssueUrl = "https://github.com/example/sandbox/issues/1"
        };
        Assert.That(runner.Commands, Is.Empty, "Opening the app must not contact GitHub.");
        await model.CheckAsync();
        Assert.That(model.Connection?.IsConnected, Is.True);
        Assert.That(model.Issue.CanRead, Is.True);
        Assert.That(model.Project.CanRead, Is.Null, "No Project was requested.");
        Assert.That(model.CanCheck, Is.True);
        Assert.That(runner.Commands.All(command => !command.Arguments.Contains("PATCH")), Is.True);
    }
}
