using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class CreationKnowledgePresentationTests
{
    [TestCase("no-operation", CreationKnowledge.Unsent, "新規・未送信")]
    [TestCase("approved-unsent", CreationKnowledge.Unsent, "新規・未送信")]
    [TestCase("dispatched", CreationKnowledge.OutcomeUnconfirmed, "作成結果未確認")]
    [TestCase("withdrawn-dispatched", CreationKnowledge.OutcomeUnconfirmed, "作成結果未確認")]
    [TestCase("retry-unsent", CreationKnowledge.OutcomeUnconfirmed, "作成結果未確認")]
    [TestCase("received-id", CreationKnowledge.IdentityUnverified, "Issueの確認待ち")]
    [TestCase("received-issue", CreationKnowledge.IdentityUnverified, "Issueの確認待ち")]
    [TestCase("verified-issue", CreationKnowledge.SetupIncomplete, "Project設定が未完了")]
    [TestCase("membership-dispatched", CreationKnowledge.SetupIncomplete, "Project設定が未完了")]
    [TestCase("verified-member", CreationKnowledge.SetupIncomplete, "フィールド設定が未完了")]
    [TestCase("verified-fields", CreationKnowledge.SetupIncomplete, "設定完了の確認待ち")]
    [TestCase("bound-complete", CreationKnowledge.Complete, "設定確認済み")]
    [TestCase("retry-complete", CreationKnowledge.Complete, "設定確認済み")]
    public void DurableKnowledgeKeepsUnsentUncertainReceivedAndVerifiedStatesDistinct(string scenario, CreationKnowledge expected, string label)
    {
        var issue = new CreatedIssue("created1", "R1", 1001, "https://example.test/owner/repo/issues/1001", "Approved title", DateTimeOffset.UnixEpoch);
        var initial = new CreationOperation("attempt", "local1", 1, new("R1", "owner/repo", true, false, true, DateTimeOffset.UnixEpoch), "Approved title", []);
        CreationOperation? operation = scenario switch
        {
            "no-operation" => null,
            "approved-unsent" => initial,
            "dispatched" => initial with { Dispatched = true },
            "withdrawn-dispatched" => initial with { Dispatched = true, Authorized = false },
            "retry-unsent" => initial with { PreviousAttempt = "original", EarlierUncertain = true },
            "received-id" => initial with { Dispatched = true, ReceivedId = issue.Id },
            "received-issue" => initial with { Dispatched = true, Received = issue },
            "verified-issue" => initial with { Dispatched = true, Verified = issue, UserBound = true, EarlierUncertain = true },
            "membership-dispatched" => initial with { Dispatched = true, Verified = issue, MembershipDispatched = true, ReceivedItemId = "item1" },
            "verified-member" => initial with { Dispatched = true, Verified = issue, ItemId = "item1" },
            "verified-fields" => initial with { Dispatched = true, Verified = issue, ItemId = "item1", Fields = [] },
            "bound-complete" => initial with { Dispatched = true, Verified = issue, ItemId = "item1", Fields = [], Completed = true, UserBound = true, EarlierUncertain = true },
            "retry-complete" => initial with { Dispatched = true, Verified = issue, ItemId = "item1", Fields = [], Completed = true, PreviousAttempt = "original", EarlierUncertain = true },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var original = JsonSerializer.Serialize(operation);

        var view = CreationKnowledgePresentation.Describe(operation);

        Assert.That(view.Knowledge, Is.EqualTo(expected));
        Assert.That(view.RowLabel, Is.EqualTo(label));
        Assert.That(view.Description, Does.Not.Contain("未作成"));
        Assert.That(JsonSerializer.Serialize(operation), Is.EqualTo(original), "Presentation must not rewrite authorization, attempts or original uncertainty.");
        if (scenario == "membership-dispatched") Assert.That(view.Description, Does.Contain("追加結果を確認"));
        if (scenario == "verified-issue") Assert.That(view.Description, Does.Contain("追加・所属確認"));
    }

    [Test]
    public async Task PersistedLostResponseAndVerifiedBindingDescribeCurrentKnowledgeWithoutChangingHistoryOrDrafts()
    {
        var h = await CreationHarness.Create(2); var local = h.Add("Retained creation");
        var work = h.Session.Workspace; var existing = work.Open(h.Workspace.Selected!)[1];
        work.Commit("P1", existing.Cells[0], "Independent draft"); work.SetBuffer(existing.Cells[0], "Z");
        h.LoseCreate = true; await h.Apply(local); await h.Restart();
        var c = h.Session.Workspace.Creations.Single();
        var before = JsonSerializer.Serialize(h.Session.Workspace.Snapshot());
        Assert.That(CreationKnowledgePresentation.Describe(c).Knowledge, Is.EqualTo(CreationKnowledge.OutcomeUnconfirmed));
        Assert.That(JsonSerializer.Serialize(h.Session.Workspace.Snapshot()), Is.EqualTo(before));

        var batch = h.Session.Workspace.Journal.Single(); h.LoseCreate = false;
        await h.Workspace.InspectCreationBindingAsync(batch.Id, c.Id, "https://github.com/sample-user/first/issues/1001");
        await h.Workspace.ConfirmCreationBindingAsync(batch.Id, c.Id, h.Workspace.CreationBindingPreview!, h.Workspace.CreationBindingRevision);
        Assert.That(CreationKnowledgePresentation.Describe(h.Session.Workspace.Creations.Single()).Knowledge, Is.EqualTo(CreationKnowledge.SetupIncomplete));
        await h.Workspace.ResumeApplyAsync(batch.Id); await h.Restart();
        c = h.Session.Workspace.Creations.Single();
        before = JsonSerializer.Serialize(h.Session.Workspace.Snapshot());

        Assert.That(CreationKnowledgePresentation.Describe(c).Knowledge, Is.EqualTo(CreationKnowledge.Complete));
        Assert.That(c.Dispatched && c.EarlierUncertain && c.UserBound, Is.True);
        Assert.That(c.Received, Is.Null); Assert.That(c.ReceivedId, Is.Null);
        Assert.That(h.Session.Workspace.Creations.Count(), Is.EqualTo(1));
        Assert.That(h.Issues.Count, Is.EqualTo(1));
        Assert.That(h.Session.Workspace.Fields.Single(field => field.Key == new FieldKey("Title", "I2")).Buffer, Is.EqualTo("Z"));
        Assert.That(JsonSerializer.Serialize(h.Session.Workspace.Snapshot()), Is.EqualTo(before));
    }
}
