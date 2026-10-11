using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using NUnit.Framework;
using Path = System.IO.Path;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanSheetHostedTests
{
    [Test, Category("PlanSheetPerformance")]
    public async Task VersionRowsCommitToRenderedDatesAndBarsRecordsTwentySamples()
    {
        Assert.That(session.Document.State.Rows.Length, Is.EqualTo(1040));
        await Ui.Run(() => Ui.Window.AppWindow.Resize(new(1920, 1080)));
        await Ui.Until(() => Ui.Root.ActualWidth * sheet.XamlRoot.RasterizationScale > 1850);
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "日");
        await Ui.Idle();
        var target = session.Document.State.Rows.First(r => r.Parent is not null && !r.Closed && r.Remaining > 0);
        var number = session.Document.State.Rows.IndexOf(target) + 1;
        await Select(number, PlanField.Remaining);
        await Ui.Ready<TextBox>($"PlanCell{number}_Remaining");
        for (var i = 0; i < 20; i++)
        {
            var remaining = target.Remaining!.Value + (i % 2 == 0 ? 8 : 0);
            await Edit(number, PlanField.Remaining, remaining.ToString(CultureInfo.InvariantCulture));
            await Ui.Until(() => File.Exists(metricsPath) && File.ReadAllLines(metricsPath!).Count(line => line.Contains("\"boundary\":\"rendered\"")) == i + 1);
            await Ui.Run(() => {
                Assert.That(session.Document.State.Rows[number - 1].Remaining, Is.EqualTo(remaining));
                var end = Ui.Find<TextBox>($"PlanCell{number}_End");
                var bar = Ui.Find<Shape>("PlanBar" + number);
                Assert.That(end.Text, Is.Not.Empty);
                var endDate = Regex.Match(AutomationProperties.GetName(end), @"\d{4}-\d{2}-\d{2}").Value;
                Assert.That(endDate, Is.Not.Empty);
                Assert.That(AutomationProperties.GetName(bar), Does.Contain(endDate));
                Assert.That(bar.Width, Is.GreaterThan(0));
            });
        }
        var records = File.ReadAllLines(metricsPath!).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var samples = records.Where(r => r.GetProperty("boundary").GetString() == "rendered").Select(r => r.GetProperty("elapsedMs").GetDouble()).Order().ToArray();
        Assert.That(samples.Length, Is.EqualTo(20));
        Assert.That(records.Length, Is.EqualTo(20), "Rejected, superseded and missing frames are not valid timing samples.");
        var evidence = Environment.GetEnvironmentVariable("GHPB_PLAN_EVIDENCE") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(evidence);
        File.Copy(metricsPath!, Path.Combine(evidence, "plan-version-frames.jsonl"), true);
        double clientWidth = 0, clientHeight = 0, scale = 0;
        await Ui.Run(() => { clientWidth = Ui.Root.ActualWidth; clientHeight = Ui.Root.ActualHeight; scale = sheet.XamlRoot.RasterizationScale; });
        var summary = JsonSerializer.Serialize(new { version = EvaluationFixture.DefaultVersion, rows = 1040, samples = samples.Length,
            target.Identity, number, medianMs = (samples[9] + samples[10]) / 2, maxMs = samples[^1], targetMs = 200,
            withinTarget = samples.All(ms => ms <= 200), clientWidth, clientHeight, scale,
            environment = Environment.OSVersion.ToString(),
            boundary = "Version-fixture cell commit through Core acceptance and visible dates/bars to CompositionTarget.Rendered; not physical display latency" });
        File.WriteAllText(Path.Combine(evidence, "plan-version-measurement.json"), summary);
        TestContext.Out.WriteLine(summary);
        await Ui.Run(async () => await RenderedEvidence.Capture(sheet, "sheet-version-edit"));
    }
}
