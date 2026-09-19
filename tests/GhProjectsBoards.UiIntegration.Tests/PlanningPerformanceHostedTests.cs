using System.Diagnostics;
using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, Category("PlanningPerformance")]
public sealed class PlanningPerformanceHostedTests
{
    [Test]
    public async Task ThousandTaskCommittedEditRendersCompletePlanAndRetainsManualPendingAndDurableState()
    {
        var output = Environment.GetEnvironmentVariable("GHPB_PLANNING_MEASURE_ROOT");
        if (string.IsNullOrWhiteSpace(output)) { Assert.Ignore("Opt in with GHPB_PLANNING_MEASURE_ROOT and the scoped UI runner."); return; }
        Directory.CreateDirectory(output);
        var p = PlanningPathTests.Registration(1000);
        p = p with { Snapshot = p.Snapshot with {
            Items = p.Snapshot.Items.Select((item, i) => item with { Values = item.Values.Select(v => v.FieldId?.NodeId switch {
                "F-Estimate" => v with { Scalar = "16", Availability = ValueAvailability.Present },
                "F-Remaining" => v with { Scalar = (i + 1) % 30 == 0 ? "0" : "4", Availability = ValueAvailability.Present }, _ => v }).ToArray() }).ToArray(),
            Issues = p.Snapshot.Issues.ToDictionary(pair => pair.Key, pair => pair.Value with {
                Native = new([new(new(p.Snapshot.Id.Scope, "U" + ((pair.Value.Number - 1) % 20 + 1)), "Owner")],
                    (pair.Value.Number - 1) % 10 == 0 ? [] : [new(p.Snapshot.Id.Scope, "I" + (pair.Value.Number - 1))], new(ValueAvailability.Empty), true) }) } };
        var manualStart = PlanningContractTests.At("2026-10-05 12:07"); var manualFinish = PlanningContractTests.At("2026-10-06 16:19");
        var tasks = Enumerable.Range(1, 1000).Select(i => new PlanningTask("I" + i, i % 50 == 0 ? PlanningMode.Manual : PlanningMode.Auto, "U" + ((i - 1) % 20 + 1),
            i % 50 == 0 ? manualStart : null, i % 50 == 0 ? manualFinish : null,
            i % 30 == 0 ? PlanningProgress.Completed : i % 7 == 0 ? PlanningProgress.InProgress : PlanningProgress.Unstarted,
            i % 30 == 0 || i % 7 == 0 ? PlanningContractTests.At("2026-10-05 09:00") : null,
            i % 30 == 0 ? PlanningContractTests.At("2026-10-06 18:00") : null,
            Actuals: i % 7 == 0 || i % 30 == 0 ? [new("U" + ((i - 1) % 20 + 1), 5, new(2026, 10, 6))] : null)).ToArray();
        var plan = PlanningPathTests.Plan() with { Tasks = tasks,
            People = Enumerable.Range(1, 20).Select(i => new PlanningPerson("U" + i, "Person " + i, i % 3 == 0 ? 50 : i % 3 == 2 ? 80 : 100)).ToArray() };
        var work = new EditingWorkspace(p.Snapshot.Id.Scope); work.SetRegistrations([p]); work.CommitPlanning(p, plan, work.Revision);
        var pending = work.Open(p)[999].Cells.Single(c => c.Key?.FieldId == "F-Estimate"); work.SetBuffer(pending, "24未確定");
        var initial = work.PlanFor(p); var calculation = new List<double>();
        for (var i = 0; i < 22; i++) {
            var timer = Stopwatch.StartNew(); var result = PlanningEngine.Calculate(initial.Configuration!, initial.Inputs!, work.Revision); timer.Stop();
            Assert.That(result.Tasks, Has.Length.EqualTo(1000)); if (i >= 2) calculation.Add(timer.Elapsed.TotalMilliseconds);
        }
        var session = new DraftSession(new DraftStore(Path.Combine(output, "session")), work, 0); await session.FlushAsync();
        var clipboard = "24"; EditingGrid grid = null!;
        await Ui.Run(() => grid = new EditingGrid(p, session, () => Task.FromResult(true), readClipboard: () => Task.FromResult(clipboard)));
        await Ui.Mount(grid); var visible = new List<double>(); var save = new List<double>();
        try
        {
            await Ui.Ready<TextBox>("GridCell0_2"); await Ui.Idle();
            for (var sample = 0; sample < 12; sample++)
            {
                clipboard = sample % 2 == 0 ? "24" : "16";
                var expected = sample % 2 == 0 ? "2026-10-07" : "2026-10-06";
                await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Focus(FocusState.Keyboard));
                await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T1");
                var timer = Stopwatch.StartNew();
                await Ui.ClickCommand("GridPaste");
                await Ui.Until(() => Ui.Find<TextBox>("GridCell0_6").Text == expected);
                await Ui.Run(async () => {
                    var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var frames = 0;
                    EventHandler<object> frame = (_, _) => { if (++frames >= 2) presented.TrySetResult(); };
                    CompositionTarget.Rendering += frame;
                    try { await presented.Task.WaitAsync(TimeSpan.FromSeconds(5)); } finally { CompositionTarget.Rendering -= frame; }
                });
                timer.Stop(); if (sample >= 2) visible.Add(timer.Elapsed.TotalMilliseconds);
                await Ui.Idle(); await session.FlushAsync();
                var snapshot = session.Workspace.Snapshot();
                var store = new DraftStore(Path.Combine(output, "save-" + sample));
                timer.Restart(); await store.SaveAsync(snapshot, 0); timer.Stop();
                if (sample >= 2) save.Add(timer.Elapsed.TotalMilliseconds);
            }
            await Ui.Run(async () => await ApplyInformationEvidence.Capture(grid, "planning-1000-rendered"));
            var final = session.Workspace.PlanFor(p);
            Assert.That(final.Tasks, Has.Length.EqualTo(1000));
            Assert.That(final.Tasks.Where(t => t.Mode == PlanningMode.Manual).All(t => t.Start == manualStart && t.Finish == manualFinish), Is.True);
            Assert.That(session.Workspace.Buffer(pending), Is.EqualTo("24未確定")); Assert.That(session.Workspace.Journal, Is.Empty);
            var restored = EditingWorkspace.Restore((await new DraftStore(Path.Combine(output, "session")).LoadAsync(work.Scope))!);
            Assert.That(restored.PlanFor(p).Tasks, Has.Length.EqualTo(1000)); Assert.That(restored.Buffer(pending), Is.EqualTo("24未確定"));
        }
        finally { await Ui.Unmount(grid); await Ui.Idle(); }
        static object Stats(List<double> values) => new { samples = values, p95 = values.Order().ElementAt((int)Math.Ceiling(values.Count * .95) - 1), maximum = values.Max() };
        await File.WriteAllTextAsync(Path.Combine(output, "measurements.json"), JsonSerializer.Serialize(new {
            workload = new { tasks = 1000, people = 20, fields = 6, fsEdges = 900, graph = "100 independent chains of 10; mixed Auto/Manual/Completed/InProgress", manualTasks = 20, start = plan.Start,
                warmups = 2, visibleSamples = 10, calculationSamples = 20, changed = "one root estimate 16/24; chain descendants recomputed; one offscreen pending buffer" },
            environment = new { machine = Environment.MachineName, os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount },
            boundaries = new { calculation = "pure full-graph engine", visible = "native command invocation through correct cell text and two composition rendering events; not physical scanout; save may overlap",
                durableSave = "independent real DraftStore.SaveAsync to new isolated root; no network", network = "none" },
            calculationMs = Stats(calculation), visibleMs = Stats(visible), durableSaveMs = Stats(save)
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.That(visible.Max(), Is.LessThanOrEqualTo(1000), "Engineering recalc-to-render target; raw attempts are retained.");
    }
}
