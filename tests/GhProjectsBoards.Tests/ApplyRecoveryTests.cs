using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

internal sealed class ApplyRecoveryTests
{
    [Test]
    public async Task InterruptedApprovalExplainsTheBlockBeforeAnotherSelection()
    {
        var h = await Interrupted();

        await h.Workspace.PrepareApplyAsync(new HashSet<string>());

        Assert.That(h.Workspace.ApplyBlockReason(h.Workspace.ApplyReview),
            Does.Contain("前回").And.Contain("未送信 1フィールド").And.Contain("要確認 1フィールド"));
        Assert.That(h.Workspace.CanRestartApplyReview, Is.True);
        Assert.That(h.Writes, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task FreshReviewRetiresInterruptedApprovalWithoutSendingAndPreservesLaterWorkAndEvidence()
    {
        var h = await Interrupted();
        var w = h.Workspace.Drafts!.Workspace;
        var old = w.Journal.Single();
        var title = w.Open(h.Workspace.Selected!)[0].Cells[0];
        w.Commit("P1", title, "Later edit"); w.SetBuffer(title, "未確定入力");

        await h.Workspace.RestartApplyReviewAsync(new HashSet<string> { "P1-T1", "P1-T2" });
        w = h.Workspace.Drafts!.Workspace;

        Assert.That(h.Writes, Has.Count.EqualTo(2));
        Assert.That(w.Journal.Single().Operations[0], Is.EqualTo(old.Operations[0]));
        Assert.That(w.Journal.Single().Operations.Skip(1).All(o => o.State == ApplyState.Superseded), Is.True);
        Assert.That(w.Journal.Single().Operations[1].Attempts, Is.EqualTo(old.Operations[1].Attempts));
        Assert.That(w.Field(title)!.Buffer, Is.EqualTo("未確定入力"));
        Assert.That(h.Workspace.ApplyBlockReason(h.Workspace.ApplyReview), Is.Null);
        Assert.That(h.Workspace.ApplyReview!.Batch.Operations.Select(o => o.Intended.Value),
            Is.EquivalentTo(new[] { "Later edit", "Remaining title" }));
        var saved = await new DraftStore(h.Root).LoadAsync(w.Scope);
        Assert.That(saved!.Journal!.Single().Operations.Select(o => o.State),
            Is.EqualTo(new[] { ApplyState.Succeeded, ApplyState.Superseded, ApplyState.Superseded }));
        Assert.That(saved.Journal!.Single().Operations[1].Attempts, Is.EqualTo(old.Operations[1].Attempts));

        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview);

        Assert.That(h.Writes.Skip(2).Select(v => v.GetProperty("title").GetString()),
            Is.EquivalentTo(new[] { "Later edit", "Remaining title" }));
        Assert.That(h.Workspace.Drafts!.Workspace.Journal.Last().Operations.All(o => o.State == ApplyState.Succeeded), Is.True);
    }

    [TestCase("read"), TestCase("save"), TestCase("project"), TestCase("identity")]
    public async Task FailedOrChangedContextCannotRetireThePriorApproval(string failure)
    {
        var h = await Interrupted();
        var w = h.Workspace.Drafts!.Workspace;
        var old = w.Journal.Single();
        if (failure == "read") h.Unreadable = true;
        if (failure == "identity") h.Context.Invalidate();
        if (failure == "project")
        {
            var choice = await new ProjectDiscovery(h.Service).ResolveAsync(h.Context, "https://github.com/users/sample-user/projects/2", default);
            await h.Workspace.RegisterAsync(choice, null);
        }
        using var locked = failure == "save"
            ? new FileStream(Path.Combine(h.Root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None) : null;

        await h.Workspace.RestartApplyReviewAsync(new HashSet<string> { "P1-T1", "P1-T2" });

        Assert.That(h.Workspace.Drafts!.Workspace.Journal.Single(), Is.EqualTo(old));
        Assert.That(h.Workspace.ApplyBlockReason(h.Workspace.ApplyReview), Is.Not.Null);
        Assert.That(h.Writes, Has.Count.EqualTo(2));
    }

    [TestCase(false), TestCase(true)]
    public async Task CancellingOrLosingConnectionDuringRecoveryKeepsTheDurableApproval(bool disconnect)
    {
        var h = await Interrupted();
        var record = await new DraftStore(h.Root).LoadAsync(h.Workspace.Profile!);
        h.ChangeResponse = (query, _) =>
        {
            if (!query.Contains("ProjectItems")) return;
            if (disconnect) h.Workspace.SuspendConnection(); else h.Workspace.Cancel();
        };

        await h.Workspace.RestartApplyReviewAsync(new HashSet<string> { "P1-T2" });

        var saved = await new DraftStore(h.Root).LoadAsync(record!.Scope);
        Assert.That(saved!.Revision, Is.EqualTo(record.Revision));
        Assert.That(saved.Journal!.Single().Operations.Select(o => o.State),
            Is.EqualTo(new[] { ApplyState.Succeeded, ApplyState.Unknown, ApplyState.Cancelled }));
        Assert.That(h.Workspace.ApplyReview, Is.Null);
        Assert.That(h.Writes, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task EditingAfterRecoveryInvalidatesTheNewApprovalWithoutDispatch()
    {
        var h = await Interrupted();
        await h.Workspace.RestartApplyReviewAsync(new HashSet<string> { "P1-T2" });
        var review = h.Workspace.ApplyReview!;
        var w = h.Workspace.Drafts!.Workspace;
        w.Commit("P1", w.Open(h.Workspace.Selected!)[1].Cells[0], "Changed after review");

        await h.Workspace.ConfirmApplyAsync(review);

        Assert.That(h.Workspace.Drafts.Workspace.Journal, Has.Count.EqualTo(1));
        Assert.That(h.Workspace.ApplyBlockReason(review), Does.Contain("確認後に変更"));
        Assert.That(h.Writes, Has.Count.EqualTo(2));
    }

    private static async Task<ApplyTests.Harness> Interrupted()
    {
        var h = await ApplyTests.Harness.Create(2);
        var w = h.Workspace.Drafts!.Workspace;
        var rows = w.Open(h.Workspace.Selected!);
        w.Commit("P1", rows[0].Cells[0], "Applied title");
        w.Commit("P1", rows[0].Cells[1], "done", true);
        w.Commit("P1", rows[1].Cells[0], "Remaining title");
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1", "P1-T2" });
        h.OnMutation = () => { if (h.Writes.Count == 2) h.Workspace.Cancel(); };
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Workspace.Drafts!.Workspace.Journal.Single().Operations.Select(o => o.State),
            Is.EqualTo(new[] { ApplyState.Succeeded, ApplyState.Unknown, ApplyState.Cancelled }));
        h.OnMutation = null;
        return h;
    }
}
