using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;
// #231: 完了にする, 中止にする, この版から外す and 未完了に戻す change what a task is in the version.
// 中止 and a pending removal leave the schedule, the totals and the load and stop constraining successors.
[TestFixture]
internal sealed class PlanRowStateTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");
    private string root = null!;
    [SetUp] public void Setup() { root = Path.Combine(Path.GetTempPath(), "ghpb-rowstate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TearDown] public void Cleanup() => Directory.Delete(root, true);
    private static PlanDocument Initial(params PlanRow[] rows)
        => new(Project, new([.. rows], []), new([.. rows], new() { DefaultRepository = "acme/repo", StatusDate = Today, ProjectStart = Today }));
    private static PlanRow Open(string id, string title = "Task") => new(id, title, "acme/repo");
    private static PlanRow Closed(string id, string title = "Task") => new(id, title, "acme/repo") { Closed = true, CloseDate = Today.AddDays(-1) };
    private static ScheduledTask Task(PlanDocument document, string identity) => PlanOperations.Schedule(document, Today).Single(t => t.Input.Identity == identity);
    private async Task<PlanSession> Start(PlanDocument document) => await PlanSession.CreateAsync(new(root), document, Today);

    [Test]
    public async Task CompletingATaskClosesItOnTheStatusDateAsOneUndoStep()
    {
        var session = await Start(Initial(Open("A") with { Estimate = 16 }));
        var result = await session.Execute(new ClosePlanRows(["A"]), Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var row = session.Document.State.Rows.Single(r => r.Identity == "A");
        Assert.That((row.Closed, row.Cancelled, row.CloseDate), Is.EqualTo((true, false, Today)), "The close day is provisional until the publish reads closedAt back.");
        Assert.That(Task(session.Document, "A").Remaining, Is.EqualTo(0), "The remaining work leaves the schedule.");
        Assert.That(Task(session.Document, "A").End.Value, Is.EqualTo(Today));
        Assert.That(session.Changes(Today).Fields["A"], Does.Contain(PlanField.Closed));
        Assert.That(session.UndoCount, Is.EqualTo(1));
        Assert.That((await session.Undo(Today)).Succeeded, Is.True);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "A").Closed, Is.False);
    }

    [Test]
    public async Task CancellingARowTakesItOutOfTheScheduleAndStopsConstrainingItsSuccessor()
    {
        var successor = Open("B") with { Predecessors = ["A"], Remaining = 8 };
        var session = await Start(Initial(Open("A") with { Estimate = 16 }, successor));
        var before = Task(session.Document, "B").Start.Value;
        Assert.That(before, Is.GreaterThan(Today), "The successor waits for the task that precedes it.");
        Assert.That((await session.Execute(new ClosePlanRows(["A"], Cancelled: true), Today)).Succeeded, Is.True);
        var row = session.Document.State.Rows.Single(r => r.Identity == "A");
        Assert.That((row.Closed, row.Cancelled), Is.EqualTo((true, true)));
        var cancelled = Task(session.Document, "A");
        Assert.That(cancelled.Start.Value, Is.Null, "中止 leaves the schedule.");
        Assert.That(cancelled.End.Value, Is.Null);
        Assert.That(cancelled.Estimate, Is.Null, "中止 leaves the totals and the load.");
        Assert.That(cancelled.Remaining, Is.Null);
        Assert.That(cancelled.Actual, Is.Null);
        Assert.That(Task(session.Document, "B").Start.Value, Is.EqualTo(Today), "A 中止 predecessor no longer constrains its successor.");
        Assert.That(session.Changes(Today).Fields["A"], Is.EquivalentTo(new[] { PlanField.Closed, PlanField.Cancelled }));
    }

    [Test]
    public async Task RemovingARowTakesItOutOfTheScheduleAndPublishesOnlyTheArchive()
    {
        var session = await Start(Initial(Open("A") with { Estimate = 16, Assignees = ["p1"] }, Open("B")));
        Assert.That((await session.Execute(new RemovePlanRows(["A"]), Today)).Succeeded, Is.True);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "A").Removed, Is.True);
        Assert.That(Task(session.Document, "A").Start.Value, Is.Null);
        Assert.That(Task(session.Document, "A").Remaining, Is.Null);
        Assert.That(session.Changes(Today).Fields["A"], Does.Contain(PlanField.Removed));
        var baseline = session.Document.Baseline;
        var remote = new PlanRemoteSnapshot(baseline, baseline.Rows.ToImmutableDictionary(r => r.Identity, r => "T-" + r.Identity), [], 0, 0);
        var review = PlanPublishPlan.Build(session.Document, remote, Today, "run");
        var writes = review.Writes.Where(w => w.Identity == "A").ToArray();
        Assert.That(writes.Select(w => w.Mutation), Is.EqualTo(new[] { "archiveProjectV2Item" }), "A removed row is archived and nothing else is written for it.");
        Assert.That(writes[0].Input, Does.Contain("\"itemId\": \"item:A\""));
        Assert.That(review.Changes.Any(c => c.Identity == "A" && c.Field == PlanField.Removed), Is.True);
    }

    [Test]
    public void ClosingPublishesTheStateWriteForEachReasonAndReopeningUndoesIt()
    {
        PlanDocument Closable(PlanRow row) => Initial(row);
        PlanDocument Applied(PlanDocument document, PlanCommand command)
            => document with { State = PlanOperations.Apply(document, command, Today).State };

        var completed = Applied(Closable(Open("A")), new ClosePlanRows(["A"]));
        var closed = PlanPublishPlan.Build(completed, Remote(completed), Today, "run").Writes;
        Assert.That(closed.Select(w => (w.Mutation, w.InputType, w.Stage)).ToArray(),
            Is.EqualTo(new[] { ("closeIssue", "CloseIssueInput", PlanPublishStage.State) }));
        Assert.That(closed[0].Input, Does.Contain("\"stateReason\": \"COMPLETED\""));

        var cancelled = Applied(Closable(Open("B")), new ClosePlanRows(["B"], Cancelled: true));
        var cancelWrite = PlanPublishPlan.Build(cancelled, Remote(cancelled), Today, "run").Writes.Single();
        Assert.That(cancelWrite.Mutation, Is.EqualTo("closeIssue"));
        Assert.That(cancelWrite.Input, Does.Contain("\"stateReason\": \"NOT_PLANNED\""));

        var reopened = Applied(Closable(Closed("C")), new ReopenPlanRows(["C"]));
        var reopenWrite = PlanPublishPlan.Build(reopened, Remote(reopened), Today, "run").Writes.Single();
        Assert.That((reopenWrite.Mutation, reopenWrite.InputType), Is.EqualTo(("reopenIssue", "ReopenIssueInput")));
    }

    [Test]
    public async Task ReopeningKeepsTheDatesAndTheRemainingWork()
    {
        var session = await Start(Initial(Closed("A") with { Estimate = 16, Start = Today.AddDays(-4), End = Today.AddDays(-2) }));
        Assert.That((await session.Execute(new ReopenPlanRows(["A"]), Today)).Succeeded, Is.True);
        var row = session.Document.State.Rows.Single(r => r.Identity == "A");
        Assert.That(row.Closed, Is.False); Assert.That(row.Cancelled, Is.False); Assert.That(row.CloseDate, Is.Null);
        Assert.That(row.Start, Is.EqualTo(Today.AddDays(-4)), "A reopened task keeps its dates.");
        Assert.That(row.End, Is.EqualTo(Today.AddDays(-2)));
        Assert.That(Task(session.Document, "A").Remaining, Is.EqualTo(16), "Its remaining work counts again.");
        Assert.That(session.Changes(Today).Fields["A"], Does.Contain(PlanField.Closed));
    }

    [Test]
    public async Task ARequirementRowTakesItsWholeSubtreeAsOneUndoStep()
    {
        var parent = Open("P", "Requirement");
        var child = Open("C") with { Parent = "P" };
        var grandchild = Open("G") with { Parent = "C" };
        var session = await Start(Initial(parent, child, grandchild, Open("X")));
        Assert.That((await session.Execute(new ClosePlanRows(["P"], Cancelled: true), Today)).Succeeded, Is.True);
        Assert.That(session.Document.State.Rows.Where(r => r.Identity != "X").All(r => r.Closed && r.Cancelled), Is.True);
        Assert.That(session.UndoCount, Is.EqualTo(1));
        Assert.That((await session.Undo(Today)).Succeeded, Is.True);
        Assert.That(session.Document.State.Rows.All(r => !r.Closed), Is.True);
    }

    [Test]
    public void AMixedSelectionAppliesToTheRowsItFitsAndReportsTheRest()
    {
        var document = Initial(Open("A"), Closed("B"), Open("C") with { Cancelled = true, Closed = true, CloseDate = Today });
        var complete = PlanOperations.Eligible(document, ["A", "B", "C"], PlanRowAction.Complete);
        Assert.That(complete.Applies, Is.EqualTo(new[] { "A" }));
        Assert.That(complete.Skipped, Is.EqualTo(new[] { ("B", "すでに完了しています"), ("C", "すでに中止しています") }));
        var reopen = PlanOperations.Eligible(document, ["A", "B", "C"], PlanRowAction.Reopen);
        Assert.That(reopen.Applies, Is.EqualTo(new[] { "B", "C" }));
        Assert.That(reopen.Skipped, Is.EqualTo(new[] { ("A", "未完了の行です") }));
    }

    [Test]
    public async Task ASelectionWithNoEligibleRowChangesNothing()
    {
        var session = await Start(Initial(Closed("A")));
        var before = PlanJson.Text(session.Document);
        var error = Assert.Throws<ArgumentException>(() => { _ = session.Execute(new ClosePlanRows(["A"]), Today); });
        Assert.That(error!.Message, Is.EqualTo(PlanOperations.NothingEligible));
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.Zero);
    }

    [Test]
    public async Task AnUnpublishedRowHasNoIssueToCloseRemoveOrReopen()
    {
        var inserted = PlanRow.New("Local");
        var session = await Start(Initial(Open("A")));
        await session.Execute(new InsertPlanRows([inserted]), Today);
        Assert.That(PlanOperations.Eligible(session.Document, [inserted.Identity], PlanRowAction.Complete).Skipped,
            Is.EqualTo(new[] { (inserted.Identity, "未発行の行です") }));
        Assert.That(PlanOperations.Eligible(session.Document, [inserted.Identity], PlanRowAction.Remove).Skipped,
            Is.EqualTo(new[] { (inserted.Identity, "未発行の行です") }));
        var error = Assert.Throws<ArgumentException>(() => { _ = session.Execute(new ClosePlanRows([inserted.Identity]), Today); });
        Assert.That(error!.Message, Is.EqualTo(PlanOperations.NothingEligible));
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == inserted.Identity).Closed, Is.False);
    }

    private static PlanRemoteSnapshot Remote(PlanDocument document)
        => new(document.Baseline, document.Baseline.Rows.ToImmutableDictionary(r => r.Identity, r => "T-" + r.Identity), [], 0, 0);
}
