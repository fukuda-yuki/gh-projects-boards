using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace GhProjectsBoards.Core.PlanEditor;

// The attention strip's toggle filters (#260 / #238), in the strip's order.
internal enum PlanToggle { Late, Cause, OverEstimate, NotUpdated, Conflict, Added }

// A task set another surface hands to the plan: a summary drill-down, a milestone, 後続を表示 or the assist.
internal sealed record PlanNamedSet(string Name, ImmutableHashSet<string> Identities, bool IncludeSummaries = false);

// The one filter model (#260 D2): every part combines with AND; the choices inside 担当者 or 工程 with OR.
internal sealed record PlanFilter
{
    internal static readonly PlanFilter None = new();
    public ImmutableHashSet<PlanToggle> Toggles { get; init; } = [];
    // Person identities; PlanPeople.Unassigned stands for 担当者なし.
    public ImmutableHashSet<string> Assignees { get; init; } = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    // 工程 option names; "" stands for 工程なし.
    public ImmutableHashSet<string> Phases { get; init; } = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    public string Title { get; init; } = "";
    public PlanNamedSet? Named { get; init; }
    public bool IsActive => Toggles.Count > 0 || Assignees.Count > 0 || Phases.Count > 0 || Title.Length > 0 || Named is not null;
}

internal sealed record PlanFilterResult(ImmutableArray<string> Matches, ImmutableHashSet<string> Context);

// What needs the PMO's attention now, as identity sets over the open plan. Nothing here compares with an
// earlier read (#260 D1).
internal sealed record PlanAttention(ImmutableHashSet<string> Late, ImmutableHashSet<string> Cause, ImmutableHashSet<string> OverEstimate,
    ImmutableHashSet<string> NotUpdated, ImmutableHashSet<string> Conflict, ImmutableHashSet<string> Added, string? NotUpdatedUnavailable)
{
    internal const int NotUpdatedDays = 7;
    internal const string IssueFieldProgress = "実績と残が Issue フィールドのため、GitHub が更新日時を返しません";
    internal const string UnmappedProgress = "実績と残のフィールドが未設定です";
    internal static readonly PlanAttention Empty = new([], [], [], [], [], [], UnmappedProgress);

    internal ImmutableHashSet<string> Set(PlanToggle toggle) => toggle switch
    {
        PlanToggle.Late => Late, PlanToggle.Cause => Cause, PlanToggle.OverEstimate => OverEstimate,
        PlanToggle.NotUpdated => NotUpdated, PlanToggle.Conflict => Conflict, _ => Added
    };

    internal static PlanAttention From(PlanDocument document, IReadOnlyList<ScheduledTask> scheduled, PlanLatenessResult lateness,
        DateOnly statusDate, TimeZoneInfo? timeZone = null)
    {
        var tasks = scheduled.Where(t => !t.IsSummary).ToArray();
        ImmutableHashSet<string> Ids(Func<ScheduledTask, bool> select) => tasks.Where(select).Select(t => t.Input.Identity).ToImmutableHashSet(StringComparer.Ordinal);
        var rows = document.State.Rows.Select(r => r.Identity).ToHashSet(StringComparer.Ordinal);
        var unavailable = ProgressUnavailable(document);
        var zone = timeZone ?? TimeZoneInfo.Local;
        bool NotUpdated(ScheduledTask task)
        {
            if (task.Input.Closed || task.Start.Value is not { } start || start > statusDate) return false;
            // Only the latest read's observation counts; a row it did not observe is unknown, not stale.
            if (!document.Sync.ProgressUpdatedAt.TryGetValue(task.Input.Identity, out var changed)) return false;
            return changed is not { } at || LocalDate(at, zone).AddDays(NotUpdatedDays) <= statusDate;
        }
        return new(Ids(t => lateness.Tasks[t.Input.Identity].IsLate), Ids(t => lateness.Tasks[t.Input.Identity].IsCause),
            Ids(t => t.IsOverEstimate), unavailable is null ? Ids(NotUpdated) : [],
            PlanOperations.BlockingConflicts(document).Select(c => c.Identity).Where(rows.Contains).ToImmutableHashSet(StringComparer.Ordinal),
            (document.Sync.LastRead?.Added ?? []).Where(rows.Contains).ToImmutableHashSet(StringComparer.Ordinal), unavailable);
    }

    internal static DateOnly LocalDate(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    // GitHub reports a change time only for Project field values (#258 Issue Fields report none).
    internal static string? ProgressUnavailable(PlanDocument document)
    {
        var settings = document.State.Settings;
        var mapped = settings.Columns.Where(c => c.Role is PlanField.Actual or PlanField.Remaining).ToArray();
        if (mapped.Length < 2) return UnmappedProgress;
        return mapped.Any(c => document.Baseline.Columns.FirstOrDefault(f => f.Id == c.FieldId)?.IssueField == true) ? IssueFieldProgress : null;
    }

    // The selection band's progress line: 「実績と残の更新 12/22（22 日前）」, or null while not judged.
    internal static string? ProgressLine(PlanDocument document, string identity, DateOnly statusDate, TimeZoneInfo? timeZone = null)
    {
        if (ProgressUnavailable(document) is not null || !document.Sync.ProgressUpdatedAt.TryGetValue(identity, out var changed)) return null;
        if (changed is not { } at) return "実績と残の更新なし";
        var day = LocalDate(at, timeZone ?? TimeZoneInfo.Local);
        var days = statusDate.DayNumber - day.DayNumber;
        return $"実績と残の更新 {day.ToString("M/d", CultureInfo.InvariantCulture)}（{(days <= 0 ? "当日" : days.ToString(CultureInfo.InvariantCulture) + " 日前")}）";
    }

    // The matching rows in plan order and their requirement rows kept as dimmed, uncounted context (#227).
    internal PlanFilterResult Apply(PlanFilter filter, IReadOnlyList<PlanRow> rows, IReadOnlySet<string> summaries)
    {
        if (!filter.IsActive) return new([], []);
        var byId = rows.ToDictionary(r => r.Identity, StringComparer.Ordinal);
        bool Matches(PlanRow row)
        {
            var summary = summaries.Contains(row.Identity);
            var taskOnly = filter.Toggles.Any(t => t is PlanToggle.Late or PlanToggle.Cause or PlanToggle.OverEstimate or PlanToggle.NotUpdated)
                || filter.Assignees.Count > 0 || filter.Phases.Count > 0;
            if (summary && taskOnly) return false;
            if (filter.Toggles.Any(toggle => !Set(toggle).Contains(row.Identity))) return false;
            if (filter.Assignees.Count > 0 && !(row.Assignees.IsEmpty ? filter.Assignees.Contains(PlanPeople.Unassigned) : row.Assignees.Any(filter.Assignees.Contains))) return false;
            if (filter.Phases.Count > 0 && !filter.Phases.Contains(row.Phase ?? "")) return false;
            if (filter.Title.Length > 0 && !row.Title.Contains(filter.Title, StringComparison.CurrentCultureIgnoreCase)) return false;
            if (filter.Named is { } named && (!named.Identities.Contains(row.Identity) || summary && !named.IncludeSummaries)) return false;
            return true;
        }
        var matches = rows.Where(Matches).Select(r => r.Identity).ToImmutableArray();
        var matched = matches.ToHashSet(StringComparer.Ordinal);
        var context = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var identity in matches)
            for (var parent = byId[identity].Parent; parent is not null && byId.TryGetValue(parent, out var row) && !matched.Contains(parent); parent = row.Parent)
                if (!context.Add(parent)) break;
        return new(matches, context.ToImmutable());
    }
}

