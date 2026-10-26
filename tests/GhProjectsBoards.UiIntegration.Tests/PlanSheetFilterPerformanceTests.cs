using System.Diagnostics;
using System.Text.Json;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanSheetHostedTests
{
    [Test, Category("PlanSheetPerformance")]
    public async Task VersionRowsFilterApplicationRecordsPauseAndFrameTimings()
    {
        Assert.That(session.Document.State.Rows.Length, Is.EqualTo(1040));
        await Ui.Run(() => Ui.Window.AppWindow.Resize(new(1920, 1080)));
        await Ui.Until(() => Ui.Root.ActualWidth * sheet.XamlRoot.RasterizationScale > 1850);
        await SheetNativeInput.Rendered();
        var before = session.Document;
        var inputs = new List<object>();
        double clientWidth = 0, clientHeight = 0, scale = 0;
        await Ui.Run(() => {
            clientWidth = Ui.Root.ActualWidth; clientHeight = Ui.Root.ActualHeight; scale = sheet.XamlRoot.RasterizationScale;
            Assert.That(Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic), Is.True);
        });
        var outcome = "incomplete";
        try
        {
            foreach (var pauseMs in new[] { 200, 700 })
            {
                var text = "";
                foreach (var character in "PS-00")
                {
                    text += character;
                    var proposed = text;
                    var requested = Stopwatch.GetTimestamp();
                    await Ui.Run(() => {
                        Ui.Find<TextBox>("PlanSheetFilter").Text = proposed;
                        inputs.Add(new { pauseMs, text = proposed, dispatchAndTextChangeMs = Stopwatch.GetElapsedTime(requested).TotalMilliseconds });
                    });
                    await Task.Delay(pauseMs);
                }
                // #227/#260: each matching task's requirement rows stay visible as dimmed context and are
                // not counted, so the version shows 120 matches plus their 40 requirement parents and the
                // new-task row; the count text states the 120 matching rows only.
                await Ui.Until(() => sheet.List.Items.Count == 161
                    && Ui.Find<TextBlock>("PlanSheetFilterCount").Text == "120 件");
                await SheetNativeInput.Rendered();
                await Ui.Run(() => {
                    Assert.That(session.Document, Is.SameAs(before));
                    Assert.That(session.UndoCount, Is.Zero);
                    Ui.Find<TextBox>("PlanSheetFilter").Text = "";
                });
                await Ui.Until(() => sheet.List.Items.Count == 1041);
                await SheetNativeInput.Rendered();
            }
            outcome = "completed";
        }
        finally
        {
            var evidence = Environment.GetEnvironmentVariable("GHPB_PLAN_EVIDENCE") ?? TestContext.CurrentContext.WorkDirectory;
            Directory.CreateDirectory(evidence);
            if (File.Exists(metricsPath)) File.Copy(metricsPath!, Path.Combine(evidence, "plan-version-filter-frames.jsonl"), true);
            var summary = JsonSerializer.Serialize(new {
                outcome, version = EvaluationFixture.DefaultVersion, rows = 1040, clientWidth, clientHeight, scale,
                environment = Environment.OSVersion.ToString(), inputs,
                boundary = "Hosted dispatcher request to TextBox.Text assignment and accepted filter to CompositionTarget.Rendered; excludes native key and physical display latency"
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidence, "plan-version-filter-measurement.json"), summary);
            TestContext.Out.WriteLine(summary);
        }
    }
}
