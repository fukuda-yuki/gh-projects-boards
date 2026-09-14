using System.Diagnostics;
using System.Text.Json;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Tests;

internal static class PerformanceRun
{
    public static async Task<int> Run(string root, int count, int changes, bool mixed, int samples, bool instrument, string field = "Title")
    {
        if (!Path.IsPathFullyQualified(root) || Directory.Exists(root)) return 2;
        Directory.CreateDirectory(root);
        var plan = new { count, changes, mixed, samples, field, warmup = 1, instrument, frequency = Stopwatch.Frequency,
            boundary = "Core with in-process synthetic gh responses; production waits and actual checkpoint I/O", runtime = Environment.Version.ToString(),
            processorCount = Environment.ProcessorCount, os = Environment.OSVersion.ToString() };
        await File.WriteAllTextAsync(Path.Combine(root, "plan.json"), JsonSerializer.Serialize(plan));
        for (int sample = -1; sample < samples; sample++)
        {
            var data = Path.Combine(root, sample < 0 ? "warmup" : "sample-" + sample);
            var h = await ApplyTests.Harness.Create(count, data);
            var w = h.Workspace.Drafts!.Workspace;
            var rows = w.Open(h.Workspace.Selected!);
            if (mixed)
            {
                // Retained success and unresolved attempt use the real executor/store, outside measurement.
                w.Commit("P1", rows[^1].Cells[0], "History A");
                await h.Workspace.PrepareApplyAsync(new HashSet<string> { rows[^1].ItemId });
                await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
                w = h.Workspace.Drafts.Workspace;
                rows = w.Open(h.Workspace.Selected!);
                w.Commit("P1", rows[^2].Cells[0], "History B");
                await h.Workspace.PrepareApplyAsync(new HashSet<string> { rows[^2].ItemId });
                h.LoseResponse = true; await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!); h.LoseResponse = false;
                await h.Workspace.SupersedeApplyAsync(h.Workspace.Drafts!.Workspace.Journal.Last().Id);
                w = h.Workspace.Drafts.Workspace; rows = w.Open(h.Workspace.Selected!);
                w.AddRow(h.Workspace.Selected!);
                w.SetBuffer(rows[^3].Cells[0], "pending\ntext");
            }
            for (int i = 0; i < changes; i++)
                if (field == "Select") { if (i % 2 == 0) w.Commit("P1", rows[i].Cells[1], "done", true); else w.Clear("P1", [rows[i].Cells[1]]); }
                else w.Commit("P1", rows[i].Cells[0], "Measured " + i.ToString("D4"));
            await h.Workspace.Drafts!.FlushAsync();
            var durable = (await new DraftStore(data).LoadAsync(w.Scope))!;
            if (mixed && (durable.LocalRows!.Length != 1 || durable.Fields.Count(f => f.Buffer is not null) != 1
                || !durable.Journal!.Any(b => b.Operations.Any(o => o.State == ApplyState.Succeeded))
                || !durable.Journal!.Any(b => b.Operations.Any(o => o.State == ApplyState.Superseded && o.Attempts.Any(a => a.State == ApplyState.Unknown)))))
                throw new InvalidOperationException("Mixed initial fixture failed durable verification");
            h.Writes.Clear(); h.Boundary.Runner.Commands.Clear();
            var trace = instrument ? new PerformanceTrace() : null;
            var timer = Stopwatch.StartNew();
            using (PerformanceTrace.Span("prepare")) await h.Workspace.PrepareApplyAsync(rows.Take(Math.Max(changes, 1)).Select(r => r.ItemId).ToHashSet());
            var prepare = timer.Elapsed.TotalMilliseconds;
            var review = h.Workspace.ApplyReview ?? throw new InvalidOperationException(h.Workspace.Status);
            if (review.Batch.Operations.Length != changes) throw new InvalidOperationException("Unexpected review count");
            timer.Restart(); // User review is outside both measured execution intervals.
            using (PerformanceTrace.Span("execute")) await h.Workspace.ConfirmApplyAsync(review);
            var execute = timer.Elapsed.TotalMilliseconds;
            trace?.Dispose();
            var journal = h.Workspace.Drafts!.Workspace.Journal.SingleOrDefault(b => b.Id == review.Batch.Id);
            var success = h.Writes.Count == changes && journal is not null && journal.Operations.All(o => o.State == ApplyState.Succeeded);
            var result = new { sample, prepareMs = prepare, executeMs = execute, endToEndMs = prepare + execute, success,
                mutations = h.Writes.Count, spans = trace?.Samples, journalOperations = journal?.Operations.Length };
            await File.WriteAllTextAsync(Path.Combine(root, sample < 0 ? "warmup.json" : $"sample-{sample}.json"), JsonSerializer.Serialize(result));
            Console.WriteLine($"sample={sample} items={count} changes={changes} prepare={prepare:F1} execute={execute:F1} success={success}");
            if (!success) return 1;
        }
        return 0;
    }
}
