using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.PlanEditor;

internal sealed record PlanPublishResult(bool Succeeded, string? Error, PlanPublishReview? Review);
internal sealed class PlanPublisher(GhConnectionService service, ConnectionContext context)
{
    public List<int> EffectiveBatchSizes { get; } = [];
    public TimeSpan CreationWait { get; private set; }
    public DateTimeOffset? CreationPhaseStarted { get; private set; }
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<PlanPublishResult> RefreshAsync(PlanSession session, DateOnly today, CancellationToken token = default, IProgress<RemoteProgress>? progressReporter = null)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var operation = session.BeginRemoteOperation();
            using var lease = await service.BeginOperationAsync(context, token, mutation: false).ConfigureAwait(false);
            await Reconcile(session, lease, today, token, progressReporter).ConfigureAwait(false);
            return new(true, null, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (Expected(ex)) { var saved = await RetainInterruptedAttempt(session).ConfigureAwait(false); return new(false, (session.Document.Sync.Unverified.IsEmpty ? "" : "発行結果は未検証です。再取得して確認してください。 ") + ex.Message + (saved is { Succeeded: false } ? " " + saved.Error : ""), null); }
        finally { gate.Release(); }
    }
    public async Task<PlanPublishResult> AddSchedulingFieldsAsync(PlanSession session, DateOnly today, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var operation = session.BeginRemoteOperation();
            if (session.Document.Sync.Publish is not null) throw new InvalidOperationException("未完了の発行を先に確認してください。");
            RequireSave(await session.FlushAsync().ConfigureAwait(false));
            using var lease = await service.BeginOperationAsync(context, token).ConfigureAwait(false);
            var result = await new ProjectReader(service, lease).ReadAsync(context, session.Document.Project, token).ConfigureAwait(false);
            if (result.Outcome != ProjectReadOutcome.Complete || result.Project is null) throw new InvalidOperationException("列を取得できません。");
            var mappings = session.Document.State.Settings.Columns.ToList();
            foreach (var (role, name, type) in new[] { (PlanField.StartNoEarlierThan, "開始日指定", "DATE"), (PlanField.Fixed, "日程固定", "SINGLE_SELECT") })
            {
                var matches = result.Project.Fields.Where(f => f.Name == name).ToArray();
                if (matches.Length > 1 || matches.Any(f => f.DataType != type || f.ValueOwner != FieldOwner.ProjectItem))
                    throw new InvalidOperationException("同名の列の型または識別子を確認してください: " + name);
                string id;
                if (matches.Length == 1)
                {
                    if (role == PlanField.Fixed && matches[0].Options.Count(o => o.Name == "固定") != 1) throw new InvalidOperationException("固定の選択肢を確認してください。");
                    id = matches[0].Id.NodeId;
                }
                else
                {
                    var input = new Dictionary<string, object?> { ["projectId"] = session.Document.Project.NodeId, ["name"] = name, ["dataType"] = type };
                    if (type == "SINGLE_SELECT") input["singleSelectOptions"] = new[] { new { name = "固定", color = "GRAY", description = "" } };
                    var response = await lease.SendAsync(ApiRequest.GraphQl("mutation PlanAddField($input:CreateProjectV2FieldInput!){createProjectV2Field(input:$input){projectV2Field{... on ProjectV2FieldCommon{id name dataType}}}}", new { input }), token).ConfigureAwait(false);
                    if (!response.IsSuccess || response.Data is not { } body) throw new InvalidOperationException("列の追加結果を確認できません。最新の情報を取得してから再試行してください。");
                    var field = body.GetProperty("data").GetProperty("createProjectV2Field").GetProperty("projectV2Field");
                    if (field.GetProperty("name").GetString() != name || field.GetProperty("dataType").GetString() != type) throw new InvalidOperationException("追加した列が一致しません。");
                    id = field.GetProperty("id").GetString()!;
                }
                mappings.RemoveAll(m => m.Role == role || m.FieldId == id);
                mappings.Add(new(role, id, name, type));
            }
            RequireSave(await session.AcceptFieldSettings(session.Document.State.Settings with { Columns = mappings.ToImmutableArray() }, today).ConfigureAwait(false));
            return new(true, null, null);
        }
        catch (Exception ex) when (Expected(ex)) { return new(false, ex.Message, null); }
        finally { gate.Release(); }
    }
    private async Task<PlanRemoteSnapshot> Read(PlanSession session, GhConnectionService.OperationLease lease, CancellationToken token, IProgress<RemoteProgress>? progressReporter = null)
    {
        var document = session.Document;
        if (document.Project.Scope != ConnectionScope.From(context)) throw new InvalidOperationException("プロジェクトの接続先が一致しません。");
        var read = await PlanSnapshot.ReadConsistentAsync(service, lease, context, document.Project, token, progressReporter).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return PlanSnapshot.From(read, document.State.Settings);
    }
    private static bool Expected(Exception ex) => ex is InvalidOperationException or ArgumentException or IOException or OperationCanceledException or JsonException or KeyNotFoundException or FormatException or OverflowException;
    private async Task<PlanRemoteSnapshot> ReadMemberships(PlanSession session, GhConnectionService.OperationLease lease, CancellationToken token)
    {
        var created = session.Document.Sync.Publish?.Writes.Where(w => w.Stage == PlanPublishStage.Create && w.ResultId is not null).Select(w => w.ResultId!).ToArray() ?? [];
        for (var attempt = 0; ; attempt++)
        {
            var remote = await Read(session, lease, token).ConfigureAwait(false);
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
                if (text.StartsWith("item:", StringComparison.Ordinal)) return JsonValue.Create(items.GetValueOrDefault(text[5..]) ?? throw new InvalidOperationException("Project への追加が完了していません。"));
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
            if (!response.IsSuccess || response.Data is not { } body) throw new InvalidOperationException("既存のProject項目を確認できません。");
            var connection = body.GetProperty("data").GetProperty("node").GetProperty("projectItems");
            found.AddRange(connection.GetProperty("nodes").EnumerateArray().Where(n => n.GetProperty("project").GetProperty("id").GetString() == project).Select(n => n.GetProperty("id").GetString()!));
            var page = connection.GetProperty("pageInfo"); if (!page.GetProperty("hasNextPage").GetBoolean()) break;
            after = page.GetProperty("endCursor").GetString();
            if (string.IsNullOrWhiteSpace(after) || !cursors.Add(after)) throw new InvalidOperationException("Project項目のページ取得が不完全です。");
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
    private async Task<PlanRemoteSnapshot> Reconcile(PlanSession session, GhConnectionService.OperationLease lease, DateOnly today, CancellationToken token, IProgress<RemoteProgress>? progressReporter = null)
    {
        await ReconcileCreations(session, lease, token).ConfigureAwait(false);
        var remote = await Read(session, lease, token, progressReporter).ConfigureAwait(false);
        progressReporter?.Report(new("日程を計算"));
        token.ThrowIfCancellationRequested();
        RequireSave(await (session.Document.Sync.Publish is null ? session.AcceptRefresh(remote, today) : session.AcceptPublished(remote, today)).ConfigureAwait(false));
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
        var complete = sync.Publish is null && session.Changes(today).TaskCount == 0 && sync.Unavailable.IsEmpty &&
            sync.Conflicts.IsEmpty && sync.Failures.IsEmpty && sync.Unverified.IsEmpty;
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
            var remote = await Reconcile(session, lease, today, token).ConfigureAwait(false);
            // Creation outcomes are adopted before the remaining writes are planned from current local inputs.
            while (true)
            {
                if (!session.Document.Sync.Conflicts.IsEmpty || !session.Document.Sync.Unavailable.IsEmpty)
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
                        return Result(session, today, review);
                    }
                    progress = new(run, writes);
                    RequireSave(await session.SaveSync(session.Document.Sync with { Publish = progress }).ConfigureAwait(false));
                }
                else if (progress.Writes.Any(w => w.Stage == PlanPublishStage.Create && w.ResultId is null))
                    repositories = await Repositories(progress.Writes, lease, token).ConfigureAwait(false);
                var creating = progress.Writes.Any(w => w.Stage == PlanPublishStage.Create);
                static string StageName(PlanPublishStage stage) => stage switch {
                    PlanPublishStage.Create or PlanPublishStage.Add => "新規 Issue",
                    PlanPublishStage.Fields => "フィールド・担当者",
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
                async Task SendBatch(ImmutableArray<PlanWrite> group)
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
                    await Save(group.Select(w => w with { State = PlanWriteState.Dispatched, Error = null }));
                    var request = ApiRequest.GraphQl("mutation PlanPublish(" + string.Join(',', declarations) + "){" + string.Join(' ', selections) + "}", variables);
                    EffectiveBatchSizes.Add(group.Length);
                    var response = await lease.SendAsync(request, token).ConfigureAwait(false);
                    var results = ReadBatch(group, response);
                    await Save(results);
                    if (response.RetryAfter is { } delay && delay > TimeSpan.Zero)
                    {
                        RequireSave(await session.SaveSync(session.Document.Sync with { NotBefore = DateTimeOffset.UtcNow + delay }).ConfigureAwait(false));
                    }
                    // A missing outcome is not an alias rejection; later writes would widen the uncertainty.
                    // Explicit resource failures retain the bounded split/creation-guard recovery below.
                    var resourceRecovery = group.Length > 1 && response.Data is { ValueKind: JsonValueKind.Object } responseBody &&
                        responseBody.TryGetProperty("errors", out var responseErrors) && responseErrors.ValueKind == JsonValueKind.Array &&
                        responseErrors.GetArrayLength() > 0 && responseErrors.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object &&
                            e.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "RESOURCE_LIMITS_EXCEEDED");
                    if (results.Any(w => w.State == PlanWriteState.Dispatched &&
                        !(resourceRecovery && w.Error?.StartsWith("RESOURCE_LIMITS_EXCEEDED:", StringComparison.Ordinal) == true)))
                        throw new InvalidOperationException("GitHubの応答を確認できないため発行を中断しました。最新の情報に更新で確認してから再発行してください。");
                    if (response.RetryAfter is { } wait && wait > TimeSpan.Zero)
                        await Task.Delay(wait, token).ConfigureAwait(false);
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
                        var observed = await Read(session, lease, token).ConfigureAwait(false);
                        await Save(uncertainLinks.Where(w => PlanVerification.Verify(w, progress, observed, session.Document.State.Settings))
                            .Select(w => w with { State = PlanWriteState.Succeeded, Error = null }));
                    }
                    var limited = results.Where(w => w.State != PlanWriteState.Succeeded &&
                        w.Error?.StartsWith("RESOURCE_LIMITS_EXCEEDED:", StringComparison.Ordinal) == true).ToArray();
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
                foreach (var batch in PlanPublishPlan.Batches(progress.Writes.Where(w => w.State != PlanWriteState.Succeeded)))
                {
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
                        var size = Math.Min(remainingReady.Length, reducedLimits.GetValueOrDefault(batch[0].Stage, remainingReady.Length));
                        await SendBatch(remainingReady.Take(size).ToImmutableArray()).ConfigureAwait(false);
                        remainingReady = remainingReady.RemoveRange(0, size);
                    }
                    if (batch[0].Stage == PlanPublishStage.Create && progress.Writes.Any(w => batch.Any(b => b.Key == w.Key) && w.State != PlanWriteState.Succeeded)) break;
                }

                progressReporter?.Report(new("検証"));
                await ReconcileCreations(session, lease, token).ConfigureAwait(false);
                remote = await ReadMemberships(session, lease, token).ConfigureAwait(false);
                RequireSave(await session.AcceptPublished(remote, today).ConfigureAwait(false));
                if (!creating || session.Document.Sync.Publish is not null || !session.Document.Sync.Failures.IsEmpty)
                    return Result(session, today, review);
            }
        }
        catch (Exception ex) when (Expected(ex))
        {
            var saved = await RetainInterruptedAttempt(session).ConfigureAwait(false);
            return new(false, (session.Document.Sync.Unverified.IsEmpty ? "" : "発行結果は未検証です。再取得して確認してください。 ") + ex.Message + (saved is { Succeeded: false } ? " " + saved.Error : ""), review);
        }
        finally { gate.Release(); }
    }
}
