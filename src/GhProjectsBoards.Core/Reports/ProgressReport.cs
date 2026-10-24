using System.Globalization;
using System.Security;
using System.Text;

namespace GhProjectsBoards.Core.Reports;

internal sealed record ProgressReportFiles(string Markdown, string TrendSvg);

// Renders the team report from snapshot files alone (#262). github.com renders Markdown without scripts and shows
// an SVG as an image, so the chart carries its own background, colours and labels.
internal static class ProgressReport
{
    internal const string TrendPath = "report/phase-trend.svg";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal static ProgressReportFiles Render(IReadOnlyCollection<ProgressSnapshot> snapshots)
    {
        if (snapshots.Count == 0) throw new ArgumentException("スナップショットがありません。");
        var projects = snapshots.Select(s => (s.Project.Host, s.Project.Owner.ToLowerInvariant(), s.Project.Number)).Distinct().Count();
        if (projects > 1) throw new ArgumentException("1 つのフォルダーに複数のプロジェクトのスナップショットがあります。");
        // One point per 基準日: a rerun on the same day replaces that day's value.
        var history = snapshots.GroupBy(s => s.StatusDate).Select(g => g.MaxBy(s => s.ReadAt)!).OrderBy(s => s.StatusDate).ToArray();
        return new(Markdown(history[^1], history.Length), TrendSvg(history));
    }

    internal static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", Invariant) + " (" + "日月火水木金土"[(int)date.DayOfWeek] + ")";
    internal static string Short(DateOnly date) => date.ToString("M/d", Invariant);
    private static string Signed(int? days) => days switch { null => "—", > 0 => "+" + days.Value.ToString(Invariant), _ => days.Value.ToString(Invariant) };
    private static string N(int value) => value.ToString("N0", Invariant);
    private static string Hours(decimal? hours) => hours is { } h ? h.ToString("0.##", Invariant) + "h" : "—";

    // The summary's rule: whole numbers, never 100% while a counted task is open.
    internal static string Percent(ProgressEffort effort)
    {
        if (effort.Progress is not { } progress) return "—";
        var percent = progress * 100;
        var whole = percent > 100 ? Math.Ceiling(percent) : Math.Round(percent, MidpointRounding.AwayFromZero);
        if (whole >= 100 && effort.Closed < effort.Tasks) whole = 99;
        return whole.ToString("0", Invariant) + "%";
    }

    internal static string Reason(ProgressTask task) => task.Late switch
    {
        { Kind: "finish" } late => $"終了日 {Short(late.Date)} を過ぎて未完了",
        { Kind: "start" } late => $"開始日 {Short(late.Date)} を過ぎて未着手",
        { Kind: "forecast" } late => $"見込終了 {Short(late.Date)} · +{late.Days?.ToString(Invariant) ?? "?"}日",
        _ => ""
    };
    internal static string Overrun(ProgressTask task) => $"見積 {Hours(task.Estimate)} → 見込 {Hours((task.Actual ?? 0) + task.Remaining)}";

    private static string Verdict(ProgressSnapshot s)
    {
        if (s.ForecastFinish is not { } finish) return "完了見込は計算できません（工数または日程のないタスクがあります）";
        var text = $"**完了見込 {Day(finish)}**";
        if (s.Period.End is not { } end) return text + " · 終了日が未設定です";
        return text + $" · 終了日 {Day(end)} " + s.FinishDifference switch
        {
            > 0 and var late => $"より {late} 稼働日遅れ",
            < 0 and var early => $"より {-early} 稼働日前",
            0 => "どおり",
            _ => "との差は計算できません"
        };
    }

