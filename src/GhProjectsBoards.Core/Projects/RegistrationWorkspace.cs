using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.Projects;

internal enum RegistrationAttempt { None, Retrieving, Complete, Partial, Cancelled, Failed, SaveFailed }

internal sealed partial class RegistrationWorkspace(RegistrationStore store)
{
    private readonly DraftStore draftStore = new(store.Root);
    private readonly Dictionary<ConnectionScope, DraftSession> drafts = [];
    private readonly HashSet<ConnectionScope> blockedDrafts = [];
    public event Action? Transitioning;
    public Func<bool>? CanRefresh { get; set; }
    public long AcceptedRefreshGeneration { get; private set; }
    public void CancelPendingEdits() => Transitioning?.Invoke();
    public DraftSession? Drafts => Profile is { } scope ? drafts.GetValueOrDefault(scope) : null;
    public async Task<bool> PrepareLocalRowsAsync()
    {
        if (Selected is not { } selected || Drafts is not { } session) return false;
        if (session.Workspace.HasCheckpoint) return true;
        return await session.CommitAsync(w => { w.SetRegistrations(registrations); return w; },
            () => Selected == selected && store.MatchesLegacy(selected.Snapshot.Id.Scope, registrations));
    }
    public bool HasDraftWork(ProjectReadModel project) => blockedDrafts.Contains(project.Id.Scope) || drafts.TryGetValue(project.Id.Scope, out var d) && d.Workspace.HasWork(project);
    public async Task<bool> FlushDraftsAsync()
    {
        foreach (var session in drafts.Values.ToArray())
            if (!await session.FlushAsync()) { Status = session.Status; Changed?.Invoke(); return false; }
        return true;
    }
    private CancellationTokenSource? cancellation;
    private Task? owned;
    private int generation;
    private ConnectionContext? context;
    private GhConnectionService? service;
    private readonly List<ProjectRegistration> registrations = [];
    private readonly Dictionary<ScopedId, RegistrationAttempt> attempts = [];
    private ScopedId? attemptedId;
    public event Action? Changed;
    public IReadOnlyList<ProjectRegistration> Registrations => registrations;
    public ConnectionScope? Profile { get; private set; }
    public int ConnectionRevision { get; private set; }
    public string ProfileLogin => CanRead ? context!.Login : registrations.FirstOrDefault(r => r.Snapshot.Id.Scope == Profile)?.ViewerLogin ?? "未選択";
    public ProjectRegistration? Selected { get; private set; }
    public ProjectReadModel? Incomplete { get; private set; }
    public string Status { get; private set; } = "保存済みプロフィールを選択するか、接続を確認してください。";
    public bool IsBusy { get; private set; }
    public bool CanRead => context is { IsInvalidated: false } && Profile == ConnectionScope.From(context);
    public RegistrationAttempt LatestAttempt => Selected is { } r ? attempts.GetValueOrDefault(r.Snapshot.Id)
        : attemptedId is { } id && id.Scope == Profile ? attempts.GetValueOrDefault(id) : RegistrationAttempt.None;

