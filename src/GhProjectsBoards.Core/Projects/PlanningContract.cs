using System.Globalization;
using System.Text.Json;

namespace GhProjectsBoards.Core.Projects;

internal enum PlanningMode { Unplanned, Auto, Manual }
internal enum PlanningProgress { Unstarted, InProgress, Completed, Reopened }
internal sealed record PlanningFieldBinding(string Role, string FieldId, string DataType);
internal sealed record WorkingInterval(int StartMinute, int EndMinute);
internal sealed record CalendarException(DateOnly Date, string? PersonId, WorkingInterval[] Intervals);
internal sealed record HolidayDate(DateOnly Date, string Name);
internal sealed record HolidayPreset(string Version, string Source, string SourceSha256, DateTimeOffset RetrievedAt,
    int FirstYear, int LastYear, HolidayDate[] Dates);
internal sealed record PlanningCalendar(string Revision, HolidayPreset Holidays, bool HolidaysNotConsidered,
    CalendarException[] Exceptions);
internal sealed record PlanningPerson(string Id, string Name, decimal WeightPercent = 100m);
internal sealed record ActualContribution(string? PersonId, decimal Hours, DateOnly ReportedThrough);
internal sealed record WorkContribution(string PersonId, decimal? EstimateHours, decimal? RemainingHours);
internal sealed record PlanningLink(string PredecessorId, string Kind = "FS", DateTime? ExternalFinish = null);
internal sealed record PlanningAssignment(string[] Assignees, bool Complete, bool Legacy = false);
internal sealed record PlanningTask(string Id, PlanningMode Mode = PlanningMode.Unplanned, string? OwnerId = null,
    DateTime? ManualStart = null, DateTime? ManualFinish = null, PlanningProgress Progress = PlanningProgress.Unstarted,
    DateTime? ActualStart = null, DateTime? ActualFinish = null, DateTime? EarliestStart = null,
    DateTime? FixedStart = null, DateTime? FixedFinish = null, DateTime? Deadline = null,
    ActualContribution[]? Actuals = null, WorkContribution[]? Contributions = null,
    PlanningLink[]? LocalLinks = null, PlanningAssignment? Assignment = null);
internal sealed record ProjectPlanning(int Version, string ProjectId, long Stamp, DateTime? Start, DateTime? Cutoff,
    PlanningFieldBinding[] Fields, PlanningCalendar Calendar, PlanningPerson[] People, PlanningTask[] Tasks);

