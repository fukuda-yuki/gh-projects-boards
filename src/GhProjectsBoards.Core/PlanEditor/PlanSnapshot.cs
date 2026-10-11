using System.Collections.Immutable;
using System.Globalization;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.PlanEditor;

internal sealed record PlanRemoteSnapshot(PlanBaseline Baseline, ImmutableDictionary<string, string> Items,
    ImmutableArray<ProjectFieldDefinition> Fields, int DraftCount, int PullRequestCount)
{ public int InaccessibleCount { get; init; }
  public ImmutableDictionary<string, string> PeopleNames { get; init; } = ImmutableDictionary<string, string>.Empty;
  public ImmutableDictionary<string, PlanIssueLink> IssueLinks { get; init; } = ImmutableDictionary<string, PlanIssueLink>.Empty;
  public ImmutableDictionary<string, ImmutableArray<string>> SubIssueOrders { get; init; } = ImmutableDictionary<string, ImmutableArray<string>>.Empty; }
internal static class PlanSnapshot
{
    internal static IReadOnlySet<string> SelectedColumns(PlanDocument document)
    {
        var selected = document.State.Settings.SheetColumns.Where(c => c.Visible).Select(c => c.Key).ToHashSet();
        selected.UnionWith(document.State.Settings.Columns.Select(c => "Field:" + c.FieldId));
        var baseline = document.Baseline.Rows.ToDictionary(r => r.Identity);
        foreach (var row in document.State.Rows)
        {
            var before = baseline.GetValueOrDefault(row.Identity);
            foreach (var key in row.ExtraValues.Keys)
                if (before is null || PlanValues.Get(before,key) != PlanValues.Get(row,key)) selected.Add(key);
            if (before is null ? row.Labels.Length > 0 : !row.Labels.ToHashSet().SetEquals(before.Labels)) selected.Add(PlanField.Labels);
        }
        return selected;
    }
    internal static PlanRemoteSnapshot PreserveUnread(PlanRemoteSnapshot snapshot, PlanDocument document, IReadOnlySet<string> selected)
    {
        var old = document.Baseline.Rows.ToDictionary(r => r.Identity);
        return snapshot with { Baseline = snapshot.Baseline with {
            Rows = snapshot.Baseline.Rows.Select(r => !old.TryGetValue(r.Identity, out var b) ? r : r with {
                ExtraValues = b.ExtraValues.SetItems(r.ExtraValues), Labels = selected.Contains(PlanField.Labels) ? r.Labels : b.Labels
            }).ToImmutableArray(),
            RepositoryLabels = selected.Contains(PlanField.Labels) ? snapshot.Baseline.RepositoryLabels : document.Baseline.RepositoryLabels
        } };
    }
    internal static string ReadDiagnostic(ProjectReadResult result) => $"{result.Outcome}: " + string.Join("; ",
        result.Problems.Select(p => $"{p.Stage}/{p.Kind}/{p.Failure}/{p.ApiOutcome}/{p.HttpStatus}"));
    internal static async Task<ProjectReadResult> ReadConsistentAsync(GhConnectionService service, GhConnectionService.OperationLease lease,
        ConnectionContext context, ScopedId project, CancellationToken token, IProgress<RemoteProgress>? progress = null, IReadOnlySet<string>? selectedColumns = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await new ProjectReader(service, lease, selectedColumns ?? PlanColumnMatching.Roles.Select(r => "Name:" + r.Name.ToUpperInvariant()).ToHashSet()).ReadAsync(context, project, token, progress).ConfigureAwait(false);
            if (result.Outcome == ProjectReadOutcome.Complete || attempt == 3 || result.Outcome != ProjectReadOutcome.Partial ||
                result.Problems.Count == 0 || result.Problems.Any(p => p.Kind != ReadProblemKind.ConcurrentChange)) return result;
            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        }
    }
    internal static PlanRemoteSnapshot From(ProjectReadResult result, ProjectPlanSettings settings, TimeZoneInfo? timeZone = null)
    {
        if (result.Outcome != ProjectReadOutcome.Complete || result.Project is not { FieldsComplete: true, ItemsComplete: true, MilestonesComplete: true } p)
            throw new InvalidOperationException("プロジェクト全体を取得できませんでした。計画は変更していません。再試行してください。 " + ReadDiagnostic(result));
        var statusField = p.Fields.SingleOrDefault(f => f.Name == "Status" && f.DataType == "SINGLE_SELECT" && f.ValueOwner == FieldOwner.ProjectItem);
        if (statusField is not null && statusField.Availability != ValueAvailability.Present) throw new InvalidOperationException("Status を読み取れません。");
        var rows = ImmutableArray.CreateBuilder<PlanRow>(); var items = ImmutableDictionary.CreateBuilder<string, string>();
        foreach (var item in p.Items.Where(i => i.Kind == ProjectItemKind.Issue && !i.IsArchived))
        {
            if (item.ContentId is null || !p.Issues.TryGetValue(item.ContentId, out var issue) || !item.ValuesComplete ||
                issue.Native is not { Complete: true } native || issue.Milestone.Availability is not (ValueAvailability.Empty or ValueAvailability.Present) || issue.Title.Availability != ValueAvailability.Present ||
                issue.State.Availability != ValueAvailability.Present ||
                issue.State.Value == IssueState.Closed && (issue.ClosedAt.Availability != ValueAvailability.Present || issue.ClosedAt.Value is null) || native.Parent.Availability is not (ValueAvailability.Present or ValueAvailability.Empty))
                throw new InvalidOperationException("Issue の取得が不完全です。");
            var row = new PlanRow(issue.Id.NodeId, issue.Title.Value!, issue.Repository.NameWithOwner)
            {
                MilestoneTitle = issue.Milestone.Value?.Title,
                Labels = (issue.Labels.Value ?? []).Select(l => l.Name).Order(StringComparer.Ordinal).ToImmutableArray(),
                Closed = issue.State.Value == IssueState.Closed,
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
                if (field is null || field.DataType != mapping.DataType || field.ValueOwner != FieldOwner.ProjectItem ||
                    field.Availability != ValueAvailability.Present) throw new InvalidOperationException("割り当てた列を読み取れません。");
                var value = item.Values.SingleOrDefault(v => v.FieldId?.NodeId == mapping.FieldId);
                if (value is not null && value.Availability is not (ValueAvailability.Present or ValueAvailability.Empty))
                    throw new InvalidOperationException("列の値を読み取れません。");
                var scalar = value?.Availability == ValueAvailability.Present ? value.Scalar : null;
                var json = mapping.Role switch
                {
                    PlanField.Estimate or PlanField.Remaining or PlanField.Actual => scalar is null ? "null" :
                        decimal.Parse(scalar, NumberStyles.Number, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                    PlanField.Start or PlanField.End or PlanField.StartNoEarlierThan => scalar is null ? "null" :
                        PlanJson.Text(DateOnly.ParseExact(scalar, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    PlanField.Phase => value?.Availability != ValueAvailability.Present ? "null" : PlanJson.Text(
                        field.Options.SingleOrDefault(o => o.Id == value.OptionId)?.Name ?? throw new InvalidOperationException("工程の選択肢を確認してください。")),
                    PlanField.Fixed => value?.OptionId is null ? "false" : field.Options.Single(o => o.Id == value.OptionId).Name == "固定" ? "true" :
                        throw new InvalidOperationException("日程固定の選択肢を確認してください。"),
                    _ => throw new InvalidOperationException("未対応の列割り当てです。")
                };
                row = PlanValues.Set(row, mapping.Role, json);
            }
            foreach (var key in p.SelectedColumns.Where(PlanSheetColumns.IsExtra))
            {
                var definition = p.Fields.SingleOrDefault(f => f.Id.NodeId == key[8..]);
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
        }
        return new(new(rows.ToImmutable(), p.Fields.Select(f => new PlanColumnDefinition(f.Id.NodeId, f.Name, f.DataType) { Options = f.Options.Select(o => o.Name).ToImmutableArray() }).ToImmutableArray()) {
                RepositoryMilestones = p.Milestones.Select(m => new PlanRepositoryOption(p.Issues.Values.First(i => i.Repository.Id == m.RepositoryId).Repository.NameWithOwner, m.Id.NodeId, m.Title)).ToImmutableArray(),
                RepositoryLabels = p.Labels.Select(l => new PlanRepositoryOption(p.Issues.Values.First(i => i.Repository.Id == l.RepositoryId).Repository.NameWithOwner, l.Id, l.Name)).ToImmutableArray(),
                Milestones = p.Milestones.GroupBy(m => m.Title, StringComparer.Ordinal).Select(g => new PlanMilestone(g.Key,
                    g.Max(m => m.DueDate), g.All(m => m.Closed), g.Select(m => m.DueDate).Distinct().Count() > 1)).ToImmutableArray() },
            items.ToImmutable(), p.Fields.ToImmutableArray(), p.Items.Count(i => i.Kind == ProjectItemKind.Draft), p.Items.Count(i => i.Kind == ProjectItemKind.PullRequest))
        { InaccessibleCount = p.UndeliveredItemCount + p.Items.Count(i => i.Kind == ProjectItemKind.Unavailable),
          IssueLinks = p.Issues.Values.ToImmutableDictionary(i => i.Id.NodeId, i => new PlanIssueLink($"{i.Repository.NameWithOwner}#{i.Number}", i.Url)),
          PeopleNames = p.Issues.Values.SelectMany(i => i.Native?.Assignees ?? []).DistinctBy(a => a.Id).ToImmutableDictionary(a => a.Id.NodeId, a => a.Login),
          SubIssueOrders = p.Issues.Values.Where(i => items.ContainsKey(i.Id.NodeId)).ToImmutableDictionary(i => i.Id.NodeId, i => i.Native!.SubIssues.Select(c => c.NodeId).ToImmutableArray()) };
    }
}
