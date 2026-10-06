using System.Text.Json;

namespace GhProjectsBoards.Core.Projects;

internal sealed record HolidayDate(DateOnly Date, string Name);
internal sealed record HolidayPreset(string Version, string Source, string SourceSha256, DateTimeOffset RetrievedAt,
    int FirstYear, int LastYear, HolidayDate[] Dates);

internal static class PlanningContract
{
    public static HolidayPreset BundledHolidays()
    {
        using var stream = typeof(PlanningContract).Assembly.GetManifestResourceStream("GhProjectsBoards.Core.Planning.JapanHolidays2025-2027.json")!;
        return JsonSerializer.Deserialize<HolidayPreset>(stream)!;
    }
}
