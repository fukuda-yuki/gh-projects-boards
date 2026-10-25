using System.Globalization;

namespace GhProjectsBoards.Core.PlanEditor;

// The 状態 column's marks (#264). One mark per row, the strongest that applies; the text names every
// state that applies. 見積超過 and 未発行 live outside this column, so a late row never hides them.
internal enum PlanRowMark { None, Failure, Conflict, LatePassed, LateForecast, SetAside, Done }

// The strongest mark and the wording its tooltip and accessible name carry. The App maps the kind to one
// shape and colour; the Core owns which case applies and what it says.
internal sealed record PlanRowMarkText(PlanRowMark Mark, string Text);

internal static class PlanSheetText
{
    internal static string Date(DateOnly day) => day.ToString("M/d", CultureInfo.InvariantCulture) + " (" + "日月火水木金土"[(int)day.DayOfWeek] + ")";
    // 見込終了 (#236 part B): the read-only column after 終了日. Blank without a forecast; a later forecast
    // carries its working-day count.
    internal static string ForecastEnd(ScheduledTask task, PlanTaskLateness? late)
        => task.ForecastEnd is { } end ? Date(end) + (late?.DaysLater is { } later ? " +" + later.ToString(CultureInfo.InvariantCulture) + "日" : "") : "";

    // #264: the 状態 column's mark and wording. The order 発行に失敗/入力エラー › 競合 › 遅れ (date passed)
    // › 遅れ (forecast) › 中止/この版から外す › 完了 is the one docs/spec.md records.
    internal static PlanRowMarkText Mark(PlanTaskNames names, PlanRow? taskRow, ScheduledTask? effort, PlanTaskLateness? late,
        string remoteProblem, PlanMarkVisibility? marks = null, IReadOnlyList<string>? conflicts = null)
    {
        var shown = marks ?? PlanMarkVisibility.All;
        var states = new List<string>();
        var failure = remoteProblem.Length > 0;
        var inputError = !failure && effort is { StartReason: "入力エラー", Warnings.Count: > 0 };
        if (failure) states.Add(remoteProblem);
        else if (inputError) states.Add("入力エラー: " + string.Join(" / ", effort!.Warnings));
        var conflicted = conflicts is { Count: > 0 };
        if (conflicted) states.Add("競合: " + string.Join("、", conflicts!));
        var done = taskRow?.Closed == true;
        var setAside = taskRow?.Cancelled == true || taskRow?.Removed == true;
        var summary = effort?.IsSummary == true;
        var passed = !summary && shown.Overdue && late?.Level == PlanLatenessLevel.Overdue;
        var forecast = !summary && shown.Later && late?.Level == PlanLatenessLevel.Later;
        // A requirement row carries the strongest descendant 遅れ mark and names the counts instead of a date.
        var summaryPassed = summary && shown.Overdue && late?.OverdueDescendantTasks > 0;
        var summaryForecast = summary && shown.Later && late?.LaterDescendantTasks > 0;
        if (summary && late is not null) {
            var lateDescendants = (shown.Overdue ? late.OverdueDescendantTasks : 0) + (shown.Later ? late.LaterDescendantTasks : 0);
            var over = shown.OverEstimate ? late.OverEstimateDescendantTasks : 0;
            var counts = new List<string>();
            if (lateDescendants > 0) counts.Add("遅れ " + lateDescendants.ToString(CultureInfo.InvariantCulture));
            if (over > 0) counts.Add("見積超過 " + over.ToString(CultureInfo.InvariantCulture));
            if (counts.Count > 0) states.Add("配下: " + string.Join(" · ", counts));
        }
        else if (passed) {
            var date = late!.MissedDate?.ToString("M/d", CultureInfo.InvariantCulture) ?? "";
            states.Add(late.OverdueKind == PlanOverdueKind.Finish
                ? $"終了日 {date} を過ぎて未完了" + (late.DaysLater is { } also ? " · +" + also.ToString(CultureInfo.InvariantCulture) + "日" : "")
                : $"開始日 {date} を過ぎて未着手");
        }
        else if (forecast) {
            var end = effort?.ForecastEnd is { } day ? Date(day) : "";
            states.Add($"見込終了 {end} · +{late!.DaysLater!.Value.ToString(CultureInfo.InvariantCulture)}日（{Cause(names, late)}）");
        }
        if (setAside) states.Add(taskRow?.Removed == true ? "この版から外す" : "中止");
        if (done) states.Add("完了");
        var kind = failure || inputError ? PlanRowMark.Failure
            : conflicted ? PlanRowMark.Conflict
            : summaryPassed || passed ? PlanRowMark.LatePassed
            : summaryForecast || forecast ? PlanRowMark.LateForecast
            : setAside ? PlanRowMark.SetAside
            : done ? PlanRowMark.Done
            : PlanRowMark.None;
        return new(kind, string.Join("、", states));
    }

    // The 見積 h cell's overrun (#264): the neighbourhood of the value it concerns, not the 状態 column.
    // Hiding the 見積超過の印 removes the figure with the mark.
    internal static string? EstimateOverrun(ScheduledTask effort, PlanMarkVisibility? marks = null) =>
        (marks ?? PlanMarkVisibility.All).OverEstimate && effort.IsOverEstimate
            ? "+" + (effort.Variance ?? 0).ToString("0.############################", CultureInfo.InvariantCulture) + "h" : null;

    internal static string? EstimateOverrunWording(ScheduledTask effort, PlanMarkVisibility? marks = null) =>
        (marks ?? PlanMarkVisibility.All).OverEstimate && effort.IsOverEstimate
            ? "見積超過: 見込 " + (effort.Forecast ?? 0).ToString("0.############################", CultureInfo.InvariantCulture) + "h（見積 "
                + (effort.Estimate ?? 0).ToString("0.############################", CultureInfo.InvariantCulture) + "h）" : null;

    internal static string Cause(PlanTaskNames names, PlanTaskLateness late) => late.CausePredecessorRowId is { } predecessor
        ? $"先行 {names.AtPosition(predecessor)} の遅れによる" : "自分の残による";
    internal static string RemoteProblem(PlanSync sync, string identity)
    {
        if (sync.Unverified.Contains(identity)) return "GitHub で未確認 — 最新の情報に更新で確認";
        var failures = sync.Failures.Where(f => f.Identity == identity).Select(PlanPublishText.Failure).Distinct().ToArray();
        if (failures.Length > 0) return (sync.Failures.Where(f => f.Identity == identity).All(f => f.Reason == "NotDispatched") ? "発行に失敗: 未送信 — " : "発行に失敗: ") + string.Join(" / ", failures);
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
