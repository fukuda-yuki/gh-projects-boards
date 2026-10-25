using System.Collections.Immutable;
using System.Globalization;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.PlanEditor;

internal sealed record PlanRemoteSnapshot(PlanBaseline Baseline, ImmutableDictionary<string, string> Items,
    ImmutableArray<ProjectFieldDefinition> Fields, int DraftCount, int PullRequestCount)
{ public int InaccessibleCount { get; init; }
  // #201: a publish observes only the items it writes. The rest of the snapshot is carried over, so a
  // scoped snapshot is not a complete view: it can verify a written item, never the whole Project.
  public bool Scoped { get; init; }
  public ImmutableDictionary<string, string> PeopleNames { get; init; } = ImmutableDictionary<string, string>.Empty;
  public ImmutableDictionary<string, PlanIssueLink> IssueLinks { get; init; } = ImmutableDictionary<string, PlanIssueLink>.Empty;
  public ImmutableDictionary<string, ImmutableArray<string>> SubIssueOrders { get; init; } = ImmutableDictionary<string, ImmutableArray<string>>.Empty;
  // #231: rows whose Issue the viewer cannot update, observed by this read.
  public ImmutableArray<string> CannotUpdate { get; init; } = [];
  // #258: rows whose Issue Field values the viewer cannot set, observed by this read.
  public ImmutableArray<string> CannotSetFields { get; init; } = [];
  // #238: when GitHub last changed each row's 実績 or 残; null when GitHub holds neither value.
  public ImmutableDictionary<string, DateTimeOffset?> ProgressUpdatedAt { get; init; } = ImmutableDictionary<string, DateTimeOffset?>.Empty; }
