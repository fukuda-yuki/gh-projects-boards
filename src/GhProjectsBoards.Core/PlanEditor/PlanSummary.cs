namespace GhProjectsBoards.Core.PlanEditor;

internal sealed record SummaryValue(decimal? Value, int Excluded);
internal sealed record SummaryTasks(IReadOnlyList<string> Identities, int Closed,
    SummaryValue Estimate, SummaryValue Actual, SummaryValue Remaining, SummaryValue Forecast,
    SummaryValue Variance, SummaryValue Progress, DateOnly? Start, DateOnly? Finish, int Overdue, int OverEstimate)
{
    public int Count => Identities.Count;
    public decimal? ProgressActual { get; init; }
    public decimal? VarianceEstimate { get; init; }
    public decimal? VarianceForecast { get; init; }
}
internal sealed record SummaryPhase(string? Phase, SummaryTasks Tasks, bool IsTotal = false)
{ public DateOnly? Target { get; init; } public int? Difference { get; init; } }
internal sealed record SummaryMilestone(PlanMilestone Milestone, IReadOnlyList<string> Tasks, int Closed, DateOnly? Forecast,
    int? Difference, IReadOnlyList<string> LateTasks);
internal sealed record SummaryPerson(string Identity, string Name, IReadOnlyList<SummaryPhase> Phases,
    SummaryTasks Tasks, decimal? Capacity, decimal? RequiredLoad);
internal sealed record SummaryAttention(IReadOnlyList<string> Overdue, IReadOnlyList<string> Later, IReadOnlyList<string> Cause,
    IReadOnlyList<string> OverEstimate, IReadOnlyList<string> ZeroRemainingOpen, IReadOnlyList<string> Unassigned, SummaryValue UnassignedRemaining,
    IReadOnlyList<string> UnknownEffort);
internal sealed record SummaryPlan(int? WorkingDays, int? ElapsedDays, int? RemainingDays,
    DateOnly? ForecastFinish, int? FinishDifference, SummaryTasks Total, decimal? Capacity, decimal? RequiredLoad,
    IReadOnlyList<SummaryPhase> Phases, IReadOnlyList<SummaryPerson> People, SummaryPerson PeopleTotal,
    PeoplePlan? Months, SummaryAttention Attention)
{ public IReadOnlyList<SummaryMilestone> Milestones { get; init; } = []; }

