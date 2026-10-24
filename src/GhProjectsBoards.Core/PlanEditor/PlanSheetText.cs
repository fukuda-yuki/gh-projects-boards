using System.Globalization;

namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanSheetText
{
    internal static string Date(DateOnly day) => day.ToString("M/d", CultureInfo.InvariantCulture) + " (" + "日月火水木金土"[(int)day.DayOfWeek] + ")";
    // 見込終了 (#236 part B): the read-only column after 終了日. Blank without a forecast; a later forecast
    // carries its working-day count.
    internal static string ForecastEnd(ScheduledTask task, PlanTaskLateness? late)
        => task.ForecastEnd is { } end ? Date(end) + (late?.DaysLater is { } later ? " +" + later.ToString(CultureInfo.InvariantCulture) + "日" : "") : "";
    internal static string Indicator(PlanTaskNames names, PlanRow? taskRow, ScheduledTask? effort, PlanTaskLateness? late,
        string remoteProblem, bool unpublished, PlanMarkVisibility? marks = null)
    {
        var shown = marks ?? PlanMarkVisibility.All;
        var states = new List<string>();
        var done = taskRow?.Closed == true;
        if (remoteProblem.Length > 0) states.Add(remoteProblem);
        if (shown.Overdue && late?.MissedDate is { } missed) {
            // The indicator tooltip names the day as M/d; the weekday belongs to the sheet's date cells
            // and the 見込終了 column (docs/spec.md line 192 versus 174).
            var date = missed.ToString("M/d", CultureInfo.InvariantCulture);
            var remaining = effort?.Remaining;
            var remainingText = remaining is { } work
                ? $"（見込み残存工数 {work.ToString("0.############################", CultureInfo.InvariantCulture)}h）" : "";
            states.Add(late.OverdueKind == PlanOverdueKind.Finish
                ? $"期限超過: 終了日 {date} を過ぎて未完了{remainingText}"
                : $"期限超過: 開始日 {date} を過ぎて未着手");
            if (late.DaysLater is { } also) states.Add($"見込終了は +{also}日");
        }
        if (shown.Later && late?.DaysLater is { } days && late.Level == PlanLatenessLevel.Later)
            states.Add($"予定より遅れ: +{days}日（{Cause(names, late)}）");
        if (effort?.IsSummary == true && late is not null) {
            var descendants = new List<string>();
            if (shown.Overdue && late.OverdueDescendantTasks > 0) descendants.Add($"期限超過 {late.OverdueDescendantTasks}");
            if (shown.Later && late.LaterDescendantTasks > 0) descendants.Add($"予定より遅れ {late.LaterDescendantTasks}");
            if (shown.OverEstimate && late.OverEstimateDescendantTasks > 0) descendants.Add($"見積超過 {late.OverEstimateDescendantTasks}");
            if (descendants.Count > 0) states.Add("配下に" + string.Join("、", descendants));
        }
        if (effort is { IsSummary: true, MissingEstimates: > 0 }) states.Add($"見積のないタスク {effort.MissingEstimates}");
        if (shown.OverEstimate && effort?.IsOverEstimate == true)
            states.Add(effort.Forecast is null ? "見積超過: 見込工数・差異が数値の範囲外です" : $"見積超過: 見込工数 {effort.Forecast:0.############################}h（見積工数 {effort.Estimate:0.############################}h、差異 {effort.Variance:+0.############################;-0.############################;0}h）");
        if (done) states.Add("完了");
        // #231: the row's own pending state, named by the command that produced it.
        if (taskRow?.Cancelled == true) states.Add("中止");
        if (taskRow?.Removed == true) states.Add("この版から外す（未発行）");
        if (unpublished) states.Add("未発行の変更あり");
        return string.Join("、", states);

    }
    internal static string Cause(PlanTaskNames names, PlanTaskLateness late) => late.CausePredecessorRowId is { } predecessor
        ? $"先行 {names.AtPosition(predecessor)} の遅れによる" : "自分の残による";
    internal static string RemoteProblem(PlanSync sync, string identity)
    {
        if (sync.Unverified.Contains(identity)) return "GitHub で未確認 — 最新の情報に更新で確認";
        var failures = sync.Failures.Where(f => f.Identity == identity).Select(PlanPublishText.Failure).Distinct().ToArray();
        if (failures.Length > 0) return (sync.Failures.Where(f => f.Identity == identity).All(f => f.Reason == "NotDispatched") ? "未送信: " : "発行失敗: ") + string.Join(" / ", failures);
        return sync.Unavailable.Contains(identity) ? "GitHubで取得できません — 発行で確認" : "";
    }
}

// Which lateness marks the sheet draws. The sets, counts and filters stay complete either way.
internal sealed record PlanMarkVisibility(bool Overdue = true, bool Later = true, bool OverEstimate = true)
{
    internal static readonly PlanMarkVisibility All = new();
    internal static PlanMarkVisibility From(ProjectPlanSettings settings)
        => new(settings.ShowOverdueMark, settings.ShowLaterMark, settings.ShowOverEstimateMark);
}
