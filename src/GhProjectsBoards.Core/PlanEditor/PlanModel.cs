using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.PlanEditor;

internal sealed record PlanTask(string Identity, int RowId)
{
    public string? Parent { get; init; }
    public string[] Predecessors { get; init; } = [];
    public string[] Assignees { get; init; } = [];
    public decimal? Estimate { get; init; }
    public decimal? Remaining { get; init; }
    public decimal? Actual { get; init; }
    public bool Closed { get; init; }
    public bool Fixed { get; init; }
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }
    public DateOnly? GitHubStart { get; init; }
    public DateOnly? GitHubEnd { get; init; }
    public DateOnly? StartNoEarlierThan { get; init; }
    public bool IsComplete => Closed || Remaining == 0 && (Estimate > 0 || Actual > 0);
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
    public IReadOnlyDictionary<DateOnly, decimal>? PlannedHours { get; init; }
}
