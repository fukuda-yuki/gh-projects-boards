using GhProjectsBoards.App.GitHub;
using System.Collections.Immutable;

namespace GhProjectsBoards.Core.Projects;

// Ownership spans observation, dispatch and durable acknowledgement, not just file replacement.
internal sealed partial class ApplyExecutor(DraftStore store, DraftSession session, ApplyRemote remote, Func<bool>? canPromote = null)
{
    public event Action<string>? Progress;
    public async Task ExecuteAsync(string batchId, CancellationToken token)
    {
        using var ownership = store.AcquireExecution(session.Workspace.Scope);
        var durable = await store.LoadAsync(session.Workspace.Scope);
        if (durable?.Revision != session.DurableRevision) throw new InvalidOperationException("別プロセスの変更があります。再起動して履歴を確認してください。");
        var batch = session.Workspace.Journal.Single(b => b.Id == batchId);
        var lastDispatch = DateTimeOffset.MinValue;
        try
        {
        foreach (var creation in batch.Creations ?? [])
        {
            if (token.IsCancellationRequested) break;
            await ExecuteCreationAsync(batch, creation, token);
        }
        foreach (var original in batch.Operations)
        {
            var o = original;
            if (o.State is ApplyState.Succeeded or ApplyState.Superseded) continue;
            if (token.IsCancellationRequested) break;
            var waits = 0;
        Revalidate:
            var pacing = lastDispatch + TimeSpan.FromSeconds(1) - DateTimeOffset.UtcNow;
            if (pacing > TimeSpan.Zero) { using var wait = PerformanceTrace.Span("mandatory-wait"); await Task.Delay(pacing, token); }
            if (o.NotBefore is { } until && until > DateTimeOffset.UtcNow)
            {
                using var wait = PerformanceTrace.Span("mandatory-wait");
                Progress?.Invoke($"レート制限待機: {until.LocalDateTime:g} まで（キャンセル可能）");
                while (until > DateTimeOffset.UtcNow)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, Math.Min(30, (until - DateTimeOffset.UtcNow).TotalSeconds))), token);
            }
            Progress?.Invoke($"{o.Identity} / {o.FieldName}: 直前照合中");
            var (observation, result) = await remote.ObserveAsync(batch, o, token);
            if (observation is null)
            {
                if (result.Failure == FailureKind.RateLimited && waits++ < 3)
                {
                    o = o with { NotBefore = DateTimeOffset.UtcNow + (result.RetryAfter ?? TimeSpan.FromMinutes(Math.Pow(2, waits - 1))), Reason = "RateLimited: 読み取り待機" };
                    await Save(o); goto Revalidate;
                }
                await Save(o with { State = o.State is ApplyState.Running or ApplyState.Unknown ? ApplyState.Unknown : ApplyState.Waiting, Reason = result.Failure.ToString() });
                if (Global(result.Failure)) break;
                continue;
            }
            if (observation.Value == o.Intended.Value)
            {
                await Save(o with { State = ApplyState.Succeeded, Verification = observation, Reason = "読み戻し一致（元の送信成功の証明ではありません）" }, true);
                continue;
            }
            if (o.State is not (ApplyState.Pending or ApplyState.Cancelled or ApplyState.Waiting) || o.Attempts.Any(a => a.State is ApplyState.Running or ApplyState.Unknown))
            {
                await Save(o with { State = ApplyState.Blocked, Verification = observation, Reason = "以前の送信結果が不確定です。明示的な再照合・新規レビューが必要です。" });
                continue;
            }
            if (observation.Value != o.Expected)
            {
                await Save(o with { State = ApplyState.Blocked, Verification = observation, Reason = "レビュー後にGitHubの値が変更されました。再照合してください。" });
                continue;
            }
            token.ThrowIfCancellationRequested();
            o = o with { State = ApplyState.Running, Reason = "送信意図を保存済み（中断時は不確定）",
                Attempts = o.Attempts.Add(new(o.Attempts.Length + 1, DateTimeOffset.UtcNow, ApplyState.Running, "Dispatch intent")) };
            await Save(o);
            lastDispatch = DateTimeOffset.UtcNow;
            result = await remote.MutateAsync(batch, o, token);
            if (!result.IsSuccess)
            {
                var state = result.Outcome == ApiOutcome.Unknown ? ApplyState.Unknown : ApplyState.Failed;
                o = o with { State = state, Reason = result.Failure.ToString(),
                    Attempts = o.Attempts.Select((a, i) => i == o.Attempts.Length - 1 ? a with { State = state, Reason = result.Failure.ToString() } : a).ToImmutableArray() };
                if (result.Failure == FailureKind.RateLimited && result.Outcome == ApiOutcome.Failed)
                    o = o with { State = ApplyState.Waiting, NotBefore = DateTimeOffset.UtcNow + (result.RetryAfter ?? TimeSpan.FromMinutes(Math.Pow(2, waits))) };
                await Save(o);
                if (o.State == ApplyState.Waiting && waits++ < 3) goto Revalidate;
                if (Global(result.Failure) || result.Failure == FailureKind.RateLimited || token.IsCancellationRequested) break;
                continue;
            }
            var verified = await remote.ObserveAsync(batch, o, token);
            var matched = verified.Observation is { } v && v.Value == o.Intended.Value;
            await Save(o with { State = matched ? ApplyState.Succeeded : ApplyState.Unknown, Verification = verified.Observation,
                Reason = matched ? "更新結果と独立読み戻しを確認" : "更新後の読み戻し不明・不一致。再送していません。",
                Attempts = o.Attempts.Select((a, i) => i == o.Attempts.Length - 1 ? a with { State = matched ? ApplyState.Succeeded : ApplyState.Unknown, Reason = "Read-back" } : a).ToImmutableArray() }, matched);
            if (Global(verified.Result.Failure) || token.IsCancellationRequested) break;
        }
        }
        finally
        {
            if (token.IsCancellationRequested)
                foreach (var o in session.Workspace.Journal.Single(b => b.Id == batchId).Operations.Where(o => o.State is ApplyState.Pending or ApplyState.Waiting))
                    await Save(o with { State = ApplyState.Cancelled, Reason = "キャンセル：追加送信していません。完了済み操作は取り消されません。" });
        }
        async Task Save(ApplyOperation operation, bool acknowledge = false)
        {
            if (!await session.CommitAsync(w => { w.RecordApply(batchId, operation, acknowledge); return w; }, () => true))
                throw new InvalidOperationException((acknowledge ? "GitHubの読み戻しは一致しましたが、ローカルへの結果保存に失敗しました。" : "実行結果の保存に失敗しました。")
                    + "追加送信を停止しました。永続Runningは不確定として復元されます。");
            Progress?.Invoke($"{operation.Identity} / {operation.FieldName}: {operation.State} / {operation.Reason}");
        }
    }
    private static bool Global(FailureKind failure) => failure is FailureKind.IdentityChanged or FailureKind.AuthenticationExpired
        or FailureKind.NotLoggedIn or FailureKind.UnknownCredentialStore or FailureKind.PlaintextCredentials;
}
