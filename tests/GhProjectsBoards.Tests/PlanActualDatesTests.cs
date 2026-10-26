using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #308: 着手日 and 完了日 are resolved from the Issue's stored history events, never from a field. These cases
// carry the rules the sheet, the CSV and the Gantt read: latest not-started -> started change, the closed state
// at the event, the latest completed close, local days and the stored format.
[TestFixture]
internal sealed class PlanActualDatesTests
{
    private static readonly TimeZoneInfo Jst = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    private static readonly string[] DefaultStopped = ["Backlog", "Todo", "Ready"];

    private static PlanHistoryEvent Status(string at, string previous, string next, string actor = "fukuda-yuki")
        => new(PlanHistoryEventKind.StatusChanged, Stamp(at), actor, false, previous, next);
    private static PlanHistoryEvent Closed(string at, string? reason = "COMPLETED", string actor = "fukuda-yuki")
        => new(PlanHistoryEventKind.Closed, Stamp(at), actor, false, StateReason: reason);
    private static PlanHistoryEvent Reopened(string at) => new(PlanHistoryEventKind.Reopened, Stamp(at), "fukuda-yuki", false);
    private static DateTimeOffset Stamp(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.RoundtripKind);
    private static PlanIssueHistory History(params PlanHistoryEvent[] events)
        => new(events.OrderBy(e => e.At).ToImmutableArray(), Stamp("2026-10-20T00:00:00Z"), Stamp("2026-10-20T00:00:00Z"));

    [Test]
    public void StartTakesTheLatestChangeFromANotStartedOption()
    {
        var history = History(Status("2026-10-01T03:00:00Z", "Ready", "In progress"),
            Status("2026-10-03T03:00:00Z", "In progress", "Ready"),
            Status("2026-10-05T03:00:00Z", "Ready", "In progress"));
        var start = PlanActualDates.Start(history, DefaultStopped, Jst);
        Assert.That(start, Is.Not.Null);
        Assert.That(start!.Day, Is.EqualTo(new DateOnly(2026, 10, 5)), "a repeat Ready -> In progress gives the newer day (R4)");
        Assert.That((start.From, start.To, start.Actor), Is.EqualTo(("Ready", "In progress", "fukuda-yuki")));
    }

    [Test]
    public void StartKeepsTheFirstStartedDayThroughLaterStartedOptions()
    {
        var history = History(Status("2026-10-01T03:00:00Z", "Ready", "In progress"),
            Status("2026-10-04T03:00:00Z", "In progress", "In review"),
            Status("2026-10-08T03:00:00Z", "In review", "In progress"));
        Assert.That(PlanActualDates.Start(history, DefaultStopped, Jst)!.Day, Is.EqualTo(new DateOnly(2026, 10, 1)),
            "In progress -> In review -> In progress does not move the record");
    }

    [Test]
    public void StartCountsAnyStartedOptionAndNoStatus()
    {
        var byMove = History(Status("2026-10-02T03:00:00Z", "Ready", "In review"));
        Assert.That(PlanActualDates.Start(byMove, DefaultStopped, Jst)!.Day, Is.EqualTo(new DateOnly(2026, 10, 2)),
            "a task moved straight from Ready to In review still starts");
        var fromNoStatus = History(Status("2026-10-06T03:00:00Z", "", "In progress"));
        Assert.That(PlanActualDates.Start(fromNoStatus, DefaultStopped, Jst)!.Day, Is.EqualTo(new DateOnly(2026, 10, 6)),
            "no Status counts as not started");
        var withoutStartedOptions = History(Status("2026-10-02T03:00:00Z", "Ready", "Done"));
        Assert.That(PlanActualDates.Start(withoutStartedOptions, ["Backlog", "Todo", "Ready", "Done"], Jst), Is.Null,
            "a configured not-started option never starts the task");
    }

    [Test]
    public void ACloseDrivenStatusWriteIsNotAStart()
    {
        var closed = History(Status("2026-10-01T03:00:00Z", "Ready", "In progress"), Closed("2026-10-09T06:00:00Z"),
            Status("2026-10-09T06:00:03Z", "In progress", "Done"));
        Assert.That(PlanActualDates.Start(closed, DefaultStopped, Jst)!.Day, Is.EqualTo(new DateOnly(2026, 10, 1)));

        var neverStarted = History(Closed("2026-10-09T06:00:00Z"), Status("2026-10-09T06:00:03Z", "Ready", "Done"));
        Assert.That(PlanActualDates.Start(neverStarted, DefaultStopped, Jst), Is.Null,
            "the item-closed workflow writes the closed Status after the close, so that write is not a start");

        var reopenedFirst = History(Closed("2026-10-02T00:00:00Z"), Reopened("2026-10-03T00:00:00Z"),
            Status("2026-10-03T01:00:00Z", "Ready", "In progress"));
        Assert.That(PlanActualDates.Start(reopenedFirst, DefaultStopped, Jst)!.Day, Is.EqualTo(new DateOnly(2026, 10, 3)),
            "a status change after a reopen counts again");
    }

    [Test]
    public void DaysUseTheGivenLocalZone()
    {
        var history = History(Status("2026-10-24T16:30:00Z", "Ready", "In progress"));
        Assert.That(PlanActualDates.Start(history, DefaultStopped, Jst)!.Day, Is.EqualTo(new DateOnly(2026, 10, 25)),
            "16:30 UTC is the next day in JST");
        Assert.That(PlanActualDates.Start(history, DefaultStopped, TimeZoneInfo.Utc)!.Day, Is.EqualTo(new DateOnly(2026, 10, 24)));
    }

