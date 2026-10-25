using System.Collections.Immutable;
using System.Globalization;

namespace GhProjectsBoards.Core.PlanEditor;

internal enum PlanLatenessLevel { None, Later, Overdue }
internal enum PlanOverdueKind { Start, Finish }
// Why a forecast end is later than the planned end (#236). 遅れの原因 contains 期限超過 and the tasks
// late through their own remaining work; a task late only because a predecessor moved is not a cause.
internal enum PlanLateCause { OwnRemaining, Predecessor }
internal sealed record PlanTaskLateness(PlanLatenessLevel Level, PlanOverdueKind? OverdueKind,
    DateOnly? MissedDate, int? DaysLater, bool OverEstimate, PlanLateCause? Cause, int? CausePredecessorRowId,
    int OverdueDescendantTasks = 0, int LaterDescendantTasks = 0, int OverEstimateDescendantTasks = 0)
{
    public bool IsCause => Level == PlanLatenessLevel.Overdue || Cause == PlanLateCause.OwnRemaining;
    public bool StartDelayedByPredecessor => Cause == PlanLateCause.Predecessor;
    // #260: one 遅れ set for the strip, the chase list and the band; the mark table still splits the causes.
    public bool IsLate => Level != PlanLatenessLevel.None;
    // Late only because a predecessor's forecast pushed it: the delay is not this task's own.
    public bool OnlyPushed => Level == PlanLatenessLevel.Later && OverdueKind is null && Cause == PlanLateCause.Predecessor;
}
internal sealed record PlanLatenessResult(ImmutableDictionary<string, PlanTaskLateness> Tasks,
    int OverdueTasks, int LaterTasks, int OverEstimateTasks, int CauseTasks)
{
    public static readonly PlanLatenessResult Empty = new(ImmutableDictionary<string, PlanTaskLateness>.Empty.WithComparers(StringComparer.Ordinal),
        0, 0, 0, 0) { Pushed = ImmutableDictionary<string, ImmutableArray<string>>.Empty.WithComparers(StringComparer.Ordinal) };
    // One 遅れ count over the mark table's two causes (#260 D0).
    internal int LateTasks => OverdueTasks + LaterTasks;
    // The late successors this task pushed directly (#260 後続を表示), one step.
    public ImmutableDictionary<string, ImmutableArray<string>> Pushed { get; init; } =
        ImmutableDictionary<string, ImmutableArray<string>>.Empty.WithComparers(StringComparer.Ordinal);
    // Every late task this task delays through the predecessor chain, in discovery order.
    internal ImmutableArray<string> DelayedSuccessors(string identity)
    {
        var found = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { identity };
        var queue = new Queue<string>([identity]);
        while (queue.TryDequeue(out var id))
            foreach (var next in Pushed.GetValueOrDefault(id, []))
                if (seen.Add(next)) { found.Add(next); queue.Enqueue(next); }
        return found.ToImmutable();
    }
}

