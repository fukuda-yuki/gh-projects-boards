using GhProjectsBoards.Core.Projects;
namespace GhProjectsBoards.Core.PlanEditor;

internal enum PlanPeriodScale { Day, Week, Month }
internal sealed record PlanPeriod(DateOnly Start, DateOnly End);
internal sealed record PersonDayOverload(DateOnly Date, decimal Planned, decimal Capacity, IReadOnlyList<string> Tasks)
{
    public decimal? Percent => Capacity > 0 ? Planned / Capacity * 100 : null;
}
internal sealed record PersonPeriod(decimal? Planned, decimal? Capacity, IReadOnlyList<string> Tasks)
{
    public decimal? Percent => Capacity > 0 ? Planned / Capacity * 100 : null;
    public bool Overloaded => Planned > Capacity;
    public IReadOnlyList<PersonDayOverload> DailyOverloads { get; init; } = [];
}
internal sealed record PersonLoad(string Identity, string Name, decimal? Rate, decimal? Allowance,
    decimal? Estimate, decimal? Actual, decimal? Remaining, IReadOnlyList<string> Missing,
    IReadOnlyList<string> Unallocated, IReadOnlyList<PersonPeriod> Periods)
{
    public decimal? Forecast => Actual + Remaining;
    public decimal? Difference => Allowance - Forecast;
}
internal sealed record PeoplePlan(IReadOnlyList<PlanPeriod> Periods, IReadOnlyList<PersonLoad> People);
internal static class PlanPeople
{
    internal const string Unassigned = "unassigned", Multiple = "multiple";
    internal static PeoplePlan Calculate(PlanDocument document, DateOnly today, DateOnly anchor, PlanPeriodScale scale, int count)
    {
        if (count is < 1 or > 366) throw new ArgumentOutOfRangeException(nameof(count));
        var settings = document.State.Settings;
        var periods = new List<PlanPeriod>();
        var start = scale switch { PlanPeriodScale.Week => anchor.AddDays(-((int)anchor.DayOfWeek + 6) % 7),
            PlanPeriodScale.Month => new(anchor.Year, anchor.Month, 1), _ => anchor };
        for (var i = 0; i < count; i++) {
            var next = scale switch { PlanPeriodScale.Week => start.AddDays(7), PlanPeriodScale.Month => start.AddMonths(1), _ => start.AddDays(1) };
            periods.Add(new(start, next.AddDays(-1))); start = next;
        }
        var holidays = PlanningContract.BundledHolidays().Dates.Select(d => d.Date).Concat(settings.ImportedHolidays?.Dates.Select(d => d.Date) ?? [])
            .Concat(settings.CompanyDaysOff).ToHashSet();
        bool Working(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
            !holidays.Contains(day);
        static IEnumerable<DateOnly> Days(DateOnly from, DateOnly to) {
            for (var n = from.DayNumber; n <= to.DayNumber; n++) yield return DateOnly.FromDayNumber(n);
        }
        var scheduled = PlanOperations.Schedule(document, today).Where(s => !s.IsSummary).ToArray();
        string Owner(ScheduledTask task) => task.Input.Assignees.Length switch { 0 => Unassigned, 1 => task.Input.Assignees[0], _ => Multiple };
        var ids = settings.People.Select(p => p.Identity).Concat(scheduled.SelectMany(s => s.Input.Assignees)).Distinct().ToArray();
        var output = new List<PersonLoad>();
        foreach (var id in ids.Concat([Unassigned, Multiple]))
        {
            var ambiguous = id is Unassigned or Multiple;
            var person = ambiguous ? null : settings.People.FirstOrDefault(p => p.Identity == id) ??
                new PlanResource(id, document.Sync.PeopleNames.GetValueOrDefault(id, "担当者（未確認）"), 100, null);
            var tasks = scheduled.Where(s => Owner(s) == id).ToArray();
            var missing = new HashSet<string>(); var unallocated = new List<string>();
            decimal? Total(Func<ScheduledTask, decimal?> value, string label) {
                if (tasks.Any(t => value(t) is null)) { missing.Add(label + "が未入力"); return null; }
                if (tasks.Any(t => value(t) < 0)) { missing.Add(label + "が不正"); return null; }
                return tasks.Sum(t => value(t)!.Value);
            }
            var estimate = Total(t => t.Estimate, "見積"); var actual = Total(t => t.Actual ?? 0, "実績"); var remaining = Total(t => t.Remaining, "残");
            var allocations = new Dictionary<string, IReadOnlyDictionary<DateOnly, decimal>>();
            foreach (var task in tasks)
            {
                if (task.Input.IsComplete) { allocations[task.Input.Identity] = new Dictionary<DateOnly, decimal>(); continue; }
                var reason = task.Remaining is null ? "残が未入力" : task.StartReason == "入力エラー" || task.Remaining < 0 ? "入力エラー" :
                    task.Start.Value is null || task.End.Value is null ? "日程が未入力" : null;
                if (reason is null && task.PlannedHours is { } daily) allocations[task.Input.Identity] = daily;
                else if (reason is null && task.Remaining == 0) allocations[task.Input.Identity] = new Dictionary<DateOnly, decimal>();
                else if (reason is null) {
                    var days = Days(task.Start.Value!.Value, task.End.Value!.Value).Where(Working).ToArray();
                    if (days.Length == 0) reason = "期間内に稼働日なし";
                    else {
                        var values = days.ToDictionary(d => d, _ => task.Remaining!.Value / days.Length);
                        values[days[^1]] += task.Remaining!.Value - values.Values.Sum();
                        allocations[task.Input.Identity] = values;
                    }
                }
                if (reason is not null) { missing.Add(reason); unallocated.Add(task.Input.Identity); }
            }
            var dailyOverloads = person is null ? [] : allocations
                .SelectMany(a => a.Value.Where(d => d.Value > 0).Select(d => (Date: d.Key, Hours: d.Value, Task: a.Key)))
                .GroupBy(d => d.Date)
                .Select(g => new PersonDayOverload(g.Key, g.Sum(d => d.Hours), Working(g.Key) ? 8m * person.Rate / 100m : 0,
                    g.Select(d => d.Task).ToArray()))
                .Where(d => d.Planned > d.Capacity).OrderBy(d => d.Date).ToArray();
            var loads = periods.Select(period => {
                var contributing = allocations.Where(a => a.Value.Any(d => d.Key >= period.Start && d.Key <= period.End && d.Value > 0)).ToArray();
                var unknown = tasks.Any(t => unallocated.Contains(t.Input.Identity) &&
                    (t.Start.Value is null || t.End.Value is null || t.Start.Value <= period.End && t.End.Value >= period.Start));
                decimal? load = unknown ? null : contributing.Sum(a => a.Value.Where(d => d.Key >= period.Start && d.Key <= period.End).Sum(d => d.Value));
                decimal? capacity = person is null ? null : Days(period.Start, period.End).Count(Working) * 8m * person.Rate / 100m;
                return new PersonPeriod(load, capacity, contributing.Select(a => a.Key).ToArray()) {
                    DailyOverloads = dailyOverloads.Where(d => d.Date >= period.Start && d.Date <= period.End).ToArray()
                };
            }).ToArray();
            output.Add(new(id, id == Unassigned ? "担当者なし" : id == Multiple ? "担当者が複数" : document.Sync.PeopleNames.GetValueOrDefault(id, person!.Name),
                person?.Rate, person?.Allowance, estimate, actual, remaining, missing.Order().ToArray(), unallocated, loads));
        }
        return new(periods, output);
    }
}
