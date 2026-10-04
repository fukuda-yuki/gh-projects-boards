using System.Diagnostics;
using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ApplyCandidatePerformanceTests
{
    [Test, Explicit("Fixed 2,000-row projection measurement; run separately from desktop timing."), Category("PlanningPerformance")]
    public void TwoThousandRowsKeepTheExactCandidateSetAcrossRepeatedReadOnlyProjection()
    {
        var project = EditingTests.Registration(count: 2000);
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        var rows = work.Open(project);
        work.Commit("P1", rows[1999].Cells[0], "Changed last title");
        work.SetBuffer(rows[999].Cells[0], "Unfinished middle title");
        var before = JsonSerializer.Serialize(work.Snapshot());
        var samples = new List<object>();
        for (var sample = -1; sample < 7; sample++)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var begin = Stopwatch.GetTimestamp();
            var candidates = work.ReadApplyCandidates(project);
            var milliseconds = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            Assert.That(candidates.Select(c => c.Id), Is.EqualTo(new[] { "P1T1000", "P1T2000" }));
            Assert.That(candidates[0].Fields.Single(f => f.Key.Kind == "Title").Buffer, Is.EqualTo("Unfinished middle title"));
            Assert.That(candidates[1].Fields.Single(f => f.Key.Kind == "Title").Change!.Value, Is.EqualTo("Changed last title"));
            samples.Add(new { sample, warmup = sample < 0, milliseconds, managedBytes = allocated });
        }
        Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
        TestContext.Out.WriteLine(JsonSerializer.Serialize(new { rows = 2000, columns = 2, samples,
            boundary = "Synchronous read-only Core projection, excluding fixture setup and assertions; not UI latency." }));
    }
}
