using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.PlanEditor;

internal sealed record PlanRefreshResult(DateOnly PreviousStatusDate, DateOnly StatusDate, ImmutableHashSet<string> ChangedTasks)
{
    public ImmutableArray<string> AddedTasks { get; init; } = [];
    // Issues the read found for the first time; Issues this app created in the same reconciliation are not "added".
    internal static ImmutableArray<string> FindAdded(PlanDocument before, PlanDocument after)
    {
        var known = before.State.Rows.Select(r => r.Identity).Concat(before.Baseline.Rows.Select(r => r.Identity))
            .Concat(before.Sync.Publish?.Writes.Where(w => w.Stage == PlanPublishStage.Create && w.ResultId is not null).Select(w => w.ResultId!) ?? [])
            .ToHashSet(StringComparer.Ordinal);
        return after.State.Rows.Where(r => !known.Contains(r.Identity)).Select(r => r.Identity).ToImmutableArray();
    }
}
internal sealed record PlanPublishResult(bool Succeeded, string? Error, PlanPublishReview? Review)
{ public PlanRefreshResult? Refresh { get; init; } }
internal sealed class PlanPublisher(GhConnectionService service, ConnectionContext context)
{
    // GitHub prefers serial requests; keep the owner-measured bound in flight and back off on pushback.
    private const int FieldConcurrencyLimit = 4;
    // #201: the Project, field and option metadata one publish read once, plus the repositories its first
    // scoped read covered. Every later scoped read of the same attempt reuses it; a new publish starts empty.
    private sealed class PublishObservation { public ProjectReadModel? Metadata { get; set; } }
    public List<int> EffectiveBatchSizes { get; } = [];
    public TimeSpan CreationWait { get; private set; }
    public DateTimeOffset? CreationPhaseStarted { get; private set; }
    private readonly SemaphoreSlim gate = new(1, 1);
    private static async Task<PlanPublishResult> CompletePublication(PlanSession session, DateOnly today, PlanPublishReview? review)
    {
        var result = Result(session, today, review);
        if (result.Succeeded) RequireSave(await session.CompletePublication().ConfigureAwait(false));
        return result;
    }
    public Task<PlanPublishResult> RefreshAsync(PlanSession session, DateOnly today, CancellationToken token = default, IProgress<RemoteProgress>? progressReporter = null)
        => ReadProjectAsync(session, today, false, token, progressReporter);
    public Task<PlanPublishResult> ResetAsync(PlanSession session, DateOnly today, CancellationToken token = default, IProgress<RemoteProgress>? progressReporter = null)
        => ReadProjectAsync(session, today, true, token, progressReporter);
    private async Task<PlanPublishResult> ReadProjectAsync(PlanSession session, DateOnly today, bool reset, CancellationToken token, IProgress<RemoteProgress>? progressReporter)
    {
        if (reset) {
            if (!await gate.WaitAsync(0, token).ConfigureAwait(false)) return new(false, "更新または発行が進行中です。", null);
        } else await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var operation = session.BeginRemoteOperation();
            if (reset && session.ResetUnavailableReason is { } reason) return new(false, reason, null);
            using var lease = await service.BeginOperationAsync(context, token, mutation: false).ConfigureAwait(false);
            var before = session.Document;
            await Reconcile(session, lease, today, token, progressReporter, refresh: true, reset: reset).ConfigureAwait(false);
            return new(true, null, null) { Refresh = new(before.State.Settings.StatusDate ?? today, today,
                reset ? [] : PlanOperations.ChangedTaskDates(before, session.Document, today))
                { AddedTasks = reset ? [] : session.Document.Sync.LastRead?.Added ?? [] } };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (Expected(ex)) {
            var saved = reset ? null : await RetainInterruptedAttempt(session).ConfigureAwait(false);
            var message = ex is ConnectionLostException ? ex.Message
                : (session.Document.Sync.Unverified.IsEmpty ? "" : "発行結果は GitHub で未確認です。「最新の情報に更新」で確認してください。 ") + ex.Message;
            return new(false, message + (saved is { Succeeded: false } ? " " + saved.Error : ""), null);
        }
        finally { gate.Release(); }
    }
    private async Task<PlanRemoteSnapshot> Read(PlanSession session, GhConnectionService.OperationLease lease, CancellationToken token, IProgress<RemoteProgress>? progressReporter = null)
    {
        var document = session.Document;
        if (document.Project.Scope != ConnectionScope.From(context)) throw new InvalidOperationException("プロジェクトの接続先が一致しません。");
        var read = await PlanSnapshot.ReadConsistentAsync(service, lease, context, document.Project, token, progressReporter).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // #308: read the Issue history only for the rows this read must fill or refresh.
        var history = await PlanSnapshot.ReadHistoriesAsync(service, lease, context, document.Project,
            PlanSnapshot.HistoryScope(read, document), token).ConfigureAwait(false);
        var snapshot = PlanSnapshot.From(read, document.State.Settings, history: history);
        if (snapshot.Baseline.Rows.Any(r => r.ExtraValues.Any(v => v.Value.Availability is Projects.ValueAvailability.Unavailable or Projects.ValueAvailability.NotLoaded)))
            throw new InvalidOperationException("追加列を読み取れません。計画は変更していません。");
        return snapshot;
    }
    private static bool Expected(Exception ex) => ex is InvalidOperationException or ArgumentException or IOException or OperationCanceledException or JsonException or KeyNotFoundException or FormatException or OverflowException;
    // #201: a publish reads only the items it is about to write. Before an attempt exists, the scope is
    // every row the plan itself reports as unpublished; once one is durable, the scope is that attempt.
    private static ImmutableArray<string> PublicationScope(PlanDocument document, DateOnly today)
        => document.Sync.Publish is { } progress
            ? WriteScope(progress.Writes)
            : Readable(PlanOperations.Changes(document, today).Fields.Keys);
    // The rows a write names as well as the rows it targets: the parent whose child order moves, the
    // anchor a position move follows, and the Issues this attempt created. Reading them keeps the item
    // identities those inputs resolve against available before anything is dispatched.
    private static ImmutableArray<string> WriteScope(IEnumerable<PlanWrite> writes)
    {
        var attempt = writes.ToImmutableArray();
        return Readable(attempt.Where(w => w.Stage >= PlanPublishStage.Fields).Select(w => w.Identity)
            .Concat(attempt.SelectMany(w => RowReferences(JsonNode.Parse(w.Input))))
            .Concat(attempt.Where(w => w.Stage == PlanPublishStage.Create && w.ResultId is not null).Select(w => w.ResultId!)));
    }
    private static ImmutableArray<string> Readable(IEnumerable<string> identities)
        => identities.Where(id => !id.StartsWith("local:", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToImmutableArray();
    // The input fields whose value is a plan row or Issue identity, and never a user, label or option id.
    private static bool IsRowReference(string key) => key is "id" or "issueId" or "subIssueId" or "blockingIssueId"
        or "contentId" or "itemId" or "afterId" or "beforeId" or "labelableId" or "assignableId";
    private static IEnumerable<string> RowReferences(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                    if (IsRowReference(pair.Key))
                    {
                        if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
                            yield return text.StartsWith("item:", StringComparison.Ordinal) ? text[5..] : text;
                    }
                    else foreach (var found in RowReferences(pair.Value)) yield return found;
                break;
            case JsonArray array:
                foreach (var item in array) foreach (var found in RowReferences(item)) yield return found;
                break;
        }
    }
    private static bool Uncovered(PlanRemoteSnapshot remote, PlanWrite write)
        => write.Stage >= PlanPublishStage.Fields && (Uncovered(remote, write.Identity)
            || RowReferences(JsonNode.Parse(write.Input)!).Any(id => Uncovered(remote, id)));
    private static bool Uncovered(PlanRemoteSnapshot remote, string identity)
        => !identity.StartsWith("local:", StringComparison.Ordinal) && !remote.Items.ContainsKey(identity);
    private async Task<PlanRemoteSnapshot> ReadItems(PlanSession session, GhConnectionService.OperationLease lease, CancellationToken token,
        ImmutableArray<string> scope, PublishObservation? observation = null)
    {
        var document = session.Document;
        if (document.Project.Scope != ConnectionScope.From(context)) throw new InvalidOperationException("プロジェクトの接続先が一致しません。");
        var seeded = observation?.Metadata;
        var read = await PlanSnapshot.ReadItemsConsistentAsync(service, lease, context, document.Project, scope, token, seeded).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var carried = seeded?.Issues.Values.Select(i => i.Repository.NameWithOwner).ToHashSet(StringComparer.Ordinal);
        // #308: only a row whose closed state is being published needs the read-back's history — it carries the
        // close or reopen event that confirms 完了日 (R7). A field write never changes the history, so an
        // ordinary publish adds no history request.
        var baseline = document.Baseline.Rows.ToDictionary(r => r.Identity);
        var state = document.State.Rows.ToDictionary(r => r.Identity);
        var stateChanges = scope.Where(id => !id.StartsWith("local:", StringComparison.Ordinal) && state.TryGetValue(id, out var row) &&
            baseline.TryGetValue(id, out var before) && row.Closed != before.Closed).ToImmutableArray();
        var history = await PlanSnapshot.ReadHistoriesAsync(service, lease, context, document.Project, stateChanges, token).ConfigureAwait(false);
        var snapshot = PlanSnapshot.FromItems(read, document, scope, carried: carried, history: history);
        if (observation is not null && observation.Metadata is null) observation.Metadata = read.Project;
        return snapshot;
    }
    private async Task<PlanRemoteSnapshot> ReadMemberships(PlanSession session, GhConnectionService.OperationLease lease, DateOnly today, CancellationToken token,
        PublishObservation? observation = null)
    {
        var created = session.Document.Sync.Publish?.Writes.Where(w => w.Stage == PlanPublishStage.Create && w.ResultId is not null).Select(w => w.ResultId!).ToArray() ?? [];
        for (var attempt = 0; ; attempt++)
        {
            var remote = await ReadItems(session, lease, token, PublicationScope(session.Document, today), observation).ConfigureAwait(false);
            if (attempt == 3 || created.All(remote.Items.ContainsKey)) return remote;
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
    }
    private static void RequireSave(PlanSaveResult save) { if (!save.Succeeded) throw new IOException(save.Error); }
    private static string Failure(ApiResult response, string alias)
    {
        if (response.Data is { } body && body.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray())
            {
                if (error.ValueKind != JsonValueKind.Object) continue;
                if (error.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.Array && path.GetArrayLength() > 0 && path[0].ValueKind == JsonValueKind.String && path[0].GetString() != alias) continue;
                if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String && message.GetString() == "Content already exists in this project")
                    return "AlreadyPresent: Content already exists in this project";
            }
        }
        return response.FailureReason(alias);
    }
    internal static ImmutableArray<PlanWrite> ReadBatch(ImmutableArray<PlanWrite> batch, ApiResult response)
    {
        JsonElement data = default;
        if (response.Data is { } body) body.TryGetProperty("data", out data);
        return batch.Select((write, i) =>
        {
            var alias = "w" + i;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(alias, out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                string? id = null;
                foreach (var property in payload.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("id", out var node)) id = node.GetString();
                if (write.Stage is not (PlanPublishStage.Create or PlanPublishStage.Add) || !string.IsNullOrWhiteSpace(id))
                    return write with { State = PlanWriteState.Succeeded, ResultId = id, Error = null };
            }
            var rejected = data.ValueKind == JsonValueKind.Object && data.TryGetProperty(alias, out var failed) && failed.ValueKind == JsonValueKind.Null;
            return write with { State = rejected ? PlanWriteState.Failed : PlanWriteState.Dispatched, Error = Failure(response, alias) };
        }).ToImmutableArray();
    }
    private static JsonNode ResolveInput(PlanWrite write, PlanPublishProgress progress, PlanRemoteSnapshot remote, IReadOnlyDictionary<string, string> repositories)
    {
        var ids = progress.Writes.Where(w => w.Stage == PlanPublishStage.Create && w.ResultId is not null).ToDictionary(w => w.Identity, w => w.ResultId!);
        var items = remote.Items.ToDictionary();
        foreach (var add in progress.Writes.Where(w => w.Stage == PlanPublishStage.Add && w.ResultId is not null)) items[add.Identity] = add.ResultId!;
        JsonNode? Resolve(JsonNode? node, bool identifier = false)
        {
            if (node is JsonObject obj) { foreach (var key in obj.Select(p => p.Key).ToArray()) obj[key] = Resolve(obj[key]?.DeepClone(), PlanPublishPlan.IsIdentifierField(key)); return obj; }
            if (node is JsonArray array) return new JsonArray(array.Select(n => Resolve(n?.DeepClone(), identifier)).ToArray());
            if (identifier && node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                if (text.StartsWith("repository:", StringComparison.Ordinal)) return JsonValue.Create(repositories.GetValueOrDefault(text[11..]) ?? throw new InvalidOperationException("リポジトリを確認できません。"));
                if (text.StartsWith("item:", StringComparison.Ordinal)) return JsonValue.Create(items.GetValueOrDefault(text[5..]) ?? throw new InvalidOperationException("プロジェクトへの追加が完了していません。"));
                if (text.StartsWith("local:", StringComparison.Ordinal)) return JsonValue.Create(ids.GetValueOrDefault(text) ?? throw new InvalidOperationException("Issue の作成が完了していません。"));
            }
            return node;
        }
        return Resolve(JsonNode.Parse(write.Input))!;
    }
    private static string CreationRepository(PlanWrite write)
    {
        var value = JsonNode.Parse(write.Input)!["repositoryId"]!.GetValue<string>();
        if (!value.StartsWith("repository:", StringComparison.Ordinal) || !PlanOperations.Repository(value[11..])) throw new InvalidOperationException("発行先リポジトリが不正です。");
        return value[11..];
    }
    private async Task<Dictionary<string, string>> Repositories(IEnumerable<PlanWrite> writes, GhConnectionService.OperationLease lease, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in writes.Where(w => w.Stage == PlanPublishStage.Create).Select(CreationRepository).Distinct(StringComparer.OrdinalIgnoreCase))
            result.Add(name, await Repository(name, lease, token).ConfigureAwait(false));
        return result;
    }
    private async Task<string> Repository(string repository, GhConnectionService.OperationLease lease, CancellationToken token)
    {
        var parts = repository.Split('/');
        var response = await lease.SendAsync(ApiRequest.GraphQl("query PlanPublishRepository($owner:String!,$name:String!){repository(owner:$owner,name:$name){id nameWithOwner hasIssuesEnabled isArchived viewerCanCreateIssues}}", new { owner = parts[0], name = parts[1] }), token);
        token.ThrowIfCancellationRequested();
        if (!response.IsSuccess || response.Data is not { } body) throw new InvalidOperationException("リポジトリを取得できません。");
        var repo = body.GetProperty("data").GetProperty("repository");
        if (repo.ValueKind != JsonValueKind.Object || !repo.GetProperty("hasIssuesEnabled").GetBoolean() || repo.GetProperty("isArchived").GetBoolean() || !repo.GetProperty("viewerCanCreateIssues").GetBoolean() ||
            !string.Equals(repo.GetProperty("nameWithOwner").GetString(), repository, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Issue を作成できません。");
        return repo.GetProperty("id").GetString() ?? throw new InvalidOperationException("リポジトリの識別子がありません。");
    }
    private static async Task<string?> FindCreation(PlanWrite write, string repositoryId, GhConnectionService.OperationLease lease, CancellationToken token)
    {
        var marker = JsonNode.Parse(write.Input)!["body"]!.GetValue<string>();
        string? after = null; var cursors = new HashSet<string>(); var found = new List<string>();
        do
        {
            var response = await lease.SendAsync(ApiRequest.GraphQl("query PlanCreationGuard($id:ID!,$after:String){node(id:$id){... on Repository{issues(first:100,after:$after){nodes{id body} pageInfo{hasNextPage endCursor}}}}}", new { id = repositoryId, after }), token);
            token.ThrowIfCancellationRequested();
            if (!response.IsSuccess || response.Data is not { } body) throw new InvalidOperationException("作成結果を確認できません。再作成は行いません。");
            var connection = body.GetProperty("data").GetProperty("node").GetProperty("issues");
            found.AddRange(connection.GetProperty("nodes").EnumerateArray().Where(n => n.GetProperty("body").GetString()?.Contains(marker, StringComparison.Ordinal) == true).Select(n => n.GetProperty("id").GetString()!));
            var page = connection.GetProperty("pageInfo");
            if (!page.GetProperty("hasNextPage").GetBoolean()) break;
            after = page.GetProperty("endCursor").GetString();
            if (string.IsNullOrWhiteSpace(after) || !cursors.Add(after)) throw new InvalidOperationException("作成結果のページ取得が不完全です。");
        } while (true);
        if (found.Count > 1) throw new InvalidOperationException("同じ作成マーカーが複数あります。確認が必要です。");
        return found.SingleOrDefault();
    }
    private static async Task<string?> FindProjectItem(string issue, string project, GhConnectionService.OperationLease lease, CancellationToken token)
    {
        string? after = null; var cursors = new HashSet<string>(); var found = new List<string>();
        do
        {
            var response = await lease.SendAsync(ApiRequest.GraphQl("query PlanExistingItem($id:ID!,$after:String){node(id:$id){... on Issue{projectItems(first:100,after:$after){nodes{id project{id}} pageInfo{hasNextPage endCursor}}}}}", new { id = issue, after }), token);
            if (!response.IsSuccess || response.Data is not { } body) throw new InvalidOperationException("既存のプロジェクト項目を確認できません。");
            var connection = body.GetProperty("data").GetProperty("node").GetProperty("projectItems");
            found.AddRange(connection.GetProperty("nodes").EnumerateArray().Where(n => n.GetProperty("project").GetProperty("id").GetString() == project).Select(n => n.GetProperty("id").GetString()!));
            var page = connection.GetProperty("pageInfo"); if (!page.GetProperty("hasNextPage").GetBoolean()) break;
            after = page.GetProperty("endCursor").GetString();
            if (string.IsNullOrWhiteSpace(after) || !cursors.Add(after)) throw new InvalidOperationException("プロジェクト項目のページ取得が不完全です。");
        } while (true);
        return found.Count == 1 ? found[0] : null;
    }
    private async Task ReconcileCreations(PlanSession session, GhConnectionService.OperationLease lease, CancellationToken token)
    {
        var progress = session.Document.Sync.Publish;
        if (progress is null) return;
        PlanPublishPlan.Validate(progress, session.Document);
        var uncertain = progress.Writes.Where(w => w.Stage == PlanPublishStage.Create && w.State == PlanWriteState.Dispatched).ToArray();
        if (uncertain.Length == 0) return;
        var repositories = await Repositories(uncertain, lease, token).ConfigureAwait(false);
        foreach (var write in uncertain)
        {
            var id = await FindCreation(write, repositories[CreationRepository(write)], lease, token).ConfigureAwait(false);
            progress = progress with { Writes = progress.Writes.Select(w => w.Key == write.Key ? w with
                { State = id is null ? PlanWriteState.Pending : PlanWriteState.Succeeded, ResultId = id, Error = null } : w).ToImmutableArray() };
            RequireSave(await session.SaveSync(session.Document.Sync with { Publish = progress }).ConfigureAwait(false));
        }
    }
    private async Task<PlanRemoteSnapshot> Reconcile(PlanSession session, GhConnectionService.OperationLease lease, DateOnly today, CancellationToken token, IProgress<RemoteProgress>? progressReporter = null, bool refresh = false, bool reset = false, bool publish = false, PublishObservation? observation = null)
    {
        await ReconcileCreations(session, lease, token).ConfigureAwait(false);
        var remote = publish
            ? await ReadItems(session, lease, token, PublicationScope(session.Document, today), observation).ConfigureAwait(false)
            : await Read(session, lease, token, progressReporter).ConfigureAwait(false);
        progressReporter?.Report(new("日程を計算"));
        token.ThrowIfCancellationRequested();
        RequireSave(await (session.Document.Sync.Publish is null ? session.AcceptRefresh(remote, today, refresh: refresh, reset: reset) : session.AcceptPublished(remote, today, refresh)).ConfigureAwait(false));
        return remote;
    }
    private static async Task<PlanSaveResult?> RetainInterruptedAttempt(PlanSession session)
    {
        var sync = session.Document.Sync;
        if (sync.Publish is not { } progress) return null;
        if (!progress.DispatchStarted && progress.Writes.All(w => w.State == PlanWriteState.Pending))
        {
            return await session.SaveSync(sync with { Publish = null, NotBefore = null, Unverified = [] }).ConfigureAwait(false);
        }
        var unverified = progress.Writes.Where(w => w.State is PlanWriteState.Dispatched or PlanWriteState.Succeeded).Select(w => w.Identity).Distinct().ToImmutableArray();
        return await session.SaveSync(sync with { Unverified = unverified,
            Failures = sync.Failures.Where(f => !unverified.Contains(f.Identity)).ToImmutableArray() }).ConfigureAwait(false);
    }
    private static PlanPublishResult Result(PlanSession session, DateOnly today, PlanPublishReview? review)
    {
        var sync = session.Document.Sync;
        // A row this publish cannot create yet (an untitled new row and its would-be children, #247) is
        // unpublished by design, and so is a remaining change that only exists because it names one (a
        // published requirement's child order, or a 先行 link to the row). Neither makes an otherwise
        // complete publish incomplete; every genuine condition below still does.
        var held = PlanOperations.HeldRows(session.Document);
        var complete = sync.Publish is null &&
            session.Changes(today).Fields.All(change => held.Contains(change.Key) ||
                change.Value.All(field => PlanOperations.HoldsRemainingChange(session.Document, change.Key, field, held))) &&
            sync.Unavailable.IsEmpty && PlanOperations.BlockingConflicts(session.Document).IsEmpty &&
            sync.Failures.IsEmpty && sync.Unverified.IsEmpty;
        return new(complete, complete ? null : "未完了の変更があります。確認後に再発行してください。", review);
    }
    public async Task<PlanPublishResult> PublishAsync(PlanSession session, DateOnly today, CancellationToken token = default, IProgress<RemoteProgress>? progressReporter = null)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        PlanPublishReview? review = null;
        try
        {
            using var operation = session.BeginRemoteOperation();
            RequireSave(await session.FlushAsync().ConfigureAwait(false));
            while (session.Document.Sync.NotBefore is { } deadline && deadline > DateTimeOffset.UtcNow)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds)), token).ConfigureAwait(false);
            using var lease = await service.BeginOperationAsync(context, token).ConfigureAwait(false);
            CreationWait = TimeSpan.Zero; CreationPhaseStarted = null; EffectiveBatchSizes.Clear();
            progressReporter?.Report(new("最新情報の確認"));
            var observation = new PublishObservation();
            var remote = await Reconcile(session, lease, today, token, publish: true, observation: observation).ConfigureAwait(false);
            var serialFields = false; var stateFollowed = false;
            // Creation outcomes are adopted before the remaining writes are planned from current local inputs.
            while (true)
            {
                if (!PlanOperations.BlockingConflicts(session.Document).IsEmpty || !session.Document.Sync.Unavailable.IsEmpty)
                    return new(false, "競合を解決してください。", review);
                var progress = session.Document.Sync.Publish;
                IReadOnlyDictionary<string, string> repositories = new Dictionary<string, string>();
                if (progress is null)
                {
                    var run = Guid.NewGuid().ToString("N");
                    var planned = PlanPublishPlan.Build(session.Document, remote, today, run);
                    review ??= planned;
                    var creates = planned.Writes.Any(w => w.Stage == PlanPublishStage.Create);
                    if (creates) repositories = await Repositories(planned.Writes, lease, token).ConfigureAwait(false);
                    var writes = creates ? planned.Writes.Where(w => w.Stage is PlanPublishStage.Create or PlanPublishStage.Add).ToImmutableArray() : planned.Writes;
                    if (writes.IsEmpty)
                    {
                        RequireSave(await session.SaveSync(session.Document.Sync with { Failures = [], Unverified = [], NotBefore = null }).ConfigureAwait(false));
                        return await CompletePublication(session, today, review).ConfigureAwait(false);
                    }
                    progress = new(run, writes);
                    RequireSave(await session.SaveSync(session.Document.Sync with { Publish = progress }).ConfigureAwait(false));
                }
                else if (progress.Writes.Any(w => w.Stage == PlanPublishStage.Create && w.ResultId is null))
                    repositories = await Repositories(progress.Writes, lease, token).ConfigureAwait(false);
                // #201: the plan is built from the document, so this is the first point that knows which
                // rows this attempt writes and names. Read any the snapshot does not cover, so a position
                // anchor or a promoted row is resolved from the row itself instead of a whole-Project read.
                if (progress.Writes.Any(w => Uncovered(remote, w)))
                    remote = await ReadItems(session, lease, token, WriteScope(progress.Writes)
                        .AddRange(PublicationScope(session.Document, today)), observation).ConfigureAwait(false);
                var creating = progress.Writes.Any(w => w.Stage == PlanPublishStage.Create);
                // #231: a close or reopen is followed in the same publish by the dates and remaining work that
                // the close day GitHub reports implies, so the PMO publishes a 完了にする once.
                var changingState = !stateFollowed && progress.Writes.Any(w => w.Mutation is "closeIssue" or "reopenIssue");
                static string StageName(PlanPublishStage stage) => stage switch {
                    PlanPublishStage.Create or PlanPublishStage.Add => "新規 Issue",
                    PlanPublishStage.Fields => "フィールド・担当者",
                    PlanPublishStage.State => "完了・中止・この版から外す",
                    PlanPublishStage.Hierarchy => "親子関係",
                    PlanPublishStage.Dependencies => "先行タスク",
                    _ => "表示順" };
                void ReportWrites(PlanPublishStage stage)
                {
                    if (progressReporter is null) return;
                    var name = StageName(stage);
                    var writes = progress.Writes.Where(w => StageName(w.Stage) == name).ToArray();
                    progressReporter?.Report(new(name, writes.Count(w => w.State == PlanWriteState.Succeeded), writes.Length));
                }
                async Task Save(IEnumerable<PlanWrite> updates)
                {
                    var byKey = updates.ToDictionary(w => w.Key);
                    progress = progress with { Writes = progress.Writes.Select(w => byKey.GetValueOrDefault(w.Key) ?? w).ToImmutableArray() };
                    RequireSave(await session.SaveSync(session.Document.Sync with { Publish = progress }).ConfigureAwait(false));
                    foreach (var stage in byKey.Values.Select(w => w.Stage).Distinct()) ReportWrites(stage);
                }
                var reducedLimits = new Dictionary<PlanPublishStage, int>();
                async Task<ApiRequest> PrepareBatch(ImmutableArray<PlanWrite> group)
                {
                    token.ThrowIfCancellationRequested();
                    ReportWrites(group[0].Stage);
                    var variables = new Dictionary<string, JsonNode>();
                    var declarations = new List<string>(); var selections = new List<string>();
                    for (var i = 0; i < group.Length; i++)
                    {
                        variables["v" + i] = ResolveInput(group[i], progress, remote, repositories);
                        declarations.Add("$v" + i + ":" + group[i].InputType + "!");
                        selections.Add("w" + i + ":" + group[i].Mutation + "(input:$v" + i + "){" + group[i].Selection + "}");
                    }
                    if (group[0].Stage == PlanPublishStage.Create)
                    {
                        CreationPhaseStarted ??= DateTimeOffset.UtcNow;
                        var next = PlanCreationPacing.NextStart(session.Document.Sync.CreationStarts, group.Length, DateTimeOffset.UtcNow);
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        while (next - DateTimeOffset.UtcNow is var pause && pause > TimeSpan.Zero)
                            await Task.Delay(pause, token).ConfigureAwait(false);
                        CreationWait += watch.Elapsed;
                        RequireSave(await session.SaveSync(session.Document.Sync with
                        { CreationStarts = PlanCreationPacing.Reserve(session.Document.Sync.CreationStarts, group.Length, DateTimeOffset.UtcNow) }).ConfigureAwait(false));
                    }
                    return ApiRequest.GraphQl("mutation PlanPublish(" + string.Join(',', declarations) + "){" + string.Join(' ', selections) + "}", variables);
                }
                static (bool Gateway, bool Resource) Recovery(ImmutableArray<PlanWrite> group, ApiResult response)
                {
                    // A missing outcome is not an alias rejection; later writes would widen the uncertainty.
                    // Field writes set absolute values or membership, so a gateway failure can use the same bounded split recovery.
                    var fieldGatewayRecovery = group.Length > 1 && group[0].Stage == PlanPublishStage.Fields &&
                        response.HttpStatus is >= 500 and <= 599 &&
                        !(response.Data is { ValueKind: JsonValueKind.Object } gatewayBody && gatewayBody.TryGetProperty("data", out var gatewayData) &&
                            gatewayData.ValueKind == JsonValueKind.Object);
                    var resourceRecovery = group.Length > 1 && response.Data is { ValueKind: JsonValueKind.Object } responseBody &&
                        responseBody.TryGetProperty("errors", out var responseErrors) && responseErrors.ValueKind == JsonValueKind.Array &&
                        responseErrors.GetArrayLength() > 0 && responseErrors.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object &&
                            e.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "RESOURCE_LIMITS_EXCEEDED");
                    return (fieldGatewayRecovery, resourceRecovery);
                }
                async Task SendBatch(ImmutableArray<PlanWrite> group)
                {
                    var request = await PrepareBatch(group).ConfigureAwait(false);
                    await Save(group.Select(w => w with { State = PlanWriteState.Dispatched, Error = null })).ConfigureAwait(false);
                    EffectiveBatchSizes.Add(group.Length);
                    var response = await lease.SendAsync(request, token).ConfigureAwait(false);
                    var results = ReadBatch(group, response);
                    await Save(results).ConfigureAwait(false);
                    var notBefore = session.Document.Sync.NotBefore;
                    if (response.RetryAfter is { } delay && delay > TimeSpan.Zero)
                    {
                        var deadline = DateTimeOffset.UtcNow + delay;
                        if (notBefore is null || deadline > notBefore) notBefore = deadline;
                    }
                    var recovery = Recovery(group, response);
                    if (group[0].Stage == PlanPublishStage.Fields &&
                        (response.RetryAfter > TimeSpan.Zero || response.Failure == FailureKind.RateLimited || recovery.Gateway ||
                         results.Any(w => w.State != PlanWriteState.Succeeded &&
                             w.Error?.StartsWith("RESOURCE_LIMITS_EXCEEDED:", StringComparison.Ordinal) == true)))
                        serialFields = true;
                    if (notBefore != session.Document.Sync.NotBefore)
                        RequireSave(await session.SaveSync(session.Document.Sync with { NotBefore = notBefore }).ConfigureAwait(false));
                    if (!recovery.Gateway && results.Any(w => w.State == PlanWriteState.Dispatched &&
                        !(recovery.Resource && w.Error?.StartsWith("RESOURCE_LIMITS_EXCEEDED:", StringComparison.Ordinal) == true)))
                    {
                        // #246: a response that never arrived is a lost connection; an answered 5xx
                        // keeps the unconfirmed-response message.
                        if (ConnectionLostException.IsTransportLoss(response.Failure, response.HttpStatus)) throw new ConnectionLostException();
                        throw new InvalidOperationException("GitHubの応答を確認できないため発行を中断しました。最新の情報に更新で確認してから再発行してください。");
                    }
                    if (notBefore is { } until && until - DateTimeOffset.UtcNow is var wait && wait > TimeSpan.Zero)
                        await Task.Delay(wait, token).ConfigureAwait(false);
                    await HandleResponse(group, response, results).ConfigureAwait(false);
                }
                async Task HandleResponse(ImmutableArray<PlanWrite> group, ApiResult response, ImmutableArray<PlanWrite> results)
                {
                    var fieldGatewayRecovery = Recovery(group, response).Gateway;
                    foreach (var already in results.Where(w => w.Stage == PlanPublishStage.Add && w.Error == "AlreadyPresent: Content already exists in this project"))
                    {
                        var issue = progress.Writes.Single(w => w.Identity == already.Identity && w.Stage == PlanPublishStage.Create).ResultId!;
                        var item = await FindProjectItem(issue, session.Document.Project.NodeId, lease, token).ConfigureAwait(false);
                        if (item is not null) await Save([already with { State = PlanWriteState.Succeeded, ResultId = item, Error = null }]);
                    }
                    var uncertainLinks = results.Where(w => w.State != PlanWriteState.Succeeded &&
                        w.Mutation is "addSubIssue" or "removeSubIssue" or "addBlockedBy" or "removeBlockedBy" &&
                        w.Error?.StartsWith("RESOURCE_LIMITS_EXCEEDED:", StringComparison.Ordinal) != true).ToArray();
                    if (uncertainLinks.Length > 0)
                    {
                        var observed = await ReadItems(session, lease, token, PublicationScope(session.Document, today), observation).ConfigureAwait(false);
                        await Save(uncertainLinks.Where(w => PlanVerification.Verify(w, progress, observed, session.Document.State.Settings))
                            .Select(w => w with { State = PlanWriteState.Succeeded, Error = null }));
                    }
                    var limited = results.Where(w => w.State != PlanWriteState.Succeeded &&
                        (fieldGatewayRecovery || w.Error?.StartsWith("RESOURCE_LIMITS_EXCEEDED:", StringComparison.Ordinal) == true)).ToArray();
                    if (group.Length > 1 && limited.Length > 0)
                    {
                        var reduced = Math.Max(1, group.Length / 2);
                        reducedLimits[group[0].Stage] = Math.Min(reducedLimits.GetValueOrDefault(group[0].Stage, int.MaxValue), reduced);
                        // Resolve uncertain creation by its marker before retrying even a resource-limit response.
                        if (group[0].Stage == PlanPublishStage.Create)
                        {
                            await ReconcileCreations(session, lease, token).ConfigureAwait(false);
                            progress = session.Document.Sync.Publish!;
                            limited = limited.Select(w => progress.Writes.Single(p => p.Key == w.Key))
                                .Where(w => w.State is PlanWriteState.Pending or PlanWriteState.Failed).ToArray();
                        }
                        foreach (var part in limited.Chunk(Math.Max(1, group.Length / 2)))
                            await SendBatch(part.ToImmutableArray()).ConfigureAwait(false);
                    }
                }
                // #298: the field phase keeps four requests in flight for the whole phase. A completion saves its
                // response, frees its slot and is replaced by the next ready request while its peers are still
                // running, instead of a window waiting for its slowest response. A pushback, a split recovery or
                // an unresolved outcome stops dispatch and drains the requests already in flight before acting on
                // any of their outcomes.
                var fieldInFlight = new List<(ImmutableArray<PlanWrite> Group, Task<ApiResult> Response)>();
                var fieldRecovery = new List<(ImmutableArray<PlanWrite> Group, ApiResult Response, ImmutableArray<PlanWrite> Results)>();
                var fieldStopped = false; var fieldLost = false; var fieldUncertain = false;
                var fieldNotBefore = session.Document.Sync.NotBefore;
                async Task DispatchFieldRequest(ImmutableArray<PlanWrite> group)
                {
                    var request = await PrepareBatch(group).ConfigureAwait(false);
                    await Save(group.Select(w => w with { State = PlanWriteState.Dispatched, Error = null })).ConfigureAwait(false);
                    EffectiveBatchSizes.Add(group.Length);
                    fieldInFlight.Add((group, lease.SendAsync(request, token)));
                }
                async Task<ApiResult> AwaitFieldResponse(Task<ApiResult> response)
                {
                    try { return await response.ConfigureAwait(false); }
                    catch
                    {
                        // A cancelled send still owns its peers: observe them before the attempt unwinds.
                        foreach (var pending in fieldInFlight.Where(r => r.Response != response))
                            try { await pending.Response.ConfigureAwait(false); } catch { /* the interruption is reported once */ }
                        fieldInFlight.Clear();
                        throw;
                    }
                }
                async Task ObserveFieldResponse()
                {
                    var done = await Task.WhenAny(fieldInFlight.Select(r => r.Response)).ConfigureAwait(false);
                    var index = fieldInFlight.FindIndex(r => r.Response == done);
                    var (group, response) = fieldInFlight[index];
                    fieldInFlight.RemoveAt(index);
                    var reply = await AwaitFieldResponse(response).ConfigureAwait(false);
                    var results = ReadBatch(group, reply);
                    await Save(results).ConfigureAwait(false);
                    var recovery = Recovery(group, reply);
                    // Every later field request of this attempt is serial after any pushback or split recovery.
                    if (reply.RetryAfter > TimeSpan.Zero || reply.Failure == FailureKind.RateLimited || recovery.Gateway ||
                        results.Any(w => w.State != PlanWriteState.Succeeded &&
                            w.Error?.StartsWith("RESOURCE_LIMITS_EXCEEDED:", StringComparison.Ordinal) == true))
                        fieldStopped = true;
                    if (reply.RetryAfter is { } delay && delay > TimeSpan.Zero &&
                        (fieldNotBefore is null || DateTimeOffset.UtcNow + delay > fieldNotBefore))
                        fieldNotBefore = DateTimeOffset.UtcNow + delay;
                    // An unresolved outcome is never acted on until every in-flight response is recorded. A
                    // gateway failure keeps its split recovery instead of stopping the phase.
                    if (!recovery.Gateway && results.Any(w => w.State == PlanWriteState.Dispatched && !(recovery.Resource &&
                        w.Error?.StartsWith("RESOURCE_LIMITS_EXCEEDED:", StringComparison.Ordinal) == true)))
                    {
                        fieldStopped = true;
                        if (ConnectionLostException.IsTransportLoss(reply.Failure, reply.HttpStatus)) fieldLost = true;
                        else fieldUncertain = true;
                    }
                    if (group.Length > 1 && results.Any(w => w.State != PlanWriteState.Succeeded &&
                        (recovery.Gateway || w.Error?.StartsWith("RESOURCE_LIMITS_EXCEEDED:", StringComparison.Ordinal) == true)))
                        fieldRecovery.Add((group, reply, results));
                }
                async Task FinishFieldRequests()
                {
                    while (fieldInFlight.Count > 0) await ObserveFieldResponse().ConfigureAwait(false);
                    var persisted = session.Document.Sync.NotBefore;
                    var deadline = fieldNotBefore;
                    if (persisted is { } saved && (deadline is null || saved > deadline)) deadline = persisted;
                    if (deadline != persisted)
                        RequireSave(await session.SaveSync(session.Document.Sync with { NotBefore = deadline }).ConfigureAwait(false));
                    if (fieldLost) throw new ConnectionLostException();
                    if (fieldUncertain)
                        throw new InvalidOperationException("GitHubの応答を確認できないため発行を中断しました。最新の情報に更新で確認してから再発行してください。");
                    if (session.Document.Sync.NotBefore is { } until && until - DateTimeOffset.UtcNow is var wait && wait > TimeSpan.Zero)
                        await Task.Delay(wait, token).ConfigureAwait(false);
                    foreach (var (group, response, results) in fieldRecovery)
                        await HandleResponse(group, response, results).ConfigureAwait(false);
                    fieldRecovery.Clear();
                }
                async Task StartFieldRequest(ImmutableArray<PlanWrite> group)
                {
                    await DispatchFieldRequest(group).ConfigureAwait(false);
                    while (fieldInFlight.Count >= FieldConcurrencyLimit) await ObserveFieldResponse().ConfigureAwait(false);
                }
                foreach (var batch in PlanPublishPlan.Batches(progress.Writes.Where(w => w.State != PlanWriteState.Succeeded)))
                {
                    if (batch[0].Stage != PlanPublishStage.Fields)
                        await FinishFieldRequests().ConfigureAwait(false);
                    var ready = ImmutableArray.CreateBuilder<PlanWrite>();
                    foreach (var write in batch)
                    {
                        var earlier = progress.Writes.TakeWhile(w => w.Key != write.Key);
                        var input = JsonNode.Parse(write.Input)!;
                        var blocked = write.Stage >= PlanPublishStage.Fields && !remote.Items.ContainsKey(write.Identity);
                        blocked |= earlier.Any(w => w.State != PlanWriteState.Succeeded &&
                            (write.Stage == PlanPublishStage.Add && w.Identity == write.Identity && w.Stage == PlanPublishStage.Create ||
                             write.Mutation == "addSubIssue" && w.Mutation == "removeSubIssue" && (w.Identity == write.Identity || w.Identity == input["issueId"]?.GetValue<string>()) ||
                             write.Mutation == "addBlockedBy" && w.Mutation == "removeBlockedBy" && (w.Identity == write.Identity || w.Identity == input["blockingIssueId"]?.GetValue<string>()) ||
                             write.Mutation == "reprioritizeSubIssue" && w.Mutation == "addSubIssue" &&
                                (w.Identity == write.Identity || w.Identity == input["afterId"]?.GetValue<string>() || w.Identity == input["beforeId"]?.GetValue<string>()) ||
                             write.Stage == PlanPublishStage.Order && w.Stage == PlanPublishStage.Order && input["afterId"]?.GetValue<string>() == "item:" + w.Identity));
                        if (!blocked)
                            try { ResolveInput(write, progress, remote, repositories); }
                            catch (InvalidOperationException) { blocked = true; }
                        if (blocked) await Save([write with { State = PlanWriteState.Pending, Error = "NotDispatched" }]);
                        else ready.Add(write);
                    }
                    var remainingReady = ready.ToImmutable();
                    while (!remainingReady.IsEmpty)
                    {
                        if (batch[0].Stage == PlanPublishStage.Fields && !serialFields && !fieldStopped)
                        {
                            var size = Math.Min(remainingReady.Length, reducedLimits.GetValueOrDefault(batch[0].Stage, remainingReady.Length));
                            var group = remainingReady.Take(size).ToImmutableArray();
                            remainingReady = remainingReady.RemoveRange(0, size);
                            await StartFieldRequest(group).ConfigureAwait(false);
                        }
                        else
                        {
                            // A pushback or an unresolved outcome drains the field phase before a serial request;
                            // its split recovery lowers the ceiling this same batch uses for its remaining writes.
                            if (batch[0].Stage == PlanPublishStage.Fields) await FinishFieldRequests().ConfigureAwait(false);
                            var size = Math.Min(remainingReady.Length, reducedLimits.GetValueOrDefault(batch[0].Stage, remainingReady.Length));
                            var group = remainingReady.Take(size).ToImmutableArray();
                            remainingReady = remainingReady.RemoveRange(0, size);
                            await SendBatch(group).ConfigureAwait(false);
                        }
                    }
                    if (batch[0].Stage == PlanPublishStage.Create && progress.Writes.Any(w => batch.Any(b => b.Key == w.Key) && w.State != PlanWriteState.Succeeded)) break;
                }

                await FinishFieldRequests().ConfigureAwait(false);

                progressReporter?.Report(new("検証"));
                await ReconcileCreations(session, lease, token).ConfigureAwait(false);
                remote = await ReadMemberships(session, lease, today, token, observation).ConfigureAwait(false);
                RequireSave(await session.AcceptPublished(remote, today).ConfigureAwait(false));
                if (!creating && !changingState || session.Document.Sync.Publish is not null || session.Document.Sync.Failures.Any(f => f.Field == PlanField.NewTask)
                    || !creating && !session.Document.Sync.Failures.IsEmpty)
                    return await CompletePublication(session, today, review).ConfigureAwait(false);
                if (!creating) stateFollowed = true;
            }
        }
        catch (Exception ex) when (Expected(ex))
        {
            var saved = await RetainInterruptedAttempt(session).ConfigureAwait(false);
            var message = ex is ConnectionLostException
                ? InterruptedByConnection(session.Document.Sync.Publish)
                : (session.Document.Sync.Unverified.IsEmpty ? "" : "発行結果は GitHub で未確認です。「最新の情報に更新」で確認してください。 ") + ex.Message;
            return new(false, message + (saved is { Succeeded: false } ? " " + saved.Error : ""), review);
        }
        finally { gate.Release(); }
    }
    // #246: a lost connection is one message with the progress GitHub confirmed. A task counts only when
    // every write this attempt sent for it was acknowledged; a dispatched write whose response was lost
    // may or may not have been applied, so an unknown count is left out instead of guessed.
    private static string InterruptedByConnection(PlanPublishProgress? progress)
    {
        var written = 0; var total = 0;
        if (progress is not null)
        {
            var tasks = progress.Writes.GroupBy(w => w.Identity).ToArray();
            total = tasks.Length;
            written = tasks.Count(writes => writes.All(w => w.State == PlanWriteState.Succeeded));
        }
        var confirmed = written > 0 ? $"（{written} / {total} タスクを発行済み）" : "";
        return "GitHub に接続できなくなったため、発行を中断しました" + confirmed
            + "。接続が戻ったら「最新の情報に更新」で確認してから、もう一度発行してください。";
    }
}
