using System.Globalization;
using System.Text;

namespace GhProjectsBoards.Core.Projects;

internal static class WeeklySummaryReport
{
    public static string Create(SummaryProjection summary)
    {
        var text = new StringBuilder();
        text.AppendLine("# 週次報告 — " + Literal(summary.ProjectTitle)).AppendLine();
        text.AppendLine("報告基準: " + Date(summary.Cutoff));
        text.AppendLine($"対象: ローカル計画 · {summary.TaskCount}タスク"
            + (summary.UnpublishedCount > 0 ? $" · 未公開 {summary.UnpublishedCount}件" : ""));
        text.AppendLine("単位: 人日（1人日 = 8人時）").AppendLine();
        // The latest cumulative report and the protected planning baseline contain no week-start actual.
        text.AppendLine("今週の増分: 未算出（週初の実績記録なし）").AppendLine();
        text.AppendLine("## 工数サマリ").AppendLine();
        text.AppendLine("| 指標 | 人日 |").AppendLine("| --- | ---: |");
        text.AppendLine("| 見積 | " + Effort(summary.Estimate) + " |");
        text.AppendLine("| 累計実績 | " + Effort(summary.Actual) + " |");
        text.AppendLine("| 残り | " + Effort(EffortValue.Sum(summary.People.Select(p => p.Remaining))) + " |");
        text.AppendLine("| 完了見込み | " + Effort(EffortValue.Sum(summary.People.Select(p => p.Forecast))) + " |");
        text.AppendLine().AppendLine("## 担当者別").AppendLine();
        text.AppendLine("| 担当者 | 投入可能 | 累計実績 | 残り | 完了見込み | 余裕 / 超過 |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: | --- |");
        var people = summary.People.OrderBy(p => p.Headroom is < 0 ? 0 : p.Headroom is null ? 1 : 2)
            .ThenBy(p => p.Headroom).ThenBy(p => p.Name, StringComparer.Ordinal).ToArray();
        foreach (var person in people)
            text.AppendLine($"| {Literal(person.Name)} | {(person.Allowance is { } a ? Days(a) : "未設定")} | {Effort(person.Actual)} | {Effort(person.Remaining)} | {Effort(person.Forecast)} | {Comparison(person)} |");

        text.AppendLine().AppendLine("## 超過担当者と対策").AppendLine();
        var overloaded = people.Where(p => p.Headroom is < 0).ToArray();
        if (overloaded.Length == 0)
            text.AppendLine(people.Any(p => p.Headroom is null) ? "確認済みの超過なし。比較未完の担当者があります。" : "超過なし。");
        foreach (var person in overloaded)
        {
            text.AppendLine($"### {Literal(person.Name)} — {Comparison(person)} 人日").AppendLine();
            text.AppendLine("| タスク | 累計実績 | 残り | 完了見込み | 実績の報告対象日 |");
            text.AppendLine("| --- | ---: | ---: | ---: | --- |");
            foreach (var task in summary.Contributions.Where(c => c.PersonId == person.Id && c.IncludedInTotals)
                .OrderByDescending(c => c.Remaining.Hours).ThenBy(c => c.TaskId, StringComparer.Ordinal))
                text.AppendLine($"| {Literal(task.Identity)} · {Literal(task.Title)} | {Effort(task.Actual)} | {Effort(task.Remaining)} | {Effort(task.Forecast)} | {Date(task.ReportedThrough)} |");
            text.AppendLine().AppendLine("対策: 未記入").AppendLine();
        }

        var incomplete = summary.Contributions.Where(c => c.IncludedInTotals
            && (!c.Estimate.Complete || !c.Actual.Complete || !c.Remaining.Complete || c.Problem is not null)).ToArray();
        if (incomplete.Length > 0)
        {
            text.AppendLine().AppendLine("## 確認が必要な内訳").AppendLine();
            text.AppendLine("| 担当者 / タスク | 累計実績 | 残り | 実績の報告対象日 | 確認事項 |");
            text.AppendLine("| --- | ---: | ---: | --- | --- |");
            var names = people.ToDictionary(p => p.Id, p => p.Name);
            foreach (var task in incomplete)
                text.AppendLine($"| {Literal(names.GetValueOrDefault(task.PersonId) ?? "帰属未確認")} / {Literal(task.Identity)} · {Literal(task.Title)} | {Effort(task.Actual)} | {Effort(task.Remaining)} | {Date(task.ReportedThrough)} | {Literal(task.Problem ?? "未入力・未確認または古い実績報告")} |");
        }
        return text.ToString();
    }

    private static string Comparison(PersonSummary person) => person.Headroom is { } h
        ? (h < 0 ? "超過 " : "余裕 ") + Days(Math.Abs(h))
        : person.Allowance is null ? "比較未完（投入可能工数未設定）" : "比較未完";
    private static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "未入力";
    private static string Days(decimal hours) => (hours / 8m).ToString("0.###########", CultureInfo.InvariantCulture);
    private static string Effort(EffortValue value)
    {
        if (value.Known == 0 && value.Unknown > 0) return "不明";
        if (value.Complete) return Days(value.Hours);
        var reasons = new List<string> { "小計" };
        if (value.Unknown > 0) reasons.Add($"未確認 {value.Unknown}件");
        if (value.Stale > 0) reasons.Add($"古い報告 {value.Stale}件");
        return Days(value.Hours) + "（" + string.Join("・", reasons) + "）";
    }
    private static string Literal(string value)
    {
        var result = new StringBuilder();
        foreach (var c in string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
        {
            if (c == '&') result.Append("&amp;");
            else if (c == '<') result.Append("&lt;");
            else if (c == '>') result.Append("&gt;");
            else { if ("\\`*_{}[]()#+-.!|".Contains(c)) result.Append('\\'); result.Append(c); }
        }
        return result.ToString();
    }
}
