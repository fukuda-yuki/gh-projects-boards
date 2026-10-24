using System.Globalization;
namespace GhProjectsBoards.App;

internal static class PlanSummaryPresentation
{
    internal static string Percent(decimal? ratio)
    {
        if (ratio is null) return "—";
        var percent = ratio.Value * 100;
        // A small overload must not look like exactly available capacity.
        var whole = percent > 100 ? decimal.Ceiling(percent) : decimal.Round(percent, 0, MidpointRounding.AwayFromZero);
        return whole.ToString("#,0", CultureInfo.GetCultureInfo("ja-JP")) + "%";
    }
    // Rounding must not announce completion while a counted task is still open.
    internal static string Progress(decimal? ratio, bool allClosed)
        => ratio is not null && !allClosed && Math.Round(ratio.Value * 100, 0, MidpointRounding.AwayFromZero) >= 100 ? "99%" : Percent(ratio);
}
