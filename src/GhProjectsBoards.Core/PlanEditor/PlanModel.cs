using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.PlanEditor;

internal sealed class PlanCycleException(string message, IReadOnlyList<string> identities) : ArgumentException(message)
{
    public IReadOnlyList<string> Identities { get; } = identities;
}

internal sealed record PlanTask(string Identity, int RowId)
{
    public string? Parent { get; init; }
    public string[] Predecessors { get; init; } = [];
    public string[] Assignees { get; init; } = [];
    public decimal? Estimate { get; init; }
    public decimal? Remaining { get; init; }
    public decimal? Actual { get; init; }
    public bool Closed { get; init; }
    public DateOnly? CloseDate { get; init; }
    public bool Fixed { get; init; }
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }
    public DateOnly? GitHubStart { get; init; }
    public DateOnly? GitHubEnd { get; init; }
    public DateOnly? StartNoEarlierThan { get; init; }
    public bool IsComplete => Closed;
    public decimal? EffectiveRemaining => Closed ? 0 : Remaining ?? (Estimate is >= 0 && Actual is null or >= 0 ? Math.Max(Estimate.Value - (Actual ?? 0), 0) : null);
    public (DateOnly? Start, DateOnly? End) KeptDates => Closed && CloseDate is { } close
        ? (Start > close ? close : Start, close) : (Start, End);
    public bool KeepsDates => IsComplete || Fixed || Estimate is null && Remaining is null;
}
internal sealed record PlanPerson(string Identity, decimal Rate = 100m)
{
    public IReadOnlySet<DateOnly> DaysOff { get; init; } = new HashSet<DateOnly>();
}
internal sealed record PlanCalendar
{
    public HolidayPreset Holidays { get; init; } = PlanningContract.BundledHolidays();
    public HolidayPreset? ImportedHolidays { get; init; }
    public IReadOnlySet<DateOnly> CompanyDaysOff { get; init; } = new HashSet<DateOnly>();
}
internal sealed record PlanSettings
{
    public DateOnly? StatusDate { get; init; }
    public DateOnly? ProjectStart { get; init; }
    public PlanCalendar Calendar { get; init; } = new();
    public IReadOnlyList<PlanPerson> People { get; init; } = [];
}
internal enum DateOrigin { Kept, Calculated }
internal sealed record PlanDate(DateOnly? Value, DateOrigin Origin, bool DiffersFromGitHub);
internal sealed record ScheduledTask(PlanTask Input, PlanDate Start, PlanDate End,
    decimal? Estimate, decimal? Remaining, decimal? Actual, bool IsSummary,
    string StartReason, IReadOnlyList<string> Warnings)
{
    public decimal? Forecast => Remaining is >= 0 && (Actual ?? 0) >= 0 && (Actual ?? 0) <= decimal.MaxValue - Remaining.Value
        ? (Actual ?? 0) + Remaining.Value : null;
    public decimal? Variance => Forecast - Estimate;
    public bool IsOverEstimate => !IsSummary && !Input.Closed && Estimate is >= 0 && Remaining is >= 0 && (Actual ?? 0) >= 0
        && ((Actual ?? 0) > Estimate || Remaining > Estimate - (Actual ?? 0));
    public bool IsZeroRemainingOpen => !IsSummary && !Input.Closed && Remaining == 0 && (Estimate > 0 || Actual > 0);
    public bool IsMilestone => !IsSummary && !Input.Closed && Remaining == 0 && !(Estimate > 0 || Actual > 0);
    public decimal? WorkCompleteFraction
    {
        get
        {
            if (Actual is not >= 0 || Remaining is not >= 0 || Actual == 0 && Remaining == 0) return null;
            // Scale before adding so accepted decimal efforts cannot overflow the ratio.
            var scale = Math.Max(Actual.Value, Remaining.Value);
            var actual = Actual.Value / scale;
            return actual / (actual + Remaining.Value / scale);
        }
    }
    public IReadOnlyDictionary<DateOnly, decimal>? PlannedHours { get; init; }
}
