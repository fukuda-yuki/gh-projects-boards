using System.Diagnostics;
using System.Text.Json;
namespace GhProjectsBoards.App.Prototypes;
internal sealed class PrototypeMetrics
{
    private readonly string? path = Environment.GetEnvironmentVariable("GHPB_PROTOTYPE_METRICS");
    private long start;
    private int sequence;
    public int Begin()
    {
        if (start != 0) End(sequence, "superseded");
        start = Stopwatch.GetTimestamp(); return ++sequence;
    }
    public void End(int revision, string boundary)
    {
        if (start == 0 || revision != sequence) return;
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds; start = 0;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.AppendAllText(path, JsonSerializer.Serialize(new { renderer = "winui", revision, boundary, elapsedMs, rows = 1000, utc = DateTimeOffset.UtcNow }) + Environment.NewLine);
        }
        catch (IOException e) { System.Diagnostics.Debug.WriteLine(e.Message); }
    }
}

