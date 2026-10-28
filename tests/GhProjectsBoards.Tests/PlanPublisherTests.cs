using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;
[TestFixture, Category("Integration")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Parallelizable(ParallelScope.All)]
internal sealed partial class PlanPublisherTests
{
    [TestCase(false, false), TestCase(true, false), TestCase(false, true), TestCase(true, true)]
    public async Task MilestoneStateFixtureReadsBothRepositoriesCompletely(bool closed, bool conflict)
    {
        Scenario();
        FakePlanEditor.Save(root, new([
            new(new("I1", "First", "acme/repo") { MilestoneTitle = "Release" }, "", true),
            new(new("I2", "Second", "acme/other") { MilestoneTitle = "Release" }, "", true)], 3) {
            Milestones = [new("M1", "acme/repo", "Release", Today, closed), new("M2", "acme/other", "Release", conflict ? Today.AddDays(1) : Today, closed)] });
        runner = new(new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }));
        var service = new GhConnectionService(GhProcessTests.FakeExecutable, "github.com", runner);
        var result = await new ProjectReader(service).ReadAsync(new("github.com", 42, "fixture-user", GhProcessTests.FakeExecutable), Project);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), string.Join(", ", result.Problems));
        var snapshot = PlanSnapshot.From(result, Settings);
        Assert.That(snapshot.Baseline.Rows.Select(r => (r.Title, r.Repository, r.MilestoneTitle)), Is.EqualTo(new[] {
            ("First", "acme/repo", "Release"), ("Second", "acme/other", "Release") }));
        Assert.That(snapshot.Baseline.Milestones, Is.EqualTo(new[] { new PlanMilestone("Release", conflict ? Today.AddDays(1) : Today, closed, conflict) }));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test]
    public async Task MilestoneCatalogPagesGroupsAndRefreshResetAndPublishNeverWriteMembership()
    {
        await Start(3, state => state with {
            Milestones = Enumerable.Range(0, 101).Select(n => new PlanFakeMilestone("M" + n, "acme/repo", "Target" + n, Today))
                .Append(new("Other", "acme/other", "Target0", Today.AddDays(3), true)).ToImmutableArray(),
            Issues = state.Issues.Select((i, n) => i with { Row = i.Row with {
                Repository = n == 2 ? "acme/other" : "acme/repo", MilestoneTitle = "Target0" } }).ToImmutableArray()
        });
        Assert.That(session.UndoCount, Is.Zero);
        var first = session.Document.Baseline.Milestones.Single(m => m.Title == "Target0");
        Assert.That((first.DueDate, first.ConflictingDates, first.Closed), Is.EqualTo((Today.AddDays(3), true, false)));
        Assert.That(session.Document.Baseline.Milestones.Length, Is.EqualTo(101));
        Assert.That(session.Document.Baseline.Rows.All(r => r.MilestoneTitle == "Target0"), Is.True);
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with {
            Milestones = remote.Milestones.Select(m => m with { DueDate = Today.AddDays(4), Closed = true }).ToImmutableArray(),
            Issues = remote.Issues.Select(i => i with { Row = i.Row with { MilestoneTitle = null } }).ToImmutableArray()
        });
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(session.Document.Baseline.Milestones.Single(m => m.Title == "Target0"), Is.EqualTo(new PlanMilestone("Target0", Today.AddDays(4), true, false)));
        Assert.That(session.Document.State.Rows.All(r => r.MilestoneTitle is null), Is.True);
        Assert.That(session.UndoCount, Is.Zero);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Edited")]), Today);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with {
            Milestones = remote.Milestones.Select(m => m with { DueDate = Today.AddDays(5) }).ToImmutableArray(),
            Issues = remote.Issues.Select(i => i.Row.Identity == "I1" ? i with { Row = i.Row with { MilestoneTitle = "Target1" } } : i).ToImmutableArray()
        });
        var writesBeforeReset = remote.MutationBatches;
        Assert.That((await publisher.ResetAsync(session, Today)).Succeeded, Is.True);
        Assert.That(session.Document.Baseline.Milestones.All(m => m.DueDate == Today.AddDays(5)), Is.True);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I1").MilestoneTitle, Is.EqualTo("Target1"));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I1").MilestoneTitle, Is.EqualTo("Target1"));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(writesBeforeReset));
        var writes = File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl"));
        Assert.That(writes, Does.Not.Contain("milestone").IgnoreCase);
    }

    [Test]
    public async Task IncompleteMilestoneCatalogCannotReplaceBaseline()
    {
        await Start(1);
        var before = PlanJson.Text(session.Document);
        Scenario("milestones-incomplete");
        var result = await publisher.RefreshAsync(session, Today);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PlanReset")]
    public async Task ResetAdoptsFreshValuesAndDatesAndUndoRestoresMergedWork()
    {
        var settings = Settings with { People = [new("U1", "Alice", 50, 20)], CompanyDaysOff = [Today.AddDays(2)], ProjectStart = Today.AddDays(-10) };
        await Start(3, settings: settings);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Older edit")]), Today);
        var added = PlanRow.New("Never created", "acme/repo");
        await session.Execute(new InsertPlanRows([added]), Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Actual, 4m)]), Today);
        var remote = FakePlanEditor.Load(root);
        // Plan dates are working days (#236): the reset must show GitHub's dates exactly, so the fixture's
        // dates must be ones the schedule can reproduce. Today-2/Today-1 are the weekend, which the
        // scheduler normalises to the next working day and then recomputes the end from, so that pair can
        // never be a fixed point of the schedule.
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Where(i => i.Row.Identity != "I3").Select(i => i with {
            Row = i.Row with { Title = "Remote " + i.Row.Identity, Remaining = 16, Start = Today.AddDays(-3), End = Today }
        }).Reverse().ToImmutableArray() });
        await session.SaveSync(session.Document.Sync with { Failures = [new("I2", PlanField.Actual, "Verified failure")] });
        var day = Today.AddDays(7);
        var result = await publisher.ResetAsync(session, day);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.Multiple(() => {
            Assert.That(PlanJson.Text(session.Document.State.Rows), Is.EqualTo(PlanJson.Text(session.Document.Baseline.Rows)));
            Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "I2", "I1" }));
            Assert.That(session.Document.State.Rows.Select(r => r.Title), Is.EqualTo(new[] { "Remote I2", "Remote I1" }));
            Assert.That(session.Document.State.Settings, Is.EqualTo(settings with { StatusDate = day }));
            Assert.That(session.Document.Sync.Conflicts, Is.Empty);
            Assert.That(session.Document.Sync.Unavailable, Is.Empty);
            Assert.That(session.Document.Sync.Failures, Is.Empty);
            Assert.That(session.Changes(day).TaskCount, Is.Zero);
            Assert.That(session.Schedule(day).All(t => t.Start.Value == t.Input.Start && t.End.Value == t.Input.End), Is.True,
                "Reset takes GitHub's dates as the plan dates.");
        });
        await Reopen();
        await session.Undo(day);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I1").Title, Is.EqualTo("Older edit"));
        Assert.That(session.Document.Baseline.Rows.Single(r => r.Identity == "I1").Title, Is.EqualTo("Remote I1"));
        Assert.That(session.Document.State.Rows.Any(r => r.Identity == added.Identity), Is.True);
        Assert.That(session.Document.Sync.Unavailable, Does.Contain("I3"));
        Assert.That(session.Document.Sync.Conflicts.Any(c => c.Identity == "I1" && c.Field == PlanField.Title), Is.True);
        Assert.That(session.Changes(day).TaskCount, Is.GreaterThan(0));
        await session.SaveSync(session.Document.Sync with { Failures = [new("I2", PlanField.Actual, "Verified failure after Undo")] });
        await session.Redo(day);
        Assert.That(session.Changes(day).TaskCount, Is.Zero);
        Assert.That(session.Document.Sync.Failures, Is.Empty);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "After reset")]), day);
        Assert.That(session.Schedule(day).Any(t => t.Start.Value != Today.AddDays(-3)), Is.False, "A title edit moves no date.");
        await session.Undo(day); // edit
        await session.Undo(day); // reset
        await session.Undo(day); // actual
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I2").Actual, Is.Null);
        await session.Undo(day); // insert
        Assert.That(session.Document.State.Rows.Any(r => r.Identity == added.Identity), Is.False);
        await session.Undo(day); // older title
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I1").Title, Is.EqualTo("Remote I1"));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PlanReset")]
    public async Task ResetAtZeroUnpublishedReadsAgainAndKeepsGitHubProjectOrder()
    {
        await Start(3, state => state with {
            Issues = state.Issues.Select(i => i.Row.Identity == "I1" ? i : i with { Row = i.Row with { Parent = "I1" } }).ToImmutableArray(),
            SubOrders = state.SubOrders.Add("I1", ["I3", "I2"])
        });
        Assert.That((await publisher.ResetAsync(session, Today)).Succeeded, Is.True);
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i with {
            Row = i.Row with { Start = Today.AddDays(-3), End = i.Row.Identity == "I1" ? null : Today.AddDays(-1) }
        }).ToImmutableArray() });
        var day = Today.AddDays(7);
        Assert.That((await publisher.ResetAsync(session, day)).Succeeded, Is.True);
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(day));
        Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "I1", "I2", "I3" }));
        Assert.That(session.Document.Sync.NativeOrders["I1"], Is.EqualTo(new[] { "I3", "I2" }));
        // #236: the read adopted GitHub's start for I1, and the plan lays that task's end out from the work it
        // holds; GitHub's other ends are kept as they are, so nothing is an unpublished difference here.
        Assert.That(session.Schedule(day).Single(r => r.Input.Identity == "I1").Start.Value, Is.EqualTo(Today.AddDays(-3)));
        Assert.That(session.Changes(day).TaskCount, Is.Zero);
        Assert.That((await publisher.ResetAsync(session, day.AddDays(1))).Succeeded, Is.True);
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(day.AddDays(1)));
        Assert.That(session.Changes(day).TaskCount, Is.Zero);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase(false), TestCase(true), Category("PlanReset")]
    public async Task ResetFailedOrCancelledReadPreservesStoredCheckpoint(bool cancel)
    {
        await Start(1);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local")]), Today);
        var before = PlanJson.Text(session.Document);
        var bytes = File.ReadAllBytes(new PlanStore(root).FileFor(Project));
        if (cancel) {
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<RemoteProgress>(p => { if (p.Stage == "日程を計算") cancellation.Cancel(); });
            Assert.ThrowsAsync<OperationCanceledException>(() => publisher.ResetAsync(session, Today.AddDays(7), cancellation.Token, progress));
        } else {
            Scenario("readfailure");
            Assert.That((await publisher.ResetAsync(session, Today.AddDays(7))).Succeeded, Is.False);
        }
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
        Assert.That(File.ReadAllBytes(new PlanStore(root).FileFor(Project)), Is.EqualTo(bytes));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase(false), TestCase(true), Category("PlanReset")]
    public async Task ResetRejectsUnreconciledPublicationWithoutChangingCheckpoint(bool unverified)
    {
        await Start(1);
        await session.SaveSync(session.Document.Sync with {
            Publish = new(Guid.NewGuid().ToString("N"), []), Unverified = unverified ? ["I1"] : []
        });
        var bytes = File.ReadAllBytes(new PlanStore(root).FileFor(Project));
        var result = await publisher.ResetAsync(session, Today);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Does.Contain("発行"));
        Assert.That(File.ReadAllBytes(new PlanStore(root).FileFor(Project)), Is.EqualTo(bytes));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PlanReset")]
    public async Task ResetRejectsAnotherRemoteOperation()
    {
        await Start(1);
        var bytes = File.ReadAllBytes(new PlanStore(root).FileFor(Project));
        using var operation = session.BeginRemoteOperation();
        Assert.That((await publisher.ResetAsync(session, Today)).Succeeded, Is.False);
        Assert.That(File.ReadAllBytes(new PlanStore(root).FileFor(Project)), Is.EqualTo(bytes));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase(false), TestCase(true)]
    public async Task RefreshFailureOrCancellationPreservesDatePlanAndHistory(bool cancel)
    {
        await Start(1);
        await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { StatusDate = Today.AddDays(-3) }), Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local")]), Today);
        var before = session.Document;
        var bytes = File.ReadAllBytes(new PlanStore(root).FileFor(Project));
        var undo = session.UndoCount;
        if (cancel)
        {
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<RemoteProgress>(p => { if (p.Stage == "日程を計算") cancellation.Cancel(); });
            Assert.ThrowsAsync<OperationCanceledException>(() => publisher.RefreshAsync(session, Today.AddDays(10), cancellation.Token, progress));
        }
        else
        {
            Scenario("readfailure");
            Assert.That((await publisher.RefreshAsync(session, Today.AddDays(10))).Succeeded, Is.False);
        }
        Assert.That(session.Document, Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
        Assert.That(File.ReadAllBytes(new PlanStore(root).FileFor(Project)), Is.EqualTo(bytes));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test]
    public async Task RefreshReportsChangedDatesAndSupersedesPickedDateWhilePublishKeepsIt()
    {
        await Start(1, state => state with { Issues = state.Issues.Select(i => i with { Row = i.Row with { Remaining = 16 } }).ToImmutableArray() });
        var picked = Today.AddDays(1);
        await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { StatusDate = picked }), Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local")]), Today);
        var published = await publisher.PublishAsync(session, Today.AddDays(10));
        Assert.That(published.Succeeded, Is.True, published.Error);
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(picked));
        // #236: a 基準日 change never moves a task date, and a task with no dates of its own stays without one.
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Start, Is.Null);
        var refreshed = await publisher.RefreshAsync(session, Today.AddDays(3));
        Assert.That(refreshed.Succeeded, Is.True, refreshed.Error);
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(Today.AddDays(3)));
        Assert.That(refreshed.Refresh!.PreviousStatusDate, Is.EqualTo(picked));
        Assert.That(refreshed.Refresh.StatusDate, Is.EqualTo(Today.AddDays(3)));
        Assert.That(refreshed.Refresh.ChangedTasks, Is.Empty, "A refresh reads dates; it does not move them.");
        Assert.That((await publisher.RefreshAsync(session, Today.AddDays(3))).Refresh!.ChangedTasks, Is.Empty);
    }

    [Test]
    public async Task LastChildOutdentReactivatesStoredAssigneeConflictBeforeAnyRemoteWrite()
    {
        await Start(2);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Assignees, new[] { "U1" })]), Today);
        await session.Execute(new IndentPlanRows(["I2"]), Today);
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i.Row.Identity == "I1" ? i with { Row = i.Row with { Assignees = ["U2"] } } : i).ToImmutableArray() });
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(session.Document.Sync.Conflicts.Single().Field, Is.EqualTo(PlanField.Assignees));
        Assert.That(PlanOperations.BlockingConflicts(session.Document), Is.Empty);
        await session.Execute(new IndentPlanRows(["I2"], true), Today);
        var before = FakePlanEditor.Load(root).MutationBatches;
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(PlanOperations.BlockingConflicts(session.Document).Single().Field, Is.EqualTo(PlanField.Assignees));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(before));
        Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I1").Row.Assignees, Is.EqualTo(new[] { "U2" }));
    }

    [Test]
    public async Task SuccessfulPublicationReportsStageWriteCountsThroughTheirTotals()
    {
        await Start(3);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I2", PlanField.Actual, 3m), new("I2", PlanField.Parent, "I1"), new("I3", PlanField.Predecessors, new[] { "I2" })]), Today);
        await session.Execute(new MovePlanRows(["I3"], "I1"), Today);
        await session.Execute(new InsertPlanRows([PlanRow.New("New task", "acme/repo")]), Today);
        var reports = new List<RemoteProgress>();
        var result = await publisher.PublishAsync(session, Today, progressReporter: new InlineProgress<RemoteProgress>(reports.Add));
        Assert.That(result.Succeeded, Is.True, result.Error);
        foreach (var stage in new[] { "新規 Issue", "フィールド・担当者", "親子関係", "先行タスク", "表示順" }) {
            var counts = reports.Where(p => p.Stage == stage && p.Total is not null).ToArray();
            Assert.That(counts, Is.Not.Empty, stage);
            Assert.That(counts.Select(p => p.Completed), Is.Ordered, stage);
            Assert.That(counts.Last().Completed, Is.EqualTo(counts.Last().Total), stage);
            Assert.That(counts.Last().Total, Is.GreaterThan(0), stage);
        }
    }

    [Test]
    public async Task FailedWritesDoNotReportTheStageAsFullySuccessful()
    {
        await Start(1);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Actual, 3m)]), Today);
        Scenario("field-denied");
        var reports = new List<RemoteProgress>();
        var result = await publisher.PublishAsync(session, Today, progressReporter: new InlineProgress<RemoteProgress>(reports.Add));
        Assert.That(result.Succeeded, Is.False);
        var last = reports.Last(p => p.Stage == "フィールド・担当者");
        Assert.That(last.Completed, Is.LessThan(last.Total!.Value));
    }

    [Test]
    public async Task ClosedIssuePublishesRemainingOnlyAndRefreshKeepsThePlanEnd()
    {
        await Start(1, state => state with { Issues = state.Issues.Select(i => i with {
            Row = i.Row with { Closed = true, CloseDate = Today, Remaining = 8, End = Today.AddDays(2) }
        }).ToImmutableArray() });
        // #308: a closed Issue publishes its calculated remaining work; its 終了日 is a plan value.
        Assert.That(session.Changes(Today).Fields["I1"], Is.EquivalentTo(new[] { PlanField.Remaining }));
        var published = await publisher.PublishAsync(session, Today);
        Assert.That(published.Succeeded, Is.True, published.Error);
        var row = FakePlanEditor.Load(root).Issues.Single().Row;
        Assert.That((row.Remaining, row.End), Is.EqualTo((0m, Today.AddDays(2))), "the close day is not written into 終了日");
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        var refreshed = await publisher.RefreshAsync(session, Today);
        Assert.That(refreshed.Succeeded, Is.True, refreshed.Error);
        var state = session.Document.State.Rows.Single();
        Assert.That((state.End, state.CloseDate), Is.EqualTo(((DateOnly?)Today.AddDays(2), (DateOnly?)Today)),
            "a refresh keeps the plan end and adopts the close record");
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }

    [Test]
    public async Task RedactedProjectMembershipDoesNotBlockRefreshOrPublishVerification()
    {
        await Start(2, state => state with { Redacted = 1, HiddenItems = 1 });
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Actual, 3m)]), Today);
        var refreshed = await publisher.RefreshAsync(session, Today);
        Assert.That(refreshed.Succeeded, Is.True, refreshed.Error);
        Assert.That(session.Document.Sync.InaccessibleCount, Is.EqualTo(2));
        Assert.That(session.Document.State.Rows, Has.Length.EqualTo(2));
        var published = await publisher.PublishAsync(session, Today);
        Assert.That(published.Succeeded, Is.True, published.Error);
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        Assert.That(session.Document.Sync.InaccessibleCount, Is.EqualTo(2));
    }

    [Test]
    public async Task RefreshReadsPagedNativeSiblingOrderWithoutCountingTheGitHubSideDifference()
    {
        await Start(103, state => state with {
            Issues = state.Issues.Select((i, n) => n == 0 ? i : i with { Row = i.Row with { Parent = "I1" } }).ToImmutableArray(),
            SubOrders = state.SubOrders.Add("I1", Enumerable.Range(2, 102).Reverse().Select(n => "I" + n).ToImmutableArray()) });
        Assert.That(session.Document.Sync.NativeOrders["I1"], Is.EqualTo(Enumerable.Range(2, 102).Reverse().Select(n => "I" + n)));
        // Issue #167: the Project item order and the native order differ, but the PMO changed nothing.
        Assert.That(session.Changes(Today).Fields.Any(f => f.Value.Contains(PlanField.SubIssueOrder)), Is.False);
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        var result = await publisher.RefreshAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(session.Document.Sync.NativeOrders["I1"], Has.Length.EqualTo(102));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }

    [Test]
    public async Task TheReadCarriesIssueUpdatePermissionWithoutMakingItAPlanChange()
    {
        await Start(2);
        Assert.That(session.Document.Sync.CannotUpdate, Is.Empty, "the fixture grants permission by default");
        Scenario("no-update-permission");
        var result = await publisher.RefreshAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(session.Document.Sync.CannotUpdate, Is.EqualTo(new[] { "I1" }));
        // A permission observation is not a plan value: it is not unpublished work and cannot be published.
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero, "nothing about a permission observation is written");
        // The observation follows the read, so restoring permission forgets the denial.
        Scenario();
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(session.Document.Sync.CannotUpdate, Is.Empty);
    }

    [Test]
    public async Task PublishingWithoutMovingRowsSendsNoSiblingReorder()
    {
        await Start(3, state => state with {
            Issues = state.Issues.Select(i => i.Row.Identity == "I1" ? i : i with { Row = i.Row with { Parent = "I1" } }).ToImmutableArray(),
            SubOrders = state.SubOrders.Add("I1", ["I3", "I2"])
        });
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Title, "Renamed")]), Today);
        Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(1), "Only the edited task is unpublished.");
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).SubOrders["I1"], Is.EqualTo(new[] { "I3", "I2" }));
        Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Does.Not.Contain("reprioritizeSubIssue"));
    }

    [Test]
    public async Task FailedSiblingReorderIsRetriedByTheNextPublish()
    {
        await Start(3, state => state with {
            Issues = state.Issues.Select(i => i.Row.Identity == "I1" ? i : i with { Row = i.Row with { Parent = "I1" } }).ToImmutableArray(),
            SubOrders = state.SubOrders.Add("I1", ["I2", "I3"])
        });
        await session.Execute(new MovePlanRows(["I3"], "I2"), Today);
        Scenario("sibling-denied");
        var failed = await publisher.PublishAsync(session, Today);
        Assert.That(failed.Succeeded, Is.False);
        Assert.That(FakePlanEditor.Load(root).SubOrders["I1"], Is.EqualTo(new[] { "I2", "I3" }));
        Assert.That(session.Document.Sync.Failures.Any(f => f.Field == PlanField.SubIssueOrder), Is.True);
        Scenario();
        var retried = await publisher.PublishAsync(session, Today);
        Assert.That(retried.Succeeded, Is.True, retried.Error);
        Assert.That(FakePlanEditor.Load(root).SubOrders["I1"], Is.EqualTo(new[] { "I3", "I2" }));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }

    [TestCase("creation"), TestCase("reset-undo")]
    public async Task FailedSiblingRetrySurvivesCreationOrResetUndo(string intervening)
    {
        await Start(3, state => state with {
            Issues = state.Issues.Select(i => i.Row.Identity == "I1" ? i : i with { Row = i.Row with { Parent = "I1" } }).ToImmutableArray(),
            SubOrders = state.SubOrders.Add("I1", ["I2", "I3"])
        });
        await session.Execute(new MovePlanRows(["I3"], "I2"), Today);
        Scenario("sibling-denied");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(session.Document.Baseline.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "I1", "I3", "I2" }));
        Assert.That(session.Document.Sync.Failures.Any(f => f.Field == PlanField.SubIssueOrder), Is.True);
        var mutationsBefore = File.ReadAllLines(Path.Combine(root, "plan-mutations.jsonl")).Length;
        Scenario();
        if (intervening == "creation")
            await session.Execute(new InsertPlanRows([PlanRow.New("Added task", "acme/repo")]), Today);
        else
        {
            Assert.That((await publisher.ResetAsync(session, Today)).Succeeded, Is.True);
            Assert.That(session.Changes(Today).TaskCount, Is.Zero);
            await Reopen();
            Assert.That((await session.Undo(Today)).Succeeded, Is.True);
            Assert.That(session.Changes(Today).Fields.GetValueOrDefault("I1"), Does.Contain(PlanField.SubIssueOrder));
            Assert.That(File.ReadAllLines(Path.Combine(root, "plan-mutations.jsonl")), Has.Length.EqualTo(mutationsBefore));
        }
        await Reopen();
        var published = await publisher.PublishAsync(session, Today);
        Assert.That(published.Succeeded, Is.True, published.Error);
        Assert.That(FakePlanEditor.Load(root).SubOrders["I1"], Is.EqualTo(new[] { "I3", "I2" }));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        Assert.That(session.Document.Sync.Failures, Is.Empty);
        var sent = File.ReadAllLines(Path.Combine(root, "plan-mutations.jsonl")).Skip(mutationsBefore)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.That(sent.Count(r => r.GetProperty("query").GetString()!.Contains("reprioritizeSubIssue")), Is.EqualTo(1));
        Assert.That(sent.Count(r => r.GetProperty("query").GetString()!.Contains("createIssue")), Is.EqualTo(intervening == "creation" ? 1 : 0));
        var positions = sent.Where(r => r.GetProperty("query").GetString()!.Contains("updateProjectV2ItemPosition"))
            .SelectMany(r => r.GetProperty("variables").EnumerateObject().Select(v => v.Value.GetProperty("itemId").GetString())).ToArray();
        Assert.That(positions, Does.Not.Contain("T-I1").And.Not.Contain("T-I2").And.Not.Contain("T-I3"));
        var completedMutations = File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl"));
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Is.EqualTo(completedMutations));
    }

    [Test]
    public async Task FailedSiblingRetryConflictsWithANewNativeOrderWithoutSendingWrites()
    {
        await Start(4, state => state with {
            Issues = state.Issues.Select(i => i.Row.Identity == "I1" ? i : i with { Row = i.Row with { Parent = "I1" } }).ToImmutableArray(),
            SubOrders = state.SubOrders.Add("I1", ["I2", "I3", "I4"])
        });
        await session.Execute(new MovePlanRows(["I3"], "I2"), Today);
        Scenario("sibling-denied");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(session.Document.Baseline.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "I1", "I3", "I2", "I4" }));
        Assert.That(session.Document.Sync.Failures.Any(f => f.Field == PlanField.SubIssueOrder), Is.True);
        Scenario();
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { SubOrders = remote.SubOrders.SetItem("I1", ["I4", "I2", "I3"]) });
        var mutations = File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl"));
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(session.Document.Sync.Conflicts.Any(c => c.Identity == "I1" && c.Field == PlanField.SubIssueOrder), Is.True);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Is.EqualTo(mutations));
        Assert.That(FakePlanEditor.Load(root).SubOrders["I1"], Is.EqualTo(new[] { "I4", "I2", "I3" }));
    }

    [Test]
    public async Task ChoosingLocalSiblingOrderPublishesAfterProjectOrderConverges()
    {
        await Start(4, state => state with {
            Issues = state.Issues.Select(i => i.Row.Identity == "I1" ? i : i with { Row = i.Row with { Parent = "I1" } }).ToImmutableArray(),
            SubOrders = state.SubOrders.Add("I1", ["I2", "I3", "I4"])
        });
        await session.Execute(new MovePlanRows(["I3"], "I2"), Today);
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with {
            Issues = new[] { "I1", "I3", "I2", "I4" }.Select(id => remote.Issues.Single(i => i.Row.Identity == id)).ToImmutableArray(),
            SubOrders = remote.SubOrders.SetItem("I1", ["I4", "I2", "I3"])
        });
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(session.Document.Sync.Conflicts.Single().Field, Is.EqualTo(PlanField.SubIssueOrder));
        Assert.That((await session.ResolveConflict("I1", PlanField.SubIssueOrder, false, Today)).Succeeded, Is.True);
        await Reopen();
        Assert.That(session.Document.Sync.Conflicts, Is.Empty);
        Assert.That(session.Changes(Today).Fields.GetValueOrDefault("I1"), Does.Contain(PlanField.SubIssueOrder));
        await session.Undo(Today);
        Assert.That(session.Document.Sync.Conflicts.Single().Field, Is.EqualTo(PlanField.SubIssueOrder));
        await session.Redo(Today);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        Assert.That(FakePlanEditor.Load(root).SubOrders["I1"], Is.EqualTo(new[] { "I3", "I2", "I4" }));
        var mutations = File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl"));
        Assert.That(mutations, Does.Contain("reprioritizeSubIssue"));
        Assert.That(mutations, Does.Not.Contain("updateProjectV2ItemPosition"));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Is.EqualTo(mutations));
    }

    [Test]
    public async Task ReversingSiblingOrderPreservesRequestedOrderUnderPartialResourceLimits()
    {
        await Start(5, state => state with
        {
            Issues = state.Issues.Select(i => i.Row.Identity == "I1" ? i : i with { Row = i.Row with { Parent = "I1" } }).ToImmutableArray(),
            SubOrders = state.SubOrders.Add("I1", ["I5", "I4", "I3", "I2"])
        });
        await session.Execute(new MovePlanRows(["I2"], "I5"), Today);
        Scenario("sibling-resource");
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).SubOrders["I1"], Is.EqualTo(new[] { "I3", "I4", "I2", "I5" }));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }

    [Test]
    public async Task ResourceLimitedUncertainCreationUsesGuardBeforeSplitting()
    {
        await Start(0);
        await session.Execute(new InsertPlanRows(Enumerable.Range(0, 4).Select(i => PlanRow.New("Created " + i, "acme/repo")).ToImmutableArray()), Today);
        Scenario("creation-resource-uncertain");
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var issues = FakePlanEditor.Load(root).Issues;
        Assert.That(issues.Length, Is.EqualTo(4));
        Assert.That(issues.Select(i => i.Row.Title).Distinct().Count(), Is.EqualTo(4));
        Assert.That(issues.All(i => i.Added), Is.True);
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }
    [Test]
    // These assertions measure request scheduling (four in flight, fault stops dispatch), which the rest of
    // the suite competing for the runner's cores can perturb; the same flake failed main #299.
    [NonParallelizable]
    public async Task FieldRequestsRunFourAtATime()
    {
        await Start(65);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, session.Document.State.Rows
            .Select(r => new PlanCellChange(r.Identity, PlanField.Actual, 3m)).ToImmutableArray()), Today);
        // Hold the first response so all four slots are dispatched before any response lands; a loaded CI host
        // can otherwise complete a 50 ms request while the fourth is still being prepared.
        Scenario(mutationDelayMs: 50, firstMutationDelayMs: 2000);
        var reports = new List<RemoteProgress>();
        var result = await publisher.PublishAsync(session, Today, progressReporter: new InlineProgress<RemoteProgress>(reports.Add));
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.All(i => i.Row.Actual == 3), Is.True);
        var counts = reports.Where(p => p.Stage == "フィールド・担当者" && p.Total is not null).ToArray();
        Assert.That(counts.Select(p => p.Completed), Is.Ordered);
        Assert.That(counts.Last().Completed, Is.EqualTo(65));
        Assert.That(counts.Last().Total, Is.EqualTo(65));
        Assert.That(runner.MaximumConcurrentMutations, Is.EqualTo(4));
        Assert.That(publisher.EffectiveBatchSizes, Is.EqualTo(new[] { 10, 10, 10, 10, 10, 10, 5 }));
    }

    [Test]
    public async Task FieldRequestsStayFourInFlightAndStartTheNextOnCompletion()
    {
        await Start(65);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, session.Document.State.Rows
            .Select(r => new PlanCellChange(r.Identity, PlanField.Actual, 3m)).ToImmutableArray()), Today);
        Scenario();
        var started = Enumerable.Range(0, 7).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var responses = Enumerable.Range(0, 7).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var nextRequest = 0;
        runner.MutationStarting = () => started[nextRequest++].TrySetResult();
        runner.BeforeMutationResponse = (index, token) => responses[index].Task.WaitAsync(token);

        var publishing = publisher.PublishAsync(session, Today);
        try
        {
            await Task.WhenAll(started.Take(4).Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(30));
            for (var index = 0; index < 3; index++)
            {
                // Only this response is released; the publisher must refill before any peer returns.
                responses[index].TrySetResult();
                await started[index + 4].Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        finally
        {
            foreach (var response in responses) response.TrySetResult();
            await publishing.WaitAsync(TimeSpan.FromSeconds(30));
            runner.BeforeMutationResponse = null;
            runner.MutationStarting = null;
        }

        var result = await publishing;
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.All(i => i.Row.Actual == 3), Is.True);
        Assert.That(runner.MaximumConcurrentMutations, Is.EqualTo(4), "The field phase never exceeds four requests in flight.");
        Assert.That(runner.MutationConcurrency, Is.EqualTo(new[] { 1, 2, 3, 4, 4, 4, 4 }),
            "Each released response starts one new request while the other three remain outstanding.");
        Assert.That(publisher.EffectiveBatchSizes, Is.EqualTo(new[] { 10, 10, 10, 10, 10, 10, 5 }));
    }

    [TestCase("field-pushback")]
    [TestCase("field-rate-rejected")]
    [TestCase("field-rate-no-delay")]
    // These assertions measure request scheduling (four in flight, fault stops dispatch), which the rest of
    // the suite competing for the runner's cores can perturb; the same flake failed main #299.
    [NonParallelizable]
    public async Task FieldPushbackMakesRemainingRequestsSerial(string fault)
    {
        await Start(65);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, session.Document.State.Rows
            .Select(r => new PlanCellChange(r.Identity, PlanField.Actual, 3m)).ToImmutableArray()), Today);
        Scenario(fault, mutationDelayMs: 50, firstMutationDelayMs: 2000);
        var deadlines = new List<DateTimeOffset?>();
        runner.MutationStarting = () => deadlines.Add(session.Document.Sync.NotBefore);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.EqualTo(fault == "field-pushback"), result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Count(i => i.Row.Actual == 3), Is.EqualTo(fault == "field-pushback" ? 65 : 25));
        Assert.That(runner.MaximumConcurrentMutations, Is.EqualTo(4));
        Assert.That(runner.MutationConcurrency.Skip(4), Is.EqualTo(new[] { 1, 1, 1 }));
        if (fault != "field-rate-no-delay")
        {
            Assert.That(deadlines.Skip(4).All(d => d is not null), Is.True);
            Assert.That(runner.MutationStarted.Skip(4).Zip(deadlines.Skip(4)).All(p => p.First >= p.Second), Is.True);
        }
        Assert.That(publisher.EffectiveBatchSizes, Is.EqualTo(new[] { 10, 10, 10, 10, 10, 10, 5 }));
        // The ramp assertion needs the four slots to fill before the first response; holding that response
        // keeps the exact 1,2,3,4 sequence independent of how fast a host prepares and starts the requests.
        // Peers queue behind the held response, so its length only bounds the dispatch time: 2 s was not
        // enough on a hosted runner (fourth request saw three in flight, PR #317), so allow 6 s.
        Scenario(mutationDelayMs: 300, firstMutationDelayMs: 6000);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, session.Document.State.Rows
            .Select(r => new PlanCellChange(r.Identity, PlanField.Actual, 4m)).ToImmutableArray()), Today);
        runner.MutationConcurrency.Clear();
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        Assert.That(runner.MutationConcurrency.Take(4), Is.EqualTo(new[] { 1, 2, 3, 4 }),
            "Serial fallback must not survive another explicit publish.");
    }

    [Test]
    // These assertions measure request scheduling (four in flight, fault stops dispatch), which the rest of
    // the suite competing for the runner's cores can perturb; the same flake failed main #299.
    [NonParallelizable]
    public async Task UnknownWindowPersistsTheLongestRetryDeadlineAndAllSiblingOutcomes()
    {
        await Start(65);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, session.Document.State.Rows
            .Select(r => new PlanCellChange(r.Identity, PlanField.Actual, 3m)).ToImmutableArray()), Today);
        Scenario("window-retry-unknown", mutationDelayMs: 50);
        var started = DateTimeOffset.UtcNow;
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Error, Does.Contain("GitHubの応答を確認できないため発行を中断しました。"));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(4));
        Assert.That(session.Document.Sync.Publish!.Writes.Count(w => w.State == PlanWriteState.Dispatched), Is.EqualTo(40));
        Assert.That(session.Document.Sync.Publish.Writes.Count(w => w.State == PlanWriteState.Pending), Is.EqualTo(25));
        var deadline = session.Document.Sync.NotBefore;
        Assert.That(deadline, Is.GreaterThanOrEqualTo(started.AddSeconds(4)));
        Assert.That(deadline, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(4)));
        await Reopen();
        Assert.That(session.Document.Sync.NotBefore, Is.EqualTo(deadline));
    }

    [TestCase("gateway")]
    [TestCase("gateway-json")]
    // These assertions measure request scheduling (four in flight, fault stops dispatch), which the rest of
    // the suite competing for the runner's cores can perturb; the same flake failed main #299.
    [NonParallelizable]
    public async Task FieldGatewayFailureSplitsAndRetainsTheReducedCeiling(string body)
    {
        await Start(65);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, session.Document.State.Rows
            .Select(r => new PlanCellChange(r.Identity, PlanField.Actual, 3m)).ToImmutableArray()), Today);
        // Hold the first response until all four slots are dispatched: the gateway error is the contract, and a
        // 50 ms delay let a loaded runner answer it before the fourth request existed.
        Scenario(body, gatewayAliasLimit: 6, mutationDelayMs: 300, firstMutationDelayMs: 2000);

        var result = await publisher.PublishAsync(session, Today);

        Assert.That(result.Succeeded, Is.True, result.Error);
        // All four initial requests split; remaining requests retain the ceiling of five.
        Assert.That(publisher.EffectiveBatchSizes, Is.EqualTo(new[] { 10, 10, 10, 10, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 }));
        var remote = FakePlanEditor.Load(root);
        Assert.That(remote.Issues.All(i => i.Row.Actual == 3), Is.True);
        Assert.That(remote.MutationBatches, Is.EqualTo(17));
        Assert.That(runner.MaximumConcurrentMutations, Is.EqualTo(4));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        Assert.That(session.Document.Sync.Publish?.Writes.Any(w => w.State == PlanWriteState.Dispatched) ?? false, Is.False);
    }

    [Test]
    public async Task SingleFieldGatewayFailureStopsWithTheWriteDispatched()
    {
        await Start(1);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Actual, 3m)]), Today);
        Scenario("gateway");

        var result = await publisher.PublishAsync(session, Today);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Does.Contain("GitHubの応答を確認できないため発行を中断しました"));
        Assert.That(session.Document.Sync.Publish!.Writes.Single().State, Is.EqualTo(PlanWriteState.Dispatched));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(1));
        Assert.That(publisher.EffectiveBatchSizes, Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public async Task CreationGatewayFailureStopsWithoutResendingTheBatch()
    {
        await Start(0);
        await session.Execute(new InsertPlanRows(Enumerable.Range(1, 2)
            .Select(i => PlanRow.New("Created " + i, "acme/repo")).ToImmutableArray()), Today);
        Scenario("gateway");

        var result = await publisher.PublishAsync(session, Today);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Does.Contain("GitHubの応答を確認できないため発行を中断しました"));
        Assert.That(session.Document.Sync.Publish!.Writes.Where(w => w.Stage == PlanPublishStage.Create)
            .All(w => w.State == PlanWriteState.Dispatched), Is.True);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(1));
        Assert.That(FakePlanEditor.Load(root).Issues, Has.Length.EqualTo(2));
        Assert.That(publisher.EffectiveBatchSizes, Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public async Task MixedResourceBatchSplitsAndFinishesWithoutResendingSuccesses()
    {
        await Start(8);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, session.Document.State.Rows.SelectMany(r => new[] {
            new PlanCellChange(r.Identity, PlanField.Actual, 3m), new PlanCellChange(r.Identity, PlanField.Status, "Done") }).ToImmutableArray()), Today);
        Scenario("mixed-resource");
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.All(i => i.Row.Actual == 3 && i.Row.Status == "Done"), Is.True);
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        var inputs = File.ReadLines(Path.Combine(root, "plan-mutations.jsonl")).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("variables").EnumerateObject().Select(v => v.Value.GetRawText()).ToArray()).ToArray();
        Assert.That(inputs.Skip(1).SelectMany(x => x), Does.Not.Contain(inputs[0][0]), "The successful alias from the rejected mixed batch must not be resent.");
    }
    [TestCase("field-denied")]
    [TestCase("resource-always")]
    public async Task FieldFailureDoesNotPreventIndependentRelationshipsAndOrder(string fault)
    {
        await Start(3);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I2", PlanField.Actual, 3m), new("I2", PlanField.Parent, "I1"), new("I3", PlanField.Predecessors, new[] { "I2" })]), Today);
        Scenario(fault);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.False);
        var rows = FakePlanEditor.Load(root).Issues.Select(i => i.Row).ToArray();
        Assert.That(rows.Single(r => r.Identity == "I2").Parent, Is.EqualTo("I1"));
        Assert.That(rows.Single(r => r.Identity == "I3").Predecessors, Is.EqualTo(new[] { "I2" }));
        Assert.That(session.Document.Sync.Failures.All(f => f.Field is not (PlanField.Parent or PlanField.Predecessors or PlanField.SubIssueOrder)), Is.True);
    }
    [TestCase("parent-denied")]
    [TestCase("link-already-exists")]
    public async Task RelationshipOutcomesReconcileAndOnlyDependentPositionsWait(string fault)
    {
        await Start(4);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I2", PlanField.Parent, "I1"), new("I3", PlanField.Parent, "I1"),
            new("I4", PlanField.Predecessors, new[] { "I3" })]), Today);
        Scenario(fault);
        var result = await publisher.PublishAsync(session, Today);
        var rows = FakePlanEditor.Load(root).Issues.Select(i => i.Row).ToArray();
        Assert.That(rows.Single(r => r.Identity == "I3").Parent, Is.EqualTo("I1"));
        Assert.That(rows.Single(r => r.Identity == "I4").Predecessors, Is.EqualTo(new[] { "I3" }));
        if (fault == "parent-denied")
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(rows.Single(r => r.Identity == "I2").Parent, Is.Null);
            Assert.That(session.Document.Sync.Failures.Where(f => f.Field == PlanField.SubIssueOrder).All(f => f.Reason == "NotDispatched"), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Does.Not.Contain("reprioritizeSubIssue"));
            Scenario();
            Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        }
        else
        {
            Assert.That(result.Succeeded, Is.True, result.Error);
            Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        }
    }
    [Test]
    public async Task NewItemAdoptsWorkflowStatusWithoutClearingIt()
    {
        await Start(0);
        await session.Execute(new InsertPlanRows([PlanRow.New("Workflow default", "acme/repo") with { Estimate = 8 }]), Today);
        Scenario("workflow-status");
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(session.Document.State.Rows.Single().Status, Is.EqualTo("In progress"));
        Assert.That(FakePlanEditor.Load(root).Issues.Single().Row.Status, Is.EqualTo("In progress"));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Does.Not.Contain("clearProjectV2ItemFieldValue"));
    }
    [TestCase("workflow-backlog")]
    [TestCase("mixed-resource")]
    public async Task FreshTwentyFourIssuePlanRecordsSerialThroughputAndKeepsWorkflowDefaults(string fault)
    {
        using var stream = typeof(PlanPublisherTests).Assembly.GetManifestResourceStream("GhProjectsBoards.Tests.Fixtures.Phase9Snapshot2.json")!;
        using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer);
        // Read through the production upgrade path: the captured fixture still holds the per-Project mark switches
        // that #251 moved to this PC's display preferences.
        var document = PlanJson.Read<PlanDocument>(buffer.ToArray());
        var imported = document.State.Rows.Where(r => r.CsvSourceHash is not null).ToArray();
        var ids = imported.ToDictionary(r => r.Identity, r => PlanRow.New(r.Title).Identity);
        var rows = imported.Select(r => r with { Identity = ids[r.Identity], Parent = r.Parent is null ? null : ids[r.Parent],
            Predecessors = r.Predecessors.Select(id => ids[id]).ToImmutableArray() }).ToImmutableArray();
        await Start(0, settings: document.State.Settings);
        await session.Execute(new InsertPlanRows(rows, Kind: PlanOperationKind.CsvImport), Today);
        Scenario(fault);
        var before = runner.Count;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await publisher.PublishAsync(session, Today);
        watch.Stop();
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        var remote = FakePlanEditor.Load(root).Issues;
        Assert.That(remote.Length, Is.EqualTo(24));
        Assert.That(remote.Count(i => i.Row.Parent is not null), Is.EqualTo(20));
        Assert.That(remote.Sum(i => i.Row.Predecessors.Length), Is.EqualTo(19));
        Assert.That(remote.All(i => i.Added && i.Row.Status == "Backlog"), Is.True);
        Assert.That(runner.MaximumConcurrentNonFieldMutations, Is.EqualTo(1));
        TestContext.Out.WriteLine($"Fresh24 {fault}: requests={runner.Count - before}; elapsed={watch.Elapsed.TotalSeconds:F3}s; creation wait={publisher.CreationWait.TotalSeconds:F3}s; max concurrent mutations={runner.MaximumConcurrentMutations}; batches={string.Join(',', publisher.EffectiveBatchSizes)}");
    }
    [Test]
    public async Task Snapshot2RecoveryRestoresRelationshipsAndFinishesWithoutCreation()
    {
        using var stream = typeof(PlanPublisherTests).Assembly.GetManifestResourceStream("GhProjectsBoards.Tests.Fixtures.Phase9Snapshot2.json")!;
        using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer);
        // Read through the production upgrade path: the captured fixture still holds the per-Project mark switches
        // that #251 moved to this PC's display preferences.
        var document = PlanJson.Read<PlanDocument>(buffer.ToArray());
        var today = new DateOnly(2026, 10, 7);
        // The captured fixture predates closedAt; this is a synthetic observation for the fake endpoint.
        PlanRow WithCloseDate(PlanRow row) => row.Closed ? row with { CloseDate = today } : row;
        document = document with { Baseline = document.Baseline with { Rows = document.Baseline.Rows.Select(WithCloseDate).ToImmutableArray() },
            State = document.State with { Rows = document.State.Rows.Select(WithCloseDate).ToImmutableArray() } };
        Scenario("mixed-resource");
        FakePlanEditor.Save(root, new(document.Baseline.Rows.Select(r => new PlanFakeIssue(r, "", true)).ToImmutableArray(), 30));
        runner = new(new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }));
        publisher = new(new GhConnectionService(GhProcessTests.FakeExecutable, "github.com", runner), new("github.com", 42, "fixture-user", GhProcessTests.FakeExecutable));
        session = await PlanSession.CreateAsync(new(root), document, today);
        // The old checkpoint has no creation provenance. This is the explicit PMO recovery correction,
        // justified by the original CSV hash, whose schema has no Status column.
        var scheduled = session.Schedule(today).ToDictionary(r => r.Input.Identity);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, document.State.Rows.Where(r => r.CsvSourceHash is not null && !scheduled[r.Identity].IsSummary)
            .Select(r => new PlanCellChange(r.Identity, PlanField.Status, "Backlog")).ToImmutableArray()), today);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await publisher.PublishAsync(session, today);
        watch.Stop();
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(session.Changes(today).TaskCount, Is.Zero);
        var remote = FakePlanEditor.Load(root).Issues;
        Assert.That(remote.Length, Is.EqualTo(29));
        foreach (var expected in document.State.Rows.Where(r => r.CsvSourceHash is not null))
        {
            var actual = remote.Single(i => i.Row.Identity == expected.Identity).Row;
            Assert.That(actual.Parent, Is.EqualTo(expected.Parent));
            Assert.That(actual.Predecessors, Is.EquivalentTo(expected.Predecessors));
            Assert.That(actual.Assignees, Is.EquivalentTo(expected.Assignees));
            Assert.That(actual.Status, Is.EqualTo(scheduled[expected.Identity].IsSummary
                ? document.Baseline.Rows.Single(r => r.Identity == expected.Identity).Status : "Backlog"));
            Assert.That((actual.Start, actual.End), Is.EqualTo((scheduled[expected.Identity].Start.Value, scheduled[expected.Identity].End.Value)));
            if (!scheduled[expected.Identity].IsSummary)
                Assert.That((actual.Estimate, actual.Remaining, actual.Actual), Is.EqualTo((expected.Estimate, expected.Remaining, expected.Actual)));
        }
        Assert.That(remote.Select(i => i.Row.Identity), Is.EqualTo(document.State.Rows.Select(r => r.Identity)));
        foreach (var parent in document.State.Rows.Where(r => r.Parent is not null).Select(r => r.Parent!).Distinct())
            Assert.That(FakePlanEditor.Load(root).SubOrders[parent], Is.EqualTo(document.State.Rows.Where(r => r.Parent == parent).Select(r => r.Identity)));
        Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Does.Not.Contain("createIssue").And.Not.Contain("clearProjectV2ItemFieldValue"));
        TestContext.Out.WriteLine($"Snapshot2 recovery: requests={runner.Count}; elapsed={watch.Elapsed.TotalSeconds:F3}s; batch sizes={string.Join(',', publisher.EffectiveBatchSizes)}");
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task CreatedIssuesWithPartialMembershipFinishWithoutDuplicates(bool reopen)
    {
        await Start(reopen ? 5 : 0);
        var parent = PlanRow.New("Parent", "acme/repo");
        var child = PlanRow.New("Child", "acme/repo") with { Parent = parent.Identity, Estimate = 16, Assignees = ["U1"] };
        var next = PlanRow.New("Next", "acme/repo") with { Estimate = 8, Predecessors = [child.Identity] };
        var rows = new List<PlanRow> { parent, child, next };
        for (var i = 3; i < (reopen ? 24 : 12); i++)
                rows.Add(PlanRow.New("Task " + (i + 100), "acme/repo") with { Parent = parent.Identity, Predecessors = [rows[^1].Identity],
                    Estimate = 16, Remaining = 8, Actual = 2, Assignees = ["U1"], Start = Today.AddDays(2) });
        await session.Execute(new InsertPlanRows(rows.ToImmutableArray(), Kind: PlanOperationKind.CsvImport), Today);
        var dates = session.Schedule(Today).ToDictionary(r => r.Input.Identity, r => (r.Start.Value, r.End.Value));
        if (reopen)
        {
            var remote = await publisher.RefreshAsync(session, Today);
            Assert.That(remote.Succeeded, Is.True);
            var service = new GhConnectionService(GhProcessTests.FakeExecutable, "github.com", runner);
            var context = new ConnectionContext("github.com", 42, "fixture-user", GhProcessTests.FakeExecutable);
            using var lease = await service.BeginOperationAsync(context, default, mutation: false);
            var snapshot = PlanSnapshot.From(await PlanSnapshot.ReadConsistentAsync(service, lease, context, Project, default), session.Document.State.Settings);
            var run = Guid.NewGuid().ToString("N");
            var writes = PlanPublishPlan.Build(session.Document, snapshot, Today, run).Writes.Where(w => w.Stage is PlanPublishStage.Create or PlanPublishStage.Add).ToImmutableArray();
            var ids = rows.Select((r, i) => (r.Identity, Id: "I" + (i + 6))).ToDictionary(p => p.Identity, p => p.Id);
            var created = rows.Select((r, i) => new PlanFakeIssue(new(ids[r.Identity], r.Title, r.Repository),
                JsonSerializer.Deserialize<Dictionary<string, string>>(writes.Single(w => w.Identity == r.Identity && w.Stage == PlanPublishStage.Create).Input)!["body"], i < 17)).ToImmutableArray();
            FakePlanEditor.Save(root, new(FakePlanEditor.Load(root).Issues.AddRange(created), 30));
            Assert.That(created.Count(i => i.Added), Is.EqualTo(17));
            Assert.That(created.Count(i => !i.Added), Is.EqualTo(7));
            await session.SaveSync(session.Document.Sync with {
                Failures = rows.Select(r => new PlanPublishFailure(r.Identity, PlanField.NewTask, "VerificationMismatch")).ToImmutableArray(),
                Publish = new(run, writes.Select(w => w.Stage == PlanPublishStage.Create
                    ? w with { State = PlanWriteState.Succeeded, ResultId = ids[w.Identity] }
                    : w with { State = PlanWriteState.Failed, Error = "VerificationMismatch", ResultId = "T-" + ids[w.Identity] }).ToImmutableArray()) });
            session = (await PlanSession.OpenAsync(new(root), Project, Today)).Session!;
        }
        else Scenario("membership-delay");
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var issues = FakePlanEditor.Load(root).Issues;
        Assert.That(issues, Has.Length.EqualTo(reopen ? 29 : 12));
        Assert.That(issues.All(i => i.Added), Is.True);
        Assert.That(issues.Single(i => i.Row.Title == "Child").Row.Estimate, Is.EqualTo(16));
        Assert.That(issues.Single(i => i.Row.Title == "Child").Row.Assignees, Is.EqualTo(new[] { "U1" }));
        string RemoteId(string local) => issues.Single(i => i.Row.Title == rows.Single(r => r.Identity == local).Title).Row.Identity;
        foreach (var row in rows)
        {
            var observed = issues.Single(i => i.Row.Title == row.Title).Row;
            Assert.That(observed.Assignees, Is.EqualTo(row.Assignees));
            Assert.That(observed.Parent, Is.EqualTo(row.Parent is null ? null : RemoteId(row.Parent)));
            Assert.That(observed.Predecessors, Is.EquivalentTo(row.Predecessors.Select(RemoteId)));
            Assert.That((observed.Start, observed.End), Is.EqualTo(dates[row.Identity]));
            if (row == parent) continue;
            Assert.That((observed.Estimate, observed.Remaining, observed.Actual),
                Is.EqualTo((row.Estimate, row.Remaining, row.Actual)));
        }
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        if (reopen) Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Does.Not.Contain("createIssue"));
        else
        {
            var explicitlyAdded = File.ReadLines(Path.Combine(root, "plan-mutations.jsonl")).SelectMany(line => {
                using var payload = JsonDocument.Parse(line);
                return payload.RootElement.GetProperty("variables").EnumerateObject()
                    .Where(v => v.Value.TryGetProperty("contentId", out _)).Select(v => v.Value.GetProperty("contentId").GetString()).ToArray();
            }).Distinct().ToArray();
            Assert.That(explicitlyAdded, Is.EquivalentTo(issues.Select(i => i.Row.Identity)), "Auto-added Issues also receive an explicit Project-add request.");
        }
    }
    [Test]
    public async Task UnconfirmedMembershipKeepsCreatedIdentityAndNextPublishFinishesAllAdds()
    {
        await Start(0);
        await session.Execute(new InsertPlanRows(Enumerable.Range(1, 12).Select(i => PlanRow.New("New " + i, "acme/repo") with { Estimate = 8 }).ToImmutableArray()), Today);
        Scenario("membership-pending");
        var first = await publisher.PublishAsync(session, Today);
        Assert.That(first.Succeeded, Is.False);
        var observed = FakePlanEditor.Load(root);
        Assert.That(observed.Issues, Has.Length.EqualTo(12));
        Assert.That(observed.Issues.Count(i => i.Added), Is.EqualTo(11));
        Assert.That(session.Document.Sync.Publish!.Writes.Single(w => w.Stage == PlanPublishStage.Create && w.ResultId == "I2").State, Is.EqualTo(PlanWriteState.Succeeded));
        Assert.That(session.Document.Sync.Failures.Single().Reason, Is.EqualTo("VerificationMismatch"));
        Scenario();
        await Reopen();
        var resumed = await publisher.PublishAsync(session, Today);
        Assert.That(resumed.Succeeded, Is.True, resumed.Error);
        var finished = FakePlanEditor.Load(root).Issues;
        Assert.That(finished, Has.Length.EqualTo(12));
        Assert.That(finished.All(i => i.Added && i.Row.Estimate == 8), Is.True);
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }
    [Test]
    public async Task ImportedHundredRowsPublishRelationshipsInStageOrderAndRetainDuplicateIdentity()
    {
        await Start(0);
        var file = PlanCsvImportTests.Read(PlanCsvImportTests.HundredRows());
        var preview = PlanCsvImport.Prepare(session.Document, file, PlanCsvImportTests.Catalog, Today);
        Assert.That(preview.Errors, Is.Empty);
        await session.Execute(preview.Command!, Today);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var remote = FakePlanEditor.Load(root).Issues;
        Assert.That(remote.Length, Is.EqualTo(100));
        Assert.That(remote.Count(i => i.Row.Parent is not null), Is.EqualTo(90));
        Assert.That(remote.Sum(i => i.Row.Predecessors.Length), Is.EqualTo(80));
        foreach (var group in Enumerable.Range(0, 10))
        {
            var parent = remote.Single(i => i.Row.Title == $"Group {group}").Row.Identity;
            Assert.That(FakePlanEditor.Load(root).SubOrders[parent], Is.EqualTo(Enumerable.Range(1, 9).Select(i => remote.Single(r => r.Row.Title == $"Task {group}-{i}").Row.Identity)));
            for (var child = 1; child <= 9; child++)
            {
                var row = remote.Single(i => i.Row.Title == $"Task {group}-{child}").Row;
                Assert.That(row.Parent, Is.EqualTo(parent));
                Assert.That(row.Predecessors, Is.EqualTo(child == 1 ? Array.Empty<string>() : new[] { remote.Single(i => i.Row.Title == $"Task {group}-{child - 1}").Row.Identity }));
            }
        }
        var sent = File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl"));
        Assert.That(sent.IndexOf("addSubIssue", StringComparison.Ordinal), Is.LessThan(sent.IndexOf("addBlockedBy", StringComparison.Ordinal)));
        Assert.That(remote.Select(i => i.Row.Title), Is.EqualTo(preview.Command!.Rows.Select(r => r.Title)), "Project order is already correct after ordered creation.");
        Assert.That(sent.LastIndexOf("reprioritizeSubIssue", StringComparison.Ordinal), Is.LessThan(sent.IndexOf("addBlockedBy", StringComparison.Ordinal)));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        await Reopen();
        Assert.That(PlanCsvImport.Prepare(session.Document, file, PlanCsvImportTests.Catalog, Today).IsDuplicate, Is.True);
    }

    [Test]
    public async Task NewTasksPublishToEachRowsRepository()
    {
        await Start(0);
        await session.Execute(new InsertPlanRows([PlanRow.New("Default", "acme/repo"), PlanRow.New("Other", "acme/other")]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Select(i => i.Row.Repository), Is.EqualTo(new[] { "acme/repo", "acme/other" }));
    }

    [Test]
    public async Task LostMultiRepositoryCreationResponsesReconcileAtTheirFrozenDestinations()
    {
        await Start(0);
        await session.Execute(new InsertPlanRows([PlanRow.New("Default", "acme/repo"), PlanRow.New("Other", "acme/other")]), Today);
        Scenario("uncertain-verification");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(FakePlanEditor.Load(root).Issues.Length, Is.EqualTo(2));
        await Reopen(); Scenario();
        var resumed = await publisher.PublishAsync(session, Today);
        Assert.That(resumed.Succeeded, Is.True, resumed.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Select(i => (i.Row.Title, i.Row.Repository)), Is.EquivalentTo(new[] { ("Default", "acme/repo"), ("Other", "acme/other") }));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }

    [TestCase("PlanPublishRepository"), TestCase("PlanCreationGuard")]
    public async Task CancelledCreationReconciliationPreservesPlanHistoryAndStoredDocument(string query)
    {
        await Start(0);
        await session.Execute(new InsertPlanRows([PlanRow.New("Unconfirmed", "acme/repo")]), Today);
        Scenario("uncertain-verification");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(session.Document.Sync.Publish!.Writes.Any(w => w.Stage == PlanPublishStage.Create && w.State == PlanWriteState.Dispatched), Is.True);
        await session.FlushAsync();
        var document = session.Document;
        var undo = session.UndoCount;
        var redo = session.RedoCount;
        var path = new PlanStore(root).FileFor(Project);
        var saved = File.ReadAllBytes(path);
        var mutations = File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl"));
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true, holdQuery = query }));
        using var cancellation = new CancellationTokenSource();
        var refresh = publisher.RefreshAsync(session, Today, cancellation.Token);
        try {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(Path.Combine(root, "held-gh.pid")) && !refresh.IsCompleted && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.That(File.Exists(Path.Combine(root, "held-gh.pid")), Is.True, "The lookup must be in flight before cancelling.");
            cancellation.Cancel();
            Assert.ThrowsAsync<OperationCanceledException>(async () => await refresh);
            Assert.That(session.Document, Is.EqualTo(document));
            Assert.That(session.UndoCount, Is.EqualTo(undo));
            Assert.That(session.RedoCount, Is.EqualTo(redo));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(saved));
            Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Is.EqualTo(mutations));
        }
        finally {
            cancellation.Cancel();
            File.WriteAllText(Path.Combine(root, "release-gh"), "release");
            try { await refresh; } catch (OperationCanceledException) { }
        }
    }

    [Test]
    public async Task CancelledProjectReadRetainsOnlyTheConfirmedCreationLink()
    {
        await Start(0);
        var row = PlanRow.New("Unconfirmed", "acme/repo");
        await session.Execute(new InsertPlanRows([row]), Today);
        Scenario("uncertain-verification");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        var before = session.Document;
        var pending = before.Sync.Publish!;
        var creation = pending.Writes.Single(w => w.Stage == PlanPublishStage.Create);
        Assert.That(creation.State, Is.EqualTo(PlanWriteState.Dispatched));
        var undo = session.UndoCount;
        var redo = session.RedoCount;
        var mutations = File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl"));
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true, holdQuery = "ProjectFields" }));
        using var cancellation = new CancellationTokenSource();
        var refresh = publisher.RefreshAsync(session, Today, cancellation.Token);
        try {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(Path.Combine(root, "held-gh.pid")) && !refresh.IsCompleted && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.That(File.Exists(Path.Combine(root, "held-gh.pid")), Is.True);
            var linked = session.Document.Sync.Publish!.Writes.Single(w => w.Key == creation.Key);
            Assert.That(linked.Identity, Is.EqualTo(row.Identity));
            Assert.That(linked.State, Is.EqualTo(PlanWriteState.Succeeded));
            Assert.That(linked, Is.EqualTo(creation with { State = PlanWriteState.Succeeded,
                ResultId = FakePlanEditor.Load(root).Issues.Single().Row.Identity, Error = null }));
            var expected = before with { Sync = before.Sync with { Publish = pending with {
                Writes = pending.Writes.Select(w => w.Key == creation.Key ? linked : w).ToImmutableArray() } } };
            var saved = File.ReadAllBytes(new PlanStore(root).FileFor(Project));
            cancellation.Cancel();
            Assert.ThrowsAsync<OperationCanceledException>(async () => await refresh);
            Assert.That(JsonSerializer.Serialize(session.Document), Is.EqualTo(JsonSerializer.Serialize(expected)));
            Assert.That(session.UndoCount, Is.EqualTo(undo));
            Assert.That(session.RedoCount, Is.EqualTo(redo));
            Assert.That(File.ReadAllBytes(new PlanStore(root).FileFor(Project)), Is.EqualTo(saved));
            await Reopen();
            Assert.That(JsonSerializer.Serialize(session.Document), Is.EqualTo(JsonSerializer.Serialize(expected)));
            Assert.That(session.UndoCount, Is.EqualTo(undo));
            Assert.That(session.RedoCount, Is.EqualTo(redo));
            Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Is.EqualTo(mutations));
        }
        finally {
            cancellation.Cancel();
            File.WriteAllText(Path.Combine(root, "release-gh"), "release");
            try { await refresh; } catch (OperationCanceledException) { }
        }
    }

    [Test]
    public async Task InaccessibleSecondDestinationRejectsAllCreationBeforeDispatch()
    {
        await Start(0);
        await session.Execute(new InsertPlanRows([PlanRow.New("Default", "acme/repo"), PlanRow.New("Other", "acme/other")]), Today);
        Scenario("other-repository-denied");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(FakePlanEditor.Load(root).Issues, Is.Empty);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        Assert.That(session.Document.Sync.Publish, Is.Null);
    }
    [Test]
    public async Task AnUntitledRowIsNotCreatedAndTheRestOfThePlanStillPublishes()
    {
        await Start(1);
        await session.Execute(new InsertPlanRows([PlanRow.New("", "acme/repo")]), Today);
        var untitled = session.Document.State.Rows.Single(r => r.Identity.StartsWith("local:", StringComparison.Ordinal));
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Edited")]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Select(i => i.Row.Title), Is.EqualTo(new[] { "Edited" }));
        var sent = File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl"));
        Assert.That(sent, Does.Contain("updateIssue"));
        Assert.That(sent, Does.Not.Contain("createIssue"));
        Assert.That(session.Document.Sync.Failures, Is.Empty, "A row that cannot be published yet is not a failure.");
        Assert.That(session.Document.State.Rows.Any(r => r.Identity == untitled.Identity), Is.True);
        Assert.That(session.Changes(Today).Fields.ContainsKey(untitled.Identity), Is.True);
    }

    [Test]
    public async Task AnUntitledRowAlonePublishesNothingAndStillSucceeds()
    {
        await Start(1);
        await session.Execute(new InsertPlanRows([PlanRow.New("", "acme/repo")]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Count, Is.EqualTo(1));
        Assert.That(File.Exists(Path.Combine(root, "plan-mutations.jsonl")), Is.False, "Nothing is sent for a row this publish cannot create.");
        Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(1));
    }
    // #247 M1: the held write can belong to a published row — here the requirement whose child order
    // now names the untitled row inserted before its published child. Publishing the rest must report
    // success and leave only that order change unpublished; nothing may name the row it cannot create.
    [Test]
    public async Task AHeldChildOrderOnAPublishedRequirementDoesNotFailTheRestOfThePublish()
    {
        await Start(3, state => state with {
            Issues = state.Issues.Select(i => i.Row.Identity == "I2" ? i with { Row = i.Row with { Parent = "I1" } } : i).ToImmutableArray(),
            SubOrders = state.SubOrders.Add("I1", ["I2"])
        });
        await session.Execute(new InsertPlanRows([PlanRow.New("", "acme/repo")], "I2"), Today);
        var untitled = session.Document.State.Rows.Single(r => r.Identity.StartsWith("local:", StringComparison.Ordinal));
        Assert.That(untitled.Parent, Is.EqualTo("I1"), "A row inserted before a published child inherits that child's requirement.");
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I3", PlanField.Title, "Edited")]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.Multiple(() => {
            Assert.That(result.Succeeded, Is.True, result.Error);
            Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I3").Row.Title, Is.EqualTo("Edited"), "The valid edit is sent.");
            Assert.That(session.Changes(Today).Fields.TryGetValue("I1", out var fields) && fields.Contains(PlanField.SubIssueOrder), Is.True,
                "The requirement's child order stays unpublished while it names the untitled row.");
            Assert.That(session.Changes(Today).Fields.ContainsKey(untitled.Identity), Is.True, "The untitled row stays unpublished.");
        });
        var sent = File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl"));
        Assert.That(sent, Does.Contain("updateIssue"));
        Assert.That(sent, Does.Not.Contain("createIssue"));
        Assert.That(sent, Does.Not.Contain("reprioritizeSubIssue"), "An order that names the untitled row is held whole.");
        Assert.That(sent, Does.Not.Contain(untitled.Identity), "No write may name a row this publish does not create.");
    }

    // #247 M1, second shape of the same held write: a published row's 先行 link now points at the
    // untitled row. The link is held; sending the rest must still report success.
    [Test]
    public async Task AHeldPredecessorLinkToAnUntitledRowDoesNotFailTheRestOfThePublish()
    {
        await Start(2);
        await session.Execute(new InsertPlanRows([PlanRow.New("", "acme/repo")]), Today);
        var untitled = session.Document.State.Rows.Single(r => r.Identity.StartsWith("local:", StringComparison.Ordinal));
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [
            new("I2", PlanField.Predecessors, new[] { untitled.Identity }),
            new("I1", PlanField.Title, "Edited")]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.Multiple(() => {
            Assert.That(result.Succeeded, Is.True, result.Error);
            Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I1").Row.Title, Is.EqualTo("Edited"), "The valid edit is sent.");
            Assert.That(session.Changes(Today).Fields.TryGetValue("I2", out var fields) && fields.Contains(PlanField.Predecessors), Is.True,
                "The 先行 link to the untitled row stays unpublished.");
        });
        var sent = File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl"));
        Assert.That(sent, Does.Contain("updateIssue"));
        Assert.That(sent, Does.Not.Contain("createIssue"));
        Assert.That(sent, Does.Not.Contain("blockingIssueId"), "A 先行 link to the untitled row is held.");
        Assert.That(sent, Does.Not.Contain(untitled.Identity), "No write may name a row this publish does not create.");
    }

    // #247 M1: the exemption is exact. A real write failure still reports the publish as unfinished,
    // while the held order change stays held and nothing is sent for the untitled row or its dependants.
    [Test]
    public async Task ARealFieldFailureStillFailsThePublishWithAHeldOrderChangePresent()
    {
        await Start(3, state => state with {
            Issues = state.Issues.Select(i => i.Row.Identity == "I2" ? i with { Row = i.Row with { Parent = "I1" } } : i).ToImmutableArray(),
            SubOrders = state.SubOrders.Add("I1", ["I2"])
        });
        await session.Execute(new InsertPlanRows([PlanRow.New("", "acme/repo")], "I2"), Today);
        var untitled = session.Document.State.Rows.Single(r => r.Identity.StartsWith("local:", StringComparison.Ordinal));
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [
            new("I3", PlanField.Title, "Edited"), new("I3", PlanField.Actual, 4m)]), Today);
        Scenario("field-denied");
        var result = await publisher.PublishAsync(session, Today);
        Assert.Multiple(() => {
            Assert.That(result.Succeeded, Is.False, "A genuine field failure is not a held write.");
            Assert.That(result.Error, Is.EqualTo("未完了の変更があります。確認後に再発行してください。"));
            Assert.That(session.Document.Sync.Failures.Any(f => f.Identity == "I3" && f.Field == PlanField.Actual), Is.True,
                "The real failure is what keeps the publish unfinished.");
            Assert.That(session.Changes(Today).Fields.TryGetValue("I1", out var order) && order.Contains(PlanField.SubIssueOrder), Is.True,
                "The held order change is still present alongside the real failure.");
        });
        var sent = File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl"));
        Assert.That(sent, Does.Contain("updateIssue"));
        Assert.That(sent, Does.Not.Contain("createIssue"));
        Assert.That(sent, Does.Not.Contain("reprioritizeSubIssue"));
        Assert.That(sent, Does.Not.Contain(untitled.Identity), "Nothing is sent for the untitled row or its dependants.");
    }

    [Test]
    public async Task ClosingAndCancellingRowsPublishTheirStateReason()
    {
        await Start(2);
        await session.Execute(new ClosePlanRows(["I1"]), Today);
        await session.Execute(new ClosePlanRows(["I2"], Cancelled: true), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var state = FakePlanEditor.Load(root);
        Assert.That(state.Issues.Single(i => i.Row.Identity == "I1").Row.Closed, Is.True);
        Assert.That(state.Issues.Single(i => i.Row.Identity == "I1").Row.Cancelled, Is.False);
        Assert.That(state.Issues.Single(i => i.Row.Identity == "I2").Row.Cancelled, Is.True);
        var sent = File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl"));
        Assert.That(sent, Does.Contain("closeIssue"));
        Assert.That(sent, Does.Contain("COMPLETED"));
        Assert.That(sent, Does.Contain("NOT_PLANNED"));
        // #308: the close also publishes the calculated Remaining 0, but no plan date from the close day:
        // 開始日 and 終了日 stay plan values.
        Assert.That(sent, Does.Contain("updateProjectV2ItemFieldValue"));
        Assert.That(sent, Does.Not.Contain("F-Start"));
        Assert.That(sent, Does.Not.Contain("F-End"));
        Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(0));
        var closedRow = FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I1").Row;
        Assert.That(closedRow.End, Is.Null, "the plan end stays the plan value (none here)");
        Assert.That(closedRow.CloseDate, Is.Not.Null, "the Issue records the close day");
    }

    [Test]
    public async Task ReopeningAClosedIssueSendsReopenIssue()
    {
        await Start(1, state => state with { Issues = state.Issues.Select(i => i with {
            Row = i.Row with { Closed = true, Cancelled = true, CloseDate = Today.AddDays(-1) } }).ToImmutableArray() });
        Assert.That(session.Document.State.Rows.Single().Cancelled, Is.True, "the read reports 中止 from the state reason");
        await session.Execute(new ReopenPlanRows(["I1"]), Today);
        Assert.That(session.Document.State.Rows.Single().Closed, Is.False);
        await publisher.PublishAsync(session, Today);
        Assert.That(FakePlanEditor.Load(root).Issues.Single().Row.Closed, Is.False);
        Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Does.Contain("reopenIssue"));
    }

    [Test]
    public async Task RemovingARowArchivesItsItemWithoutDeletingTheIssue()
    {
        await Start(1);
        await session.Execute(new RemovePlanRows(["I1"]), Today);
        await publisher.PublishAsync(session, Today);
        var state = FakePlanEditor.Load(root);
        Assert.That(state.Issues.Single().Archived, Is.True);
        Assert.That(state.Issues.Single().Added, Is.True, "The Issue and its membership remain; only the item is archived.");
        Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Does.Contain("archiveProjectV2Item").And.Contain("\"projectId\""));
        Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Does.Not.Contain("deleteIssue"));
        // The archived row leaves the plan; it is not kept as an item GitHub can no longer return.
        Assert.That(session.Document.State.Rows.Select(r => r.Identity), Does.Not.Contain("I1"));
        Assert.That(session.Document.Sync.Unavailable, Is.Empty);
        Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(0));
    }

    // #231 C3: an Issue that comes back after this app archived its Project item is returning, not
    // newly found. It keeps the position the read reports instead of being re-placed after its parent's
    // last child, so the plan has no position or sub-issue-order difference to review or publish.
    [Test]
    public async Task ARestoredIssueKeepsTheReadPositionAndNeedsNoOrderRewrite()
    {
        await Start(4, state => state with {
            Issues = state.Issues.Select(i => i.Row.Identity == "I1" ? i : i with { Row = i.Row with { Parent = "I1" } }).ToImmutableArray(),
            SubOrders = state.SubOrders.Add("I1", ["I2", "I3", "I4"])
        });
        await session.Execute(new RemovePlanRows(["I3"]), Today);
        var archived = await publisher.PublishAsync(session, Today);
        Assert.That(archived.Succeeded, Is.True, archived.Error);
        Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "I1", "I2", "I4" }));
        Assert.That(session.Document.State.ArchivedIdentities, Is.EqualTo(new[] { "I3" }), "The verified archive is remembered with the plan.");
        await Reopen();
        Assert.That(session.Document.State.ArchivedIdentities, Is.EqualTo(new[] { "I3" }), "The memory survives closing and reopening.");
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Edited locally")]), Today);
        await session.Undo(Today);
        Assert.That(session.Document.State.ArchivedIdentities, Is.EqualTo(new[] { "I3" }), "The memory is a GitHub observation, so Undo keeps it.");
        // A read while the item is still archived does not return it, so the memory must survive it.
        var stillArchived = await publisher.RefreshAsync(session, Today);
        Assert.That(stillArchived.Succeeded, Is.True, stillArchived.Error);
        Assert.That(session.Document.State.Rows.Any(r => r.Identity == "I3"), Is.False);
        Assert.That(session.Document.State.ArchivedIdentities, Is.EqualTo(new[] { "I3" }), "A read that still misses the archive does not forget it.");
        // GitHub's archived item is restored at the Project position it had, and the native sub-issue
        // order kept its slot; the next read returns both.
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i.Row.Identity == "I3" ? i with { Archived = false } : i).ToImmutableArray() });
        var refreshed = await publisher.RefreshAsync(session, Today);
        Assert.That(refreshed.Succeeded, Is.True, refreshed.Error);
        var changes = session.Changes(Today).Fields;
        Assert.Multiple(() => {
            Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "I1", "I2", "I3", "I4" }),
                "The returning Issue keeps the position the read reports.");
            Assert.That(changes.Values.SelectMany(fields => fields), Has.None.EqualTo(PlanField.Order), "The plan asks for no position rewrite.");
            Assert.That(changes.Values.SelectMany(fields => fields), Has.None.EqualTo(PlanField.SubIssueOrder), "The plan asks for no sub-issue-order rewrite.");
            Assert.That(PlanOperations.SiblingOrderChange(session.Document, "I1"), Is.Null, "The review has no sub-issue-order line to show.");
            Assert.That(session.Document.Sync.Conflicts.Any(c => c.Field == PlanField.SubIssueOrder), Is.False);
            Assert.That(session.Document.State.ArchivedIdentities, Is.Empty, "The memory is pruned once the Issue comes back.");
        });
    }

    // #231: archiving a published row moves the unpublished children that stay in the plan to its
    // parent, and the verified archive is what later reads may find again.
    [Test]
    public async Task ArchivingARowMovesItsLocalChildToItsParent()
    {
        await Start(3, state => state with {
            Issues = state.Issues.Select(i => i.Row.Identity switch { "I2" => i with { Row = i.Row with { Parent = "I1" } }, "I3" => i with { Row = i.Row with { Parent = "I2" } }, _ => i }).ToImmutableArray(),
            SubOrders = state.SubOrders.Add("I1", ["I2"]).Add("I2", ["I3"])
        });
        var child = PlanRow.New("", "acme/repo");
        await session.Execute(new InsertPlanRows([child], "I3"), Today);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == child.Identity).Parent, Is.EqualTo("I2"));
        await session.Execute(new RemovePlanRows(["I2"]), Today);
        var archived = await publisher.PublishAsync(session, Today);
        Assert.That(archived.Succeeded, Is.True, archived.Error);
        var rows = session.Document.State.Rows;
        Assert.That(string.Join(",", rows.Select(r => r.Identity)), Is.EqualTo("I1," + child.Identity), "The published subtree leaves the plan; the unpublished child stays.");
        Assert.That(rows.Single(r => r.Identity == child.Identity).Parent, Is.EqualTo("I1"), "The archived row's local child moves to its parent.");
        Assert.That(session.Document.Baseline.Rows.Any(r => r.Identity == child.Identity), Is.False, "The child is not published, so it has no baseline row.");
        Assert.That(session.Changes(Today).Fields.ContainsKey(child.Identity), Is.True, "The child stays a held new row.");
    }

    [Test]
    public async Task OneRowsStateFailureStaysPendingAndDoesNotStopTheOthers()
    {
        await Start(2);
        await session.Execute(new ClosePlanRows(["I1"]), Today);
        await session.Execute(new ClosePlanRows(["I2"]), Today);
        Scenario("close-denied");
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.False, "expected the failed row to keep the publish unfinished; error=" + (result.Error ?? "<none>"));
        var state = FakePlanEditor.Load(root);
        Assert.That(state.Issues.Single(i => i.Row.Identity == "I1").Row.Closed, Is.True, "The other row's state write is independent.");
        Assert.That(state.Issues.Single(i => i.Row.Identity == "I2").Row.Closed, Is.False);
        Assert.That(session.Document.Sync.Failures.Any(f => f.Identity == "I2" && f.Field == PlanField.Closed), Is.True, "The failed row keeps its reason.");
        Assert.That(session.Changes(Today).Fields.ContainsKey("I2"), Is.True, "The failed row stays pending for the next publish.");
        Assert.That(session.Document.Sync.Failures.Any(f => f.Identity == "I1"), Is.False, "The successful row is not recorded as a failure.");
    }
    private static readonly DateOnly Today = new(2026, 10, 5);
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");
    private string root = null!;
    private PlanSession session = null!;
    private PlanPublisher publisher = null!;
    private CountingRunner runner = null!;
    private static readonly ProjectPlanSettings Settings = new()
    {
        DefaultRepository = "acme/repo", StatusDate = Today,
        Columns = new[] { PlanField.Estimate, PlanField.Remaining, PlanField.Actual, PlanField.Start, PlanField.End }
            .Select(f => new PlanColumnMapping(f, "F-" + f, f.ToString(), f is PlanField.Estimate or PlanField.Remaining or PlanField.Actual ? "NUMBER" : "DATE")).ToImmutableArray()
    };
    [SetUp] public void Setup() { root = Path.Combine(Path.GetTempPath(), "ghpb-phase4-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TearDown] public async Task Cleanup()
    {
        if (session is not null) await session.FlushAsync();
        TestContext.Out.WriteLine("Real fake-gh processes: " + (runner?.Count ?? 0));
        Directory.Delete(root, true);
    }
    private void Scenario(string? fault = null, int batch = 1, int gatewayAliasLimit = 0, int mutationDelayMs = 0, int firstMutationDelayMs = 0)
    {
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true, planFault = fault, faultBatch = batch, gatewayAliasLimit, mutationDelayMs, firstMutationDelayMs }));
        var marker = Path.Combine(root, "first-mutation-delay.done");
        if (File.Exists(marker)) File.Delete(marker);
        if (File.Exists(FakePlanEditor.StatePath(root))) FakePlanEditor.Save(root, FakePlanEditor.Load(root) with { ReadAttempts = 0 });
    }
    private async Task Start(int count, Func<PlanFakeState, PlanFakeState>? fixture = null, ProjectPlanSettings? settings = null)
    {
        Scenario();
        var initial = new PlanFakeState(Enumerable.Range(1, count).Select(i => new PlanFakeIssue(new("I" + i, "Task " + i, "acme/repo"), "", true)).ToImmutableArray(), count + 1);
        FakePlanEditor.Save(root, fixture?.Invoke(initial) ?? initial);
        runner = new(new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }));
        var service = new GhConnectionService(GhProcessTests.FakeExecutable, "github.com", runner);
        publisher = new(service, new("github.com", 42, "fixture-user", GhProcessTests.FakeExecutable));
        session = await PlanSession.CreateAsync(new(root), new(Project, new([], []), new([], settings ?? Settings)), Today);
        var refresh = await publisher.RefreshAsync(session, Today);
        Assert.That(refresh.Succeeded, Is.True, refresh.Error);
    }
    private async Task Reopen()
    {
        var loaded = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(loaded.Status, Is.EqualTo(PlanLoadStatus.Loaded), loaded.Error); session = loaded.Session!;
    }
    [TestCase("Done"), TestCase(null)]
    public async Task StatusPublishesThroughProjectAdapterAndUndoRemainsLocal(string? status)
    {
        await Start(1, state => state with { Issues = state.Issues.Select(i => i with { Row = i.Row with { Status = "In progress" } }).ToImmutableArray() });
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Status, status)]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Status, Is.EqualTo(status));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        var sent = File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl"));
        Assert.That(sent, Does.Contain(status is null ? "clearProjectV2ItemFieldValue" : "singleSelectOptionId"));
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows[0].Status, Is.EqualTo("In progress"));
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Status, Is.EqualTo(status));
        await Reopen();
        Assert.That(session.Document.State.Rows[0].Status, Is.EqualTo("In progress"));
    }
    [Test]
    public async Task StatusOnNewTaskSurvivesCreationAndIsPublished()
    {
        await Start(0);
        await session.Execute(new InsertPlanRows([PlanRow.New("New", "acme/repo") with { Status = "Done" }]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Single().Row.Status, Is.EqualTo("Done"));
        Assert.That(session.Document.State.Rows.Single().Status, Is.EqualTo("Done"));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }
    [Test]
    public async Task UnknownStatusStopsBeforeAnyMutation()
    {
        await Start(1);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Status, "Unknown")]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Does.Contain("Status"));
        Assert.That(File.Exists(Path.Combine(root, "plan-mutations.jsonl")), Is.False);
        Assert.That(session.Document.State.Rows[0].Status, Is.EqualTo("Unknown"));
    }
    private static IEnumerable<int> SequenceSeeds => Enumerable.Range(51000, 20);
    private static IEnumerable<int> FullSequenceSeeds => Enumerable.Range(51000, 200);
    [TestCaseSource(nameof(FullSequenceSeeds)), Category("HistorySequence"), Explicit("Full 200-sequence experiment")]
    public Task FullHistorySequences(int seed) => RandomizedOperationsKeepEverySavedHistoryReplayable(seed);
    [TestCaseSource(nameof(SequenceSeeds))]
    public async Task RandomizedOperationsKeepEverySavedHistoryReplayable(int seed)
    {
        await Start(5);
        var random = new Random(seed); var trace = new List<string>(); var audits = 0;
        var counts = new Dictionary<string, int>();
        var operations = Enumerable.Range(0, 30).Select(i => i % 14).OrderBy(_ => random.Next()).ToArray();
        for (var step = 0; step < operations.Length; step++)
        {
            var rows = session.Document.State.Rows;
            var row = rows[random.Next(rows.Length)];
            var other = rows.Where(r => r.Identity != row.Identity).ToArray()[random.Next(rows.Length - 1)];
            var op = operations[step]; var label = op.ToString();
            PlanCommand? command = op switch
            {
                0 => new EditPlanCells(PlanOperationKind.Cell, [new(row.Identity, PlanField.Title, $"Edit {seed}/{step}")]),
                1 => new EditPlanCells(PlanOperationKind.Cell, [new(row.Identity, PlanField.Actual, (decimal)random.Next(0, 30))]),
                2 => new IndentPlanRows([row.Identity]),
                3 => new IndentPlanRows([row.Identity], Outdent: true),
                4 => new MovePlanRows([row.Identity], random.Next(2) == 0 ? null : other.Identity),
                5 => new EditPlanCells(PlanOperationKind.Cell, [new(row.Identity, PlanField.Predecessors,
                    random.Next(2) == 0 ? ImmutableArray<string>.Empty : ImmutableArray.Create(other.Identity))]),
                6 => new InsertPlanRows([new PlanRow("local:" + seed.ToString("x16") + step.ToString("x16"), $"New {seed}/{step}", "acme/repo")], other.Identity),
                12 => new MovePlanSubtrees([row.Identity], random.Next(2) == 0 ? null : other.Identity),
                13 => new ArrangePlanOutline(),
                _ => null
            };
            try
            {
                if (command is not null)
                {
                    var before = PlanJson.Text(session.Document);
                    try { var save = await session.Execute(command, Today); Assert.That(save.Succeeded, Is.True, save.Error); }
                    catch (ArgumentException) { Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before), "Invalid user input must reject atomically."); label += "-rejected"; }
                }
                else if (op == 7)
                {
                    Scenario(); var remote = FakePlanEditor.Load(root);
                    var order = remote.Issues.OrderBy(_ => random.Next()).ToArray();
                    var changed = order.Select((issue, i) => issue with { Row = issue.Row with
                    {
                        Parent = i > 0 && random.Next(3) == 0 ? order[random.Next(i)].Row.Identity : null,
                        Predecessors = i > 0 && random.Next(3) == 0 ? [order[random.Next(i)].Row.Identity] : [],
                        Actual = random.Next(3) == 0 ? random.Next(30) : issue.Row.Actual
                    } }).ToImmutableArray();
                    FakePlanEditor.Save(root, remote with { Issues = changed, SubOrders = ImmutableDictionary<string, ImmutableArray<string>>.Empty });
                    label += (await publisher.RefreshAsync(session, Today)).Succeeded ? "-adopted" : "-rejected";
                }
                else if (op == 8)
                {
                    foreach (var conflict in session.Document.Sync.Conflicts.ToArray())
                    {
                        try { Assert.That((await session.ResolveConflict(conflict.Identity, conflict.Field, random.Next(2) == 0, Today)).Succeeded, Is.True); }
                        catch (ArgumentException) { /* An invalid relationship resolution remains unresolved. */ }
                    }
                    var fault = new string?[] { null, "partial", "before", "uncertain-verification", "verificationfailure", "repository-denied" }[random.Next(6)];
                    Scenario(fault, FakePlanEditor.Load(root).MutationBatches + 1);
                    var result = await publisher.PublishAsync(session, Today);
                    label += "-" + (fault ?? "complete") + (result.Succeeded ? "-succeeded" : "-unfinished");
                    Assert.That(session.Document.Sync.Publish is not { DispatchStarted: false }, Is.True, "An unsent attempt must not lock editing after Publish returns.");
                }
                else if (op == 9) Assert.That((await session.Undo(Today)).Succeeded, Is.True);
                else if (op == 10) Assert.That((await session.Redo(Today)).Succeeded, Is.True);
                else { Assert.That((await session.FlushAsync()).Succeeded, Is.True); await Reopen(); }
                trace.Add($"{step}:{label}:{row.Identity}"); counts[label] = counts.GetValueOrDefault(label) + 1;
                AssertAcyclic(FakePlanEditor.Load(root).Issues.Select(i => i.Row).ToArray());
                var creations = FakePlanEditor.Load(root).Issues.Where(i => i.Body.Length > 0).ToArray();
                Assert.That(creations.Select(i => System.Text.RegularExpressions.Regex.Match(i.Body, @"local:[a-f0-9]{32}").Value).Distinct().Count(), Is.EqualTo(creations.Length), "A local identity must never produce duplicate Issues, even under a new publish run marker.");
                await Reopen();
                var current = PlanJson.Text(session.Document.State); var baseline = PlanJson.Text(session.Document.Baseline);
                string Evidence() => session.Document.Sync.Publish is not { } attempt ? "null" : PlanJson.Text(new { attempt.RunId, attempt.DispatchStarted, Writes = attempt.Writes.Select(w => new { w.Key, w.Identity, w.State, w.ResultId }) });
                var evidence = Evidence(); var undo = session.UndoCount; var redo = session.RedoCount;
                var remoteBefore = PlanJson.Text(FakePlanEditor.Load(root));
                async Task Travel(bool forward)
                {
                    var save = await (forward ? session.Redo(Today) : session.Undo(Today));
                    Assert.That(save.Succeeded, Is.True, save.Error);
                    await Reopen(); audits++;
                    Assert.That(PlanJson.Text(session.Document.Baseline), Is.EqualTo(baseline), "I1: Undo cannot roll back verified remote values.");
                    Assert.That(Evidence(), Is.EqualTo(evidence), "I1: Undo cannot reset dispatch, outcomes or identities.");
                }
                for (var i = 0; i < undo; i++) await Travel(false);
                Assert.That(session.UndoCount, Is.Zero);
                for (var i = 0; i < undo + redo; i++) await Travel(true);
                Assert.That(session.RedoCount, Is.Zero);
                for (var i = 0; i < redo; i++) await Travel(false);
                Assert.That(session.UndoCount, Is.EqualTo(undo)); Assert.That(session.RedoCount, Is.EqualTo(redo));
                Assert.That(PlanJson.Text(session.Document.State), Is.EqualTo(current));
                Assert.That(PlanJson.Text(FakePlanEditor.Load(root)), Is.EqualTo(remoteBefore), "History navigation must not write to GitHub.");
            }
            catch (Exception ex) { Assert.Fail($"Seed {seed}, step {step}, operation {label}; trace: {string.Join(" | ", trace)}\n{ex}"); }
        }
        TestContext.Progress.WriteLine($"Completed randomized sequence {seed}: 30 steps, {audits} history traversals.");
        TestContext.Out.WriteLine($"Randomized invariant sequence: seed={seed}; steps=30; historyTraversals={audits}; outcomes={JsonSerializer.Serialize(counts)}");
    }
    private static void AssertAcyclic(PlanRow[] rows)
    {
        var byId = rows.ToDictionary(r => r.Identity);
        foreach (var hierarchy in new[] { true, false })
        {
            var complete = new HashSet<string>(); var active = new HashSet<string>();
            void Visit(string id)
            {
                if (complete.Contains(id) || !byId.TryGetValue(id, out var row)) return;
                Assert.That(active.Add(id), Is.True, "I3: the synthetic remote graph contains a cycle.");
                foreach (var next in hierarchy ? row.Parent is null ? [] : new[] { row.Parent } : row.Predecessors.ToArray()) Visit(next);
                active.Remove(id); complete.Add(id);
            }
            foreach (var row in rows) Visit(row.Identity);
        }
    }
    [Test]
    public async Task CreationBatchesArePacedFromTheirStartsWithNoInitialSleep()
    {
        await Start(0);
        await session.Execute(new InsertPlanRows(Enumerable.Range(0, 11).Select(i => PlanRow.New("Paced " + i, "acme/repo")).ToImmutableArray()), Today);
        var started = DateTimeOffset.UtcNow;
        var published = await publisher.PublishAsync(session, Today);
        Assert.That(published.Succeeded, Is.True, published.Error);
        var creates = File.ReadAllLines(Path.Combine(root, "plan-mutations.jsonl")).Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(r => r.GetProperty("query").GetString()!.Contains("createIssue")).Select(r => r.GetProperty("started").GetDateTimeOffset()).ToArray();
        Assert.That((creates[0] - started).TotalSeconds, Is.LessThan(5), "The first create batch needs no pacing wait.");
        // Each request reaches gh after its reservation by a save and a process start whose latency varies on a
        // shared machine, so the spacing GitHub sees is checked against the persisted reservations, not between arrivals.
        await Reopen();
        var reserved = session.Document.Sync.CreationStarts;
        Assert.That(reserved.Select(s => s.Issues), Is.EqualTo(new[] { 10, 1 }));
        Assert.That((reserved[1].Started - reserved[0].Started).TotalSeconds, Is.InRange(10, 13), "Ten created Issues reserve the next ten seconds start-to-start, even when the following batch contains only one Issue.");
        Assert.That(creates.Zip(reserved, (sent, reservation) => sent >= reservation.Started), Is.All.True, "No create batch reaches GitHub before its reserved start.");
    }
    private async Task StartWithIssuesAddedOnGitHub()
    {
        await Start(6, state => state with {
            Issues = state.Issues.Select(i => i with { Row = i.Row with { Parent = i.Row.Identity switch { "I2" or "I3" => "I1", "I5" or "I6" => "I4", _ => null } } }).ToImmutableArray(),
            SubOrders = state.SubOrders.Add("I1", ["I2", "I3"]).Add("I4", ["I5", "I6"])
        });
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I5", PlanField.Title, "Edited before refresh")]), Today);
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with {
            Issues = remote.Issues.Add(new(new("I7", "Added sub-issue", "acme/repo") { Parent = "I1" }, "", true)).Add(new(new("I8", "Added without parent", "acme/repo"), "", true)),
            NextId = 9, SubOrders = remote.SubOrders.SetItem("I1", ["I2", "I3", "I7"])
        });
    }
    private string[] OrderChanges() => session.Changes(Today).Fields.Where(p => p.Value.Contains(PlanField.Order)).Select(p => p.Key).ToArray();

    [Test]
    public async Task IssueAddedOnGitHubLandsUnderItsParentAndPublishesOnlyItsPosition()
    {
        await StartWithIssuesAddedOnGitHub();
        var placed = new[] { "I1", "I2", "I3", "I7", "I4", "I5", "I6", "I8" };
        var refresh = await publisher.RefreshAsync(session, Today);
        Assert.That(refresh.Succeeded, Is.True, refresh.Error);
        Assert.That(refresh.Refresh!.AddedTasks, Is.EqualTo(new[] { "I7", "I8" }));
        Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(placed));
        Assert.That(OrderChanges(), Is.EqualTo(new[] { "I7" }));
        Assert.That((await publisher.RefreshAsync(session, Today)).Refresh!.AddedTasks, Is.Empty, "A second refresh finds nothing new.");
        await Reopen();
        Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(placed));
        Assert.That(OrderChanges(), Is.EqualTo(new[] { "I7" }));
        Assert.That((await session.Undo(Today)).Succeeded, Is.True);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I5").Title, Is.EqualTo("Task 5"));
        Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(placed), "Undo of an earlier edit keeps the placement.");
        var mutations = File.Exists(Path.Combine(root, "plan-mutations.jsonl")) ? File.ReadAllLines(Path.Combine(root, "plan-mutations.jsonl")).Length : 0;
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var positions = File.ReadAllLines(Path.Combine(root, "plan-mutations.jsonl")).Skip(mutations)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(r => r.GetProperty("query").GetString()!.Contains("updateProjectV2ItemPosition"))
            .SelectMany(r => r.GetProperty("variables").EnumerateObject().Select(v => v.Value)).ToArray();
        Assert.That(positions.Select(v => (v.GetProperty("itemId").GetString(), v.GetProperty("afterId").GetString())), Is.EqualTo(new[] { ("T-I7", "T-I3") }));
        Assert.That(FakePlanEditor.Load(root).Issues.Select(i => i.Row.Identity), Is.EqualTo(placed));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }

    [Test]
    public async Task ResetReturnsAnIssueAddedOnGitHubToGitHubOrder()
    {
        await StartWithIssuesAddedOnGitHub();
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        var reset = await publisher.ResetAsync(session, Today);
        Assert.That(reset.Succeeded, Is.True, reset.Error);
        Assert.That(reset.Refresh!.AddedTasks, Is.Empty);
        Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "I1", "I2", "I3", "I4", "I5", "I6", "I7", "I8" }));
        Assert.That(OrderChanges(), Is.Empty);
    }

    [Test]
    public async Task MovingBlockDownPublishesOnlyThatBlockAndRemoteMatchesPlan()
    {
        await Start(8);
        await session.Execute(new MovePlanRows(["I1", "I2"], "I7"), Today);
        var expected = new[] { "I3", "I4", "I5", "I6", "I1", "I2", "I7", "I8" };
        Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(expected));
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Select(i => i.Row.Identity), Is.EqualTo(expected));
        var positions = File.ReadAllLines(Path.Combine(root, "plan-mutations.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(r => r.GetProperty("query").GetString()!.Contains("updateProjectV2ItemPosition"))
            .SelectMany(r => r.GetProperty("variables").EnumerateObject().Select(v => v.Value)).ToArray();
        Assert.That(positions.Select(v => v.GetProperty("itemId").GetString()), Is.EqualTo(new[] { "T-I1", "T-I2" }));
        Assert.That(positions.Select(v => v.GetProperty("afterId").GetString()), Is.EqualTo(new[] { "T-I6", "T-I1" }));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }

    [Test]
    public async Task PositionUpdatesCompleteWhenMultiAliasPositionRequestsLoseTheirResponsePartway()
    {
        await Start(8);
        await session.Execute(new MovePlanRows(["I5", "I6", "I7", "I8"], "I1"), Today);
        Scenario("position-batch-interrupted");
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Select(i => i.Row.Identity), Is.EqualTo(new[] { "I5", "I6", "I7", "I8", "I1", "I2", "I3", "I4" }));
        Assert.That(session.Document.Sync.Failures, Is.Empty);
        await Reopen();
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }
    [TestCase("verificationfailure")]
    [TestCase("uncertain-verification")]
    public async Task CreationEvidenceSurvivesCorrectionHistoryAndRepublish(string fault)
    {
        await Start(0);
        var good = PlanRow.New("Good", "acme/repo"); var rejected = PlanRow.New("Rejected", "acme/repo");
        await session.Execute(new InsertPlanRows([good, rejected]), Today);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(FakePlanEditor.Load(root).Issues.Length, Is.EqualTo(1));
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(rejected.Identity, PlanField.Title, "Corrected")]), Today);
        Scenario(fault, FakePlanEditor.Load(root).MutationBatches + 1);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        var evidence = session.Document.Sync.Publish!.Writes.Where(w => w.Stage == PlanPublishStage.Create)
            .Select(w => (w.Identity, w.ResultId, w.State)).ToArray();
        Assert.That(FakePlanEditor.Load(root).Issues.Length, Is.EqualTo(2));
        await Reopen();
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == rejected.Identity).Title, Is.EqualTo("Rejected"));
        Assert.That(session.Document.Sync.Publish!.Writes.Where(w => w.Stage == PlanPublishStage.Create).Select(w => (w.Identity, w.ResultId, w.State)), Is.EqualTo(evidence));
        await session.Redo(Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(rejected.Identity, PlanField.Title, "Final")]), Today);
        Scenario();
        var published = await publisher.PublishAsync(session, Today);
        Assert.That(published.Succeeded, Is.True, published.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Select(i => i.Row.Title), Is.EquivalentTo(new[] { "Good", "Final" }));
        Assert.That(FakePlanEditor.Load(root).Issues.Length, Is.EqualTo(2));
        await Reopen();
        var identities = session.Document.State.Rows.Select(r => r.Identity).ToArray();
        Assert.That(session.UndoCount, Is.GreaterThan(0));
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows.Single(r => r.Title != "Good").Title, Is.EqualTo("Corrected"));
        Assert.That(session.Document.Baseline.Rows.Single(r => r.Title != "Good").Title, Is.EqualTo("Final"));
        await session.Redo(Today);
        Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(identities));
        Scenario("forbid-mutation");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
    }
    [TestCase("global-unknown")]
    [TestCase("global-resource-unknown")]
    [TestCase("missing-alias")]
    [TestCase("unknown-and-resource")]
    public async Task UnresolvedResponseStopsLaterFieldsAndRelationshipsAndSurvivesReopen(string fault)
    {
        await Start(51);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, session.Document.State.Rows
            .Select(r => new PlanCellChange(r.Identity, PlanField.Actual, 3m))
            .Append(new("I51", PlanField.Predecessors, new[] { "I1" })).ToImmutableArray()), Today);
        Scenario(fault);
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        runner.MutationStarting = () => { if (++started == 4) fourStarted.TrySetResult(); };
        runner.BeforeMutationResponse = async (index, token) =>
        {
            await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            if (index == 0) return;
            // Return the unresolved response first. Peers remain outstanding until its outcome is saved.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            while (!session.Document.Sync.Publish!.Writes.Any(w => w.Stage == PlanPublishStage.Fields &&
                w.State == PlanWriteState.Dispatched && w.Error is not null))
                await Task.Delay(10, deadline.Token);
        };

        PlanPublishResult result;
        try { result = await publisher.PublishAsync(session, Today); }
        finally
        {
            runner.BeforeMutationResponse = null;
            runner.MutationStarting = null;
        }

        Assert.That(result.Succeeded, Is.False);
        var remote = FakePlanEditor.Load(root);
        Assert.That(remote.Issues.Single(i => i.Row.Identity == "I51").Row.Actual, Is.Null);
        Assert.That(remote.Issues.Single(i => i.Row.Identity == "I51").Row.Predecessors, Is.Empty);
        Assert.That(remote.MutationBatches, Is.EqualTo(4));
        Assert.That(remote.Issues.Count(i => i.Row.Actual == 3),
            Is.EqualTo(fault.StartsWith("global-", StringComparison.Ordinal) ? 30 : 31));
        Assert.That(result.Error, Does.Contain("GitHubの応答を確認できないため発行を中断しました。"));
        var progress = session.Document.Sync.Publish!;
        Assert.That(progress.Writes.Count(w => w.State == PlanWriteState.Succeeded),
            Is.EqualTo(fault.StartsWith("global-", StringComparison.Ordinal) ? 30 : 31));
        Assert.That(progress.Writes.Any(w => w.State == PlanWriteState.Dispatched), Is.True);
        Assert.That(progress.Writes.Where(w => w.Identity == "I51").All(w => w.State == PlanWriteState.Pending), Is.True);
        var saved = PlanJson.Text(progress);
        await Reopen();
        Assert.That(PlanJson.Text(session.Document.Sync.Publish), Is.EqualTo(saved));
        Assert.That(session.Document.State.Rows.All(r => r.Actual == 3), Is.True);
        Assert.That(session.Document.Sync.Unverified, Is.Not.Empty);

        Scenario();
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(PlanJson.Text(FakePlanEditor.Load(root).Issues), Is.EqualTo(PlanJson.Text(remote.Issues)),
            "Reconciliation must not dispatch pending writes.");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        Assert.That(FakePlanEditor.Load(root).Issues.All(i => i.Row.Actual == 3), Is.True);
        Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I51").Row.Predecessors, Is.EqualTo(new[] { "I1" }));
    }
    [TestCase("partial")]
    [TestCase("before")]
    [TestCase("after-and-read")]
    public async Task FieldOutcomesAreIndependentOfLocalHistorySequences(string fault)
    {
        await Start(2);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I1", PlanField.Actual, 3m), new("I2", PlanField.Actual, 3m)]), Today);
        Scenario(fault); Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        var baseline = PlanJson.Text(session.Document.Baseline); var progress = PlanJson.Text(session.Document.Sync.Publish);
        await Reopen(); await session.Undo(Today);
        Assert.That(session.Document.State.Rows.All(r => r.Actual is null), Is.True);
        Assert.That(PlanJson.Text(session.Document.Baseline), Is.EqualTo(baseline));
        Assert.That(PlanJson.Text(session.Document.Sync.Publish), Is.EqualTo(progress));
        await session.Redo(Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I1", PlanField.Actual, 5m), new("I2", PlanField.Actual, 5m)]), Today);
        Scenario(); var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.All(i => i.Row.Actual == 5m), Is.True);
        await Reopen(); await session.Undo(Today);
        Assert.That(session.Document.State.Rows.All(r => r.Actual == 3m), Is.True);
        Assert.That(session.Document.Baseline.Rows.All(r => r.Actual == 5m), Is.True);
        await session.Redo(Today);
    }
    [Test]
    public async Task PromotedIdentityKeepsItsBaselineDuringUnfinishedSiblingRecovery()
    {
        await Start(0);
        await session.Execute(new InsertPlanRows([PlanRow.New("First", "acme/repo"), PlanRow.New("Second", "acme/repo")]), Today);
        Scenario("partial", 2);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(session.Document.State.Rows.Any(r => r.Identity == "I1"), Is.True);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local")]), Today);
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i.Row.Identity == "I1" ? i with { Row = i.Row with { Title = "Remote" } } : i).ToImmutableArray() });
        Scenario();
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.False);
        var conflict = session.Document.Sync.Conflicts.Single(c => c.Identity == "I1" && c.Field == PlanField.Title);
        Assert.That(conflict.Baseline, Is.EqualTo(PlanJson.Text("First")));
        Assert.That(conflict.Local, Is.EqualTo(PlanJson.Text("Local")));
        Assert.That(conflict.Remote, Is.EqualTo(PlanJson.Text("Remote")));
        Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I1").Row.Title, Is.EqualTo("Remote"));
        await Reopen();
    }
    [Test]
    public async Task DispatchedAttemptIsNotMistakenForUnsentWhenVerificationIsIncomplete()
    {
        await Start(0);
        var row = PlanRow.New("Initial", "acme/repo"); await session.Execute(new InsertPlanRows([row]), Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(row.Identity, PlanField.Title, "Edited")]), Today);
        Scenario("before-and-read");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(session.Document.Sync.Publish, Is.Not.Null);
        var run = session.Document.Sync.Publish!.RunId;
        await Reopen(); await session.Undo(Today); await session.Redo(Today);
        Assert.That(session.Document.Sync.Publish!.RunId, Is.EqualTo(run));
        Scenario(); var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Single().Row.Title, Is.EqualTo("Edited"));
        await Reopen(); Assert.That(session.Document.State.Rows.Single().Identity, Is.EqualTo("I1"));
    }
    [Test]
    public async Task RestartReleasesAnAttemptWhoseFirstMutationWasNeverDispatched()
    {
        await Start(0);
        var row = PlanRow.New("Pending", "acme/repo"); await session.Execute(new InsertPlanRows([row]), Today);
        var run = Guid.NewGuid().ToString("N");
        var review = PlanPublishPlan.Build(session.Document, new(session.Document.Baseline, ImmutableDictionary<string, string>.Empty, [], 0, 0), Today, run);
        await session.SaveSync(session.Document.Sync with { Publish = new(run, review.Writes), NotBefore = DateTimeOffset.UtcNow.AddHours(1) });
        await Reopen();
        Assert.That(session.Document.Sync.Publish, Is.Null); Assert.That(session.Document.Sync.NotBefore, Is.Null);
        await session.Undo(Today); Assert.That(session.Document.State.Rows, Is.Empty);
        await session.Redo(Today); await session.Execute(new ReplacePlanSettings(Settings with { ProjectStart = Today }), Today);
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(FakePlanEditor.Load(root).Issues, Is.Empty);
    }
    [Test]
    public async Task UndoingUnavailableDispositionNeverRewindsAnotherRowsVerifiedBaseline()
    {
        await Start(2);
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = state.Issues.SetItem(0, state.Issues[0] with { Added = false }) });
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        await session.ResolveUnavailable("I1", false, Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Actual, 9m)]), Today);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        await Reopen(); await session.Undo(Today); await session.Undo(Today);
        Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EquivalentTo(new[] { "I1", "I2" }));
        Assert.That(session.Document.Sync.Unavailable, Is.EqualTo(new[] { "I1" }));
        Assert.That(session.Document.Baseline.Rows.Single(r => r.Identity == "I2").Actual, Is.EqualTo(9m));
        Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I2").Row.Actual, Is.EqualTo(9m));
        await session.Redo(Today); await session.Redo(Today); await Reopen();
        Assert.That(session.Document.State.Rows.Single().Actual, Is.EqualTo(9m));
    }
    [TestCase("repository-missing")]
    [TestCase("repository-archived")]
    [TestCase("repository-denied")]
    [TestCase("repository-disabled")]
    [TestCase("mapping")]
    public async Task UnsentPreflightFailuresRemainEditableAcrossRestart(string fault)
    {
        await Start(0, settings: fault == "mapping" ? Settings with { Columns = Settings.Columns.Where(c => c.Role != PlanField.Actual).ToImmutableArray() } : Settings);
        var row = PlanRow.New("New", "acme/repo") with { Actual = 1m };
        await session.Execute(new InsertPlanRows([row]), Today);
        Scenario(fault);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(FakePlanEditor.Load(root).Issues, Is.Empty);
        Assert.That(session.Document.Sync.Publish, Is.Null);
        await Reopen(); await session.Undo(Today); Assert.That(session.Document.State.Rows, Is.Empty);
        await session.Redo(Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(row.Identity, PlanField.Title, "Corrected")]), Today);
        await session.Execute(new ReplacePlanSettings(Settings), Today);
        Scenario(); Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        var result = await publisher.PublishAsync(session, Today); Assert.That(result.Succeeded, Is.True, result.Error);
        await Reopen(); Assert.That(session.Document.State.Rows.Single().Title, Is.EqualTo("Corrected"));
        Assert.That(FakePlanEditor.Load(root).Issues.Length, Is.EqualTo(1));
    }
    [TestCase(PlanField.Parent, 1)]
    [TestCase(PlanField.Parent, 51)]
    [TestCase(PlanField.Predecessors, 1)]
    [TestCase(PlanField.Predecessors, 51)]
    public async Task RelationshipReversalsStayAcyclicAcrossRowsBatchesAndHistory(string relationship, int pairs)
    {
        await Start(pairs * 2, state => state with { Issues = state.Issues.Select((issue, i) => i % 2 == 0 ? issue : issue with
            { Row = relationship == PlanField.Parent ? issue.Row with { Parent = "I" + i } : issue.Row with { Predecessors = ["I" + i] } }).ToImmutableArray() });
        var changes = Enumerable.Range(0, pairs).SelectMany(pair => new[]
        {
            new PlanCellChange("I" + (pair * 2 + 1), relationship, relationship == PlanField.Parent ? "I" + (pair * 2 + 2) : new[] { "I" + (pair * 2 + 2) }),
            new PlanCellChange("I" + (pair * 2 + 2), relationship, relationship == PlanField.Parent ? null : Array.Empty<string>())
        }).ToImmutableArray();
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, changes), Today);
        if (pairs > 1)
        {
            Scenario("partial"); Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
            var partialRows = FakePlanEditor.Load(root).Issues.Select(i => i.Row).ToArray();
            Assert.That(partialRows.Where((_, i) => i % 2 == 0).Count(r => relationship == PlanField.Parent ? r.Parent is not null : !r.Predecessors.IsEmpty), Is.EqualTo(pairs - 1), "Independent pairs continue after one removal fails.");
            Assert.That(session.Document.Sync.Failures.Any(f => f.Identity == "I3" && f.Reason == "NotDispatched"), Is.True, "The reversal dependent on the failed removal is not sent.");
            Scenario();
        }
        var result = await publisher.PublishAsync(session, Today); Assert.That(result.Succeeded, Is.True, result.Error);
        await session.Undo(Today); await session.Redo(Today);
        Scenario("forbid-mutation"); Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        await Reopen();
        foreach (var pair in Enumerable.Range(0, pairs))
        {
            var first = FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I" + (pair * 2 + 1)).Row;
            var second = FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I" + (pair * 2 + 2)).Row;
            Assert.That(relationship == PlanField.Parent ? first.Parent == second.Identity && second.Parent is null : first.Predecessors.SequenceEqual(new[] { second.Identity }) && second.Predecessors.IsEmpty, Is.True);
        }
    }
    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(3, false)]
    public async Task ConcurrentRefreshUsesOnlyACompleteSnapshotWithinThreeAttempts(int changingReads, bool succeeds)
    {
        await Start(101);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local")]), Today);
        var before = PlanJson.Text(session.Document);
        Scenario("churn", changingReads);
        var result = await publisher.RefreshAsync(session, Today);
        Assert.That(result.Succeeded, Is.EqualTo(succeeds), result.Error);
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Local"));
        if (!succeeds) { Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before)); Assert.That(result.Error, Does.Contain("ConcurrentChange")); }
        await Reopen();
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task ExhaustedVerificationKeepsUnverifiedOutcomesUntilReadOnlyRecovery(bool useRefresh)
    {
        await Start(101);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Written")]), Today);
        Scenario("verification-churn");
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Title, Is.EqualTo("Written"));
        Assert.That(session.Document.Sync.Unverified, Is.EqualTo(new[] { "I1" }));
        Assert.That(session.Document.Sync.Failures, Is.Empty);
        Assert.That(session.Document.Baseline.Rows[0].Title, Is.EqualTo("Task 1"));
        await Reopen(); await session.Undo(Today); await session.Redo(Today);
        Assert.That(session.Document.Sync.Unverified, Is.EqualTo(new[] { "I1" }));
        Scenario("forbid-mutation");
        var recovered = useRefresh ? await publisher.RefreshAsync(session, Today) : await publisher.PublishAsync(session, Today);
        Assert.That(recovered.Succeeded, Is.True, recovered.Error);
        Assert.That(session.Document.Sync.Unverified, Is.Empty);
        Assert.That(session.Document.Sync.Publish, Is.Null);
        Assert.That(session.Document.Baseline.Rows[0].Title, Is.EqualTo("Written"));
        await Reopen(); Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Written"));
    }
    [Test]
    public async Task NonConcurrentReadFailureIsNotSilentlyRetried()
    {
        await Start(1); var before = PlanJson.Text(session.Document); Scenario("read-once");
        var result = await publisher.RefreshAsync(session, Today);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Does.Contain("Api")); Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
    }
    [Test]
    public async Task ArchivedParentsNeverOwnPlanOrderConflictsAndThePlanReopens()
    {
        await Start(4, initial => initial with
        {
            Issues = initial.Issues.Select(i => i.Row.Identity == "I1" ? i with { Archived = true } : i with { Row = i.Row with { Parent = "I1" } }).ToImmutableArray(),
            SubOrders = ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("I1", ["I2", "I3", "I4"])
        });
        await session.Execute(new MovePlanRows(["I4"], "I2"), Today);
        var external = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, external with { SubOrders = external.SubOrders.SetItem("I1", ["I3", "I2", "I4"]) });
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error); session = opened.Session!;
        Assert.That(session.Document.Sync.NativeOrders.ContainsKey("I1"), Is.False);
        Assert.That(session.Document.Sync.Conflicts, Is.Empty);
        var published = await publisher.PublishAsync(session, Today);
        Assert.That(published.Succeeded, Is.True, published.Error);
        Assert.That(FakePlanEditor.Load(root).SubOrders["I1"], Is.EqualTo(new[] { "I3", "I2", "I4" }));
    }
    [TestCase(true, null)]
    [TestCase(false, "Done")]
    public async Task CopyingUnavailableTasksResetsReadOnlyIssueStateAndPublishes(bool closed, string? status)
    {
        await Start(1, initial => initial with { Issues = [initial.Issues[0] with { Row = initial.Issues[0].Row with { Closed = closed, CloseDate = closed ? Today : null, Status = status } }] });
        var source = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, source with { Issues = [source.Issues[0] with { Added = false }] });
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        await session.ResolveUnavailable("I1", true, Today);
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows[0].Closed, Is.EqualTo(closed));
        Assert.That(session.Document.State.Rows[0].CloseDate, Is.EqualTo(closed ? Today : (DateOnly?)null));
        Assert.That(session.Document.State.Rows[0].Status, Is.EqualTo(status));
        await session.Redo(Today);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity.StartsWith("local:")).CloseDate, Is.Null);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var copy = FakePlanEditor.Load(root).Issues.Single(i => i.Added).Row;
        Assert.That(copy.Closed, Is.False); Assert.That(copy.Status, Is.Null);
        Assert.That(copy.Title, Is.EqualTo("Task 1"));
    }
    [TestCase("removed-during-write")]
    [TestCase("summary-conflict-during-write")]
    public async Task VerificationBlocksUnavailableTasksButIgnoresExcludedSummaryConflicts(string fault)
    {
        await Start(2);
        if (fault == "summary-conflict-during-write")
        {
            await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Estimate, 8m)]), Today);
            await session.Execute(new IndentPlanRows(["I2"]), Today);
        }
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Published")]), Today);
        Scenario(fault);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        if (fault == "removed-during-write") {
            // #201: an item this publish does not write is not read by it, so its removal during the write
            // is not seen here; it arrives with 最新の情報に更新 as an unavailable row.
            Assert.That(result.Succeeded, Is.True, result.Error);
            Assert.That(session.Document.Sync.Publish, Is.Null);
            var refresh = await publisher.RefreshAsync(session, Today);
            Assert.That(refresh.Succeeded, Is.True, refresh.Error);
            Assert.That(session.Document.Sync.Unavailable, Is.EqualTo(new[] { "I2" }));
        } else {
            Assert.That(result.Succeeded, Is.True, result.Error);
            Assert.That(session.Document.Sync.Conflicts.Single().Field, Is.EqualTo(PlanField.Estimate));
            Assert.That(PlanOperations.BlockingConflicts(session.Document), Is.Empty);
            Assert.That(session.Document.Baseline.Rows.Single(r => r.Identity == "I1").Estimate, Is.EqualTo(9));
            Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I1").Row.Estimate, Is.EqualTo(9));
        }
        Assert.That((await PlanSession.OpenAsync(new(root), Project, Today)).Status, Is.EqualTo(PlanLoadStatus.Loaded));
    }
    [Test]
    public async Task TypedDatesPublishOnlyThroughStartAndEndWhenTheProjectStillHasTheRemovedFields()
    {
        // #268: a Project that still has 開始日指定 and 日程固定 keeps them as ordinary fields the plan never writes.
        await Start(1, fixture: s => s with { ExtraFields = [new("X-snet", "開始日指定", "DATE"), new("X-fixed", "日程固定", "SINGLE_SELECT") { Options = ["固定"] }] });
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Estimate, 8m)]), Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Start, Today.AddDays(2))]), Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.End, Today.AddDays(3))]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        var remote = FakePlanEditor.Load(root).Issues[0].Row;
        Assert.That((remote.Start, remote.End), Is.EqualTo((Today.AddDays(2), Today.AddDays(3))));
        Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Does.Not.Contain("X-snet").And.Not.Contain("X-fixed"));
    }
    [TestCase("In progress")]
    [TestCase("Done")]
    [TestCase(null)]
    public async Task RefreshKeepsTheBuiltInStatusWithoutAPlanningMapping(string? status)
    {
        await Start(1);
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [state.Issues[0] with { Row = state.Issues[0].Row with { Status = status } }] });
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(session.Document.State.Rows[0].Status, Is.EqualTo(status));
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local title")]), Today);
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(session.Document.State.Rows[0].Status, Is.EqualTo(status));
        Assert.That(session.Document.Baseline.Rows[0].Status, Is.EqualTo(status));
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Local title"));
        Assert.That(session.Changes(Today).Fields["I1"], Does.Not.Contain(PlanField.Status));
    }
    [Test]
    public async Task FailedPublishRowsKeepGitHubsReasonAcrossReopen()
    {
        await Start(2);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I1", PlanField.Actual, 1m), new("I2", PlanField.Actual, 1m)]), Today);
        Scenario("partial");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        var reopened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(reopened.Status, Is.EqualTo(PlanLoadStatus.Loaded));
        Assert.That(reopened.Session!.Document.Sync.Failures.Single().Reason, Does.Contain("FORBIDDEN").And.Contain("Synthetic failure"));
    }
    [Test]
    public async Task EffortEditedBeforeIndentDoesNotPreventPublishCompletion()
    {
        await Start(2);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Estimate, 8m)]), Today);
        await session.Execute(new IndentPlanRows(["I2"]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Estimate, Is.Null);
        Assert.That(FakePlanEditor.Load(root).Issues[1].Row.Parent, Is.EqualTo("I1"));
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
    }
    [Test]
    public async Task ParentChildSwapPublishesWithoutAnIntermediateCycle()
    {
        await Start(2);
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = state.Issues.SetItem(1, state.Issues[1] with { Row = state.Issues[1].Row with { Parent = "I1" } }) });
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I1", PlanField.Parent, "I2"), new("I2", PlanField.Parent, null)]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var rows = FakePlanEditor.Load(root).Issues.Select(i => i.Row).ToArray();
        Assert.That(rows[0].Parent, Is.EqualTo("I2")); Assert.That(rows[1].Parent, Is.Null);
    }
    [TestCase("local: setup")]
    [TestCase("item: cleanup")]
    [TestCase("repository: literal")]
    public async Task PlaceholderLikeTitlesRemainLiteral(string title)
    {
        await Start(1);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, title)]), Today);
        await session.Execute(new InsertPlanRows([PlanRow.New(title, "acme/repo")]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Select(i => i.Row.Title), Is.EqualTo(new[] { title, title }));
    }
    [Test]
    public async Task CompleteReadMergesAllPagesBeforeChangingTheCheckpoint()
    {
        await Start(101);
        Assert.That(session.Document.State.Rows, Has.Length.EqualTo(101));
        Assert.That((await PlanSession.OpenAsync(new(root), Project, Today)).Status, Is.EqualTo(PlanLoadStatus.Loaded));
    }
    [Test]
    public async Task FiftyOneUpdatesUseSixBatchesAndUndoAfterPublishStaysLocal()
    {
        await Start(51);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, session.Document.State.Rows.Select(r => new PlanCellChange(r.Identity, PlanField.Actual, 2m)).ToImmutableArray()), Today);
        var start = runner.Count;
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.All(i => i.Row.Actual == 2), Is.True);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(6));
        TestContext.Out.WriteLine("51-update publish processes: " + (runner.Count - start));
        await session.Undo(Today);
        Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(51));
        Assert.That(FakePlanEditor.Load(root).Issues.All(i => i.Row.Actual == 2), Is.True);
        Assert.That((await PlanSession.OpenAsync(new(root), Project, Today)).Status, Is.EqualTo(PlanLoadStatus.Loaded));
    }
    [TestCase("before")]
    [TestCase("after")]
    [TestCase("autoadd")]
    [TestCase("autoadd-after")]
    public async Task CreationRestartReconcilesDispatchedIdentitiesBeforeRetry(string fault)
    {
        await Start(0);
        var row = PlanRow.New("Created", "acme/repo");
        await session.Execute(new InsertPlanRows([row]), Today);
        Scenario(fault);
        var first = await publisher.PublishAsync(session, Today);
        Scenario();
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error);
        session = opened.Session!;
        var resumed = await publisher.PublishAsync(session, Today);
        Assert.That(resumed.Succeeded, Is.True, first.Error + "; " + resumed.Error);
        Assert.That(FakePlanEditor.Load(root).Issues, Has.Length.EqualTo(1));
        Assert.That(session.Document.State.Rows.Single().Identity, Is.EqualTo("I1"));
        Assert.That(session.UndoCount, Is.Zero);
        Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Does.Contain("addProjectV2ItemById"), "Auto-add cannot complete an addition that was never sent.");
    }
    [TestCase("partial")]
    [TestCase("resource")]
    [TestCase("after")]
    public async Task PartialUpdateRestartDoesNotResendSuccessfulAliases(string fault)
    {
        await Start(51);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, session.Document.State.Rows.Select(r => new PlanCellChange(r.Identity, PlanField.Actual, 3m)).ToImmutableArray()), Today);
        Scenario(fault);
        var first = await publisher.PublishAsync(session, Today);
        Assert.That(first.Succeeded, Is.EqualTo(fault == "resource"));
        var pending = fault switch { "resource" => 0, "after" => 11, _ => 1 };
        Assert.That(FakePlanEditor.Load(root).Issues.Count(i => i.Row.Actual != 3), Is.EqualTo(pending),
            "An after-batch failure loses the first response while the other three requests of its window complete, leaving 11 of 51 unsent; partial rejects one alias, while resource rejection recovers.");
        Scenario();
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error);
        session = opened.Session!;
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.All(i => i.Row.Actual == 3), Is.True);
        var lines = File.ReadAllLines(Path.Combine(root, "plan-mutations.jsonl"));
        var sent = lines.SelectMany(line => {
            using var payload = JsonDocument.Parse(line);
            return payload.RootElement.GetProperty("variables").EnumerateObject().Select(v => v.Value.GetProperty("itemId").GetString()).ToArray();
        }).ToArray();
        Assert.That(sent.Count(id => id == "T-I1"), Is.EqualTo(1), "Successful siblings must not be resent.");
        Assert.That(sent.Count(id => id == "T-I2"), Is.EqualTo(fault == "after" ? 1 : 2));
        if (fault == "after") Assert.That(sent, Is.EquivalentTo(Enumerable.Range(1, 51).Select(i => "T-I" + i)),
            "Restart sends every remaining write exactly once without repeating any successful first-batch alias.");
    }
    [Test]
    public async Task VerificationMismatchStaysPendingAndIsResent()
    {
        await Start(1);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Actual, 4m)]), Today);
        Scenario("mismatch");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(session.Document.Sync.Failures.Single().Reason, Is.EqualTo("VerificationMismatch"));
        Scenario();
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        Assert.That(FakePlanEditor.Load(root).Issues.Single().Row.Actual, Is.EqualTo(4));
    }
    [Test]
    public async Task ConflictResolutionSurvivesRestartAndUndoRestoresTheConflict()
    {
        await Start(1);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local")]), Today);
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [state.Issues[0] with { Row = state.Issues[0].Row with { Title = "Remote" } }] });
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        await publisher.RefreshAsync(session, Today);
        Assert.That(session.Document.Sync.Conflicts, Has.Length.EqualTo(1));
        await session.ResolveConflict("I1", PlanField.Title, true, Today);
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Remote"));
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error);
        session = opened.Session!;
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Local"));
        Assert.That(session.Document.Sync.Conflicts, Has.Length.EqualTo(1));
    }
    [Test]
    public async Task ElevenCreationsWithPartialFieldFailureResumeWithoutLosingPublishedRows()
    {
        await Start(0);
        await session.Execute(new InsertPlanRows(Enumerable.Range(1, 11).Select(i => PlanRow.New("Created " + i, "acme/repo") with { Actual = 2 }).ToImmutableArray()), Today);
        Scenario("partial", 5);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Scenario();
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error);
        session = opened.Session!;
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues, Has.Length.EqualTo(11));
        Assert.That(session.Document.State.Rows.All(r => r.Actual == 2 && !r.Identity.StartsWith("local:")), Is.True);
    }
    [Test]
    public async Task FailedReadDoesNotChangeTheCheckpointOrLocalWork()
    {
        await Start(101);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local")]), Today);
        var before = PlanJson.Text(session.Document);
        Scenario("readfailure");
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.False);
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task RemovedTaskRequiresExplicitDispositionAndResolutionIsUndoable(bool copy)
    {
        await Start(1);
        FakePlanEditor.Save(root, new([], 2));
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(session.Document.Sync.Unavailable, Is.EqualTo(new[] { "I1" }));
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        await session.ResolveUnavailable("I1", copy, Today);
        Assert.That(session.Document.State.Rows.Length, Is.EqualTo(copy ? 1 : 0));
        Assert.That(session.Document.Sync.Unavailable, Is.Empty);
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error);
        session = opened.Session!;
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows[0].Identity, Is.EqualTo("I1"));
        Assert.That(session.Document.Sync.Unavailable, Is.EqualTo(new[] { "I1" }));
    }
    [Test]
    public async Task NativeRelationshipsAndProjectOrderAreReadBackAfterPublishing()
    {
        await Start(3);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I2", PlanField.Parent, "I1"), new("I3", PlanField.Parent, "I1"),
            new("I3", PlanField.Predecessors, new[] { "I2" }), new("I2", PlanField.Assignees, new[] { "U1" })]), Today);
        await session.Execute(new MovePlanRows(["I3"], "I2"), Today);
        var published = await publisher.PublishAsync(session, Today);
        Assert.That(published.Succeeded, Is.True, published.Error);
        var issues = FakePlanEditor.Load(root).Issues;
        Assert.That(issues.Select(i => i.Row.Identity), Is.EqualTo(new[] { "I1", "I3", "I2" }));
        Assert.That(issues.Single(i => i.Row.Identity == "I3").Row.Predecessors, Is.EqualTo(new[] { "I2" }));
        Assert.That(issues.Single(i => i.Row.Identity == "I2").Row.Assignees, Is.EqualTo(new[] { "U1" }));
    }
    [Test]
    public async Task ChoosingGitHubDuringResumeRemovesOnlyTheConflictingPendingWrite()
    {
        await Start(2);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I1", PlanField.Actual, 3m), new("I2", PlanField.Actual, 3m)]), Today);
        Scenario("partial");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i.Row.Identity == "I2" ? i with { Row = i.Row with { Actual = 9 } } : i).ToImmutableArray() });
        Scenario();
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        await session.ResolveConflict("I2", PlanField.Actual, true, Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I2").Row.Actual, Is.EqualTo(9));
    }
    [Test]
    public async Task LostUpdateResponseAndFailedVerificationAreReconciledBeforeResumeWithoutLosingUndo()
    {
        await Start(1);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Actual, 5m)]), Today);
        Scenario("after-and-read");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Scenario();
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error); session = opened.Session!;
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(1));
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows[0].Actual, Is.Null);
        Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(1));
    }
    [TestCase("rate")]
    [TestCase("reset")]
    public async Task InterruptedRateLimitWaitIsDurableAndResumeHonorsTheDeadline(string fault)
    {
        await Start(1);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Actual, 7m)]), Today);
        using var cancel = new CancellationTokenSource(); runner.RateLimitObserved = cancel.Cancel;
        Scenario(fault);
        Assert.That((await publisher.PublishAsync(session, Today, cancel.Token)).Succeeded, Is.False);
        var deadline = session.Document.Sync.NotBefore;
        Assert.That(deadline, Is.Not.Null);
        Scenario(); runner.RateLimitObserved = null;
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error); session = opened.Session!;
        var first = runner.Started.Count;
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(runner.Started[first], Is.GreaterThanOrEqualTo(deadline!.Value));
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Actual, Is.EqualTo(7));
    }
    [Test]
    public async Task RejectedCreationCanBeCorrectedWithoutRecreatingSuccessfulSiblings()
    {
        await Start(0);
        var rows = Enumerable.Range(1, 11).Select(i => PlanRow.New(i == 2 ? "Rejected" : "Task " + i, "acme/repo")).ToImmutableArray();
        await session.Execute(new InsertPlanRows(rows), Today);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(FakePlanEditor.Load(root).Issues, Has.Length.EqualTo(9));
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(rows[1].Identity, PlanField.Title, "Corrected")]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues, Has.Length.EqualTo(11));
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows[1].Title, Is.EqualTo("Rejected"));
        Assert.That(session.Document.State.Rows[1].Identity.StartsWith("local:", StringComparison.Ordinal), Is.False);
        await session.Redo(Today);
        Assert.That(session.Document.State.Rows[1].Title, Is.EqualTo("Corrected"));
    }
    [Test]
    public async Task PartialPublishReleasesVerifiedFailuresForLocalUndoWithoutWriting()
    {
        await Start(2);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I1", PlanField.Actual, 3m), new("I2", PlanField.Actual, 3m)]), Today);
        Scenario("partial");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(session.Document.Sync.Failures, Has.Length.EqualTo(1));
        var before = FakePlanEditor.Load(root);
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows.All(r => r.Actual is null), Is.True);
        Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(1));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(before.MutationBatches));
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Actual, Is.EqualTo(3));
    }
    private sealed class CountingRunner(IGhProcessRunner inner) : IGhProcessRunner
    {
        private int active, tickets;
        public int MaximumConcurrentMutations { get; private set; }
        public int MaximumConcurrentNonFieldMutations { get; private set; }
        public List<int> MutationConcurrency { get; } = [];
        public List<DateTimeOffset> MutationStarted { get; } = [];
        public Action? MutationStarting { get; set; }
        public Func<int, CancellationToken, Task>? BeforeMutationResponse { get; set; }
        public int Count { get; private set; }
        public List<string> Queries { get; } = [];
        public List<DateTimeOffset> Started { get; } = [];
        public Action? RateLimitObserved { get; set; }
        public async Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken cancellationToken = default)
        {
            var mutation = command.StandardInput?.Contains("mutation PlanPublish", StringComparison.Ordinal) == true;
            var mutationIndex = -1;
            lock (Queries)
            {
                Count++; Started.Add(DateTimeOffset.UtcNow);
                if (command.Arguments.Contains("graphql") && command.StandardInput is { } input) { using var json = JsonDocument.Parse(input); Queries.Add(json.RootElement.GetProperty("query").GetString()!); }
                if (mutation)
                {
                    // Calls arrive in dispatch order; the ticket lets the fake process them in that order.
                    var payload = System.Text.Json.Nodes.JsonNode.Parse(command.StandardInput!)!.AsObject();
                    payload["orderTicket"] = ++tickets;
                    command = new GhCommand(command.Executable, command.Arguments, payload.ToJsonString(), command.Timeout);
                    var concurrent = Interlocked.Increment(ref active);
                    MaximumConcurrentMutations = Math.Max(MaximumConcurrentMutations, concurrent);
                    MutationConcurrency.Add(concurrent);
                    MutationStarted.Add(DateTimeOffset.UtcNow);
                    mutationIndex = tickets - 1;
                    MutationStarting?.Invoke();
                    if (new[] { "createIssue", "addProjectV2ItemById", "addSubIssue", "removeSubIssue",
                        "reprioritizeSubIssue", "addBlockedBy", "removeBlockedBy", "updateProjectV2ItemPosition" }
                        .Any(name => command.StandardInput!.Contains(":" + name + "(", StringComparison.Ordinal)))
                        MaximumConcurrentNonFieldMutations = Math.Max(MaximumConcurrentNonFieldMutations, concurrent);
                }
            }
            try
            {
                var result = await inner.RunAsync(command, cancellationToken);
                if (mutation && BeforeMutationResponse is { } beforeResponse)
                    await beforeResponse(mutationIndex, cancellationToken);
                if (result.StandardOutput.StartsWith("HTTP/2 429", StringComparison.Ordinal)) RateLimitObserved?.Invoke();
                return result;
            }
            finally
            {
                if (mutation)
                {
                    Interlocked.Decrement(ref active);
                }
            }
        }
    }
}
