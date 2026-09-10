using GhProjectsBoards.App.GitHub;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
[Category("Integration")]
internal sealed class TargetDiagnosticTests
{
    [Test]
    public async Task MissingResponseFieldsAreUnknownNotProofThatATargetIsAbsent()
    {
        var runner = GhConnectionTests.ConnectedRunner(_ => ScriptedRunner.Http("{\"data\":{}}"));
        var service = new GhConnectionService("gh.exe", "github.com", runner);
        var connection = await service.ConnectAsync();
        var result = await new TargetDiagnostics(service).ProjectAsync(connection.Context!, connection.Authentication!, "https://github.com/users/example/projects/3");
        Assert.That(result.Failure, Is.EqualTo(FailureKind.InvalidResponse));
        Assert.That(result.CanRead, Is.Null);
    }

    [Test]
    public async Task IssueAndProjectPermissionsAreDiagnosedSeparatelyFromScopes()
    {
        var runner = GhConnectionTests.ConnectedRunner(command => command.StandardInput!.Contains("projectV2", StringComparison.Ordinal)
            ? ScriptedRunner.Http("{\"data\":{\"user\":{\"projectV2\":{\"id\":\"P1\",\"viewerCanUpdate\":true}}}}")
            : ScriptedRunner.Http("{\"data\":{\"repository\":{\"isPrivate\":true,\"issue\":{\"id\":\"I1\",\"viewerCanUpdate\":false}}}}"));
        var service = new GhConnectionService("gh.exe", "github.com", runner);
        var report = await service.ConnectAsync();
        var diagnostics = new TargetDiagnostics(service);
        var limitedScopes = new AuthenticationInfo(CredentialStore.Keyring, null, new HashSet<string> { "repo", "read:project" });
        var issue = await diagnostics.IssueAsync(report.Context!, limitedScopes, "https://github.com/example/sandbox/issues/1#issuecomment-42");
        var project = await diagnostics.ProjectAsync(report.Context!, limitedScopes, "https://github.com/users/example/projects/3/views/1");
        Assert.That(issue.CanRead, Is.True);
        Assert.That(issue.CanUpdate, Is.False);
        Assert.That(issue.HasWriteScope, Is.True);
        Assert.That(project.CanRead, Is.True);
        Assert.That(project.CanUpdate, Is.True);
        Assert.That(project.HasWriteScope, Is.False, "An account's resource permission does not grant a missing OAuth scope.");
        Assert.That(runner.Commands.Any(command => command.StandardInput?.TrimStart().StartsWith("mutation") == true), Is.False);
    }

    [Test]
    public async Task OtherHostUrlsAreRejectedWithoutCallingGh()
    {
        var runner = GhConnectionTests.ConnectedRunner();
        var service = new GhConnectionService("gh.exe", "github.com", runner);
        var report = await service.ConnectAsync();
        runner.Commands.Clear();
        var result = await new TargetDiagnostics(service).IssueAsync(report.Context!, report.Authentication!, "https://other.example/owner/repo/issues/1");
        Assert.That(result.Failure, Is.EqualTo(FailureKind.InvalidInput));
        Assert.That(runner.Commands, Is.Empty);
    }

    [Test]
    public async Task UnreachableProjectRemainsUnknownInsteadOfEmptyOrWritable()
    {
        var runner = GhConnectionTests.ConnectedRunner(_ => new GhProcessResult(ProcessCompletion.TimedOut, true));
        var service = new GhConnectionService("gh.exe", "github.com", runner);
        var report = await service.ConnectAsync();
        var result = await new TargetDiagnostics(service).ProjectAsync(report.Context!, report.Authentication!, "https://github.com/orgs/example/projects/3");
        Assert.That(result.Failure, Is.EqualTo(FailureKind.TimedOut));
        Assert.That(result.CanRead, Is.Null);
        Assert.That(result.CanUpdate, Is.Null);
    }
}
