using System.Collections.Immutable;
using System.Globalization;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.PlanEditor;

internal sealed record PlanRemoteSnapshot(PlanBaseline Baseline, ImmutableDictionary<string, string> Items,
    ImmutableArray<ProjectFieldDefinition> Fields, int DraftCount, int PullRequestCount)
{ public ImmutableDictionary<string, string> PeopleNames { get; init; } = ImmutableDictionary<string, string>.Empty;
  public ImmutableDictionary<string, PlanIssueLink> IssueLinks { get; init; } = ImmutableDictionary<string, PlanIssueLink>.Empty;
  public ImmutableDictionary<string, ImmutableArray<string>> SubIssueOrders { get; init; } = ImmutableDictionary<string, ImmutableArray<string>>.Empty; }
internal static class PlanSnapshot
{
    internal static string ReadDiagnostic(ProjectReadResult result) => $"{result.Outcome}: " + string.Join("; ",
        result.Problems.Select(p => $"{p.Stage}/{p.Kind}/{p.Failure}/{p.ApiOutcome}/{p.HttpStatus}"));
    internal static async Task<ProjectReadResult> ReadConsistentAsync(GhConnectionService service, GhConnectionService.OperationLease lease,
        ConnectionContext context, ScopedId project, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await new ProjectReader(service, lease).ReadAsync(context, project, token).ConfigureAwait(false);
            if (result.Outcome == ProjectReadOutcome.Complete || attempt == 3 || result.Outcome != ProjectReadOutcome.Partial ||
                result.Problems.Count == 0 || result.Problems.Any(p => p.Kind != ReadProblemKind.ConcurrentChange)) return result;
            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        }
    }
    internal static PlanRemoteSnapshot From(ProjectReadResult result, ProjectPlanSettings settings)
    {
        if (result.Outcome != ProjectReadOutcome.Complete || result.Project is not { FieldsComplete: true, ItemsComplete: true } p)
            throw new InvalidOperationException("プロジェクト全体を取得できませんでした。計画は変更していません。再試行してください。 " + ReadDiagnostic(result));
        var statusField = p.Fields.SingleOrDefault(f => f.Name == "Status" && f.DataType == "SINGLE_SELECT" && f.ValueOwner == FieldOwner.ProjectItem);
        if (statusField is not null && statusField.Availability != ValueAvailability.Present) throw new InvalidOperationException("Status を読み取れません。");
        var rows = ImmutableArray.CreateBuilder<PlanRow>(); var items = ImmutableDictionary.CreateBuilder<string, string>();
        foreach (var item in p.Items.Where(i => i.Kind == ProjectItemKind.Issue && !i.IsArchived))
        {
            if (item.ContentId is null || !p.Issues.TryGetValue(item.ContentId, out var issue) || !item.ValuesComplete ||
                issue.Native is not { Complete: true } native || issue.Title.Availability != ValueAvailability.Present ||
                issue.State.Availability != ValueAvailability.Present || native.Parent.Availability is not (ValueAvailability.Present or ValueAvailability.Empty))
                throw new InvalidOperationException("Issue の取得が不完全です。");
            var row = new PlanRow(issue.Id.NodeId, issue.Title.Value!, issue.Repository.NameWithOwner)
            {
                Closed = issue.State.Value == IssueState.Closed,
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
                    PlanField.Fixed => value?.OptionId is null ? "false" : field.Options.Single(o => o.Id == value.OptionId).Name == "固定" ? "true" :
                        throw new InvalidOperationException("日程固定の選択肢を確認してください。"),
                    _ => throw new InvalidOperationException("未対応の列割り当てです。")
                };
                row = PlanValues.Set(row, mapping.Role, json);
            }
            rows.Add(row); items.Add(row.Identity, item.Id.NodeId);
        }
        return new(new(rows.ToImmutable(), p.Fields.Select(f => new PlanColumnDefinition(f.Id.NodeId, f.Name, f.DataType)).ToImmutableArray()),
            items.ToImmutable(), p.Fields.ToImmutableArray(), p.Items.Count(i => i.Kind == ProjectItemKind.Draft), p.Items.Count(i => i.Kind == ProjectItemKind.PullRequest))
        { IssueLinks = p.Issues.Values.ToImmutableDictionary(i => i.Id.NodeId, i => new PlanIssueLink($"{i.Repository.NameWithOwner}#{i.Number}", i.Url)),
          PeopleNames = p.Issues.Values.SelectMany(i => i.Native?.Assignees ?? []).DistinctBy(a => a.Id).ToImmutableDictionary(a => a.Id.NodeId, a => a.Login),
          SubIssueOrders = p.Issues.Values.Where(i => items.ContainsKey(i.Id.NodeId)).ToImmutableDictionary(i => i.Id.NodeId, i => i.Native!.SubIssues.Select(c => c.NodeId).ToImmutableArray()) };
    }
}
