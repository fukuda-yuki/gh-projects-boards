using System.Collections.Immutable;
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
    // Every day with allocated work, so the summary can draw the load against the cap line.
    public IReadOnlyList<PersonDayOverload> Days { get; init; } = [];
    public decimal? MaxDailyPercent { get; init; }
    public DateOnly? MaxDailyDate { get; init; }
}
internal sealed record PersonLoad(string Identity, string Name, decimal? Rate, decimal? Allowance,
    decimal? Estimate, decimal? Actual, decimal? Remaining, IReadOnlyList<string> Missing,
    IReadOnlyList<string> Unallocated, IReadOnlyList<PersonPeriod> Periods)
{
    public decimal? Forecast => Actual + Remaining;
    public decimal? Difference => Allowance - Forecast;
}
internal sealed record PersonWork(PlanResource Person, int Tasks, decimal? Remaining);
internal sealed record PeoplePlan(IReadOnlyList<PlanPeriod> Periods, IReadOnlyList<PersonLoad> People);
internal static class PlanPeople
{
    internal const string Unassigned = "unassigned", Multiple = "multiple";
    // #253: the Project's people are the people assigned in it. A stored person appears only while a row holds
    // that identity; 中止, a pending この版から外す and requirement rows still count, because the row leaves the
    // plan only when a publish and read confirm it. Their stored rate and allowance stay in settings, so the
    // values return unchanged when a row holds them again. A row assignee with no stored entry appears at 100%
    // with the readable login the read returned. Settings, the summary and PlanPeople share this derivation.
    internal static ImmutableArray<PlanResource> Assigned(PlanDocument document)
    {
        var order = document.State.Rows.SelectMany(r => r.Assignees).Distinct(StringComparer.Ordinal).ToArray();
        var assigned = order.ToHashSet(StringComparer.Ordinal);
        var stored = document.State.Settings.People.Where(p => assigned.Contains(p.Identity))
            .Select(p => p with { Name = document.Sync.PeopleNames.GetValueOrDefault(p.Identity, p.Name) }).ToArray();
        var known = stored.Select(p => p.Identity).ToHashSet(StringComparer.Ordinal);
        return stored.Concat(order.Where(id => !known.Contains(id))
            .Select(id => new PlanResource(id, document.Sync.PeopleNames.GetValueOrDefault(id, "担当者（未確認）"), 100, null)))
            .ToImmutableArray();
    }
    // #251: the settings table's read-only context per assigned person. A task counts for each of its assignees
    // while it is an open leaf task (not completed, 中止 or この版から外す); 残 is null when one of those tasks
    // has no 見込み残存工数.
    internal static ImmutableArray<PersonWork> Work(PlanDocument document, IReadOnlyList<ScheduledTask> schedule)
    {
        var rows = document.State.Rows.ToDictionary(r => r.Identity);
        var open = schedule.Where(t => !t.IsSummary && !t.Input.IsComplete
            && !(rows.TryGetValue(t.Input.Identity, out var row) && (row.Cancelled || row.Removed))).ToArray();
        return Assigned(document).Select(person => {
            var tasks = open.Where(t => t.Input.Assignees.Contains(person.Identity, StringComparer.Ordinal)).ToArray();
            return new PersonWork(person, tasks.Length, tasks.Any(t => t.Remaining is null) ? null : tasks.Sum(t => t.Remaining!.Value));
        }).ToImmutableArray();
    }
    internal static PeoplePlan Calculate(PlanDocument document, DateOnly today, DateOnly anchor, PlanPeriodScale scale, int count)
        => Calculate(document, PlanOperations.Schedule(document, today), anchor, scale, count);