internal static class PlanSummary
{
    // #236 decision 5: 完了見込 is the latest end on the forecast schedule. Open work finishes at its
    // forecast end; closed work at the close date; kept work and work with no forecast keep its retained
    // plan end, so a kept row still finishes the version (docs/spec.md).
    private static DateOnly? Finish(ScheduledTask task) => task.Input.Closed ? task.Input.CloseDate ?? task.End.Value
        : task.ForecastEnd ?? task.End.Value;
    private static int? Difference(DateOnly? target, DateOnly? forecast, Func<int, int, int> days) => target is { } t && forecast is { } f
        ? f >= t ? days(t.DayNumber + 1, f.DayNumber) : -days(f.DayNumber + 1, t.DayNumber) : null;
    private static Func<int, int, int> WorkingDays(PlanCalendar calendar)
    {
        var holidays = calendar.Holidays.Dates.Select(h => h.Date).Concat(calendar.ImportedHolidays?.Dates.Select(h => h.Date) ?? [])
            .Concat(calendar.CompanyDaysOff).ToHashSet();
        return (first, last) =>
        {
            var count = 0;
            for (var n = first; n <= last; n++)
            {
                var date = DateOnly.FromDayNumber(n);
                if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(date)) count++;
            }
            return count;
        };
    }
    internal static IReadOnlyList<SummaryMilestone> CalculateMilestones(PlanDocument document, IReadOnlyList<ScheduledTask> scheduled)
    {
        var settings = document.State.Settings;
        return CalculateMilestones(document, scheduled, WorkingDays(new PlanCalendar {
            CompanyDaysOff = settings.CompanyDaysOff.ToHashSet(), ImportedHolidays = settings.ImportedHolidays?.ToPreset() }));
    }
    private static SummaryMilestone[] CalculateMilestones(PlanDocument document, IReadOnlyList<ScheduledTask> scheduled, Func<int, int, int> days)
    {
        var rows = document.State.Rows.ToDictionary(r => r.Identity);
        var groups = scheduled.Where(t => !t.IsSummary).ToLookup(t => rows.GetValueOrDefault(t.Input.Identity)?.MilestoneTitle);
        return document.Baseline.Milestones.OrderBy(m => m.DueDate is null).ThenBy(m => m.DueDate).ThenBy(m => m.Title, StringComparer.Ordinal)
            .Select(m => {
                var members = groups[m.Title].ToArray();
                var finish = members.Any(t => Finish(t) is null) ? null : members.Max(Finish);
                return new SummaryMilestone(m, members.Select(t => t.Input.Identity).ToArray(), members.Count(t => t.Input.Closed),
                    finish, Difference(m.DueDate, finish, days),
                    members.Where(t => m.DueDate is { } due && Finish(t) > due).Select(t => t.Input.Identity).ToArray());
            }).ToArray();
    }

    internal static SummaryPlan Calculate(PlanDocument document, IReadOnlyList<ScheduledTask> scheduled, DateOnly statusDate)
    {
        var settings = document.State.Settings;
        var calendar = new PlanCalendar { CompanyDaysOff = settings.CompanyDaysOff.ToHashSet(), ImportedHolidays = settings.ImportedHolidays?.ToPreset() };
        var days = WorkingDays(calendar);
        int? working = settings.ProjectStart is { } start && settings.ProjectEnd is { } end ? days(start.DayNumber, end.DayNumber) : null;
        int? elapsed = settings.ProjectStart is { } from ? days(from.DayNumber, statusDate.DayNumber - 1) : null;
        int? remaining = settings.ProjectEnd is { } until ? days(statusDate.DayNumber, until.DayNumber) : null;
        var tasks = scheduled.Where(t => !t.IsSummary).ToArray();
        var lateness = PlanLateness.Classify(scheduled, calendar, statusDate);
        bool Overdue(ScheduledTask task) => lateness.Tasks[task.Input.Identity].Level == PlanLatenessLevel.Overdue;
        SummaryTasks Sum(IReadOnlyList<ScheduledTask> source)
        {
            SummaryValue Total(Func<ScheduledTask, decimal?> select)
            {
                decimal sum = 0; var known = 0;
                foreach (var task in source)
                    if (select(task) is { } value) { sum += value; known++; }
                return new(known == 0 ? null : sum, source.Count - known);
            }
            var forecast = Total(t => t.Forecast);
            var progressActual = Total(t => t.Forecast.HasValue ? t.Actual ?? 0 : null);
            return new(source.Select(t => t.Input.Identity).ToArray(), source.Count(t => t.Input.Closed),
                Total(t => t.Estimate), Total(t => t.Actual ?? 0), Total(t => t.Remaining), forecast,
                Total(t => t.Variance), new(forecast.Value > 0 ? progressActual.Value / forecast.Value : null, forecast.Excluded),
                source.Min(t => t.Start.Value), source.Any(t => Finish(t) is null) ? null : source.Max(Finish),
                source.Count(Overdue), source.Count(t => t.IsOverEstimate)) {
                    ProgressActual = progressActual.Value,
                    VarianceEstimate = Total(t => t.Variance.HasValue ? t.Estimate : null).Value,
                    VarianceForecast = Total(t => t.Variance.HasValue ? t.Forecast : null).Value
                };
        }
        var total = Sum(tasks);
        var finish = total.Finish;
        var difference = Difference(settings.ProjectEnd, finish, days);
        var milestones = CalculateMilestones(document, tasks, days);
        var mapping = settings.Columns.SingleOrDefault(c => c.Role == PlanField.Phase);
        var options = document.Baseline.Columns.SingleOrDefault(c => c.Id == mapping?.FieldId)?.Options ?? [];
        var rows = document.State.Rows.ToDictionary(r => r.Identity);
        var optionSet = options.ToHashSet(StringComparer.Ordinal);
        string Phase(ScheduledTask task) => rows[task.Input.Identity].Phase is { } phase && optionSet.Contains(phase) ? phase : "";
        string Owner(ScheduledTask task) => task.Input.Assignees.Length switch { 0 => PlanPeople.Unassigned, 1 => task.Input.Assignees[0], _ => PlanPeople.Multiple };
        var phaseGroups = tasks.ToLookup(Phase);
        var personGroups = tasks.ToLookup(Owner);
        var cells = tasks.ToLookup(t => (Owner(t), Phase(t)));
        var phases = options.Concat(phaseGroups.Contains("") ? new[] { "" } : []).ToArray();
        SummaryPhase PhaseRow(string phase, IReadOnlyList<ScheduledTask> source)
        {
            var sum = Sum(source);
            var target = milestones.SingleOrDefault(m => m.Milestone.Title == settings.PhaseMilestones.GetValueOrDefault(phase))?.Milestone.DueDate;
            return new(phase.Length == 0 ? null : phase, sum) { Target = target, Difference = Difference(target, sum.Finish, days) };
        }
        var phaseTotals = phases.Select(p => PhaseRow(p, phaseGroups[p].ToArray())).ToArray();
        // #253: the summary names the people assigned in this Project, the same derivation settings uses.
        var assignedPeople = PlanPeople.Assigned(document);
        var people = new List<SummaryPerson>();
        var identities = assignedPeople.Select(p => p.Identity)
            .Concat(new[] { PlanPeople.Unassigned, PlanPeople.Multiple }.Where(personGroups.Contains));
        foreach (var identity in identities)
        {
            var person = assignedPeople.FirstOrDefault(p => p.Identity == identity);
            var assigned = personGroups[identity].ToArray();
            decimal? capacity = person is null ? null : remaining * 8m * person.Rate / 100;
            if (capacity is { } available && person?.Allowance is { } allowance)
                capacity = Math.Min(available, Math.Max(allowance - assigned.Sum(t => t.Actual ?? 0), 0));
            var personTotal = Sum(assigned);
            var name = identity == PlanPeople.Unassigned ? "担当者なし" : identity == PlanPeople.Multiple ? "担当者が複数"
                : document.Sync.PeopleNames.GetValueOrDefault(identity, person?.Name ?? "担当者（未確認）");
            people.Add(new(identity, name, phases.Select(p => PhaseRow(p, cells[(identity, p)].ToArray())).ToArray(),
                personTotal, capacity, capacity > 0 ? personTotal.Remaining.Value / capacity : null));
        }
        decimal? projectCapacity = remaining.HasValue && assignedPeople.Length > 0
            ? people.Where(p => assignedPeople.Any(s => s.Identity == p.Identity)).Sum(p => p.Capacity) : null;
        var load = projectCapacity > 0 ? total.Remaining.Value / projectCapacity : null;
        PeoplePlan? months = null;
        if (settings.ProjectStart is { } monthStart && settings.ProjectEnd is { } monthEnd)
        {
            if (tasks.Max(Finish) is { } latest && latest > monthEnd) monthEnd = latest;
            var count = (monthEnd.Year - monthStart.Year) * 12 + monthEnd.Month - monthStart.Month + 1;
            if (count > 0) months = PlanPeople.Calculate(document, scheduled, monthStart, PlanPeriodScale.Month, count);
        }
        string[] Ids(Func<ScheduledTask, bool> predicate) => tasks.Where(predicate).Select(t => t.Input.Identity).ToArray();
        var unassigned = Sum(personGroups[PlanPeople.Unassigned].ToArray());
        return new(working, elapsed, remaining, finish, difference, total, projectCapacity, load,
            phaseTotals.Append(new("全体", total, true)).ToArray(), people, new("total", "合計", phaseTotals, total, projectCapacity, load), months,
            new(Ids(Overdue), Ids(t => lateness.Tasks[t.Input.Identity].Level == PlanLatenessLevel.Later),
                Ids(t => lateness.Tasks[t.Input.Identity].IsCause && !t.Input.Closed),
                Ids(t => t.IsOverEstimate), Ids(t => t.IsZeroRemainingOpen), unassigned.Identities, unassigned.Remaining,
                Ids(t => t.Remaining is null || t.Forecast is null))) { Milestones = milestones };
    }
}
