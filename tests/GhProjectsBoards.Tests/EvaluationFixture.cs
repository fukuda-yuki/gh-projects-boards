using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
namespace GhProjectsBoards.Tests;
internal static class EvaluationFixture
{
    internal static readonly DateOnly Today = new(2026, 10, 5);
    internal sealed record VersionRow(string Key, string Title, string? Parent, string? Phase, decimal? Estimate,
        string? Person, string[] Predecessors, DateOnly? Start);
    internal sealed record VersionPerson(string Identity, string Name, decimal Rate, decimal Allowance);
    internal sealed record VersionPlan(string Title, DateOnly StatusDate, DateOnly ProjectStart, DateOnly ShippingDate,
        VersionPerson[] People, VersionRow[] Rows);
    internal static VersionPlan ReadPlan()
    {
        using var stream = typeof(EvaluationFixture).Assembly.GetManifestResourceStream("VersionPlan.json")!;
        return JsonSerializer.Deserialize<VersionPlan>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    internal static async Task Create(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("An absolute data root is required.");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Use a new empty data root or -Resume.");
        var plan = ReadPlan();
        var fake = Path.Combine(root, "fake-gh"); Directory.CreateDirectory(fake);
        var columns = PlanColumnMatching.Roles.Select(r => new PlanColumnDefinition("F-" + r.Role, r.Name, r.Type)).ToImmutableArray();
        var settings = new ProjectPlanSettings {
            StatusDate = plan.StatusDate, ProjectStart = plan.ProjectStart, DefaultRepository = "acme/repo",
            Columns = PlanColumnMatching.Roles.Select(r => new PlanColumnMapping(r.Role, "F-" + r.Role, r.Name, r.Type)).ToImmutableArray(),
            People = plan.People.Select(p => new PlanResource(p.Identity, p.Name, p.Rate, p.Allowance, [])).ToImmutableArray()
        };
        var identities = plan.Rows.Select((r, n) => (r.Key, Id: "I" + (n + 1))).ToDictionary(p => p.Key, p => p.Id);
        var rows = plan.Rows.Select(r => new PlanRow(identities[r.Key], r.Title, "acme/repo") {
            Parent = r.Parent is null ? null : identities[r.Parent], Estimate = r.Estimate, Remaining = r.Estimate,
            Actual = r.Parent is null ? null : 0, Assignees = r.Person is null ? [] : [r.Person],
            Predecessors = r.Predecessors.Select(k => identities[k]).ToImmutableArray(), StartNoEarlierThan = r.Start
        }).ToImmutableArray();
        var project = new ScopedId(new("github.com", 42), "P1");
        var document = new PlanDocument(project, new(rows, columns), new(rows, settings));
        var scheduled = PlanOperations.Schedule(document, plan.StatusDate).ToDictionary(t => t.Input.Identity);
        rows = rows.Select(r => r with { Start = scheduled[r.Identity].Start.Value, End = scheduled[r.Identity].End.Value }).ToImmutableArray();
        var orders = rows.ToImmutableDictionary(r => r.Identity, r => rows.Where(c => c.Parent == r.Identity).Select(c => c.Identity).ToImmutableArray());
        document = document with { Baseline = new(rows, columns), State = new(rows, settings), Sync = new() {
            NativeOrders = orders,
            IssueLinks = rows.ToImmutableDictionary(r => r.Identity,
                r => new PlanIssueLink($"{r.Repository}#{r.Identity[1..]}", $"https://github.com/{r.Repository}/issues/{r.Identity[1..]}"))
        } };
        FakePlanEditor.Save(fake, new(rows.Select(r => new PlanFakeIssue(r, "", true)).ToImmutableArray(), rows.Length + 1) {
            SubOrders = orders, AddedFields = [nameof(PlanField.StartNoEarlierThan), nameof(PlanField.Fixed)]
        });
        await File.WriteAllTextAsync(Path.Combine(fake, "scenario.json"), "{\"planEditor\":true,\"workspace\":true,\"versionEvaluation\":true}");
        var session = await PlanSession.CreateAsync(new(root), document, plan.StatusDate);
        await session.FlushAsync();
        if (session.Changes(plan.StatusDate).TaskCount != 0) throw new InvalidOperationException("Evaluation baseline differs from calculated dates.");
        var weeks = PlanPeople.Calculate(document, plan.StatusDate, plan.ProjectStart, PlanPeriodScale.Week, 29);
        var workload = weeks.Periods.Select((period, index) => new {
            period.Start, period.End,
            planned = weeks.People.Sum(p => p.Periods[index].Planned ?? 0),
            capacity = weeks.People.Sum(p => p.Periods[index].Capacity ?? 0),
            overloadedPeople = weeks.People.Count(p => p.Periods[index].Overloaded)
        }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(root, "evaluation.json"), JsonSerializer.Serialize(new {
            version = 2, title = plan.Title, statusDate = plan.StatusDate, requirements = 40, tasks = 1000, people = 20,
            workload, identities
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
