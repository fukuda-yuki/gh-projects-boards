using System.Globalization;
using System.Text.RegularExpressions;

namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanDateInput
{
    private static readonly string[] Formats = ["yyyy-MM-dd", "yyyy/M/d"];
    private static readonly Regex MonthDay = new(@"^(\d{1,2})月(\d{1,2})日$", RegexOptions.CultureInvariant);
    // The IME writes `/` as `・` and the short date as `M月d日`. Settings keep the strict forms:
    // only a plan cell passes its text through here first (docs/spec.md "Cell text input (#239)").
    internal static string Normalize(string text)
    {
        var value = PlanInput.Fold(text).Replace('・', '/');
        var monthDay = MonthDay.Match(value);
        return monthDay.Success ? $"{monthDay.Groups[1].Value}/{monthDay.Groups[2].Value}" : value;
    }
    internal static bool TryParse(string text, out DateOnly date)
        => DateOnly.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    internal static DateOnly Parse(string text)
        => TryParse(text, out var date) ? date : throw new FormatException();
}
