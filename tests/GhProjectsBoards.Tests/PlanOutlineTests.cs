using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanOutlineTests
{
    internal static PlanDocument Document(string order = "p,a,b,c,q")
    {
        var rows = order.Split(',').Select(id => new PlanRow(id, id, "acme/repo") {
            Parent = id is "a" or "b" ? "p" : id == "c" ? "a" : null
        }).ToImmutableArray();
        return PlanCsvImportTests.Empty() with { Baseline = new(rows, []), State = new(rows, new() { DefaultRepository = "acme/repo" }) };
    }
    [Test]
    public void InsertInheritsTheFollowingRowsParent()
    {
        var row = PlanRow.New("new", "acme/repo");
        var result = PlanOperations.Apply(Document(), new InsertPlanRows([row], "b"), PlanCsvImportTests.Today);
        Assert.That(result.State.Rows.Single(r => r.Identity == row.Identity).Parent, Is.EqualTo("p"));
    }
    [Test]
    public void OutdentCarriesScatteredDescendantsAfterFormerParentsSubtree()
    {
        var result = PlanOperations.Apply(Document(), new IndentPlanRows(["a"], true), PlanCsvImportTests.Today);
        Assert.That(result.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "p", "b", "a", "c", "q" }));
        Assert.That(result.State.Rows.Single(r => r.Identity == "b").Parent, Is.EqualTo("p"));
    }
    [Test]
    public void CsvForwardReferencesBecomeParentFirst()
    {
        var preview = PlanCsvImport.Prepare(PlanCsvImportTests.Empty(), PlanCsvImportTests.Read(
            "c,C,,,,a,,\np,Parent,,,,,,\na,A,,,,p,,\nb,B,,,,p,,"), PlanCsvImportTests.Catalog, PlanCsvImportTests.Today);
        Assert.That(preview.Errors, Is.Empty);
        var result = PlanOperations.Apply(PlanCsvImportTests.Empty(), preview.Command!, PlanCsvImportTests.Today);
        Assert.That(result.State.Rows.Select(r => r.Title), Is.EqualTo(new[] { "Parent", "A", "C", "B" }));
    }
    [TestCase("a", "b", "p,a,c,b,q")]
    [TestCase("b", "a", "p,b,a,c,q")]
    [TestCase("a", null, "p,b,a,c,q")]
    [TestCase("a,c", null, "p,b,a,c,q")]
    [TestCase("a,b", null, "p,a,c,b,q")]
    [TestCase("p", null, "q,p,a,c,b")]
    public void SubtreesMoveTogetherIncludingScatteredOrSelectedDescendants(string targets, string? before, string expected)
    {
        var result = PlanOperations.Apply(Document(), new MovePlanSubtrees([.. targets.Split(',')], before), PlanCsvImportTests.Today);
        Assert.That(result.State.Rows.Select(r => r.Identity), Is.EqualTo(expected.Split(',')));
        Assert.That(result.Kind, Is.EqualTo(PlanOperationKind.Move));
    }
    [TestCase("a,q", null)]
    [TestCase("a", "c")]
    [TestCase("a", "q")]
    [TestCase("a", "a")]
    [TestCase("missing", null)]
    public void InvalidMoveRejectsWholeCommand(string targets, string? before)
        => Assert.Throws<ArgumentException>(() => PlanOperations.Apply(Document(), new MovePlanSubtrees([.. targets.Split(',')], before), PlanCsvImportTests.Today));

    [Test]
    public void ArrangeIsStableAndIdempotent()
    {
        var result = PlanOperations.Apply(Document("c,q,p,b,a"), new ArrangePlanOutline(), PlanCsvImportTests.Today);
        Assert.That(result.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "q", "p", "b", "a", "c" }));
        var again = PlanOperations.Apply(Document() with { State = result.State }, new ArrangePlanOutline(), PlanCsvImportTests.Today);
        Assert.That(again.State.Rows, Is.EqualTo(result.State.Rows));
    }
    [TestCase("a", true, null)]
    [TestCase("b", false, "a")]
    [TestCase("p", true, null)]
    public void StepSkipsNeighbourSubtree(string id, bool down, string? before)
        => Assert.That(new PlanOutline(Document().State.Rows).Step([id], down).Before, Is.EqualTo(before));
    [TestCase("a", false), TestCase("b", true)]
    public void StepAtGroupEdgeExplainsWhy(string id, bool down)
        => Assert.Throws<ArgumentException>(() => new PlanOutline(Document().State.Rows).Step([id], down));

    [TestCase(0, -1), TestCase(1, 2), TestCase(1.8, 2), TestCase(3, 3), TestCase(4, -1)]
    public void FoldedVisibleGapsSnapOnlyInsideParentGroup(double gap, int expected)
    {
        var gaps = new PlanOutline(Document().State.Rows).Gaps(["a"], ["p", "a", "b", "q"]);
        Assert.That(gaps.Snap(gap)?.Index ?? -1, Is.EqualTo(expected));
    }
    [Test]
    public void TopLevelGapsSpanWholePlan()
    {
        var gaps = new PlanOutline(Document().State.Rows).Gaps(["p"], ["p", "q"]);
        Assert.That((gaps.First, gaps.Last), Is.EqualTo((0, 2)));
        Assert.That(gaps.Snap(0)!.Before, Is.EqualTo("q"));
    }
    [Test]
    public void ExternalParentsAreTopLevelForArrangeButSeparateSiblingGroupsForMove()
    {
        ImmutableArray<PlanRow> rows = [new("a", "A", "acme/repo") { Parent = "external" },
            new("q", "Q", "acme/repo"), new("c", "C", "acme/repo") { Parent = "a" },
            new("b", "B", "acme/repo") { Parent = "external" }];
        var outline = new PlanOutline(rows);
        Assert.That(outline.IsContiguous, Is.False);
        Assert.That(outline.Arrange().Select(r => r.Identity), Is.EqualTo(new[] { "a", "c", "q", "b" }));
        Assert.That(new PlanOutline(outline.Arrange()).IsContiguous, Is.True);
        Assert.That(outline.Move(["b"], "a").Select(r => r.Identity), Is.EqualTo(new[] { "b", "a", "q", "c" }));
        Assert.That(outline.Gaps(["a"], ["a", "q", "b"]).Snap(3)!.Before, Is.Null);
        Assert.Throws<ArgumentException>(() => outline.Roots(["a", "q"]));
    }
    [TestCase(null), TestCase("q")]
    public void InsertAtBottomOrBeforeTopLevelDoesNotInheritExternalParent(string? before)
    {
        var d = Document(); var row = PlanRow.New("new", "acme/repo");
        d = d with { State = d.State with { Rows = d.State.Rows.SetItem(4, d.State.Rows[4] with { Parent = "external" }) } };
        Assert.That(PlanOperations.Apply(d, new InsertPlanRows([row], before), PlanCsvImportTests.Today).State.Rows.Single(r => r.Identity == row.Identity).Parent, Is.Null);
    }
    [Test]
    public void CsvExistingParentSplicesSubtreesAtItsTailAndAppendsOtherRoots()
    {
        var d = Document("p,a,c,b,q");
        var child = PlanRow.New("child", "acme/repo") with { Parent = "p" };
        var second = PlanRow.New("second", "acme/repo") with { Parent = "p" };
        var grandchild = PlanRow.New("grandchild", "acme/repo") with { Parent = child.Identity };
        var root = PlanRow.New("root", "acme/repo");
        var result = PlanOperations.Apply(d, new InsertPlanRows([grandchild, child, root, second], Kind: PlanOperationKind.CsvImport), PlanCsvImportTests.Today);
        Assert.That(result.State.Rows.Select(r => r.Title), Is.EqualTo(new[] { "p", "a", "c", "b", "child", "grandchild", "second", "q", "root" }));
        Assert.That(result.Kind, Is.EqualTo(PlanOperationKind.CsvImport));
    }
    [TestCase(false), TestCase(true)]
    public void SinglePatchUndoRedoRestoresExactOrderAndIdentities(bool arrange)
    {
        var d = Document();
        var result = PlanOperations.Apply(d, arrange ? new ArrangePlanOutline() : new MovePlanSubtrees(["a"]), PlanCsvImportTests.Today);
        var patch = PlanOperations.Difference(d.State, result.State, result.Kind);
        var undone = PlanOperations.Replay(result.State, patch, false);
        Assert.That(undone.Rows, Is.EqualTo(d.State.Rows));
        Assert.That(PlanOperations.Replay(undone, patch, true).Rows, Is.EqualTo(result.State.Rows));
    }
    [Test]
    public void MovingToCurrentPositionHasNoPatch()
    {
        var d = Document("p,a,c,b,q");
        var result = PlanOperations.Apply(d, new MovePlanSubtrees(["a"], "b"), PlanCsvImportTests.Today);
        var patch = PlanOperations.Difference(d.State, result.State, result.Kind);
        Assert.That(patch.BeforeOrder, Is.Null); Assert.That(patch.Rows, Is.Empty);
    }
    [Test]
    public void DisplayedRowAndPredecessorIdsFollowTheMovedOrder()
    {
        var document = Document("p,a,c,b,q");
        document = document with { State = document.State with { Rows = document.State.Rows.SetItem(4, document.State.Rows[4] with { Predecessors = ["b"] }) } };
        var result = PlanOperations.Apply(document, new MovePlanSubtrees(["b"], "a"), PlanCsvImportTests.Today);
        document = document with { State = result.State };
        Assert.That(PlanOperations.Schedule(document, PlanCsvImportTests.Today).Single(t => t.Input.Identity == "b").Input.RowId, Is.EqualTo(2));
        Assert.That(PlanSheetEditing.Parse(document, PlanField.Predecessors, "2", PlanCsvImportTests.Today), Is.EqualTo(new[] { "b" }));
        Assert.That(result.State.Rows.Single(r => r.Identity == "q").Predecessors, Is.EqualTo(new[] { "b" }));
    }
    [Test]
    public void OutdentMultipleRootsKeepsTheirOrderAndRemainingSiblingParent()
    {
        var document = Document("p,a,c,b,q");
        var result = PlanOperations.Apply(document, new IndentPlanRows(["b", "a"], true), PlanCsvImportTests.Today);
        Assert.That(result.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "p", "a", "c", "b", "q" }));
        Assert.That(result.State.Rows.Where(r => r.Identity is "a" or "b").Select(r => r.Parent), Is.All.Null);
        Assert.That(result.State.Rows.Single(r => r.Identity == "c").Parent, Is.EqualTo("a"));
    }
    [Test]
    public void ScatteredDescendantBeforeFirstSiblingDoesNotExtendTheDropSpan()
    {
        var outline = new PlanOutline(Document("c,p,a,b,q").State.Rows);
        var gaps = outline.Gaps(["b"], ["c", "p", "a", "b", "q"]);
        Assert.That(gaps.First, Is.EqualTo(2));
        Assert.That(gaps.Snap(1), Is.Null);
    }
}
