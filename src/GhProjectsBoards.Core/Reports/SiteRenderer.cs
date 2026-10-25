using System.Globalization;
using System.Net;
using System.Text;

namespace GhProjectsBoards.Core.Reports;

// #271: the static pages. Everything is drawn at build time: inline SVG, one stylesheet, no scripts and no external
// requests, and every link is relative so the same output works from a local folder and under /gh-projects-boards/.
internal static class SiteRenderer
{
    internal sealed record Output(string Path, byte[] Bytes);

    // Okabe-Ito ordering: distinguishable for colour-blind readers, assigned by position in the option order.
    private static readonly string[] Palette =
    [
        "#0072B2", "#E69F00", "#009E73", "#D55E00", "#CC79A7", "#56B4E9", "#F0E442", "#8C6D31",
    ];
    private const string UnsetColor = "#9AA0A6";
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static IReadOnlyList<Output> Render(SiteConfig config, IReadOnlyList<ProjectProgress> projects)
    {
        var outputs = new List<Output>
        {
            new("index.html", Utf8.GetBytes(ListPage(config, projects))),
            new("style.css", Utf8.GetBytes(Stylesheet)),
            new("robots.txt", Utf8.GetBytes("User-agent: *\nDisallow: /\n")),
        };
        foreach (var project in projects)
        {
            var folder = "p/" + project.Config.FolderName + "/";
            outputs.Add(new(folder + "index.html", Utf8.GetBytes(DetailPage(config, project))));
            outputs.Add(new(folder + "items.csv", SiteCsv.Render(project.Project, project.LatestItems, project.LatestTaskIds)));
        }
        return outputs;
    }

    private static string ListPage(SiteConfig config, IReadOnlyList<ProjectProgress> projects)
    {
        var ordered = projects
            .OrderBy(p => p.Config.PeriodEnd is null ? 1 : 0)
            .ThenBy(p => p.Config.PeriodEnd ?? DateOnly.MaxValue)
            .ToArray();
        var page = new StringBuilder();
        page.Append(Head(config.Title, "style.css"));
        page.Append("<body>");
        page.Append(Chrome(config, ""));
        page.Append("<main class=\"page list-page\">");
        page.Append("<div class=\"project-grid\" role=\"table\" aria-label=\"プロジェクト一覧\">");
        page.Append("<div class=\"grid-head\" role=\"row\">");
        foreach (var label in new[] { "プロジェクト", "期間", "完了", "状態", "最終記録日", "CSV" })
            page.Append($"<span role=\"columnheader\">{Esc(label)}</span>");
        page.Append("</div>");
        foreach (var project in ordered)
        {
            var config1 = project.Config;
            var latest = project.Weeks[^1];
            var href = "p/" + config1.FolderName + "/index.html";
            var csv = "p/" + config1.FolderName + "/items.csv";
            page.Append("<div class=\"project-row\" role=\"row\">");
            page.Append($"<span class=\"cell-name\" role=\"cell\"><a class=\"row-link\" href=\"{Attr(href)}\">{Esc(config1.Name)}</a>");
            page.Append($"<span class=\"group-name\">{Esc(config1.GroupField)}</span></span>");
            page.Append($"<span class=\"cell-period\" role=\"cell\">{Esc(SiteAnalysis.Period(config1))}</span>");
            page.Append($"<span class=\"cell-done\" role=\"cell\"><span class=\"done-count\">{SiteAnalysis.Number(project.ClosedCount)} / {SiteAnalysis.Number(project.TaskCount)}</span>");
            page.Append($"<span class=\"percent\">{SiteAnalysis.Percent(project.ClosedCount, project.TaskCount)}%</span></span>");
            page.Append($"<span class=\"cell-bar\" role=\"cell\">{CompositionBar(project, latest)}</span>");
            page.Append($"<span class=\"cell-date\" role=\"cell\">{SiteAnalysis.Date(project.LatestDay)}</span>");
            page.Append($"<span class=\"cell-csv\" role=\"cell\"><a href=\"{Attr(csv)}\" download=\"{Attr(CsvName(project))}\">CSV</a></span>");
            page.Append("</div>");
        }
        page.Append("</div>");
        page.Append(Legend(projects.Select(p => (IReadOnlyList<string>)p.States).DistinctBy(s => string.Join("\u001f", s)).ToArray()));
        page.Append("</main></body></html>");
        return page.ToString();
    }

