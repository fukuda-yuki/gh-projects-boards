using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GhProjectsBoards.App.GridPrototype;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
[Category("GridMeasurement")]
[Explicit("Run scripts/Measure-Grid.ps1 to collect measurements separately from correctness tests.")]
internal sealed class GridProcessingMeasurements
{
    [Test]
    public void RecordApplicationProcessingTimes()
    {
        var samples = new List<Measurement>();
        for (var sample = 0; sample <= 10; sample++)
        {
            var started = Stopwatch.GetTimestamp();
            var model = new GridPrototypeViewModel();
            samples.Add(new("initialize-100-rows", sample, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            Assert.That(model.Rows, Has.Count.EqualTo(100));
            double editing = 0;
            for (var row = 1; row <= 10; row++)
            {
                Assert.That(model.Edit(new(row, GridField.Title), $"Edit {sample}-{row}").Succeeded, Is.True);
                editing += model.LastProcessingMilliseconds;
            }
            samples.Add(new("edit-10-cells", sample, editing));
            for (var row = 1; row <= 10; row++) Assert.That(model.Undo(), Is.True);

            foreach (var rows in new[] { 10, 100 })
            {
                var baseline = model.Rows.Select(row => row.Values).ToArray();
                var result = model.Paste(new(1, GridField.Title), Payload(rows));
                samples.Add(new($"paste-{rows}x5", sample, model.LastProcessingMilliseconds));
                Assert.That(result.ChangedCells, Is.EqualTo(rows * 5));
                Assert.That(model.UndoCount, Is.EqualTo(1));
                Assert.That(model.Undo(), Is.True);
                samples.Add(new($"undo-{rows}x5", sample, model.LastProcessingMilliseconds));
                Assert.That(model.Rows.Select(row => row.Values), Is.EqualTo(baseline));
            }
        }
        var resultPath = Path.Combine(Environment.GetEnvironmentVariable("GHPB_GRID_MEASUREMENTS")!, "application-processing.json");
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new
        {
            boundary = "Real application ViewModel, validation, history, and notifications in process; no WPF subscribers, rendering, clipboard, or UI automation. Initialization is measured around construction; other samples use the application stopwatch.",
            units = "milliseconds", warmupSample = 0, measuredSamples = 10, samples,
            summary = samples.Where(x => x.Sample > 0).GroupBy(x => x.Operation).Select(group =>
            {
                var sorted = group.Select(x => x.Milliseconds).Order().ToArray();
                return new { operation = group.Key, median = (sorted[4] + sorted[5]) / 2, maximum = sorted[^1] };
            })
        }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(resultPath);
    }

    private static string Payload(int rows) => string.Join('\n', Enumerable.Range(1, rows).Select(row =>
        $"Paste {row:000}\t{(row % 3 == 0 ? "Open" : "Closed")}\t{1000 + row}\t2030-01-01\t{new[] { "Medium", "Low", "High" }[(row - 1) % 3]}"));
    private sealed record Measurement(string Operation, int Sample, double Milliseconds);
}
