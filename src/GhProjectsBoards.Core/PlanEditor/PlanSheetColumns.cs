using System.Collections.Immutable;
namespace GhProjectsBoards.Core.PlanEditor;

internal sealed record PlanSheetColumn(string Key, string DefaultName, string GitHub, string Editability, bool Visible, string? DisplayName)
{
    public string Type { get; init; } = "—";
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? DefaultName : DisplayName;
    public bool Required => Key is "Id" or "Title";
}
internal static class PlanSheetColumns
{
    internal static readonly (string Key, string Name)[] Standard = [
        ("Id", "ID"), ("Indicator", "状態"), ("Title", "タスク名"), ("Phase", "工程"), ("Assignees", "担当者"),
        ("Estimate", "見積 h"), ("Remaining", "残 h"), ("Actual", "実績 h"), ("Start", "開始日"), ("End", "終了日"),
        ("Predecessors", "先行"), ("StartNoEarlierThan", "開始日指定"), ("Fixed", "日程固定"), ("Status", "ステータス")];
    internal static bool IsExtra(string key) => key.StartsWith("Project:", StringComparison.Ordinal) && key.Length > 8;
    internal static bool IsMappedExtra(ProjectPlanSettings settings, string key)
        => IsExtra(key) && settings.Columns.Any(c => c.FieldId == key[8..]);
    internal static void ValidateOwnership(PlanDocument document)
    {
        var baseline = document.Baseline.Rows.ToDictionary(r => r.Identity);
        foreach (var row in document.State.Rows)
            foreach (var key in row.ExtraValues.Keys.Where(k => IsMappedExtra(document.State.Settings, k)))
                PlanOperations.Require(PlanValues.Get(row, key) == (baseline.TryGetValue(row.Identity, out var before) ? PlanValues.Get(before, key) : "null"),
                    "未発行の追加列編集があるフィールドは割り当てられません。編集を発行するか元に戻してください。");
    }
    internal static void Validate(ImmutableArray<PlanSheetColumnChoice> choices)
    {
        PlanOperations.Require(!choices.IsDefault && choices.All(c => c is not null &&
            (Standard.Any(s => s.Key == c.Key) || c.Key is "Labels" or "MilestoneTitle" or "Repository" || IsExtra(c.Key)) &&
            (!(c.Key is "Id" or "Title") || c.Visible) && (c.DisplayName is null || !c.DisplayName.Any(char.IsControl))) &&
            choices.Select(c => c.Key).Distinct().Count() == choices.Length, "表示列の設定が不正です。");
    }
    internal static ImmutableArray<PlanSheetColumn> All(PlanDocument document)
    {
        var settings = document.State.Settings;
        var result = new List<PlanSheetColumn>();
        for (var i = 0; i < Standard.Length; i++)
        {
            var (key, name) = Standard[i];
            var mapping = settings.Columns.SingleOrDefault(c => c.Role.ToString() == key);
            var source = mapping?.Name ?? key switch { "Title" => "Title", "Assignees" => "Assignees", "Predecessors" => "Blocked by", "Status" => "Status", _ => "—" };
            var type = key switch { "Title" => "テキスト", "Assignees" => "担当者", "Predecessors" => "関係",
                "Estimate" or "Remaining" or "Actual" => "数値", "Start" or "End" or "StartNoEarlierThan" => "日付",
                "Phase" or "Status" or "Fixed" => "単一選択", _ => "—" };
            result.Add(new(key, name, source, key == "Indicator" ? "計算" : key == "Id" ? "読み取りのみ" : "編集できる", i < 11, null) { Type = type });
        }
        var mapped = settings.Columns.Select(c => c.FieldId).ToHashSet();
        foreach (var f in document.Baseline.Columns.Where(f => !mapped.Contains(f.Id) && !(f.Name == "Status" && f.DataType == "SINGLE_SELECT") && f.DataType is "TEXT" or "NUMBER" or "DATE" or "SINGLE_SELECT" or "ITERATION"))
            result.Add(new("Project:" + f.Id, f.Name, f.Name, f.DataType == "ITERATION" ? "読み取りのみ" : "編集できる", false, null) {
                Type = f.DataType switch { "TEXT" => "テキスト", "NUMBER" => "数値", "DATE" => "日付", "SINGLE_SELECT" => "単一選択", "ITERATION" => "イテレーション", _ => "—" } });
        result.AddRange([new("Labels", "ラベル", "Labels", "編集できる", false, null) { Type = "ラベル" },
            new("MilestoneTitle", "マイルストーン", "Milestone", "編集できる", false, null) { Type = "マイルストーン" },
            new("Repository", "リポジトリ", "Repository", "読み取りのみ", false, null) { Type = "リポジトリ" }]);
        var definitions = result.ToDictionary(c => c.Key);
        var ordered = settings.SheetColumns.Where(c => definitions.ContainsKey(c.Key)).Select(c => definitions[c.Key] with { Visible = c.Visible, DisplayName = c.DisplayName }).ToList();
        var used = ordered.Select(c => c.Key).ToHashSet();
        ordered.AddRange(result.Where(c => !used.Contains(c.Key)));
        return ordered.ToImmutableArray();
    }
    internal static ProjectPlanSettings Change(PlanDocument document, string key, bool? visible = null, string? name = null, bool rename = false)
    {
        var columns = All(document);
        PlanOperations.Require(columns.Any(c => c.Key == key), "表示列が見つかりません。");
        return document.State.Settings with { SheetColumns = columns.Select(c => new PlanSheetColumnChoice(c.Key,
            c.Key == key ? visible ?? c.Visible : c.Visible, c.Key == key && rename ? string.IsNullOrWhiteSpace(name) ? null : name : c.DisplayName)).ToImmutableArray() };
    }
    internal static ProjectPlanSettings Move(PlanDocument document, string key, int index)
    {
        var columns = All(document).Select(c => new PlanSheetColumnChoice(c.Key, c.Visible, c.DisplayName)).ToList();
        var from = columns.FindIndex(c => c.Key == key);
        PlanOperations.Require(from >= 0 && index >= 0 && index < columns.Count, "列の移動先が不正です。");
        var item = columns[from]; columns.RemoveAt(from); columns.Insert(index, item);
        return document.State.Settings with { SheetColumns = columns.ToImmutableArray() };
    }
}
