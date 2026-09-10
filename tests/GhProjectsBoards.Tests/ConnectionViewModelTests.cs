using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
[Category("Integration")]
internal sealed class ConnectionViewModelTests
{
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
