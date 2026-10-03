namespace GhProjectsBoards.Core.Projects;

internal enum WorkingDaySource { PersonalException, ProjectException, Regular, Weekend, Holiday }
internal sealed record EffectiveWorkingDay(DateOnly Date, WorkingInterval[] Intervals, WorkingDaySource Source, string? HolidayName = null);
internal sealed record CalendarStartExplanation(DateTime Boundary, DateTime AdoptedStart, string Controller,
    string? PredecessorId, bool NoRemainingInterval);
internal sealed record PlanningCalendarExplanation(long SourceRevision, string? PersonId, string PersonName,
    EffectiveWorkingDay[] Days, CalendarStartExplanation? Start)
{
    public static PlanningCalendarExplanation? Create(AdoptedPlan plan, string taskId)
    {
        if (plan.Configuration is not { } configuration || plan.Inputs is not { } inputs
            || configuration.ProjectId != plan.ProjectId) return null;
        var input = inputs.SingleOrDefault(i => i.Task.Id == taskId);
        var result = plan.Tasks.SingleOrDefault(t => t.Id == taskId);
        if (input is null || result is null || result.SourceRevision != plan.SourceRevision
            || result.Mode != input.Task.Mode) return null;

        var calendar = new WorkingCalendar(configuration.Calendar);
        var task = input.Task;
        var boundary = ForwardBoundary(plan, input, result);
        var dates = new[] { boundary.Time, result.Start, result.Finish }
            .Where(d => d is not null).Select(d => DateOnly.FromDateTime(d!.Value)).ToHashSet();
        if (dates.Count == 0) return null;
        // Only the validated forward start gap explains skipped working days.
        // Manual dates and an unrecognized controller must not invent that cause.
        if (boundary.Time is { } from && result.Start is { } to && to > from)
        {
            var first = DateOnly.FromDateTime(from);
            var last = DateOnly.FromDateTime(to);
            var gap = last.DayNumber - first.DayNumber;
            if (gap > 3660) return null;
            for (var offset = 1; offset < gap; offset++) dates.Add(first.AddDays(offset));
        }
        EffectiveWorkingDay[] days;
        try { days = dates.Order().Select(d => {
            var day = calendar.Explain(d, task.OwnerId);
            return day.Source == WorkingDaySource.Holiday
                ? day with { HolidayName = configuration.Calendar.Holidays.Dates.SingleOrDefault(h => h.Date == d)?.Name }
                : day;
        }).ToArray(); }
        catch (InvalidOperationException) { return null; }

        CalendarStartExplanation? start = null;
        if (boundary.Time is { } anchor && result.Start is { } adopted && adopted > anchor)
        {
            var day = days.Single(d => d.Date == DateOnly.FromDateTime(anchor));
            var noRemaining = !day.Intervals.Any(i => i.EndMinute > anchor.TimeOfDay.TotalMinutes);
            start = new(anchor, adopted, result.Controller!, boundary.Predecessor, noRemaining);
        }
        var person = task.OwnerId is null ? "共通カレンダー" : configuration.People.SingleOrDefault(p => p.Id == task.OwnerId)?.Name ?? task.OwnerId;
        return new(plan.SourceRevision, task.OwnerId, person, days, start);
    }

    private static (DateTime? Time, string? Predecessor) ForwardBoundary(AdoptedPlan plan, PlanningInput input, TaskPlan result)
    {
        var task = input.Task;
        if (result.Mode != PlanningMode.Auto || !result.Resolved || result.Problem is not null
            || result.Finish < result.Start || result.ScheduledMinutes is not > 0
            || task.Progress == PlanningProgress.Completed || task.FixedFinish is not null) return default;

        // Controller is written when the scheduler chooses the winning bound. A matching
        // endpoint alone cannot identify a cause, especially when several bounds coincide.
        if (result.Controller == "最早開始") return (task.EarliestStart, null);
        if (result.Controller == "固定開始") return (task.FixedStart, null);
        var link = input.Predecessors.SingleOrDefault(l => l.Kind == "FS" && result.Controller == "先行 " + l.PredecessorId);
        if (link is not null)
        {
            if (plan.Inputs!.Any(i => i.Task.Id == link.PredecessorId))
            {
                var predecessor = plan.Tasks.SingleOrDefault(t => t.Id == link.PredecessorId);
                return predecessor is not null && predecessor.SourceRevision == plan.SourceRevision && predecessor.Resolved
                    ? (predecessor.Finish, link.PredecessorId) : default;
            }
            return (link.ExternalFinish, link.PredecessorId);
        }

        var remaining = task.Progress is PlanningProgress.InProgress or PlanningProgress.Reopened;
        var expected = remaining ? "残工数・基準日時・配賦・カレンダー" : "見積工数・配賦・カレンダー";
        if (result.Controller != expected || plan.Configuration!.Start is not { } anchor) return default;
        if (remaining)
        {
            if (plan.Configuration.Cutoff is not { } cutoff) return default;
            if (cutoff > anchor) anchor = cutoff;
        }
        return (anchor, null);
    }
}
