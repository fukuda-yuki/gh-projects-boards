using System.Collections.Immutable;

namespace GhProjectsBoards.Core.Projects;

internal sealed record CreationRepository(string Id, string Name, bool IssuesEnabled, bool Archived, bool CanCreate, DateTimeOffset At)
{
    public bool Allowed => IssuesEnabled && !Archived && CanCreate;
}
internal sealed record CreatedIssue(string Id, string RepositoryId, int Number, string Url, string Title, DateTimeOffset At);
internal sealed record CreationOperation(string Id, string LocalId, long Stamp, CreationRepository Repository, string Title,
    ImmutableArray<LocalSelect> Selects, bool Authorized = true, bool Dispatched = false,
    CreatedIssue? Received = null, CreatedIssue? Verified = null, string? ItemId = null,
    bool MembershipDispatched = false, ApplyOperation[]? Fields = null, bool Completed = false,
    string Reason = "未送信", string? PreviousAttempt = null, bool EarlierUncertain = false, bool UserBound = false,
    string? ReceivedId = null, ApplyOperation[]? EarlierFields = null, string? SetupReviewedTitle = null, string? ReceivedItemId = null,
    LocalSelect[]? SetupIntents = null, LocalSelect[][]? EarlierSetupIntents = null);
internal sealed record CreationSetupReview(string BatchId, string OperationId, long Revision, CreatedIssue Issue, ApplyOperation[]? Fields,
    LocalSelect[] Intents, LocalSelect[] Withdrawn);

