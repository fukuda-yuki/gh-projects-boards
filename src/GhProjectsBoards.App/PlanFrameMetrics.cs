using System.Diagnostics;
using System.Text.Json;
namespace GhProjectsBoards.App;
internal sealed class PlanFrameMetrics
{
    private readonly string? path = Environment.GetEnvironmentVariable("GHPB_PLAN_METRICS");
    private long start;
    private int sequence, rows;
    private readonly Dictionary<string, double> stages = [];
    internal void Mark(string stage) { if (start != 0) stages[stage] = Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
    internal int Begin(int count)
    {
        if (start != 0) End(sequence, "superseded");
        stages.Clear(); rows = count; start = Stopwatch.GetTimestamp(); return ++sequence;
    }
    internal void End(int revision, string boundary)
    {
        if (start == 0 || sequence != revision) return;
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds; start = 0;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.AppendAllText(path, JsonSerializer.Serialize(new { renderer = "winui-plan", revision, boundary, elapsedMs, stages, rows, utc = DateTimeOffset.UtcNow }) + Environment.NewLine);
        }
        catch (IOException ex) { Debug.WriteLine(ex.Message); }
    }
}
