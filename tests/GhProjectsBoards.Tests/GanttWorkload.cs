using GhProjectsBoards.Core.Projects;
using System.Text.Json;

namespace GhProjectsBoards.Tests;

internal static class GanttWorkload
{
    // Extends #61's 1,000-task/20-person mixed-mode chains with explicit Gantt contrasts.
    internal static (ProjectRegistration Project, EditingWorkspace Work) Create(int count = 1000)
    {
        var p = PlanningPathTests.Registration(count);
        p = p with { Snapshot = p.Snapshot with {
            Items = p.Snapshot.Items.Select((item, i) => item with { Values = item.Values.Select(v => v.FieldId?.NodeId switch {
                "F-Estimate" => v with { Scalar = i < 120 ? "4" : "16", Availability = ValueAvailability.Present },
                "F-Remaining" => v with { Scalar = (i + 1) % 30 == 0 ? "0" : "4", Availability = ValueAvailability.Present }, _ => v }).ToArray() }).ToArray(),
            Issues = p.Snapshot.Issues.ToDictionary(pair => pair.Key, pair => pair.Value with {
                Title = pair.Value.Title with { Value = pair.Value.Number is 1 or 2 ? "週次計画の確認" : "作業 " + pair.Value.Number },
                Native = new([new(new(p.Snapshot.Id.Scope, "U" + ((pair.Value.Number - 1) % 20 + 1)), "Owner")],
                    Predecessors(pair.Value.Number).Select(id => new ScopedId(p.Snapshot.Id.Scope, "I" + id)).ToArray(), new(ValueAvailability.Empty), true) }) } };
        var manualStart = At("2026-10-10 12:07"); var manualFinish = At("2026-10-10 12:08");
        var tasks = Enumerable.Range(1, count).Select(i => new PlanningTask("I" + i, i % 50 == 0 ? PlanningMode.Manual : PlanningMode.Auto, "U" + ((i - 1) % 20 + 1),
            i % 50 == 0 ? manualStart : null, i % 50 == 0 ? manualFinish : null,
            i % 30 == 0 ? PlanningProgress.Completed : i % 7 == 0 ? PlanningProgress.InProgress : PlanningProgress.Unstarted,
            i % 30 == 0 || i % 7 == 0 ? At("2026-10-05 09:00") : null,
            i % 30 == 0 ? At("2026-10-06 18:00") : null,
            Actuals: i % 7 == 0 || i % 30 == 0 ? [new("U" + ((i - 1) % 20 + 1), 5, new(2026, 10, 6))] : null)).ToArray();
        if (count >= 1000)
        {
            tasks[996] = new("I997");
            tasks[997] = new("I998", PlanningMode.Manual, ManualStart: At("2026-11-02 12:07"));
            tasks[998] = new("I999", PlanningMode.Auto, "U1", LocalLinks: [new("external-missing")]);
            tasks[999] = new("I1000", PlanningMode.Manual, ManualStart: At("2027-03-15 12:07"), ManualFinish: At("2027-03-15 13:00"));
        }
        var plan = PlanningPathTests.Plan() with { Tasks = tasks,
            People = Enumerable.Range(1, 20).Select(i => new PlanningPerson("U" + i, "Person " + i, i % 3 == 0 ? 50 : i % 3 == 2 ? 80 : 100)).ToArray(),
            Calendar = PlanningPathTests.Plan().Calendar with { Exceptions = [new(new(2026, 10, 13), "U2", [new(600, 720)])] } };
        var work = new EditingWorkspace(p.Snapshot.Id.Scope); work.SetRegistrations([p]); work.CommitPlanning(p, plan, work.Revision);
        var pending = work.Open(p)[count - 1].Cells.First(c => c.Key?.FieldId == "F-Estimate"); work.SetBuffer(pending, "24未確定");
        return (p, work);
    }
    private static int[] Predecessors(int i) => i == 990 ? Enumerable.Range(970, 12).ToArray()
        : i > 1 && i <= 120 || (i - 1) % 10 != 0 ? [i - 1] : [];
    private static DateTime At(string value) => PlanningContractTests.At(value);
    internal static async Task<int> Seed(string root)
    {
        if (!Path.IsPathFullyQualified(root) || Directory.Exists(root) || File.Exists(root)) return 2;
        var (p, work) = Create();
        // A second cached Project checks shared title buffers against Project-local plans.
        var second = EditingTests.Registration("P2", count: 1000); work.SetRegistrations([p, second]);
        var store = new DraftStore(root); await store.SaveAsync(work.Snapshot(), 0);
        var restored = EditingWorkspace.Restore((await store.LoadAsync(work.Scope))!);
        if (restored.PlanFor(p).Tasks.Length != 1000) throw new InvalidDataException("Gantt fixture readback failed.");
        await File.WriteAllTextAsync(Path.Combine(root, "gantt-fixture.json"), JsonSerializer.Serialize(new {
            tasks = 1000, people = 20, fields = 6, dateStart = "2026-10-05", dateEnd = "2027-03-15",
            graph = "120-node initial chain plus ten-node chains; task 990 has twelve predecessors; mixed Auto/Manual/Completed/InProgress; task 999 also has an unresolved external prerequisite",
            contrasts = "duplicate titles 1/2, one-minute manual weekend bars, person exception, 997 Unplanned, 998 partial, 999 unresolved, 1000 far endpoint and pending effort",
            independentExpected = "I1000 Manual 2027-03-15 12:07 to 13:00; I998 has no complete bar; I997 Unplanned", validatedReadback = true }));
        return 0;
    }
}
