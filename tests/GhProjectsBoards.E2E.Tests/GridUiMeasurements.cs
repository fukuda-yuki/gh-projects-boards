using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

[TestFixture]
[Category("GridMeasurement")]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class GridUiMeasurements
{
    [Test]
    public void RecordOrdinaryExecutableOperationTimes()
    {
        var samples = new List<Measurement>();
        uint dpi = 0;
        for (var sample = 0; sample <= 10; sample++)
        {
            using var app = new GridAppDriver();
            dpi = GetDpiForWindow(new nint(app.Window.Properties.NativeWindowHandle.Value));
            samples.Add(new("display-100-rows", sample, app.GridOpenMilliseconds));
            app.CloseNormally();
        }
        using (var app = new GridAppDriver())
        {
            for (var sample = 0; sample <= 10; sample++)
            {
                double editing = 0;
                for (var row = 1; row <= 10; row++)
                {
                    var currentRow = row;
                    editing += Timed(() =>
                    {
                        app.EditText(currentRow, 0, $"Edit {sample}-{currentRow}", VirtualKeyShort.RETURN);
                        GridAppDriver.Wait(() => app.Value(currentRow, 0) == $"Edit {sample}-{currentRow}");
                    });
                }
                samples.Add(new("edit-10-cells", sample, editing));
                for (var row = 1; row <= 10; row++) app.Button("GridUndoButton").Invoke();
                GridAppDriver.Wait(() => !app.Button("GridUndoButton").IsEnabled);

                app.SelectCell(1, 0);
                samples.Add(new("scroll-1-to-100-to-1", sample, Timed(() =>
                {
                    Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
                    GridAppDriver.Wait(() => app.Element("PrototypeGrid").FindFirstDescendant(cf => cf.ByAutomationId("GridRow_100")) is { IsOffscreen: false });
                    Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.HOME);
                    GridAppDriver.Wait(() => app.Cell(1, 0).Patterns.SelectionItem.Pattern.IsSelected.Value);
                })));
                foreach (var rows in new[] { 10, 100 })
                {
                    app.SelectCell(1, 0);
                    // Clicking a focused cell may start editing. Escape leaves the anchor selected.
                    Keyboard.Type(VirtualKeyShort.ESCAPE);
                    System.Windows.Clipboard.SetText(Payload(rows));
                    samples.Add(new($"paste-{rows}x5", sample, Timed(() =>
                    {
                        app.Button("GridPasteButton").Invoke();
                        GridAppDriver.Wait(() => app.Text("GridStatus") == $"範囲貼り付け：{rows * 5}セルを変更しました。");
                    })));
                    Assert.That(app.Value(rows, 0), Is.EqualTo($"Paste {rows:000}"));
                    Assert.That(app.Button("GridUndoButton").Name, Does.Contain("1 操作"));
                    samples.Add(new($"undo-{rows}x5", sample, Timed(() =>
                    {
                        app.Button("GridUndoButton").Invoke();
                        GridAppDriver.Wait(() => !app.Button("GridUndoButton").IsEnabled);
                    })));
                    Assert.That(app.Value(rows, 0), Is.EqualTo($"試験データ {rows:000}"));
                }
            }
            app.AssertNoGhCalls();
            app.Capture("grid-measurements-complete");
            app.CloseNormally();
        }
        var path = Path.Combine(Environment.GetEnvironmentVariable("GHPB_GRID_MEASUREMENTS")!, "ui-elapsed.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            boundary = "Ordinary Release executable through FlaUI; includes UI automation, keyboard input and condition waits (40 ms polling), not human reaction or display scanout. Display starts at the prototype-button Invoke after the connection window is ready. Paste excludes clipboard setup and subsequent last-row assertions; Undo ends at the disabled Undo control. Scrolling uses Ctrl+End then Ctrl+Home and checks realized rows/selection.",
            units = "milliseconds", dpi, scalingPercent = dpi / 96d * 100,
            warmupSample = 0, measuredSamples = 10, samples,
            summary = samples.Where(x => x.Sample > 0).GroupBy(x => x.Operation).Select(group =>
            {
                var sorted = group.Select(x => x.Milliseconds).Order().ToArray();
                return new { operation = group.Key, median = (sorted[4] + sorted[5]) / 2, maximum = sorted[^1] };
            })
        }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(path);
    }
    private static double Timed(Action action)
    {
        var start = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    private static string Payload(int rows) => string.Join('\n', Enumerable.Range(1, rows).Select(row =>
        $"Paste {row:000}\t{(row % 3 == 0 ? "Open" : "Closed")}\t{1000 + row}\t2030-01-01\t{new[] { "Medium", "Low", "High" }[(row - 1) % 3]}"));
    private sealed record Measurement(string Operation, int Sample, double Milliseconds);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
