namespace GhProjectsBoards.Core.Projects;

internal sealed record PlanningInput(PlanningTask Task, decimal? Estimate, decimal? Remaining,
    string[] Assignees, PlanningLink[] Predecessors, bool RelationshipsComplete = true, bool? RemoteClosed = null);
internal sealed record TaskPlan(string Id, PlanningMode Mode, long SourceRevision, decimal? RawHours,
    decimal? ScheduledMinutes, DateTime? Start, DateTime? Finish, DateTime? SuggestedStart, DateTime? SuggestedFinish,
    string? Problem, string[] Warnings, string? Controller)
{
    public bool Resolved => Start is not null && Finish is not null;
}
internal sealed record AdoptedPlan(string ProjectId, long SourceRevision, TaskPlan[] Tasks);

internal sealed class WorkingCalendar(PlanningCalendar calendar)
{
    private readonly HashSet<DateOnly> holidays = calendar.Holidays.Dates.Select(d => d.Date).ToHashSet();
    private readonly Dictionary<(DateOnly, string?), WorkingInterval[]> exceptions = calendar.Exceptions.ToDictionary(e => (e.Date, e.PersonId), e => e.Intervals);
    private static readonly WorkingInterval[] Regular = [new(540, 780), new(840, 1080)];
    public WorkingInterval[] Intervals(DateOnly day, string? person)
    {
        if (person is not null && exceptions.TryGetValue((day, person), out var own)) return own;
        if (exceptions.TryGetValue((day, null), out var common)) return common;
        if (!calendar.HolidaysNotConsidered && (day.Year < calendar.Holidays.FirstYear || day.Year > calendar.Holidays.LastYear))
            throw new InvalidOperationException($"{day.Year}年の祝日を採用していません。例外日または祝日を考慮しない設定が必要です。");
        return day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || !calendar.HolidaysNotConsidered && holidays.Contains(day) ? [] : Regular;
    }
    public (DateTime Start, DateTime Finish) Place(DateTime anchor, decimal minutes, string? person)
    {
        if (minutes == 0) return (anchor, anchor);
        var remaining = decimal.Ceiling(minutes); DateTime? start = null; var date = DateOnly.FromDateTime(anchor);
        // A finite planning horizon also bounds calendars with no future capacity.
        for (var days = 0; days < 3660; days++, date = date.AddDays(1))
        {
            foreach (var span in Intervals(date, person))
            {
                var beginning = date.ToDateTime(TimeOnly.MinValue).AddMinutes(span.StartMinute);
                var end = date.ToDateTime(TimeOnly.MinValue).AddMinutes(span.EndMinute);
                if (beginning < anchor) beginning = anchor;
                if (beginning >= end) continue;
                start ??= beginning;
                var available = (decimal)(end - beginning).TotalMinutes;
                if (remaining <= available) return (start.Value, beginning.AddMinutes((double)remaining));
                remaining -= available;
            }
        }
        throw new InvalidOperationException("10年の計画範囲内に必要な稼働時間がありません。");
    }
}

internal static class PlanningEngine
{
    public static AdoptedPlan Calculate(ProjectPlanning project, PlanningInput[] inputs, long revision)
    {
        var calendar = new WorkingCalendar(project.Calendar);
        var people = project.People.ToDictionary(p => p.Id);
        var results = new List<TaskPlan>(inputs.Length);
        foreach (var input in inputs)
        {
            var task = input.Task; var warnings = new List<string>(); string? problem = null;
            var work = task.Progress is PlanningProgress.InProgress or PlanningProgress.Reopened ? input.Remaining : input.Estimate;
            decimal? minutes = null; DateTime? start = null, finish = null;
            try
            {
                if (task.Mode == PlanningMode.Unplanned) throw new InvalidOperationException("Auto または Manual を選んでください。");
                if (project.Start is null) throw new InvalidOperationException("Project開始日時が必要です。");
                if (work is null) throw new InvalidOperationException(task.Progress is PlanningProgress.InProgress or PlanningProgress.Reopened ? "残工数が未入力です。" : "見積工数が未入力です。");
                decimal weight;
                if (task.OwnerId is null)
                {
                    if (input.Assignees.Length != 0) throw new InvalidOperationException("計画担当者を明示的に選んでください。");
                    weight = 100; warnings.Add("担当未設定・共通カレンダー100%の暫定計画");
                }
                else if (!people.TryGetValue(task.OwnerId, out var owner)) throw new InvalidOperationException("計画担当者の配賦が未設定です。");
                else weight = owner.WeightPercent;
                if (work > 0 && weight == 0) throw new InvalidOperationException("配賦0%のため自動終了を計算できません。");
                minutes = work == 0 ? 0 : work * 6000 / weight;
                if (minutes != decimal.Ceiling(minutes.Value)) warnings.Add($"終了境界を{decimal.Ceiling(minutes.Value) - minutes:0.########}分切り上げ（工数は保持）");
                var anchor = project.Start.Value;
                if (task.Progress is PlanningProgress.InProgress or PlanningProgress.Reopened)
                {
                    if (project.Cutoff is null) throw new InvalidOperationException("再計画の基準日時が必要です。");
                    if (anchor < project.Cutoff) anchor = project.Cutoff.Value;
                }
                (start, finish) = calendar.Place(anchor, minutes.Value, task.OwnerId);
            }
            catch (InvalidOperationException e) { problem = e.Message; }
            if (task.Mode == PlanningMode.Manual)
            {
                if (problem is not null) warnings.Add(problem);
                if (task.ManualStart != start || task.ManualFinish != finish) warnings.Add("Manual日時を保持。自動案と異なります。");
            }
            results.Add(new(task.Id, task.Mode, revision, work, minutes,
                task.Mode == PlanningMode.Manual ? task.ManualStart : start, task.Mode == PlanningMode.Manual ? task.ManualFinish : finish,
                start, finish, problem, warnings.ToArray(), task.Progress is PlanningProgress.InProgress or PlanningProgress.Reopened ? "残工数・基準日時・配賦・カレンダー" : "見積工数・配賦・カレンダー"));
        }
        return new(project.ProjectId, revision, results.ToArray());
    }
}