internal static class CreationJournal
{
    public static void Validate(DraftRecord record)
    {
        if ((record.Journal ?? []).Any(b => b is null)) throw new InvalidDataException("Missing creation batch.");
        var all = (record.Journal ?? []).SelectMany(b => b.Creations ?? []).ToArray();
        if (record.Version < 5 && all.Length > 0) throw new InvalidDataException("Unversioned creation history.");
        if (all.Select(c => c?.Id).Distinct().Count() != all.Length) throw new InvalidDataException("Duplicate creation attempt.");
        var scoped = (record.Journal ?? []).SelectMany(b => (b.Creations ?? []).Select(c => (Creation: c, Project: b.Project))).ToArray();
        if (scoped.Any(x => x.Creation is null) || scoped.GroupBy(x => x.Creation.LocalId).Any(g => g.Select(x => x.Project).Distinct().Count() != 1)
            || scoped.Any(x => (record.LocalRows ?? []).Any(r => r.Id == x.Creation.LocalId && r.ProjectId != x.Project.NodeId)))
            throw new InvalidDataException("Creation lineage changed Project.");
        foreach (var b in record.Journal ?? [])
        foreach (var c in b.Creations ?? [])
        {
            bool Identity(CreatedIssue? i) => i is null || !string.IsNullOrWhiteSpace(i.Id) && i.RepositoryId == c.Repository.Id
                && i.Number > 0 && Uri.TryCreate(i.Url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host == record.Scope.Host
                && !string.IsNullOrWhiteSpace(i.Title) && i.At != default;
            if (c is null || string.IsNullOrWhiteSpace(c.Id) || !c.LocalId.StartsWith("local-") || c.Repository is null
                || string.IsNullOrWhiteSpace(c.Repository.Id) || !c.Repository.Allowed || c.Repository.At == default
                || c.Stamp < 0 || c.Stamp > b.ReviewedRevision || string.IsNullOrWhiteSpace(c.Title) || c.Title.IndexOfAny(['\r','\n','\t']) >= 0
                || c.Selects.IsDefault || c.Selects.Any(s => s is null || s.ExplicitClear && s.OptionId is not null)
                || !Identity(c.Received) || !Identity(c.Verified) || c.ItemId is not null && c.Verified is null
                || c.Completed && (c.ItemId is null || c.Fields is null || c.Fields.Any(f => f.State != ApplyState.Succeeded))
                || c.ReceivedId is not null && string.IsNullOrWhiteSpace(c.ReceivedId)
                || c.ReceivedItemId is not null && (string.IsNullOrWhiteSpace(c.ReceivedItemId) || c.Verified is null)
                || c.PreviousAttempt is not null && !all.Any(a => a.Id == c.PreviousAttempt && a.LocalId == c.LocalId && a.Repository.Id == c.Repository.Id))
                throw new InvalidDataException("Invalid creation evidence.");
            var setupFields = (c.Fields ?? []).Concat(c.EarlierFields ?? []).ToArray();
            if ((c.EarlierSetupIntents ?? []).Any(s => s is null)) throw new InvalidDataException("Missing prior setup approval.");
            if ((c.Fields ?? []).Select(f => f?.Key).Distinct().Count() != (c.Fields ?? []).Length
                || c.Selects.Concat(c.SetupIntents ?? []).Concat((c.EarlierSetupIntents ?? []).SelectMany(s => s)).Any(s => s is null
                    || string.IsNullOrWhiteSpace(s.FieldId) || s.ExplicitClear && s.OptionId is not null))
                throw new InvalidDataException("Invalid creation setup intents.");
            if (setupFields.Any(f => f is null || f.IssueId != c.Verified?.Id || f.ItemId != c.ItemId || f.Key.Kind != "Select"
                || !c.Selects.Concat(c.SetupIntents ?? []).Concat((c.EarlierSetupIntents ?? []).SelectMany(s => s)).Any(s => s.FieldId == f.Key.FieldId && (s.OptionId is not null || s.ExplicitClear)
                    && s.OptionId == f.Intended.Value && s.ExplicitClear == f.Intended.Clear)))
                throw new InvalidDataException("Creation field target or approved intent mismatch.");
            foreach (var f in setupFields)
                ApplyJournal.Validate(record with { Journal = [b with { Creations = null, Operations = [f] }] });
        }
        if (all.Where(c => c.Verified is not null).GroupBy(c => c.Verified!.Id).Any(g => g.Select(c => c.LocalId).Distinct().Count() != 1))
            throw new InvalidDataException("Issue claimed by different creation lineages.");
    }
}

internal sealed partial class EditingWorkspace
{
    public IEnumerable<CreationOperation> Creations => journal.SelectMany(b => b.Creations ?? []);
    public bool CreationLocked(string localId) => Creations.Any(c => c.LocalId == localId && (c.Authorized || c.Dispatched || c.Verified is not null));
    private CreationOperation[] ReviewCreations(ProjectRegistration p, LocalRow[] rows,
        IReadOnlyDictionary<string, CreationRepository>? destinations, List<ApplyReviewProblem> blocked)
    {
        var result = new List<CreationOperation>();
        foreach (var r in rows)
        {
            foreach (var error in LocalProblems(p, r.Id)) blocked.Add(new(r.Id, null, error));
            if (CreationLocked(r.Id)) blocked.Add(new(r.Id, null, "以前の作成履歴があります。履歴から照合・解決してください。"));
            var repository = destinations?.GetValueOrDefault(r.Id);
            if (repository is null || !repository.Allowed || repository.Name != r.Repository)
            { blocked.Add(new(r.Id, null, "宛先Repository ID・Issue有効化・archive・作成権限を確認できません。")); continue; }
            if (p.Snapshot.Capability?.CanUpdate != true) blocked.Add(new(r.Id, null, "Project更新権限を確認できません。"));
            result.Add(new(Guid.NewGuid().ToString("N"), r.Id, r.Stamp, repository, r.Title, r.Selects));
        }
        return result.ToArray();
    }
    public void RecordCreation(string batchId, CreationOperation next)
    {
        var index = journal.FindIndex(b => b.Id == batchId);
        var old = journal[index].Creations!.Single(c => c.Id == next.Id);
        if (old.LocalId != next.LocalId || old.Title != next.Title || old.Repository != next.Repository || old.Stamp != next.Stamp
            || !old.Selects.SequenceEqual(next.Selects) || old.Dispatched && !next.Dispatched
            || old.ReceivedId is not null && old.ReceivedId != next.ReceivedId
            || old.Verified is not null && old.Verified.Id != next.Verified?.Id)
            throw new InvalidOperationException("Creation payload and known identity cannot be replaced.");
        if (next.Verified is { } identity && Creations.Any(c => c.LocalId != next.LocalId && c.Verified?.Id == identity.Id))
            throw new InvalidOperationException("このIssueは別の作成行に関連付け済みです。");
        journal[index] = journal[index] with { Creations = journal[index].Creations!.Select(c => c.Id == next.Id ? next : c).ToArray() };
        Revision++;
    }
    public void BindCreation(string batchId, string id, CreatedIssue issue, long expectedRevision)
    {
        var c = Creations.Single(c => c.Id == id);
        if (Revision != expectedRevision || c.Verified is not null || !c.Dispatched || issue.RepositoryId != c.Repository.Id)
            throw new InvalidOperationException("関連付けの比較が古いか対象が一致しません。");
        if (fields.TryGetValue(new("Title", issue.Id), out var draft) && (draft.Change is not null || draft.Buffer is not null || draft.Conflict))
            throw new InvalidOperationException("関連付け先には既存のタイトル下書きがあります。作業を解決してから再確認してください。");
        RecordCreation(batchId, c with { Verified = issue, UserBound = true, Authorized = true,
            EarlierUncertain = true, Reason = "ユーザー確認で関連付け（元の送信成功の証明ではありません）" });
    }
    public ApplyReview ReviewCreationRetry(string batchId, string id, CreationRepository repository)
    {
        var b = journal.Single(b => b.Id == batchId); var c = b.Creations!.Single(c => c.Id == id);
        if (!c.Dispatched || Creations.Any(x => x.LocalId == c.LocalId && x.Verified is not null)
            || c.Repository.Id != repository.Id || c.Repository.Name != repository.Name || !repository.Allowed
            || Creations.Last(x => x.LocalId == c.LocalId).Id != id)
            throw new InvalidOperationException("新しい作成試行を承認できません。履歴を確認してください。");
        var next = new CreationOperation(Guid.NewGuid().ToString("N"), c.LocalId, c.Stamp, repository, c.Title, c.Selects,
            PreviousAttempt: c.Id, EarlierUncertain: true, Reason: "以前の結果不確定・重複リスクを別途承認");
        return new(new(Guid.NewGuid().ToString("N"), b.Project, b.ProjectName, Revision, DateTimeOffset.UtcNow, [], [next]), [], 1, 0);
    }
    public void ConfirmCreationRetry(ApplyReview review)
    {
        var c = review.Batch.Creations!.Single();
        if (review.Batch.ReviewedRevision != Revision || c.PreviousAttempt is null || !c.EarlierUncertain
            || Creations.Last(x => x.LocalId == c.LocalId).Id != c.PreviousAttempt
            || Creations.Any(x => x.LocalId == c.LocalId && x.Verified is not null))
            throw new InvalidOperationException("作成履歴が変更されました。再確認してください。");
        foreach (var b in journal.ToArray())
            if ((b.Creations ?? []).Any(x => x.LocalId == c.LocalId))
                journal[journal.IndexOf(b)] = b with { Creations = b.Creations!.Select(x => x.LocalId == c.LocalId ? x with { Authorized = false } : x).ToArray() };
        journal.Add(review.Batch); Revision++;
    }
    public void ConfirmCreationSetup(CreationSetupReview review)
    {
        var c = Creations.Single(c => c.Id == review.OperationId);
        if (Revision != review.Revision || c.Verified?.Id != review.Issue.Id) throw new InvalidOperationException("設定の比較が古くなっています。");
        RecordCreation(review.BatchId, c with { Authorized = true, Completed = false, Fields = review.Fields,
            EarlierFields = (c.EarlierFields ?? []).Concat(c.Fields ?? []).ToArray(), SetupReviewedTitle = review.Issue.Title,
            SetupIntents = review.Intents, EarlierSetupIntents = (c.EarlierSetupIntents ?? []).Append(c.SetupIntents ?? c.Selects.ToArray()).ToArray(),
            Reason = "既知Issueの設定を再レビュー・承認済み（再作成しません）" });
        if (localRows.SingleOrDefault(r => r.Id == c.LocalId) is { } row && review.Withdrawn.Length > 0)
            ReplaceLocal(row with { Selects = row.Selects.Where(s => !review.Withdrawn.Any(x => x.FieldId == s.FieldId)).ToImmutableArray() });
    }
    private void PromoteCreatedRows(ProjectRegistration registration)
    {
        var p = registration.Snapshot;
        foreach (var c in Creations.Where(c => c.Completed && c.Verified is not null).ToArray())
        {
            var local = localRows.SingleOrDefault(r => r.Id == c.LocalId && r.ProjectId == p.Id.NodeId);
            var item = p.Items.SingleOrDefault(i => i.Id.NodeId == c.ItemId && i.ContentId?.NodeId == c.Verified!.Id);
            if (local is null || item is null || !p.Issues.TryGetValue(item.ContentId!, out var issue)) continue;
            // A complete row promotion must not drop retained unavailable intents or shared drafts.
            if (local.Selects.Any(s => !p.Fields.Any(f => f.Id.NodeId == s.FieldId && f.Availability == ValueAvailability.Present)
                || !item.Values.Any(v => v.FieldId?.NodeId == s.FieldId && v.Availability is ValueAvailability.Present or ValueAvailability.Empty))) continue;
            if (fields.TryGetValue(new("Title", issue.Id.NodeId), out var shared)
                && (shared.Change is not null || shared.Buffer is not null || shared.Conflict)) continue;
            void Transfer(FieldKey key, string? baseline, string? value, string? buffer, FieldObservation observation)
            {
                fields[key] = new(key, baseline, p.Id, observation.At,
                    value == baseline ? null : new(value, key.Kind == "Select" && value is null), buffer, Revision + 1, observation,
                    buffer is null && value != baseline && observation.Value != baseline && observation.Value != value);
            }
            var title = c.SetupReviewedTitle ?? (c.UserBound ? c.Verified!.Title : c.Title);
            Transfer(new("Title", issue.Id.NodeId), title, local.Title, local.TitleBuffer,
                new(Guid.NewGuid().ToString("N"), p.Id, registration.RetrievedAt, issue.Title.Value, issue.Title.Availability, null, []));
            foreach (var s in local.Selects)
            {
                var value = item.Values.SingleOrDefault(v => v.FieldId?.NodeId == s.FieldId);
                var definition = p.Fields.SingleOrDefault(f => f.Id.NodeId == s.FieldId);
                if (definition is null || value?.Availability is not (ValueAvailability.Present or ValueAvailability.Empty)) continue;
                var applied = c.Fields?.SingleOrDefault(f => f.Key.FieldId == s.FieldId);
                var baseline = applied is null ? value.OptionId : applied.Intended.Value;
                var currentValue = s.OptionId is null && !s.ExplicitClear ? value.OptionId : s.OptionId;
                Transfer(new("Select", item.Id.NodeId, p.Id.NodeId, s.FieldId), baseline, currentValue, null,
                    new(Guid.NewGuid().ToString("N"), p.Id, registration.RetrievedAt, value.OptionId, value.Availability, null, definition.Options.ToArray()));
            }
            // Keep unrelated parts of mixed operations available; no Undo may resurrect this lineage.
            foreach (var transaction in history.Where(t => (t.Rows ?? []).Any(r => r.Id == local.Id)).ToArray())
            {
                var index = history.IndexOf(transaction);
                var unaffected = transaction.Rows!.Where(r => r.Id != local.Id).ToArray();
                history[index] = transaction with { Rows = transaction.Rows!.Where(r => r.Id == local.Id).ToArray(), Changes = [], InvalidReason = "作成済み行の以前のUndoは復元できません。" };
                if (unaffected.Length > 0 || transaction.Changes.Length > 0)
                    history.Insert(index + 1, new(transaction.Id + "-remaining", transaction.ProjectId, transaction.Changes, Rows: unaffected));
            }
            localRows.Remove(local); Revision++;
        }
    }
}