    private static string DetailPage(SiteConfig config, ProjectProgress project)
    {
        var name = project.Config.Name;
        var page = new StringBuilder();
        page.Append(Head(name + " – " + config.Title, "../../style.css"));
        page.Append("<body>");
        page.Append("<main class=\"page detail-page\">");
        page.Append("<header class=\"project-header\">");
        page.Append("<div class=\"header-top\"><a class=\"back\" href=\"../../index.html\">← プロジェクト一覧</a>");
        if (config.Demo) page.Append("<span class=\"demo-label\">デモデータ</span>");
        page.Append("</div>");
        page.Append($"<h1>{Esc(name)}</h1>");
        page.Append("<dl class=\"meta\">");
        page.Append($"<div><dt>期間</dt><dd>{Esc(SiteAnalysis.Period(project.Config))}</dd></div>");
        page.Append($"<div><dt>完了</dt><dd><span class=\"done-count\">{SiteAnalysis.Number(project.ClosedCount)} / {SiteAnalysis.Number(project.TaskCount)}</span>");
        page.Append($"<span class=\"percent\">{SiteAnalysis.Percent(project.ClosedCount, project.TaskCount)}%</span></dd></div>");
        page.Append($"<div><dt>最終記録日</dt><dd>{SiteAnalysis.Date(project.LatestDay)}</dd></div>");
        page.Append($"<div><dt>CSV</dt><dd><a href=\"items.csv\" download=\"{Attr(CsvName(project))}\">ダウンロード</a></dd></div>");
        page.Append("</dl>");
        page.Append("</header>");
        page.Append("<section class=\"panel chart-panel\">");
        page.Append("<h2>状態の推移</h2>");
        page.Append(Chart(project));
        page.Append(Legend([project.States]));
        page.Append("</section>");
        page.Append("<section class=\"panel group-panel\">");
        page.Append($"<h2>{Esc(project.Config.GroupField)}ごとの完了</h2>");
        page.Append("<table class=\"groups\"><caption class=\"visually-hidden\">");
        page.Append($"{Esc(project.Config.GroupField)}ごとのタスク数と完了数、完了率</caption>");
        page.Append($"<thead><tr><th scope=\"col\">{Esc(project.Config.GroupField)}</th>");
        page.Append("<th scope=\"col\" class=\"num\">タスク</th><th scope=\"col\" class=\"num\">完了</th><th scope=\"col\" class=\"num\">完了率</th></tr></thead><tbody>");
        foreach (var row in project.Groups)
        {
            page.Append($"<tr><th scope=\"row\">{Esc(row.Name)}</th><td class=\"num\">{SiteAnalysis.Number(row.Tasks)}</td>");
            page.Append($"<td class=\"num\">{SiteAnalysis.Number(row.Closed)}</td><td class=\"num\">{SiteAnalysis.Percent(row.Closed, row.Tasks)}%</td></tr>");
        }
        page.Append("</tbody><tfoot><tr><th scope=\"row\">合計</th>");
        page.Append($"<td class=\"num\">{SiteAnalysis.Number(project.TaskCount)}</td><td class=\"num\">{SiteAnalysis.Number(project.ClosedCount)}</td>");
        page.Append($"<td class=\"num\">{SiteAnalysis.Percent(project.ClosedCount, project.TaskCount)}%</td></tr></tfoot></table>");
        page.Append("</section></main></body></html>");
        return page.ToString();
    }

    private static string Head(string title, string stylesheet)
        => "<!DOCTYPE html><html lang=\"ja\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            + "<meta name=\"robots\" content=\"noindex\">"
            + $"<title>{Esc(title)}</title><link rel=\"stylesheet\" href=\"{Attr(stylesheet)}\"></head>";

