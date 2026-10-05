using System.Globalization;
namespace GhProjectsBoards.Core.Prototypes;

// Deliberately disposable rendering workload, not the #78 scheduling engine.
public sealed record PrototypeRow(int Id, string Title, double Remaining, string RequestedStart, string Start, string End, int Predecessor, string Person);
public sealed class PrototypePlan
{
    public PrototypeRow[] Rows { get; private set; }
    public PrototypePlan()
    {
        Rows = Enumerable.Range(1, 1000).Select(i => new PrototypeRow(i, $"作業 {i}", 8, "2026-10-05", "", "", i % 10 == 1 ? 0 : i - 1, $"担当 {(i - 1) % 20 + 1:00}")).ToArray();
        Recalculate(Rows);
    }
    public void Edit(int index, int column, string value)
    {
        if (index is < 0 or >= 1000 || column is < 0 or > 2) throw new ArgumentException("Invalid cell");
        var candidate = Rows.ToArray();
        var row = candidate[index];
        if (column == 0) row = row with { Title = value };
        if (column == 1)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) || !double.IsFinite(hours) || hours is < 0 or > 8000)
                throw new ArgumentException("Remaining must be 0–8000 hours in this prototype");
            row = row with { Remaining = hours };
        }
        if (column == 2)
        {
            if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new ArgumentException("Use yyyy-MM-dd");
            row = row with { RequestedStart = value };
        }
        candidate[index] = row;
        try { Recalculate(candidate); }
        catch (ArgumentOutOfRangeException e) { throw new ArgumentException("Date outside supported range", e); }
        Rows = candidate;
    }
    private static void Recalculate(PrototypeRow[] rows)
    {
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i]; var start = DateOnly.ParseExact(row.RequestedStart, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (row.Predecessor > 0)
            {
                var predecessorEnd = DateOnly.ParseExact(rows[row.Predecessor - 1].End, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (predecessorEnd > start) start = predecessorEnd;
            }
            var end = start.AddDays((int)Math.Ceiling(row.Remaining / 8));
            rows[i] = row with { Start = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), End = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        }
    }
}
