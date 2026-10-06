using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
namespace GhProjectsBoards.Tests;
internal static class EvaluationFixture
{
    internal static readonly DateOnly Today = new(2026, 10, 5);
    internal static async Task Create(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("An absolute data root is required.");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Use a new empty data root or -Resume.");
        var fake = Path.Combine(root, "fake-gh"); Directory.CreateDirectory(fake);
        var columns = PlanColumnMatching.Roles.Select(r => new PlanColumnDefinition("F-" + r.Role, r.Name, r.Type)).ToImmutableArray();
        var settings = new ProjectPlanSettings {
            StatusDate = Today, ProjectStart = Today, DefaultRepository = "acme/repo",
            Columns = PlanColumnMatching.Roles.Select(r => new PlanColumnMapping(r.Role, "F-" + r.Role, r.Name, r.Type)).ToImmutableArray(),
            People = Enumerable.Range(1, 20).Select(i => new PlanResource("U" + i, "person-U" + i,
                i == 1 ? 50 : 100, i == 1 ? 16 : i == 2 ? 120 : 240 + i % 4 * 20, i == 3 ? [Today.AddDays(3)] : [])).ToImmutableArray()
        };
        var holidays = PlanningContract.BundledHolidays().Dates.Select(d => d.Date).ToHashSet();
        var workingDays = Enumerable.Range(0, 300).Select(Today.AddDays)
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(d)).ToArray();
        var rows = Enumerable.Range(1, 1000).Select(i => {
            var group = (i - 1) / 10 * 10 + 1;
            var summary = i == group;
            var phase = (i - 1) / 10;
            var slot = phase / 19 * 20 + Math.Max(0, i - group - 1) * 2;
            var start = workingDays[slot];
            return new PlanRow("I" + i, summary ? "工程 " + ((i - 1) / 10 + 1) : "作業 " + i, "acme/repo") {
                Parent = summary ? null : "I" + group,
                Estimate = summary ? null : 4 + i % 4 * 4, Remaining = summary ? null : i % 17 == 0 ? 0 : 2 + i % 3 * 2,
                Actual = summary ? null : i % 17 == 0 ? 8 : i % 7 == 0 ? 2 : 0,
                Assignees = summary || i % 29 == 0 ? [] : i % 31 == 0 ? ["U2", "U3"] : ["U" + (2 + phase % 19)],
                Predecessors = !summary && i > group + 1 ? ["I" + (i - 1)] : [],
                Fixed = !summary && i % 23 == 0, Closed = !summary && i % 17 == 0,
                StartNoEarlierThan = summary ? null : start,
                Start = summary ? null : start, End = summary ? null : start
            };
        }).ToImmutableArray();
        // Two independent four-hour tasks exceed U1's four-hour day, but not the Project allowance.
        foreach (var index in new[] { 1, 2 }) rows = rows.SetItem(index, rows[index] with {
            Assignees = ["U1"], Estimate = 4, Remaining = 4, Actual = 0, Predecessors = [], StartNoEarlierThan = Today, Fixed = false, Closed = false
        });
        // A second visible exception contrasts with the otherwise spaced work.
        foreach (var index in new[] { 21, 22 }) rows = rows.SetItem(index, rows[index] with {
            Assignees = ["U4"], Estimate = 8, Remaining = 6, Actual = 0, Predecessors = [],
            StartNoEarlierThan = Today, Fixed = false, Closed = false
        });
        var project = new ScopedId(new("github.com", 42), "P1");
        var document = new PlanDocument(project, new(rows, columns), new(rows, settings));
        var scheduled = PlanOperations.Schedule(document, Today).ToDictionary(t => t.Input.Identity);
        rows = rows.Select(r => r with { Start = scheduled[r.Identity].Start.Value, End = scheduled[r.Identity].End.Value }).ToImmutableArray();
        var orders = rows.ToImmutableDictionary(r => r.Identity, r => rows.Where(c => c.Parent == r.Identity).Select(c => c.Identity).ToImmutableArray());
        document = document with { Baseline = new(rows, columns), State = new(rows, settings), Sync = new() { NativeOrders = orders } };
        FakePlanEditor.Save(fake, new(rows.Select(r => new PlanFakeIssue(r, "", true)).ToImmutableArray(), 1001) {
            SubOrders = orders, AddedFields = [nameof(PlanField.StartNoEarlierThan), nameof(PlanField.Fixed)]
        });
        await File.WriteAllTextAsync(Path.Combine(fake, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        var session = await PlanSession.CreateAsync(new(root), document, Today);
        await session.FlushAsync();
        if (session.Changes(Today).TaskCount != 0) throw new InvalidOperationException("Evaluation baseline differs from calculated dates.");
        await File.WriteAllTextAsync(Path.Combine(root, "evaluation.json"), "{\"version\":1,\"statusDate\":\"2026-10-05\",\"tasks\":1000,\"people\":20}");
    }
}
