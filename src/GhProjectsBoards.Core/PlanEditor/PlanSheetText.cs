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
        if (effort?.IsSummary != true && late is { IsLate: true } && (late.Level == PlanLatenessLevel.Overdue ? shown.Overdue : shown.Later))
            states.Add(Late(names, effort, late));
        if (effort?.IsSummary == true && late is not null) {
            var descendants = new List<string>();
            var lateDescendants = (shown.Overdue ? late.OverdueDescendantTasks : 0) + (shown.Later ? late.LaterDescendantTasks : 0);
            if (lateDescendants > 0) descendants.Add($"遅れ {lateDescendants}");
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
    // The tooltip names which 遅れ case applies (#260 D0). The day is M/d; the weekday belongs to the
    // sheet's date cells and the 見込終了 column.
    internal static string Late(PlanTaskNames names, ScheduledTask? effort, PlanTaskLateness late)
    {
        var forecast = late.DaysLater is { } days ? $"見込終了 +{days}日（{Cause(names, late)}）" : "";
        if (late.MissedDate is not { } missed) return "遅れ: " + forecast;
        var date = missed.ToString("M/d", CultureInfo.InvariantCulture);
        var remainingText = effort?.Remaining is { } work
            ? $"（見込み残存工数 {work.ToString("0.############################", CultureInfo.InvariantCulture)}h）" : "";
        var text = late.OverdueKind == PlanOverdueKind.Finish ? $"遅れ: 終了日 {date} を過ぎて未完了{remainingText}" : $"遅れ: 開始日 {date} を過ぎて未着手";
        return forecast.Length == 0 ? text : $"{text}、{forecast.Replace("見込終了 ", "見込終了は ")}";
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
