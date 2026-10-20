using System.Globalization;
using System.Text.Json;
namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanExtraEditing
{
    internal static PlanExtraValue Read(PlanRow row, string key)
        => row.ExtraValues.GetValueOrDefault(key) ?? (row.Identity.StartsWith("local:", StringComparison.Ordinal)
            ? new(Projects.ValueAvailability.Empty, "null") : new(Projects.ValueAvailability.Unavailable, null));
    internal static PlanColumnDefinition Definition(PlanDocument document, string key) => document.Baseline.Columns.SingleOrDefault(c => "Project:" + c.Id == key)
        ?? throw new ArgumentException("列を読み取れません。最新の情報に更新してください。");
    internal static string? ReadOnlyReason(PlanDocument document, PlanRow row, string key)
    {
        if (PlanSheetColumns.IsMappedExtra(document, key)) return "このフィールドは割り当てた標準列で編集してください。";
        var definition = document.Baseline.Columns.SingleOrDefault(c => "Project:" + c.Id == key);
        if (definition?.DataType is not ("TEXT" or "NUMBER" or "DATE" or "SINGLE_SELECT")) return "この列は読み取り専用です。";
        return Read(row, key).Availability is Projects.ValueAvailability.Present or Projects.ValueAvailability.Empty
            ? null : "列の値を読み取れません。最新の情報に更新してください。";
    }
    internal static object? Value(PlanRow row, string key)
    {
        if (Read(row, key).Json is not { } json) return null;
        using var node = JsonDocument.Parse(json);
        return node.RootElement.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.Number => node.RootElement.GetDecimal(), JsonValueKind.String => node.RootElement.GetString(), _ => throw new ArgumentException("列の値が不正です。") };
    }
    internal static object? Parse(PlanDocument document, string key, string text, DateOnly today)
    {
        var definition = Definition(document, key);
        if (definition.DataType is not ("TEXT" or "NUMBER" or "DATE" or "SINGLE_SELECT")) throw new ArgumentException("この列は読み取り専用です。");
        if (text.Length == 0) return null;
        return definition.DataType switch {
            "TEXT" => PlanSheetEditing.Parse(document, PlanField.Title, text, today),
            "NUMBER" => PlanInput.TryNumber(text, out var n) ? n : throw new ArgumentException("数値を入力してください。"),
            "DATE" => PlanSheetEditing.Parse(document, PlanField.Start, text, today),
            "SINGLE_SELECT" => Select(definition.Name, definition.Options, text), _ => null };
    }
    internal static string? Select(string name, IEnumerable<string> options, string text, string? emptyMessage = null)
    {
        var value = PlanInput.Fold(text).Trim();
        if (value.Length == 0) return null;
        var choices = options.ToArray();
        if (choices.Length == 0) throw new ArgumentException(emptyMessage ?? $"{name} に選択肢がありません。選択肢は GitHub のプロジェクト設定で追加します。");
        var matches = choices.Where(o => o.Equals(value, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1) return matches[0];
        throw new ArgumentException($"{name}は {string.Join(", ", choices)} のどれかを入力してください。");
    }
    internal static object? Validate(PlanDocument document, string key, object? value)
    {
        var definition = Definition(document, key);
        if (definition.DataType is not ("TEXT" or "NUMBER" or "DATE" or "SINGLE_SELECT")) throw new ArgumentException("この列は読み取り専用です。");
        if (value is null) return null;
        return definition.DataType switch {
            "TEXT" when value is string s && !s.Any(c => c is '\t' or '\r' or '\n') => s,
            "NUMBER" when value is decimal n => n,
            "DATE" when value is DateOnly d => d,
            "DATE" when value is string s && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
            "SINGLE_SELECT" when value is string s => Select(definition.Name, definition.Options, s),
            _ => throw new ArgumentException("列の値の型が不正です。") };
    }
}
