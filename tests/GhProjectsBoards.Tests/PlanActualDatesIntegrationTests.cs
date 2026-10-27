using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #308: the refresh and publish paths of the records. A close never moves a plan date; the close event that a
// published close needs arrives through a read-back, and a refresh reads only the rows it must.
[TestFixture, Category("Integration")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Parallelizable(ParallelScope.All)]
internal sealed class PlanActualDatesIntegrationTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");
    private static readonly TimeZoneInfo Jst = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    private static readonly ProjectPlanSettings Settings = new()
    {
        DefaultRepository = "acme/repo", StatusDate = Today,
        Columns = new[] { PlanField.Estimate, PlanField.Remaining, PlanField.Actual, PlanField.Start, PlanField.End }
            .Select(f => new PlanColumnMapping(f, "F-" + f, f.ToString(), f is PlanField.Estimate or PlanField.Remaining or PlanField.Actual ? "NUMBER" : "DATE")).ToImmutableArray()
    };
    private string root = null!;
    private PlanSession session = null!;
    private PlanPublisher publisher = null!;
    private CountingRunner runner = null!;

    [SetUp]
    public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "ghpb-dates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true }));
    }

    [TearDown]
    public void Cleanup() => Directory.Delete(root, true);

    private static PlanFakeIssue Row(string identity, PlanRow row, params PlanHistoryEvent[] history)
        => new(row, "", true) { History = history.ToImmutableArray(), ItemUpdatedAt = FakePlanEditor.ObservedAt, IssueUpdatedAt = FakePlanEditor.ObservedAt };
    private static PlanHistoryEvent Changed(string at, string previous, string next)
        => new(PlanHistoryEventKind.StatusChanged, Stamp(at), "fukuda-yuki", false, previous, next);
    private static PlanHistoryEvent Closed(string at, string? reason = "COMPLETED")
        => new(PlanHistoryEventKind.Closed, Stamp(at), "fukuda-yuki", false, StateReason: reason);
    private static PlanHistoryEvent Reopened(string at) => new(PlanHistoryEventKind.Reopened, Stamp(at), "fukuda-yuki", false);
    private static DateTimeOffset Stamp(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.RoundtripKind);

    private async Task Start(params PlanFakeIssue[] issues)
    {
        FakePlanEditor.Save(root, new(issues.ToImmutableArray(), issues.Length + 1));
        runner = new(new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }));
        var service = new GhConnectionService(GhProcessTests.FakeExecutable, "github.com", runner);
        publisher = new(service, new("github.com", 42, "fixture-user", GhProcessTests.FakeExecutable));
        session = await PlanSession.CreateAsync(new(root), new(Project, new([], []), new([], Settings)), Today);
        var refresh = await publisher.RefreshAsync(session, Today);
        Assert.That(refresh.Succeeded, Is.True, refresh.Error);
    }

    private ScheduledTask Task(string identity) => PlanOperations.Schedule(session.Document, Today).Single(t => t.Input.Identity == identity);
    private int HistoryRequests() => runner.Commands.Count(c => c.StandardInput?.Contains("PlanIssueHistories", StringComparison.Ordinal) == true);
    private static PlanRow WithDates(string identity, DateOnly start, DateOnly end)
        => new(identity, identity, "acme/repo") { Estimate = 8, Remaining = 8, Start = start, End = end };

    [Test]
    public async Task RefreshRecordsTheCloseEventWithoutMovingThePlanDates()
    {
        var start = Today.AddDays(-5);
        var end = Today.AddDays(-2);
        await Start(Row("I1", WithDates("I1", start, end), Changed("2026-09-29T10:00:00+09:00", "Ready", "In progress")),
            Row("I2", new("I2", "Successor", "acme/repo") { Estimate = 8, Remaining = 8, Predecessors = ["I1"] }));
        Assert.That(Task("I1").Input.ActualStart!.Day, Is.EqualTo(new DateOnly(2026, 9, 29)), "the stored history yields 着手日");
        Assert.That(Task("I1").Input.ActualFinish, Is.Null);

        // Someone closes the Issue on GitHub: the item and Issue updatedAt move, the close event is recorded.
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i.Row.Identity == "I1"
            ? i with { Row = i.Row with { Closed = true, CloseDate = Today.AddDays(-1) },
                History = i.History.Add(Closed("2026-10-04T17:00:00+09:00")), ItemUpdatedAt = FakePlanEditor.ObservedAt.AddDays(1), IssueUpdatedAt = FakePlanEditor.ObservedAt.AddDays(1) }
            : i).ToImmutableArray() });
        var refresh = await publisher.RefreshAsync(session, Today);
        Assert.That(refresh.Succeeded, Is.True, refresh.Error);

        var row = session.Document.State.Rows.Single(r => r.Identity == "I1");
        Assert.That((row.Start, row.End), Is.EqualTo(((DateOnly?)start, (DateOnly?)end)), "a close changes no plan date (R9)");
        Assert.That(row.Closed, Is.True);
        var task = Task("I1");
        Assert.That((task.Start.Value, task.End.Value), Is.EqualTo(((DateOnly?)start, (DateOnly?)end)), "the plan shows the plan dates");
        Assert.That(task.Input.ActualFinish!.Day, Is.EqualTo(new DateOnly(2026, 10, 4)), "完了日 comes from the close event (R5)");
        Assert.That(session.Changes(Today).Fields.TryGetValue("I1", out var fields) ? fields.Contains(PlanField.End) : false, Is.False,
            "closing creates no unpublished date difference");
    }

    [Test]
    public async Task APublishedCloseShowsNoFinishUntilTheReadBackObservesTheEvent()
    {
        var start = Today.AddDays(-5);
        var end = Today.AddDays(-2);
        await Start(Row("I1", WithDates("I1", start, end), Changed("2026-09-29T10:00:00+09:00", "Ready", "In progress")));
        await session.Execute(new ClosePlanRows(["I1"]), Today);
        var published = await publisher.PublishAsync(session, Today);
        Assert.That(published.Succeeded, Is.True, published.Error);

        var row = session.Document.State.Rows.Single(r => r.Identity == "I1");
        Assert.That((row.Start, row.End), Is.EqualTo(((DateOnly?)start, (DateOnly?)end)), "the publish does not derive a date from the close");
        var writes = File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl"));
        Assert.That(writes, Does.Contain("closeIssue"));
        Assert.That(writes, Does.Not.Contain("F-Start"), "no start write follows a close");
        Assert.That(writes, Does.Not.Contain("F-End"), "no end write follows a close");
        Assert.That(Task("I1").Input.ActualFinish, Is.Null, "the provisional close day is not 完了日 (R7)");

        // The read-back did not see the close event yet; the next refresh does.
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i.Row.Identity == "I1"
            ? i with { History = i.History.Add(Closed("2026-10-05T16:00:00+09:00")), ItemUpdatedAt = FakePlanEditor.ObservedAt.AddDays(2), IssueUpdatedAt = FakePlanEditor.ObservedAt.AddDays(2) }
            : i).ToImmutableArray() });
        var refresh = await publisher.RefreshAsync(session, Today);
        Assert.That(refresh.Succeeded, Is.True, refresh.Error);
        var task = Task("I1");
        Assert.That(task.Input.ActualFinish!.Day, Is.EqualTo(Today), "the read-back's event becomes 完了日");
        Assert.That((task.Start.Value, task.End.Value), Is.EqualTo(((DateOnly?)start, (DateOnly?)end)), "the plan dates stay plan values");
    }

    [Test]
    public async Task AReopenClearsTheFinishUntilTheNextClose()
    {
        var start = Today.AddDays(-9);
        var end = Today.AddDays(-4);
        await Start(Row("I1", WithDates("I1", start, end) with { Closed = true, CloseDate = Today.AddDays(-3) },
            Changed("2026-09-20T10:00:00+09:00", "Ready", "In progress"), Closed("2026-10-02T17:00:00+09:00")));
        Assert.That(Task("I1").Input.ActualFinish!.Day, Is.EqualTo(new DateOnly(2026, 10, 2)));
        await session.Execute(new ReopenPlanRows(["I1"]), Today);
        Assert.That(Task("I1").Input.ActualFinish, Is.Null, "a reopen clears 完了日 at once (R5)");
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I1").End, Is.EqualTo(end), "the reopen moves no plan date");
    }

    [Test]
    public async Task ARefreshReadsOnlyTheHistoryOfChangedRowsOrRowsWithoutOne()
    {
        await Start(Row("I1", WithDates("I1", Today.AddDays(-2), Today.AddDays(-1))), Row("I2", WithDates("I2", Today.AddDays(-3), Today.AddDays(-2))));
        var afterFirstRefresh = HistoryRequests();
        Assert.That(afterFirstRefresh, Is.EqualTo(1), "a first refresh reads every row's history in one batch");
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(HistoryRequests(), Is.EqualTo(afterFirstRefresh), "an unchanged row keeps its stored history (R13)");

        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i.Row.Identity == "I2"
            ? i with { History = i.History.Add(Changed("2026-10-03T10:00:00+09:00", "Ready", "In progress")),
                ItemUpdatedAt = FakePlanEditor.ObservedAt.AddDays(3), IssueUpdatedAt = FakePlanEditor.ObservedAt.AddDays(3) }
            : i).ToImmutableArray() });
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(HistoryRequests(), Is.EqualTo(afterFirstRefresh + 1), "only the changed row's history is read again");
        Assert.That(Task("I2").Input.ActualStart!.Day, Is.EqualTo(new DateOnly(2026, 10, 3)));
        Assert.That(session.Document.Sync.IssueHistory["I1"].ItemUpdatedAt, Is.EqualTo(FakePlanEditor.ObservedAt),
            "the unchanged row kept the history from its own read");
    }

    [Test]
    public async Task ACloseWithNoRecordedReasonGivesTheFinishAndNeedsNoSecondHistoryRead()
    {
        var start = Today.AddDays(-5);
        var end = Today.AddDays(-2);
        await Start(Row("I1", WithDates("I1", start, end) with { Closed = true, CloseDate = Today.AddDays(-1) },
            Changed("2026-09-29T10:00:00+09:00", "Ready", "In progress"), Closed("2026-10-04T17:00:00+09:00", null)));
        Assert.That(Task("I1").Input.ActualFinish!.Day, Is.EqualTo(new DateOnly(2026, 10, 4)),
            "a recorded close with no state reason reads as completed, as the row's state already does");
        var afterFirstRefresh = HistoryRequests();

        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(HistoryRequests(), Is.EqualTo(afterFirstRefresh),
            "a reasonless completed close is settled history, not re-read on every refresh");
    }

    [Test]
    public async Task AChangedNotStartedOptionReResolvesTheRecordWithoutAnotherRead()
    {
        await Start(Row("I1", WithDates("I1", Today.AddDays(-2), Today.AddDays(-1)),
            Changed("2026-10-02T10:00:00+09:00", "Ready", "In review")));
        Assert.That(Task("I1").Input.ActualStart!.Day, Is.EqualTo(new DateOnly(2026, 10, 2)));
        var before = HistoryRequests();
        await session.Execute(new ReplacePlanSettings(Settings with { NotStartedStatuses = ["Backlog", "Todo", "Ready", "In review"] }), Today);
        Assert.That(Task("I1").Input.ActualStart, Is.Null, "the configured not-started options decide the record");
        Assert.That(HistoryRequests(), Is.EqualTo(before), "changing the options needs no remote read");
    }

    [Test]
    public void AClosedTaskKeepsItsPlanDatesAndItsCloseDayConstrainsTheSuccessor()
    {
        var close = new DateOnly(2026, 10, 1);
        var closed = new PlanTask("A", 1) { Start = new(2026, 9, 20), End = new(2026, 9, 25), CloseDate = close, Closed = true, Estimate = 8, Remaining = 8 };
        // A plan start before the close day lets the close day constrain the successor without the 基準日 floor.
        var successor = new PlanTask("B", 2) { Predecessors = ["A"], Start = new(2026, 9, 28), Estimate = 8, Remaining = 8 };
        var schedule = PlanScheduler.Calculate([closed, successor], new PlanSettings { StatusDate = Today }, Today);
        var a = schedule.Single(t => t.Input.Identity == "A");
        Assert.That((a.Start.Value, a.End.Value), Is.EqualTo(((DateOnly?)new DateOnly(2026, 9, 20), (DateOnly?)new DateOnly(2026, 9, 25))),
            "a closed task keeps its plan dates");
        Assert.That(schedule.Single(t => t.Input.Identity == "B").Start.Value, Is.EqualTo(close),
            "the successor starts on the close day, the task's effective end (R10)");
    }

    [Test]
    public void AStartedTaskIsNeverOverdueForHavingNoActual()
    {
        var statusDate = new DateOnly(2026, 10, 5);
        // No effort keeps the plan dates as they are, so the start case is the one that decides.
        var started = new PlanTask("A", 1) { Start = new(2026, 10, 1),
            ActualStart = new(new(2026, 10, 1), Stamp("2026-10-01T10:00:00+09:00"), "fukuda-yuki", false, "Ready", "In progress") };
        var notStarted = started with { Identity = "B", RowId = 2, ActualStart = null };
        var calendar = new PlanCalendar();
        var tasks = PlanScheduler.Calculate([started, notStarted], new PlanSettings { StatusDate = statusDate }, statusDate);
        var late = PlanLateness.Classify(tasks, calendar, statusDate);
        Assert.That(late.Tasks["A"].OverdueKind, Is.Null, "着手日 means the task is not 未着手");
        Assert.That(late.Tasks["B"].OverdueKind, Is.EqualTo(PlanOverdueKind.Start));
        Assert.That(late.Tasks["B"].MissedDate, Is.EqualTo(new DateOnly(2026, 10, 1)));
    }

    private sealed class CountingRunner(IGhProcessRunner inner) : IGhProcessRunner
    {
        public List<GhCommand> Commands { get; } = [];
        public async Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken cancellationToken = default)
        {
            lock (Commands) Commands.Add(command);
            return await inner.RunAsync(command, cancellationToken);
        }
    }
}
