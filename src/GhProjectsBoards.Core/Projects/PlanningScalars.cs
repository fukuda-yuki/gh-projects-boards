using System.Globalization;
using System.Text.Json;

namespace GhProjectsBoards.Core.Projects;

internal static class PlanningScalars
{
    public static string RemoteNumber(JsonElement raw)
    {
        // Normalize the decimal spelling, never its magnitude/precision. Decimal
        // parsing can round even a nonzero JSON Float such as 9e-29.
        var text = raw.GetRawText(); var parts = text.Split(['e', 'E']); var exponent = 0;
        if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) return text;
        var mantissa = parts[0]; var negative = mantissa.StartsWith('-'); if (negative) mantissa = mantissa[1..];
        var point = mantissa.IndexOf('.'); var scale = (long)(point < 0 ? 0 : mantissa.Length - point - 1) - exponent;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0'); if (digits.Length == 0) return "0";
        if (scale is < -64 or > 64) return text;
        var fixedText = scale <= 0 ? digits + new string('0', (int)-scale)
            : scale >= digits.Length ? "0." + new string('0', (int)scale - digits.Length) + digits
            : digits.Insert(digits.Length - (int)scale, ".");
        if (fixedText.Contains('.')) fixedText = fixedText.TrimEnd('0').TrimEnd('.');
        return (negative ? "-" : "") + fixedText;
    }
    public static string RemoteDate(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : throw new InvalidOperationException("日付は yyyy-MM-dd で入力してください。");
}