    public async Task RestoreAsync()
    {
        var loaded = await store.LoadAsync();
        registrations.Clear(); registrations.AddRange(loaded.Registrations);
        foreach (var scope in registrations.Select(r => r.Snapshot.Id.Scope).Distinct().ToArray())
        {
            try
            {
                var record = loaded.Checkpoints?.SingleOrDefault(r => r.Scope == scope) ?? await draftStore.LoadAsync(scope);
                RestoreSession(scope, record);
            }
            catch (Exception) { blockedDrafts.Add(scope); }
        }
        Status = loaded.Problems.Count == 0 ? "ローカル保存を読み込みました。プロフィールの選択は接続確認ではありません。"
            : "保存データに問題があります。自動修復・削除はしていません：" + string.Join(" / ", loaded.Problems.Select(p => $"{p.File}: {p.Kind}"));
        Changed?.Invoke();
        if (blockedDrafts.Count > 0) { Status = "下書きのスキーマ・破損・アクセスに問題があります。編集とキャッシュ置換を停止しています。元ファイルは保持しています。"; Changed?.Invoke(); }
    }
    public async Task BindAsync(ConnectionContext? next, GhConnectionService? nextService = null)
    {
        Transitioning?.Invoke();
        if (!await FlushDraftsAsync()) return;
        await StopAsync();
        context = next; service = next is null ? null : nextService ?? new(next.Executable, next.Host);
        Profile = next is null ? null : ConnectionScope.From(next);
        if (Profile is { } profile && !drafts.ContainsKey(profile) && !blockedDrafts.Contains(profile))
        {
            try { var record = await draftStore.LoadAsync(profile); RestoreSession(profile, record); }
            catch (Exception) { blockedDrafts.Add(profile); }
        }
        Selected = null; Incomplete = null;
        ConnectionRevision++;
        Changed?.Invoke();
    }
    public void InvalidateConnection()
    {
        Transitioning?.Invoke();
        generation++; cancellation?.Cancel(); context = null; service = null;
        Profile = null; Selected = null; Incomplete = null; ConnectionRevision++; Changed?.Invoke();
    }
    private void RestoreSession(ConnectionScope scope, DraftRecord? record)
    {
        if (record?.Registrations is { } saved)
        {
            registrations.RemoveAll(r => r.Snapshot.Id.Scope == scope);
            registrations.AddRange(saved.Select(RegistrationStore.FromRecord));
        }
        drafts[scope] = new(draftStore, record is null ? new(scope) : EditingWorkspace.Restore(record), record?.Revision ?? 0);
    }
    public async Task SelectProfileAsync(ConnectionScope? profile)
    {
        Transitioning?.Invoke();
        if (!await FlushDraftsAsync()) return;
        await StopAsync();
        Profile = profile; Selected = null; Incomplete = null;
        Changed?.Invoke();
    }
    public async Task<bool> SelectAsync(ScopedId id)
    {
        Transitioning?.Invoke();
        if (!await FlushDraftsAsync()) return false;
        await StopAsync();
        Selected = registrations.SingleOrDefault(r => r.Snapshot.Id == id && id.Scope == Profile);
        Incomplete = null;
        Changed?.Invoke();
        return Selected is not null;
    }
    public async Task StopAsync()
    {
        generation++;
        cancellation?.Cancel();
        if (owned is { } task) await task;
    }
    public void Cancel() => cancellation?.Cancel();
    public Task DiscoverAsync(Func<ProjectDiscovery, ConnectionContext, CancellationToken, Task> action)
        => RunAsync(async token =>
        {
            RequireConnection();
            Status = "Projectの候補を検索しています…"; Changed?.Invoke();
            await action(new(service!), context!, token);
            token.ThrowIfCancellationRequested();
            Status = "検索が完了しました。候補を選択して内容を確認してください。";
        });

