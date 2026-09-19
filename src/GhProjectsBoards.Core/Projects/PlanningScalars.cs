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
    public static bool RemoteValid(string kind, string? value)
    {
        if (kind != "Number" || value is null) return Valid(kind, value);
        try { using var doc = JsonDocument.Parse(value); return doc.RootElement.ValueKind == JsonValueKind.Number; }
        catch (JsonException) { return false; }
    }
    public static string Kind(string dataType) => dataType switch { "NUMBER" => "Number", "DATE" => "Date", _ => "Select" };
    public static string DataType(string kind) => kind switch { "Number" => "NUMBER", "Date" => "DATE", _ => "SINGLE_SELECT" };
    public static string Normalize(string kind, string value) => kind switch {
        "Number" => PlanningContract.CanonicalHours(PlanningContract.ParseHours(value)),
        "Date" => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : throw new InvalidOperationException("日付は yyyy-MM-dd で入力してください。"),
        _ => value
    };
    public static bool Valid(string kind, string? value)
    {
        if (value is null) return true;
        try { return Normalize(kind, value) == value; } catch (InvalidOperationException) { return false; }
    }
    public static bool Publishable(string kind, LocalValue value) => Valid(kind, value.Value)
        && (kind != "Number" || value.Value is null || PlanningContract.CanPublishHours(PlanningContract.ParseHours(value.Value)));
}

internal sealed partial class EditingWorkspace
{
    // Fresh comparison rows carry the same explicit mapping, never local values.
    private EditingWorkspace ObservationWorkspace()
    {
        var result = new EditingWorkspace(Scope); result.planning.AddRange(planning); return result;
    }
}
