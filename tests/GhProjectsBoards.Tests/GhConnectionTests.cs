using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
[Category("Integration")]
internal sealed class GhConnectionTests
{
    [Test]
    public async Task NetworkFailureDuringAuthPreflightIsNotReportedAsExpiredCredentials()
    {
        var broken = false;
        var baseline = ConnectedRunner();
        var runner = new ScriptedRunner(command => !broken ? baseline.RunAsync(command).GetAwaiter().GetResult()
            : command.Arguments[0] == "auth" ? Auth(state: "error") : ScriptedRunner.Http("{}", 503, 1));
        var service = new GhConnectionService("gh.exe", "github.com", runner);
        var context = (await service.ConnectAsync()).Context!;
        runner.Commands.Clear();
        broken = true;
        var result = await service.SendAsync(context, ApiRequest.Rest("PATCH", "repos/example/sandbox/issues/1", new { title = "change" }));
        Assert.That(result.Failure, Is.EqualTo(FailureKind.Network));
        Assert.That(result.Outcome, Is.EqualTo(ApiOutcome.Failed), "The target mutation was not dispatched.");
        Assert.That(runner.Commands.Any(command => command.Arguments.Contains("PATCH")), Is.False);
    }

    [Test]
    public async Task RestoredKeyringStorageIsUsedAfterRecheckingTheSameViewer()
    {
        var source = @"C:\test-config\hosts.yml";
        var baseline = ConnectedRunner();
        var runner = new ScriptedRunner(command => command.Arguments[0] == "auth"
            ? Auth(source: source) : baseline.RunAsync(command).GetAwaiter().GetResult());
        var service = new GhConnectionService("gh.exe", "github.com", runner);
        var context = (await service.ConnectAsync()).Context!;
        source = "keyring";
        Assert.That((await service.RecheckAsync(context)).Authentication!.Store, Is.EqualTo(CredentialStore.Keyring));
        var result = await service.SendAsync(context, ApiRequest.Rest("PATCH", "repos/example/sandbox/issues/1", new { title = "change" }));
        Assert.That(result.IsSuccess, Is.True);
    }

    [Test]
    public async Task RecheckingTheScreenDoesNotSilentlyAdoptAnotherAccount()
    {
        var viewer = 42;
        var baseline = ConnectedRunner();
        var runner = new ScriptedRunner(command => command.Arguments[0] == "api" && command.Arguments[1] == "user"
            ? ScriptedRunner.Http(JsonSerializer.Serialize(new { id = viewer, login = "sample-user" }))
            : baseline.RunAsync(command).GetAwaiter().GetResult());
        var service = new GhConnectionService("gh.exe", "github.com", runner);
        var original = (await service.ConnectAsync()).Context!;
        viewer = 99;
        var report = await service.RecheckAsync(original);
        Assert.That(report.Result.Failure, Is.EqualTo(FailureKind.IdentityChanged));
        Assert.That(original.IsInvalidated, Is.True);
        var explicitNewConnection = await service.ConnectAsync();
        Assert.That(explicitNewConnection.Context!.ViewerId, Is.EqualTo(99));
    }

    [TestCase("[]", FailureKind.NotLoggedIn)]
    [TestCase("not-json-synthetic-secret", FailureKind.InvalidResponse)]
    public async Task UnauthenticatedAndMalformedStatusCannotBindAWorkspace(string statusJson, FailureKind expected)
    {
        var baseline = ConnectedRunner();
        var runner = new ScriptedRunner(command => command.Arguments[0] == "auth"
            ? new GhProcessResult(ProcessCompletion.Exited, true, 0, statusJson)
            : baseline.RunAsync(command).GetAwaiter().GetResult());
        var report = await new GhConnectionService("gh.exe", "github.com", runner).ConnectAsync();
        Assert.That(report.Result.Failure, Is.EqualTo(expected));
        Assert.That(report.Context, Is.Null);
        Assert.That(report.ToString(), Does.Not.Contain("synthetic-secret"));
    }

    [Test]
    public async Task StatusExitZeroDoesNotHideExpiredAuthentication()
    {
        var baseline = ConnectedRunner();
        var runner = new ScriptedRunner(command => command.Arguments[0] switch
        {
            "auth" => Auth(state: "error"),
            "api" => ScriptedRunner.Http("{}", 401, 1),
            _ => baseline.RunAsync(command).GetAwaiter().GetResult()
        });
        var report = await new GhConnectionService("gh.exe", "github.com", runner).ConnectAsync();
        Assert.That(report.Result.Failure, Is.EqualTo(FailureKind.AuthenticationExpired));
        Assert.That(report.Context, Is.Null);
    }

    [Test]
    public async Task UnknownScopeMetadataStaysUnknown()
    {
        var baseline = ConnectedRunner();
        var runner = new ScriptedRunner(command => command.Arguments[0] == "auth"
            ? Auth(scopes: null) : baseline.RunAsync(command).GetAwaiter().GetResult());
        var report = await new GhConnectionService("gh.exe", "github.com", runner).ConnectAsync();
        Assert.That(report.IsConnected, Is.True);
        Assert.That(report.Authentication!.HasScope("project"), Is.Null);
    }

    [Test]
    public async Task RechecksTheViewerImmediatelyBeforeEachTargetRequest()
    {
        var runner = ConnectedRunner();
        var service = new GhConnectionService("gh.exe", "github.com", runner);
        var report = await service.ConnectAsync();
        runner.Commands.Clear();
        var result = await service.SendAsync(report.Context!, ApiRequest.Rest("PATCH", "repos/example/sandbox/issues/1", new { title = "changed" }));
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(runner.Commands.Select(command => command.Arguments[0] == "auth" ? "auth" : command.Arguments[1]),
            Is.EqualTo(new[] { "auth", "user", "repos/example/sandbox/issues/1" }));
    }