    public Task RegisterAsync(ProjectChoice choice, string? defaultRepository, bool refresh = false)
        => RunAsync(async token =>
        {
            RequireConnection();
            if (choice.Id.Scope != Profile) throw new DiscoveryException(FailureKind.IdentityChanged);
            defaultRepository = ValidateRepository(defaultRepository);
            var existing = registrations.SingleOrDefault(r => r.Snapshot.Id == choice.Id);
            if (refresh && CanRefresh?.Invoke() == false) { Status = "IME変換中です。自然な操作で変換を確定・取消してから「最新を取得」を再試行してください。"; return; }
            if (refresh) { Transitioning?.Invoke(); if (!await FlushDraftsAsync()) return; }
            if (existing is not null && !refresh) { Selected = existing; Status = "既に登録されています。同じ保存データを開きました。"; return; }
            var requestGeneration = generation;
            var bound = context!;
            var readerService = service!;
            attemptedId = choice.Id;
            attempts[choice.Id] = RegistrationAttempt.Retrieving;
            Status = "取得中：Repositoryの関連付けを確認しています…"; Changed?.Invoke();
            var links = await new ProjectDiscovery(readerService).LinksAsync(bound, choice.Id, token);
            token.ThrowIfCancellationRequested();
            var result = await new ProjectReader(readerService).ReadAsync(bound, choice.Id, token, p =>
            {
                if (generation != requestGeneration) return;
                Status = $"取得中：{p.Stage} / フィールド {p.Fields} / 項目 {p.Items} / Issue {p.Issues}";
                Changed?.Invoke();
            });
            token.ThrowIfCancellationRequested();
            if (requestGeneration != generation || context != bound || bound.IsInvalidated) throw new OperationCanceledException();
            if (result.Outcome != ProjectReadOutcome.Complete || result.Project is null)
            {
                attempts[choice.Id] = result.Outcome == ProjectReadOutcome.Partial ? RegistrationAttempt.Partial
                    : result.Outcome == ProjectReadOutcome.Cancelled ? RegistrationAttempt.Cancelled : RegistrationAttempt.Failed;
                Incomplete = result.Project;
                Status = $"{AttemptText(attempts[choice.Id])}：{string.Join(" / ", result.Problems.Select(p => $"{p.Stage}: {p.Kind} {p.Failure}"))}。前回の保存データを維持します。";
                return;
            }
            if (result.Project.OwnerId != choice.OwnerId || result.Project.Number != choice.Number) throw new DiscoveryException(FailureKind.InvalidResponse);
            var saved = new ProjectRegistration(bound.Login, choice.OwnerLogin, links, defaultRepository,
                DateTimeOffset.UtcNow, result.Project);
            Status = "取得完了。ローカル保存中…"; Changed?.Invoke();
            if (blockedDrafts.Contains(choice.Id.Scope)) { Status = "下書きを読み込めないためキャッシュを置換していません。"; return; }
            bool CanCommit() => !token.IsCancellationRequested && requestGeneration == generation && context == bound && !bound.IsInvalidated && CanRefresh?.Invoke() != false;
            var checkpoint = existing is not null || Drafts?.Workspace.HasCheckpoint == true;
            try {
                if (checkpoint && Drafts is { } session)
                {
                    var committed = await session.CommitAsync(candidate =>
                    {
                        if (existing is not null) candidate.Reconcile(existing, saved);
                        candidate.SetRegistrations(registrations.Where(r => r.Snapshot.Id != choice.Id).Append(saved));
                        return candidate;
                    }, () => CanCommit() && (session.Workspace.HasCheckpoint || store.MatchesLegacy(choice.Id.Scope, registrations)));
                    if (!committed) { attempts[choice.Id] = RegistrationAttempt.SaveFailed; Status = session.Status; return; }
                }
                else await store.SaveAsync(saved, token, CanCommit);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                attempts[choice.Id] = RegistrationAttempt.SaveFailed;
                Status = "ローカル保存失敗：保存先の権限・空き容量・他の起動中アプリを確認してください。登録成功にはしていません。";
                return;
            }
            registrations.RemoveAll(r => r.Snapshot.Id == choice.Id); registrations.Add(saved);
            attempts[choice.Id] = RegistrationAttempt.Complete;
            if (generation == requestGeneration) { Selected = saved; Incomplete = null; if (refresh) AcceptedRefreshGeneration++; }
            Status = refresh ? "最新取得とフィールド照合をローカル保存しました。競合・未確認欄を確認してください。GitHubは変更していません。" : "登録完了：取得結果と設定をローカルに保存しました。";
        }, choice.Id);

