using System.Diagnostics;
using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, Category("PlanningPerformance")]
public sealed class SummaryPerformanceHostedTests
{
    [Test]
    public async Task ThousandTaskSummaryPublishesIndependentTotalsAndContextWithSeparatelyMeasuredSave()
    {
        var output = Environment.GetEnvironmentVariable("GHPB_SUMMARY_MEASURE_ROOT");
        if (string.IsNullOrWhiteSpace(output)) { Assert.Ignore("Opt in with a fresh GHPB_SUMMARY_MEASURE_ROOT."); return; }
        Assert.That(Directory.Exists(output), Is.False, "Measurements never overwrite an earlier run."); Directory.CreateDirectory(output);
        var (p, work) = SummaryWorkload.Create(); var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(9));
        var calculation = new List<double>(); var publication = new List<double>(); var save = new List<double>();
        for (var i = -2; i < 20; i++)
        {
            var watch = Stopwatch.StartNew(); var projection = SummaryProjection.Create(work, p, today); watch.Stop();
            Assert.That(projection.TaskCount, Is.EqualTo(1000)); Assert.That(projection.People.Single(p => p.Id == "U1").Forecast.Hours, Is.EqualTo(120));
            Assert.That(projection.People.Single(p => p.Id == "U2").Headroom, Is.EqualTo(-16)); if (i >= 0) calculation.Add(watch.Elapsed.TotalMilliseconds);
        }
        var session = new DraftSession(new DraftStore(Path.Combine(output, "session")), work, 0); Assert.That(await session.FlushAsync(), Is.True);
        EditingGrid grid = null!; await Ui.Run(() => { Ui.Window.AppWindow.Resize(new(1400, 1000)); grid = new(p, session, () => Task.FromResult(true), allowSummary: true); });
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridCell0_0"); await Ui.Idle();
        var scale = 0d;
        try
        {
            for (var i = -2; i < 10; i++)
            {
                await Ui.Run(() => { var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[0]; });
                var watch = Stopwatch.StartNew();
                await Ui.Run(() => { var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[2]; });
                await Ui.Until(() => Ui.Find<ListView>("SummaryPeople").Items.OfType<PersonSummary>().Single(p => p.Id == "U1").Forecast.Hours == 120
                    && EditingGrid.Descendants(Ui.Find<ListView>("SummaryPeople")).OfType<TextBlock>().Any(t => t.Text == "15"));
                await SheetNativeInput.Rendered(); watch.Stop(); if (i >= 0) publication.Add(watch.Elapsed.TotalMilliseconds);
                await Ui.Run(() => { var people = Ui.Find<ListView>("SummaryPeople"); people.SelectedItem = people.Items.OfType<PersonSummary>().Single(p => p.Id == "U2"); });
                await Ui.Until(() => Ui.Find<TextBlock>("SummaryPersonDetail").Text.Contains("5 人日 / 40 人時"));
                var store = new DraftStore(Path.Combine(output, "save-" + (i + 2))); watch.Restart(); await store.SaveAsync(work.Snapshot(), 0); watch.Stop();
                if (i >= 0) save.Add(watch.Elapsed.TotalMilliseconds);
            }
            await Ui.Run(async () => { scale = grid.XamlRoot.RasterizationScale; await ApplyInformationEvidence.Capture(grid, "summary-1000-normal"); });
            await Ui.Run(() => Ui.Window.AppWindow.Resize(new(1000, 750))); await SheetNativeInput.Rendered();
            await Ui.Run(async () => await ApplyInformationEvidence.Capture(grid, "summary-1000-narrow"));
            Assert.That(work.Journal, Is.Empty); Assert.That(work.Fields.Any(f => f.Buffer == "24未確定"), Is.True);
        }
        finally
        {
            await Ui.Unmount(grid); await Ui.Idle();
            static object Stats(List<double> samples) => new { samples, maximum = samples.Count == 0 ? (double?)null : samples.Max() };
            await File.WriteAllTextAsync(Path.Combine(output, "summary-measurements.json"), JsonSerializer.Serialize(new {
                workload = new { tasks = 1000, people = 20, changedSet = 0, operation = "weekly Summary inspection and A/B contribution drill-down", warmups = 2, calculationSamples = 20, publicationSamples = 10, saveSamples = 10 },
                expected = new { A = "160/144/48/72/120h; +40h headroom", B = "80/64/56/40/96h; 16h excess" },
                boundaries = new { calculation = "committed workspace to full Summary projection, with adopted plan cached", publication = "SelectorBar selection through rendered native value15 and render events; not presented pixels or physical scanout", save = "separate real DraftStore.SaveAsync to fresh roots; no network" },
                environment = new { os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount, rasterizationScale = scale, window = "1400x1000 then 1000x750 physical pixels", highContrast = "not run", humanAcceptance = "not run" },
                aggregationMs = Stats(calculation), uiPublicationMs = Stats(publication), durableSaveMs = Stats(save)
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.That(publication.Count, Is.EqualTo(10));
    }
}
