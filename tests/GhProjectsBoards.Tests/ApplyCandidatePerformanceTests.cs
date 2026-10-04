using System.Diagnostics;
using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ApplyCandidatePerformanceTests
{
    [Test, Explicit("Fixed 1,000-row/twelve-field sparse projection allocation; run separately from desktop timing."), Category("PlanningPerformance")]
    public void ThousandRowsTwelveFieldsKeepSparseCandidatesWithinTheAllocationBudget()
    {
        ProjectRegistration Project(string id)
        {
            var project = EditingTests.Registration(id, count: 1000);
            var template = project.Snapshot.Fields[0];
            var definitions = Enumerable.Range(0, 12).Select(i => template with {
                Id = new(template.Id.Scope, id + "-field-" + i), Name = "Field " + i }).ToArray();
            return project with { Snapshot = project.Snapshot with { Fields = definitions,
                Items = project.Snapshot.Items.Select(item => item with {
                    Values = definitions.Select(field => item.Values[0] with { FieldId = field.Id }).ToArray() }).ToArray() } };
        }
        var project = Project("P1"); var second = Project("P2");
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project, second]);
        var rows = work.Open(project); work.Open(second);
        work.SetBuffer(rows[499].Cells[0], "Unfinished middle title");
        work.Commit("P1", rows[999].Cells[1], "done", true);
        var before = JsonSerializer.Serialize(work.Snapshot());
        var samples = new List<(double Milliseconds, long Bytes)>();
        for (var sample = -1; sample < 7; sample++)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread(); var begin = Stopwatch.GetTimestamp();
            var candidates = work.ReadApplyCandidates(project);
            var elapsed = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            Assert.That(candidates.Select(c => c.Id), Is.EqualTo(new[] { "P1T500", "P1T1000" }));
            Assert.That(candidates.All(c => c.Fields.Length == 13), Is.True, "Each candidate retains its title and all twelve related fields.");
            Assert.That(candidates[0].Fields.Single(f => f.Key.Kind == "Title").Buffer, Is.EqualTo("Unfinished middle title"));
            Assert.That(candidates[1].Fields.Single(f => f.Key.FieldId == "P1-field-0").Change!.Value, Is.EqualTo("done"));
            Assert.That(candidates.SelectMany(c => c.Fields).Any(f => f.Key.ProjectId == "P2"), Is.False);
            if (sample >= 0) samples.Add((elapsed, allocated));
        }
        Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
        TestContext.Out.WriteLine(JsonSerializer.Serialize(new { rows = 1000, selectFields = 12, projects = 2,
            maximumManagedBytes = 8 * 1024 * 1024, samples = samples.Select(s => new { milliseconds = s.Milliseconds, managedBytes = s.Bytes }),
            boundary = "Synchronous read-only Core projection, excluding fixture setup and assertions; not UI latency." }));
        Assert.That(samples.Max(s => s.Bytes), Is.LessThanOrEqualTo(8 * 1024 * 1024),
            "Sparse status reads must stay below the allocation budget for this fixed workload.");
    }

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
