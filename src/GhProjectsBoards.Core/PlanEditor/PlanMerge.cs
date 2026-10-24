using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GhProjectsBoards.Core.PlanEditor;

internal sealed record PlanConflict(string Identity, string Field, string? Baseline, string? Local, string? Remote)
{
    public string? DeletedFieldName { get; init; }
}
internal sealed record PlanPublishFailure(string Identity, string Field, string Reason)
{
    // The child order a failed sub-issue reorder asked for, so the retry can tell whether the plan
    // still wants that order.
    public string? Value { get; init; }
}
internal sealed record PlanIssueLink(string Caption, string Url);
internal sealed record PlanSync
{
    public ImmutableArray<string> Unverified { get; init; } = [];
    public ImmutableArray<PlanPublishFailure> Failures { get; init; } = [];
    public ImmutableDictionary<string, ImmutableArray<string>> NativeOrders { get; init; } = ImmutableDictionary<string, ImmutableArray<string>>.Empty;
    public ImmutableDictionary<string, ImmutableArray<string>> LocalOrderChoices { get; init; } = ImmutableDictionary<string, ImmutableArray<string>>.Empty;
    public ImmutableArray<PlanCreationStart> CreationStarts { get; init; } = [];
    public DateTimeOffset? NotBefore { get; init; }
    public PlanPublishProgress? Publish { get; init; }
    public ImmutableDictionary<string, string> PeopleNames { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, PlanIssueLink> IssueLinks { get; init; } = ImmutableDictionary<string, PlanIssueLink>.Empty;
    // #231: identities the last read observed without Issue update permission. A read observation, not a
    // plan value, so it disables commands and never becomes a difference, a write or a conflict.
    public ImmutableArray<string> CannotUpdate { get; init; } = [];
    public int InaccessibleCount { get; init; }
    public int DraftCount { get; init; }
    public int PullRequestCount { get; init; }
    public ImmutableArray<PlanConflict> Conflicts { get; init; } = [];
    public ImmutableArray<string> Unavailable { get; init; } = [];
}
internal static class PlanValues
{
    internal static string NormalizeNumber(string json)
        => decimal.TryParse(json, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("G29", CultureInfo.InvariantCulture) : json;
    internal static readonly string[] RowFields = PlanField.All.Where(f => f is not (PlanField.Order or PlanField.NewTask or PlanField.SubIssueOrder)).ToArray();
    internal static IEnumerable<string> Fields(params PlanRow[] rows) => RowFields.Concat(rows.SelectMany(r => r.ExtraValues.Keys)).Distinct();
    internal static string Get(PlanRow row, string field)
    {
        if (PlanSheetColumns.IsExtra(field)) return NormalizeNumber(row.ExtraValues.GetValueOrDefault(field)?.Json ?? "null");
        var property = typeof(PlanRow).GetProperty(field.ToString()) ?? throw new ArgumentException("Unknown row field.");
        var value = property.GetValue(row);
        if (field is PlanField.Assignees or PlanField.Predecessors or PlanField.Labels) value = ((ImmutableArray<string>)value!).Order(StringComparer.Ordinal).ToArray();
        return JsonSerializer.Serialize(value, PlanJson.Options);
    }
    internal static PlanRow Set(PlanRow row, string field, string json)
    {
        if (PlanSheetColumns.IsExtra(field)) return row with { ExtraValues = row.ExtraValues.SetItem(field, new(json == "null" ? Projects.ValueAvailability.Empty : Projects.ValueAvailability.Present, NormalizeNumber(json))) };
        var node = JsonNode.Parse(PlanJson.Text(row))!.AsObject();
        var name = JsonNamingPolicy.CamelCase.ConvertName(field.ToString());
        node[name] = JsonNode.Parse(json);
        return node.Deserialize<PlanRow>(PlanJson.Options)!;
    }
}
internal static class PlanMerge
{
    // #248: the edits this window made since its last stored save, applied onto the plan another writer
    // stored. Only the fields this window actually changed are taken, so the other writer's fields on the
    // same row survive; a row this window created is carried over whole and one it deleted stays deleted.
    internal static PlanState ReapplyLocalEdits(PlanState stored, PlanState saved, PlanState mine)
    {
        var savedRows = saved.Rows.ToDictionary(r => r.Identity);
        var mineIds = mine.Rows.Select(r => r.Identity).ToHashSet();
        var rows = stored.Rows.ToDictionary(r => r.Identity);
        foreach (var row in mine.Rows)
        {
            if (!savedRows.TryGetValue(row.Identity, out var before)) { rows[row.Identity] = row; continue; }
            if (!rows.TryGetValue(row.Identity, out var target)) continue;
            var applied = target;
            foreach (var field in PlanValues.RowFields)
                if (PlanValues.Get(row, field) != PlanValues.Get(before, field)) applied = PlanValues.Set(applied, field, PlanValues.Get(row, field));
            rows[row.Identity] = applied;
        }
        var storedOrder = stored.Rows.Select(r => r.Identity).Where(rows.ContainsKey).ToArray();
        var mineOrder = mine.Rows.Select(r => r.Identity).Where(rows.ContainsKey).ToArray();
        // The stored order stands unless this window reordered or inserted; rows it never saw keep their place.
        var order = mine.Rows.Select(r => r.Identity).SequenceEqual(saved.Rows.Select(r => r.Identity))
            ? storedOrder.Concat(mineOrder.Where(id => !storedOrder.Contains(id))).ToArray()
            : mineOrder.Concat(storedOrder.Where(id => !mineIds.Contains(id))).ToArray();
        var settings = PlanOperations.SettingsEqual(saved.Settings, mine.Settings) ? stored.Settings : mine.Settings;
        // Remembered archives are a GitHub observation; keep either window's memory of them.
        return new(order.Select(id => rows[id]).ToImmutableArray(), settings)
            { ArchivedIdentities = stored.ArchivedIdentities.Union(mine.ArchivedIdentities, StringComparer.Ordinal).ToImmutableArray() };
    }
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
        var unavailable = original.Sync.Unavailable;
        remote = remote.Where(pair => members.Contains(pair.Key)).ToImmutableDictionary();
        foreach (var (parent, remoteIds) in remote)
        {
            var prior = original.Sync.Conflicts.FirstOrDefault(c => c.Identity == parent && c.Field == PlanField.SubIssueOrder);
            var readIds = original.Sync.NativeOrders.GetValueOrDefault(parent, remoteIds);
            // Rows the read could not deliver are appended to the baseline, so they carry no displayed order.
            string Text(IEnumerable<string> ids) => JsonSerializer.Serialize(ids.Where(id => members.Contains(id) && !unavailable.Contains(id)));
            var b = Text(readIds);
            var p = Text(original.Baseline.Rows.Where(r => r.Parent == parent).Select(r => r.Identity));
            var l = Text(original.State.Rows.Where(r => r.Parent == parent).Select(r => r.Identity));
            var r = Text(remoteIds);
            // "Local" means the plan's child order moved away from the order the plan read. A difference
            // that already exists between the two GitHub orders is not a local change. A pending retry
            // or explicit local conflict choice still counts after the Project baseline has advanced.
            var localChange = l != p || PlanOperations.SiblingOrderChange(original, parent) is not null; var remoteChange = r != b;
            if (l != r && (prior is not null || localChange && remoteChange)) conflicts.Add(new(parent, PlanField.SubIssueOrder, prior?.Baseline ?? b, l, r));
            else if (l == b && r != b) state = SiblingOrder(state, parent, remoteIds);
        }
        return merged with { State = state, Sync = merged.Sync with { NativeOrders = remote, Conflicts = conflicts.ToImmutable(),
            LocalOrderChoices = merged.Sync.LocalOrderChoices.Where(p => members.Contains(p.Key) &&
                (!remote.TryGetValue(p.Key, out var observed) || !p.Value.SequenceEqual(observed.Where(id => members.Contains(id) && !merged.Sync.Unavailable.Contains(id))))).ToImmutableDictionary() } };
    }
    internal static PlanDocument Merge(PlanDocument document, PlanBaseline remote)
    {
        var baseline = document.Baseline.Rows.ToDictionary(r => r.Identity);
        var local = document.State.Rows.ToDictionary(r => r.Identity);
        var incoming = remote.Rows.ToDictionary(r => r.Identity);
        // #231: an Issue this plan archived and GitHub returned is re-adopted where the read has it;
        // #245's tail placement is only for Issues this plan meets for the first time.
        var returning = document.State.ArchivedIdentities.ToHashSet(StringComparer.Ordinal);
        var conflicts = new List<PlanConflict>();
        var unavailable = baseline.Keys.Where(id => !incoming.ContainsKey(id)).ToImmutableArray();
        foreach (var r in remote.Rows)
        {
            if (!baseline.TryGetValue(r.Identity, out var b)) { local[r.Identity] = r; continue; }
            var l = local[r.Identity];
            // Closed, Cancelled and CloseDate are GitHub-owned unless the PMO has a pending state change
            // (#231): the read must confirm that write instead of discarding it before it is published.
            var pendingState = l.Closed != b.Closed || l.Cancelled != b.Cancelled;
            var merged = l;
            if (!pendingState) merged = merged with { Closed = r.Closed, Cancelled = r.Cancelled, CloseDate = r.Closed ? r.CloseDate : null };
            else if (l.Closed && r.Closed) merged = merged with { CloseDate = r.CloseDate };
            // Date adoption needs the merged progress state, regardless of enum declaration order.
            foreach (var field in PlanValues.Fields(b, l, r).OrderBy(field => field is PlanField.Start or PlanField.End))
            {
                if (PlanSheetColumns.IsExtra(field) && !r.ExtraValues.ContainsKey(field)) continue;
                var bv = PlanValues.Get(b, field); var lv = PlanValues.Get(l, field); var rv = PlanValues.Get(r, field);
                var prior = document.Sync.Conflicts.FirstOrDefault(c => c.Identity == r.Identity && c.Field == field);
                if (prior is not null && lv != rv)
                    conflicts.Add(prior with { Local = lv, Remote = rv });
                else if (PlanOperations.NeedsProgressDate(l, merged, field)) merged = PlanValues.Set(merged, field, rv);
                else if (lv == bv || lv == rv) merged = PlanValues.Set(merged, field, rv);
                else if (rv != bv) conflicts.Add(new(r.Identity, field, bv, lv, rv));
            }
            local[r.Identity] = merged;
        }
        var removed = document.Baseline.Columns.Where(c => !remote.Columns.Any(r => r.Id == c.Id))
            .ToDictionary(c => "Project:" + c.Id, c => c.Name);
        foreach (var prior in document.Sync.Conflicts.Where(c => c.DeletedFieldName is not null))
            removed.TryAdd(prior.Field, prior.DeletedFieldName!);
        foreach (var row in document.State.Rows)
        {
            foreach (var (field, name) in removed)
            {
                var prior = document.Sync.Conflicts.SingleOrDefault(c => c.Identity == row.Identity && c.Field == field);
                var bv = baseline.TryGetValue(row.Identity, out var before) ? PlanValues.Get(before, field) : "null";
                var lv = PlanValues.Get(row, field);
                if (prior is not null || lv != bv)
                    conflicts.Add(new(row.Identity, field, prior?.Baseline ?? bv, prior?.DeletedFieldName is not null ? prior.Local : lv, "null") { DeletedFieldName = name });
            }
            local[row.Identity] = local[row.Identity] with { ExtraValues = local[row.Identity].ExtraValues.RemoveRange(removed.Keys) };
        }
        // Compare order only among identities present in all three snapshots; membership is merged separately.
        var localIds = document.State.Rows.Select(r => r.Identity).ToHashSet();
        var shared = baseline.Keys.Where(id => incoming.ContainsKey(id) && localIds.Contains(id)).ToHashSet();
        string[] Order(IEnumerable<PlanRow> rows) => rows.Where(r => shared.Contains(r.Identity)).Select(r => r.Identity).ToArray();
        var bo = Order(document.Baseline.Rows); var lo = Order(document.State.Rows); var ro = Order(remote.Rows);
        var localChanged = PlanOrder.Moved(bo, lo).Count > 0;
        var remoteChanged = PlanOrder.Moved(bo, ro).Count > 0;
        var ordersDiffer = PlanOrder.Moved(lo, ro).Count > 0;
        var priorOrder = document.Sync.Conflicts.FirstOrDefault(c => c.Field == PlanField.Order);
        var adoptOrder = !localChanged || !ordersDiffer;
        if (ordersDiffer && (priorOrder is not null || localChanged && remoteChanged))
        {
            adoptOrder = false;
            conflicts.Add(new("", PlanField.Order, priorOrder?.Baseline ?? JsonSerializer.Serialize(bo), JsonSerializer.Serialize(lo), JsonSerializer.Serialize(ro)));
        }
        var order = MergeOrder((adoptOrder ? remote.Rows : document.State.Rows).Select(r => r.Identity),
            (adoptOrder ? document.State.Rows : remote.Rows).Select(r => r.Identity));
        var added = remote.Rows.Where(r => !baseline.ContainsKey(r.Identity) && !localIds.Contains(r.Identity) && !returning.Contains(r.Identity)
            && r.Parent is { } parent && local.ContainsKey(parent)).Select(r => local[r.Identity]).ToImmutableArray();
        var rows = order.Select(id => local[id]).ToImmutableArray();
        if (!added.IsEmpty) {
            var ids = added.Select(r => r.Identity).ToHashSet();
            rows = new PlanOutline(rows.Where(r => !ids.Contains(r.Identity)).ToImmutableArray()).Import(added);
        }
        // Unavailable rows remain in the baseline solely to preserve their last known content and identity.
        return document with
        {
            Baseline = remote with { Rows = remote.Rows.AddRange(document.Baseline.Rows.Where(r => unavailable.Contains(r.Identity)))
                .Select(r => r with { ExtraValues = r.ExtraValues.RemoveRange(removed.Keys) }).ToImmutableArray() },
            State = document.State with { Rows = rows,
                // A remembered archive the read returned is adopted; the memory has done its job.
                ArchivedIdentities = document.State.ArchivedIdentities.Where(id => !incoming.ContainsKey(id)).ToImmutableArray() },
            Sync = document.Sync with { Conflicts = conflicts.ToImmutableArray(), Unavailable = unavailable }
        };
    }
}