// Dates are wall-clock Asia/Tokyo minutes, never machine-local DateTime or an
// assumed time reconstructed from a GitHub DATE projection.
internal static class PlanningContract
{
    internal static DateTime? ParseMinute(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new InvalidOperationException("日時は yyyy-MM-dd HH:mm（日本時間）で入力してください。");
        return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }
    public static readonly string[] Roles = ["Estimate", "Remaining", "Actual", "Start", "Finish"];
    public static string CanonicalHours(decimal hours)
    {
        if (hours < 0 || hours > 1_000_000_000m || decimal.Round(hours, 8) != hours)
            throw new InvalidOperationException("工数は0以上10億以下、小数8桁以内の時間で入力してください。");
        return hours.ToString("0.########", CultureInfo.InvariantCulture);
    }
    public static decimal ParseHours(string value)
    {
        var parts = value.Split('.');
        if (parts.Length > 2 || parts.Any(p => p.Length == 0 || p.Any(c => c is < '0' or > '9'))
            || parts.Length == 2 && parts[1].Length > 8)
            throw new InvalidOperationException("工数は小数8桁以内の非負数で入力してください。");
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var hours))
            throw new InvalidOperationException("工数は有限の非負数で入力してください。");
        _ = CanonicalHours(hours); return hours;
    }
    public static bool CanPublishHours(decimal hours)
        => CanonicalHours(hours).Replace(".", "", StringComparison.Ordinal).TrimStart('0').Length <= 15;
    public static bool Minute(DateTime? value) => value is null || value.Value.Kind == DateTimeKind.Unspecified
        && value.Value.Ticks % TimeSpan.TicksPerMinute == 0;
    public static string? ProjectDate(DateTime? value)
    {
        if (!Minute(value)) throw new InvalidOperationException("日時は日本時間の分単位で指定してください。");
        return value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
    // No implicit time adoption. B/L/R comparison can identify unchanged projection,
    // but changed/cleared remote dates need explicit endpoint reconciliation.
    public static bool RequiresDateDecision(string? baselineDate, DateTime? adopted, string? remoteDate)
        => remoteDate != baselineDate && ProjectDate(adopted) != remoteDate;

    public static PlanningTask ConfirmRemoteEndpoint(PlanningTask task, bool start, string? remoteDate, DateTime? exact)
    {
        if (ProjectDate(exact) != remoteDate) throw new InvalidOperationException("取得した日付と採用する日時を確認してください。");
        var chosen = start ? task with { Mode = PlanningMode.Manual, ManualStart = exact }
            : task with { Mode = PlanningMode.Manual, ManualFinish = exact };
        if (chosen.ManualFinish < chosen.ManualStart) throw new InvalidOperationException("終了日時は開始日時以降にしてください。");
        return chosen;
    }

    public static HolidayPreset BundledHolidays()
    {
        using var stream = typeof(PlanningContract).Assembly.GetManifestResourceStream("GhProjectsBoards.Core.Planning.JapanHolidays2025-2027.json")!;
        return JsonSerializer.Deserialize<HolidayPreset>(stream)!;
    }

    internal static void Validate(ProjectPlanning p, long revision)
    {
        static bool Id(string? v) => !string.IsNullOrWhiteSpace(v);
        static bool Hours(decimal h) => h >= 0 && h <= 1_000_000_000m && decimal.Round(h, 8) == h;
        if (p is null || p.Version is not (1 or 3) || !Id(p.ProjectId) || p.Stamp < 0 || p.Stamp > revision
            || !Minute(p.Start) || !Minute(p.Cutoff) || p.Fields is null || p.People is null || p.Tasks is null || p.Calendar is null)
            throw new InvalidDataException("Unsupported or invalid planning metadata; preserve the source checkpoint.");
        if (p.Fields.Any(f => f is null || !Roles.Contains(f.Role) || !Id(f.FieldId)
                || f.DataType != (f.Role is "Start" or "Finish" ? "DATE" : "NUMBER"))
            || p.Fields.Select(f => f.Role).Distinct().Count() != p.Fields.Length
            || p.Fields.Select(f => f.FieldId).Distinct().Count() != p.Fields.Length)
            throw new InvalidDataException("Invalid planning field mapping.");
        if (p.People.Any(v => v is null || !Id(v.Id) || v.Name is null || v.WeightPercent < 0 || v.WeightPercent > 100)
            || p.People.Select(v => v.Id).Distinct().Count() != p.People.Length)
            throw new InvalidDataException("Invalid Project/person weights.");
        var c = p.Calendar; var h = c.Holidays;
        if (!Id(c.Revision) || h is null || !Id(h.Version) || !Id(h.Source) || h.SourceSha256 is not { Length: 64 }
            || h.SourceSha256.Any(ch => !Uri.IsHexDigit(ch)) || h.RetrievedAt == default || h.FirstYear < 1 || h.LastYear > 9999 || h.FirstYear > h.LastYear
            || h.Dates is null || h.Dates.Any(d => d is null || d.Name is null || d.Date.Year < h.FirstYear || d.Date.Year > h.LastYear)
            || h.Dates.Select(d => d.Date).Distinct().Count() != h.Dates.Length || c.Exceptions is null || c.Exceptions.Any(e => e is null)
            || c.Exceptions.Select(e => (e.Date, e.PersonId)).Distinct().Count() != c.Exceptions.Length)
            throw new InvalidDataException("Invalid adopted calendar; do not substitute the current preset.");
        foreach (var e in c.Exceptions)
        {
            if (e.Intervals is null || e.PersonId is not null && !Id(e.PersonId)) throw new InvalidDataException("Invalid calendar exception.");
            var end = 0;
            foreach (var i in e.Intervals)
            {
                if (i is null || i.StartMinute < end || i.EndMinute <= i.StartMinute || i.EndMinute > 1440) throw new InvalidDataException("Invalid working intervals.");
                end = i.EndMinute;
            }
        }
        if (p.Tasks.Any(t => t is null) || p.Tasks.Select(t => t.Id).Distinct().Count() != p.Tasks.Length) throw new InvalidDataException("Missing or duplicate planning identity.");
        foreach (var t in p.Tasks)
        {
            if (t is null || !Id(t.Id) || !Enum.IsDefined(t.Mode) || !Enum.IsDefined(t.Progress)
                || p.Version >= 3 && t.Assignment is null
                || t.Assignment is { } assignment && (p.Version < 3 || assignment.Assignees is null
                    || assignment.Assignees.Any(a => !Id(a)) || assignment.Assignees.Distinct().Count() != assignment.Assignees.Length
                    || assignment.Legacy && (assignment.Complete || assignment.Assignees.Length != 0)
                    || !assignment.Legacy && t.OwnerId != (assignment.Complete && assignment.Assignees.Length == 1 ? assignment.Assignees[0] : null))
                || !new[] { t.ManualStart, t.ManualFinish, t.ActualStart, t.ActualFinish, t.EarliestStart, t.FixedStart, t.FixedFinish, t.Deadline }.All(Minute)
                || t.ManualFinish < t.ManualStart || t.ActualFinish < t.ActualStart
                || (t.Actuals ?? []).Any(a => a is null || a.PersonId is not null && !Id(a.PersonId) || !Hours(a.Hours) || a.ReportedThrough == default)
                || (t.Actuals ?? []).Select(a => a.PersonId).Distinct().Count() != (t.Actuals ?? []).Length
                || !Hours((t.Actuals ?? []).Sum(a => a.Hours))
                || (t.Contributions ?? []).Any(a => a is null || !Id(a.PersonId) || a.EstimateHours is { } e && !Hours(e) || a.RemainingHours is { } r && !Hours(r))
                || (t.Contributions ?? []).Select(a => a.PersonId).Distinct().Count() != (t.Contributions ?? []).Length
                || (t.LocalLinks ?? []).Any(l => l is null || !Id(l.PredecessorId) || !Id(l.Kind) || !Minute(l.ExternalFinish))
                || (t.LocalLinks ?? []).Select(l => (l.PredecessorId, l.Kind)).Distinct().Count() != (t.LocalLinks ?? []).Length)
                throw new InvalidDataException("Invalid planning task metadata.");
        }
    }
}

internal sealed partial class EditingWorkspace
{
    private readonly List<ProjectPlanning> planning = [];
    public ProjectPlanning? Planning(string projectId) => planning.SingleOrDefault(p => p.ProjectId == projectId);
    public void SetPlanning(ProjectPlanning plan, long expectedStamp)
    {
        if (Planning(plan.ProjectId)?.Stamp != expectedStamp && !(Planning(plan.ProjectId) is null && expectedStamp == 0))
            throw new InvalidOperationException("計画は変更されています。現在の値を確認してください。");
        var next = plan with { Stamp = Revision + 1 };
        PlanningContract.Validate(next, Revision + 1);
        planning.RemoveAll(p => p.ProjectId == next.ProjectId); planning.Add(next); Revision++; InvalidatePlan(next.ProjectId);
    }
}