    public async Task SetDefaultAsync(string? repository)
    {
        await StopAsync();
        if (Selected is not { } r) return;
        await RunAsync(async token =>
        {
            var replacement = r with { DefaultRepository = ValidateRepository(repository) };
            if (Drafts is { Workspace.HasCheckpoint: true } d)
            {
                if (!await d.CommitAsync(candidate => { candidate.SetRegistrations(registrations.Select(p => p == r ? replacement : p)); return candidate; }, () => Selected == r))
                { Status = d.Status; return; }
            }
            else await store.SaveAsync(replacement, token);
            registrations[registrations.IndexOf(r)] = replacement; Selected = replacement;
            Status = "既定Repositoryをローカル保存しました。取得範囲は変わりません。";
        });
    }
    public async Task UnregisterAsync(bool retainDrafts = false, bool discardDrafts = false)
    {
        if (Drafts?.Workspace.HasUnresolvedApply == true) { Status = "未解決のApply履歴があるため登録解除できません。履歴を再照合してください。"; Changed?.Invoke(); return; }
        Transitioning?.Invoke();
        var selected = Selected;
        await StopAsync();
        if (selected is null || selected.Snapshot.Id.Scope != Profile) return;
        if (blockedDrafts.Contains(selected.Snapshot.Id.Scope)) { Status = "下書きを確認できないため登録解除を停止しています。"; Changed?.Invoke(); return; }
        if (HasDraftWork(selected.Snapshot) && !retainDrafts && !discardDrafts) { Status = "下書きがあります。保持または破棄を明示してください。"; Changed?.Invoke(); return; }
        if (!await FlushDraftsAsync()) return;
        await RunAsync(async token =>
        {
            if (Drafts is { } session && (session.Workspace.HasCheckpoint || discardDrafts))
            {
                if (!await session.CommitAsync(candidate => {
                    if (discardDrafts) candidate.Discard(selected.Snapshot, registrations.Where(r => r != selected).Select(r => r.Snapshot));
                    candidate.SetRegistrations(registrations.Where(r => r != selected)); return candidate;
                }, () => Profile == selected.Snapshot.Id.Scope && (session.Workspace.HasCheckpoint || store.MatchesLegacy(selected.Snapshot.Id.Scope, registrations))))
                { Status = session.Status; return; }
            }
            else await store.RemoveAsync(selected.Snapshot.Id, token);
            registrations.Remove(selected); attempts.Remove(selected.Snapshot.Id); Selected = null; Incomplete = null;
            Status = "ローカル登録とキャッシュを解除しました。GitHubのデータは変更していません。";
        });
    }
    private void RequireConnection()
    {
        if (!CanRead || service is null) throw new DiscoveryException(FailureKind.IdentityChanged);
    }
    private static string? ValidateRepository(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+\z")) throw new DiscoveryException(FailureKind.InvalidInput);
        return value;
    }
    private Task RunAsync(Func<CancellationToken, Task> action, ScopedId? attempted = null)
    {
        if (IsBusy) return Task.CompletedTask;
        var source = new CancellationTokenSource(); cancellation = source;
        IsBusy = true;
        owned = ExecuteAsync(action, source, attempted);
        Changed?.Invoke();
        return owned;
    }
    private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationTokenSource source, ScopedId? attempted)
    {
        // Ensure owned is assigned before synchronous collaborators can notify the UI.
        await Task.Yield();
        try { await action(source.Token); }
        catch (OperationCanceledException)
        {
            if (attempted is not null) attempts[attempted] = RegistrationAttempt.Cancelled;
            Status = "キャンセルしました。前回の保存データを維持します。";
        }
        catch (DiscoveryException ex)
        {
            if (attempted is not null) attempts[attempted] = ex.Failure == FailureKind.Cancelled ? RegistrationAttempt.Cancelled : RegistrationAttempt.Failed;
            Status = ConnectionViewModel.FailureText(ex.Failure);
        }
        catch (Exception)
        {
            if (attempted is not null) attempts[attempted] = RegistrationAttempt.Failed;
            Status = "処理に失敗しました。保存先・接続状態を確認してください。既存データは自動削除しません。";
        }
        finally { cancellation = null; source.Dispose(); IsBusy = false; Changed?.Invoke(); }
    }
    public static string AttemptText(RegistrationAttempt value) => value switch
    {
        RegistrationAttempt.None => "今回の起動では未取得", RegistrationAttempt.Retrieving => "取得中", RegistrationAttempt.Complete => "完了",
        RegistrationAttempt.Partial => "一部取得", RegistrationAttempt.Cancelled => "キャンセル", RegistrationAttempt.SaveFailed => "ローカル保存失敗", _ => "取得失敗"
    };
}
