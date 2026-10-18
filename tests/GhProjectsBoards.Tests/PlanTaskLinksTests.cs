using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanTaskLinksTests
{
    private static readonly DateOnly Today = PlanCsvImportTests.Today;
    private static PlanRow Row(string id, string? parent = null, params string[] predecessors)
        => new(id, id, "acme/repo") { Parent = parent, Predecessors = [.. predecessors] };
    private static PlanDocument Document(params PlanRow[] rows)
        => PlanCsvImportTests.Empty() with { Baseline = new([.. rows], []), State = new([.. rows], new()) };
    private static PlanDocument Apply(PlanDocument document, bool unlink, params string[] selected)
    {
        var (command, reason) = PlanTaskLinks.Prepare(document.State, selected, unlink);
        Assert.That(reason, Is.Null); Assert.That(command, Is.Not.Null);
        Assert.That(command!.Kind, Is.EqualTo(PlanOperationKind.Cell));
        return document with { State = PlanOperations.Apply(document, command, Today).State };
    }
    private static void Predecessors(PlanDocument document, string id, params string[] expected)
        => Assert.That(document.State.Rows.Single(r => r.Identity == id).Predecessors, Is.EqualTo(expected), id);

    [Test]
    public void SiblingRangeChainsInPlanOrderAndPreservesExistingAndExternalReferences()
    {
        var document = Document(Row("a"), Row("b", null, "outside"), Row("c", null, "a"), Row("d"));
        var result = Apply(document, false, "c", "a", "b");
        Predecessors(result, "a"); Predecessors(result, "b", "outside", "a");
        Predecessors(result, "c", "a", "b"); Predecessors(result, "d");
    }
    [TestCase(false), TestCase(true)]
    public void SummarySelectionExpandsAllDescendantsWithOrWithoutVisibleChildren(bool childrenVisible)
    {
        var document = Document(Row("p"), Row("a", "p"), Row("nested", "p"), Row("b", "nested"), Row("c", "nested"), Row("d", "p"));
        var result = Apply(document, false, childrenVisible ? ["p", "a", "nested", "b", "c", "d"] : ["p"]);
        Predecessors(result, "a"); Predecessors(result, "d", "a");
        Predecessors(result, "b"); Predecessors(result, "c", "b"); Predecessors(result, "nested");
    }
    [Test]
    public void WholePlanChainsRequirementsSeparatelyWithoutCrossParentLinks()
    {
        var document = Document(Row("p"), Row("a", "p"), Row("b", "p"), Row("q"), Row("c", "q"), Row("d", "q"));
        var result = Apply(document, false, document.State.Rows.Select(r => r.Identity).ToArray());
        Predecessors(result, "p"); Predecessors(result, "q"); Predecessors(result, "a");
        Predecessors(result, "b", "a"); Predecessors(result, "c"); Predecessors(result, "d", "c");
    }
    [TestCase("a", "リンクできるタスクを2つ以上選択してください")]
    [TestCase("a,b", "選択したタスクはすでにリンクされています")]
    [TestCase("a,c", "リンクできるタスクを2つ以上選択してください")]
    [TestCase("", "リンクできるタスクを2つ以上選択してください")]
    public void UnavailableLinkReturnsReasonWithoutCommand(string selection, string expected)
    {
        var document = Document(Row("p"), Row("a", "p"), Row("b", "p", "a"), Row("c"));
        var (command, reason) = PlanTaskLinks.Prepare(document.State, selection.Split(',', StringSplitOptions.RemoveEmptyEntries), false);
        Assert.That(command, Is.Null); Assert.That(reason, Is.EqualTo(expected));
    }
    [Test]
    public void UnlinkChainRemovesOnlyLinksInsideExpandedSelection()
    {
        var document = Document(Row("a"), Row("b", null, "a", "outside"), Row("c", null, "b"), Row("d", null, "c"));
        var result = Apply(document, true, "a", "b", "c");
        Predecessors(result, "a"); Predecessors(result, "b", "outside"); Predecessors(result, "c"); Predecessors(result, "d", "c");
    }
    [Test]
    public void SingleTaskUnlinkRemovesBothDirectionsButPreservesExternalAndSummaryPredecessors()
    {
        var document = Document(Row("a"), Row("b", null, "a", "outside"), Row("c", null, "b", "external"), Row("p", null, "b"), Row("child", "p"));
        var result = Apply(document, true, "b");
        Predecessors(result, "b", "outside"); Predecessors(result, "c", "external"); Predecessors(result, "p", "b");
    }
    [Test]
    public void ExpandedUnlinkIncludesSelectedSummaryReferencesAndNeverEditsSummaryPredecessors()
    {
        var document = Document(Row("p", null, "outside"), Row("a", "p", "p", "external"), Row("b", "p", "a"));
        var (command, reason) = PlanTaskLinks.Prepare(document.State, ["p"], true);
        Assert.That(reason, Is.Null);
        Assert.That(command!.Cells.Select(c => c.Identity), Is.EqualTo(new[] { "a", "b" }));
        var result = Apply(document, true, "p");
        Predecessors(result, "p", "outside"); Predecessors(result, "a", "external"); Predecessors(result, "b");
    }
    [Test]
    public void LinkNeverChangesSummaryPredecessors()
    {
        var result = Apply(Document(Row("p", null, "outside"), Row("a", "p"), Row("b", "p")), false, "p");
        Predecessors(result, "p", "outside"); Predecessors(result, "b", "a");
    }
    [TestCase("a"), TestCase("a,b"), TestCase("")]
    public void UnlinkWithoutInternalLinksReturnsReason(string selection)
    {
        var (command, reason) = PlanTaskLinks.Prepare(Document(Row("a", null, "outside"), Row("b")).State,
            selection.Split(',', StringSplitOptions.RemoveEmptyEntries), true);
        Assert.That(command, Is.Null); Assert.That(reason, Is.EqualTo("解除するリンクがありません"));
    }
    [Test, Category("PlanTaskLinksStorage")]
    public async Task CycleIsRejectedBySessionWithoutChangingStateOrHistory()
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "task-links-" + Guid.NewGuid().ToString("N"));
        try {
            var session = await PlanSession.CreateAsync(new(root), Document(Row("a", null, "b"), Row("b")), Today);
            Assert.That((await session.FlushAsync()).Succeeded, Is.True);
            await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("a", PlanField.Title, "edited")]), Today);
            await session.Undo(Today);
            var before = session.Document; var undo = session.UndoCount; var redo = session.RedoCount;
            var (command, reason) = PlanTaskLinks.Prepare(before.State, ["a", "b"], false);
            Assert.That(reason, Is.Null);
            Assert.That(() => session.Execute(command!, Today), Throws.TypeOf<PlanCycleException>().With.Message.EqualTo("循環参照: ID 1, ID 2"));
            Assert.That(session.Document, Is.SameAs(before));
            Assert.That((session.UndoCount, session.RedoCount), Is.EqualTo((undo, redo)));
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TestCase(false), TestCase(true), Category("PlanTaskLinksStorage")]
    public async Task OneSessionUndoRestoresAllPredecessors(bool unlink)
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "task-links-" + Guid.NewGuid().ToString("N"));
        try {
            var document = Document(Row("a"), Row("b", null, unlink ? ["a", "outside"] : ["outside"]), Row("c", null, unlink ? ["b"] : []));
            var session = await PlanSession.CreateAsync(new(root), document, Today);
            Assert.That((await session.FlushAsync()).Succeeded, Is.True);
            var (command, _) = PlanTaskLinks.Prepare(document.State, ["a", "b", "c"], unlink);
            Assert.That((await session.Execute(command!, Today)).Succeeded, Is.True);
            Assert.That(session.UndoCount, Is.EqualTo(1));
            Assert.That((await session.Undo(Today)).Succeeded, Is.True);
            Assert.That(session.Document.State.Rows, Is.EqualTo(document.State.Rows));
            Assert.That((session.UndoCount, session.RedoCount), Is.EqualTo((0, 1)));
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