internal static class PlanLateness
{
    // Lateness compares the plan with the 基準日 and the plan with the 見込終了 forecast (#236). It never
    // compares against a previous publish: publishing only records what the plan already says.
    internal static PlanLatenessResult Classify(IReadOnlyList<ScheduledTask> tasks, PlanCalendar calendar, DateOnly statusDate)
    {
        var scheduled = tasks.ToDictionary(row => row.Input.Identity, StringComparer.Ordinal);
        var byRowId = tasks.ToDictionary(row => row.Input.RowId, row => row.Input.Identity);
        var pushed = new Dictionary<string, ImmutableArray<string>.Builder>(StringComparer.Ordinal);
        var result = new Dictionary<string, PlanTaskLateness>(StringComparer.Ordinal);
        var childrenLeft = tasks.ToDictionary(row => row.Input.Identity, _ => 0, StringComparer.Ordinal);
        var overdueTasks = 0;
        var laterTasks = 0;
        var overEstimateTasks = 0;
        var causeTasks = 0;
        foreach (var row in tasks)
        {
            var input = row.Input;
            if (input.Parent is { } parent && childrenLeft.ContainsKey(parent)) childrenLeft[parent]++;
            PlanOverdueKind? kind = null;
            DateOnly? missed = null;
            if (!row.IsSummary && !input.IsComplete)
            {
                if (row.End.Value is { } plannedEnd && plannedEnd < statusDate) { kind = PlanOverdueKind.Finish; missed = plannedEnd; }
                else if (input.Actual is null or 0 && row.Start.Value is { } plannedStart && plannedStart < statusDate) { kind = PlanOverdueKind.Start; missed = plannedStart; }
            }
            var days = PlanScheduler.WorkingDaysBetween(row.End.Value, row.ForecastEnd, calendar);
            var cause = days is null ? (PlanLateCause?)null : row.ForecastStartReason?.EndsWith(" の終了後", StringComparison.Ordinal) == true
                ? PlanLateCause.Predecessor : PlanLateCause.OwnRemaining;
            var level = kind is not null ? PlanLatenessLevel.Overdue : days.HasValue ? PlanLatenessLevel.Later : PlanLatenessLevel.None;
            var predecessor = cause == PlanLateCause.Predecessor ? PredecessorRowId(row, scheduled) : null;
            result[input.Identity] = new(level, kind, missed, days, row.IsOverEstimate, cause, predecessor);
            if (!row.IsSummary)
            {
                if (level == PlanLatenessLevel.Overdue) overdueTasks++;
                else if (level == PlanLatenessLevel.Later) laterTasks++;
                if (row.IsOverEstimate) overEstimateTasks++;
                if (result[input.Identity].IsCause) causeTasks++;
            }
            if (predecessor is { } by && byRowId.TryGetValue(by, out var pusher))
                (pushed.TryGetValue(pusher, out var list) ? list : pushed[pusher] = ImmutableArray.CreateBuilder<string>()).Add(input.Identity);
        }

        // Process each hierarchy edge once, regardless of row order or folding.
        // Summary levels propagate, but only leaf levels contribute to counts.
        var ready = new Queue<string>(childrenLeft.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        while (ready.TryDequeue(out var id))
        {
            var row = scheduled[id];
            if (row.Input.Parent is not { } parent || !scheduled.ContainsKey(parent)) continue;
            var child = result[id];
            var ancestor = result[parent];
            result[parent] = ancestor with
            {
                Level = (PlanLatenessLevel)Math.Max((int)ancestor.Level, (int)child.Level),
                OverdueDescendantTasks = ancestor.OverdueDescendantTasks + Count(row.IsSummary, child.OverdueDescendantTasks, child.Level == PlanLatenessLevel.Overdue),
                LaterDescendantTasks = ancestor.LaterDescendantTasks + Count(row.IsSummary, child.LaterDescendantTasks, child.Level == PlanLatenessLevel.Later),
                OverEstimateDescendantTasks = ancestor.OverEstimateDescendantTasks + Count(row.IsSummary, child.OverEstimateDescendantTasks, child.OverEstimate)
            };
            if (--childrenLeft[parent] == 0) ready.Enqueue(parent);
        }
        return new(result.ToImmutableDictionary(StringComparer.Ordinal), overdueTasks, laterTasks, overEstimateTasks, causeTasks)
        {
            Pushed = pushed.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutable(), StringComparer.Ordinal)
        };

        static int Count(bool summary, int descendants, bool itself) => summary ? descendants : itself ? 1 : 0;
    }

    // The predecessor whose forecast end pushed this task's forecast start. The scheduler flattens a
    // summary's predecessors into its descendants, so an inherited push may name no direct predecessor.
    private static int? PredecessorRowId(ScheduledTask row, IReadOnlyDictionary<string, ScheduledTask> scheduled)
        => row.Input.Predecessors.Where(scheduled.ContainsKey).Select(id => scheduled[id]).Where(p => p.ForecastEnd is not null)
            .OrderByDescending(p => p.ForecastEnd!.Value.DayNumber).ThenBy(p => p.Input.RowId).Select(p => (int?)p.Input.RowId).FirstOrDefault();

    internal static string? EndReason(ScheduledTask task)
    {
        if (task.IsSummary || task.Input.IsComplete || task.StartReason == "入力エラー") return null;
        var remaining = task.Remaining;
        if (remaining == 0) return null;
        if (task.End.Origin == DateOrigin.Kept && task.End.Value == task.Input.End)
            return task.End.Value.HasValue ? "終了: 指定" : null;
        if (!(remaining > 0)) return null;
        // The plan consumes the task from its start: actual work plus what is left (#236).
        var hours = remaining.Value + (task.Input.Actual ?? 0);
        return $"終了: 開始から {hours.ToString("0.############################", CultureInfo.InvariantCulture)}h";
    }

    // The descendants this task's own lateness pushed later (#236 part B): the identity set behind 後続を表示.
    internal static ImmutableArray<string> LaterDescendants(IReadOnlyList<ScheduledTask> tasks, string identity, PlanLatenessResult result)
    {
        var children = tasks.Where(t => t.Input.Parent is not null).GroupBy(t => t.Input.Parent!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Input.Identity).ToArray(), StringComparer.Ordinal);
        var found = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { identity };
        var queue = new Queue<string>();
        queue.Enqueue(identity);
        while (queue.Count > 0)
            foreach (var child in children.GetValueOrDefault(queue.Dequeue()) ?? [])
                if (seen.Add(child))
                {
                    queue.Enqueue(child);
                    if (result.Tasks.TryGetValue(child, out var late) && late.Level == PlanLatenessLevel.Later) found.Add(child);
                }
        return found.ToImmutable();
    }
}
