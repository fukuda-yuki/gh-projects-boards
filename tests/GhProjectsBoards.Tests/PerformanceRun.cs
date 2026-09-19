using System.Diagnostics;
using System.Text.Json;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Tests;

internal static class PerformanceRun
{
    public static async Task<int> Run(string root, int count, int changes, bool mixed, int samples, bool instrument, string field, string referenceRoot)
    {
        if (!Path.IsPathFullyQualified(root) || Directory.Exists(root)) return 2;
        Directory.CreateDirectory(root);
        if (field is not ("Title" or "Select" or "Both") || changes < 0 || changes > count) throw new ArgumentException("Invalid workload");
        var expectedFields = changes * (field == "Both" ? 2 : 1);
        var plan = new { count, changedRows = changes, changedFields = expectedFields, mixed, samples, field, warmup = 1, instrument, frequency = Stopwatch.Frequency,
            boundary = "Core with in-process synthetic gh responses; production waits and actual checkpoint I/O", runtime = Environment.Version.ToString(),
            processorCount = Environment.ProcessorCount, os = Environment.OSVersion.ToString() };
        await File.WriteAllTextAsync(Path.Combine(root, "plan.json"), JsonSerializer.Serialize(plan));
        var calibration = new List<double>();
        for (var i = -1; i < 5; i++)
        {
            var process = await new GhProjectsBoards.App.GitHub.GhProcessRunner().RunAsync(new(@"C:\Program Files\GitHub CLI\gh.exe", ["--version"]));
            if (process.ExitCode != 0) throw new InvalidOperationException("Installed gh calibration failed");
            if (i >= 0) calibration.Add(process.Elapsed.TotalMilliseconds);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "process-calibration.json"), JsonSerializer.Serialize(new {
            boundary = "Installed gh --version process startup/exit only; no authentication/network; never subtracted from product spans", warmup = 1, samplesMs = calibration }));
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
            {
                if (field is "Select" or "Both") { if (i % 2 == 0) w.Commit("P1", rows[i].Cells[1], "done", true); else w.Clear("P1", [rows[i].Cells[1]]); }
                if (field is "Title" or "Both") w.Commit("P1", rows[i].Cells[0], "Measured " + i.ToString("D4"));
            }
            await h.Workspace.Drafts!.FlushAsync();
            var checkpoint = new DraftStore(data).FileFor(w.Scope);
            var seedName = sample < 0 ? "warmup-seed.json" : $"sample-{sample}-seed.json";
            var remoteName = sample < 0 ? "warmup-remote.json" : $"sample-{sample}-remote.json";
            if (referenceRoot != "none")
            {
                // Exact immutable initial profile from the baseline, including IDs, timestamps and retained attempts.
                File.Copy(Path.Combine(referenceRoot, seedName), checkpoint, true);
                var remoteState = JsonSerializer.Deserialize<RemoteState>(await File.ReadAllTextAsync(Path.Combine(referenceRoot, remoteName)))!;
                h.Titles.Clear(); foreach (var pair in remoteState.Titles) h.Titles.Add(pair.Key, pair.Value);
                h.Selects.Clear(); foreach (var pair in remoteState.Selects) h.Selects.Add(pair.Key, pair.Value);
                h.Workspace = new(new(h.Root)); await h.Workspace.RestoreAsync();
                await h.Workspace.BindAsync(h.Context, h.Service); await h.Workspace.SelectAsync(new(w.Scope, "P1"));
                w = h.Workspace.Drafts!.Workspace; rows = w.Open(h.Workspace.Selected!);
            }
            File.Copy(checkpoint, Path.Combine(root, seedName));
            await File.WriteAllTextAsync(Path.Combine(root, remoteName), JsonSerializer.Serialize(new RemoteState(h.Titles, h.Selects)));
            var initialSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(checkpoint)));
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
            if (review.Batch.Operations.Length != expectedFields) throw new InvalidOperationException("Unexpected review count");
            var executionStart = Stopwatch.GetTimestamp();
            timer.Restart(); // User review is outside both measured execution intervals.
            using (PerformanceTrace.Span("execute")) await h.Workspace.ConfirmApplyAsync(review);
            var execute = timer.Elapsed.TotalMilliseconds;
            trace?.Dispose();
            var journal = h.Workspace.Drafts!.Workspace.Journal.SingleOrDefault(b => b.Id == review.Batch.Id);
            var journalDelta = h.Workspace.Drafts.Workspace.Journal.Count - (durable.Journal?.Length ?? 0);
            var success = h.Writes.Count == expectedFields && (expectedFields == 0 ? journal is null && journalDelta == 0
                : journal is not null && journal.Operations.Length == expectedFields && journal.Operations.All(o => o.State == ApplyState.Succeeded));
            var firstSuccess = trace?.Samples.FirstOrDefault(s => s.Kind == "durable-success");
            var result = new { sample, prepareMs = prepare, executeMs = execute, endToEndMs = prepare + execute, success,
                firstDurableSuccessMs = firstSuccess is null ? (double?)null : (firstSuccess.Start - executionStart) * 1000d / Stopwatch.Frequency,
                initialSha256, mutations = h.Writes.Count, changedRows = changes, changedFields = expectedFields, newBatches = journalDelta,
                commandInvocations = trace?.Samples.Where(s => s.Kind.StartsWith("process-")).GroupBy(s => new { s.Kind, phase = s.Start < executionStart ? "prepare" : "execute" })
                    .Select(g => new { g.Key.Kind, g.Key.phase, count = g.Count(), inclusiveMs = g.Sum(s => s.End - s.Start) * 1000d / Stopwatch.Frequency }).ToArray(),
                errors = journal?.Operations.Where(o => o.State != ApplyState.Succeeded).Select(o => new { o.State, o.Reason }).ToArray(),
                spans = trace?.Samples, journalOperations = journal?.Operations.Length };
            await File.WriteAllTextAsync(Path.Combine(root, sample < 0 ? "warmup.json" : $"sample-{sample}.json"), JsonSerializer.Serialize(result));
            Console.WriteLine($"sample={sample} items={count} changes={changes} prepare={prepare:F1} execute={execute:F1} success={success}");
            if (!success) return 1;
        }
        return 0;
    }
    private sealed record RemoteState(Dictionary<string, string> Titles, Dictionary<string, string?> Selects);
}
