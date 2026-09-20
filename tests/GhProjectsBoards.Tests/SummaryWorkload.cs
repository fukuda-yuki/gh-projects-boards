using GhProjectsBoards.Core.Projects;
using System.Text.Json;

namespace GhProjectsBoards.Tests;

internal static class SummaryWorkload
{
    internal static (ProjectRegistration Project, EditingWorkspace Work) Create()
    {
        var (p, w) = GanttWorkload.Create(999);
        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(9));
        var plan = w.Planning("P1")!;
        var tasks = plan.Tasks.Select((t, i) => t with { OwnerId = i == 0 ? "U1" : i == 1 ? "U2" : "U" + (i % 18 + 3),
            Actuals = [new(i == 0 ? "U1" : i == 1 ? "U2" : "U" + (i % 18 + 3), i == 0 ? 48 : i == 1 ? 56 : 0, day)] }).ToArray();
        tasks[3] = tasks[3] with { Actuals = [new(tasks[3].OwnerId, 8, day.AddDays(-7))] };
        tasks[4] = tasks[4] with { Actuals = [new(tasks[4].OwnerId, 8, day.AddDays(1))] };
        tasks[5] = tasks[5] with { OwnerId = "U4", Actuals = [new("U3", 8, day)] };
        tasks[6] = tasks[6] with { Mode = PlanningMode.Manual, ManualStart = plan.Start, ManualFinish = plan.Start!.Value.AddHours(4),
            Contributions = [new("U5", 2, 1), new("U6", 1, 1)], Actuals = [new("U5", 2, day), new("U6", 2, day)] };
        tasks[7] = tasks[7] with { LaborKind = TaskLaborKind.Rollup };
        tasks[9] = tasks[9] with { LaborKind = TaskLaborKind.Direct };
        // 8->9 rollup; 10->11 direct; 12->13 deliberately ambiguous. Hierarchy is not an FS edge.
        p = p with { Snapshot = p.Snapshot with { Issues = p.Snapshot.Issues.ToDictionary(pair => pair.Key, pair =>
            pair.Value.Number is 9 or 11 or 13 ? pair.Value with { Native = pair.Value.Native! with {
                Parent = new(ValueAvailability.Present, new(w.Scope, "I" + (pair.Value.Number - 1))) } } : pair.Value) } };
        w.SetRegistrations([p]);
        w.CommitPlanning(p, plan with { Version = 2, Cutoff = day.ToDateTime(new(18, 0)), Tasks = tasks,
            People = plan.People.Select(p => p with { Name = p.Id == "U1" ? "A" : p.Id == "U2" ? "B" : p.Name }).ToArray() }, w.Revision,
            [new("P1T1", "Estimate", "144"), new("P1T1", "Remaining", "72"), new("P1T2", "Estimate", "64"), new("P1T2", "Remaining", "40"), new("P1T3", "Remaining", null)]);
        var local = w.AppendRows(p with { DefaultRepository = "owner/repo" }, "未公開の追加作業\tTodo\t8").Single();
        w.CommitPlanning(p, w.Planning("P1")! with { Tasks = w.Planning("P1")!.Tasks.Append(new(local, OwnerId: "U20", Actuals: [new("U20", 0, day)])).ToArray() },
            w.Revision, [new(local, "Remaining", "8")]);
        w.SetAllowance(p, "U1", 160, w.Revision); w.SetAllowance(p, "U2", 80, w.Revision);
        w.CaptureBaseline(p, w.Revision, null, DateTimeOffset.UtcNow);
        w.Commit("P1", w.Open(p).Single(r => r.ItemId == "P1T10").Cells.Single(c => c.Key?.FieldId == "F-Estimate"), "12");
        var second = PlanningPathTests.Registration(2);
        second = second with { Snapshot = second.Snapshot with { Id = new(w.Scope, "P2"), Title = "P2",
            Fields = second.Snapshot.Fields.Select(f => f with { ProjectId = new(w.Scope, "P2") }).ToArray(),
            Items = second.Snapshot.Items.Select((item, i) => item with { Id = new(w.Scope, "P2T" + (i + 1)) }).ToArray() } };
        w.SetRegistrations([p, second]);
        w.CommitPlanning(second, PlanningPathTests.Plan() with { ProjectId = "P2", Cutoff = day.ToDateTime(new(18, 0)) }, w.Revision);
        w.SetAllowance(second, "U1", 8, w.Revision);
        return (p, w);
    }
    internal static async Task<int> Seed(string root)
    {
        if (!Path.IsPathFullyQualified(root) || Directory.Exists(root) || File.Exists(root)) return 2;
        var (p, w) = Create(); var store = new DraftStore(root); await store.SaveAsync(w.Snapshot(), 0);
        var restored = EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!);
        var result = SummaryProjection.Create(restored, p, DateOnly.FromDateTime(DateTime.UtcNow.AddHours(9)));
        if (result.TaskCount != 1000 || result.People.Single(p => p.Id == "U1").Forecast.Hours != 120
            || result.People.Single(p => p.Id == "U2").Headroom != -16) throw new InvalidDataException("Independent Summary fixture readback failed.");
        await File.WriteAllTextAsync(Path.Combine(root, "summary-fixture.json"), JsonSerializer.Serialize(new {
            kind = "synthetic-summary-v1", tasks = 1000, people = 20, projects = 2, scope = "github.com/viewer42", validatedReadback = true,
            cutoff = result.Cutoff, independentExpected = new { A = new[] { 160, 144, 48, 72, 120 }, B = new[] { 80, 64, 56, 40, 96 } },
            examples = "A/B values in raw hours (allowance, estimate, actual, remaining, forecast); I3 missing remaining, I4 stale actual, I5 future actual, I6 reassigned historical U3, I7 joint U5/U6 with unattributed balance, I8 rollup, I10 direct with baseline4h/current12h, I12 ambiguous parent, local unpublished task, offscreen pending estimate; P2 allowance U1=8h",
            network = "offline seed; no authentication or GitHub calls", humanAcceptance = "not run" }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
