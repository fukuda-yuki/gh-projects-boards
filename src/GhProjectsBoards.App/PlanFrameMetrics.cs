using System.Diagnostics;
using System.Text.Json;
namespace GhProjectsBoards.App;
internal sealed class PlanFrameMetrics
{
    private readonly string? path = Environment.GetEnvironmentVariable("GHPB_PLAN_METRICS");
    private long start;
    private int sequence, rows;
    private string operation = "commit";
    private readonly Dictionary<string, double> stages = [];
    private readonly Dictionary<string, double> durationsMs = [];
    private readonly Dictionary<string, int> counts = [];
    internal void Mark(string stage) { if (start != 0) stages[stage] = Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
    internal void Count(string name, int value) { if (start != 0) counts[name] = value; }
    internal long MeasureStart() => start == 0 ? 0 : Stopwatch.GetTimestamp();
    internal void AddDuration(string name, long since)
    {
        if (start == 0 || since == 0) return;
        durationsMs[name] = durationsMs.GetValueOrDefault(name) + Stopwatch.GetElapsedTime(since).TotalMilliseconds;
        counts[name + "Passes"] = counts.GetValueOrDefault(name + "Passes") + 1;
    }
    internal int Begin(int count, string operation = "commit")
    {
        if (start != 0) End(sequence, "superseded");
        stages.Clear(); durationsMs.Clear(); counts.Clear(); rows = count; this.operation = operation;
        start = Stopwatch.GetTimestamp(); return ++sequence;
    }
    internal void End(int revision, string boundary)
    {
        if (start == 0 || sequence != revision) return;
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds; start = 0;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.AppendAllText(path, JsonSerializer.Serialize(new { renderer = "winui-plan", revision, operation, boundary, elapsedMs, stages, durationsMs, counts, rows, utc = DateTimeOffset.UtcNow }) + Environment.NewLine);
        }
        catch (IOException ex) { Debug.WriteLine(ex.Message); }
    }
}
