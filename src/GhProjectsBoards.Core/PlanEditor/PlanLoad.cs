namespace GhProjectsBoards.Core.PlanEditor;

// #230: while reassigning, the plan's selection line names the selected task's assignee load for the
// weeks the task occupies, so the PMO sees an assignee change's effect without opening サマリー.
internal sealed record PlanLoadPeriod(DateOnly Start, DateOnly End, decimal? Percent, int DaysOverCap,
    decimal? MaxDailyPercent, DateOnly? MaxDate)
{
    public bool Overloaded => DaysOverCap > 0;
}
internal sealed record PlanAssigneeLoad(string Identity, string Name, IReadOnlyList<PlanLoadPeriod> Periods);

internal static class PlanLoad
{
    // The weeks are the ones the task's own planned allocation occupies, so a task waiting after a
    // predecessor names the weeks it actually loads. A task without a single assignee, without planned
    // work or already closed has no load to show, and an unallocated task has no place for it.
    internal static PlanAssigneeLoad? ForTask(PlanDocument document, IReadOnlyList<ScheduledTask> schedule, string taskIdentity)
    {
        var task = schedule.FirstOrDefault(t => t.Input.Identity == taskIdentity && !t.IsSummary);
        // A closed task loads nobody's future, and work with no or unknown remaining hours has no load
        // to name: the band must not say 0% for work that is merely unestimated.
        if (task is null || task.Input.Closed || task.Input.Assignees.Length != 1 || task.Remaining is not > 0) return null;
        var assignee = task.Input.Assignees[0];
        var planned = task.PlannedHours?.Where(day => day.Value > 0).Select(day => day.Key).ToArray() ?? [];
        DateOnly first, last;
        if (planned.Length > 0) { first = planned.Min(); last = planned.Max(); }
        else if (task.Start.Value is { } start && task.End.Value is { } end) { first = start; last = end; }
        else return null;
        var weekStart = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        var weeks = (last.DayNumber - weekStart.DayNumber) / 7 + 1;
        var plan = PlanPeople.Calculate(document, schedule, weekStart, PlanPeriodScale.Week, weeks);
        var person = plan.People.FirstOrDefault(p => p.Identity == assignee);
        if (person is null) return null;
        return new(assignee, person.Name, person.Periods.Select((period, index) => new PlanLoadPeriod(
            plan.Periods[index].Start, plan.Periods[index].End,
            period.Percent, period.DailyOverloads.Count, period.MaxDailyPercent, period.MaxDailyDate)).ToArray());
    }
}
