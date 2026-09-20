using System.Text.Json;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Tests;

// Synthetic evaluation data only; normal production startup never calls this.
internal static class PlanningEvaluation
{
    internal static async Task<int> Seed(string root, string scenario)
    {
        if (!Path.IsPathFullyQualified(root) || Directory.Exists(root) || File.Exists(root) || scenario is not ("Fresh" or "Weekly" or "Load")) return 2;
        var (project, existing) = GanttWorkload.Create(scenario == "Load" ? 1000 : 20);
        project = project with { Snapshot = project.Snapshot with { Fields = project.Snapshot.Fields.Select(f => f.Id.NodeId == "F-Finish" ? f with { Name = "EndDate" } : f).ToArray() } };
        var work = scenario == "Load" ? existing : new EditingWorkspace(project.Snapshot.Id.Scope);
        if (scenario == "Fresh") project = project with { Snapshot = project.Snapshot with { Items = project.Snapshot.Items.Select(i => i with {
            Values = i.Values.Select(v => v.FieldId?.NodeId is "F-Estimate" or "F-Remaining" ? v with { Scalar = null, Availability = ValueAvailability.Empty } : v).ToArray() }).ToArray() } };
        var second = EditingTests.Registration("P2", count: 20); work.SetRegistrations([project, second]);
        if (scenario == "Weekly")
        {
            var plan = EditingWorkspace.UpgradeAssignmentContract(existing.Planning("P1")!);
            plan = plan with { Cutoff = PlanningContractTests.At("2026-10-09 18:00"), Tasks = plan.Tasks.Select(t => EditingWorkspace.WithObservedAssignment(project,
                t.Id == "I1" ? t with { Actuals = [new("U1", 5, new(2026, 10, 5))] } : t)).ToArray() };
            work.CommitPlanning(project, plan, work.Revision);
        }
        var store = new DraftStore(root); await store.SaveAsync(work.Snapshot(), 0);
        var read = (await store.LoadAsync(work.Scope))!;
        if (read.Registrations?.Length != 2 || scenario == "Fresh" && read.Planning!.Length != 0) throw new InvalidDataException("Evaluation fixture readback failed.");
        var diagnostics = Path.Combine(root, "diagnostics"); Directory.CreateDirectory(diagnostics);
        await File.WriteAllTextAsync(Path.Combine(diagnostics, "planning-evaluation.json"), JsonSerializer.Serialize(new {
            scenario, synthetic = true, tasks = scenario == "Load" ? 1000 : 20, projects = 2, people = 20,
            assignment = scenario == "Fresh" ? "No task plan/owner metadata" : scenario == "Load" ? "Retained legacy owner semantics for comparison" : "Explicit synthetic native assignment and Project weights",
            field = "F-Finish is observed as EndDate; mapping remains by ID/type", validatedReadback = true }));
        return 0;
    }
}
