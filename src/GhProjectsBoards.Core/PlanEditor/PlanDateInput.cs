using System.Globalization;

namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanDateInput
{
    internal static bool TryParse(string text, out DateOnly date)
        => DateOnly.TryParseExact(text, ["yyyy-MM-dd", "yyyy/M/d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    internal static DateOnly Parse(string text)
        => TryParse(text, out var date) ? date : throw new FormatException();
}