    internal static PeoplePlan Calculate(PlanDocument document, IReadOnlyList<ScheduledTask> schedule, DateOnly anchor, PlanPeriodScale scale, int count)
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
        var scheduled = schedule.Where(s => !s.IsSummary).ToArray();
        string Owner(ScheduledTask task) => task.Input.Assignees.Length switch { 0 => Unassigned, 1 => task.Input.Assignees[0], _ => Multiple };
        var byOwner = scheduled.ToLookup(Owner);
        var people = Assigned(document);
        var ids = people.Select(p => p.Identity).ToArray();
        var output = new List<PersonLoad>();
        foreach (var id in ids.Concat([Unassigned, Multiple]))
        {
            var ambiguous = id is Unassigned or Multiple;
            var person = ambiguous ? null : people.First(p => p.Identity == id);
            var tasks = byOwner[id].ToArray();
            var missing = new HashSet<string>(); var unallocated = new List<string>();
            decimal? Total(Func<ScheduledTask, decimal?> value, string label) {
                if (tasks.Any(t => value(t) is null)) { missing.Add(label + "が未入力"); return null; }
                if (tasks.Any(t => value(t) < 0)) { missing.Add(label + "が不正"); return null; }
                return tasks.Sum(t => value(t)!.Value);
            }
            var estimate = Total(t => t.Estimate, "見積工数"); var actual = Total(t => t.Actual ?? 0, "実績工数"); var remaining = Total(t => t.Remaining, "見込み残存工数");
            var allocations = new Dictionary<string, IReadOnlyDictionary<DateOnly, decimal>>();
            foreach (var task in tasks)
            {
                if (task.Input.IsComplete) { allocations[task.Input.Identity] = new Dictionary<DateOnly, decimal>(); continue; }
                var reason = task.Remaining is null ? "見込み残存工数が未入力" : task.StartReason == "入力エラー" || task.Remaining < 0 ? "入力エラー" :
                    task.Start.Value is null || task.End.Value is null ? "日程が未入力" : null;
                if (reason is null && task.PlannedHours is { } daily) allocations[task.Input.Identity] = daily;
                else if (reason is null && task.Remaining == 0) allocations[task.Input.Identity] = new Dictionary<DateOnly, decimal>();
                else reason ??= "日程が未入力";
                if (reason is not null) { missing.Add(reason); unallocated.Add(task.Input.Identity); }
            }
            var dailyLoads = person is null ? [] : allocations
                .SelectMany(a => a.Value.Where(d => d.Value > 0).Select(d => (Date: d.Key, Hours: d.Value, Task: a.Key)))
                .GroupBy(d => d.Date)
                .Select(g => new PersonDayOverload(g.Key, g.Sum(d => d.Hours), Working(g.Key) ? 8m * person.Rate / 100m : 0,
                    g.Select(d => d.Task).ToArray()))
                .OrderBy(d => d.Date).ToArray();
            var loads = periods.Select(period => {
                var contributing = allocations.Where(a => a.Value.Any(d => d.Key >= period.Start && d.Key <= period.End && d.Value > 0)).ToArray();
                var unknown = tasks.Any(t => unallocated.Contains(t.Input.Identity) &&
                    (t.Start.Value is null || t.End.Value is null || t.Start.Value <= period.End && t.End.Value >= period.Start));
                decimal? load = unknown ? null : contributing.Sum(a => a.Value.Where(d => d.Key >= period.Start && d.Key <= period.End).Sum(d => d.Value));
                decimal? capacity = person is null ? null : Days(period.Start, period.End).Count(Working) * 8m * person.Rate / 100m;
                var daily = dailyLoads.Where(d => d.Date >= period.Start && d.Date <= period.End).ToArray();
                var maxKnown = !unknown && capacity is not null and not 0 && daily.All(d => d.Capacity > 0);
                var maxPercent = !maxKnown ? null : daily.Length == 0 ? 0 : daily.Max(d => d.Percent);
                return new PersonPeriod(load, capacity, contributing.Select(a => a.Key).ToArray()) {
                    DailyOverloads = daily.Where(d => d.Planned > d.Capacity).ToArray(),
                    Days = daily,
                    MaxDailyPercent = maxPercent,
                    // The date follows the same known set as the maximum; the earliest day wins a tie.
                    MaxDailyDate = maxPercent is null or 0 ? null : daily.OrderByDescending(d => d.Percent).ThenBy(d => d.Date).First().Date
                };
            }).ToArray();
            output.Add(new(id, id == Unassigned ? "担当者なし" : id == Multiple ? "担当者が複数" : document.Sync.PeopleNames.GetValueOrDefault(id, person!.Name),
                person?.Rate, person?.Allowance, estimate, actual, remaining, missing.Order().ToArray(), unallocated, loads));
        }
        return new(periods, output);
    }
}
