using GhProjectsBoards.Core.PlanEditor;

namespace GhProjectsBoards.Core.Assist;

internal sealed record AssistTaskReference(string Identity, int Id, string Title)
{
    public IReadOnlyList<AssistPerson> Assignees { get; init; } = [];
}
internal sealed record AssistPerson(string Identity, string Name);
internal sealed record AssistPage<T>(int Total, int Offset, bool Complete, IReadOnlyList<T> Items);
internal sealed record AssistFindTasks
{
    public string? Title { get; init; }
    public string? Phase { get; init; }
    public string? Assignee { get; init; }
    public string? Requirement { get; init; }
    public string? State { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; } = 20;
}
internal sealed record AssistPublishedDifference(string Field, string Published, string Current);
internal sealed record AssistTaskDetails(AssistTaskReference Task, AssistTaskReference? Requirement, bool IsSummary,
    string? Phase, bool Closed, IReadOnlyList<AssistPerson> Assignees,
    decimal? Estimate, decimal? Actual, decimal? Remaining, decimal? Forecast,
    PlanDate Start, PlanDate End, string StartReason, string? EndReason,
    IReadOnlyList<AssistPublishedDifference> PublishedDifferences,
    IReadOnlyList<AssistTaskReference> Predecessors, IReadOnlyList<AssistTaskReference> Successors,
    IReadOnlyList<string> Warnings, PlanTaskLateness Lateness, bool Unpublished)
{
    // A summary's Estimate and Remaining are known sums; these say how many leaf tasks they leave out.
    public int MissingEstimates { get; init; }
    public int MissingRemaining { get; init; }
}
internal sealed record AssistOverload(string Identity, string Name, decimal? Remaining, decimal? Capacity, decimal? Allowance);
internal sealed record AssistPlanSummary(DateOnly? Start, DateOnly? End, DateOnly StatusDate, SummaryPlan Measures,
    IReadOnlyList<AssistOverload> Overloads, IReadOnlyList<AssistTaskReference> Tasks);