internal static class PlanSnapshot
{
    internal static string ReadDiagnostic(ProjectReadResult result) => $"{result.Outcome}: " + string.Join("; ",
        result.Problems.Select(p => $"{p.Stage}/{p.Kind}/{p.Failure}/{p.ApiOutcome}/{p.HttpStatus}"));
    internal static async Task<ProjectReadResult> ReadConsistentAsync(GhConnectionService service, GhConnectionService.OperationLease lease,
        ConnectionContext context, ScopedId project, CancellationToken token, IProgress<RemoteProgress>? progress = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await new ProjectReader(service, lease).ReadAsync(context, project, token, progress).ConfigureAwait(false);
            if (result.Outcome == ProjectReadOutcome.Complete || attempt == 3 || result.Outcome != ProjectReadOutcome.Partial ||
                result.Problems.Count == 0 || result.Problems.Any(p => p.Kind != ReadProblemKind.ConcurrentChange)) return result;
            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        }
    }
    // #201: the same bounded concurrent-change policy as ReadConsistentAsync, for the scoped read.
    internal static async Task<ProjectReadResult> ReadItemsConsistentAsync(GhConnectionService service, GhConnectionService.OperationLease lease,
        ConnectionContext context, ScopedId project, IReadOnlyList<string> issueIds, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await new ProjectReader(service, lease).ReadItemsAsync(context, project, issueIds, token).ConfigureAwait(false);
            if (result.Outcome == ProjectReadOutcome.Complete || attempt == 3 || result.Outcome != ProjectReadOutcome.Partial ||
                result.Problems.Count == 0 || result.Problems.Any(p => p.Kind != ReadProblemKind.ConcurrentChange)) return result;
            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        }
    }
    internal static PlanRemoteSnapshot From(ProjectReadResult result, ProjectPlanSettings settings, TimeZoneInfo? timeZone = null)
    {
        var p = RequireComplete(result, "プロジェクト全体を取得できませんでした。計画は変更していません。再試行してください。 ");
        var (rows, items, progress) = ObservedRows(p, settings, timeZone);
        return new(new(rows, Columns(p)) {
                RepositoryMilestones = p.Milestones.Select(m => new PlanRepositoryOption(Repository(p, m.RepositoryId), m.Id.NodeId, m.Title)).ToImmutableArray(),
                RepositoryLabels = p.Labels.Select(l => new PlanRepositoryOption(Repository(p, l.RepositoryId), l.Id, l.Name)).ToImmutableArray(),
                Milestones = Milestones(p) },
            items, p.Fields.ToImmutableArray(), p.Items.Count(i => i.Kind == ProjectItemKind.Draft), p.Items.Count(i => i.Kind == ProjectItemKind.PullRequest))
        { InaccessibleCount = p.UndeliveredItemCount + p.Items.Count(i => i.Kind == ProjectItemKind.Unavailable),
          IssueLinks = p.Issues.Values.ToImmutableDictionary(i => i.Id.NodeId, i => new PlanIssueLink($"{i.Repository.NameWithOwner}#{i.Number}", i.Url)),
          PeopleNames = PeopleNames(p),
          SubIssueOrders = p.Issues.Values.Where(i => items.ContainsKey(i.Id.NodeId)).ToImmutableDictionary(i => i.Id.NodeId, i => i.Native!.SubIssues.Select(c => c.NodeId).ToImmutableArray()),
          // #231: only a definite false observation disables a command; an unavailable capability is not treated
          // as a denial, so a read that could not see permission leaves the observation alone.
          CannotUpdate = DeniedUpdates(p, items),
          CannotSetFields = DeniedFieldWrites(p, items),
          ProgressUpdatedAt = progress };
    }
    // #201: assembles the scoped observation into the whole-Project snapshot shape the merge, verification
    // and adoption paths expect. The written rows and their items come from the read; a row outside the
    // scope keeps its last known values, while one the read covered and did not deliver is treated as gone
    // — that absence is how an archived or deleted item is verified.
    internal static PlanRemoteSnapshot FromItems(ProjectReadResult result, PlanDocument document, IReadOnlyCollection<string> scope, TimeZoneInfo? timeZone = null)
    {
        var p = RequireComplete(result, "発行対象の項目を取得できませんでした。計画は変更していません。再試行してください。 ");
        var settings = document.State.Settings;
        var (observed, items, progress) = ObservedRows(p, settings, timeZone);
        var covered = scope.ToHashSet(StringComparer.Ordinal);
        var known = document.Baseline.Rows.Select(r => r.Identity).ToHashSet(StringComparer.Ordinal);
        var absent = document.Sync.Unavailable.ToHashSet(StringComparer.Ordinal);
        var byIdentity = observed.ToDictionary(r => r.Identity);
        var rows = document.Baseline.Rows
            .Where(r => !absent.Contains(r.Identity))
            .Select(r => byIdentity.TryGetValue(r.Identity, out var fresh) ? fresh : covered.Contains(r.Identity) ? null : r)
            .OfType<PlanRow>()
            .Concat(observed.Where(r => !known.Contains(r.Identity))).ToImmutableArray();
        var repositories = p.Issues.Values.Select(i => i.Repository.NameWithOwner).ToHashSet(StringComparer.Ordinal);
        return new(new(rows, Columns(p)) {
                Milestones = document.Baseline.Milestones,
                RepositoryMilestones = document.Baseline.RepositoryMilestones.Where(m => !repositories.Contains(m.Repository))
                    .Concat(p.Milestones.Select(m => Repository(p, m.RepositoryId) is { Length: > 0 } name ? new PlanRepositoryOption(name, m.Id.NodeId, m.Title) : null).Where(m => m is not null).Select(m => m!)).ToImmutableArray(),
                RepositoryLabels = document.Baseline.RepositoryLabels.Where(l => !repositories.Contains(l.Repository))
                    .Concat(p.Labels.Select(l => Repository(p, l.RepositoryId) is { Length: > 0 } name ? new PlanRepositoryOption(name, l.Id, l.Name) : null).Where(l => l is not null).Select(l => l!)).ToImmutableArray() },
            items, p.Fields.ToImmutableArray(), document.Sync.DraftCount, document.Sync.PullRequestCount)
        { Scoped = true,
          InaccessibleCount = document.Sync.InaccessibleCount,
          IssueLinks = document.Sync.IssueLinks.SetItems(p.Issues.Values.ToImmutableDictionary(i => i.Id.NodeId, i => new PlanIssueLink($"{i.Repository.NameWithOwner}#{i.Number}", i.Url))),
          PeopleNames = document.Sync.PeopleNames.SetItems(PeopleNames(p)),
          SubIssueOrders = document.Sync.NativeOrders.SetItems(p.Issues.Values.Where(i => items.ContainsKey(i.Id.NodeId))
              .ToImmutableDictionary(i => i.Id.NodeId, i => i.Native!.SubIssues.Select(c => c.NodeId).ToImmutableArray())),
          // #231: a scoped read observes permission only for the rows it covered, so an observation for a row
          // outside the scope is carried over while the row stays in the plan, and dropped when it leaves. A row
          // the read covered but did not deliver is gone, so its entry goes too.
          CannotUpdate = document.Sync.CannotUpdate.Where(id => !observed.Any(r => r.Identity == id) && rows.Any(r => r.Identity == id))
              .Concat(DeniedUpdates(p, items)).Distinct(StringComparer.Ordinal).ToImmutableArray(),
          CannotSetFields = DeniedFieldWrites(p, items),
          // #238: like the Issue links, a row outside the scope keeps its last observed change time.
          ProgressUpdatedAt = document.Sync.ProgressUpdatedAt.Where(pair => !covered.Contains(pair.Key) && rows.Any(r => r.Identity == pair.Key))
              .ToImmutableDictionary(StringComparer.Ordinal).SetItems(progress) };
    }
    // #231: the identities this read observed with a definite inability to update, keyed the way the rows are.
    private static ImmutableArray<string> DeniedUpdates(ProjectReadModel p, ImmutableDictionary<string, string> items)
        => p.Issues.Values.Where(i => items.ContainsKey(i.Id.NodeId) && i.Capability?.CanUpdate == false)
            .Select(i => i.Id.NodeId).Distinct(StringComparer.Ordinal).ToImmutableArray();
    private static ImmutableArray<string> DeniedFieldWrites(ProjectReadModel p, ImmutableDictionary<string, string> items)
        => p.Issues.Values.Where(i => items.ContainsKey(i.Id.NodeId) && i.CanSetFields == false)
            .Select(i => i.Id.NodeId).Distinct(StringComparer.Ordinal).ToImmutableArray();
    private static ProjectReadModel RequireComplete(ProjectReadResult result, string message)
    {
        if (result.Outcome != ProjectReadOutcome.Complete || result.Project is not { FieldsComplete: true, ItemsComplete: true, MilestonesComplete: true } p)
            throw new InvalidOperationException(message + ReadDiagnostic(result));
        var statusField = p.Fields.SingleOrDefault(f => f.Name == "Status" && f.DataType == "SINGLE_SELECT" && f.ValueOwner == FieldOwner.ProjectItem);
        if (statusField is not null && statusField.Availability != ValueAvailability.Present) throw new InvalidOperationException("Status を読み取れません。");
        return p;
    }
    // The fields whose values the app reads. A field it cannot read is never offered as a column or a mapping.
    private static ImmutableArray<PlanColumnDefinition> Columns(ProjectReadModel p)
        => p.Fields.Where(f => f.Availability == ValueAvailability.Present).Select(f => new PlanColumnDefinition(f.Id.NodeId, f.Name, f.DataType)
            { Options = f.Options.Select(o => o.Name).ToImmutableArray(), IssueField = f.IssueFieldId is not null }).ToImmutableArray();
    private static ImmutableArray<PlanMilestone> Milestones(ProjectReadModel p)
        => p.Milestones.GroupBy(m => m.Title, StringComparer.Ordinal).Select(g => new PlanMilestone(g.Key,
            g.Max(m => m.DueDate), g.All(m => m.Closed), g.Select(m => m.DueDate).Distinct().Count() > 1)).ToImmutableArray();
    private static ImmutableDictionary<string, string> PeopleNames(ProjectReadModel p)
        => p.Issues.Values.SelectMany(i => i.Native?.Assignees ?? []).DistinctBy(a => a.Id).ToImmutableDictionary(a => a.Id.NodeId, a => a.Login);
    private static string Repository(ProjectReadModel p, ScopedId repositoryId)
        => p.Issues.Values.FirstOrDefault(i => i.Repository.Id == repositoryId)?.Repository.NameWithOwner ?? "";
    private static (ImmutableArray<PlanRow> Rows, ImmutableDictionary<string, string> Items, ImmutableDictionary<string, DateTimeOffset?> Progress) ObservedRows(
        ProjectReadModel p, ProjectPlanSettings settings, TimeZoneInfo? timeZone)
    {
        // #238: GitHub reports a change time only for Project field values, so the progress change time is
        // observed only while both 実績 and 残 are mapped to Project fields.
        var progressFields = settings.Columns.Where(c => c.Role is PlanField.Actual or PlanField.Remaining)
            .Select(c => p.Fields.SingleOrDefault(f => f.Id.NodeId == c.FieldId)).ToArray();
        var observeProgress = progressFields.Length == 2 && progressFields.All(f => f is { ValueOwner: FieldOwner.ProjectItem, IssueFieldId: null });
        var progress = ImmutableDictionary.CreateBuilder<string, DateTimeOffset?>(StringComparer.Ordinal);
        var statusField = p.Fields.SingleOrDefault(f => f.Name == "Status" && f.DataType == "SINGLE_SELECT" && f.ValueOwner == FieldOwner.ProjectItem);
        var rows = ImmutableArray.CreateBuilder<PlanRow>(); var items = ImmutableDictionary.CreateBuilder<string, string>();
        foreach (var item in p.Items.Where(i => i.Kind == ProjectItemKind.Issue && !i.IsArchived))
        {
            if (item.ContentId is null || !p.Issues.TryGetValue(item.ContentId, out var issue) || !item.ValuesComplete ||
                issue.Native is not { Complete: true } native || issue.Labels.Availability is not (ValueAvailability.Empty or ValueAvailability.Present) || issue.Milestone.Availability is not (ValueAvailability.Empty or ValueAvailability.Present) || issue.Title.Availability != ValueAvailability.Present ||
                issue.State.Availability != ValueAvailability.Present ||
                issue.State.Value == IssueState.Closed && (issue.ClosedAt.Availability != ValueAvailability.Present || issue.ClosedAt.Value is null) || native.Parent.Availability is not (ValueAvailability.Present or ValueAvailability.Empty))
                throw new InvalidOperationException("Issue の取得が不完全です。");
            var row = new PlanRow(issue.Id.NodeId, issue.Title.Value!, issue.Repository.NameWithOwner)
            {
                MilestoneTitle = issue.Milestone.Value?.Title,
                Labels = (issue.Labels.Value ?? []).Select(l => l.Name).Order(StringComparer.Ordinal).ToImmutableArray(),
                Closed = issue.State.Value == IssueState.Closed,
                // #231: NOT_PLANNED and DUPLICATE read as 中止; COMPLETED (or a closed Issue with no
                // reason) reads as 完了.
                Cancelled = issue.State.Value == IssueState.Closed
                    && issue.StateReason.Value is IssueStateReason.NotPlanned or IssueStateReason.Duplicate,
                CloseDate = issue.State.Value == IssueState.Closed && issue.ClosedAt.Value is { } closedAt
                    ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(closedAt, timeZone ?? TimeZoneInfo.Local).DateTime) : null,
                Assignees = native.Assignees.Select(x => x.Id.NodeId).Order(StringComparer.Ordinal).ToImmutableArray(),
                Predecessors = native.Predecessors.Select(x => x.NodeId).Order(StringComparer.Ordinal).ToImmutableArray(),
                Parent = native.Parent.Value?.NodeId
            };
            if (statusField is not null)
            {
                var status = item.Values.SingleOrDefault(v => v.FieldId == statusField.Id);
                if (status is not null && status.Availability is not (ValueAvailability.Present or ValueAvailability.Empty))
                    throw new InvalidOperationException("Status を読み取れません。");
                if (status?.Availability == ValueAvailability.Present)
                    row = row with { Status = statusField.Options.SingleOrDefault(o => o.Id == status.OptionId)?.Name
                        ?? throw new InvalidOperationException("Status の選択肢を確認してください。") };
            }
            foreach (var mapping in settings.Columns)
            {
                var field = p.Fields.SingleOrDefault(f => f.Id.NodeId == mapping.FieldId);
                if (field is null || field.DataType != mapping.DataType || field.ValueOwner != FieldOwner.ProjectItem && field.IssueFieldId is null ||
                    field.Availability != ValueAvailability.Present) throw new InvalidOperationException("割り当てた列を読み取れません。");
                var value = item.Values.SingleOrDefault(v => v.FieldId?.NodeId == mapping.FieldId);
                if (value is not null && value.Availability is not (ValueAvailability.Present or ValueAvailability.Empty))
                    throw new InvalidOperationException("列の値を読み取れません。");
                var scalar = value?.Availability == ValueAvailability.Present ? value.Scalar : null;
                var json = mapping.Role switch
                {
                    PlanField.Estimate or PlanField.Remaining or PlanField.Actual => scalar is null ? "null" :
                        decimal.Parse(scalar, NumberStyles.Number, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                    PlanField.Start or PlanField.End => scalar is null ? "null" :
                        PlanJson.Text(DateOnly.ParseExact(scalar, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    PlanField.Phase => value?.Availability != ValueAvailability.Present ? "null" : PlanJson.Text(
                        field.Options.SingleOrDefault(o => o.Id == value.OptionId)?.Name ?? throw new InvalidOperationException("工程の選択肢を確認してください。")),
                    _ => throw new InvalidOperationException("未対応の列割り当てです。")
                };
                row = PlanValues.Set(row, mapping.Role, json);
            }
            foreach (var definition in p.Fields.Where(f => f.ValueOwner == FieldOwner.ProjectItem || f.IssueFieldId is not null))
            {
                var key = "Project:" + definition.Id.NodeId;
                var value = item.Values.SingleOrDefault(v => v.FieldId?.NodeId == key[8..]);
                var availability = value?.Availability ?? ValueAvailability.Unavailable;
                string? json = null;
                if (availability == ValueAvailability.Empty) json = "null";
                else if (availability == ValueAvailability.Present && definition is not null)
                {
                    if (definition.DataType == "SINGLE_SELECT")
                    {
                        var option = definition.Options.SingleOrDefault(o => o.Id == value?.OptionId);
                        if (option is null) availability = ValueAvailability.Unavailable; else json = PlanJson.Text(option.Name);
                    }
                    else if (definition.DataType == "NUMBER")
                    {
                        if (decimal.TryParse(value?.Scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) json = PlanJson.Text(number);
                        else availability = ValueAvailability.Unavailable;
                    }
                    else if (definition.DataType == "DATE")
                    {
                        if (DateOnly.TryParseExact(value?.Scalar,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day)) json = PlanJson.Text(day);
                        else availability = ValueAvailability.Unavailable;
                    }
                    else json = PlanJson.Text(value?.Scalar);
                }
                row = row with { ExtraValues = row.ExtraValues.SetItem(key, new(availability,json)) };
            }
            rows.Add(row); items.Add(row.Identity, item.Id.NodeId);
            if (observeProgress)
            {
                // An item with neither 実績 nor 残 holds no change time: 未更新 judges that task as stale. A present
                // value GitHub sends no change time for leaves the observation unknown instead of guessing.
                var values = item.Values.Where(v => progressFields.Any(f => f!.Id == v.FieldId) && v.Availability == ValueAvailability.Present).ToArray();
                if (values.Length == 0) progress[row.Identity] = null;
                else if (values.All(v => v.UpdatedAt is not null)) progress[row.Identity] = values.Max(v => v.UpdatedAt);
            }
        }
        return (rows.ToImmutable(), items.ToImmutable(), progress.ToImmutable());
    }
}
