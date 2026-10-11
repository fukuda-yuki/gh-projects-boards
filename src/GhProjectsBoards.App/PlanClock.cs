using System.Globalization;
namespace GhProjectsBoards.App;
internal static class PlanClock
{
    internal static DateOnly Today => DateOnly.TryParseExact(Environment.GetEnvironmentVariable("GHPB_TODAY"),
        "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
        ? day : DateOnly.FromDateTime(DateTime.Today);
}
