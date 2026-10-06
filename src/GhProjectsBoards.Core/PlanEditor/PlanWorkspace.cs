using System.Collections.Immutable;
using GhProjectsBoards.Core.Projects;
namespace GhProjectsBoards.Core.PlanEditor;
internal static class PlanColumnMatching
{
    internal static readonly (PlanField Role, string Name, string Type)[] Roles = [
        (PlanField.Estimate, "Estimate", "NUMBER"), (PlanField.Remaining, "Remaining", "NUMBER"),
        (PlanField.Actual, "Actual", "NUMBER"), (PlanField.Start, "Start date", "DATE"),
        (PlanField.End, "Target date", "DATE"), (PlanField.StartNoEarlierThan, "開始日指定", "DATE"),
        (PlanField.Fixed, "日程固定", "SINGLE_SELECT")];
    internal static ImmutableArray<PlanColumnMapping> Match(IEnumerable<PlanColumnDefinition> fields)
    {
        var source = fields.ToArray();
        return Roles.SelectMany(role => {
            var matches = source.Where(f => f.Name.Equals(role.Name, StringComparison.OrdinalIgnoreCase) && f.DataType == role.Type).ToArray();
            return matches.Length == 1 ? new[] { new PlanColumnMapping(role.Role, matches[0].Id, matches[0].Name, role.Type) } : [];
        }).ToImmutableArray();
    }
}

