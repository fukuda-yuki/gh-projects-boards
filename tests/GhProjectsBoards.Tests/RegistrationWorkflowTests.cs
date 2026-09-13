using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class RegistrationWorkflowTests
{
    private static (ProjectReaderTests.ProjectBoundary Boundary, GhConnectionService Service) Boundary()
    {
        var boundary = new ProjectReaderTests.ProjectBoundary();
        boundary.Override = (q, v) => RegistrationResponses.Query(q, v) is { } value ? ScriptedRunner.Http(JsonSerializer.Serialize(value)) : null;
        return (boundary, new GhConnectionService("gh.exe", "github.com", boundary.Runner));
    }
    private static RegistrationStore Store() => new(Path.Combine(Path.GetTempPath(), "ghpb-registration-" + Guid.NewGuid().ToString("N")));
    private static async Task<ProjectChoice> Choice(GhConnectionService service, ConnectionContext context, int number = 1)
        => await new ProjectDiscovery(service).ResolveAsync(context, $"https://github.com/users/sample-user/projects/{number}", default);

    [TestCase("permission")]
    [TestCase("cursor")]
    public async Task DiscoveryErrorsNeverPublishACompleteList(string mode)
    {
        var (b, s) = Boundary(); var c = (await s.ConnectAsync()).Context!;
        b.Override = (q, v) => mode == "permission" ? ScriptedRunner.Http("{}", 403)
            : ScriptedRunner.Http(JsonSerializer.Serialize(new { data = new { viewer = new { organizations = new
                { nodes = new[] { new { login = "org" } }, pageInfo = new { hasNextPage = true, endCursor = "same" } } } } }));
        var ex = Assert.ThrowsAsync<DiscoveryException>(() => new ProjectDiscovery(s).OwnersAsync(c, default));
        Assert.That(ex!.Failure, Is.EqualTo(mode == "permission" ? FailureKind.PermissionDenied : FailureKind.InvalidResponse));
        b.AssertQueriesOnly();
    }

    [Test]
    public async Task LateResultAfterProfileSwitchCannotPublishOrSave()
    {
        var (b, _) = Boundary(); var blocked = new DelayedRunner(b.Runner);
        var service = new GhConnectionService("gh.exe", "github.com", blocked);
        var c = (await service.ConnectAsync()).Context!; var p = await Choice(service, c);
        var store = Store(); var w = new RegistrationWorkspace(store); await w.BindAsync(c, service);
        var retrieval = w.RegisterAsync(p, null);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var switchProfile = w.SelectProfileAsync(new("other.test", 99));
        Assert.That(switchProfile.IsCompleted, Is.False, "Switch must settle owned work.");
        blocked.Release.TrySetResult(); await switchProfile; await retrieval;
        Assert.That(w.Profile, Is.EqualTo(new ConnectionScope("other.test", 99)));
        Assert.That(w.Selected, Is.Null); Assert.That(w.Registrations, Is.Empty); Assert.That((await store.LoadAsync()).Registrations, Is.Empty);
    }

    private sealed class DelayedRunner(IGhProcessRunner inner) : IGhProcessRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken cancellationToken = default)
        {
            if (command.StandardInput?.Contains("ProjectFields") == true) { Started.TrySetResult(); await Release.Task; }
            // Deliberately deliver a late external response even after cancellation.
            return await inner.RunAsync(command, CancellationToken.None);
        }
    }

    [Test]
    public async Task ReadOnlyScopeAndPlaintextStoragePermitDiscoveryAndRetrieval()
    {
        var (boundary, _) = Boundary();
        var runner = new AuthReadOnlyRunner(boundary.Runner);
        var service = new GhConnectionService("gh.exe", "github.com", runner);
        var connection = await service.ConnectAsync();
        Assert.That(connection.Authentication!.Store, Is.EqualTo(CredentialStore.Plaintext));
        Assert.That(connection.Authentication.HasScope("project"), Is.False);
        var w = new RegistrationWorkspace(Store()); await w.BindAsync(connection.Context!, service);
        await w.RegisterAsync(await Choice(service, connection.Context!), null);
        Assert.That(w.Registrations, Has.Count.EqualTo(1)); boundary.AssertQueriesOnly();
    }
    private sealed class AuthReadOnlyRunner(IGhProcessRunner inner) : IGhProcessRunner
    {
        public Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken token = default)
            => command.Arguments[0] == "auth" ? Task.FromResult(GhConnectionTests.Auth(source: "C:/isolated/hosts.yml", scopes: "read:project")) : inner.RunAsync(command, token);
    }

    [Test]
    public async Task OrganizationUrlResolvesStableIdentity()
    {
        var (b, s) = Boundary(); var c = (await s.ConnectAsync()).Context!;
        b.Override = (_, _) => ScriptedRunner.Http(JsonSerializer.Serialize(new { data = new { organization = new { projectV2 = new
        {
            id = "P-org", number = 7, title = "Organization project", url = "https://github.com/orgs/org/projects/7",
            owner = new { id = "O-org", login = "org", __typename = "Organization" }
        } } } }));
        var choice = await new ProjectDiscovery(s).ResolveAsync(c, "https://github.com/orgs/org/projects/7/views/1", default);
        Assert.That(choice.Id.NodeId, Is.EqualTo("P-org")); Assert.That(choice.OwnerType, Is.EqualTo("Organization"));
        b.AssertQueriesOnly();
    }

    [Test]
    public async Task SameNodeAndViewerOnAnotherHostHasSeparateFileAndSelection()
    {
        var (_, s) = Boundary(); var c = (await s.ConnectAsync()).Context!; var p = await Choice(s, c);
        var store = Store(); var w = new RegistrationWorkspace(store); await w.BindAsync(c, s); await w.RegisterAsync(p, null);
        var otherId = new ScopedId(new("other.test", 42), p.Id.NodeId);
        Assert.That(store.FileFor(otherId), Is.Not.EqualTo(store.FileFor(p.Id)));
        await w.SelectProfileAsync(otherId.Scope); await w.SelectAsync(p.Id); Assert.That(w.Selected, Is.Null);
        Assert.That((await store.LoadAsync()).Registrations, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task DiscoveryTraversesOwnersRepositoriesProjectsAndRepositoryLinks()
    {
        var (boundary, service) = Boundary(); var context = (await service.ConnectAsync()).Context!;
        var discovery = new ProjectDiscovery(service);
        Assert.That(await discovery.OwnersAsync(context, default), Has.Count.EqualTo(3));
        Assert.That(await discovery.RepositoriesAsync(context, "sample-user", default), Has.Count.EqualTo(2));
        var projects = await discovery.ProjectsAsync(context, "sample-user", null, "", default);
        Assert.That(projects.Select(p => p.Id.NodeId), Is.EqualTo(new[] { "P1", "P2" }));
        var linked = await discovery.ProjectsAsync(context, "sample-user", "first", "", default);
        Assert.That(linked.Single().Id, Is.EqualTo(projects[0].Id));
        Assert.That(await discovery.LinksAsync(context, projects[1].Id, default), Is.Empty);
        boundary.AssertQueriesOnly();
    }

    [TestCase("https://other.test/users/sample-user/projects/1")]
    [TestCase("https://github.com/sample-user/repo/projects/1")]
    [TestCase("https://github.com/users/sample-user/projects/0")]
    public async Task InvalidUrlDispatchesNoDiscovery(string url)
    {
        var (b, s) = Boundary(); var c = (await s.ConnectAsync()).Context!; var count = b.Runner.Commands.Count;
        Assert.ThrowsAsync<DiscoveryException>(() => new ProjectDiscovery(s).ResolveAsync(c, url, default));
        Assert.That(b.Runner.Commands.Count, Is.EqualTo(count));
    }

    [Test]
    public async Task TwoProjectsDuplicateRoutesRestartAndLocalRemovalPreserveContent()
    {
        var (b, s) = Boundary(); var c = (await s.ConnectAsync()).Context!;
        var store = Store(); var w = new RegistrationWorkspace(store); await w.BindAsync(c, s);
        var linked = (await new ProjectDiscovery(s).ProjectsAsync(c, "sample-user", "first", "", default)).Single();
        await w.RegisterAsync(linked, "sample-user/first");
        Assert.That(w.Status, Does.StartWith("登録完了"));
        Assert.That(w.Selected!.Snapshot.Items, Has.Count.EqualTo(101));
        Assert.That(w.Selected.Snapshot.Issues.Values.Select(i => i.Repository.NameWithOwner).Distinct().Count(), Is.EqualTo(2));
        var direct = await Choice(s, c); var calls = b.Runner.Commands.Count;
        await w.RegisterAsync(direct, null);
        Assert.That(b.Runner.Commands.Count, Is.EqualTo(calls));
        await w.RegisterAsync(await Choice(s, c, 2), null);
        var restored = new RegistrationWorkspace(new(store.Root)); await restored.RestoreAsync();
        Assert.That(restored.Registrations, Has.Count.EqualTo(2));
        Assert.That(restored.Profile, Is.Null); Assert.That(restored.CanRead, Is.False);
        await restored.SelectProfileAsync(linked.Id.Scope); await restored.SelectAsync(linked.Id);
        Assert.That(restored.Selected!.DefaultRepository, Is.EqualTo("sample-user/first"));
        Assert.That(restored.Selected.Snapshot.Items.Last().Id.NodeId, Is.EqualTo("P1-T101"));
        Assert.That(restored.Selected.Snapshot.Fields[1].Availability, Is.EqualTo(ValueAvailability.Unsupported));
        await restored.UnregisterAsync();
        Assert.That((await store.LoadAsync()).Registrations.Single().Snapshot.Id.NodeId, Is.EqualTo("P2"));
        Assert.That(File.Exists(store.FileFor(linked.Id)), Is.False); b.AssertQueriesOnly();
    }

    [TestCase("partial")]
    [TestCase("failure")]
    [TestCase("cancel")]
    public async Task IncompleteFirstReadIsNotRegisteredAndFailedRefreshPreservesSavedFile(string kind)
    {
        var (b, s) = Boundary(); var c = (await s.ConnectAsync()).Context!; var choice = await Choice(s, c);
        var good = b.Override; var store = Store(); var w = new RegistrationWorkspace(store); await w.BindAsync(c, s);
        b.Override = (q, v) => q.Contains(kind == "partial" ? "ProjectItems" : "ProjectFields")
            ? kind == "cancel" ? new GhProcessResult(ProcessCompletion.Cancelled, true)
                : ScriptedRunner.Http("{}", kind == "partial" ? 403 : 500)
            : good!(q, v);
        await w.RegisterAsync(choice, null);
        Assert.That(w.Registrations, Is.Empty); Assert.That((await store.LoadAsync()).Registrations, Is.Empty);
        b.Override = good; await w.RegisterAsync(choice, "sample-user/first");
        var bytes = await File.ReadAllBytesAsync(store.FileFor(choice.Id));
        b.Override = (q, v) => q.Contains("ProjectFields") ? ScriptedRunner.Http("{}", 403) : good!(q, v);
        await w.RegisterAsync(choice, null, true);
        Assert.That(await File.ReadAllBytesAsync(store.FileFor(choice.Id)), Is.EqualTo(bytes));
        Assert.That(w.Selected!.DefaultRepository, Is.EqualTo("sample-user/first")); b.AssertQueriesOnly();
    }

    [Test]
    public async Task IdentityChangeCannotRegisterAnotherAccountsData()
    {
        var (b, s) = Boundary(); var c = (await s.ConnectAsync()).Context!; var p = await Choice(s, c);
        var store = Store(); var w = new RegistrationWorkspace(store); await w.BindAsync(c, s);
        b.ViewerId = 99; await w.RegisterAsync(p, null);
        Assert.That(w.Registrations, Is.Empty); Assert.That(c.IsInvalidated, Is.True);
        var next = (await s.ConnectAsync()).Context!; await w.BindAsync(next, s);
        await w.RegisterAsync(p, null); Assert.That(w.Registrations, Is.Empty);
        await w.RegisterAsync(await Choice(s, next), null);
        Assert.That(w.Registrations.Single().Snapshot.Id.Scope.ViewerId, Is.EqualTo(99));
    }

    [Test]
    public async Task CompetingWriterFailsSaveWithoutPublishingSuccess()
    {
        var (b, s) = Boundary(); var c = (await s.ConnectAsync()).Context!; var p = await Choice(s, c);
        var store = Store(); Directory.CreateDirectory(store.Root);
        using var competing = new FileStream(Path.Combine(store.Root, ".writer.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var w = new RegistrationWorkspace(store); await w.BindAsync(c, s); await w.RegisterAsync(p, null);
        Assert.That(w.Registrations, Is.Empty); Assert.That(w.Status, Does.StartWith("ローカル保存失敗"));
    }

    [TestCase("version")]
    [TestCase("nullItem")]
    [TestCase("nullIssue")]
    [TestCase("missingAvailability")]
    [TestCase("scope")]
    [TestCase("json")]
    [TestCase("duplicateOption")]
    public async Task CorruptRecordIsDiagnosedAndPreserved(string corruption)
    {
        var (_, s) = Boundary(); var c = (await s.ConnectAsync()).Context!; var p = await Choice(s, c);
        var store = Store(); var w = new RegistrationWorkspace(store); await w.BindAsync(c, s); await w.RegisterAsync(p, null);
        var file = store.FileFor(p.Id); var json = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        switch (corruption)
        {
            case "version": json["Version"] = 99; break;
            case "nullItem": json["Snapshot"]!["Items"]![0] = null; break;
            case "nullIssue": json["Snapshot"]!["Issues"]![0] = null; break;
            case "missingAvailability": json["Snapshot"]!["Fields"]![0]!.AsObject().Remove("Availability"); break;
            case "scope": json["Snapshot"]!["Id"]!["Scope"]!["ViewerId"] = 99; break;
            case "duplicateOption": json["Snapshot"]!["Fields"]![0]!["Options"]![1]!["Id"] = "todo"; break;
        }
        var text = corruption == "json" ? "{broken" : json.ToJsonString(); await File.WriteAllTextAsync(file, text);
        var result = await store.LoadAsync(); Assert.That(result.Registrations, Is.Empty); Assert.That(result.Problems, Has.Count.EqualTo(1));
        Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(text));
    }

    [Test]
    public async Task InterruptedReplacementKeepsGoodFileAndUnregistrationRemovesOwnedLeftovers()
    {
        var (_, s) = Boundary(); var c = (await s.ConnectAsync()).Context!; var p = await Choice(s, c);
        var store = Store(); var w = new RegistrationWorkspace(store); await w.BindAsync(c, s); await w.RegisterAsync(p, null);
        var file = store.FileFor(p.Id); var bytes = await File.ReadAllBytesAsync(file);
        using (var denyReplacement = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAsync<IOException>(() => store.SaveAsync(w.Selected! with { DefaultRepository = "sample-user/second" }));
        Assert.That(await File.ReadAllBytesAsync(file), Is.EqualTo(bytes));
        var loaded = await store.LoadAsync(); Assert.That(loaded.Registrations, Has.Count.EqualTo(1)); Assert.That(loaded.Problems, Is.Not.Empty);
        await store.RemoveAsync(p.Id);
        Assert.That(Directory.GetFiles(store.Root).Where(f => !f.EndsWith(".writer.lock")), Is.Empty);
    }
}
