using System.Collections.Immutable;
using System.Globalization;

namespace GhProjectsBoards.Core.PlanEditor;

// #308: the Issue history a read observed, kept with the plan as a read observation (never an Undo step and
// never a publish write). The raw events are stored; 着手日 and 完了日 are resolved at display time, so a
// Project's not-started Status options can change without another remote read.
internal enum PlanHistoryEventKind { StatusChanged, Closed, Reopened }
internal sealed record PlanHistoryEvent(PlanHistoryEventKind Kind, DateTimeOffset At, string Actor, bool Automated,
    string? Previous = null, string? Status = null, string? StateReason = null);

// ItemUpdatedAt and IssueUpdatedAt are the values observed by the read that produced this history: a refresh
// re-reads a row only when one of them moved (or the stored plan holds no history for it).
internal sealed record PlanIssueHistory(ImmutableArray<PlanHistoryEvent> Events, DateTimeOffset ItemUpdatedAt, DateTimeOffset IssueUpdatedAt);

internal sealed record PlanActualStart(DateOnly Day, DateTimeOffset At, string Actor, bool Automated, string? From, string? To);
internal sealed record PlanActualFinish(DateOnly Day, DateTimeOffset At, string Actor, bool Automated);

internal static class PlanActualDates
{
    // R3: options that do not count as started, matched by name. No Status is always not started.
    internal static readonly string[] DefaultNotStarted = ["Backlog", "Todo", "Ready"];
    internal static IReadOnlyList<string> NotStarted(ProjectPlanSettings settings) => settings.NotStartedStatuses ?? DefaultNotStarted;
    internal static string ActorText(string actor, bool automated) => automated ? "自動（Project のワークフロー）"
        : string.IsNullOrWhiteSpace(actor) ? "不明" : actor;

    // 着手日 (R4): the latest Status change from a not-started option (or no Status) to a started option while
    // the Issue was not closed. A close-driven change — the item-closed workflow writes the closed Status after
    // ClosedEvent — is not a start, so the close and reopen events bound the Issue's state at that moment.
    internal static PlanActualStart? Start(PlanIssueHistory? history, IReadOnlyList<string> notStarted, TimeZoneInfo zone)
    {
        if (history is null) return null;
        var stopped = new HashSet<string>(notStarted, StringComparer.Ordinal);
        PlanActualStart? latest = null;
        var closed = false;
        foreach (var change in history.Events.OrderBy(e => e.At))
        {
            switch (change.Kind)
            {
                case PlanHistoryEventKind.Closed: closed = true; break;
                case PlanHistoryEventKind.Reopened: closed = false; break;
                case PlanHistoryEventKind.StatusChanged when !closed:
                    var fromNotStarted = string.IsNullOrEmpty(change.Previous) || stopped.Contains(change.Previous);
                    var toStarted = !stopped.Contains(change.Status ?? "");
                    if (fromNotStarted && toStarted)
                        latest = new(Day(change.At, zone), change.At, change.Actor, change.Automated, change.Previous, change.Status);
                    break;
            }
        }
        return latest;
    }

    // 完了日 (R5): the latest close as completed while the Issue is closed. A reopen, a 中止 close (not planned
    // or duplicate) and a provisional local close the read has not recorded give none.
    internal static PlanActualFinish? Finish(PlanIssueHistory? history, bool closed, TimeZoneInfo zone)
    {
        if (history is null || !closed) return null;
        PlanHistoryEvent? latest = null;
        foreach (var change in history.Events.OrderBy(e => e.At))
            if (change.Kind is PlanHistoryEventKind.Closed or PlanHistoryEventKind.Reopened) latest = change;
        return latest is { Kind: PlanHistoryEventKind.Closed, StateReason: "COMPLETED" } close
            ? new(Day(close.At, zone), close.At, close.Actor, close.Automated) : null;
    }

    // The Issue's closed state according to its events, for the refresh's consistency check. Null when the
    // history holds no close or reopen event (an Issue that never closed, or a timeline GitHub did not keep).
    internal static bool? ClosedByEvents(PlanIssueHistory history)
    {
        PlanHistoryEvent? latest = null;
        foreach (var change in history.Events)
            if (change.Kind is PlanHistoryEventKind.Closed or PlanHistoryEventKind.Reopened && (latest is null || change.At >= latest.At)) latest = change;
        return latest?.Kind switch { PlanHistoryEventKind.Closed => true, PlanHistoryEventKind.Reopened => false, _ => null };
    }

    // The tooltip and accessible wording of a record (R8). The App draws it; Core owns the wording so the sheet,
    // the accessible name and the CSV text stay one vocabulary.
    internal static string StartWording(PlanActualStart start, TimeZoneInfo? zone = null)
        => $"{ActorText(start.Actor, start.Automated)} が {start.To} に変更 · {Stamp(start.At, zone)}";
    internal static string FinishWording(PlanActualFinish finish, TimeZoneInfo? zone = null)
        => $"{ActorText(finish.Actor, finish.Automated)} が完了としてクローズ · {Stamp(finish.At, zone)}";
    internal static string Stamp(DateTimeOffset at, TimeZoneInfo? zone = null)
        => TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    internal static DateOnly Day(DateTimeOffset at, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
}
