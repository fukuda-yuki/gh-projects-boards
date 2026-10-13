using System.Collections.Immutable;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class PlanPublishTests
{
    [TestCase(false), TestCase(true)]
    public void SummaryAssignmentsAndDependenciesNeverProduceWrites(bool editedBeforeBecomingSummary)
    {
        var parent = new PlanRow("P", "Requirement", "acme/repo") { Assignees = ["U1"], Predecessors = ["X"] };
        var child = new PlanRow("C", "Task", "acme/repo") { Parent = "P" };
        var predecessor = new PlanRow("X", "Earlier", "acme/repo");
        var local = editedBeforeBecomingSummary ? parent with { Assignees = ["U2"], Predecessors = [] } : parent;
        var baseline = new PlanBaseline([parent, child, predecessor], []);
        var document = new PlanDocument(new(new("github.com", 42), "P1"), baseline, new([local, child, predecessor], new()));
        var remote = new PlanRemoteSnapshot(baseline, ImmutableDictionary<string, string>.Empty, [], 0, 0);
        var review = PlanPublishPlan.Build(document, remote, new(2026, 10, 5), "test");
        Assert.That(review.Writes, Is.Empty);
        Assert.That(review.Changes, Is.Empty);
        Assert.That(PlanOperations.Changes(document, new(2026, 10, 5)).TaskCount, Is.Zero);
        Assert.That(document.Baseline.Rows[0].Assignees, Is.EqualTo(new[] { "U1" }));
        Assert.That(document.Baseline.Rows[0].Predecessors, Is.EqualTo(new[] { "X" }));
    }

    [TestCase(0)]
    [TestCase(1)]
    public void RemovingChildrenDoesNotInventAnOrderWriteForTheRemainingSingletonOrEmptyParent(int remaining)
    {
        var project = new ScopedId(new("github.com", 42), "P1");
        ImmutableArray<PlanRow> rows = [new("I1", "Parent", "acme/repo"), new("I2", "A", "acme/repo") { Parent = "I1" }, new("I3", "B", "acme/repo") { Parent = "I1" }];
        var baseline = new PlanBaseline(rows, []);
        var orders = ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("I1", ["I3", "I2"]);
        var local = rows.Select((r, i) => i > remaining ? r with { Parent = null } : r).ToImmutableArray();
        var document = new PlanDocument(project, baseline, new(local, new())) { Sync = new() { NativeOrders = orders } };
        var remote = new PlanRemoteSnapshot(baseline, ImmutableDictionary<string, string>.Empty, [], 0, 0) { SubIssueOrders = orders };
        var changes = PlanOperations.Changes(document, new(2026, 10, 5));
        var review = PlanPublishPlan.Build(document, remote, new(2026, 10, 5), Guid.NewGuid().ToString("N"));
        Assert.That(changes.Fields.ContainsKey("I1"), Is.False);
        Assert.That(review.Changes.Any(c => c.Field == PlanField.SubIssueOrder), Is.False);
        Assert.That(review.Writes.Any(w => w.Mutation == "reprioritizeSubIssue"), Is.False);
    }

    [TestCase("I2,I3", "I2,I3", "I3,I2")]
    [TestCase("I3,I2", "I3,I2", "I2,I3")]
    public void DifferingGitHubChildOrdersAreNotAnUnpublishedChange(string projectOrder, string planOrder, string nativeOrder)
    {
        // Issue #167: the Project item order and the native sub-issue order differ before the PMO
        // changes anything, so neither order may be counted, reviewed or published as an edit.
        var document = SiblingPlan(projectOrder, planOrder, nativeOrder);
        var changes = PlanOperations.Changes(document, Day);
        var review = PlanPublishPlan.Build(document, SiblingRemote(document), Day, Guid.NewGuid().ToString("N"));
        Assert.That(changes.TaskCount, Is.Zero);
        Assert.That(review.Changes.Any(c => c.Field == PlanField.SubIssueOrder), Is.False);
        Assert.That(review.Writes.Any(w => w.Mutation == "reprioritizeSubIssue"), Is.False);
    }

    [Test]
    public void MovingAChildRowPublishesTheReorderThatMoveImplies()
    {
        var document = SiblingPlan("I2,I3", "I3,I2", "I2,I3");
        var changes = PlanOperations.Changes(document, Day);
        var review = PlanPublishPlan.Build(document, SiblingRemote(document), Day, Guid.NewGuid().ToString("N"));
        Assert.That(changes.Fields["I1"], Is.EquivalentTo(new[] { PlanField.SubIssueOrder }));
        var change = review.Changes.Single(c => c.Field == PlanField.SubIssueOrder);
        Assert.That((change.Before, change.After), Is.EqualTo((PlanJson.Text(new[] { "I2", "I3" }), PlanJson.Text(new[] { "I3", "I2" }))));
        var writes = review.Writes.Where(w => w.Mutation == "reprioritizeSubIssue").ToArray();
        string Input(PlanWrite write, string key) => System.Text.Json.JsonDocument.Parse(write.Input).RootElement.GetProperty(key).GetString()!;
        Assert.That(writes.Select(w => w.Identity), Is.EqualTo(new[] { "I3", "I2" }));
        Assert.That(Input(writes[0], "beforeId"), Is.EqualTo("I2"));
        Assert.That(Input(writes[1], "afterId"), Is.EqualTo("I3"));
        Assert.That(review.Writes.Any(w => w.Mutation == "updateProjectV2ItemPosition"), Is.True);
    }

    [Test]
    public void MovingAChildRowIntoTheOrderGitHubAlreadyHasSendsNoReorder()
    {
        var document = SiblingPlan("I2,I3", "I3,I2", "I3,I2");
        var changes = PlanOperations.Changes(document, Day);
        var review = PlanPublishPlan.Build(document, SiblingRemote(document), Day, Guid.NewGuid().ToString("N"));
        Assert.That(changes.Fields["I3"], Does.Contain(PlanField.Order));
        Assert.That(review.Changes.Any(c => c.Field == PlanField.SubIssueOrder), Is.False);
        Assert.That(review.Writes.Any(w => w.Mutation == "reprioritizeSubIssue"), Is.False);
    }

    [Test]
    public void AFailedReorderIsRetriedWhileThePlanStillAsksForTheSameOrder()
    {
        // The Project position already carries the move, so only the recorded failed reorder keeps it pending.
        var document = SiblingPlan("I3,I2", "I3,I2", "I2,I3");
        var retry = document with { Sync = document.Sync with { Failures = [new("I3", PlanField.SubIssueOrder, "Forbidden") { Value = PlanJson.Text(new[] { "I3", "I2" }) }] } };
        var change = PlanOperations.SiblingOrderChange(retry, "I1");
        Assert.That(change, Is.Not.Null);
        Assert.That((PlanJson.Text(change!.Before), PlanJson.Text(change.After)), Is.EqualTo((PlanJson.Text(new[] { "I2", "I3" }), PlanJson.Text(new[] { "I3", "I2" }))));
        Assert.That(PlanOperations.Changes(retry, Day).TaskCount, Is.EqualTo(1));
        var review = PlanPublishPlan.Build(retry, SiblingRemote(retry), Day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Writes.Count(w => w.Mutation == "reprioritizeSubIssue"), Is.EqualTo(2));
        Assert.That(review.Changes.Single(c => c.Field == PlanField.SubIssueOrder).Before, Is.EqualTo(PlanJson.Text(new[] { "I2", "I3" })));
    }

    [TestCase("creation"), TestCase("title")]
    public async Task VerifiedUnrelatedPhaseKeepsThePendingSiblingRetry(string phase)
    {
        var document = SiblingPlan("I3,I2", "I3,I2", "I2,I3");
        var failure = new PlanPublishFailure("I3", PlanField.SubIssueOrder, "Forbidden")
            { Value = PlanJson.Text(new[] { "I3", "I2" }) };
        var added = PlanRow.New("Added task", "acme/repo");
        document = document with {
            State = document.State with { Rows = phase == "creation" ? document.State.Rows.Add(added)
                : document.State.Rows.Select(r => r.Identity == "I3" ? r with { Title = "Renamed" } : r).ToImmutableArray() },
            Sync = document.Sync with { Failures = [failure] }
        };
        var remote = SiblingRemote(document);
        var review = PlanPublishPlan.Build(document, remote, Day, Guid.NewGuid().ToString("N"));
        var processed = review.Writes.Where(w => phase == "creation" ? w.Stage is PlanPublishStage.Create or PlanPublishStage.Add
            : w.Mutation == "updateIssue").Select(w => w with { State = PlanWriteState.Succeeded,
                ResultId = w.Stage == PlanPublishStage.Create ? "I4" : w.Stage == PlanPublishStage.Add ? "T4" : "I3" }).ToImmutableArray();
        Assert.That(processed, Has.Length.EqualTo(phase == "creation" ? 2 : 1));
        document = document with { Sync = document.Sync with { Publish = new(Guid.NewGuid().ToString("N"), processed) { DispatchStarted = true } } };
        remote = remote with {
            Baseline = document.Baseline with { Rows = phase == "creation" ? document.Baseline.Rows.Add(added with { Identity = "I4" }) : document.State.Rows },
            Items = ImmutableDictionary<string, string>.Empty.Add("I1", "T1").Add("I2", "T2").Add("I3", "T3").Add("I4", "T4")
        };
        var root = Path.Combine(Path.GetTempPath(), "ghpb-phase-retry-" + Guid.NewGuid().ToString("N"));
        var session = await PlanSession.CreateAsync(new(root), document, Day);
        try
        {
            var saved = await session.AcceptPublished(remote, Day);
            TestContext.Out.WriteLine($"Phase verification save: {saved.Succeeded}; {saved.Error}");
            Assert.That(session.Document.Sync.Failures, Is.EqualTo(new[] { failure }));
            var remaining = PlanPublishPlan.Build(session.Document, remote, Day, Guid.NewGuid().ToString("N"));
            Assert.That(remaining.Writes.Select(w => w.Mutation), Is.EqualTo(new[] { "reprioritizeSubIssue", "reprioritizeSubIssue" }));
            Assert.That(remaining.Changes.Single().After, Is.EqualTo(failure.Value));

            // A verified retry retires its old failures and must not be planned again.
            await session.SaveSync(session.Document.Sync with { Publish = new(Guid.NewGuid().ToString("N"),
                remaining.Writes.Select(w => w with { State = PlanWriteState.Succeeded }).ToImmutableArray()) { DispatchStarted = true } });
            remote = remote with { SubIssueOrders = remote.SubIssueOrders.SetItem("I1", ["I3", "I2"]) };
            await session.AcceptPublished(remote, Day);
            Assert.That(session.Document.Sync.Failures, Is.Empty);
            Assert.That(PlanPublishPlan.Build(session.Document, remote, Day, Guid.NewGuid().ToString("N")).Writes, Is.Empty);
        }
        finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }

    [TestCase(PlanField.Status), TestCase(PlanField.Order), TestCase(PlanField.SubIssueOrder)]
    public async Task VerificationRetiresUnrequestedFailuresWithoutPublishingThem(string obsoleteField)
    {
        var document = SiblingPlan("I3,I2", "I3,I2", "I3,I2");
        var failure = new PlanPublishFailure(obsoleteField == PlanField.Status ? "I1" : "I3", obsoleteField, "Forbidden")
            { Value = obsoleteField == PlanField.SubIssueOrder ? PlanJson.Text(new[] { "I3", "I2" }) : null };
        document = document with {
            State = document.State with { Rows = document.State.Rows.Select(r => r.Identity == "I3" ? r with { Title = "Renamed" }
                : r.Identity == "I1" && obsoleteField == PlanField.Status ? r with { Status = "Done" } : r).ToImmutableArray() },
            Sync = document.Sync with { Failures = [failure] }
        };
        var remote = SiblingRemote(document);
        var review = PlanPublishPlan.Build(document, remote, Day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Writes.Select(w => w.Mutation), Is.EqualTo(new[] { "updateIssue" }),
            "Neither a summary Status nor an already-converged order may be written to clear an old failure.");
        document = document with { Sync = document.Sync with { Publish = new(Guid.NewGuid().ToString("N"),
            review.Writes.Select(w => w with { State = PlanWriteState.Succeeded }).ToImmutableArray()) { DispatchStarted = true } } };
        remote = remote with { Baseline = document.Baseline with {
            Rows = document.Baseline.Rows.Select(r => r.Identity == "I3" ? r with { Title = "Renamed" } : r).ToImmutableArray() } };
        var root = Path.Combine(Path.GetTempPath(), "ghpb-obsolete-failure-" + Guid.NewGuid().ToString("N"));
        var session = await PlanSession.CreateAsync(new(root), document, Day);
        try
        {
            var saved = await session.AcceptPublished(remote, Day);
            TestContext.Out.WriteLine($"Verification save: {saved.Succeeded}; {saved.Error}");
            Assert.That(session.Changes(Day).TaskCount, Is.Zero);
            Assert.That(session.Document.Sync.Failures, Is.Empty, "An inapplicable historical failure must not block completion.");
            Assert.That(PlanPublishPlan.Build(session.Document, remote, Day, "complete").Writes, Is.Empty);
            if (obsoleteField == PlanField.Status)
                Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I1").Status, Is.EqualTo("Done"),
                    "Retiring the failure must preserve the summary's dormant local input.");
        }
        finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }

    [TestCase(false), TestCase(true)]
    public async Task ResetUndoRestoresThePendingSiblingRetryAsOneOperation(bool refreshAfterReset)
    {
        var document = SiblingPlan("I3,I2", "I3,I2", "I2,I3");
        document = document with { Sync = document.Sync with { Failures = [new("I3", PlanField.SubIssueOrder, "Forbidden")
            { Value = PlanJson.Text(new[] { "I3", "I2" }) }] } };
        var remote = SiblingRemote(document);
        var root = Path.Combine(Path.GetTempPath(), "ghpb-reset-retry-" + Guid.NewGuid().ToString("N"));
        var session = await PlanSession.CreateAsync(new(root), document, Day);
        try
        {
            var saved = await session.AcceptRefresh(remote, Day, reset: true);
            TestContext.Out.WriteLine($"Reset save: {saved.Succeeded}; {saved.Error}");
            Assert.That(session.Changes(Day).TaskCount, Is.Zero);
            Assert.That(session.Document.Sync.Failures, Is.Empty);
            Assert.That(PlanPublishPlan.Build(session.Document, remote, Day, "reset").Writes, Is.Empty);
            if (refreshAfterReset) await session.AcceptRefresh(remote, Day);
            await session.Undo(Day);
            var retry = PlanPublishPlan.Build(session.Document, remote, Day, "undo-reset");
            Assert.That(retry.Writes.Select(w => w.Mutation), Is.EqualTo(new[] { "reprioritizeSubIssue", "reprioritizeSubIssue" }));
            Assert.That(retry.Changes.Single().After, Is.EqualTo(PlanJson.Text(new[] { "I3", "I2" })));
            Assert.That(session.Document.Sync.Failures, Is.Empty, "Undo restores the requested order, not a historical publication outcome.");
            await session.Redo(Day);
            Assert.That(session.Changes(Day).TaskCount, Is.Zero);
            Assert.That(PlanPublishPlan.Build(session.Document, remote, Day, "redo-reset").Writes, Is.Empty);
        }
        finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }

    [Test]
    public void AReorderThePlanNoLongerAsksForIsNotRetried()
    {
        // Undoing the move leaves a stale failure, whose recorded order no longer matches the plan.
        var document = SiblingPlan("I3,I2", "I2,I3", "I2,I3");
        var stale = document with { Sync = document.Sync with { Failures = [new("I3", PlanField.SubIssueOrder, "Forbidden") { Value = PlanJson.Text(new[] { "I3", "I2" }) }] } };
        Assert.That(PlanOperations.SiblingOrderChange(stale, "I1"), Is.Null);
        var review = PlanPublishPlan.Build(stale, SiblingRemote(stale), Day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Writes.Any(w => w.Mutation == "reprioritizeSubIssue"), Is.False);
    }

    [Test]
    public void AFailedSiblingRetryRequiresAConflictDecisionBeforePlanningWrites()
    {
        var document = SiblingPlan("I3,I2,I4", "I3,I2,I4", "I2,I3,I4");
        document = document with { Sync = document.Sync with { Failures = [new("I3", PlanField.SubIssueOrder, "Forbidden")
            { Value = PlanJson.Text(new[] { "I3", "I2", "I4" }) }] } };
        var remote = SiblingRemote(document) with { SubIssueOrders = document.Sync.NativeOrders.SetItem("I1", ["I4", "I2", "I3"]) };
        var merged = PlanMerge.NativeOrder(document, PlanMerge.Merge(document, remote.Baseline), remote.SubIssueOrders);
        Assert.That(merged.Sync.Conflicts.SingleOrDefault()?.Field, Is.EqualTo(PlanField.SubIssueOrder));
        Assert.Throws<InvalidOperationException>(() => PlanPublishPlan.Build(merged, remote, Day, "blocked"));
    }

    [TestCase("undo-redo"), TestCase("github"), TestCase("reset"), TestCase("converged"), TestCase("converged-external"), TestCase("changed-plan")]
    public async Task LocalSiblingConflictChoiceRemainsInThePublishPlanAfterBaselineConvergence(string next)
    {
        var document = SiblingPlan("I2,I3,I4", "I3,I2,I4", "I2,I3,I4");
        var remote = SiblingRemote(document) with {
            Baseline = document.Baseline with { Rows = document.State.Rows },
            SubIssueOrders = document.Sync.NativeOrders.SetItem("I1", ["I4", "I2", "I3"])
        };
        var merged = PlanMerge.NativeOrder(document, PlanMerge.Merge(document, remote.Baseline), remote.SubIssueOrders);
        Assert.That(merged.Sync.Conflicts.Single().Field, Is.EqualTo(PlanField.SubIssueOrder));
        var root = Path.Combine(Path.GetTempPath(), "ghpb-order-choice-" + Guid.NewGuid().ToString("N"));
        var session = await PlanSession.CreateAsync(new(root), merged, Day);
        try
        {
            var saved = await session.ResolveConflict("I1", PlanField.SubIssueOrder, false, Day);
            TestContext.Out.WriteLine($"Conflict choice save: {saved.Succeeded}; {saved.Error}");
            Assert.That(session.Document.Sync.Conflicts, Is.Empty);
            var review = PlanPublishPlan.Build(session.Document, remote, Day, "local-choice");
            Assert.That(review.Changes.SingleOrDefault(c => c.Field == PlanField.SubIssueOrder)?.After,
                Is.EqualTo(PlanJson.Text(new[] { "I3", "I2", "I4" })));
            Assert.That(review.Writes.Count(w => w.Mutation == "reprioritizeSubIssue"), Is.EqualTo(3));
            Assert.That(review.Writes.Any(w => w.Mutation == "updateProjectV2ItemPosition"), Is.False);
            if (next == "undo-redo")
            {
                await session.Undo(Day);
                Assert.Throws<InvalidOperationException>(() => PlanPublishPlan.Build(session.Document, remote, Day, "undone"));
                await session.Redo(Day);
                await session.AcceptRefresh(remote, Day);
                Assert.That(PlanPublishPlan.Build(session.Document, remote, Day, "redone").Writes.Count(w => w.Mutation == "reprioritizeSubIssue"), Is.EqualTo(3));
                var changed = remote with { SubIssueOrders = remote.SubIssueOrders.SetItem("I1", ["I2", "I4", "I3"]) };
                await session.AcceptRefresh(changed, Day);
                Assert.That(session.Document.Sync.Conflicts.Single().Field, Is.EqualTo(PlanField.SubIssueOrder));
                Assert.Throws<InvalidOperationException>(() => PlanPublishPlan.Build(session.Document, changed, Day, "changed-again"));
                return;
            }
            if (next == "github")
            {
                await session.Undo(Day);
                await session.ResolveConflict("I1", PlanField.SubIssueOrder, true, Day);
            }
            else if (next == "reset")
            {
                await session.AcceptRefresh(remote, Day, reset: true);
                Assert.That(PlanPublishPlan.Build(session.Document, remote, Day, "reset").Writes, Is.Empty);
                await session.Undo(Day);
                Assert.That(PlanPublishPlan.Build(session.Document, remote, Day, "undo-reset").Writes.Count(w => w.Mutation == "reprioritizeSubIssue"), Is.EqualTo(3));
                await session.Redo(Day);
            }
            else if (next.StartsWith("converged", StringComparison.Ordinal))
            {
                remote = remote with { SubIssueOrders = remote.SubIssueOrders.SetItem("I1", next == "converged-external" ? ["outside", "I3", "I2", "I4"] : ["I3", "I2", "I4"]) };
                await session.AcceptRefresh(remote, Day);
                Assert.That(PlanPublishPlan.Build(session.Document, remote, Day, "observed").Writes, Is.Empty);
                remote = remote with { SubIssueOrders = remote.SubIssueOrders.SetItem("I1", ["I4", "I2", "I3"]) };
                await session.AcceptRefresh(remote, Day);
                Assert.That(session.Document.Sync.Conflicts, Is.Empty, "A completed choice must not create a later conflict.");
            }
            else
            {
                await session.Execute(new MovePlanRows(["I4"], "I3"), Day);
                await session.Execute(new MovePlanRows(["I2"], "I3"), Day);
            }
            Assert.That(PlanPublishPlan.Build(session.Document, remote, Day, "no-reorder").Writes.Any(w => w.Mutation == "reprioritizeSubIssue"), Is.False);
        }
        finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }

    [Test]
    public void ChildrenTheLastReadCouldNotDeliverDoNotInventAChildOrderChange()
    {
        var parent = new PlanRow("I1", "Parent", "acme/repo");
        PlanRow Child(string id) => new(id, id, "acme/repo") { Parent = "I1" };
        var state = new PlanState([parent, Child("X"), Child("I2"), Child("I3")], new());
        var baseline = new PlanBaseline([parent, Child("I2"), Child("I3"), Child("X")], []);
        var document = new PlanDocument(new(new("github.com", 42), "P1"), baseline, state) { Sync = new()
            { Unavailable = ["X"], NativeOrders = ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("I1", ["I3", "I2"]) } };
        Assert.That(PlanOperations.SiblingOrderChange(document, "I1"), Is.Null);
        Assert.That(PlanOperations.Changes(document, Day).Fields.ContainsKey("I1"), Is.False);
    }

    private static readonly DateOnly Day = new(2026, 10, 5);
    private static PlanDocument SiblingPlan(string projectOrder, string planOrder, string nativeOrder)
    {
        var parent = new PlanRow("I1", "Parent", "acme/repo");
        PlanRow Child(string id) => new(id, id, "acme/repo") { Parent = "I1" };
        var baseline = new PlanBaseline([parent, .. projectOrder.Split(',').Select(Child)], []);
        var state = new PlanState([parent, .. planOrder.Split(',').Select(Child)], new());
        return new PlanDocument(new(new("github.com", 42), "P1"), baseline, state) { Sync = new()
            { NativeOrders = ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("I1", nativeOrder.Split(',').ToImmutableArray()) } };
    }
    private static PlanRemoteSnapshot SiblingRemote(PlanDocument document)
        => new(document.Baseline, ImmutableDictionary<string, string>.Empty, [], 0, 0) { SubIssueOrders = document.Sync.NativeOrders };

    [Test]
    public async Task EditingRowsKeepsARecordedReorderFailureAndDropsResolvedRowFailures()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-failure-" + Guid.NewGuid().ToString("N"));
        var project = new ScopedId(new("github.com", 42), "P1");
        var parent = new PlanRow("I1", "Parent", "acme/repo");
        PlanRow Child(string id) => new(id, id, "acme/repo") { Parent = "I1" };
        var rows = new[] { parent, Child("I2"), Child("I3") }.ToImmutableArray();
        var session = await PlanSession.CreateAsync(new(root), new(project, new(rows, []), new(rows, new())), Day);
        try
        {
            await session.SaveSync(session.Document.Sync with { Failures = [
                new("I3", PlanField.SubIssueOrder, "Forbidden") { Value = PlanJson.Text(new[] { "I3", "I2" }) },
                new("I2", PlanField.Actual, "Forbidden")] });
            // Editing a child that carries the reorder failure must not retire the retry anchor.
            await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I3", PlanField.Actual, 4m), new("I2", PlanField.Actual, 4m)]), Day);
            Assert.Multiple(() =>
            {
                Assert.That(session.Document.Sync.Failures.Any(f => f.Field == PlanField.SubIssueOrder), Is.True,
                    "Only the plan's own child order may retire a reorder failure.");
                Assert.That(session.Document.Sync.Failures.Any(f => f.Field == PlanField.Actual), Is.False,
                    "A row failure is dropped once that row is edited.");
            });
        }
        finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }

    [Test]
    public void NativeSiblingOrderIgnoresChildrenOutsideTheCurrentPlanWhenCountingChanges()
    {
        ImmutableArray<PlanRow> rows = [new("P", "Parent", "acme/repo"),
            new("A", "First", "acme/repo") { Parent = "P" }, new("B", "Second", "acme/repo") { Parent = "P" }];
        var document = new PlanDocument(new(new("github.com", 42), "P1"), new(rows, []), new(rows, new())) {
            Sync = new() { NativeOrders = ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("P", ["outside", "A", "B"]) }
        };

        var today = new DateOnly(2026, 10, 5);
        var schedule = PlanOperations.Schedule(document, today);
        Assert.That(PlanOperations.Changes(document, today, schedule).Fields, Is.Empty);
    }

    [TestCase("Done"), TestCase(null)]
    public void StatusReviewUsesOptionIdentityAndVerifiesObservedName(string? status)
    {
        var scope = new ConnectionScope("github.com", 42); var project = new ScopedId(scope, "P1");
        var field = new ProjectFieldDefinition(new(scope, "F-status"), project, "Status", "ProjectV2SingleSelectField", "SINGLE_SELECT", FieldOwner.ProjectItem,
            [new("option-done", "Done")], ValueAvailability.Present);
        var before = new PlanRow("I1", "Task", "acme/repo") { Status = "Todo" };
        var row = before with { Status = status }; var baseline = new PlanBaseline([before], []);
        var settings = new ProjectPlanSettings();
        var remote = new PlanRemoteSnapshot(baseline, ImmutableDictionary<string,string>.Empty, [field], 0, 0);
        var review = PlanPublishPlan.Build(new(project, baseline, new([row], settings)), remote, new(2026,10,5), Guid.NewGuid().ToString("N"));
        var write = review.Writes.Single();
        Assert.That(review.Changes.Single().Field, Is.EqualTo(PlanField.Status));
        Assert.That(write.Stage, Is.EqualTo(PlanPublishStage.Fields));
        Assert.That(write.Mutation, Is.EqualTo(status is null ? "clearProjectV2ItemFieldValue" : "updateProjectV2ItemFieldValue"));
        if (status is not null) Assert.That(write.Input, Does.Contain("option-done"));
        var progress = new PlanPublishProgress(Guid.NewGuid().ToString("N"), review.Writes);
        Assert.That(PlanVerification.Verify(write, progress, remote with { Baseline = new([row], []) }, settings), Is.True);
        Assert.That(PlanVerification.Verify(write, progress, remote, settings), Is.False);
    }
    [Test, SetCulture("th-TH")]
    public void GitHubDatesAndNumbersRemainGregorianAndInvariant()
    {
        var day = new DateOnly(2026, 10, 6); var scope = new ConnectionScope("github.com", 42); var project = new ScopedId(scope, "P1");
        var roles = new[] { PlanField.Start, PlanField.End, PlanField.StartNoEarlierThan, PlanField.Actual };
        var fields = roles.Select(f => new ProjectFieldDefinition(new(scope, "F-" + f), project, f.ToString(), "ProjectV2Field",
            f == PlanField.Actual ? "NUMBER" : "DATE", FieldOwner.ProjectItem, [], ValueAvailability.Present)).ToImmutableArray();
        var settings = new ProjectPlanSettings { Columns = fields.Select((f, i) => new PlanColumnMapping(roles[i], f.Id.NodeId, f.Name, f.DataType)).ToImmutableArray() };
        var before = new PlanRow("I1", "Task", "acme/repo");
        var row = before with { Start = day, End = day, StartNoEarlierThan = day, Fixed = true, Actual = 12.5m };
        var baseline = new PlanBaseline([before], []);
        var review = PlanPublishPlan.Build(new(project, baseline, new([row], settings)), new(baseline, ImmutableDictionary<string, string>.Empty, fields, 0, 0), day, Guid.NewGuid().ToString("N"));
        var verified = new PlanRemoteSnapshot(new([row], []), ImmutableDictionary<string, string>.Empty, fields, 0, 0);
        Assert.That(review.Writes.All(w => PlanVerification.Verify(w, new(Guid.NewGuid().ToString("N"), review.Writes), verified, settings)), Is.True);
        var values = review.Writes.Select(w => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(w.Input)).ToArray();
        Assert.That(values.Where(v => v.GetProperty("value").TryGetProperty("date", out _)).Select(v => v.GetProperty("value").GetProperty("date").GetString()), Is.EqualTo(new[] { "2026-10-06", "2026-10-06", "2026-10-06" }));
        Assert.That(values.Single(v => v.GetProperty("value").TryGetProperty("number", out _)).GetProperty("value").GetProperty("number").GetRawText(), Is.EqualTo("12.5"));
    }
    [Test]
    public void CreationPacingOverlapsRequestTimeAndBoundsEveryRollingMinuteAcrossMixedBatches()
    {
        var origin = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
        ImmutableArray<PlanCreationStart> starts = [];
        Assert.That(PlanCreationPacing.NextStart(starts, 10, origin), Is.EqualTo(origin));
        starts = PlanCreationPacing.Reserve(starts, 10, origin);
        Assert.That(PlanCreationPacing.NextStart(starts, 1, origin.AddSeconds(3)), Is.EqualTo(origin.AddSeconds(10)));
        var all = new List<PlanCreationStart>(starts); var random = new Random(5901); var now = origin.AddSeconds(3);
        for (var i = 0; i < 2000; i++)
        {
            var count = random.Next(1, 11);
            now = PlanCreationPacing.NextStart(starts, count, now);
            Assert.That(all.Where(s => s.Started > now.AddMinutes(-1)).Sum(s => s.Issues) + count, Is.LessThanOrEqualTo(60));
            Assert.That(now, Is.GreaterThanOrEqualTo(all[^1].Started.AddSeconds(all[^1].Issues)));
            starts = PlanCreationPacing.Reserve(starts, count, now);
            // The exact persisted representation, including varying batch sizes, is used after reopen.
            starts = PlanJson.Read<ImmutableArray<PlanCreationStart>>(System.Text.Encoding.UTF8.GetBytes(PlanJson.Text(starts)));
            all.Add(new(now, count)); now = now.AddSeconds(random.Next(0, 15));
        }
    }
    [TestCase(null)]
    [TestCase("I2")]
    public async Task RecordedCreationIdentityCannotBeForgottenOrRebound(string? replacement)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-binding-" + Guid.NewGuid().ToString("N"));
        var day = new DateOnly(2026, 10, 5); var project = new ScopedId(new("github.com", 42), "P1");
        var row = PlanRow.New("Created", "acme/repo");
        var session = await PlanSession.CreateAsync(new(root), new(project, new([], []), new([row], new())), day);
        var write = new PlanWrite("create", row.Identity, PlanPublishStage.Create, "createIssue", "CreateIssueInput",
            PlanJson.Text(new { repositoryId = "R1", title = "Created", body = "marker" }), "issue { id }") { State = PlanWriteState.Succeeded, ResultId = "I1" };
        var attempt = new PlanPublishProgress(Guid.NewGuid().ToString("N"), [write]);
        try
        {
            await session.SaveSync(session.Document.Sync with { Publish = attempt });
            Assert.ThrowsAsync<ArgumentException>(async () => await session.SaveSync(session.Document.Sync with { Publish = attempt with
            { Writes = [write with { ResultId = replacement, State = replacement is null ? PlanWriteState.Pending : PlanWriteState.Succeeded }] } }));
            var reopened = await PlanSession.OpenAsync(new(root), project, day);
            Assert.That(reopened.Status, Is.EqualTo(PlanLoadStatus.Loaded));
            Assert.That(reopened.Session!.Document.Sync.Publish!.Writes.Single().ResultId, Is.EqualTo("I1"));
        }
        finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }
    [TestCase(PlanField.Estimate)]
    [TestCase(PlanField.Remaining)]
    [TestCase(PlanField.Actual)]
    public void SummaryOwnEffortIsNeverAnUnpublishedDifference(string field)
    {
        var day = new DateOnly(2026, 10, 5);
        var parent = new PlanRow("P", "Parent", "acme/repo"); var child = new PlanRow("C", "Child", "acme/repo") { Parent = "P" };
        var edited = PlanValues.Set(parent, field, "8");
        var document = new PlanDocument(new(new("github.com", 42), "P1"), new([parent, child], []), new([edited, child], new()));
        Assert.That(PlanOperations.Changes(document, day).TaskCount, Is.Zero);
    }
    [Test]
    public void LiveBudgetBoundsIssueCreationAndReportsTheWholeRun()
    {
        var budget = PlanPublisherLive.Estimate();
        Assert.That(budget.CreatedIssues, Is.EqualTo(250));
        Assert.That(budget.CreateRequests, Is.EqualTo(25));
        Assert.That(budget.PlannedMutationRequests, Is.EqualTo(480));
        Assert.That(() => PlanPublisherLive.Estimate(100, 3, 67), Throws.InvalidOperationException);
        Assert.That(() => PlanPublisherLive.Estimate(101), Throws.InvalidOperationException);
    }
    [TestCase(1, 1, 7, 7, false, false)]
    [TestCase(1, 1, 7, 7, false)]
    [TestCase(1, 4, 1, 4, false)]
    [TestCase(1, 4, 4, 4, false)]
    [TestCase(1, 4, 7, 4, true)]
    public async Task VerificationMergesUnpublishedFields(int baseline, int local, int remote, int expected, bool conflict, bool verifiedTitle = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-review-" + Guid.NewGuid().ToString("N"));
        var day = new DateOnly(2026, 10, 5); var project = new ScopedId(new("github.com", 42), "P1");
        var row = new PlanRow("I1", "Before", "acme/repo") { Actual = baseline };
        var session = await PlanSession.CreateAsync(new(root), new(project, new([row], []), new([row with { Actual = local }], new())), day);
        try
        {
            await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Published")]), day);
            var write = new PlanWrite("0", "I1", PlanPublishStage.Fields, "updateIssue", "UpdateIssueInput", "{\"id\":\"I1\",\"title\":\"Published\"}", "issue { id }") { State = PlanWriteState.Succeeded };
            await session.SaveSync(session.Document.Sync with { Publish = new(Guid.NewGuid().ToString("N"), [write]) });
            await session.AcceptPublished(new(new([row with { Title = verifiedTitle ? "Published" : "Before", Actual = remote }], []), ImmutableDictionary<string,string>.Empty.Add("I1", "T1"), [], 0, 0), day);
            Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Published"));
            Assert.That(session.Document.State.Rows[0].Actual, Is.EqualTo(expected));
            Assert.That(session.Document.Sync.Conflicts.Any(c => c.Field == PlanField.Actual), Is.EqualTo(conflict));
            await session.Undo(day);
            Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Before"));
            Assert.That(session.Document.State.Rows[0].Actual, Is.EqualTo(expected));
            Assert.That((await PlanSession.OpenAsync(new(root), project, day)).Status, Is.EqualTo(PlanLoadStatus.Loaded));
        }
        finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task DiscoveredRowsSurviveMoveUndoAndReopen(bool refresh)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-review-" + Guid.NewGuid().ToString("N"));
        var day = new DateOnly(2026, 10, 5); var project = new ScopedId(new("github.com", 42), "P1");
        var rows = new[] { "A", "B" }.Select(id => new PlanRow(id, id, "acme/repo")).ToImmutableArray();
        var session = await PlanSession.CreateAsync(new(root), new(project, new(rows, []), new(rows, new())), day);
        try
        {
            await session.Execute(new MovePlanRows(["B"], "A"), day);
            var remote = new PlanRemoteSnapshot(new(rows.Add(new("C", "C", "acme/repo")), []), ImmutableDictionary<string,string>.Empty, [], 0, 0);
            if (refresh) await session.AcceptRefresh(remote, day);
            else { await session.SaveSync(session.Document.Sync with { Publish = new(Guid.NewGuid().ToString("N"), []) }); await session.AcceptPublished(remote, day); }
            var opened = await PlanSession.OpenAsync(new(root), project, day);
            Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error);
            session = opened.Session!;
            await session.Undo(day);
            Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "A", "B", "C" }));
            Assert.That((await PlanSession.OpenAsync(new(root), project, day)).Status, Is.EqualTo(PlanLoadStatus.Loaded));
            await session.Redo(day);
            Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "B", "A", "C" }));
        }
        finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }
    [Test]
    public void ReviewIncludesCalculatedSummaryDatesButNeverRolledUpEffort()
    {
        var day = new DateOnly(2026, 10, 5);
        var scope = new ConnectionScope("github.com", 42); var project = new ScopedId(scope, "P1");
        var parent = new PlanRow("P", "Parent", "acme/repo") { Estimate = 99, Remaining = 99 };
        var child = new PlanRow("C", "Child", "acme/repo") { Parent = "P", Estimate = 8, Remaining = 8 };
        var mappings = new[] { PlanField.Estimate, PlanField.Remaining, PlanField.Start, PlanField.End }
            .Select(f => new PlanColumnMapping(f, "F" + f, f.ToString(), f is PlanField.Start or PlanField.End ? "DATE" : "NUMBER")).ToImmutableArray();
        var settings = new ProjectPlanSettings { StatusDate = day, Columns = mappings };
        var baseline = new PlanBaseline([parent, child], mappings.Select(m => new PlanColumnDefinition(m.FieldId, m.Name, m.DataType)).ToImmutableArray());
        var document = new PlanDocument(project, baseline, new(baseline.Rows, settings));
        var remote = new PlanRemoteSnapshot(baseline, ImmutableDictionary<string, string>.Empty.Add("P", "TP").Add("C", "TC"),
            mappings.Select(m => new ProjectFieldDefinition(new(scope, m.FieldId), project, m.Name, "ProjectV2Field", m.DataType, FieldOwner.ProjectItem, [], ValueAvailability.Present)).ToImmutableArray(), 0, 0) { SubIssueOrders = ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("P", ["C"]) };
        var plan = PlanPublishPlan.Build(document, remote, day, "run");
        Assert.That(plan.Changes.Where(c => c.Identity == "P").Select(c => c.Field), Is.EquivalentTo(new[] { PlanField.Start, PlanField.End }));
        Assert.That(plan.Writes, Has.Length.EqualTo(4));
        Assert.That(plan.Changes.All(c => c.After == "\"2026-10-05\""), Is.True);
    }
    [TestCase(PlanPublishStage.Fields, 49, 5, "number")]
    [TestCase(PlanPublishStage.Fields, 50, 5, "number")]
    [TestCase(PlanPublishStage.Fields, 51, 6, "number")]
    [TestCase(PlanPublishStage.Fields, 51, 6, "date")]
    [TestCase(PlanPublishStage.Fields, 11, 2, "mixed")]
    [TestCase(PlanPublishStage.Create, 9, 1, "")]
    [TestCase(PlanPublishStage.Create, 10, 1, "")]
    [TestCase(PlanPublishStage.Create, 11, 2, "")]
    [TestCase(PlanPublishStage.Add, 11, 2, "")]
    public void BatchesRespectThePublishedLimits(PlanPublishStage stage, int count, int batches, string values)
    {
        var writes = Enumerable.Range(0, count).Select(i => new PlanWrite(i.ToString(), "I" + i, stage,
            stage == PlanPublishStage.Fields ? "updateProjectV2ItemFieldValue" : stage == PlanPublishStage.Create ? "createIssue" : "addProjectV2ItemById", "Input",
            stage != PlanPublishStage.Fields ? "{}" : values == "date" ? "{\"value\":{\"date\":\"2026-10-07\"}}" :
            values == "mixed" && i % 2 == 1 ? "{\"value\":{\"singleSelectOptionId\":\"done\"}}" : "{\"value\":{\"number\":8}}", "id")).ToArray();
        var actual = PlanPublishPlan.Batches(writes).ToArray();
        Assert.That(actual.Length, Is.EqualTo(batches));
        Assert.That(actual.SelectMany(b => b), Is.EqualTo(writes));
    }
    [Test]
    public void MixedAndNumericDateRunsUseAtMostTenAliasesWithoutReordering()
    {
        var writes = Enumerable.Range(0, 135).Select(i => new PlanWrite(i.ToString(), "I" + i, PlanPublishStage.Fields,
            i is 0 or 121 ? "updateIssue" : "updateProjectV2ItemFieldValue", "Input",
            i is 0 or 121 ? "{\"id\":\"I1\",\"assigneeIds\":[\"U1\"]}" : "{\"value\":{\"number\":8}}", "id")).ToArray();
        var batches = PlanPublishPlan.Batches(writes).ToArray();
        Assert.That(batches.SelectMany(b => b), Is.EqualTo(writes), "Writes retain their planned order and occur exactly once.");
        Assert.That(batches.Length, Is.EqualTo(14));
        foreach (var batch in batches) Assert.That(batch.Length,
            Is.LessThanOrEqualTo(10));
    }
}