    [Test]
    public async Task AccountChangeInvalidatesTheOldContextEvenAfterSwitchingBack()
    {
        var viewerId = 42;
        var baseline = ConnectedRunner();
        var runner = new ScriptedRunner(command => command.Arguments[0] == "api" && command.Arguments[1] == "user"
            ? ScriptedRunner.Http(JsonSerializer.Serialize(new { id = viewerId, login = "sample-user" }))
            : baseline.RunAsync(command).GetAwaiter().GetResult());
        var service = new GhConnectionService("gh.exe", "github.com", runner);
        var context = (await service.ConnectAsync()).Context!;
        runner.Commands.Clear();
        viewerId = 99;
        var first = await service.SendAsync(context, ApiRequest.Rest("PATCH", "repos/example/sandbox/issues/1", new { title = "changed" }));
        Assert.That(first.Failure, Is.EqualTo(FailureKind.IdentityChanged));
        Assert.That(context.IsInvalidated, Is.True);
        viewerId = 42;
        var second = await service.SendAsync(context, ApiRequest.Rest("GET", "repos/example/sandbox/issues/1"));
        Assert.That(second.Failure, Is.EqualTo(FailureKind.IdentityChanged));
        Assert.That(runner.Commands.Any(command => command.Arguments.Contains("repos/example/sandbox/issues/1")), Is.False);
    }

    [Test]
    public async Task OtherHostCannotReuseTheConnectionContext()
    {
        var runner = ConnectedRunner();
        var context = (await new GhConnectionService("gh.exe", "github.com", runner).ConnectAsync()).Context!;
        runner.Commands.Clear();
        var result = await new GhConnectionService("gh.exe", "other.example", runner)
            .SendAsync(context, ApiRequest.Rest("POST", "repos/example/sandbox/issues"));
        Assert.That(result.Failure, Is.EqualTo(FailureKind.IdentityChanged));
        Assert.That(runner.Commands, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PlaintextStorageBlocksWritesButAllowsDiagnostics(bool changesAfterConnecting)
    {
        const string storagePath = @"C:\test-config\hosts.yml";
        var source = changesAfterConnecting ? "keyring" : storagePath;
        var baseline = ConnectedRunner();
        var runner = new ScriptedRunner(command => command.Arguments[0] == "auth"
            ? Auth(source: source) : baseline.RunAsync(command).GetAwaiter().GetResult());
        var service = new GhConnectionService("gh.exe", "github.com", runner);
        var report = await service.ConnectAsync();
        Assert.That(report.IsConnected, Is.True);
        if (!changesAfterConnecting) Assert.That(report.Authentication!.StoragePath, Is.EqualTo(storagePath));
        source = storagePath;
        var read = await service.SendAsync(report.Context!, ApiRequest.Rest("GET", "repos/example/sandbox/issues/1"));
        Assert.That(read.IsSuccess, Is.True);
        runner.Commands.Clear();
        var write = await service.SendAsync(report.Context!, ApiRequest.Rest("PATCH", "repos/example/sandbox/issues/1", new { title = "changed" }));
        Assert.That(write.Failure, Is.EqualTo(FailureKind.PlaintextCredentials));
        Assert.That(runner.Commands.Any(command => command.Arguments.Contains("PATCH")), Is.False);
    }

    internal static GhProcessResult Auth(string state = "success", string source = "keyring", string? scopes = "repo, project, read:org",
        string host = "github.com", string login = "sample-user")
        => new(ProcessCompletion.Exited, true, 0,
            JsonSerializer.Serialize(new[] { new { host, login, active = true, state, tokenSource = source, scopes } }));

    internal static ScriptedRunner ConnectedRunner(Func<GhCommand, GhProcessResult>? api = null)
        => new(command => command.Arguments[0] switch
        {
            "--version" => new GhProcessResult(ProcessCompletion.Exited, true, 0, "gh version 2.100.0 (fixture)"),
            "auth" => Auth(),
            _ when command.Arguments[1] == "user" => ScriptedRunner.Http("{\"id\":42,\"login\":\"sample-user\"}"),
            _ => api?.Invoke(command) ?? ScriptedRunner.Http("{\"number\":1}")
        });

    [Test]
    public async Task ConnectsUsingTheLiveViewerIdAndCredentialMetadata()
    {
        var runner = ConnectedRunner();
        var report = await new GhConnectionService("gh.exe", "GitHub.COM", runner).ConnectAsync();
        Assert.That(report.IsConnected, Is.True);
        Assert.That(report.Context!.Host, Is.EqualTo("github.com"));
        Assert.That(report.Context.ViewerId, Is.EqualTo(42));
        Assert.That(report.Context.Login, Is.EqualTo("sample-user"));
        Assert.That(report.Version, Is.EqualTo("2.100.0"));
        Assert.That(report.Authentication!.Store, Is.EqualTo(CredentialStore.Keyring));
        Assert.That(report.Authentication.HasScope("repo"), Is.True);
        Assert.That(runner.Commands.Any(command => command.Arguments.Contains("--show-token") || command.Arguments.Contains("token")), Is.False);
        Assert.That(runner.Commands.Single(command => command.Arguments[0] == "auth").Arguments, Does.Contain("--jq"));
    }
}