    // Text inside a Markdown table cell or link label.
    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ReplaceLineEndings(" "))
        {
            if (c is '\\' or '|' or '[' or ']' or '*' or '_' or '`' or '<' or '>' or '#' or '~') builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }
    private static string Link(ProgressTask task) => task.Url.Length == 0 ? Escape(task.Issue) : $"[{Escape(task.Issue)}]({task.Url})";

    private static string Markdown(ProgressSnapshot s, int points)
    {
        var md = new StringBuilder();
        var tasks = s.Tasks.ToDictionary(t => t.Issue, StringComparer.Ordinal);
        md.Append("# ").Append(Escape(s.Project.Title)).Append(" 進捗レポート\n\n");
        md.Append($"基準日 {Day(s.StatusDate)} · 取得 {TimeZoneInfo.ConvertTime(s.ReadAt, Zone(s.TimeZone)).ToString("yyyy-MM-dd HH:mm", Invariant)} ({s.TimeZone}) · [プロジェクト]({s.Project.Url})\n\n");
        md.Append(Verdict(s)).Append("\n\n");
        md.Append("| 遅れ | 遅れの原因 | 見積超過 | 完了 / タスク | 進捗 |\n| ---: | ---: | ---: | ---: | ---: |\n");
        md.Append($"| {N(s.Lateness.Late.Length)} | {N(s.Lateness.Cause.Length)} | {N(s.Lateness.OverEstimate.Length)} | {N(s.Totals.Closed)} / {N(s.Totals.Tasks)} | {Percent(s.Totals)} |\n\n");

        md.Append("## 工程別\n\n| 工程 | 目標日 | 完了見込 | 差（稼働日） | 完了 / タスク | 進捗 | 遅れ | 見積超過 |\n| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |\n");
        foreach (var p in s.Phases)
            md.Append($"| {Escape(p.Phase ?? "工程なし")} | {(p.Target is { } t ? Day(t) : "—")} | {(p.ForecastFinish is { } f ? Day(f) : "—")} | {Signed(p.Difference)} | {N(p.Effort.Closed)} / {N(p.Effort.Tasks)} | {Percent(p.Effort)} | {N(p.Late)} | {N(p.OverEstimate)} |\n");

        md.Append("\n## 工程の完了見込の推移\n\n");
        md.Append($"![工程ごとの完了見込の推移]({TrendPath})\n\n");
        md.Append($"基準日ごとの各工程の完了見込（実線）と目標日（破線）。記録 {N(points)} 回分。\n");

        void List(string heading, IReadOnlyList<string> issues, Func<ProgressTask, string> reason)
        {
            md.Append($"\n## {heading}（{N(issues.Count)}）\n\n");
            if (issues.Count == 0) { md.Append("ありません。\n"); return; }
            md.Append("| Issue | タスク | 工程 | 担当者 | 理由 |\n| --- | --- | --- | --- | --- |\n");
            foreach (var task in issues.Select(i => tasks[i]))
                md.Append($"| {Link(task)} | {Escape(task.Title)} | {Escape(task.Phase ?? "")} | {Escape(string.Join(", ", task.Assignees))} | {reason(task)} |\n");
        }
        List("遅れ", s.Lateness.Late, Reason);
        List("遅れの原因", s.Lateness.Cause, Reason);
        List("見積超過", s.Lateness.OverEstimate, Overrun);
        return md.ToString();
    }

    private static TimeZoneInfo Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    private const int Columns = 3, Margin = 16, Gap = 24, PanelWidth = 296, PanelHeight = 176, Header = 64;
    private static string X(double value) => value.ToString("0.#", Invariant);

    // One small panel per 工程: nine phases are too many series to tell apart by colour in one chart.
    private static string TrendSvg(IReadOnlyList<ProgressSnapshot> history)
    {
        var phases = history[^1].Phases.Select(p => p.Phase).ToArray();
        var rows = (phases.Length + Columns - 1) / Columns;
        var width = Margin * 2 + PanelWidth * Columns + Gap * (Columns - 1);
        var height = Header + rows * PanelHeight + (rows - 1) * Gap / 2 + Margin;
        var first = history[0].StatusDate.DayNumber; var last = history[^1].StatusDate.DayNumber;
        var svg = new StringBuilder();
        var description = string.Join("。", phases.Select(phase => {
            var latest = history[^1].Phases.Single(p => p.Phase == phase);
            return $"{phase ?? "工程なし"}: 完了見込 {(latest.ForecastFinish is { } f ? Short(f) : "なし")}、目標日 {(latest.Target is { } t ? Short(t) : "なし")}";
        }));
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-labelledby=\"title desc\">\n");
        svg.Append("<title id=\"title\">工程の完了見込の推移</title>\n");
        svg.Append($"<desc id=\"desc\">{SecurityElement.Escape(description)}</desc>\n");
        svg.Append("<style>\n" +
            "text{font-family:system-ui,-apple-system,\"Segoe UI\",\"Hiragino Sans\",\"Noto Sans CJK JP\",sans-serif}\n" +
            ".bg{fill:#fcfcfb}.t1{fill:#0b0b0b}.t2{fill:#52514e}.t3{fill:#898781}.grid{stroke:#e1e0d9}.target{stroke:#898781}" +
            ".line{stroke:#2a78d6}.dot{fill:#2a78d6;stroke:#fcfcfb}.halo{paint-order:stroke;stroke:#fcfcfb;stroke-width:4px;stroke-linejoin:round}\n" +
            "@media (prefers-color-scheme:dark){.bg{fill:#1a1a19}.t1{fill:#f0efec}.t2{fill:#c3c2b7}.grid{stroke:#2c2c2a}" +
            ".line{stroke:#3987e5}.dot{fill:#3987e5;stroke:#1a1a19}.halo{stroke:#1a1a19}}\n</style>\n");
        svg.Append($"<rect class=\"bg\" width=\"{width}\" height=\"{height}\" rx=\"8\"/>\n");
        svg.Append($"<text class=\"t1\" x=\"{Margin}\" y=\"30\" font-size=\"16\" font-weight=\"600\">工程の完了見込の推移</text>\n");
        svg.Append($"<text class=\"t2\" x=\"{Margin}\" y=\"52\" font-size=\"12\">横軸 基準日 · 縦軸 完了見込 · 破線 目標日</text>\n");
        for (var n = 0; n < phases.Length; n++)
        {
            var x = Margin + n % Columns * (PanelWidth + Gap);
            var y = Header + n / Columns * (PanelHeight + Gap / 2);
            Panel(svg, phases[n], history, x, y, first, last);
        }
        svg.Append("</svg>\n");
        return svg.ToString();
    }

    private static void Panel(StringBuilder svg, string? phase, IReadOnlyList<ProgressSnapshot> history, int x, int y, int first, int last)
    {
        var points = history.Select(s => (Day: s.StatusDate.DayNumber, Phase: s.Phases.FirstOrDefault(p => p.Phase == phase)))
            .Where(p => p.Phase?.ForecastFinish is not null).Select(p => (p.Day, Finish: p.Phase!.ForecastFinish!.Value.DayNumber, p.Phase.Difference)).ToArray();
        var target = history[^1].Phases.FirstOrDefault(p => p.Phase == phase)?.Target;
        svg.Append($"<text class=\"t1\" x=\"{x}\" y=\"{y + 14}\" font-size=\"14\" font-weight=\"600\">{SecurityElement.Escape(phase ?? "工程なし")}</text>\n");
        double left = x + 44, right = x + PanelWidth - 12, top = y + 30, bottom = y + PanelHeight - 28;
        if (points.Length == 0)
        {
            svg.Append($"<text class=\"t3\" x=\"{X(left)}\" y=\"{X((top + bottom) / 2)}\" font-size=\"12\">完了見込なし</text>\n");
            return;
        }
        var values = points.Select(p => p.Finish).Concat(target is { } t ? new[] { t.DayNumber } : []).ToArray();
        int low = values.Min(), high = values.Max();
        var pad = Math.Max(2, (high - low) / 10);
        low -= pad; high += pad;
        double Px(int day) => first == last ? (left + right) / 2 : left + (right - left) * (day - first) / (last - first);
        double Py(int day) => bottom - (bottom - top) * (day - low) / (high - low);
        foreach (var (edge, value) in new[] { (top, high), (bottom, low) })
        {
            svg.Append($"<line class=\"grid\" x1=\"{X(left)}\" x2=\"{X(right)}\" y1=\"{X(edge)}\" y2=\"{X(edge)}\" stroke-width=\"1\"/>\n");
            svg.Append($"<text class=\"t3\" x=\"{X(left - 6)}\" y=\"{X(edge + 4)}\" font-size=\"11\" text-anchor=\"end\">{Short(DateOnly.FromDayNumber(value))}</text>\n");
        }
        if (target is { } due)
        {
            var ty = Py(due.DayNumber);
            svg.Append($"<line class=\"target\" x1=\"{X(left)}\" x2=\"{X(right)}\" y1=\"{X(ty)}\" y2=\"{X(ty)}\" stroke-width=\"1.5\" stroke-dasharray=\"4 3\"/>\n");
            svg.Append($"<text class=\"t2 halo\" x=\"{X(right)}\" y=\"{X(ty - 5)}\" font-size=\"11\" text-anchor=\"end\">目標 {Short(due)}</text>\n");
        }
        if (points.Length > 1)
            svg.Append($"<polyline class=\"line\" fill=\"none\" stroke-width=\"2\" stroke-linejoin=\"round\" stroke-linecap=\"round\" points=\"{string.Join(' ', points.Select(p => X(Px(p.Day)) + "," + X(Py(p.Finish))))}\"/>\n");
        foreach (var p in points)
            svg.Append($"<circle class=\"dot\" cx=\"{X(Px(p.Day))}\" cy=\"{X(Py(p.Finish))}\" r=\"4\" stroke-width=\"2\"/>\n");
        var latest = points[^1];
        var (lx, ly) = (Px(latest.Day), Py(latest.Finish));
        var label = Short(DateOnly.FromDayNumber(latest.Finish)) + (latest.Difference is { } d and not 0 ? $" · {Signed(d)}日" : "");
        var anchor = lx > (left + right) / 2 ? "end" : "start";
        // Above the point unless that leaves the plot or meets the right-aligned target label; then below it.
        var above = ly - 8;
        var targetLabel = target is { } dueLabel ? Py(dueLabel.DayNumber) - 5 : double.NaN;
        var labelY = above < top + 4 || Math.Abs(above - targetLabel) < 14 ? ly + 18 : above;
        svg.Append($"<text class=\"t1 halo\" x=\"{X(anchor == "end" ? lx - 8 : lx + 8)}\" y=\"{X(labelY)}\" font-size=\"12\" text-anchor=\"{anchor}\">{SecurityElement.Escape(label)}</text>\n");
        svg.Append($"<text class=\"t3\" x=\"{X(left)}\" y=\"{X(bottom + 18)}\" font-size=\"11\">{Short(DateOnly.FromDayNumber(first))}</text>\n");
        if (last != first)
            svg.Append($"<text class=\"t3\" x=\"{X(right)}\" y=\"{X(bottom + 18)}\" font-size=\"11\" text-anchor=\"end\">{Short(DateOnly.FromDayNumber(last))}</text>\n");
    }
}
