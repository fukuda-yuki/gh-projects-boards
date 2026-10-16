using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Windows.Foundation;
namespace GhProjectsBoards.App;

internal sealed partial class PlanSheetView
{
    private readonly List<(Grid Cell, TextBlock Text, PlanSheetDivider? Grip)> columnHeaders = [];
    private string? resizingColumn;
    private double resizeOriginal;
    internal static double DefaultColumnWidth(string key)
        => Columns.FirstOrDefault(c => (c.Indicator ? "Indicator" : c.Field ?? "Id") == key)?.Width ?? 140;
    private double ColumnWidth(string key) => ConfiguredColumns.Single(c => (c.Indicator ? "Indicator" : c.Field ?? "Id") == key).Width;
    private PlanSheetDivider ColumnGrip(Column column)
    {
        var key = column.Field ?? "Id";
        var grip = Id(new PlanSheetDivider(() => ColumnWidth(key), () => PlanSheetColumns.MaximumWidth,
            width => _ = ResizeColumn(key, width), PlanSheetColumns.MinimumWidth,
            dragStarted: () => { resizingColumn = key; resizeOriginal = ColumnWidth(key); },
            dragPreview: width => PreviewColumnWidth(key, width),
            dragCompleted: async canceled => {
                var width = ColumnWidth(key); var original = resizeOriginal; resizingColumn = null;
                if (canceled || width == original) { Refresh(); return; }
                await ResizeColumn(key, width);
            }, fit: () => ResizeColumn(key, FitColumnWidth(key)), canChange: () => !remoteBusy && !disposed) {
                Width = 8, Height = 48, IsTabStop = false
            }, "PlanHeaderGrip" + key);
        AutomationProperties.SetName(grip, column.Label + " の幅");
        return grip;
    }
    private void LayoutColumnHeaders()
    {
        var x = 0d;
        for (var i = 0; i < columnHeaders.Count; i++) {
            var (cell, text, grip) = columnHeaders[i];
            var width = VisibleColumns[i].Width;
            cell.Width = text.Width = width; Canvas.SetLeft(cell, x);
            x += width;
            if (grip is not null) Canvas.SetLeft(grip, x - 4);
        }
        sheetHead.Width = x + 4;
    }
    private void PreviewColumnWidth(string key, double width)
    {
        width = Math.Clamp(Math.Round(width), PlanSheetColumns.MinimumWidth, PlanSheetColumns.MaximumWidth);
        ConfiguredColumns = ConfiguredColumns.Select(c => (c.Field ?? "Id") == key && !c.Indicator ? c with { Width = width } : c).ToArray();
        VisibleColumns = VisibleColumns.Select(c => (c.Field ?? "Id") == key && !c.Indicator ? c with { Width = width } : c).ToArray();
        // The document and timeline range have not changed; only realized rows need layout.
        RefreshLayout(columnResize: true);
    }
    private async Task ResizeColumn(string key, double width)
    {
        try {
            if (remoteBusy) { error.Text = "更新または発行が進行中です。"; return; }
            await Run(async () => {
                var current = PlanSheetColumns.All(Session.Document).Single(c => c.Key == key);
                width = Math.Clamp(Math.Round(width), PlanSheetColumns.MinimumWidth, PlanSheetColumns.MaximumWidth);
                if (width == (current.Width ?? DefaultColumnWidth(key))) return;
                await CommitPending();
                Check(await Session.Execute(new ReplacePlanSettings(PlanSheetColumns.Change(Session.Document, key, width: width, resize: true)), Today));
            }, "Column width");
        } finally { Refresh(); }
    }
    private double FitColumnWidth(string key)
    {
        var index = Array.FindIndex(VisibleColumns, c => (c.Field ?? "Id") == key && !c.Indicator);
        var header = columnHeaders[index].Text;
        var width = MeasureColumnText(header.Text, header.FontSize, header.FontFamily, header.FontWeight) + header.Padding.Left + header.Padding.Right;
        foreach (var row in Realized.Where(r => r.IsLoaded && r.Identity.Length > 0 && RowIndexes.ContainsKey(r.Identity))) {
            var bounds = row.TransformToVisual(List).TransformBounds(new(0, 0, row.ActualWidth, row.ActualHeight));
            if (bounds.Bottom <= 0 || bounds.Top >= List.ActualHeight) continue;
            width = Math.Max(width, row.FittedColumnWidth(key));
        }
        return Math.Clamp(Math.Ceiling(width), PlanSheetColumns.MinimumWidth, PlanSheetColumns.MaximumWidth);
    }
    internal static double MeasureColumnText(string text, double size, Microsoft.UI.Xaml.Media.FontFamily family, Windows.UI.Text.FontWeight weight)
    {
        var measure = new TextBlock { Text = text, FontSize = size, FontFamily = family, FontWeight = weight, TextWrapping = TextWrapping.NoWrap };
        Typography.SetNumeralAlignment(measure, FontNumeralAlignment.Tabular);
        measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return measure.DesiredSize.Width;
    }
}