// A snapshot is captured per tool invocation, not per conversation: local edits remain visible to tools.
internal sealed class AssistPlanReader
{
    private readonly PlanDocument document;
    private readonly DateOnly today;
    private readonly IReadOnlyList<ScheduledTask> schedule;
    private readonly Dictionary<string, ScheduledTask> scheduled;
    private readonly Dictionary<string, PlanRow> rows;
    private readonly Dictionary<string, AssistTaskReference> references;
    private readonly PlanLatenessResult lateness;
    private readonly PlanUnpublished unpublished;
    public AssistPlanReader(PlanDocument document, DateOnly today)
    {
        this.document = document; this.today = today;
        schedule = PlanOperations.Schedule(document, today); scheduled = schedule.ToDictionary(t => t.Input.Identity);
        rows = PlanOperations.EffectiveRows(document).ToDictionary(r => r.Identity);
        references = document.State.Rows.Select((r, i) => new AssistTaskReference(r.Identity, i + 1, r.Title) {
            Assignees = rows[r.Identity].Assignees.Select(Person).ToArray()
        }).ToDictionary(r => r.Identity);
        var settings = document.State.Settings;
        lateness = PlanLateness.Classify(schedule,
            new() { CompanyDaysOff = settings.CompanyDaysOff.ToHashSet(), ImportedHolidays = settings.ImportedHolidays?.ToPreset() }, settings.StatusDate ?? today);
        unpublished = PlanOperations.Changes(document, today);
    }
    internal AssistTaskReference Reference(string identity) => references.TryGetValue(identity, out var task) ? task
        : throw new ArgumentException("開いている計画にないタスクです。");
    internal static AssistPage<T> Page<T>(IReadOnlyList<T> values, int offset, int limit, int maximum = 100)
    {
        if (offset < 0 || limit < 1 || limit > maximum) throw new ArgumentException($"取得位置は0以上、件数は1〜{maximum}で指定してください。");
        var page = values.Skip(offset).Take(limit).ToArray();
        return new(values.Count, offset, offset == 0 && page.Length == values.Count, page);
    }
    public AssistPage<AssistTaskReference> FindTasks(AssistFindTasks filter)
    {
        if (filter.Requirement is { } requirement) _ = Reference(requirement);
        if (filter.From > filter.To) throw new ArgumentException("日付範囲が不正です。");
        if (filter.State is not (null or "open" or "closed" or "overdue" or "later" or "unpublished" or "missing-estimate" or "missing-remaining"))
            throw new ArgumentException("タスクの状態フィルターが不正です。");
        bool Under(PlanRow row, string ancestor)
        {
            for (var parent = row.Parent; parent is not null && rows.TryGetValue(parent, out var parentRow); parent = parentRow.Parent)
                if (parent == ancestor) return true;
            return false;
        }
        var matches = document.State.Rows.Where(r =>
            (filter.Title is null || r.Title.Contains(filter.Title, StringComparison.OrdinalIgnoreCase)) &&
            (filter.Phase is null || rows[r.Identity].Phase == filter.Phase) &&
            (filter.Assignee is null || rows[r.Identity].Assignees.Any(id => id == filter.Assignee || Person(id).Name.Equals(filter.Assignee, StringComparison.OrdinalIgnoreCase))) &&
            (filter.Requirement is null || Under(r, filter.Requirement)) &&
            (filter.From is null || scheduled[r.Identity].End.Value >= filter.From) &&
            (filter.To is null || scheduled[r.Identity].Start.Value <= filter.To) &&
            (filter.State switch {
                "open" => !r.Closed, "closed" => r.Closed,
                "overdue" => !scheduled[r.Identity].IsSummary && lateness.Tasks[r.Identity].Level == PlanLatenessLevel.Overdue,
                "later" => !scheduled[r.Identity].IsSummary && lateness.Tasks[r.Identity].Level == PlanLatenessLevel.Later,
                "unpublished" => unpublished.Fields.ContainsKey(r.Identity),
                "missing-estimate" => r.Estimate is null, "missing-remaining" => r.Remaining is null, _ => true
            })).Select(r => Reference(r.Identity)).ToArray();
        return Page(matches, filter.Offset, filter.Limit);
    }
    internal static string PersonName(PlanDocument document, string id)
    {
        var configured = document.State.Settings.People.FirstOrDefault(p => p.Identity == id)?.Name;
        var synchronized = document.Sync.PeopleNames.GetValueOrDefault(id);
        return !string.IsNullOrWhiteSpace(configured) ? configured
            : !string.IsNullOrWhiteSpace(synchronized) ? synchronized : "担当者（未確認）";
    }
    private AssistPerson Person(string id) => new(id, PersonName(document, id));
    public AssistPage<AssistTaskDetails> GetTasks(IReadOnlyList<string> identities)
    {
        if (identities.Count is < 1 or > 20 || identities.Distinct().Count() != identities.Count)
            throw new ArgumentException("重複のないタスクを1〜20件指定してください。");
        foreach (var id in identities) _ = Reference(id);
        var details = identities.Select(id => {
            var row = rows[id]; var task = scheduled[id]; var old = document.Baseline.Rows.FirstOrDefault(r => r.Identity == id);
            var differences = new List<AssistPublishedDifference>();
            if (old is not null) foreach (var field in AssistProposal.EditableFields) {
                var published = AssistProposal.Display(document, old, field);
                var current = field == PlanField.Start ? AssistProposal.Date(task.Start.Value) : field == PlanField.End ? AssistProposal.Date(task.End.Value)
                    : field == PlanField.Remaining ? AssistProposal.Number(task.Remaining) : AssistProposal.Display(document, row, field);
                if (published != current) differences.Add(new(field, published, current));
            }
            return new AssistTaskDetails(Reference(id), row.Parent is { } parent && references.TryGetValue(parent, out var requirement) ? requirement : null, task.IsSummary,
                row.Phase, row.Closed, row.Assignees.Select(Person).ToArray(), task.Estimate, task.Actual, task.Remaining, task.Forecast,
                task.Start, task.End, task.StartReason, PlanLateness.EndReason(task),
                differences, row.Predecessors.Where(references.ContainsKey).Select(Reference).ToArray(),
                rows.Values.Where(r => r.Predecessors.Contains(id)).Select(r => Reference(r.Identity)).ToArray(),
                task.Warnings, lateness.Tasks[id], unpublished.Fields.ContainsKey(id)) { MissingEstimates = task.MissingEstimates, MissingRemaining = task.MissingRemaining };
        }).ToArray();
        return new(details.Length, 0, true, details);
    }
    public AssistPlanSummary SummarizePlan()
    {
        var summary = PlanSummary.Calculate(document, schedule, document.State.Settings.StatusDate ?? today);
        string Name(string id, string fallback) => id is PlanPeople.Unassigned or PlanPeople.Multiple ? fallback : PersonName(document, id);
        summary = summary with {
            People = summary.People.Select(p => p with { Name = Name(p.Identity, p.Name) }).ToArray(),
            Months = summary.Months is { } months ? months with {
                People = months.People.Select(p => p with { Name = Name(p.Identity, p.Name) }).ToArray()
            } : null
        };
        var settings = document.State.Settings;
        var overloads = summary.People.Where(p => p.Tasks.Remaining.Value > p.Capacity).Select(p =>
            new AssistOverload(p.Identity, p.Name, p.Tasks.Remaining.Value, p.Capacity, settings.People.FirstOrDefault(s => s.Identity == p.Identity)?.Allowance)).ToArray();
        return new(settings.ProjectStart, settings.ProjectEnd, settings.StatusDate ?? today, summary, overloads,
            summary.Total.Identities.Select(Reference).ToArray());
    }
}

internal sealed record AssistManualSection(string Heading, string Markdown);
internal static class AssistManual
{
    public static AssistPage<AssistManualSection> Read(string markdown, string query, int offset = 0, int limit = 5)
    {
        var sections = new List<AssistManualSection>(); var heading = ""; var lines = new List<string>(); var inFence = false;
        void Add() { if (lines.Count > 0) sections.Add(new(heading, string.Join('\n', lines))); lines.Clear(); }
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n')) {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            if (!inFence && System.Text.RegularExpressions.Regex.IsMatch(line, "^#{1,6} +")) { Add(); heading = line.TrimStart('#', ' '); }
            lines.Add(line);
        }
        Add();
        return AssistPlanReader.Page(sections.Where(s => s.Markdown.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray(), offset, limit, 10);
    }
}
