using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

internal sealed partial class PlanPublisherTests
{
    [TestCase(null), TestCase("duplicate"), TestCase("creation-guard-page-failure"), TestCase("creation-guard-cursor-cycle")]
    public async Task UncertainCreationsShareOneCompleteRepositoryScan(string? fault)
    {
        await Start(0);
        await session.Execute(new InsertPlanRows([PlanRow.New("First", "acme/repo"), PlanRow.New("Second", "acme/repo")]), Today);
        Scenario("uncertain-verification");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        var remote = FakePlanEditor.Load(root);
        Assert.That(remote.Issues.Length, Is.EqualTo(2));
        var filler = Enumerable.Range(0, 199).Select(i => new PlanFakeIssue(new("Filler" + i, "Unrelated", "acme/repo"), "", false));
        // Both matches are on the first page; a duplicate on the last page must still be detected.
        var issues = remote.Issues.Concat(filler).ToImmutableArray();
        if (fault == "duplicate") issues = issues.Add(new(new("Duplicate", "Duplicate", "acme/repo"), remote.Issues[1].Body, false));
        FakePlanEditor.Save(root, remote with { Issues = issues });
        await Reopen(); Scenario(fault);
        var before = session.Document.Sync.Publish;
        var calls = runner.Queries.Count;
        var result = await publisher.PublishAsync(session, Today);
        var expectedPages = fault is "creation-guard-page-failure" or "creation-guard-cursor-cycle" ? 2 : 3;
        Assert.That(runner.Queries.Skip(calls).Count(q => q.Contains("PlanCreationGuard")), Is.EqualTo(expectedPages), "One scan per repository; stop if a page fails.");
        Assert.That(FakePlanEditor.Load(root).Issues.Length, Is.EqualTo(issues.Length), "Recovery must not create duplicates.");
        if (fault is not null)
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(session.Document.Sync.Publish!.Writes, Is.EqualTo(before!.Writes), "No partial adoption from an ambiguous repository scan.");
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(remote.MutationBatches));
            await Reopen();
            Assert.That(session.Document.Sync.Publish!.Writes, Is.EqualTo(before.Writes), "Restart must retain every uncertain creation.");
        }
        else
        {
            Assert.That(result.Succeeded, Is.True, result.Error);
            Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        }
    }
}
