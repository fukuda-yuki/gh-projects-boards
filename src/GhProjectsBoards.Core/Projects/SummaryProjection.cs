namespace GhProjectsBoards.Core.Projects;

internal sealed record EffortValue(decimal Hours, int Unknown = 0, int Stale = 0, int Known = 1)
{
    public bool Complete => Unknown == 0 && Stale == 0;
    public decimal Days => Hours / 8m;
    public static EffortValue Missing => new(0, 1, 0, 0);
    public static EffortValue Of(decimal? hours) => hours is { } h ? new(h) : Missing;
    public static EffortValue Sum(IEnumerable<EffortValue> values) => values.Aggregate(new EffortValue(0, Known: 0),
        (a, b) => new(a.Hours + b.Hours, a.Unknown + b.Unknown, a.Stale + b.Stale, a.Known + b.Known));
}
internal sealed record PersonSummary(string Id, string Name, decimal? Allowance, EffortValue Estimate,
    EffortValue Actual, EffortValue Remaining, EffortValue Forecast)
{
    public decimal? Headroom => Allowance is { } allowance && Forecast.Complete ? allowance - Forecast.Hours : null;
}
internal sealed record SummaryContribution(string PersonId, string TaskId, string? RowId, string Title, string Identity,
    EffortValue Estimate, EffortValue Actual, EffortValue Remaining, DateOnly? ReportedThrough, string? Problem, bool IncludedInTotals = true)
{
    public EffortValue Forecast => EffortValue.Sum([Actual, Remaining]);
}
internal sealed record BaselineComparison(BaselineTask? Baseline, GanttRow? Current, string State)
{
    public string TaskId => Baseline?.TaskId ?? Current!.TaskId;
}
internal sealed record SummaryProjection(PersonSummary[] People, EffortValue Estimate, EffortValue Actual,
    SummaryContribution[] Contributions, BaselineComparison[] Comparisons, DateOnly Today, DateOnly Cutoff,
    string ProjectTitle, int TaskCount, int UnpublishedCount, ProtectedBaseline? Baseline,
    IReadOnlyDictionary<string, string>? TaskIdsByRow = null)
{
    internal const string Unattributed = "";
    public static SummaryProjection Create(EditingWorkspace work, ProjectRegistration project, DateOnly today)
    {
        var p = work.Planning(project.Snapshot.Id.NodeId);
        var adopted = GanttProjection.Create(work, project, []);
        var rows = adopted.Rows.GroupBy(r => r.TaskId).Select(group => group.OrderBy(r => r.RowId, StringComparer.Ordinal).First()).ToArray();
        var cutoff = p?.Cutoff is { } at && DateOnly.FromDateTime(at) < today ? DateOnly.FromDateTime(at) : today;
        var parents = project.Snapshot.Issues.Values.Select(i => i.Native?.Parent.Value?.NodeId).OfType<string>().ToHashSet();
        var contributions = new List<SummaryContribution>(); var estimates = new List<EffortValue>(); var actuals = new List<EffortValue>();
        var metadata = (p?.Tasks ?? []).ToDictionary(t => t.Id);
        void Add(GanttRow? row, PlanningTask task)
        {
            var id = task.Id;
            var identity = row?.Identity ?? id; var title = row?.Title ?? id;
            var unavailable = row is null || row.Input is null;
            var ambiguous = task.LaborKind == TaskLaborKind.Unspecified && parents.Contains(id);
            var rollup = task.LaborKind == TaskLaborKind.Rollup;
            var reason = unavailable ? "対象・工数を未確認" : ambiguous ? "親タスクの工数区分を確認" : rollup ? "子の集計（工数合計から除外）" : null;
            if (row?.Input?.SourceProblem is { } sourceProblem) reason = Join(reason, sourceProblem);
            var estimate = row?.Input?.Estimate; var remaining = row?.Input?.Remaining;
            var future = (task.Actuals ?? []).Any(a => a.ReportedThrough > cutoff);
            if (future) reason = Join(reason, "基準日より後の実績は未算入");
            if (task.Actuals is not { Length: > 0 }) reason = Join(reason, row?.Input?.ActualTotal is { } raw
                ? $"日付・帰属未確認の実績 {raw}人時" : "実績未入力");
            var shares = task.Contributions ?? [];
            Dictionary<string, EffortValue> Allocate(decimal? total, Func<WorkContribution, decimal?> operand)
            {
                if (shares.Length == 0) return new() { [task.OwnerId ?? Unattributed] = EffortValue.Of(total) };
                var result = shares.ToDictionary(s => s.PersonId, s => EffortValue.Of(operand(s)));
                var known = shares.Sum(s => operand(s) ?? 0);
                // A balance is explicit unallocated work. Nullable shares are never silently filled.
                if (total is null || total > known) result[Unattributed] = total is { } h ? new(h - known) : EffortValue.Missing;
                if (total < known) { result[Unattributed] = EffortValue.Missing; reason = Join(reason, "内訳が工数を超過。再確認が必要"); }
                return result;
            }
            var est = Allocate(estimate, s => s.EstimateHours); var rem = Allocate(remaining, s => s.RemainingHours);
            var reports = (task.Actuals ?? []).ToDictionary(a => a.PersonId ?? Unattributed);
            var people = est.Keys.Concat(rem.Keys).Concat(reports.Keys).Distinct().ToArray();
            var actualValues = new List<EffortValue>();
            foreach (var person in people)
            {
                reports.TryGetValue(person, out var report);
                var actual = report is null ? (est.ContainsKey(person) || rem.ContainsKey(person) ? EffortValue.Missing : new(0))
                    : report.ReportedThrough > cutoff ? EffortValue.Missing : new EffortValue(report.Hours, Stale: report.ReportedThrough < today ? 1 : 0);
                actualValues.Add(actual);
                var ev = est.GetValueOrDefault(person) ?? new(0); var rv = rem.GetValueOrDefault(person) ?? new(0);
                if (ambiguous || unavailable) { ev = EffortValue.Missing; rv = EffortValue.Missing; }
                if (ambiguous) actual = EffortValue.Missing;
                contributions.Add(new(person, id, row?.RowId, title, identity, ev, actual, rv, report?.ReportedThrough, reason, !rollup));
            }
            if (rollup) return;
            estimates.Add(ambiguous || unavailable ? EffortValue.Missing : EffortValue.Of(estimate));
            actuals.Add(ambiguous ? EffortValue.Missing : task.Actuals is not { Length: > 0 } ? EffortValue.Missing : EffortValue.Sum(actualValues));
        }
        foreach (var row in rows) Add(row, row.Input?.Task ?? metadata.GetValueOrDefault(row.TaskId) ?? new(row.TaskId));
        // A disappeared remote task is not proven removed; retain historical workers and incomplete scope.
        var rowIds = rows.Select(r => r.TaskId).ToHashSet();
        foreach (var task in metadata.Values.Where(t => !rowIds.Contains(t.Id) && !t.Id.StartsWith("local-", StringComparison.Ordinal))) Add(null, task);
        var configured = (p?.People ?? []).ToDictionary(p => p.Id, p => p.Name);
        var allowances = (p?.Summary?.Allowances ?? []).ToDictionary(a => a.PersonId, a => a.Hours);
        var groups = contributions.ToLookup(c => c.PersonId);
        var peopleRows = groups.Select(g => g.Key).Concat(configured.Keys).Concat(allowances.Keys).Distinct().Select(id => {
            var items = groups[id].Where(c => c.IncludedInTotals).ToArray();
            var e = EffortValue.Sum(items.Select(i => i.Estimate)); var a = EffortValue.Sum(items.Select(i => i.Actual)); var r = EffortValue.Sum(items.Select(i => i.Remaining));
            return new PersonSummary(id, id == Unattributed ? "未割当・帰属未確認" : configured.GetValueOrDefault(id) ?? id,
                allowances.TryGetValue(id, out var allowance) ? allowance : null, e, a, r, EffortValue.Sum([a, r]));
        }).OrderBy(p => p.Id == Unattributed).ThenBy(p => p.Name, StringComparer.CurrentCulture).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
        var baseline = p?.Summary?.Baseline;
        var current = rows.ToDictionary(r => r.TaskId);
        var old = (baseline?.Tasks ?? []).ToDictionary(t => t.TaskId);
        var comparison = current.Keys.Concat(old.Keys).Distinct().Select(id => {
            var b = old.GetValueOrDefault(id); var c = current.GetValueOrDefault(id);
            var state = b is null ? "基準なし（追加）" : c is null ? id.StartsWith("local-", StringComparison.Ordinal) ? "ローカル範囲から削除" : "現在の範囲を未確認"
                : c.Input is null ? "現在の対象を未確認" : "基準と現在";
            return new BaselineComparison(b, c, state);
        }).ToArray();
        return new(peopleRows, EffortValue.Sum(estimates), EffortValue.Sum(actuals), contributions.ToArray(), comparison, today, cutoff,
            project.Snapshot.Title, rows.Length, rows.Count(r => r.RowId.StartsWith("local-", StringComparison.Ordinal)), baseline,
            adopted.Rows.ToDictionary(r => r.RowId, r => r.TaskId));
    }
    private static string Join(string? first, string second) => first is null ? second : first + " / " + second;
}
