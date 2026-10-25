using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #246: a lost or timed-out connection is a connection problem. The publish says it once, with the
// progress GitHub confirmed, instead of the authentication message and the duplicated 未確認 sentence.
internal sealed partial class PlanPublisherTests
{
    [Test]
    public async Task LostConnectionDuringPublishReportsConfirmedProgressOnceAndStopsWriting()
    {
        await Start(21);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, session.Document.State.Rows
            .Select(r => new PlanCellChange(r.Identity, PlanField.Actual, 3m)).ToImmutableArray()), Today);
        // The second write batch reaches GitHub and is applied, but its response never arrives.
        Scenario("after", batch: 2);

        var result = await publisher.PublishAsync(session, Today);

        Assert.That(result.Succeeded, Is.False);
        var attempt = session.Document.Sync.Publish!;
        var acknowledged = attempt.Writes.GroupBy(w => w.Identity).Count(writes => writes.All(w => w.State == PlanWriteState.Succeeded));
        Assert.That(acknowledged, Is.InRange(1, 20), "Some tasks had every write acknowledged before the connection dropped.");
        Assert.That(attempt.Writes.Any(w => w.State == PlanWriteState.Dispatched), Is.True, "The lost response stays unconfirmed.");
        Assert.Multiple(() =>
        {
            Assert.That(result.Error, Is.EqualTo("GitHub に接続できなくなったため、発行を中断しました（"
                + acknowledged + " / 21 タスクを発行済み）。接続が戻ったら「最新の情報に更新」で確認してから、もう一度発行してください。"));
            Assert.That(result.Error, Does.Not.Contain("未確認"), "The connection message already says what to do.");
            Assert.That(result.Error, Does.Not.Contain("認証またはアクセス権"));
            Assert.That(result.Error, Does.Contain("「最新の情報に更新」"));
        });
        var batches = FakePlanEditor.Load(root).MutationBatches;
        await session.FlushAsync();
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(batches), "No request is sent after the connection loss.");
    }

    [Test]
    public async Task RefreshThatCannotReachGitHubSaysTheConnectionCannotBeReached()
    {
        await Start(1);
        Scenario("readnetwork");

        var result = await publisher.RefreshAsync(session, Today);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Is.EqualTo("GitHub に接続できません。ネットワークを確認してください。"));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }
}
