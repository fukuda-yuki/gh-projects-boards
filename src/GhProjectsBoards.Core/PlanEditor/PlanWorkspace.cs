using System.Collections.Immutable;
using GhProjectsBoards.Core.Projects;
namespace GhProjectsBoards.Core.PlanEditor;
internal static class PlanColumnMatching
{
    internal static readonly (string Role, string Name, string Type)[] Roles = [
        (PlanField.Estimate, "Estimate", "NUMBER"), (PlanField.Remaining, "Remaining", "NUMBER"),
        (PlanField.Actual, "Actual", "NUMBER"), (PlanField.Start, "Start date", "DATE"),
        (PlanField.End, "Target date", "DATE"), (PlanField.StartNoEarlierThan, "開始日指定", "DATE"),
        (PlanField.Fixed, "日程固定", "SINGLE_SELECT"), (PlanField.Phase, "工程", "SINGLE_SELECT")];
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
internal sealed class PlanUnreadableException(ProjectChoice project, string? detail) : Exception(detail)
{
    public ProjectChoice Project { get; } = project;
}
internal sealed class PlanWorkspace(PlanStore store, Func<DateOnly> today)
{
    private readonly Dictionary<ScopedId, PlanSession> sessions = [];
    private PlanWorkspaceCatalog catalog = new(1, [], null);
    public GhProjectsBoards.App.GitHub.GhConnectionService? Service { get; private set; }
    public GhProjectsBoards.App.GitHub.ConnectionContext? Context { get; private set; }
    private readonly Dictionary<string, IReadOnlyList<ProjectListing>> listings = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<OwnerChoice> Owners { get; private set; } = [];
    public OwnerChoice? DefaultOwner => Owners.FirstOrDefault(o => o.Login.Equals(Registered.FirstOrDefault()?.OwnerLogin, StringComparison.OrdinalIgnoreCase)) ?? Owners.FirstOrDefault();
    public IReadOnlyList<ProjectChoice> Registered => catalog.Projects.Where(p => p.Id.Scope == Scope).Reverse().ToArray();
    private ConnectionScope? Scope => Context is null ? null : ConnectionScope.From(Context);
    public ProjectChoice? Selected { get; private set; }
    public PlanSession? Session { get; private set; }
    public string Root => store.Root;
    public IReadOnlyList<PlanResource> People
    {
        get
        {
            if (Session is not { } session) return [];
            return PlanPeople.Assigned(session.Document);
        }
    }
    public static PlanWorkspace ForUser(Func<DateOnly> today) => new(PlanStore.ForUser(), today);
    public async Task<PlanCsvPreview> PreviewCsv(PlanCsvFile file, CancellationToken token = default)
    {
        var session = Session ?? throw new InvalidOperationException("プロジェクトを開いてください。");
        if (Service is null || Context is null || session.Document.Project.Scope != Scope) throw new InvalidOperationException("接続先が一致しません。");
        if (!file.Errors.IsEmpty) return new(file.Errors, null, false);
        using var lease = await Service.BeginOperationAsync(Context, token, mutation: false);
        var repositories = new List<PlanCsvRepository>();
        foreach (var name in file.Rows.Select(r => r.Repository.Length == 0 ? session.Document.State.Settings.DefaultRepository ?? "" : r.Repository).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!PlanOperations.Repository(name)) continue;
            var parts = name.Split('/');
            var people = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? after = null; var cursors = new HashSet<string>();
            do
            {
                var response = await lease.SendAsync(GhProjectsBoards.App.GitHub.ApiRequest.GraphQl(
                    "query PlanCsvRepository($owner:String!,$name:String!,$after:String){repository(owner:$owner,name:$name){nameWithOwner hasIssuesEnabled isArchived viewerCanCreateIssues assignableUsers(first:100,after:$after){nodes{id login} pageInfo{hasNextPage endCursor}}}}",
                    new { owner = parts[0], name = parts[1], after }), token);
                if (response.Failure == GhProjectsBoards.App.GitHub.FailureKind.NotFoundOrInaccessible ||
                    response.Failure == GhProjectsBoards.App.GitHub.FailureKind.GraphQl &&
                    response.Data is { } unavailable && unavailable.TryGetProperty("data", out var unavailableData) &&
                    unavailableData.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    unavailableData.TryGetProperty("repository", out var unavailableRepository) && unavailableRepository.ValueKind == System.Text.Json.JsonValueKind.Null &&
                    response.GraphQlErrors.Count > 0 && response.GraphQlErrors.All(code => code is "NOT_FOUND" or "FORBIDDEN")) break;
                if (!response.IsSuccess || response.Data is not { } body) throw new InvalidOperationException("CSVのリポジトリと担当者を確認できません。");
                var repository = body.GetProperty("data").GetProperty("repository");
                if (repository.ValueKind == System.Text.Json.JsonValueKind.Null) break;
                if (!repository.GetProperty("hasIssuesEnabled").GetBoolean() || repository.GetProperty("isArchived").GetBoolean() || !repository.GetProperty("viewerCanCreateIssues").GetBoolean()) break;
                var canonical = repository.GetProperty("nameWithOwner").GetString()!;
                if (!canonical.Equals(name, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("リポジトリが一致しません。");
                var connection = repository.GetProperty("assignableUsers");
                foreach (var person in connection.GetProperty("nodes").EnumerateArray()) people[person.GetProperty("login").GetString()!] = person.GetProperty("id").GetString()!;
                var page = connection.GetProperty("pageInfo");
                if (!page.GetProperty("hasNextPage").GetBoolean()) { repositories.Add(new(canonical, people)); break; }
                after = page.GetProperty("endCursor").GetString();
                if (string.IsNullOrWhiteSpace(after) || !cursors.Add(after)) throw new InvalidOperationException("担当者の一覧を最後まで取得できません。");
            } while (true);
        }
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(Session, session)) throw new InvalidOperationException("プロジェクトが変更されました。CSVを選び直してください。");
        return PlanCsvImport.Prepare(session.Document, file, repositories, today());
    }
    // #253: verify one typed login against the row's repository on the read-only lease, so assigning a person
    // never needs the repository's whole assignable-user list. The response must be the connected viewer's, the
    // same repository, and exactly one node whose login equals the typed text; the result is the stored person
    // shape the registration command carries. A refusal or a failed lookup writes nothing.
    public async Task<PlanResource> ResolveAssigneeAsync(string repository, string login, CancellationToken token = default)
    {
        if (Service is null || Context is null) throw new InvalidOperationException("接続してください。");
        if (!PlanOperations.Repository(repository)) throw new InvalidOperationException("担当者のリポジトリを確認できません。");
        var parts = repository.Split('/');
        using var lease = await Service.BeginOperationAsync(Context, token, mutation: false);
        var response = await lease.SendAsync(GhProjectsBoards.App.GitHub.ApiRequest.GraphQl(ProjectQueries.ResolveAssignee,
            new { owner = parts[0], name = parts[1], login }), token);
        token.ThrowIfCancellationRequested();
        if (!response.IsSuccess || response.Data is not { } body)
            throw new InvalidOperationException("担当者の確認に失敗しました。再試行してください。");
        var data = body.GetProperty("data");
        if (data.GetProperty("viewer").GetProperty("databaseId").GetInt64() != Context.ViewerId)
            throw new InvalidOperationException("接続先のアカウントが変わりました。再接続してください。");
        var found = data.GetProperty("repository");
        if (found.ValueKind == System.Text.Json.JsonValueKind.Null ||
            !string.Equals(found.GetProperty("nameWithOwner").GetString(), repository, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("担当者のリポジトリを確認できません。");
        var nodes = found.GetProperty("assignableUsers").GetProperty("nodes").EnumerateArray()
            .Where(node => node.ValueKind == System.Text.Json.JsonValueKind.Object)
            .Select(node => (Id: node.GetProperty("id").GetString(), Login: node.GetProperty("login").GetString()))
            .Where(node => !string.IsNullOrWhiteSpace(node.Id) && !string.IsNullOrWhiteSpace(node.Login)).ToArray();
        var exact = nodes.Where(node => node.Login!.Equals(login, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length != 1) throw new ArgumentException("担当者を確認できません: " + login);
        return new PlanResource(exact[0].Id!, exact[0].Login!, 100, null);
    }
    public async Task Connect(GhProjectsBoards.App.GitHub.GhConnectionService service, CancellationToken token = default)
    {
        await Flush();
        Service = null; Context = null; Selected = null; Session = null; Owners = []; listings.Clear();
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
                throw new InvalidOperationException("プロジェクト一覧を読み込めません。");
        }
        Owners = await new ProjectDiscovery(service).OwnersAsync(Context, token);
        token.ThrowIfCancellationRequested();
        if (catalog.Selected is { } selected && Registered.SingleOrDefault(p => p.Id == selected) is { } choice)
            await Open(choice, token);
    }
    public async Task<IReadOnlyList<ProjectListing>> Listings(string owner, bool reload = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var context = Context ?? throw new InvalidOperationException("接続してください。");
        var service = Service!;
        if (!reload && listings.TryGetValue(owner, out var cached)) return cached;
        var result = await new ProjectDiscovery(service).ListingsAsync(context, owner, token);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(context, Context)) throw new OperationCanceledException("接続先が変更されました。", token);
        listings[owner] = result;
        return result;
    }
    public async Task OpenUrl(string url, CancellationToken token = default)
        => await Open(await new ProjectDiscovery(Service!).ResolveAsync(Context!, url, token), token);
    public async Task Open(ProjectChoice choice, CancellationToken token = default, IProgress<RemoteProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        if (Service is null || Context is null || choice.Id.Scope != Scope) throw new InvalidOperationException("接続先が一致しません。");
        await Flush();
        var adoptionStarted = false;
        if (!sessions.TryGetValue(choice.Id, out var session))
        {
            var opened = await PlanSession.OpenAsync(store, choice.Id, today());
            if (opened.Status == PlanLoadStatus.Unreadable) throw new PlanUnreadableException(choice, opened.Error);
            if (opened.Status == PlanLoadStatus.Blocked) throw new IOException(opened.Error);
            session = opened.Session;
            if (session is null)
            {
                using var lease = await Service.BeginOperationAsync(Context, token, mutation: false);
                var read = await PlanSnapshot.ReadConsistentAsync(Service, lease, Context, choice.Id, token, progress);
                token.ThrowIfCancellationRequested();
                if (read.Outcome != ProjectReadOutcome.Complete || read.Project is null) throw new InvalidOperationException("プロジェクトを取得できません。");
                var project = read.Project;
                var fields = project.Fields.Where(f => f.ValueOwner == FieldOwner.ProjectItem && f.Availability == ValueAvailability.Present)
                    .Select(f => new PlanColumnDefinition(f.Id.NodeId, f.Name, f.DataType));
                var repositories = project.Issues.Values.Select(i => i.Repository.NameWithOwner).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                // #253: the Project's people are the people assigned in it. The complete read already returns
                // each Issue's assignees, so opening never reads a repository's assignable-user list.
                var names = ImmutableDictionary.CreateBuilder<string, string>();
                foreach (var person in project.Issues.Values.SelectMany(i => i.Native?.Assignees ?? [])) names[person.Id.NodeId] = person.Login;
                var people = names.Select(p => new PlanResource(p.Key, p.Value, 100, null)).ToImmutableArray();
                var settings = new ProjectPlanSettings { StatusDate = today(), Columns = PlanColumnMatching.Match(fields), People = people,
                    DefaultRepository = repositories.Length == 1 ? repositories[0] : null };
                var remote = PlanSnapshot.From(read, settings);
                settings = PlanInitialSettings.From(remote.Baseline, settings);
                var document = new PlanDocument(choice.Id, remote.Baseline, new(remote.Baseline.Rows, settings))
                { Sync = new() { InaccessibleCount = remote.InaccessibleCount, DraftCount = remote.DraftCount, PullRequestCount = remote.PullRequestCount, NativeOrders = remote.SubIssueOrders, IssueLinks = remote.IssueLinks, PeopleNames = names.ToImmutable() } };
                progress?.Report(new("日程を計算"));
                token.ThrowIfCancellationRequested();
                adoptionStarted = true;
                session = await PlanSession.CreateAsync(store, document, today());
                RequireSave(await session.FlushAsync());
            }
            sessions.Add(choice.Id, session);
        }
        // A new document and its selection complete together once adoption starts.
        if (!adoptionStarted) token.ThrowIfCancellationRequested();
        var candidate = catalog with { Projects = catalog.Projects.Where(p => p.Id != choice.Id).Append(choice).ToImmutableArray(), Selected = choice.Id };
        catalog = await SaveCatalog(candidate);
        Selected = choice; Session = session;
    }
    public async Task ReadAgain(ProjectChoice choice, CancellationToken token = default, IProgress<RemoteProgress>? progress = null)
    {
        if (Service is null || Context is null || choice.Id.Scope != Scope) throw new InvalidOperationException("接続先が一致しません。");
        await store.SetAsideUnreadable(choice.Id);
        await Open(choice, token, progress);
    }
    public async Task<PlanRefreshResult?> Refresh(CancellationToken token = default, IProgress<RemoteProgress>? progress = null)
    {
        if (Session is null || Service is null || Context is null) return null;
        var result = await new PlanPublisher(Service, Context).RefreshAsync(Session, today(), token, progress);
        if (!result.Succeeded) throw new InvalidOperationException(result.Error);
        return result.Refresh;
    }
    public async Task<PlanRefreshResult?> Reset(CancellationToken token = default, IProgress<RemoteProgress>? progress = null)
    {
        if (Session is null || Service is null || Context is null) return null;
        var result = await new PlanPublisher(Service, Context).ResetAsync(Session, today(), token, progress);
        if (!result.Succeeded) throw new InvalidOperationException(result.Error);
        return result.Refresh;
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
    private static void RequireSave(PlanSaveResult result) { if (!result.Succeeded) throw new PlanSaveFailedException(result); }
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
                throw new IOException("プロジェクト一覧を読み込めません。");
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
