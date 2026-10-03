using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class HistoricalDispositionTests
{
    [Test]
    public async Task LegacyCheckpointWithoutDecisionsKeepsUncertaintyWhenMigrated()
    {
        var (h, _) = await UncertainHistory("superseded-existing");
        var record = h.Session.Workspace.Snapshot() with { Version = 13, HistoricalDispositions = null };
        var store = new DraftStore(h.Existing.Root);
        await File.WriteAllTextAsync(store.FileFor(record.Scope), JsonSerializer.Serialize(record));
        await h.Restart();
        Assert.That(h.Session.Workspace.HasUnresolvedApply, Is.True);
        Assert.That(h.Session.Workspace.HistoricalDispositions, Is.Empty);
        var migrated = h.Session.Workspace.Snapshot();
        Assert.That(migrated.Version, Is.EqualTo(14));
        Assert.That(migrated.HistoricalDispositions, Is.Empty);
        Assert.That(JsonSerializer.Serialize(migrated.Journal), Is.EqualTo(JsonSerializer.Serialize(record.Journal)));
        await store.SaveAsync(migrated, record.Revision);
        Assert.That((await store.LoadAsync(record.Scope))!.Version, Is.EqualTo(14));
    }

    [TestCase("missing-metadata"), TestCase("unversioned"), TestCase("wrong-scope"), TestCase("wrong-target"),
        TestCase("unknown-choice"), TestCase("unsupported-absence"), TestCase("duplicate-choice")]
    public async Task MalformedDecisionCannotBecomeAnUnregisterWaiver(string defect)
    {
        var (h, target) = await UncertainHistory("superseded-existing");
        await h.Workspace.PrepareHistoricalFieldAsync(target);
        var result = await h.Workspace.ConfirmHistoricalFieldAsync(h.Workspace.HistoricalFieldReview!, HistoricalFieldDecisionKind.AcceptCurrent);
        Assert.That(result.AllLocalWorkSaved, Is.True, result.Problem);
        var record = h.Session.Workspace.Snapshot(); var decision = record.HistoricalDispositions!.Single();
        record = defect switch {
            "missing-metadata" => record with { HistoricalDispositions = null },
            "unversioned" => record with { Version = 13 },
            "wrong-scope" => record with { HistoricalDispositions = [decision with { Observation = decision.Observation with { Project = new(new("github.com", 99), "P1") } }] },
            "wrong-target" => record with { HistoricalDispositions = [decision with { Target = target with { OperationId = "unrelated" } }] },
            "unknown-choice" => record with { HistoricalDispositions = [decision with { Kind = (HistoricalFieldDecisionKind)99 }] },
            "unsupported-absence" => record with { HistoricalDispositions = [decision with { Kind = HistoricalFieldDecisionKind.FieldNotApplicable,
                Observation = decision.Observation with { Kind = HistoricalFieldEvidenceKind.ProjectFieldAbsent, Current = null } }] },
            "duplicate-choice" => record with { HistoricalDispositions = [decision, decision] },
            _ => throw new ArgumentException(defect)
        };
        await RejectCheckpointWithoutRewriting(h, record);
    }

    [Test]
    public async Task SnapshotOwnsHistoricalDefinitionAndOptionArrays()
    {
        var (h, target) = await UncertainHistory("completed-retired");
        await h.Workspace.PrepareHistoricalFieldAsync(target);
        var result = await h.Workspace.ConfirmHistoricalFieldAsync(h.Workspace.HistoricalFieldReview!, HistoricalFieldDecisionKind.AcceptCurrent);
        Assert.That(result.AllLocalWorkSaved, Is.True, result.Problem);
        var before = JsonSerializer.Serialize(h.Session.Workspace.HistoricalDispositions);
        var copy = h.Session.Workspace.Snapshot(); var observation = copy.HistoricalDispositions!.Single().Observation;
        observation.ProjectFieldIds[0] = "unrelated";
        observation.Current!.Options[0] = new("unrelated", "unrelated");
        Assert.That(JsonSerializer.Serialize(h.Session.Workspace.HistoricalDispositions), Is.EqualTo(before));
        await h.Restart();
        Assert.That(JsonSerializer.Serialize(h.Session.Workspace.HistoricalDispositions), Is.EqualTo(before));
    }

    [TestCase(false), TestCase(true)]
    public async Task UnlinkedSuccessDoesNotSettleHistoricalHandling(bool differentField)
    {
        var (h, target) = await UncertainHistory("superseded-existing");
        h.Existing.Titles["I1"] = "Issue 1";
        var item = differentField ? "P1-T2" : "P1-T1";
        var cell = h.Session.Workspace.Open(h.Workspace.Selected!).Single(r => r.ItemId == item).Cells[0];
        h.Session.Workspace.Commit("P1", cell, "Separately confirmed work");
        await h.Workspace.PrepareHistoricalFieldAsync(target);
        var result = await h.Workspace.ConfirmHistoricalFieldAsync(h.Workspace.HistoricalFieldReview!, HistoricalFieldDecisionKind.ContinueChange);
        Assert.That(result.AllLocalWorkSaved, Is.True, result.Problem);
        await h.Apply(item);
        await h.Restart();
        Assert.That(h.Session.Workspace.Journal.Last().Operations.Single().State, Is.EqualTo(ApplyState.Succeeded));
        Assert.That(h.Session.Workspace.HasUnresolvedApply, Is.True);
        Assert.That(h.Session.Workspace.HistoricalDispositions.Single().FollowUp, Is.Null);
        if (differentField)
        {
            var record = h.Session.Workspace.Snapshot(); var batch = record.Journal!.Last();
            await RejectCheckpointWithoutRewriting(h, record with { HistoricalDispositions = [record.HistoricalDispositions!.Single() with
                { FollowUp = new(batch.Id, batch.Operations.Single().Id) }] });
        }
    }

    [Test]
    public async Task CompletingOneHistoricalFieldKeepsAnotherUncertaintyBlockingUnregister()
    {
        var (h, target) = await UncertainHistory("superseded-existing");
        var cell = h.Session.Workspace.Open(h.Workspace.Selected!).Single(r => r.ItemId == "P1-T2").Cells[0];
        h.Session.Workspace.Commit("P1", cell, "Other uncertain title");
        h.Existing.LoseResponse = true;
        await h.Apply("P1-T2");
        h.Existing.LoseResponse = false;
        var other = h.Session.Workspace.Journal.Last();
        await h.Workspace.SupersedeApplyAsync(other.Id);
        await h.Workspace.PrepareHistoricalFieldAsync(target);
        var result = await h.Workspace.ConfirmHistoricalFieldAsync(h.Workspace.HistoricalFieldReview!, HistoricalFieldDecisionKind.AcceptCurrent);
        Assert.That(result.AllLocalWorkSaved, Is.True, result.Problem);
        await h.Restart();
        Assert.That(h.Session.Workspace.HasUnresolvedApply, Is.True);
        Assert.That(ApplyResultsPresentation.Attention(h.Session.Workspace).Single().OperationId, Is.EqualTo(other.Operations.Single().Id));
        await h.Workspace.UnregisterAsync(retainDrafts: true);
        Assert.That(h.Workspace.Selected, Is.Not.Null);
    }

    [TestCase("unknown"), TestCase("failed"), TestCase("cancelled"), TestCase("superseded")]
    public async Task LinkedUnfinishedOutcomeDoesNotSettleOriginalHandling(string state)
    {
        var (h, target) = await UncertainHistory("superseded-existing");
        h.Existing.Titles["I1"] = "Issue 1";
        await h.Workspace.PrepareHistoricalFieldAsync(target);
        var result = await h.Workspace.ConfirmHistoricalFieldAsync(h.Workspace.HistoricalFieldReview!, HistoricalFieldDecisionKind.ContinueChange);
        await h.Workspace.PrepareHistoricalFollowUpAsync(result.Decision!.Id, new HashSet<string> { "P1-T1" });
        var review = h.Workspace.ApplyReview!;
        Assert.That(await h.Session.CommitAsync(w => { w.ConfirmApply(review); return w; }, () => true), Is.True);
        if (state == "superseded") await h.Workspace.SupersedeApplyAsync(review.Batch.Id);
        else if (state == "cancelled")
            Assert.That(await h.Session.CommitAsync(w => { w.RecordApply(review.Batch.Id,
                review.Batch.Operations.Single() with { State = ApplyState.Cancelled }); return w; }, () => true), Is.True);
        else
        {
            h.Existing.LoseResponse = state == "unknown";
            if (state == "failed") h.Existing.MutationResult = (_, _) => ScriptedRunner.Http("{}", 403);
            await h.Workspace.ResumeApplyAsync(review.Batch.Id);
        }
        await h.Restart();
        Assert.That(h.Session.Workspace.Journal.Last().Operations.Single().State.ToString().ToLowerInvariant(), Is.EqualTo(state));
        Assert.That(h.Session.Workspace.HasUnresolvedApply, Is.True);
        Assert.That(HistoricalFieldHandling.IsSettled(h.Session.Workspace.Journal, h.Session.Workspace.HistoricalDispositions, target), Is.False);
    }

    [Test]
    public async Task EarlierSuccessAfterALaterChoiceDoesNotReplaceThatChoiceOnRestore()
    {
        var (h, target) = await UncertainHistory("superseded-existing");
        h.Existing.Titles["I1"] = "Issue 1";
        await h.Workspace.PrepareHistoricalFieldAsync(target);
        var first = await h.Workspace.ConfirmHistoricalFieldAsync(h.Workspace.HistoricalFieldReview!, HistoricalFieldDecisionKind.ContinueChange);
        await h.Workspace.PrepareHistoricalFollowUpAsync(first.Decision!.Id, new HashSet<string> { "P1-T1" });
        var review = h.Workspace.ApplyReview!;
        Assert.That(await h.Session.CommitAsync(w => { w.ConfirmApply(review); return w; }, () => true), Is.True);
        await h.Workspace.PrepareHistoricalFieldAsync(target);
        var later = await h.Workspace.ConfirmHistoricalFieldAsync(h.Workspace.HistoricalFieldReview!, HistoricalFieldDecisionKind.ContinueChange);
        Assert.That(later.AllLocalWorkSaved, Is.True, later.Problem);
        await h.Workspace.ResumeApplyAsync(review.Batch.Id);
        await h.Restart();
        Assert.That(h.Session.Workspace.Journal.Last().Operations.Single().State, Is.EqualTo(ApplyState.Succeeded));
        Assert.That(h.Session.Workspace.HistoricalDispositions.Last().Id, Is.EqualTo(later.Decision!.Id));
        Assert.That(h.Session.Workspace.HasUnresolvedApply, Is.True);
    }

    [Test]
    public async Task DurableDecisionIsNotResubmittedWhenIndependentInputSaveFails()
    {
        var (h, target) = await UncertainHistory("superseded-existing");
        await h.Workspace.PrepareHistoricalFieldAsync(target);
        var review = h.Workspace.HistoricalFieldReview!;
        FileStream? lockFile = null;
        void IndependentInput()
        {
            if (h.Session.Workspace.HistoricalDispositions.Count == 0 || lockFile is not null) return;
            lockFile = new FileStream(Path.Combine(h.Existing.Root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var cell = h.Session.Workspace.Open(h.Workspace.Selected!).Single(r => r.ItemId == "P1-T2").Cells[0];
            h.Session.Workspace.SetBuffer(cell, "Input after the decision checkpoint");
        }
        h.Session.Changed += IndependentInput;
        HistoricalFieldDecisionResult result;
        try { result = await h.Workspace.ConfirmHistoricalFieldAsync(review, HistoricalFieldDecisionKind.AcceptCurrent); }
        finally { h.Session.Changed -= IndependentInput; lockFile?.Dispose(); }
        Assert.That(result.Decision, Is.Not.Null, result.Problem);
        Assert.That(result.AllLocalWorkSaved, Is.False);
        Assert.That(h.Session.Workspace.HistoricalDispositions, Has.Count.EqualTo(1));
        Assert.That(await h.Workspace.FlushDraftsAsync(), Is.True);
        await h.Restart();
        Assert.That(h.Session.Workspace.HistoricalDispositions.Single().Id, Is.EqualTo(result.Decision!.Id));
        Assert.That(h.Session.Workspace.Fields.Single(f => f.Key.Kind == "Title" && f.Key.NodeId == "I2").Buffer,
            Is.EqualTo("Input after the decision checkpoint"));
    }

    [TestCase("observation-before-dispatch"), TestCase("verification-before-approval"), TestCase("decision-after-success")]
    public async Task RestoreRejectsContradictoryHistoricalEvidenceWithoutRewritingIt(string defect)
    {
        var (h, target) = await UncertainHistory("superseded-existing");
        h.Existing.Titles["I1"] = "Issue 1";
        await h.Workspace.PrepareHistoricalFieldAsync(target);
        var result = await h.Workspace.ConfirmHistoricalFieldAsync(h.Workspace.HistoricalFieldReview!,
            defect == "observation-before-dispatch" ? HistoricalFieldDecisionKind.AcceptCurrent : HistoricalFieldDecisionKind.ContinueChange);
        Assert.That(result.AllLocalWorkSaved, Is.True, result.Problem);
        if (defect != "observation-before-dispatch")
        {
            await h.Workspace.PrepareHistoricalFollowUpAsync(result.Decision!.Id, new HashSet<string> { "P1-T1" });
            await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
            Assert.That(h.Session.Workspace.HasUnresolvedApply, Is.False, h.Workspace.Status);
        }
        var record = h.Session.Workspace.Snapshot();
        var decision = record.HistoricalDispositions!.Single();
        if (defect == "observation-before-dispatch")
        {
            var at = record.Journal!.Single().Operations.Single().Attempts.Last().At.AddMinutes(-1);
            record = record with { HistoricalDispositions = [decision with { Observation = decision.Observation with
                { At = at, Current = decision.Observation.Current! with { At = at } } }] };
        }
        if (defect == "verification-before-approval")
            record = record with { Journal = record.Journal!.Select(b => b.Id != decision.FollowUp!.BatchId ? b : b with
                { Operations = [b.Operations.Single() with { Verification = b.Operations.Single().Verification! with { At = b.ReviewedAt.AddMinutes(-1) } }] }).ToArray() };
        if (defect == "decision-after-success")
        {
            var at = record.Journal!.Last().Operations.Single().Verification!.At.AddSeconds(1);
            record = record with { Revision = record.Revision + 1, HistoricalDispositions = [decision,
                decision with { Id = "impossible-later-choice", Revision = record.Revision + 1, At = at, FollowUp = null,
                    Observation = decision.Observation with { At = at, Current = decision.Observation.Current! with { At = at } } }] };
        }
        await RejectCheckpointWithoutRewriting(h, record);
    }

    private static async Task RejectCheckpointWithoutRewriting(CreationHarness h, DraftRecord record)
    {
        var store = new DraftStore(h.Existing.Root); var path = store.FileFor(record.Scope);
        var raw = JsonSerializer.Serialize(record);
        await File.WriteAllTextAsync(path, raw);
        Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(record.Scope));
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(raw));
    }

    [TestCase("absent"), TestCase("old-option-removed"), TestCase("empty")]
    public async Task ExactRetiredFieldObservationDistinguishesAbsenceFromKnownValues(string state)
    {
        var (h, target) = await UncertainHistory("completed-retired");
        if (state == "absent") h.MissingPlanningField = "P1-status";
        if (state == "empty") h.Existing.Selects["item-created1"] = null;
        if (state == "old-option-removed") h.Existing.ChangeResponse = (query, response) => {
            if (!query.Contains("ProjectFields")) return;
            var options = response["data"]!["node"]!["fields"]!["nodes"]![0]!["options"]!.AsArray();
            foreach (var option in options.Where(o => o!["id"]!.GetValue<string>() == "done").ToArray()) options.Remove(option);
        };
        var before = JsonSerializer.Serialize(h.Session.Workspace.Journal);
        await h.Workspace.PrepareHistoricalFieldAsync(target);
        var review = h.Workspace.HistoricalFieldReview!;
        Assert.That(review, Is.Not.Null, h.Workspace.Status);
        Assert.That(review.Observation.Kind, Is.EqualTo(state == "absent" ? HistoricalFieldEvidenceKind.ProjectFieldAbsent : HistoricalFieldEvidenceKind.CurrentValue));
        if (state != "absent") Assert.That(review.Observation.Current!.Availability,
            Is.EqualTo(state == "empty" ? ValueAvailability.Empty : ValueAvailability.Present));
        var result = await h.Workspace.ConfirmHistoricalFieldAsync(review, state == "absent"
            ? HistoricalFieldDecisionKind.FieldNotApplicable : HistoricalFieldDecisionKind.AcceptCurrent);
        Assert.That(result.AllLocalWorkSaved, Is.True, result.Problem);
        await h.Restart();
        Assert.That(h.Session.Workspace.HasUnresolvedApply, Is.False);
        Assert.That(ApplyResultsPresentation.Attention(h.Session.Workspace), Is.Empty);
        Assert.That(JsonSerializer.Serialize(h.Session.Workspace.Journal), Is.EqualTo(before));
    }

    [TestCase("project"), TestCase("item"), TestCase("content"), TestCase("viewer"), TestCase("denied"), TestCase("partial")]
    public async Task UnverifiedTargetsNeverProduceDispositionAuthority(string defect)
    {
        var (h, target) = await UncertainHistory("superseded-existing");
        var store = new DraftStore(h.Existing.Root); var scope = h.Session.Workspace.Scope;
        var before = await File.ReadAllBytesAsync(store.FileFor(scope));
        var writes = h.Writes.Count;
        h.Existing.Boundary.ChangeCombinedResponse = response => {
            var data = response["data"]!;
            if (defect == "project" || defect == "item") data[defect] = null;
            if (defect == "content") data["item"]!["content"]!["id"] = "foreign-issue";
            if (defect == "viewer") data["viewer"]!["databaseId"] = 99;
            if (defect == "denied") data["item"]!["content"]!["viewerCanUpdate"] = false;
            if (defect == "partial") response["errors"] = new JsonArray(new JsonObject { ["type"] = "INTERNAL", ["message"] = "partial synthetic response" });
        };
        await h.Workspace.PrepareHistoricalFieldAsync(target);
        Assert.Multiple(() => {
            Assert.That(h.Workspace.HistoricalFieldReview, Is.Null);
            Assert.That(h.Session.Workspace.HistoricalDispositions, Is.Empty);
            Assert.That(h.Session.Workspace.HasUnresolvedApply, Is.True);
            Assert.That(h.Writes.Count, Is.EqualTo(writes));
        });
        Assert.That(await File.ReadAllBytesAsync(store.FileFor(scope)), Is.EqualTo(before));
    }

    [Test]
    public async Task ChangedSecondObservationRequiresAnotherExplicitDecision()
    {
        var (h, target) = await UncertainHistory("superseded-existing");
        await h.Workspace.PrepareHistoricalFieldAsync(target);
        var first = h.Workspace.HistoricalFieldReview!;
        h.Existing.Titles["I1"] = "Changed after preview";
        var result = await h.Workspace.ConfirmHistoricalFieldAsync(first, HistoricalFieldDecisionKind.AcceptCurrent);
        Assert.Multiple(() => {
            Assert.That(result.Decision, Is.Null);
            Assert.That(result.Review!.Observation.Current!.Value, Is.EqualTo("Changed after preview"));
            Assert.That(h.Session.Workspace.HasUnresolvedApply, Is.True);
            Assert.That(h.Session.Workspace.HistoricalDispositions, Is.Empty);
        });
        var repeatedOld = await h.Workspace.ConfirmHistoricalFieldAsync(first, HistoricalFieldDecisionKind.AcceptCurrent);
        Assert.That(repeatedOld.Decision, Is.Null);
        var accepted = await h.Workspace.ConfirmHistoricalFieldAsync(result.Review!, HistoricalFieldDecisionKind.AcceptCurrent);
        Assert.That(accepted.AllLocalWorkSaved, Is.True, accepted.Problem);
    }

    [Test]
    public async Task FailedDecisionSaveRetainsCandidateAndOriginalCheckpointUntilRetry()
    {
        var (h, target) = await UncertainHistory("superseded-existing");
        await h.Workspace.PrepareHistoricalFieldAsync(target);
        var review = h.Workspace.HistoricalFieldReview!;
        var store = new DraftStore(h.Existing.Root);
        var before = await File.ReadAllBytesAsync(store.FileFor(h.Session.Workspace.Scope));
        using (var writer = new FileStream(Path.Combine(h.Existing.Root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await h.Workspace.ConfirmHistoricalFieldAsync(review, HistoricalFieldDecisionKind.AcceptCurrent);
            Assert.That(result.Decision, Is.Null);
            Assert.That(result.Review, Is.SameAs(review));
            Assert.That(h.Session.Workspace.HasUnresolvedApply, Is.True);
            Assert.That(await File.ReadAllBytesAsync(store.FileFor(h.Session.Workspace.Scope)), Is.EqualTo(before));
        }
        var saved = await h.Workspace.ConfirmHistoricalFieldAsync(review, HistoricalFieldDecisionKind.AcceptCurrent);
        Assert.That(saved.AllLocalWorkSaved, Is.True, saved.Problem);
        await h.Restart();
        Assert.That(h.Session.Workspace.HistoricalDispositions, Has.Count.EqualTo(1));
    }

    [TestCase(false), TestCase(true)]
    public async Task ContinuedHandlingRemainsOpenWithoutAnExplicitLinkedApproval(bool noCurrentChange)
    {
        var (h, target) = await UncertainHistory("superseded-existing");
        h.Existing.Titles["I1"] = "Issue 1";
        var cell = h.Session.Workspace.Open(h.Workspace.Selected!).Single(r => r.ItemId == "P1-T1").Cells[0];
        h.Session.Workspace.Commit("P1", cell, noCurrentChange ? "Issue 1" : "Fresh unapproved change");
        await h.Workspace.PrepareHistoricalFieldAsync(target);
        var decision = await h.Workspace.ConfirmHistoricalFieldAsync(h.Workspace.HistoricalFieldReview!, HistoricalFieldDecisionKind.ContinueChange);
        var writes = h.Writes.Count;
        await h.Workspace.PrepareHistoricalFollowUpAsync(decision.Decision!.Id, new HashSet<string> { "P1-T1" });
        if (noCurrentChange)
            Assert.That(h.Workspace.ApplyBlockReason(h.Workspace.ApplyReview), Does.Contain("確定済み変更がありません"));
        await h.Restart();
        Assert.Multiple(() => {
            Assert.That(h.Session.Workspace.HistoricalDispositions.Single().FollowUp, Is.Null);
            Assert.That(h.Session.Workspace.HasUnresolvedApply, Is.True);
            Assert.That(h.Writes.Count, Is.EqualTo(writes));
        });
    }

    [Test]
    public async Task ContinueUsesCurrentCommittedIntentAndLinksOnlyItsNewVerifiedOperation()
    {
        var (h, target) = await UncertainHistory("superseded-existing");
        h.Existing.Titles["I1"] = "Issue 1";
        var cell = h.Session.Workspace.Open(h.Workspace.Selected!).Single(r => r.ItemId == "P1-T1").Cells[0];
        h.Session.Workspace.Commit("P1", cell, "New deliberately different intent");
        h.Session.Workspace.SetBuffer(cell, "Do not publish this unfinished text");
        await h.Workspace.PrepareHistoricalFieldAsync(target);
        var decision = await h.Workspace.ConfirmHistoricalFieldAsync(h.Workspace.HistoricalFieldReview!, HistoricalFieldDecisionKind.ContinueChange);
        Assert.That(decision.AllLocalWorkSaved, Is.True, decision.Problem);
        var original = JsonSerializer.Serialize(h.Session.Workspace.Journal.Single());
        var initialWrites = h.Writes.Count;

        await h.Workspace.PrepareHistoricalFollowUpAsync(decision.Decision!.Id, new HashSet<string> { "P1-T1" });

        var review = h.Workspace.ApplyReview!;
        Assert.That(review.Batch.Operations.Single().Intended.Value, Is.EqualTo("New deliberately different intent"));
        Assert.That(h.Writes.Count, Is.EqualTo(initialWrites), "Preparing a continuation is not approval.");
        await h.Workspace.ConfirmApplyAsync(review);
        await h.Restart();
        var saved = h.Session.Workspace.HistoricalDispositions.Single();
        Assert.That(saved.FollowUp, Is.EqualTo(new HistoricalFieldFollowUp(review.Batch.Id, review.Batch.Operations.Single().Id)));
        Assert.Multiple(() => {
            Assert.That(h.Session.Workspace.HasUnresolvedApply, Is.False);
            Assert.That(h.Session.Workspace.Fields.Single(f => f.Key.Kind == "Title" && f.Key.NodeId == "I1").Buffer,
                Is.EqualTo("Do not publish this unfinished text"));
            Assert.That(JsonSerializer.Serialize(h.Session.Workspace.Journal.First()), Is.EqualTo(original));
            Assert.That(h.Writes.Last().Input.GetProperty("title").GetString(), Is.EqualTo("New deliberately different intent"));
        });
    }

    [TestCase("superseded-existing"), TestCase("completed-retired")]
    public async Task AcceptCurrentPreservesOriginalUncertaintyAndLocalWorkAcrossRestart(string origin)
    {
        var (h, target) = await UncertainHistory(origin);
        if (origin == "superseded-existing") h.Existing.Titles["I1"] = "Current external title";
        var independent = h.Session.Workspace.Open(h.Workspace.Selected!).Single(r => r.ItemId == "P1-T2").Cells[0];
        h.Session.Workspace.Commit("P1", independent, "Independent confirmed edit");
        h.Session.Workspace.SetBuffer(independent, "Independent unfinished input");
        Assert.That(await h.Workspace.FlushDraftsAsync(), Is.True);
        var before = h.Session.Workspace.Snapshot();
        var journal = JsonSerializer.Serialize(before.Journal);
        var fields = JsonSerializer.Serialize(before.Fields);
        var history = JsonSerializer.Serialize(before.History);
        var writes = h.Writes.Select(w => (w.Query, Input: w.Input.GetRawText())).ToArray();

        await h.Workspace.PrepareHistoricalFieldAsync(target);

        var review = h.Workspace.HistoricalFieldReview;
        Assert.That(review, Is.Not.Null, "An exact retired field must offer a fresh read-only disposition review.");
        Assert.That(review!.Observation.Current!.Value, Is.Not.EqualTo(review.Operation.Intended.Value));
        var result = await h.Workspace.ConfirmHistoricalFieldAsync(review, HistoricalFieldDecisionKind.AcceptCurrent);
        Assert.That(result.AllLocalWorkSaved, Is.True, result.Problem);
        Assert.That(result.Decision, Is.Not.Null);
        await h.Restart();
        var after = h.Session.Workspace.Snapshot();
        Assert.Multiple(() => {
            Assert.That(JsonSerializer.Serialize(after.Journal), Is.EqualTo(journal));
            Assert.That(JsonSerializer.Serialize(after.Fields), Is.EqualTo(fields));
            Assert.That(JsonSerializer.Serialize(after.History), Is.EqualTo(history));
            Assert.That(h.Session.Workspace.HasUnresolvedApply, Is.False);
            Assert.That(h.Writes.Select(w => (w.Query, Input: w.Input.GetRawText())), Is.EqualTo(writes));
        });
        await h.Workspace.UnregisterAsync(retainDrafts: true);
        Assert.That(h.Workspace.Selected, Is.Null, h.Workspace.Status);
    }

    internal static async Task<(CreationHarness Harness, HistoricalFieldTarget Target)> UncertainHistory(string origin)
    {
        var h = await CreationHarness.Create(2);
        if (origin == "superseded-existing")
        {
            var row = h.Session.Workspace.Open(h.Workspace.Selected!).Single(r => r.ItemId == "P1-T1");
            h.Session.Workspace.Commit("P1", row.Cells[0], "Old uncertain title");
            h.Existing.LoseResponse = true;
            await h.Apply(row.ItemId);
            h.Existing.LoseResponse = false;
            var batch = h.Session.Workspace.Journal.Single();
            Assert.That(batch.Operations.Single().State, Is.EqualTo(ApplyState.Unknown));
            await h.Workspace.SupersedeApplyAsync(batch.Id);
            return (h, new(batch.Id, null, batch.Operations.Single().Id));
        }
        var local = h.Add("Normally acknowledged creation");
        var status = h.Session.Workspace.Open(h.Workspace.Selected!).Single(r => r.ItemId == local).Cells[1];
        h.Session.Workspace.Commit("P1", status, "done", true);
        h.Existing.MutationResult = (_, _) => ScriptedRunner.Http("{}", 403);
        await h.Apply(local);
        var creation = h.Session.Workspace.Creations.Single();
        Assert.That(creation.Fields!.Single().State, Is.EqualTo(ApplyState.Unknown));
        h.Existing.MutationResult = null;
        h.Session.Workspace.Commit("P1", status, "todo", true);
        var source = h.Session.Workspace.Journal.Single();
        await h.Workspace.PrepareCreationSetupAsync(source.Id, creation.Id);
        await h.Workspace.ConfirmCreationSetupAsync(h.Workspace.CreationSetupReview!);
        creation = h.Session.Workspace.Creations.Single();
        Assert.That(creation.Completed, Is.True, h.Workspace.Status);
        Assert.That(creation.EarlierFields!.Single().State, Is.EqualTo(ApplyState.Unknown));
        return (h, new(source.Id, creation.Id, creation.EarlierFields!.Single().Id));
    }
}