    [Test]
    public void StartWordingNamesTheActorTheTargetAndTheLocalTime()
    {
        var start = PlanActualDates.Start(History(Status("2026-10-24T08:04:00Z", "Ready", "In progress")), DefaultStopped, Jst)!;
        Assert.That(PlanActualDates.StartWording(start, Jst), Is.EqualTo("fukuda-yuki が In progress に変更 · 2026-10-24 17:04"));
        var automated = start with { Actor = "github-project-automation", Automated = true };
        Assert.That(PlanActualDates.StartWording(automated, Jst), Does.StartWith("自動（Project のワークフロー） が In progress に変更"));
    }

    [Test]
    public void FinishTakesTheLatestCompletedCloseWhileClosed()
    {
        var completed = History(Closed("2026-10-10T06:00:00Z"));
        var finish = PlanActualDates.Finish(completed, closed: true, Jst);
        Assert.That(finish, Is.Not.Null);
        Assert.That(finish!.Day, Is.EqualTo(new DateOnly(2026, 10, 10)));
        Assert.That(PlanActualDates.FinishWording(finish, Jst), Is.EqualTo("fukuda-yuki が完了としてクローズ · 2026-10-10 15:00"));

        Assert.That(PlanActualDates.Finish(History(Closed("2026-10-10T06:00:00Z", "NOT_PLANNED")), true, Jst), Is.Null,
            "a 中止 close gives no 完了日 (R5)");
        Assert.That(PlanActualDates.Finish(History(Closed("2026-10-10T06:00:00Z"), Reopened("2026-10-12T06:00:00Z")), true, Jst), Is.Null,
            "a reopen clears the record");
        Assert.That(PlanActualDates.Finish(completed, closed: false, Jst), Is.Null, "an open Issue has no 完了日");
        Assert.That(PlanActualDates.Finish(null, true, Jst), Is.Null);
        Assert.That(PlanActualDates.Finish(History(Closed("2026-10-10T06:00:00Z", null)), true, Jst), Is.Null,
            "only a completed close counts");
    }

    [Test]
    public void ClosedByEventsReportsTheStateTheTimelineHolds()
    {
        Assert.That(PlanActualDates.ClosedByEvents(History(Closed("2026-10-10T06:00:00Z"))), Is.True);
        Assert.That(PlanActualDates.ClosedByEvents(History(Closed("2026-10-10T06:00:00Z"), Reopened("2026-10-12T06:00:00Z"))), Is.False);
        Assert.That(PlanActualDates.ClosedByEvents(History(Status("2026-10-01T00:00:00Z", "Ready", "In progress"))), Is.Null);
    }

    [Test]
    public void NotStartedDefaultsToNoStatusBacklogTodoAndReady()
    {
        var settings = new ProjectPlanSettings();
        Assert.That(PlanActualDates.NotStarted(settings), Is.EqualTo(new[] { "Backlog", "Todo", "Ready" }));
        Assert.That(PlanActualDates.NotStarted(settings with { NotStartedStatuses = [] }), Is.Empty,
            "an explicit empty list means every option starts");
        Assert.That(PlanActualDates.NotStarted(settings with { NotStartedStatuses = ["待機"] }), Is.EqualTo(new[] { "待機" }));
        var document = new PlanDocument(new(new("github.com", 42), "P1"),
            new([new PlanRow("I1", "Task", "acme/repo")], []),
            new([new PlanRow("I1", "Task", "acme/repo")], settings));
        Assert.DoesNotThrow(() => PlanOperations.ValidateDocument(document, new DateOnly(2026, 10, 5)));
    }

    [Test]
    public void APlanSavedBeforeTheHistoryStillOpensWithEmptyObservationsAndDefaultOptions()
    {
        var project = new ScopedId(new ConnectionScope("github.com", 42), "P1");
        var row = new PlanRow("I1", "Task", "acme/repo");
        var document = new PlanDocument(project, new([row], []), new([row], new ProjectPlanSettings {
            StatusDate = new DateOnly(2026, 10, 5),
            SheetColumns = [new("Id", true, null)] }));
        var json = JsonNode.Parse(PlanJson.Text(document))!.AsObject();
        json["sync"]!.AsObject().Remove("issueHistory");
        foreach (var settings in Objects(json).Where(o => o.ContainsKey("sheetColumns")))
            settings.Remove("notStartedStatuses");
        var read = PlanJson.Read<PlanDocument>(Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.That(read.Sync.IssueHistory, Is.Empty);
        Assert.That(read.State.Settings.NotStartedStatuses, Is.Null, "null keeps the defaults");
        Assert.That(PlanActualDates.NotStarted(read.State.Settings), Is.EqualTo(new[] { "Backlog", "Todo", "Ready" }));
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var (_, child) in obj.ToArray())
                foreach (var found in Objects(child)) yield return found;
        }
        else if (node is JsonArray list)
            foreach (var child in list.ToArray())
                foreach (var found in Objects(child)) yield return found;
    }

    [Test]
    public void NotStartedOptionsRoundTripInTheStoredPlan()
    {
        var project = new ScopedId(new ConnectionScope("github.com", 42), "P1");
        var row = new PlanRow("I1", "Task", "acme/repo");
        var settings = new ProjectPlanSettings { StatusDate = new DateOnly(2026, 10, 5), NotStartedStatuses = ["待機", "Ready"] };
        var document = new PlanDocument(project, new([row], []), new([row], settings));
        var read = PlanJson.Read<PlanDocument>(Encoding.UTF8.GetBytes(PlanJson.Text(document)));
        Assert.That(read.State.Settings.NotStartedStatuses, Is.EqualTo(new[] { "待機", "Ready" }));
    }
}
