using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ApplySupersessionReasonTests
{
    private const string LegacyAmbiguousReason = "ユーザーが以前の承認を撤回。試行履歴を保持し、新たな取得・レビューが必要。";

    [TestCase(false), TestCase(true)]
    public async Task RetiredApprovalPersistsItsActualCauseWithoutChangingAttemptsOrVerifiedSuccess(bool explicitWithdrawal)
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "apply-supersession-" + Guid.NewGuid().ToString("N"));
        var h = await ApplyTests.Harness.Create(2, root);
        var work = h.Workspace.Drafts!.Workspace;
        var rows = work.Open(h.Workspace.Selected!);
        work.Commit("P1", rows[0].Cells[0], "Recovered title");
        work.Commit("P1", rows[0].Cells[1], "done", true);
        work.Commit("P1", rows[1].Cells[0], "Independent confirmed title");
        work.SetBuffer(rows[1].Cells[0], "Z");
        h.MutationResult = (query, _) => query.Contains("ApplyTitle") ? ScriptedRunner.Http("{}", 403) : null;
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        var original = h.Workspace.Drafts.Workspace.Journal.Single();
        var failed = original.Operations.Single(operation => operation.Key.Kind == "Title");
        var succeeded = original.Operations.Single(operation => operation.Key.Kind == "Select");
        Assert.That(failed.State, Is.EqualTo(ApplyState.Failed));
        Assert.That(failed.Attempts.Single().Reason, Is.EqualTo("PermissionDenied"));
        Assert.That(succeeded.State, Is.EqualTo(ApplyState.Succeeded));
        Assert.That(ApplyResultsPresentation.IsVerified(original, succeeded), Is.True);
        var dispatched = h.Writes.Select(write => write.GetRawText()).ToArray();
        var durableBefore = await new DraftStore(root).LoadAsync(work.Scope);
        File.WriteAllText(Path.Combine(root, "test-before.json"), JsonSerializer.Serialize(durableBefore));

        if (explicitWithdrawal) await h.Workspace.SupersedeApplyAsync(original.Id);
        else await h.Workspace.RestartApplyReviewAsync(new HashSet<string> { "P1-T1" });

        var saved = await new DraftStore(root).LoadAsync(work.Scope);
        File.WriteAllText(Path.Combine(root, "test-after.json"), JsonSerializer.Serialize(saved));
        TestContext.AddTestAttachment(Path.Combine(root, "test-before.json"));
        TestContext.AddTestAttachment(Path.Combine(root, "test-after.json"));
        var restored = EditingWorkspace.Restore(saved!);
        var retired = restored.Journal.Single();
        var title = retired.Operations.Single(operation => operation.Id == failed.Id);
        var status = retired.Operations.Single(operation => operation.Id == succeeded.Id);
        var independent = restored.Fields.Single(field => field.Key == new FieldKey("Title", "I2"));
        Assert.Multiple(() => {
            Assert.That(title.State, Is.EqualTo(ApplyState.Superseded));
            Assert.That(JsonSerializer.Serialize(title with { State = failed.State, Reason = failed.Reason }),
                Is.EqualTo(JsonSerializer.Serialize(failed)), "Retiring approval must retain the exact payload, target, attempts and other original evidence.");
            Assert.That(JsonSerializer.Serialize(status), Is.EqualTo(JsonSerializer.Serialize(succeeded)),
                "The earlier independently verified Status is neither rewritten nor resent.");
            Assert.That(h.Writes.Select(write => write.GetRawText()), Is.EqualTo(dispatched), "Neither route sends work.");
            Assert.That((independent.Change?.Value, independent.Buffer), Is.EqualTo(("Independent confirmed title", "Z")));
            Assert.That(saved!.Version, Is.EqualTo(durableBefore!.Version), "Cause wording does not require a checkpoint schema change.");
            Assert.That(h.Workspace.Drafts.Workspace.Journal.Single().Operations.Single(operation => operation.Id == failed.Id).Reason,
                Is.EqualTo(title.Reason), "The cause must survive real checkpoint persistence and workspace restoration.");
            Assert.That(title.Reason, Is.Not.EqualTo(LegacyAmbiguousReason),
                "A newly saved cause must remain distinguishable from old records that conflated review preparation with withdrawal.");
            if (explicitWithdrawal)
                Assert.That(title.Reason, Does.Contain("撤回").And.Not.Contain("新しいレビュー"));
            else
                Assert.That(title.Reason, Does.Contain("再確認").And.Contain("承認").And.Contain("終了").And.Not.Contain("撤回"),
                    "Starting a fresh comparison is not a user withdrawal of approval.");
        });
    }

    [Test]
    public void LegacyAmbiguousSupersessionExplainsApprovalWithoutInventingItsActorOrRewritingHistory()
    {
        var operation = new ApplyOperation("old-title", new("Title", "I1"), "P1-T1", "I1", "owner/repo #1 / I1",
            "Title", "Original title", new("Recovered title"), 1, ApplyState.Superseded,
            [new(1, DateTimeOffset.Parse("2026-10-01T00:00:00Z"), ApplyState.Failed, "PermissionDenied")], LegacyAmbiguousReason);
        var before = JsonSerializer.Serialize(operation);

        var explanation = ApplyResultsPresentation.OutcomeReason(operation);

        Assert.Multiple(() => {
            Assert.That(explanation, Does.Contain("以前の承認").And.Contain("終了"));
            Assert.That(explanation, Does.Not.Contain("ユーザー").And.Not.Contain("撤回").And.Not.Contain("新しいレビュー"),
                "The legacy reason was shared by two paths and cannot identify either cause.");
            Assert.That(explanation, Does.Not.Contain("必要").And.Not.Contain("理由は未記録"),
                "Historical approval state neither demands current repair nor implies that no reason string was stored.");
            Assert.That(JsonSerializer.Serialize(operation), Is.EqualTo(before));
            Assert.That(operation.Attempts.Single().Reason, Is.EqualTo("PermissionDenied"));
            Assert.That(ApplyJournal.HasUnresolvedDispatch(operation), Is.False);
        });
    }
}
