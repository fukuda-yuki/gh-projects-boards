using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ApplyResultsPresentationTests
{
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

        var attention = ApplyResultsPresentation.Attention([Batch() with { Operations = [operation] }]).Single();

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

        var attention = ApplyResultsPresentation.Attention([Batch() with { Operations = [operation] }]).Single();

        Assert.That(attention.Reason, Is.EqualTo(LegacyUncertainReason));
        Assert.That(attention.Kind, Is.EqualTo(uncertain is null ? ApplyAttentionKind.Review : ApplyAttentionKind.Uncertain));
    }

    [TestCase("Network"), TestCase("TimedOut")]
    public void LegacyFailedEvidenceDoesNotReplaceTheLatestReadFailureExplanation(string latestReason)
    {
        var operation = Operation(ApplyState.Blocked) with { Reason = latestReason,
            Attempts = [new(1, DateTimeOffset.UtcNow, ApplyState.Failed, "PermissionDenied")] };

        var attention = ApplyResultsPresentation.Attention([Batch() with { Operations = [operation] }]).Single();

        Assert.That(attention.Reason, Is.EqualTo("通信結果を確認できませんでした。"));
        Assert.That(operation.Reason, Is.EqualTo(latestReason));
    }

    [Test]
    public void MixedResultsKeepExactTargetsAndOmitOnlyVerifiedSuccess()
    {
        var batch = Batch() with { Operations = [Operation(ApplyState.Succeeded), Operation(ApplyState.Failed) with {
            Id = "select", Key = new("Select", "item", "P1", "status"), FieldName = "Status"
        }] };
        var result = ApplyResultsPresentation.Attention([batch]);
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
        var attention = ApplyResultsPresentation.Attention([Batch() with { Operations = [], Creations = [creation] }]).Single();
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
        var result = ApplyResultsPresentation.Attention([Batch() with { Operations = [], Creations = [c] }]);
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

        var result = ApplyResultsPresentation.Attention([batch]);

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

        var result = ApplyResultsPresentation.Attention([Batch() with { Creations = [creation] }]);

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

        var result = ApplyResultsPresentation.Attention([Batch() with { Creations = [creation] }]);

        Assert.That(result, Is.Not.Empty);
        Assert.That(result.Any(a => membershipConfirmed ? a.OperationId == "operation" : a.Kind == ApplyAttentionKind.Review), Is.True);
    }

    [Test]
    public void ExplicitlyBoundCompletedCreationKeepsUncertainRetiredFieldActionable()
    {
        var field = Operation(ApplyState.Superseded) with { Key = new("Select", "created-item", "P1", "deleted-field"),
            Attempts = [new(1, DateTimeOffset.UtcNow, ApplyState.Unknown, "lost response")] };
        var creation = BoundCreation() with { EarlierFields = [field] };

        var result = ApplyResultsPresentation.Attention([Batch() with { Creations = [creation] }]);

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
        var result = ApplyResultsPresentation.Attention([Batch() with { Operations = [], Creations = [creation] }]);
        Assert.That(result, Has.Length.EqualTo(1));
        Assert.That(result[0].Kind, Is.EqualTo(ApplyAttentionKind.Uncertain));
        Assert.That(result[0].Field, Is.EqualTo(field.Key));
        Assert.That(result[0].Withdrawn, Is.True);
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
