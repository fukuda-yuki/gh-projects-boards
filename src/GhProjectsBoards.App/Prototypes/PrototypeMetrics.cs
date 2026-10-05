using System.Diagnostics;
using System.Text.Json;
namespace GhProjectsBoards.App.Prototypes;
internal sealed class PrototypeMetrics(string renderer)
{
    private readonly string? path = Environment.GetEnvironmentVariable("GHPB_PROTOTYPE_METRICS");
    private long start;
    private int sequence;
    public int Begin()
    {
        if (start != 0) End(sequence, "superseded");
        start = Stopwatch.GetTimestamp(); return ++sequence;
    }
    public void End(int revision, string boundary, double? clientElapsedMs = null)
    {
        if (start == 0 || revision != sequence) return;
        var hostMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var ms = clientElapsedMs is >= 0 && double.IsFinite(clientElapsedMs.Value) ? clientElapsedMs.Value : hostMs; start = 0;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.AppendAllText(path, JsonSerializer.Serialize(new { renderer, revision, boundary, elapsedMs = ms, hostElapsedMs = hostMs, rows = 1000, utc = DateTimeOffset.UtcNow }) + Environment.NewLine);
        }
        catch (IOException e) { System.Diagnostics.Debug.WriteLine(e.Message); }
    }
}

