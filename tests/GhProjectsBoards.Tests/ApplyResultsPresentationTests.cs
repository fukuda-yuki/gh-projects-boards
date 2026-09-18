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
    private static ApplyOperation Operation(ApplyState state) => new("operation", new("Title", "issue"), "item", "issue",
        "owner/repo #1 / issue", "Title", "old", new("new"), 1, state, [], "reason");
}
