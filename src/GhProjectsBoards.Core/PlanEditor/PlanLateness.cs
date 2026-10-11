using System.Collections.Immutable;
using System.Globalization;

namespace GhProjectsBoards.Core.PlanEditor;

internal enum PlanLatenessLevel { None, Later, Overdue }
internal enum PlanOverdueKind { Start, Finish }
internal sealed record PlanTaskLateness(PlanLatenessLevel Level, PlanOverdueKind? OverdueKind,
    DateOnly? MissedPublishedDate, int? DaysLater, bool StartDelayedByPredecessor,
    int OverdueDescendantTasks = 0, int LaterDescendantTasks = 0);
internal sealed record PlanLatenessResult(ImmutableDictionary<string, PlanTaskLateness> Tasks,
    int OverdueTasks, int LaterTasks);

internal static class PlanLateness
{
    internal static PlanLatenessResult Classify(IReadOnlyList<ScheduledTask> tasks, PlanBaseline baseline,
        PlanCalendar calendar, DateOnly statusDate)
    {
        var published = baseline.Rows.ToDictionary(row => row.Identity, StringComparer.Ordinal);
        var scheduled = tasks.ToDictionary(row => row.Input.Identity, StringComparer.Ordinal);
        var result = new Dictionary<string, PlanTaskLateness>(StringComparer.Ordinal);
        var childrenLeft = tasks.ToDictionary(row => row.Input.Identity, _ => 0, StringComparer.Ordinal);
        var overdueTasks = 0;
        var laterTasks = 0;
        foreach (var row in tasks)
        {
            var input = row.Input;
            if (input.Parent is { } parent && childrenLeft.ContainsKey(parent)) childrenLeft[parent]++;
            var old = published.GetValueOrDefault(input.Identity);
            var days = PlanScheduler.PublishedEndLateness(old?.End, row.End.Value, calendar);
            PlanOverdueKind? kind = null;
            DateOnly? missed = null;
            if (!row.IsSummary && !input.IsComplete)
            {
                if (old?.End < statusDate) { kind = PlanOverdueKind.Finish; missed = old.End; }
                else if (input.Actual is null or 0 && old?.Start < statusDate) { kind = PlanOverdueKind.Start; missed = old.Start; }
            }
            var level = kind is not null ? PlanLatenessLevel.Overdue : days.HasValue ? PlanLatenessLevel.Later : PlanLatenessLevel.None;
            var predecessorDelay = row.Start.Value > old?.Start && row.StartReason.EndsWith(" の終了後", StringComparison.Ordinal);
            result[input.Identity] = new(level, kind, missed, days, predecessorDelay);
            if (!row.IsSummary)
            {
                if (level == PlanLatenessLevel.Overdue) overdueTasks++;
                else if (level == PlanLatenessLevel.Later) laterTasks++;
            }
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
                OverdueDescendantTasks = ancestor.OverdueDescendantTasks + (row.IsSummary ? child.OverdueDescendantTasks : child.Level == PlanLatenessLevel.Overdue ? 1 : 0),
                LaterDescendantTasks = ancestor.LaterDescendantTasks + (row.IsSummary ? child.LaterDescendantTasks : child.Level == PlanLatenessLevel.Later ? 1 : 0)
            };
            if (--childrenLeft[parent] == 0) ready.Enqueue(parent);
        }
        return new(result.ToImmutableDictionary(StringComparer.Ordinal), overdueTasks, laterTasks);
    }

    internal static string? EndReason(ScheduledTask task, DateOnly statusDate)
    {
        if (task.IsSummary || task.Input.IsComplete || task.StartReason == "入力エラー") return null;
        var work = task.Remaining;
        if (work == 0) return null;
        if (task.End.Origin == DateOrigin.Kept) return task.End.Value.HasValue ? "終了: 指定" : null;
        if (!(work > 0)) return null;
        var hours = work.Value.ToString("0.############################", CultureInfo.InvariantCulture);
        return task.Input.Actual > 0
            ? $"終了: 状況日 {statusDate.ToString("M/d", CultureInfo.InvariantCulture)} から残り {hours}h"
            : $"終了: 開始から {hours}h";
    }
}
