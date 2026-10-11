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
}
