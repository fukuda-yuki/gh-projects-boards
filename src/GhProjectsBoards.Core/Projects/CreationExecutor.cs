using System.Collections.Immutable;

namespace GhProjectsBoards.Core.Projects;

internal sealed partial class ApplyExecutor
{
    private async Task ExecuteCreationAsync(ApplyBatch batch, CreationOperation original, CancellationToken token)
    {
        var c = session.Workspace.Creations.Single(c => c.Id == original.Id);
        if (!c.Authorized || c.Completed) return;
        if (session.Workspace.Creations.Last(x => x.LocalId == c.LocalId).Id != c.Id) return;
        await Task.Delay(TimeSpan.FromSeconds(1), token);
        async Task Save(CreationOperation next)
        {
            if (!await session.CommitAsync(w => { w.RecordCreation(batch.Id, next); return w; }, () => true))
                throw new InvalidOperationException("作成結果の保存に失敗しました。追加送信を停止しました。履歴から照合してください。");
            c = next; Progress?.Invoke($"{c.LocalId}: {c.Reason}");
        }
        if (c.Verified is null && c.Received is null && c.ReceivedId is null)
        {
            if (c.Dispatched)
            { await Save(c with { Reason = "作成結果不確定：保留、URL関連付け、または重複リスクの新規承認が必要です。" }); return; }
            var destination = await remote.ResolveCreationRepositoryAsync(batch.Project, c.Repository.Name, token);
            var project = await remote.ObserveCreationProjectAsync(batch, token);
            if (destination?.Allowed != true || destination.Id != c.Repository.Id || project is null)
            { await Save(c with { Reason = "宛先・作成権限・Project権限の直前確認に失敗。未送信。" }); return; }
            token.ThrowIfCancellationRequested();
            // Persist the veto before transport. No create outcome is automatically retried.
            await Save(c with { Dispatched = true, Reason = "作成送信意図を保存済み（中断時は不確定）" });
            var result = await remote.CreateAsync(batch, c, token);
            await Save(c with { Received = result.Evidence, ReceivedId = result.ReceivedId, Reason = result.ReceivedId is null
                ? "作成結果不確定：ID未確認。自動再作成しません。" : "Issue IDを受信。独立検証は未完了。" });
            if (c.ReceivedId is null) return;
        }
        var observed = await remote.ObserveCreatedIssueAsync(batch, c, null, token);
        if (observed is null)
        { await Save(c with { Reason = "Issue IDを保持していますが、同じRepositoryのIssueを確認できません。作成は再送しません。" }); return; }
        if (c.Verified is null) await Save(c with { Verified = observed, Reason = "Issue存在・identity確認済み。Project設定未完了。" });
        if (!c.UserBound && observed.Title != (c.SetupReviewedTitle ?? c.Title))
        { await Save(c with { Reason = "既知Issueのタイトルが承認値と不一致。IDを保持し設定を保留します。" }); return; }

        var snapshot = await remote.ObserveCreationProjectAsync(batch, token);
        if (snapshot is null) { await Save(c with { Reason = "Issue存在確認済み。Projectの完全な所属照合を確認できません。" }); return; }
        ProjectItemReadModel? Member(ProjectReadModel p) => p.Items.SingleOrDefault(i => i.Kind == ProjectItemKind.Issue && i.ContentId?.NodeId == c.Verified!.Id);
        var item = Member(snapshot);
        if (item is null)
        {
            if (c.ItemId is not null) { await Save(c with { Reason = "既知のProject項目が未観測です。設定を保留します。" }); return; }
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            observed = await remote.ObserveCreatedIssueAsync(batch, c, null, token);
            if (observed is null || !c.UserBound && observed.Title != (c.SetupReviewedTitle ?? c.Title)) { await Save(c with { Reason = "Project追加前のIssue照合に失敗。" }); return; }
            token.ThrowIfCancellationRequested();
            await Save(c with { MembershipDispatched = true, Reason = "Project追加意図を保存済み" });
            var added = await remote.AddCreatedIssueAsync(batch, c, token);
            if (added.ItemId is not null) await Save(c with { ReceivedItemId = added.ItemId, Reason = "所有関係を確認したProject項目IDを受信。独立所属照合待ち。" });
            // Membership is reconciled by exact content+Project, including API existing-item returns.
            snapshot = await remote.ObserveCreationProjectAsync(batch, token);
            item = snapshot is null ? null : Member(snapshot);
            if (item is null || c.ReceivedItemId is not null && c.ReceivedItemId != item.Id.NodeId)
            { await Save(c with { Reason = "Issueは存在。Project追加結果は未確認。所属照合から再開してください。" }); return; }
        }
        if (item.IsArchived || !item.ValuesComplete || c.ItemId is not null && c.ItemId != item.Id.NodeId)
        { await Save(c with { Reason = "Project項目の所有関係・状態が不一致。設定を保留します。" }); return; }
        await Save(c with { ItemId = item.Id.NodeId, Reason = "IssueとProject所属を確認済み。初期フィールド設定中。" });
        if (c.Fields is null)
        {
            var setup = new List<ApplyOperation>();
            foreach (var s in (c.SetupIntents ?? c.Selects.ToArray()).Where(s => s.OptionId is not null || s.ExplicitClear))
            {
                var field = snapshot!.Fields.SingleOrDefault(f => f.Id.NodeId == s.FieldId);
                var value = item.Values.SingleOrDefault(v => v.FieldId?.NodeId == s.FieldId);
                if (field?.ValueOwner != FieldOwner.ProjectItem || field.DataType != "SINGLE_SELECT" || field.Availability != ValueAvailability.Present
                    || value?.Availability is not (ValueAvailability.Present or ValueAvailability.Empty)
                    || s.OptionId is not null && !field.Options.Any(o => o.Id == s.OptionId))
                { await Save(c with { Reason = "初期フィールド・選択肢の観測が不完全です。" }); return; }
                var observation = new FieldObservation(Guid.NewGuid().ToString("N"), batch.Project, DateTimeOffset.UtcNow,
                    value.OptionId, value.Availability, null, field.Options.ToArray());
                setup.Add(new(Guid.NewGuid().ToString("N"), new("Select", item.Id.NodeId, batch.Project.NodeId, s.FieldId), item.Id.NodeId,
                    c.Verified!.Id, c.Verified.Url, s.FieldName, value.OptionId, new(s.OptionId, s.ExplicitClear), c.Stamp,
                    ApplyState.Pending, [], "所属後の初期値を観測・保存", observation));
            }
            foreach (var intent in c.SetupPlanningIntents ?? c.PlanningIntents ?? [])
            {
                var target = intent.Kind == "Dependency" ? session.Workspace.VerifiedPredecessor(intent.FieldId) : intent.FieldId;
                if (target is null) { await Save(c with { Reason = "先行する新規行のIssue ID検証待ちです。既知の作成結果は保持しています。" }); return; }
                var key = new FieldKey(intent.Kind, intent.Kind == "Dependency" ? c.Verified!.Id : item.Id.NodeId, batch.Project.NodeId, target);
                var operation = new ApplyOperation(Guid.NewGuid().ToString("N"), key, item.Id.NodeId, c.Verified!.Id,
                    c.Verified.Url, intent.FieldName, null, intent.Value, c.Stamp, ApplyState.Pending, [], "所属後の計画フィールドを観測");
                var initial = await remote.ObserveAsync(batch, operation, token);
                if (initial.Observation is not { } observedValue) { await Save(c with { Reason = "計画フィールド・先行関係の初期値または権限を確認できません。" }); return; }
                setup.Add(operation with { Expected = observedValue.Value, Verification = observedValue });
            }
            await Save(c with { Fields = setup.ToArray() });
        }
        foreach (var saved in c.Fields!)
        {
            var f = saved;
            if (f.State == ApplyState.Succeeded) continue;
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            var read = await remote.ObserveAsync(batch, f, token);
            if (read.Observation is not { } before) { await Save(c with { Reason = "設定前のフィールド再照合に失敗。" }); return; }
            if (before.Value != f.Intended.Value)
            {
                if (f.Attempts.Length != 0 || before.Value != f.Expected)
                { await Save(c with { Reason = "初期設定後の変更・不確定送信を検出。上書きを保留します。" }); return; }
                f = f with { State = ApplyState.Running, Verification = before, Attempts = [new(1, DateTimeOffset.UtcNow, ApplyState.Running, "Dispatch intent")] };
                await Save(c with { Fields = c.Fields.Select(x => x.Id == f.Id ? f : x).ToArray() });
                await remote.MutateAsync(batch, f, token);
                read = await remote.ObserveAsync(batch, f, token);
            }
            var matched = read.Observation?.Value == f.Intended.Value && read.Observation is not null;
            f = f with { State = matched ? ApplyState.Succeeded : ApplyState.Unknown, Verification = read.Observation,
                Attempts = f.Attempts.Select((attempt, index) => index == f.Attempts.Length - 1
                    ? attempt with { State = matched ? ApplyState.Succeeded : ApplyState.Unknown, Reason = "Read-back" } : attempt).ToImmutableArray(),
                Reason = matched ? "独立読み戻し一致" : "初期設定の結果未確認" };
            await Save(c with { Fields = c.Fields.Select(x => x.Id == f.Id ? f : x).ToArray() });
            if (!matched) return;
        }
        await Save(c with { Completed = true, Reason = c.EarlierUncertain ? "今回の設定完了を検証済み。以前の作成試行は依然不確定です。" : "Issue作成・Project設定の完了を検証済み。" });
        snapshot = await remote.ObserveCreationProjectAsync(batch, token);
        if (snapshot is not null && canPromote?.Invoke() != false && !await session.CommitAsync(w =>
        {
            var registration = w.CheckpointRegistrations.Single(r => r.Snapshot.Id == batch.Project);
            var current = registration with { Snapshot = snapshot, RetrievedAt = DateTimeOffset.UtcNow };
            w.Reconcile(registration, current);
            w.SetRegistrations(w.CheckpointRegistrations.Select(r => r.Snapshot.Id == batch.Project ? current : r)); return w;
        }, () => canPromote?.Invoke() != false)) throw new InvalidOperationException("設定は完了しましたが最新表示の保存に失敗しました。再取得してください。");
    }
}