internal sealed record PlanWorkspaceCatalog(int Version, ImmutableArray<ProjectChoice> Projects, ScopedId? Selected);
internal sealed class PlanWorkspace(PlanStore store)
{
    private readonly Dictionary<ScopedId, PlanSession> sessions = [];
    private PlanWorkspaceCatalog catalog = new(1, [], null);
    public GhProjectsBoards.App.GitHub.GhConnectionService? Service { get; private set; }
    public GhProjectsBoards.App.GitHub.ConnectionContext? Context { get; private set; }
    public IReadOnlyList<ProjectChoice> Available { get; private set; } = [];
    public IReadOnlyList<ProjectChoice> Registered => catalog.Projects.Where(p => p.Id.Scope == Scope).ToArray();
    private ConnectionScope? Scope => Context is null ? null : ConnectionScope.From(Context);
    public ProjectChoice? Selected { get; private set; }
    public PlanSession? Session { get; private set; }
    public string? DiscoveryWarning { get; private set; }
    public string Root => store.Root;
    public IReadOnlyList<PlanResource> People
    {
        get
        {
            if (Session is not { } session) return [];
            var document = session.Document;
            return document.State.Settings.People.Select(p => p with { Name = document.Sync.PeopleNames.GetValueOrDefault(p.Identity, p.Name) })
                .Concat(document.State.Rows.SelectMany(r => r.Assignees).Distinct().Where(id => document.State.Settings.People.All(p => p.Identity != id))
                    .Select(id => new PlanResource(id, document.Sync.PeopleNames.GetValueOrDefault(id, "担当者（未確認）"), 100, null, []))).ToArray();
        }
    }
    public static PlanWorkspace ForUser() => new(PlanStore.ForUser());
    public async Task Connect(GhProjectsBoards.App.GitHub.GhConnectionService service, CancellationToken token = default)
    {
        await Flush();
        Service = null; Context = null; Selected = null; Session = null; Available = [];
        var connection = await service.ConnectAsync(token);
        token.ThrowIfCancellationRequested();
        if (!connection.IsConnected) throw new InvalidOperationException(GhProjectsBoards.App.ConnectionViewModel.FailureText(connection.Result.Failure));
        Service = service; Context = connection.Context!;
        var path = Path.Combine(store.Root, "workspace.json");
        if (File.Exists(path))
        {
            catalog = PlanJson.Read<PlanWorkspaceCatalog>(await File.ReadAllBytesAsync(path, token));
            if (catalog.Version != 1 || catalog.Projects.IsDefault || catalog.Projects.Any(p => p.Id.Scope.ViewerId <= 0)
                || catalog.Projects.Select(p => p.Id).Distinct().Count() != catalog.Projects.Length)
                throw new InvalidOperationException("Project一覧を読み込めません。");
        }
        var discovery = new ProjectDiscovery(service);
        var available = new List<ProjectChoice>();
        DiscoveryWarning = null;
        foreach (var owner in await discovery.OwnersAsync(Context, token))
        {
            try { available.AddRange(await discovery.ProjectsAsync(Context, owner.Login, null, "", token)); }
            catch (DiscoveryException) { token.ThrowIfCancellationRequested(); DiscoveryWarning = owner.Login + " のProjectを取得できません。再接続してください。"; }
        }
        token.ThrowIfCancellationRequested();
        Available = available.DistinctBy(p => p.Id).ToArray();
        if (catalog.Selected is { } selected && Registered.SingleOrDefault(p => p.Id == selected) is { } choice)
            await Open(choice, token);
    }
    public async Task OpenUrl(string url, CancellationToken token = default)
        => await Open(await new ProjectDiscovery(Service!).ResolveAsync(Context!, url, token), token);
    public async Task Open(ProjectChoice choice, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Service is null || Context is null || choice.Id.Scope != Scope) throw new InvalidOperationException("接続先が一致しません。");
        await Flush();
        if (!sessions.TryGetValue(choice.Id, out var session))
        {
            var opened = await PlanSession.OpenAsync(store, choice.Id, DateOnly.FromDateTime(DateTime.Today));
            if (opened.Status == PlanLoadStatus.Blocked) throw new IOException(opened.Error);
            session = opened.Session;
            if (session is null)
            {
                using var lease = await Service.BeginOperationAsync(Context, token, mutation: false);
                var read = await PlanSnapshot.ReadConsistentAsync(Service, lease, Context, choice.Id, token);
                token.ThrowIfCancellationRequested();
                if (read.Outcome != ProjectReadOutcome.Complete || read.Project is null) throw new InvalidOperationException("Projectを取得できません。");
                var project = read.Project;
                var fields = project.Fields.Where(f => f.ValueOwner == FieldOwner.ProjectItem && f.Availability == ValueAvailability.Present)
                    .Select(f => new PlanColumnDefinition(f.Id.NodeId, f.Name, f.DataType));
                var repositories = project.Issues.Values.Select(i => i.Repository.NameWithOwner).Distinct().ToArray();
                var people = project.Issues.Values.SelectMany(i => i.Native?.Assignees ?? []).DistinctBy(p => p.Id)
                    .Select(p => new PlanResource(p.Id.NodeId, p.Login, 100, null, [])).ToImmutableArray();
                var settings = new ProjectPlanSettings { Columns = PlanColumnMatching.Match(fields), People = people,
                    DefaultRepository = repositories.Length == 1 ? repositories[0] : null };
                var remote = PlanSnapshot.From(read, settings);
                var document = new PlanDocument(choice.Id, remote.Baseline, new(remote.Baseline.Rows, settings))
                { Sync = new() { DraftCount = remote.DraftCount, PullRequestCount = remote.PullRequestCount, NativeOrders = remote.SubIssueOrders, PeopleNames = remote.PeopleNames } };
                session = await PlanSession.CreateAsync(store, document, DateOnly.FromDateTime(DateTime.Today));
                RequireSave(await session.FlushAsync());
            }
            sessions.Add(choice.Id, session);
        }
        token.ThrowIfCancellationRequested();
        var candidate = catalog with { Projects = catalog.Projects.Where(p => p.Id != choice.Id).Append(choice).ToImmutableArray(), Selected = choice.Id };
        catalog = await SaveCatalog(candidate);
        Selected = choice; Session = session;
    }
    public async Task Refresh(CancellationToken token = default)
    {
        if (Session is null || Service is null || Context is null) return;
        var result = await new PlanPublisher(Service, Context).RefreshAsync(Session, DateOnly.FromDateTime(DateTime.Today), token);
        token.ThrowIfCancellationRequested();
        if (!result.Succeeded) throw new InvalidOperationException(result.Error);
    }
    public async Task Flush()
    {
        foreach (var session in sessions.Values) RequireSave(await session.FlushAsync());
    }
    public async Task RetrySave()
    {
        foreach (var session in sessions.Values) RequireSave(await session.RetrySaveAsync());
        catalog = await SaveCatalog(catalog);
    }
    private static void RequireSave(PlanSaveResult result) { if (!result.Succeeded) throw new IOException(result.Error); }
    private async Task<PlanWorkspaceCatalog> SaveCatalog(PlanWorkspaceCatalog candidate)
    {
        Directory.CreateDirectory(store.Root);
        var path = Path.Combine(store.Root, "workspace.json");
        using var writer = new FileStream(path + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(path))
        {
            var latest = PlanJson.Read<PlanWorkspaceCatalog>(await File.ReadAllBytesAsync(path));
            if (latest.Version != 1 || latest.Projects.IsDefault || latest.Projects.Any(p => p.Id.Scope.ViewerId <= 0)
                || latest.Projects.Select(p => p.Id).Distinct().Count() != latest.Projects.Length)
                throw new IOException("Project一覧を読み込めません。");
            candidate = candidate with { Projects = latest.Projects.Where(p => candidate.Projects.All(c => c.Id != p.Id))
                .Concat(candidate.Projects).ToImmutableArray() };
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await PlanStore.WriteCandidate(temporary, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(candidate, PlanJson.Options));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            return candidate;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
