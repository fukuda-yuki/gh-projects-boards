using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
namespace GhProjectsBoards.Tests;
internal static class EvaluationFixture
{
    internal const string DefaultVersion = "2027.04";
    internal static readonly string[] Versions = [DefaultVersion, "2027.10"];
    internal sealed record VersionRow(string Key, string Title, string? Parent, string? Phase, decimal? Estimate,
        string? Person, string[] Predecessors, DateOnly? Start)
    {
        // The team's true work when it differs from the estimate; the simulation discovers it while working.
        public decimal? Work { get; init; }
        public bool OpenAtZero { get; init; }
    }
    internal sealed record VersionPerson(string Identity, string Name, decimal Rate, decimal? Allowance);
    internal sealed record VersionMilestone(string Phase, string Title, DateOnly Due);
    internal sealed record VersionPlan(string Title, DateOnly ProjectStart, DateOnly ProjectEnd, DateOnly StatusDate,
        DateOnly[] StatusDates, string[] Phases, VersionMilestone[] Milestones, VersionPerson[] People, VersionRow[] Rows);
    internal sealed record Snapshot(DateOnly StatusDate, ImmutableArray<PlanRow> Rows, PlanDocument Document);

    internal static VersionPlan ReadPlan(string version = DefaultVersion)
    {
        using var stream = typeof(EvaluationFixture).Assembly.GetManifestResourceStream("VersionPlan-" + version + ".json")
            ?? throw new ArgumentException("Unknown evaluation version: " + version);
        return JsonSerializer.Deserialize<VersionPlan>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    internal static Dictionary<string, string> Identities(VersionPlan plan) => plan.Rows.Select((r, n) => (r.Key, Id: "I" + (n + 1))).ToDictionary(p => p.Key, p => p.Id);

    internal static PlanDocument Document(VersionPlan plan, ImmutableArray<PlanRow> rows, DateOnly statusDate)
    {
        var columns = PlanColumnMatching.Roles.Select(r => new PlanColumnDefinition("F-" + r.Role, r.Name, r.Type)).ToImmutableArray();
        var settings = new ProjectPlanSettings {
            StatusDate = statusDate, ProjectStart = plan.ProjectStart, DefaultRepository = "acme/repo",
            Columns = PlanColumnMatching.Roles.Select(r => new PlanColumnMapping(r.Role, "F-" + r.Role, r.Name, r.Type)).ToImmutableArray(),
            People = plan.People.Select(p => new PlanResource(p.Identity, p.Name, p.Rate, p.Allowance, [])).ToImmutableArray()
        };
        return new PlanDocument(new ScopedId(new("github.com", 42), "P1"), new(rows, columns), new(rows, settings));
    }

    // The team works day by day as the plan says and updates 実績 and 残 as it goes: 残 is the
    // assignee's current estimate, raised once an overrun shows halfway through the estimate.
    // A task is closed on the day its work is done. Each status date is a published plan.
    internal static IReadOnlyList<Snapshot> Simulate(VersionPlan plan, IReadOnlyCollection<DateOnly> statusDates)
    {
        var identities = Identities(plan);
        var source = plan.Rows.ToDictionary(r => identities[r.Key]);
        var rows = plan.Rows.Select(r => new PlanRow(identities[r.Key], r.Title, "acme/repo") {
            Parent = r.Parent is null ? null : identities[r.Parent], Estimate = r.Estimate, Remaining = r.Estimate,
            Actual = r.Parent is null ? null : 0, Assignees = r.Person is null ? [] : [r.Person],
            Predecessors = r.Predecessors.Select(k => identities[k]).ToImmutableArray(), StartNoEarlierThan = r.Start
        }).ToImmutableArray();
        var snapshots = new List<Snapshot>();
        var last = statusDates.Max();
        for (var day = plan.ProjectStart; day <= last; day = day.AddDays(1))
        {
            var schedule = PlanOperations.Schedule(Document(plan, rows, day), day).ToDictionary(t => t.Input.Identity);
            if (statusDates.Contains(day)) snapshots.Add(Publish(day));
            var updated = rows.ToDictionary(r => r.Identity);
            var visited = new HashSet<string>();
            foreach (var task in schedule.Values.OrderBy(t => t.Start.Value)) Work(task.Input.Identity);
            rows = rows.Select(row => updated[row.Identity]).ToImmutableArray();

            void Work(string identity)
            {
                if (!visited.Add(identity)) return;
                var row = updated[identity];
                foreach (var predecessor in row.Predecessors) Work(predecessor);
                if (row.Parent is null || row.Closed || row.Remaining == 0) return;
                // Re-estimates can invalidate today's planned handoff; only work already done releases it.
                if (row.Predecessors.Any(id => !updated[id].Closed && updated[id].Remaining != 0)) return;
                var hours = schedule[row.Identity].PlannedHours?.GetValueOrDefault(day) ?? 0;
                if (hours == 0) return;
                var task = source[row.Identity];
                var estimate = task.Estimate!.Value;
                var work = task.Work ?? estimate;
                var actual = Math.Min(work, row.Actual!.Value + hours);
                var remaining = actual == work ? 0 : work <= estimate || actual * 2 < estimate ? estimate - actual : work - actual;
                var done = remaining == 0;
                updated[identity] = row with { Actual = actual, Remaining = remaining, Start = row.Start ?? day, End = done ? day : row.End,
                    Closed = done && !task.OpenAtZero, CloseDate = done && !task.OpenAtZero ? day : null, Status = done && !task.OpenAtZero ? "Done" : "In progress" };
            }
            rows = rows.Select(row => row.Parent is null && rows.Where(c => c.Parent == row.Identity).All(c => c.Closed) ? row with { Closed = true, CloseDate = row.CloseDate ?? day, Status = "Done" } : row).ToImmutableArray();
        }
        return snapshots;

        Snapshot Publish(DateOnly statusDate)
        {
            var document = Document(plan, rows, statusDate);
            var scheduled = PlanOperations.Schedule(document, statusDate).ToDictionary(t => t.Input.Identity);
            var published = rows.Select(r => r with { Start = scheduled[r.Identity].Start.Value, End = scheduled[r.Identity].End.Value }).ToImmutableArray();
            return new(statusDate, published, Document(plan, published, statusDate));
        }
    }

    internal static async Task Create(string root, string version = DefaultVersion, DateOnly? statusDate = null)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("An absolute data root is required.");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Use a new empty data root or -Resume.");
        var plan = ReadPlan(version);
        var selected = statusDate ?? plan.StatusDate;
        if (selected < plan.ProjectStart || selected > plan.ProjectEnd) throw new ArgumentOutOfRangeException(nameof(statusDate), "The status date must fall within the project.");
        var snapshots = Simulate(plan, plan.StatusDates.Append(selected).Distinct().Order().ToArray());
        var current = snapshots.Single(s => s.StatusDate == selected);
        var rows = current.Rows;
        var orders = rows.ToImmutableDictionary(r => r.Identity, r => rows.Where(c => c.Parent == r.Identity).Select(c => c.Identity).ToImmutableArray());
        var document = current.Document with { Sync = new() {
            NativeOrders = orders,
            IssueLinks = rows.ToImmutableDictionary(r => r.Identity,
                r => new PlanIssueLink($"{r.Repository}#{r.Identity[1..]}", $"https://github.com/{r.Repository}/issues/{r.Identity[1..]}"))
        } };
        var identities = Identities(plan);
        var phases = plan.Rows.Where(r => r.Phase is not null).ToDictionary(r => identities[r.Key], r => r.Phase!);
        var fake = Path.Combine(root, "fake-gh"); Directory.CreateDirectory(fake);
        FakePlanEditor.Save(fake, new(rows.Select(r => new PlanFakeIssue(r, "", true) { Phase = phases.GetValueOrDefault(r.Identity) }).ToImmutableArray(), rows.Length + 1) {
            SubOrders = orders, AddedFields = [nameof(PlanField.StartNoEarlierThan), nameof(PlanField.Fixed)], Phases = [.. plan.Phases]
        });
        await File.WriteAllTextAsync(Path.Combine(fake, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true, workspace = true, versionTitle = plan.Title }));
        var session = await PlanSession.CreateAsync(new(root), document, selected);
        await session.FlushAsync();
        if (session.Changes(selected).TaskCount != 0) throw new InvalidOperationException("Evaluation baseline differs from calculated dates.");
        await File.WriteAllTextAsync(Path.Combine(root, "evaluation.json"), JsonSerializer.Serialize(new {
            version = 3, title = plan.Title, plan.ProjectStart, plan.ProjectEnd, statusDate = selected, requirements = 40, tasks = 1000, people = 20,
            milestones = plan.Milestones, measures = snapshots.Select(s => Measures(plan, s)), identities
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    internal sealed record Effort(decimal Estimate, decimal Actual, decimal Remaining, int Tasks, int Closed, int Overruns, int OpenAtZero)
    {
        public decimal Forecast => Actual + Remaining;
        public decimal Variance => Forecast - Estimate;
        public decimal Progress => Forecast == 0 ? 0 : Math.Round(Actual / Forecast * 100, 1);
    }
    internal sealed record PhaseMeasure(string Phase, Effort Effort, DateOnly? Start, DateOnly? Forecast, DateOnly Due, int LateWorkingDays);
    internal sealed record PersonMeasure(string Person, Effort Effort, int WorkingDays, int OverloadedDaysNext20, decimal PeakPercentNext20);
    internal sealed record StatusMeasures(DateOnly StatusDate, Effort Total, DateOnly? Forecast, IReadOnlyList<PhaseMeasure> Phases, IReadOnlyList<PersonMeasure> People);

    // Summary measures follow #131: 見込 = 実績 + 残, 差異 = 見込 − 見積, progress = 実績 ÷ 見込, completion by closing.
    internal static StatusMeasures Measures(VersionPlan plan, Snapshot snapshot)
    {
        var identities = Identities(plan);
        var phaseOf = plan.Rows.Where(r => r.Phase is not null).ToDictionary(r => identities[r.Key], r => r.Phase!);
        var schedule = PlanOperations.Schedule(snapshot.Document, snapshot.StatusDate).Where(t => phaseOf.ContainsKey(t.Input.Identity)).ToArray();
        Effort Sum(IEnumerable<ScheduledTask> tasks)
        {
            var list = tasks.ToArray();
            return new(list.Sum(t => t.Estimate ?? 0), list.Sum(t => t.Actual ?? 0), list.Sum(t => t.Remaining ?? 0), list.Length,
                list.Count(t => t.Input.Closed), list.Count(t => !t.Input.Closed && t.Actual + t.Remaining > t.Estimate),
                list.Count(t => !t.Input.Closed && t.Remaining == 0));
        }
        var calendar = new PlanCalendar();
        var phases = plan.Phases.Select(phase => {
            var tasks = schedule.Where(t => phaseOf[t.Input.Identity] == phase).ToArray();
            var forecast = tasks.Max(t => t.End.Value);
            var due = plan.Milestones.Single(m => m.Phase == phase).Due;
            return new PhaseMeasure(phase, Sum(tasks), tasks.Min(t => t.Start.Value), forecast, due, PlanScheduler.PublishedEndLateness(due, forecast, calendar) ?? 0);
        }).ToArray();
        var holidays = calendar.Holidays.Dates.Select(d => d.Date).ToHashSet();
        var days = 0;
        for (var workingDays = 0; workingDays < 20; days++)
        {
            var day = snapshot.StatusDate.AddDays(days);
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(day)) workingDays++;
        }
        var load = PlanPeople.Calculate(snapshot.Document, snapshot.StatusDate, snapshot.StatusDate, PlanPeriodScale.Day, days);
        var people = plan.People.Select(person => {
            var periods = load.People.Single(p => p.Identity == person.Identity).Periods.Where(p => p.Capacity > 0).Take(20).ToArray();
            return new PersonMeasure(person.Identity, Sum(schedule.Where(t => t.Input.Assignees.Contains(person.Identity))),
                periods.Length, periods.Count(p => p.Overloaded), periods.Length == 0 ? 0 : Math.Round(periods.Max(p => p.Percent ?? 0), 0));
        }).ToArray();
        return new(snapshot.StatusDate, Sum(schedule), schedule.Max(t => t.End.Value), phases, people);
    }
}