internal sealed record PlanChaseGroup(string Name, ImmutableArray<string> Tasks);

// The status line's per-assignee list and 担当者ごとにコピー (#260): who to chase about the filtered tasks.
internal static class PlanChase
{
    internal static ImmutableArray<PlanChaseGroup> Groups(PlanDocument document, IEnumerable<string> matches, IReadOnlySet<string> summaries)
    {
        var rows = document.State.Rows.ToDictionary(r => r.Identity, StringComparer.Ordinal);
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var identity in matches.Where(id => !summaries.Contains(id) && rows.ContainsKey(id)))
            foreach (var person in rows[identity].Assignees.IsEmpty ? [PlanPeople.Unassigned] : rows[identity].Assignees)
                (groups.TryGetValue(person, out var list) ? list : groups[person] = []).Add(identity);
        // 担当者なし sorts with the people by count, then after any name it ties with.
        return groups.Select(g => new PlanChaseGroup(Name(document, g.Key), g.Value.ToImmutableArray()))
            .OrderByDescending(g => g.Tasks.Length).ThenBy(g => g.Name == "担当者なし").ThenBy(g => g.Name, StringComparer.Ordinal).ToImmutableArray();
    }

    private static string Name(PlanDocument document, string person)
    {
        if (person == PlanPeople.Unassigned) return "担当者なし";
        var configured = document.State.Settings.People.FirstOrDefault(p => p.Identity == person)?.Name;
        return !string.IsNullOrWhiteSpace(configured) ? configured : document.Sync.PeopleNames.GetValueOrDefault(person) ?? "担当者（未確認）";
    }

    internal static string Line(IReadOnlyList<PlanChaseGroup> groups)
        => string.Join(" · ", groups.Select(g => $"{g.Name} {g.Tasks.Length.ToString("N0", CultureInfo.InvariantCulture)}"));

    // One block per assignee: 「person（2）」 then 「#524 SS-003 設計 https://…」 per task, blank line between.
    internal static string Text(PlanDocument document, IReadOnlyList<PlanChaseGroup> groups)
    {
        var names = PlanTaskNames.Of(document);
        var rows = document.State.Rows.ToDictionary(r => r.Identity, StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (var group in groups)
        {
            if (text.Length > 0) text.Append("\r\n");
            text.Append(group.Name).Append('（').Append(group.Tasks.Length.ToString(CultureInfo.InvariantCulture)).Append("）\r\n");
            foreach (var identity in group.Tasks)
            {
                text.Append(names[identity]).Append(' ').Append(rows[identity].Title);
                if (document.Sync.IssueLinks.TryGetValue(identity, out var link)) text.Append(' ').Append(link.Url);
                text.Append("\r\n");
            }
        }
        return text.ToString();
    }
}
