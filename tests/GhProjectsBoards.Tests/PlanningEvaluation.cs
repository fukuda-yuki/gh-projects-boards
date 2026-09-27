using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

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
        if (scenario != "Load") project = project with { Snapshot = project.Snapshot with {
            // GitHub login identifies one account. The shared stress fixture's
            // repeated Owner label must not make ordinary evaluation ambiguous.
            Issues = project.Snapshot.Issues.ToDictionary(pair => pair.Key, pair => pair.Value with {
                Native = pair.Value.Native! with { Assignees = pair.Value.Native!.Assignees
                    .Select(person => person with { Login = EvaluationLogin(person.Id.NodeId) }).ToArray() } }) } };
        var second = EditingTests.Registration("P2", count: 20); work.SetRegistrations([project, second]);
        if (scenario == "Weekly")
        {
            var plan = EditingWorkspace.UpgradeAssignmentContract(existing.Planning("P1")!);
            plan = plan with { People = plan.People.Select(person => person with { Name = EvaluationLogin(person.Id) }).ToArray(),
                Cutoff = PlanningContractTests.At("2026-10-09 18:00"), Tasks = plan.Tasks.Select(t => EditingWorkspace.WithObservedAssignment(project,
                t.Id == "I1" ? t with { Actuals = [new("U1", 5, new(2026, 10, 5))] } : t)).ToArray() };
            work.CommitPlanning(project, plan, work.Revision);
        }
        var store = new DraftStore(root); await store.SaveAsync(work.Snapshot(), 0);
        var read = (await store.LoadAsync(work.Scope))!;
        if (read.Registrations?.Length != 2 || scenario == "Fresh" && read.Planning!.Length != 0) throw new InvalidDataException("Evaluation fixture readback failed.");
        var diagnostics = Path.Combine(root, "diagnostics"); Directory.CreateDirectory(diagnostics);
        await File.WriteAllTextAsync(Path.Combine(diagnostics, "planning-evaluation.json"), JsonSerializer.Serialize(new {
            scenario, synthetic = true, isolationId = Guid.NewGuid(), evaluationRoot = Path.GetFullPath(root),
            tasks = scenario == "Load" ? 1000 : 20, projects = 2, people = 20,
            assignment = scenario == "Fresh" ? "No task plan/owner metadata" : scenario == "Load" ? "Retained legacy owner semantics for comparison" : "Explicit synthetic native assignment and Project weights",
            field = "F-Finish is observed as EndDate; mapping remains by ID/type", validatedReadback = true }));
        return 0;
    }
    private static string EvaluationLogin(string id) => id == "U1" ? "Owner" : "Owner" + id[1..];
}

[TestFixture]
internal sealed class PlanningEvaluationTests
{
    [TestCase("Fresh")]
    [TestCase("Weekly")]
    public async Task OrdinaryEvaluationReadbackHasIdentifiablePeopleWithoutChangingWork(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-planning-evaluation-" + Guid.NewGuid());
        Assert.That(await PlanningEvaluation.Seed(root, scenario), Is.Zero);
        var saved = (await new DraftStore(root).LoadAsync(new("github.com", 42)))!;
        var project = RegistrationStore.FromRecord(saved.Registrations!.Single(r => r.Snapshot.Id.NodeId == "P1"));
        var issues = project.Snapshot.Issues.Values.OrderBy(i => i.Number).ToArray();
        var people = issues.SelectMany(i => i.Native!.Assignees).ToArray();
        Assert.Multiple(() => {
            Assert.That(saved.Registrations, Has.Length.EqualTo(2));
            Assert.That(project.Snapshot.Items, Has.Count.EqualTo(20));
            Assert.That(issues, Has.Length.EqualTo(20));
            Assert.That(people.Select(p => p.Id.NodeId), Is.EqualTo(Enumerable.Range(1, 20).Select(i => "U" + i)));
            Assert.That(people.Select(p => p.Login).Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(20));
            Assert.That(people[0].Login, Is.EqualTo("Owner"));
            Assert.That(people[1].Login, Is.EqualTo("Owner2"));
            Assert.That(people[19].Login, Is.EqualTo("Owner20"));
        });
        if (scenario == "Fresh")
        {
            Assert.That(saved.Planning, Is.Empty);
            Assert.That(project.Snapshot.Items.SelectMany(i => i.Values).Where(v => v.FieldId?.NodeId is "F-Estimate" or "F-Remaining")
                .Select(v => (v.Availability, v.Scalar)), Is.All.EqualTo((ValueAvailability.Empty, (string?)null)));
            return;
        }
        var plan = saved.Planning!.Single();
        var work = EditingWorkspace.Restore(saved);
        var logins = people.ToDictionary(p => p.Id.NodeId, p => p.Login);
        Assert.Multiple(() => {
            Assert.That(plan.People, Has.Length.EqualTo(20));
            Assert.That(plan.Tasks, Has.Length.EqualTo(20));
            Assert.That(plan.People.All(p => logins[p.Id] == p.Name), Is.True);
            Assert.That(plan.Tasks.All(t => t.OwnerId == t.Assignment!.Assignees.Single() && logins.ContainsKey(t.OwnerId!)
                && t.OwnerId == project.Snapshot.Issues[new(work.Scope, t.Id)].Native!.Assignees.Single().Id.NodeId), Is.True);
            Assert.That(plan.Start, Is.EqualTo(new DateTime(2026, 10, 5, 9, 0, 0)));
            Assert.That(plan.Cutoff, Is.EqualTo(new DateTime(2026, 10, 9, 18, 0, 0)));
            Assert.That(plan.Tasks.Single(t => t.Id == "I1").Progress, Is.EqualTo(PlanningProgress.Unstarted));
            Assert.That(plan.Tasks.Single(t => t.Id == "I1").Actuals, Is.EqualTo(new[] { new ActualContribution("U1", 5, new(2026, 10, 5)) }));
        });
    }
}
