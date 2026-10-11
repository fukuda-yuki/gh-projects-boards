using System.Diagnostics;
using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanSheetHostedTests
{
    private sealed record ScrollFrame(double ElapsedMs, double IntervalMs, string Phase, double RequestedOffset,
        double VerticalOffset, double HorizontalOffset, int[] UnpopulatedRows);

    [Test, Category("PlanSheetPerformance")]
    public async Task VersionRowsScrollRoundTripRecordsFrameIntervalsAndViewportPopulation()
    {
        const double verticalStep = 560, horizontalStep = 240;
        var titles = session.Document.State.Rows.ToDictionary(r => r.Identity, r => r.Title);
        var frames = new List<ScrollFrame>();
        var phase = "prepare";
        var requestedOffset = 0d;
        var outcome = "incomplete";
        string? failure = null;
        ScrollViewer vertical = null!, horizontal = null!;
        double verticalExtent = 0, horizontalExtent = 0, scale = 0, clientWidth = 0, clientHeight = 0;
        var timer = new Stopwatch();
        var previous = 0d;
        TaskCompletionSource? nextFrame = null;
        var evidence = Environment.GetEnvironmentVariable("GHPB_PLAN_EVIDENCE")
            ?? Path.Combine(Path.GetTempPath(), "ghpb-plan-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);

        void Rendered(object? sender, RenderedEventArgs args)
        {
            var elapsed = timer.Elapsed.TotalMilliseconds;
            var missing = new List<int>();
            var first = Math.Max(0, (int)Math.Floor(vertical.VerticalOffset / sheet.RowHeight));
            var last = Math.Min(sheet.List.Items.Count - 1,
                (int)Math.Ceiling((vertical.VerticalOffset + vertical.ViewportHeight) / sheet.RowHeight) - 1);
            for (var index = first; index <= last; index++)
            {
                var identity = (string)sheet.List.Items[index];
                var container = sheet.List.ContainerFromIndex(index) as ListViewItem;
                var row = container?.ContentTemplateRoot as PlanSheetRow;
                var title = row?.Cells.Single(c => c.Field == GhProjectsBoards.Core.PlanEditor.PlanField.Title);
                var y = row?.TransformToVisual(vertical).TransformPoint(new()).Y ?? double.NaN;
                if (row is not { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } || row.Identity != identity || row.Owner != sheet
                    || title?.Text != titles.GetValueOrDefault(identity, "")
                    || Math.Abs(y - (index * sheet.RowHeight - vertical.VerticalOffset)) > 2)
                    missing.Add(index + 1);
            }
            lock (frames) frames.Add(new(elapsed, elapsed - previous, phase, requestedOffset,
                vertical.VerticalOffset, horizontal.HorizontalOffset, missing.ToArray()));
            previous = elapsed;
            nextFrame?.TrySetResult();
        }

        async Task Move(ScrollViewer scroll, double target, bool chart)
        {
            var deadline = Stopwatch.StartNew();
            var moving = false;
            await Ui.Run(() => {
                moving = Math.Abs((chart ? scroll.HorizontalOffset : scroll.VerticalOffset) - target) >= 1;
                if (!moving) return;
                requestedOffset = target;
                nextFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
                scroll.ChangeView(chart ? target : null, chart ? null : target, null, true);
            });
            if (!moving) return;
            while (true)
            {
                await nextFrame!.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var reached = false;
                await Ui.Run(() => {
                    reached = Math.Abs((chart ? scroll.HorizontalOffset : scroll.VerticalOffset) - target) < 1;
                    if (!reached) nextFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
                });
                if (reached) return;
                if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Scroll did not reach " + target);
            }
        }

        try
        {
            Assert.That(session.Document.State.Rows.Length, Is.EqualTo(1040));
            await Ui.Run(() => Ui.Window.AppWindow.Resize(new(1920, 1080)));
            await Ui.Run(() => {
                vertical = Ui.Tree(sheet.List).OfType<ScrollViewer>().Single();
                horizontal = Ui.Find<ScrollViewer>("PlanGanttHorizontal");
                vertical.ChangeView(null, 0, null, true);
                horizontal.ChangeView(0, null, null, true);
            });
            await Ui.Until(() => vertical.VerticalOffset == 0 && horizontal.HorizontalOffset == 0 && vertical.ScrollableHeight > 0);
            await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                verticalExtent = vertical.ScrollableHeight; horizontalExtent = horizontal.ScrollableWidth;
                Assert.That(verticalExtent, Is.GreaterThan(0));
                Assert.That(horizontalExtent, Is.GreaterThan(0), "The fixture must exercise Gantt scrolling.");
                scale = sheet.XamlRoot.RasterizationScale; clientWidth = Ui.Root.ActualWidth; clientHeight = Ui.Root.ActualHeight;
                phase = "vertical-down"; timer.Start(); CompositionTarget.Rendered += Rendered;
            });
            for (var offset = verticalStep; offset < verticalExtent; offset += verticalStep) await Move(vertical, offset, false);
            await Move(vertical, verticalExtent, false);
            await Ui.Run(() => phase = "vertical-up");
            for (var offset = Math.Max(0, verticalExtent - verticalStep); offset > 0; offset -= verticalStep) await Move(vertical, offset, false);
            await Move(vertical, 0, false);
            await Ui.Run(() => phase = "gantt-right");
            for (var offset = horizontalStep; offset < horizontalExtent; offset += horizontalStep) await Move(horizontal, offset, true);
            await Move(horizontal, horizontalExtent, true);
            await Ui.Run(() => phase = "gantt-left");
            for (var offset = Math.Max(0, horizontalExtent - horizontalStep); offset > 0; offset -= horizontalStep) await Move(horizontal, offset, true);
            await Move(horizontal, 0, true);
            outcome = "completed";
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            try {
                await Ui.Run(() => { CompositionTarget.Rendered -= Rendered; timer.Stop(); return Task.CompletedTask; }, check: false);
            } catch (Exception ex) {
                outcome = "incomplete"; failure = (failure is null ? "" : failure + "\n") + "Cleanup: " + ex;
                throw;
            } finally {
                ScrollFrame[] samples;
                lock (frames) samples = frames.ToArray();
                var result = new {
                    schemaVersion = 1, outcome, failure, version = EvaluationFixture.DefaultVersion,
                    rows = session.Document.State.Rows.Length, statusDate = session.Document.State.Settings.StatusDate,
                    windowPixels = new { width = 1920, height = 1080 }, clientWidth, clientHeight, scale,
                    verticalStep, horizontalStep, verticalExtent, horizontalExtent,
                    environment = new { os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(),
                        processors = Environment.ProcessorCount, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() },
                    longFrameThresholdMs = 50, frameCount = samples.Length,
                    longFrames = samples.Count(f => f.IntervalMs > 50),
                    unpopulatedFrames = samples.Count(f => f.UnpopulatedRows.Length > 0),
                    maxFrameMs = samples.Length == 0 ? (double?)null : samples.Max(f => f.IntervalMs), frames = samples,
                    boundary = "Hosted CompositionTarget.Rendered callback intervals and fixed-height viewport population; not physical-display flicker detection"
                };
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                var path = Path.Combine(evidence, "plan-scroll.json");
                File.WriteAllText(path, json);
                TestContext.Out.WriteLine($"Scroll evidence: {path}; outcome={outcome}; frames={samples.Length}; long={samples.Count(f => f.IntervalMs > 50)}; unpopulated={samples.Count(f => f.UnpopulatedRows.Length > 0)}");
            }
        }
    }
}
