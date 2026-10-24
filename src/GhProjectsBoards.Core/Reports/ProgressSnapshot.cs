using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.Reports;

internal sealed record ProgressProject(string Host, string Owner, int Number, string Title, string Url);
internal sealed record ProgressPeriod(DateOnly? Start, DateOnly? End);
internal sealed record ProgressEffort(int Tasks, int Closed, decimal? Estimate, decimal? Actual, decimal? Remaining,
    decimal? Forecast, decimal? Variance, decimal? Progress, int UnknownEffort);
internal sealed record ProgressPhase(string? Phase, string? Milestone, DateOnly? Target, DateOnly? Start, DateOnly? ForecastFinish,
    int? Difference, ProgressEffort Effort, int Late, int OverEstimate);
internal sealed record ProgressLateness(ImmutableArray<string> Late, ImmutableArray<string> Cause, ImmutableArray<string> OverEstimate);
internal sealed record ProgressRequirement(string Issue, string Url, string Title);
internal sealed record ProgressLate(string Kind, DateOnly Date, int? Days);
internal sealed record ProgressTask(string Issue, string Url, string Title, string? Parent, string? Phase, string? Milestone,
    ImmutableArray<string> Assignees, decimal? Estimate, decimal? Actual, decimal? Remaining, DateOnly? Start, DateOnly? End,
    DateOnly? ForecastEnd, bool Closed, DateOnly? CloseDate, ProgressLate? Late, bool Cause, bool OverEstimate);
internal sealed record ProgressSnapshot(string Schema, DateTimeOffset ReadAt, DateOnly StatusDate, string TimeZone,
    ProgressProject Project, ProgressPeriod Period, DateOnly? ForecastFinish, int? FinishDifference, ProgressEffort Totals,
    ImmutableArray<ProgressPhase> Phases, ProgressLateness Lateness, ImmutableArray<ProgressRequirement> Requirements,
    ImmutableArray<ProgressTask> Tasks)
{
    internal const string SchemaV1 = "ghpb-progress-snapshot/1";
    // Snapshots are diffed in Git and read by people, so Japanese text stays unescaped. Reading is as strict as
    // the settings file: every property is required and unknown ones are rejected.
    internal static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Kind == JsonTypeInfoKind.Object)
                foreach (var property in info.Properties) property.IsRequired = true;
        });
        return new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, TypeInfoResolver = resolver };
    }
    internal byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, Options);
    internal static ProgressSnapshot Read(byte[] bytes)
    {
        var snapshot = JsonSerializer.Deserialize<ProgressSnapshot>(bytes, Options) ?? throw new ArgumentException("スナップショットが空です。");
        if (snapshot.Schema != SchemaV1) throw new ArgumentException("未対応のスナップショット形式です: " + snapshot.Schema);
        return snapshot;
    }

    // The same scheduled result, lateness and summary the app shows for this document and 基準日 (#262).
    internal static ProgressSnapshot Build(ProjectChoice project, PlanDocument document, DateOnly statusDate, string timeZone, DateTimeOffset readAt)
    {
        var settings = document.State.Settings;
        var scheduled = PlanOperations.Schedule(document, statusDate);
        var summary = PlanSummary.Calculate(document, scheduled, statusDate);
        var calendar = new PlanCalendar { CompanyDaysOff = settings.CompanyDaysOff.ToHashSet(), ImportedHolidays = settings.ImportedHolidays?.ToPreset() };
        var lateness = PlanLateness.Classify(scheduled, calendar, statusDate);
        var rows = document.State.Rows.ToDictionary(r => r.Identity, StringComparer.Ordinal);
        var links = document.Sync.IssueLinks;
        string Issue(string identity) => links.TryGetValue(identity, out var link) ? link.Caption : identity;
        string Url(string identity) => links.TryGetValue(identity, out var link) ? link.Url : "";
        var tasks = scheduled.Where(t => !t.IsSummary).ToArray();
        var order = tasks.Select((t, n) => (t.Input.Identity, n)).ToDictionary(p => p.Identity, p => p.n, StringComparer.Ordinal);
        // #260 (owner): 期限超過 and 予定より遅れ are one 遅れ count; each task keeps which case applies.
        var late = summary.Attention.Overdue.Concat(summary.Attention.Later).ToHashSet(StringComparer.Ordinal);
        var cause = summary.Attention.Cause.ToHashSet(StringComparer.Ordinal);
        var overEstimate = summary.Attention.OverEstimate.ToHashSet(StringComparer.Ordinal);
        var unknown = summary.Attention.UnknownEffort.ToHashSet(StringComparer.Ordinal);
        ImmutableArray<string> References(IEnumerable<string> identities) => identities.OrderBy(id => order[id]).Select(Issue).ToImmutableArray();
        ProgressEffort Effort(SummaryTasks sum) => new(sum.Count, sum.Closed, sum.Estimate.Value, sum.Actual.Value, sum.Remaining.Value,
            sum.Forecast.Value, sum.Variance.Value, sum.Progress.Value is { } progress ? Math.Round(progress, 4) : null,
            sum.Identities.Count(unknown.Contains));
        ProgressLate? Late(ScheduledTask task)
        {
            var state = lateness.Tasks[task.Input.Identity];
            return state.Level switch
            {
                PlanLatenessLevel.Overdue => new(state.OverdueKind == PlanOverdueKind.Start ? "start" : "finish", state.MissedDate!.Value, state.DaysLater),
                PlanLatenessLevel.Later => new("forecast", task.ForecastEnd!.Value, state.DaysLater),
                _ => null
            };
        }
        string Login(string person) => document.Sync.PeopleNames.GetValueOrDefault(person)
            ?? settings.People.FirstOrDefault(p => p.Identity == person)?.Name ?? person;
        return new(SchemaV1, readAt.ToUniversalTime(), statusDate, timeZone,
            new(project.Id.Scope.Host, project.OwnerLogin, project.Number, project.Title, project.Url),
            new(settings.ProjectStart, settings.ProjectEnd), summary.ForecastFinish, summary.FinishDifference, Effort(summary.Total),
            summary.Phases.Where(p => !p.IsTotal).Select(p => new ProgressPhase(p.Phase,
                p.Phase is { } phase ? settings.PhaseMilestones.GetValueOrDefault(phase) : null, p.Target, p.Tasks.Start, p.Tasks.Finish,
                p.Difference, Effort(p.Tasks), p.Tasks.Identities.Count(late.Contains), p.Tasks.OverEstimate)).ToImmutableArray(),
            new(References(late), References(cause), References(overEstimate)),
            scheduled.Where(t => t.IsSummary).Select(t => new ProgressRequirement(Issue(t.Input.Identity), Url(t.Input.Identity),
                rows[t.Input.Identity].Title)).ToImmutableArray(),
            tasks.Select(t => {
                var row = rows[t.Input.Identity];
                return new ProgressTask(Issue(row.Identity), Url(row.Identity), row.Title, row.Parent is { } parent ? Issue(parent) : null,
                    row.Phase, row.MilestoneTitle, row.Assignees.Select(Login).ToImmutableArray(), t.Estimate, t.Actual, t.Remaining,
                    t.Start.Value, t.End.Value, t.ForecastEnd, row.Closed, row.CloseDate, Late(t),
                    cause.Contains(row.Identity), overEstimate.Contains(row.Identity));
            }).ToImmutableArray());
    }
}