    private static string Chrome(SiteConfig config, string extra)
    {
        var header = new StringBuilder();
        header.Append("<header class=\"site-header\">");
        header.Append($"<h1>{Esc(config.Title)}</h1>");
        if (config.Demo) header.Append("<span class=\"demo-label\">デモデータ</span>");
        header.Append(extra);
        header.Append("</header>");
        return header.ToString();
    }

    private static string CsvName(ProjectProgress project)
        => project.Config.Name + "_" + SiteAnalysis.Date(project.LatestDay) + ".csv";

    private static string CompositionBar(ProjectProgress project, WeeklyBar latest)
    {
        const double width = 260, height = 16;
        var sb = new StringBuilder();
        sb.Append($"<svg class=\"composition\" viewBox=\"0 0 {F(width)} {F(height)}\" preserveAspectRatio=\"none\" role=\"img\" aria-label=\"最新の状態の構成\">");
        var total = latest.Total;
        if (total == 0)
        {
            sb.Append($"<rect x=\"0\" y=\"0\" width=\"{F(width)}\" height=\"{F(height)}\" fill=\"{UnsetColor}\"/>");
        }
        else
        {
            var x = 0.0;
            for (var n = 0; n < latest.Counts.Count; n++)
            {
                var count = latest.Counts[n];
                if (count == 0) continue;
                var segment = count / (double)total * width;
                sb.Append($"<rect x=\"{F(x)}\" y=\"0\" width=\"{F(segment)}\" height=\"{F(height)}\" fill=\"{StateColor(project.States, n)}\">");
                sb.Append($"<title>{Esc(project.States[n] + " · " + SiteAnalysis.Number(count))}</title></rect>");
                x += segment;
            }
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    // The weekly stacked bars: one bar per drawn week, stacked bottom-up in the state's option order, an empty slot
    // for a week without a record, and the x axis labelled with each week's Monday. Dense charts rotate the week
    // labels so every week stays readable instead of dropping weeks from the axis.
    private static string Chart(ProjectProgress project)
    {
        const double width = 1840, left = 76, top = 8, plotHeight = 238;
        var plotWidth = width - left - 20;
        var weeks = project.Weeks;
        var slot = plotWidth / weeks.Count;
        var rotate = slot < 36;
        var (max, step) = NiceScale(weeks.Max(w => w.Total));
        var sb = new StringBuilder();
        sb.Append($"<svg class=\"chart\" viewBox=\"0 0 {F(width)} {F(top + plotHeight + (rotate ? 62 : 54))}\" role=\"img\" aria-label=\"週ごとの状態の積み上げ棒グラフ\">");
        for (var value = 0.0; value <= max + 0.001; value += step)
        {
            var y = top + plotHeight - value / max * plotHeight;
            sb.Append($"<line class=\"grid\" x1=\"{F(left)}\" y1=\"{F(y)}\" x2=\"{F(left + plotWidth)}\" y2=\"{F(y)}\"/>");
            sb.Append($"<text class=\"axis\" x=\"{F(left - 10)}\" y=\"{F(y + 4)}\" text-anchor=\"end\">{SiteAnalysis.Number((int)Math.Round(value))}</text>");
        }
        var bar = Math.Min(48.0, slot * 0.72);
        var labelStep = rotate ? 1 : Math.Max(1, (int)Math.Ceiling(30.0 / slot));
        int? year = null;
        for (var n = 0; n < weeks.Count; n++)
        {
            var week = weeks[n];
            var x = left + n * slot + (slot - bar) / 2;
            if (week.Day is { } day)
            {
                var y = top + plotHeight;
                for (var state = 0; state < week.Counts.Count; state++)
                {
                    var count = week.Counts[state];
                    if (count == 0) continue;
                    var height = count / max * plotHeight;
                    y -= height;
                    sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(bar)}\" height=\"{F(Math.Max(height, 0.8))}\" fill=\"{StateColor(project.States, state)}\">");
                    sb.Append($"<title>{Esc(SiteAnalysis.Date(day) + " の週 · " + project.States[state] + " · " + SiteAnalysis.Number(count))}</title></rect>");
                }
            }
            if (n % labelStep != 0) continue;
            var center = left + n * slot + slot / 2;
            var label = week.Monday.Month + "/" + week.Monday.Day;
            if (rotate)
            {
                sb.Append($"<text class=\"axis\" transform=\"translate({F(center)} {F(top + plotHeight + 12)}) rotate(-45)\" text-anchor=\"end\">{label}</text>");
                if (year != week.Monday.Year)
                {
                    year = week.Monday.Year;
                    sb.Append($"<text class=\"axis-year\" x=\"{F(center)}\" y=\"{F(top + plotHeight + 48)}\" text-anchor=\"middle\">{year}</text>");
                }
            }
            else
            {
                sb.Append($"<text class=\"axis\" x=\"{F(center)}\" y=\"{F(top + plotHeight + 22)}\" text-anchor=\"middle\">{label}</text>");
                if (year != week.Monday.Year)
                {
                    year = week.Monday.Year;
                    sb.Append($"<text class=\"axis-year\" x=\"{F(center)}\" y=\"{F(top + plotHeight + 40)}\" text-anchor=\"middle\">{year}</text>");
                }
            }
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    // Integer ticks only: task counts are integers, so half-step labels (2.5, 7.5) would read as wrong numbers.
    private static (double Max, double Step) NiceScale(int maximum)
    {
        var target = Math.Max(maximum, 1);
        if (target <= 6) return (target, 1);
        var raw = target / 4.0;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var normalized = raw / magnitude;
        foreach (var candidate in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
        {
            if (candidate < normalized - 1e-9) continue;
            var step = candidate * magnitude;
            if (step >= 1 && step == Math.Floor(step)) return (Math.Ceiling(target / step) * step, step);
        }
        return (target, 1);
    }

    private static string StateColor(IReadOnlyList<string> states, int index)
        => index == states.Count - 1 && states[index] == SiteAnalysis.UnsetState ? UnsetColor : Palette[index % Palette.Length];

    // One legend per page, listing the states by position so colour is never the only carrier. The list page has
    // Projects with different option sets, so a sequence of names is shown once for each distinct set it serves.
    private static string Legend(IReadOnlyList<IReadOnlyList<string>> sequences)
    {
        var sb = new StringBuilder("<div class=\"legend\" aria-label=\"凡例\">");
        foreach (var states in sequences)
        {
            sb.Append("<ul class=\"legend-line\">");
            for (var n = 0; n < states.Count; n++)
                sb.Append($"<li><span class=\"swatch\" style=\"background:{StateColor(states, n)}\"></span>{Esc(states[n])}</li>");
            sb.Append("</ul>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string Esc(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string Attr(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private const string Stylesheet = """
        :root {
          --bg: #f4f6f8;
          --card: #ffffff;
          --border: #d7dce2;
          --text: #1c2024;
          --muted: #5c6670;
          --accent: #0b5cad;
        }
        * { box-sizing: border-box; }
        html { background: var(--bg); }
        body {
          margin: 0;
          color: var(--text);
          background: var(--bg);
          font-family: "Segoe UI", "Yu Gothic UI", "Hiragino Kaku Gothic ProN", "Noto Sans JP", sans-serif;
          font-size: 15px;
          line-height: 1.4;
        }
        a { color: var(--accent); text-decoration: none; }
        a:hover { text-decoration: underline; }
        .page { max-width: 1880px; margin: 0 auto; padding: 0 28px 16px; }
        .site-header, .project-header {
          display: flex;
          align-items: center;
          flex-wrap: wrap;
          gap: 8px 16px;
          padding: 14px 0 10px;
        }
        h1 { margin: 0; font-size: 26px; font-weight: 600; letter-spacing: 0.01em; }
        h2 { margin: 0 0 6px; font-size: 17px; font-weight: 600; }
        .demo-label {
          display: inline-block;
          padding: 2px 10px;
          border: 1px solid #c8a24a;
          border-radius: 10px;
          background: #fdf6e3;
          color: #7a5a00;
          font-size: 12px;
          font-weight: 600;
          letter-spacing: 0.08em;
        }
        .project-grid { background: var(--card); border: 1px solid var(--border); border-radius: 6px; overflow: hidden; }
        .grid-head, .project-row {
          display: grid;
          grid-template-columns: minmax(240px, 1.5fr) 190px 168px minmax(280px, 1.6fr) 124px 64px;
          align-items: center;
          gap: 16px;
          padding: 10px 18px;
        }
        .grid-head {
          padding-top: 8px;
          padding-bottom: 8px;
          border-bottom: 1px solid var(--border);
          color: var(--muted);
          font-size: 12px;
          letter-spacing: 0.06em;
        }
        .project-row { position: relative; border-bottom: 1px solid #eceff2; min-height: 78px; }
        .project-row:last-child { border-bottom: 0; }
        .project-row:hover { background: #f7fafd; }
        .project-row .row-link { font-size: 18px; font-weight: 600; color: var(--text); }
        .project-row .row-link::after { content: ""; position: absolute; inset: 0; }
        .project-row:hover .row-link { color: var(--accent); text-decoration: none; }
        .cell-name { display: flex; flex-direction: column; gap: 2px; }
        .group-name { color: var(--muted); font-size: 12px; }
        .cell-period, .cell-date { font-variant-numeric: tabular-nums; }
        .cell-done, .meta dd { font-variant-numeric: tabular-nums; }
        .done-count { font-weight: 600; }
        .percent { margin-left: 10px; color: var(--muted); font-variant-numeric: tabular-nums; }
        .cell-csv { position: relative; z-index: 1; text-align: right; }
        .cell-csv a { display: inline-block; padding: 3px 10px; border: 1px solid var(--border); border-radius: 4px; background: #fff; font-size: 13px; }
        .cell-csv a:hover { background: #eef4fb; text-decoration: none; }
        .composition { display: block; width: 100%; max-width: 360px; height: 16px; border-radius: 2px; overflow: hidden; }
        .legend { display: flex; flex-direction: column; gap: 3px; margin-top: 8px; }
        .legend-line { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 18px; margin: 0; padding: 0; list-style: none; }
        .legend li { display: inline-flex; align-items: center; gap: 6px; color: var(--muted); font-size: 13px; }
        .swatch { width: 12px; height: 12px; border-radius: 2px; display: inline-block; }
        .detail-page .project-header { display: block; padding-bottom: 8px; }
        .header-top { display: flex; align-items: center; gap: 14px; margin-bottom: 2px; }
        .back { font-size: 13px; }
        .meta { display: flex; flex-wrap: wrap; gap: 8px 44px; margin: 6px 0 0; }
        .meta div { display: flex; align-items: baseline; gap: 8px; }
        .meta dt { color: var(--muted); font-size: 12px; letter-spacing: 0.05em; }
        .meta dd { margin: 0; font-size: 16px; }
        .panel { background: var(--card); border: 1px solid var(--border); border-radius: 6px; padding: 12px 18px; margin-top: 12px; }
        .chart-panel { padding-bottom: 10px; }
        .chart { display: block; width: 100%; height: auto; }
        .chart .grid { stroke: #e4e8ec; stroke-width: 1; }
        .chart .axis { fill: var(--muted); font-size: 11px; font-variant-numeric: tabular-nums; }
        .chart .axis-year { fill: var(--muted); font-size: 10px; }
        .groups { width: 100%; border-collapse: collapse; font-variant-numeric: tabular-nums; }
        .groups th, .groups td { padding: 3px 10px; border-bottom: 1px solid #eceff2; font-size: 14px; text-align: left; }
        .groups thead th { color: var(--muted); font-size: 12px; font-weight: 500; letter-spacing: 0.05em; border-bottom: 1px solid var(--border); }
        .groups tbody th { font-weight: 400; }
        .groups .num { text-align: right; }
        .groups tfoot th, .groups tfoot td { border-top: 1px solid var(--border); border-bottom: 0; font-weight: 600; }
        .groups tfoot td { text-align: right; }
        .visually-hidden {
          position: absolute;
          width: 1px;
          height: 1px;
          margin: -1px;
          padding: 0;
          overflow: hidden;
          clip: rect(0 0 0 0);
          white-space: nowrap;
          border: 0;
        }
        @media (max-width: 1100px) {
          .grid-head, .project-row { grid-template-columns: 1fr 160px 150px 1fr 110px 60px; }
        }
        """;
}
