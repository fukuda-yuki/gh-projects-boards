using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ApplyResultsPresentationTests
{
    [Test]
    public void PermissionFailureNamesTheRejectedActionAndPreservedWorkWithoutInventingItsCause()
    {
        var operation = Operation(ApplyState.Failed) with { Reason = "PermissionDenied",
            Attempts = [new(1, DateTimeOffset.UtcNow, ApplyState.Failed, "PermissionDenied")] };

        var explanation = ApplyResultsPresentation.OutcomeReason(operation);

        Assert.That(explanation, Does.Contain("タイトル更新が拒否").And.Contain("ローカルに保持"));
        Assert.That(explanation, Does.Not.Contain("OAuth").And.Not.Contain("管理者").And.Not.Contain("再接続すれば"));
        Assert.That(operation.Reason, Is.EqualTo("PermissionDenied"));
    }

    [Test]
    public void ExecutionOutcomeKeepsItsVerifiedSuccessBesideItsFailureAndExcludesOtherWork()
    {
        var batch = Batch();
        var failed = Operation(ApplyState.Failed) with { Reason = "PermissionDenied" };
        var status = Operation(ApplyState.Succeeded) with { Id = "status-operation", Key = new("Select", "item", "P1", "status"),
            FieldName = "Status", Intended = new("done"), Verification = new("readback", batch.Project, DateTimeOffset.UtcNow,
                "done", ValueAvailability.Present, null, [new("done", "Done")]) };
        batch = batch with { Operations = [failed, status] };
        var unrelated = Batch() with { Id = "another-batch", Operations = [Operation(ApplyState.Pending) with { Id = "unrelated" }] };

        var result = ApplyResultsPresentation.ExecutionOutcome(batch, ApplyResultsPresentation.Attention([batch, unrelated], []));

        Assert.That(result.VerifiedFields, Is.EqualTo(new[] { status }));
        Assert.That(result.Remaining.Select(entry => entry.OperationId), Is.EqualTo(new[] { failed.Id }));
        Assert.That(result.Title, Is.EqualTo("今回の反映は一部完了しました"));
        Assert.That(result.Summary, Is.EqualTo("反映確認済み 1フィールド / 失敗 1フィールド"));
        Assert.That(ApplyResultsPresentation.VerifiedValue(status), Is.EqualTo("Done"));
    }

    [TestCase(ApplyState.Unknown, ApplyAttentionKind.Uncertain)]
    [TestCase(ApplyState.Running, ApplyAttentionKind.Uncertain)]
    [TestCase(ApplyState.Pending, ApplyAttentionKind.Unsent)]
    public void IncompleteExecutionNeverTurnsUnknownOrUnsentIntoVerifiedSuccess(ApplyState state, ApplyAttentionKind expected)
    {
        var batch = Batch() with { Operations = [Operation(state) with { Reason = "PermissionDenied" }] };

        var result = ApplyResultsPresentation.ExecutionOutcome(batch, ApplyResultsPresentation.Attention([batch], []));

        Assert.That(result.HasVerifiedWork, Is.False);
        Assert.That(result.Remaining.Single().Kind, Is.EqualTo(expected));
        Assert.That(ApplyResultsPresentation.OutcomeReason(batch.Operations.Single()), Is.EqualTo("PermissionDenied"),
            "An error classification alone must not claim a definite rejected dispatch.");
    }

    [TestCase("missing"), TestCase("different-project"), TestCase("different-value")]
    public void SuccessPresentationRequiresTheApprovedValueAndSameProjectReadback(string evidence)
    {
        var batch = Batch();
        var operation = Operation(ApplyState.Succeeded) with { Verification = evidence == "missing" ? null
            : new("readback", evidence == "different-project" ? batch.Project with { NodeId = "P2" } : batch.Project,
                DateTimeOffset.UtcNow, evidence == "different-value" ? "other" : "new", ValueAvailability.Present, null, []) };

        Assert.That(ApplyResultsPresentation.ExecutionOutcome(batch with { Operations = [operation] }, []).VerifiedFields, Is.Empty);
    }

    [Test]
    public void HistoricalOptionNamesKeepCurrentReadbackAndUnknownIdentityDistinct()
    {
        var batch = Batch();
        var operation = Operation(ApplyState.Succeeded) with { Key = new("Select", "item", "P1", "status"),
            Verification = new("old-read", batch.Project, DateTimeOffset.UtcNow, "done", ValueAvailability.Present, null,
                [new("todo", "Old todo"), new("done", "Old done")]) };
        var current = new FieldObservation("new-read", batch.Project, DateTimeOffset.UtcNow, "done", ValueAvailability.Present, null,
            [new("todo", "To do"), new("done", "Done"), new("other-done", "Done")]);
        var field = new DraftField(operation.Key, "done", batch.Project, current.At, null, null, 1, current);

        Assert.That(ApplyResultsPresentation.HistoricalValue(operation, "done", batch.Project, field),
            Is.EqualTo("Done（ID done）（現在確認できる名称）"));
        Assert.That(ApplyResultsPresentation.HistoricalValue(operation, "todo", batch.Project),
            Is.EqualTo("Old todo（読み戻し確認時の名称）"));
        Assert.That(ApplyResultsPresentation.HistoricalValue(operation, "deleted", batch.Project, field),
            Is.EqualTo("選択肢 ID deleted（名前は未確認）"));
        Assert.That(ApplyResultsPresentation.HistoricalValue(operation, "done", batch.Project with { Scope = new("other.test", 42) }, field),
            Is.EqualTo("選択肢 ID done（名前は未確認）"));
        Assert.That(ApplyResultsPresentation.HistoricalValue(operation, "done", batch.Project, field with { Key = field.Key with { FieldId = "other-field" } }),
            Is.EqualTo("Old done（読み戻し確認時の名称）"), "Another field's matching option ID is not this field's current name.");
        Assert.That(operation.Verification.Options[1].Name, Is.EqualTo("Old done"));
    }

    [TestCase(ApplyState.Failed, true), TestCase(ApplyState.Blocked, true)]
    [TestCase(ApplyState.Unknown, false), TestCase(ApplyState.Pending, false), TestCase(ApplyState.Waiting, false)]
    public void DirectRemainingReviewRequiresKnownFailedDispatches(ApplyState state, bool expected)
    {
        var operation = Operation(state) with { Attempts = [new(1, DateTimeOffset.UtcNow, ApplyState.Failed, "PermissionDenied")] };
        var batch = Batch() with { Operations = [operation] };

        Assert.That(ApplyResultsPresentation.RequiresFreshReview(batch), Is.EqualTo(expected));
        Assert.That(ApplyResultsPresentation.RequiresFreshReview(batch with { Creations = [BoundCreation() with { Completed = false }] }), Is.False);
        Assert.That(ApplyResultsPresentation.RequiresFreshReview(batch with { Operations = [operation with {
            Attempts = [new(1, DateTimeOffset.UtcNow, ApplyState.Unknown, "lost response")] }] }), Is.False);
    }

    [TestCase(false, ValueAvailability.Empty, false)]
    [TestCase(true, ValueAvailability.Present, false)]
    [TestCase(true, ValueAvailability.Empty, true)]
    public void EmptyVerificationOnlyConfirmsAnExplicitClear(bool clear, ValueAvailability availability, bool expected)
    {
        var batch = Batch();
        var operation = Operation(ApplyState.Succeeded) with { Key = new("Select", "item", "P1", "status"), Intended = new(null, clear),
            Verification = new("readback", batch.Project, DateTimeOffset.UtcNow, null, availability, null, []) };

        Assert.That(ApplyResultsPresentation.IsVerified(batch, operation), Is.EqualTo(expected));
    }

    [TestCase(false), TestCase(true)]
    public void RecoveryContextSeparatesEarlierStatusFromTheNewTitleExecution(bool otherAccount)
    {
        var original = Batch();
        var title = Operation(ApplyState.Superseded) with { Attempts = [new(1, DateTimeOffset.UtcNow, ApplyState.Failed, "PermissionDenied")] };
        var status = Operation(ApplyState.Succeeded) with { Id = "status", Key = new("Select", "item", "P1", "status"),
            Intended = new("done"), Verification = new("status-read", original.Project, DateTimeOffset.UtcNow, "done", ValueAvailability.Present, null, [new("done", "Done")]) };
        original = original with { Operations = [title, status] };
        var next = original with { Id = "next", Project = otherAccount ? original.Project with { Scope = new("other.test", 42) } : original.Project };
        var published = title with { Id = "new-title", State = ApplyState.Succeeded,
            Verification = new("title-read", next.Project, DateTimeOffset.UtcNow, "new", ValueAvailability.Present, null, []) };
        next = next with { Operations = [published] };
        ApplyBatch[] history = [original, next];

        Assert.That(ApplyResultsPresentation.EarlierVerifiedWork(history, next, published), Is.EqualTo(otherAccount ? Array.Empty<ApplyOperation>() : [status]));
        Assert.That(ApplyResultsPresentation.ResolvedByLaterExecution(history, original, title), Is.EqualTo(!otherAccount));
        Assert.That(original.Operations[0].Attempts.Single().State, Is.EqualTo(ApplyState.Failed));
        Assert.That(next.Operations.Select(operation => operation.Id), Is.EqualTo(new[] { "new-title" }));
    }

    [TestCase(ApplyState.Succeeded, null)]
    [TestCase(ApplyState.Pending, ApplyAttentionKind.Unsent)]
    [TestCase(ApplyState.Cancelled, ApplyAttentionKind.Unsent)]
    [TestCase(ApplyState.Waiting, ApplyAttentionKind.Unsent)]
    [TestCase(ApplyState.Failed, ApplyAttentionKind.Failed)]
    [TestCase(ApplyState.Blocked, ApplyAttentionKind.Review)]
    [TestCase(ApplyState.Running, ApplyAttentionKind.Uncertain)]
    [TestCase(ApplyState.Unknown, ApplyAttentionKind.Uncertain)]
    [TestCase(ApplyState.Superseded, null)]
    public void DurableOutcomeRetainsItsKnowledgeState(ApplyState state, ApplyAttentionKind? expected)
        => Assert.That(ApplyResultsPresentation.Kind(Operation(state)), Is.EqualTo(expected));

    [TestCase(ApplyState.Superseded), TestCase(ApplyState.Blocked), TestCase(ApplyState.Waiting)]
    public void RetainedDispatchUncertaintyIsNotHiddenByLaterApprovalState(ApplyState state)
        => Assert.That(ApplyResultsPresentation.Kind(Operation(state) with {
            Attempts = [new(1, DateTimeOffset.UtcNow, ApplyState.Unknown, "lost response")]
        }), Is.EqualTo(ApplyAttentionKind.Uncertain));

    [Test]
    public void LegacyBlockedFailureIsExplainedFromFailedEvidenceWithoutRewritingTheRecord()
    {
        var operation = Operation(ApplyState.Blocked) with { Reason = LegacyUncertainReason,
            Attempts = [new(1, DateTimeOffset.UtcNow, ApplyState.Failed, "PermissionDenied")] };

        var attention = ApplyResultsPresentation.Attention([Batch() with { Operations = [operation] }], []).Single();

        Assert.That(attention.Kind, Is.EqualTo(ApplyAttentionKind.Review));
        Assert.That(attention.Reason, Is.EqualTo("前回の送信は失敗しました。残った変更を再確認し、新しいレビューで承認してください。"));
        Assert.That(operation.Reason, Is.EqualTo(LegacyUncertainReason));
        Assert.That(operation.Attempts.Single().Reason, Is.EqualTo("PermissionDenied"));
    }

    [TestCase(null), TestCase(ApplyState.Unknown), TestCase(ApplyState.Running)]
    public void LegacyBlockedReasonDoesNotInventFailureForMissingOrUncertainEvidence(ApplyState? uncertain)
    {
        var operation = Operation(ApplyState.Blocked) with { Reason = LegacyUncertainReason,
            Attempts = uncertain is { } state
                ? [new(1, DateTimeOffset.UtcNow, ApplyState.Failed, "PermissionDenied"), new(2, DateTimeOffset.UtcNow, state, "lost response")]
                : [] };

        var attention = ApplyResultsPresentation.Attention([Batch() with { Operations = [operation] }], []).Single();

        Assert.That(attention.Reason, Is.EqualTo(LegacyUncertainReason));
        Assert.That(attention.Kind, Is.EqualTo(uncertain is null ? ApplyAttentionKind.Review : ApplyAttentionKind.Uncertain));
    }

    [TestCase("Network"), TestCase("TimedOut")]
    public void LegacyFailedEvidenceDoesNotReplaceTheLatestReadFailureExplanation(string latestReason)
    {
        var operation = Operation(ApplyState.Blocked) with { Reason = latestReason,
            Attempts = [new(1, DateTimeOffset.UtcNow, ApplyState.Failed, "PermissionDenied")] };

        var attention = ApplyResultsPresentation.Attention([Batch() with { Operations = [operation] }], []).Single();

        Assert.That(attention.Reason, Is.EqualTo("通信結果を確認できませんでした。"));
        Assert.That(operation.Reason, Is.EqualTo(latestReason));
    }

    [Test]
    public void MixedResultsKeepExactTargetsAndOmitOnlyVerifiedSuccess()
    {
        var batch = Batch() with { Operations = [Operation(ApplyState.Succeeded), Operation(ApplyState.Failed) with {
            Id = "select", Key = new("Select", "item", "P1", "status"), FieldName = "Status"
        }] };
        var result = ApplyResultsPresentation.Attention([batch], []);
        Assert.That(result, Has.Length.EqualTo(1));
        Assert.That(result[0].Field, Is.EqualTo(new FieldKey("Select", "item", "P1", "status")));
        Assert.That(result[0].RowId, Is.EqualTo("item"));
        Assert.That(ApplyResultsPresentation.Summary(result), Is.EqualTo("失敗 1フィールド"));
        Assert.That(batch.Operations, Has.Length.EqualTo(2));
    }

    [Test]
    public void UncertainCreationTargetsTheEditableLocalTitleInItsProject()
    {
        var registration = EditingTests.Registration();
        var workspace = new EditingWorkspace(registration.Snapshot.Id.Scope);
        workspace.SetRegistrations([registration]);
        var id = workspace.AddRow(registration);
        var row = workspace.Open(registration).Single(r => r.ItemId == id);
        var creation = new CreationOperation("creation", id, 1,
            new("repo", "owner/repo", true, false, true, DateTimeOffset.UtcNow),
            "Created", [], Dispatched: true);
        var attention = ApplyResultsPresentation.Attention([Batch() with { Operations = [], Creations = [creation] }], []).Single();
        Assert.That(attention.RowId, Is.EqualTo(row.ItemId));
        Assert.That(row.Cells.SingleOrDefault(c => c.Key == attention.Field), Is.EqualTo(row.Cells[0]),
            "The problem must lead back to the actual editable local row, not a missing target.");
    }

    [Test]
    public void CompletedCreationWithEarlierUncertaintyStillNeedsVerificationAtPromotedRow()
    {
        var c = new CreationOperation("creation", "local-one", 1, new("repo", "owner/repo", true, false, true, DateTimeOffset.UtcNow),
            "Created", [], Completed: true, EarlierUncertain: true, ItemId: "created-item",
            Verified: new("created-issue", "repo", 1, "https://github.com/owner/repo/issues/1", "Created", DateTimeOffset.UtcNow));
        var result = ApplyResultsPresentation.Attention([Batch() with { Operations = [], Creations = [c] }], []);
        Assert.That(result.Single().Kind, Is.EqualTo(ApplyAttentionKind.Uncertain));
        Assert.That(result.Single().RowId, Is.EqualTo("created-item"));
        Assert.That(result.Single().Field, Is.EqualTo(new FieldKey("Title", "created-issue")));
        Assert.That(ApplyResultsPresentation.Summary(result), Is.EqualTo("要確認 1件の新規Issue"));
    }

    [Test]
    public void ExplicitlyBoundCompletedCreationKeepsOriginalUnknownOnlyInDurableHistory()
    {
        var creation = BoundCreation();
        var batch = Batch() with { Creations = [creation] };

        var result = ApplyResultsPresentation.Attention([batch], []);

        Assert.That(result, Is.Empty, "Verified current work needs no recovery merely because the original create response was lost.");
        Assert.That(batch.Creations!.Single(), Is.SameAs(creation));
        Assert.That(creation.EarlierUncertain, Is.True, "Current completion must not rewrite the original request outcome.");
        Assert.That(creation.Received, Is.Null);
        Assert.That(ApplyResultsPresentation.CompletionAnnouncement(batch), Is.EqualTo("Issueの確認とProjectへの追加が完了しました。"));
    }

    [Test]
    public void ExplicitBindingOfCompletedRetryKeepsTheEarlierDuplicateRiskActionable()
    {
        var creation = BoundCreation() with { PreviousAttempt = "original-attempt" };

        var result = ApplyResultsPresentation.Attention([Batch() with { Creations = [creation] }], []);

        Assert.That(result.Single().Kind, Is.EqualTo(ApplyAttentionKind.Uncertain));
        Assert.That(result.Single().CreationId, Is.EqualTo(creation.Id));
    }

    [TestCase(false), TestCase(true)]
    public void ExplicitBindingKeepsIncompleteMembershipOrSetupActionable(bool membershipConfirmed)
    {
        var creation = BoundCreation() with {
            Completed = false,
            ItemId = membershipConfirmed ? "created-item" : null,
            Fields = membershipConfirmed ? [Operation(ApplyState.Pending)] : null
        };

        var result = ApplyResultsPresentation.Attention([Batch() with { Creations = [creation] }], []);

        Assert.That(result, Is.Not.Empty);
        Assert.That(result.Any(a => membershipConfirmed ? a.OperationId == "operation" : a.Kind == ApplyAttentionKind.Review), Is.True);
    }

    [Test]
    public void ExplicitlyBoundCompletedCreationKeepsUncertainRetiredFieldActionable()
    {
        var field = Operation(ApplyState.Superseded) with { Key = new("Select", "created-item", "P1", "deleted-field"),
            Attempts = [new(1, DateTimeOffset.UtcNow, ApplyState.Unknown, "lost response")] };
        var creation = BoundCreation() with { EarlierFields = [field] };

        var result = ApplyResultsPresentation.Attention([Batch() with { Creations = [creation] }], []);

        Assert.That(result, Has.Length.EqualTo(1));
        Assert.That(result[0].Kind, Is.EqualTo(ApplyAttentionKind.Uncertain));
        Assert.That(result[0].Field, Is.EqualTo(field.Key));
        Assert.That(result[0].Withdrawn, Is.True);
    }

    [Test]
    public void BoundCompletionWithApprovedFieldsNamesTheCompletedProjectSettings()
    {
        var creation = BoundCreation() with { Fields = [Operation(ApplyState.Succeeded)] };

        var announcement = ApplyResultsPresentation.CompletionAnnouncement(Batch() with { Creations = [creation] });

        Assert.That(announcement, Is.EqualTo("Issueの確認とProject設定が完了しました。"));
    }

    [Test]
    public void CompletedCreationKeepsUncertainRetiredFieldVisibleAtItsExactTarget()
    {
        var field = Operation(ApplyState.Superseded) with { Key = new("Select", "created-item", "P1", "deleted-field"),
            Attempts = [new(1, DateTimeOffset.UtcNow, ApplyState.Unknown, "lost response")] };
        var creation = new CreationOperation("creation", "local-one", 1,
            new("repo", "owner/repo", true, false, true, DateTimeOffset.UtcNow), "Created", [],
            Completed: true, ItemId: "created-item", EarlierFields: [field]);
        var result = ApplyResultsPresentation.Attention([Batch() with { Operations = [], Creations = [creation] }], []);
        Assert.That(result, Has.Length.EqualTo(1));
        Assert.That(result[0].Kind, Is.EqualTo(ApplyAttentionKind.Uncertain));
        Assert.That(result[0].Field, Is.EqualTo(field.Key));
        Assert.That(result[0].Withdrawn, Is.True);
    }

    [TestCase(false), TestCase(true)]
    public void NewOutcomeTargetsTheIncompleteCreationBeforeCompletedExistingOrCreationWork(bool completedCreationFirst)
    {
        var incomplete = BoundCreation() with { Id = "unfinished", LocalId = "local-two", Completed = false,
            Verified = null, ItemId = null, Fields = null, UserBound = false };
        var batch = Batch() with {
            Operations = completedCreationFirst ? [] : [Operation(ApplyState.Succeeded)],
            Creations = completedCreationFirst ? [BoundCreation(), incomplete] : [incomplete]
        };
        var attention = ApplyResultsPresentation.Attention([batch], []);

        var target = ApplyResultsPresentation.OutcomeTarget(batch, attention);

        Assert.That(target, Is.EqualTo(new ApplyOutcomeTarget(null, "unfinished")));
        Assert.That(batch.Creations!.Last().Dispatched, Is.True);
    }

    [Test]
    public void OutcomeTargetIgnoresOtherExecutionAttentionAndRetainsCompletedFallback()
    {
        var batch = Batch() with { Operations = [Operation(ApplyState.Succeeded)] };
        var other = batch with { Id = "older", Operations = [Operation(ApplyState.Failed)] };

        var target = ApplyResultsPresentation.OutcomeTarget(batch, ApplyResultsPresentation.Attention([other, batch], []));

        Assert.That(target, Is.EqualTo(new ApplyOutcomeTarget("operation", null)));
    }

    [Test]
    public void LostCreationResponseShowsDispatchedIssueAndUnexecutedMembershipAndFrozenStatusIntent()
    {
        var creation = BoundCreation() with { Verified = null, Completed = false, ItemId = null, Fields = null,
            UserBound = false, Selects = [new("status", "Status", "done", "Done")] };

        var stages = ApplyResultsPresentation.CreationStages(Batch(), creation);

        Assert.That(stages.Issue, Is.EqualTo("Issue作成：送信済み・結果未確認"));
        Assert.That(stages.Membership, Is.EqualTo("Projectへの追加：未実行（Issueの確認待ち）"));
        Assert.That(stages.Fields, Is.EqualTo(new[] { "Status → Done：設定予定・未実行" }));
        Assert.That(creation.MembershipDispatched, Is.False);
        Assert.That(creation.Received, Is.Null);
    }

    [TestCase(false), TestCase(true)]
    public void MatchingCreationSettingIsVerifiedWithoutInventingAWriteOrCompletingTheOldUnknown(bool valueMatches)
    {
        var batch = Batch();
        var field = Operation(ApplyState.Succeeded) with { Key = new("Select", "created-item", "P1", "status"),
            Intended = new(valueMatches ? "done" : "todo"),
            Verification = new("read", batch.Project, DateTimeOffset.UtcNow, valueMatches ? "done" : "todo", ValueAvailability.Present, null, [new("done", "Done")]) };
        var creation = BoundCreation() with { Selects = [new("status", "Status", "done", "Done")], Fields = [field] };

        var stages = ApplyResultsPresentation.CreationStages(batch, creation);

        Assert.That(stages.Issue, Is.EqualTo("Issue：独立確認済み"));
        Assert.That(stages.Membership, Is.EqualTo("Projectへの所属：確認済み"));
        Assert.That(stages.Fields.Single(), Is.EqualTo("Status → Done：" + (valueMatches ? "設定値を確認済み（この設定の送信なし）" : "設定結果は未確認")));
        Assert.That(creation.EarlierUncertain, Is.True);
        Assert.That(field.Attempts, Is.Empty);
    }

    [Test]
    public void OriginalBindingWithPendingStatusPresentsRemainingSetupWithoutReopeningHistoricalCreationUncertainty()
    {
        var field = Operation(ApplyState.Pending) with { Key = new("Select", "created-item", "P1", "status"), FieldName = "Status" };
        var creation = BoundCreation() with { Completed = false, Fields = [field], Reason = "設定前のフィールド再照合に失敗。" };
        var batch = Batch() with { Creations = [creation] };

        var attention = ApplyResultsPresentation.Attention([batch], []);

        Assert.That(attention, Has.Length.EqualTo(1));
        Assert.That(attention[0].FieldName, Is.EqualTo("Status"));
        Assert.That(attention[0].Reason, Does.Contain("再確認"));
        Assert.That(creation.EarlierUncertain, Is.True);
        Assert.That(creation.Completed, Is.False);
        Assert.That(field.Attempts, Is.Empty);
    }

    private static ApplyBatch Batch() => new("batch", new(new("github.com", 42), "P1"), "Project", 1, DateTimeOffset.UtcNow, []);
    private const string LegacyUncertainReason = "以前の送信結果が不確定です。明示的な再照合・新規レビューが必要です。";
    private static CreationOperation BoundCreation() => new("creation", "local-one", 1,
        new("repo", "owner/repo", true, false, true, DateTimeOffset.UtcNow), "Created", [],
        Dispatched: true, Completed: true, EarlierUncertain: true, UserBound: true, ItemId: "created-item", Fields: [],
        Verified: new("created-issue", "repo", 1, "https://github.com/owner/repo/issues/1", "Created", DateTimeOffset.UtcNow));
    private static ApplyOperation Operation(ApplyState state) => new("operation", new("Title", "issue"), "item", "issue",
        "owner/repo #1 / issue", "Title", "old", new("new"), 1, state, [], "reason");
}
