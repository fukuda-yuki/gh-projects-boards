using System.Globalization;

namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanSheetText
{
    internal static string Indicator(PlanRow? taskRow, ScheduledTask? effort, PlanTaskLateness? late,
        DateOnly? publishedEnd, string remoteProblem, bool unpublished)
    {
        var states = new List<string>();
        var done = taskRow?.Closed == true;
        var typed = taskRow?.StartNoEarlierThan is not null || taskRow?.Fixed == true;
        if (remoteProblem.Length > 0) states.Add(remoteProblem);
        if (late?.MissedPublishedDate is { } missed) {
            var date = missed.ToString("M/d", CultureInfo.InvariantCulture);
            var remaining = effort?.Remaining;
            var remainingText = remaining is { } work
                ? $"（見込み残存工数 {work.ToString("0.############################", CultureInfo.InvariantCulture)}h）" : "";
            states.Add(late.OverdueKind == PlanOverdueKind.Finish
                ? $"期限超過: 完了予定 {date} を過ぎて未完了{remainingText}"
                : $"期限超過: 開始予定 {date} を過ぎて未着手");
        }
        if (late?.DaysLater is { } days && publishedEnd is { } published)
            states.Add($"予定より遅れ: 発行済み {published.ToString("M/d", CultureInfo.InvariantCulture)} から +{days} 日");
        if (effort?.IsSummary == true && late is not null) {
            var descendants = new List<string>();
            if (late.OverdueDescendantTasks > 0) descendants.Add($"期限超過 {late.OverdueDescendantTasks}");
            if (late.LaterDescendantTasks > 0) descendants.Add($"予定より遅れ {late.LaterDescendantTasks}");
            if (descendants.Count > 0) states.Add("配下に" + string.Join("、", descendants));
        }
        if (effort?.IsOverEstimate == true)
            states.Add(effort.Forecast is null ? "見積超過: 見込工数・差異が数値の範囲外です" : $"見積超過: 見込工数 {effort.Forecast:0.############################}h（見積工数 {effort.Estimate:0.############################}h、差異 {effort.Variance:+0.############################;-0.############################;0}h）");
        if (effort?.IsZeroRemainingOpen == true) states.Add("残 0・未完了: 見込み残存工数が 0 ですが Issue は閉じていません");
        if (done) states.Add("完了");
        if (typed) states.Add(taskRow?.Fixed == true ? "日程固定" : "開始日を指定");
        if (unpublished) states.Add("未発行の変更あり");
        return string.Join("、", states);
    }
    internal static string RemoteProblem(PlanSync sync, string identity)
    {
        if (sync.Unverified.Contains(identity)) return "GitHub で未確認 — 最新の情報に更新で確認";
        var failures = sync.Failures.Where(f => f.Identity == identity).Select(PlanPublishText.Failure).Distinct().ToArray();
        if (failures.Length > 0) return (sync.Failures.Where(f => f.Identity == identity).All(f => f.Reason == "NotDispatched") ? "未送信: " : "発行失敗: ") + string.Join(" / ", failures);
        return sync.Unavailable.Contains(identity) ? "GitHubで取得できません — 発行で確認" : "";
    }
}
