using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;
// #247: a row that was never published can be removed from the plan without losing the work done
// after the mistaken insertion, and a selection that reaches a published Issue changes nothing.
[TestFixture]
internal sealed class PlanRowDeleteTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");
    private string root = null!;
    [SetUp] public void Setup() { root = Path.Combine(Path.GetTempPath(), "ghpb-rowdelete-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TearDown] public void Cleanup() => Directory.Delete(root, true);
    // Published rows carry Issues #17, #18…, so refusals name them as the team does (#263).
    private static PlanDocument Initial(params PlanRow[] rows)
        => new(Project, new([.. rows], []), new([.. rows], new() { DefaultRepository = "acme/repo", StatusDate = Today, ProjectStart = Today }))
        { Sync = new() { IssueLinks = rows.Select((r, i) => (r.Identity, Number: 17 + i)).ToImmutableDictionary(l => l.Identity,
            l => new PlanIssueLink($"acme/repo#{l.Number}", $"https://github.com/acme/repo/issues/{l.Number}")) } };
    private static EditPlanCells Edit(string id, string field, object? value) => new(PlanOperationKind.Cell, [new(id, field, value)]);
    private static string[] Ids(PlanDocument document) => document.State.Rows.Select(r => r.Identity).ToArray();
    private static PlannedDeletion Planned(string title = "誤って挿入")
    {
        var parent = PlanRow.New(title);
        var child = PlanRow.New(title + "の子");
        return new(parent, child);
    }
    private sealed record PlannedDeletion(PlanRow Parent, PlanRow Child);
    private async Task<PlanSession> SessionWithSubtree(PlanRow parent, PlanRow child, params PlanRow[] baseline)
    {
        var session = await PlanSession.CreateAsync(new(root), Initial(baseline), Today);
        await session.Execute(new InsertPlanRows([parent, child]), Today);
        await session.Execute(new IndentPlanRows([child.Identity]), Today);
        return session;
    }

    [Test]
    public async Task DeletingALocalRowRemovesItsWholeSubtreeAsOneUndoStep()
    {
        var planned = Planned();
        var session = await SessionWithSubtree(planned.Parent, planned.Child, new PlanRow("A", "Existing", "acme/repo"));
        Assert.That(Ids(session.Document), Is.EqualTo(new[] { "A", planned.Parent.Identity, planned.Child.Identity }));
        var steps = session.UndoCount;
        Assert.That((await session.Execute(new DeletePlanRows([planned.Parent.Identity]), Today)).Succeeded, Is.True);
        Assert.That(Ids(session.Document), Is.EqualTo(new[] { "A" }));
        Assert.That(session.UndoCount, Is.EqualTo(steps + 1), "Deleting a subtree is exactly one Undo step.");
        Assert.That(session.Changes(Today).TaskCount, Is.Zero, "The mistaken insertion leaves nothing unpublished behind.");
        Assert.That((await session.Undo(Today)).Succeeded, Is.True);
        Assert.That(Ids(session.Document), Is.EqualTo(new[] { "A", planned.Parent.Identity, planned.Child.Identity }));
        Assert.That(session.Document.State.Rows[1].Title, Is.EqualTo("誤って挿入"));
        Assert.That(session.Document.State.Rows[2].Parent, Is.EqualTo(planned.Parent.Identity));
        Assert.That((await session.Redo(Today)).Succeeded, Is.True);
        Assert.That(Ids(session.Document), Is.EqualTo(new[] { "A" }));
        Assert.That(session.Document.Baseline.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "A" }));
    }

    [Test]
    public async Task DeletingTheMistakenRowKeepsTheWorkDoneAfterTheInsertion()
    {
        // The reported dead end: the only way out was Undo past the insertion, which discarded the
        // later edit as well (#247).
        var planned = Planned();
        var session = await SessionWithSubtree(planned.Parent, planned.Child, new PlanRow("A", "Existing", "acme/repo"));
        await session.Execute(Edit("A", PlanField.Estimate, 21m), Today);
        await session.Execute(new DeletePlanRows([planned.Parent.Identity]), Today);
        Assert.That(Ids(session.Document), Is.EqualTo(new[] { "A" }));
        Assert.That(session.Changes(Today).Fields.Keys, Is.EqualTo(new[] { "A" }));
        Assert.That((await session.Undo(Today)).Succeeded, Is.True);
        Assert.That(Ids(session.Document), Is.EqualTo(new[] { "A", planned.Parent.Identity, planned.Child.Identity }), "Undo restores the deleted subtree.");
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "A").Estimate, Is.EqualTo(21m), "Undoing the delete keeps the work done after the insertion.");
        var reopened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(reopened.Status, Is.EqualTo(PlanLoadStatus.Loaded), reopened.Error);
        Assert.That(reopened.Session!.Document.State.Rows.Single(r => r.Identity == "A").Estimate, Is.EqualTo(21m));
    }

    [Test]
    public async Task DeletingAPublishedRowIsRefusedAndChangesNothing()
    {
        var session = await PlanSession.CreateAsync(new(root), Initial(new PlanRow("A", "Existing", "acme/repo"), new PlanRow("B", "Other", "acme/repo")), Today);
        var error = Assert.Throws<ArgumentException>(() => { _ = session.Execute(new DeletePlanRows(["B"]), Today); });
        Assert.That(error!.Message, Does.Contain("GitHub の Issue は削除できません（#18）"));
        Assert.That(error.Message, Does.Contain("完了・中止・この版から外す"));
        Assert.That(Ids(session.Document), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(session.UndoCount, Is.Zero, "A refused delete creates no history.");
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }

    [Test]
    public async Task DeletingALocalRowWhoseSubtreeHoldsAPublishedIssueIsRefused()
    {
        var session = await PlanSession.CreateAsync(new(root), Initial(new PlanRow("A", "Existing", "acme/repo")), Today);
        var parent = PlanRow.New("誤って挿入");
        await session.Execute(new InsertPlanRows([parent], "A"), Today);
        await session.Execute(new IndentPlanRows(["A"]), Today);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "A").Parent, Is.EqualTo(parent.Identity));
        var error = Assert.Throws<ArgumentException>(() => { _ = session.Execute(new DeletePlanRows([parent.Identity]), Today); });
        Assert.That(error!.Message, Does.Contain("GitHub の Issue は削除できません（#17）"));
        Assert.That(Ids(session.Document), Is.EqualTo(new[] { parent.Identity, "A" }));
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "A").Parent, Is.EqualTo(parent.Identity), "A refused delete changes no relationship.");
        Assert.That(session.UndoCount, Is.EqualTo(2));
    }

    [Test]
    public async Task DeletingARowDropsItFromTheSurvivingPredecessors()
    {
        var session = await PlanSession.CreateAsync(new(root), Initial(new PlanRow("A", "Existing", "acme/repo"), new PlanRow("B", "Other", "acme/repo")), Today);
        var inserted = PlanRow.New("誤って挿入");
        await session.Execute(new InsertPlanRows([inserted], "B"), Today);
        await session.Execute(Edit("B", PlanField.Predecessors, ImmutableArray.Create(inserted.Identity)), Today);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "B").Predecessors, Is.EqualTo(new[] { inserted.Identity }));
        Assert.That((await session.Execute(new DeletePlanRows([inserted.Identity]), Today)).Succeeded, Is.True);
        Assert.That(Ids(session.Document), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "B").Predecessors, Is.Empty, "A deleted row leaves no dangling link behind.");
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }

    [Test]
    public async Task DeletingAChildReschedulesTheParentRollUp()
    {
        var session = await PlanSession.CreateAsync(new(root), Initial(new PlanRow("A", "Existing", "acme/repo")), Today);
        var parent = PlanRow.New("誤って挿入");
        var first = PlanRow.New("長い子");
        var second = PlanRow.New("短い子");
        await session.Execute(new InsertPlanRows([parent, first, second]), Today);
        await session.Execute(new IndentPlanRows([first.Identity]), Today);
        await session.Execute(new IndentPlanRows([second.Identity]), Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell,
            [new(first.Identity, PlanField.Estimate, 10m), new(second.Identity, PlanField.Estimate, 1m)]), Today);
        var before = PlanOperations.Schedule(session.Document, Today);
        var summary = before.Single(t => t.Input.Identity == parent.Identity);
        var shortChild = before.Single(t => t.Input.Identity == second.Identity);
        Assert.That(summary.IsSummary, Is.True);
        Assert.That(summary.End.Value > shortChild.End.Value, Is.True, "The roll-up follows the longer child.");
        Assert.That((await session.Execute(new DeletePlanRows([first.Identity]), Today)).Succeeded, Is.True);
        var after = PlanOperations.Schedule(session.Document, Today);
        Assert.That(after.Single(t => t.Input.Identity == parent.Identity).End.Value, Is.EqualTo(shortChild.End.Value));
    }

    [Test]
    public async Task DeletingAMistakenRowLeavesItsSiblingsPlanDatesAlone()
    {
        // The sibling's end is a plan value that a reschedule would move; deleting a row it never depended
        // on must not reach it through the shared parent (#236 dates are plan values).
        var parent = new PlanRow("P", "Requirement", "acme/repo");
        var sibling = new PlanRow("S", "Sibling", "acme/repo") { Parent = "P", Estimate = 8, Remaining = 8, Start = Today, End = Today.AddDays(9) };
        var session = await PlanSession.CreateAsync(new(root), Initial(parent, sibling), Today);
        var mistaken = PlanRow.New("誤って挿入");
        await session.Execute(new InsertPlanRows([mistaken], Before: "S"), Today);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == mistaken.Identity).Parent, Is.EqualTo("P"));
        Assert.That((await session.Execute(new DeletePlanRows([mistaken.Identity]), Today)).Succeeded, Is.True);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "S").End, Is.EqualTo(Today.AddDays(9)));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }

    [Test]
    public async Task DeletingOneOfTwoSelectedSubtreesDeletesBoth()
    {
        var one = Planned("一つ目");
        var two = Planned("二つ目");
        var session = await PlanSession.CreateAsync(new(root), Initial(new PlanRow("A", "Existing", "acme/repo")), Today);
        await session.Execute(new InsertPlanRows([one.Parent, one.Child]), Today);
        await session.Execute(new IndentPlanRows([one.Child.Identity]), Today);
        await session.Execute(new InsertPlanRows([two.Parent, two.Child]), Today);
        await session.Execute(new IndentPlanRows([two.Child.Identity]), Today);
        Assert.That((await session.Execute(new DeletePlanRows([two.Parent.Identity, one.Parent.Identity]), Today)).Succeeded, Is.True);
        Assert.That(Ids(session.Document), Is.EqualTo(new[] { "A" }));
        Assert.That(session.UndoCount, Is.EqualTo(5), "Four insert/indent steps and one delete step.");
        Assert.That((await session.Undo(Today)).Succeeded, Is.True);
        Assert.That(Ids(session.Document), Is.EqualTo(new[] { "A", one.Parent.Identity, one.Child.Identity, two.Parent.Identity, two.Child.Identity }));
    }
}
