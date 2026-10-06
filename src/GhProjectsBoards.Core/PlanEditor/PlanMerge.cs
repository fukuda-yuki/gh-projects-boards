using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GhProjectsBoards.Core.PlanEditor;

internal sealed record PlanConflict(string Identity, PlanField Field, string? Baseline, string? Local, string? Remote);
internal sealed record PlanPublishFailure(string Identity, PlanField Field, string Reason);
internal sealed record PlanSync
{
    public ImmutableArray<string> Unverified { get; init; } = [];
    public ImmutableArray<PlanPublishFailure> Failures { get; init; } = [];
    public ImmutableDictionary<string, ImmutableArray<string>> NativeOrders { get; init; } = ImmutableDictionary<string, ImmutableArray<string>>.Empty;
    public ImmutableArray<PlanCreationStart> CreationStarts { get; init; } = [];
    public DateTimeOffset? NotBefore { get; init; }
    public PlanPublishProgress? Publish { get; init; }
    public ImmutableDictionary<string, string> PeopleNames { get; init; } = ImmutableDictionary<string, string>.Empty;
    public int DraftCount { get; init; }
    public int PullRequestCount { get; init; }
    public ImmutableArray<PlanConflict> Conflicts { get; init; } = [];
    public ImmutableArray<string> Unavailable { get; init; } = [];
}
internal static class PlanValues
{
    internal static readonly PlanField[] RowFields = Enum.GetValues<PlanField>().Where(f => f is not (PlanField.Order or PlanField.NewTask or PlanField.SubIssueOrder)).ToArray();
    internal static string Get(PlanRow row, PlanField field)
    {
        var property = typeof(PlanRow).GetProperty(field.ToString()) ?? throw new ArgumentException("Unknown row field.");
        var value = property.GetValue(row);
        if (field is PlanField.Assignees or PlanField.Predecessors) value = ((ImmutableArray<string>)value!).Order(StringComparer.Ordinal).ToArray();
        return JsonSerializer.Serialize(value, PlanJson.Options);
    }
    internal static PlanRow Set(PlanRow row, PlanField field, string json)
    {
        var node = JsonNode.Parse(PlanJson.Text(row))!.AsObject();
        var name = JsonNamingPolicy.CamelCase.ConvertName(field.ToString());
        node[name] = JsonNode.Parse(json);
        return node.Deserialize<PlanRow>(PlanJson.Options)!;
    }
}
internal static class PlanMerge
{
    internal static string[] MergeOrder(IEnumerable<string> primary, IEnumerable<string> secondary)
    {
        var order = primary.ToList(); var anchors = order.ToHashSet(); var additions = secondary.ToArray();
        for (var i = 0; i < additions.Length; i++)
        {
            if (anchors.Contains(additions[i])) continue;
            var next = additions.Skip(i + 1).FirstOrDefault(anchors.Contains);
            if (next is null) order.Add(additions[i]); else order.Insert(order.IndexOf(next), additions[i]);
        }
        return order.ToArray();
    }
    internal static PlanState SiblingOrder(PlanState state, string parent, IEnumerable<string> order)
    {
        var children = state.Rows.Where(r => r.Parent == parent).ToDictionary(r => r.Identity);
        var wanted = order.Where(children.ContainsKey).Concat(children.Keys).Distinct().Select(id => children[id]).ToArray();
        var index = 0;
        return state with { Rows = state.Rows.Select(r => r.Parent == parent ? wanted[index++] : r).ToImmutableArray() };
    }
    internal static PlanDocument NativeOrder(PlanDocument original, PlanDocument merged, ImmutableDictionary<string, ImmutableArray<string>> remote)
    {
        var conflicts = merged.Sync.Conflicts.ToBuilder(); var state = merged.State;
        var members = state.Rows.Select(r => r.Identity).ToHashSet();
        remote = remote.Where(pair => members.Contains(pair.Key)).ToImmutableDictionary();
        foreach (var (parent, remoteIds) in remote)
        {
            var prior = original.Sync.Conflicts.FirstOrDefault(c => c.Identity == parent && c.Field == PlanField.SubIssueOrder);
            var baselineIds = original.Sync.NativeOrders.GetValueOrDefault(parent, remoteIds);
            string Text(IEnumerable<string> ids) => JsonSerializer.Serialize(ids.Where(members.Contains));
            var b = Text(baselineIds); var l = Text(original.State.Rows.Where(r => r.Parent == parent).Select(r => r.Identity)); var r = Text(remoteIds);
            if (l != r && (prior is not null || l != b && r != b)) conflicts.Add(new(parent, PlanField.SubIssueOrder, prior?.Baseline ?? b, l, r));
            else if (l == b && r != b) state = SiblingOrder(state, parent, remoteIds);
        }
        return merged with { State = state, Sync = merged.Sync with { NativeOrders = remote, Conflicts = conflicts.ToImmutable() } };
    }
    internal static PlanDocument Merge(PlanDocument document, PlanBaseline remote)
    {
        var baseline = document.Baseline.Rows.ToDictionary(r => r.Identity);
        var local = document.State.Rows.ToDictionary(r => r.Identity);
        var incoming = remote.Rows.ToDictionary(r => r.Identity);
        var conflicts = new List<PlanConflict>();
        var unavailable = baseline.Keys.Where(id => !incoming.ContainsKey(id)).ToImmutableArray();
        foreach (var r in remote.Rows)
        {
            if (!baseline.TryGetValue(r.Identity, out var b)) { local[r.Identity] = r; continue; }
            var l = local[r.Identity]; var merged = l;
            foreach (var field in PlanValues.RowFields)
            {
                if (PlanOperations.IsLocalConstraint(field, document.State.Settings)) continue;
                var bv = PlanValues.Get(b, field); var lv = PlanValues.Get(l, field); var rv = PlanValues.Get(r, field);
                var prior = document.Sync.Conflicts.FirstOrDefault(c => c.Identity == r.Identity && c.Field == field);
                if (prior is not null && lv != rv)
                    conflicts.Add(prior with { Local = lv, Remote = rv });
                else if (lv == bv || lv == rv) merged = PlanValues.Set(merged, field, rv);
                else if (rv != bv) conflicts.Add(new(r.Identity, field, bv, lv, rv));
            }
            local[r.Identity] = merged;
        }
        // Compare order only among identities present in all three snapshots; membership is merged separately.
        var shared = baseline.Keys.Where(incoming.ContainsKey).ToHashSet();
        string Order(IEnumerable<PlanRow> rows) => JsonSerializer.Serialize(rows.Where(r => shared.Contains(r.Identity)).Select(r => r.Identity));
        var bo = Order(document.Baseline.Rows); var lo = Order(document.State.Rows); var ro = Order(remote.Rows);
        var priorOrder = document.Sync.Conflicts.FirstOrDefault(c => c.Field == PlanField.Order);
        var adoptOrder = lo == bo || lo == ro;
        if (lo != ro && (priorOrder is not null || lo != bo && ro != bo))
        {
            adoptOrder = false;
            conflicts.Add(new("", PlanField.Order, priorOrder?.Baseline ?? bo, lo, ro));
        }
        var order = MergeOrder((adoptOrder ? remote.Rows : document.State.Rows).Select(r => r.Identity),
            (adoptOrder ? document.State.Rows : remote.Rows).Select(r => r.Identity));
        // Unavailable rows remain in the baseline solely to preserve their last known content and identity.
        return document with
        {
            Baseline = remote with { Rows = remote.Rows.AddRange(document.Baseline.Rows.Where(r => unavailable.Contains(r.Identity))) },
            State = document.State with { Rows = order.Select(id => local[id]).ToImmutableArray() },
            Sync = document.Sync with { Conflicts = conflicts.ToImmutableArray(), Unavailable = unavailable }
        };
    }
}
