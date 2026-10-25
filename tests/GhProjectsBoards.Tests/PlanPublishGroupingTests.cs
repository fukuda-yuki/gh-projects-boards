using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #224: the publish review groups recalculated dates under the edit that caused them and shows a parent's
// rewritten child order only around the rows that changed it.
[TestFixture, Category("PlanPublishReview")]
internal sealed class PlanPublishGroupingTests
{
    private static DateOnly D(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd");
    private static readonly DateOnly Monday = D("2026-10-05");
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");
    private static PlanDocument Document(ImmutableArray<PlanRow> rows) => new(Project, new(rows, []), new(rows, new()
    {
        StatusDate = Monday,
        Columns = [new(PlanField.Start, "start", "Start date", "DATE"), new(PlanField.End, "end", "Target date", "DATE")]
    }));
    private static PlanDocument Apply(PlanDocument document, PlanCommand command)
        => document with { State = PlanOperations.Apply(document, command, Monday).State };
    private static ImmutableArray<PlanRecalculationGroup> Groups(PlanDocument document)
    {
        var schedule = PlanOperations.Schedule(document, Monday);
        return PlanPublishGrouping.RecalculationGroups(document, PlanOperations.Changes(document, Monday, schedule), schedule);
    }
    private static PlanRow Task(string id, string start, string end, decimal hours, params string[] predecessors)
        => new(id, id, "acme/repo") { Start = D(start), End = D(end), Estimate = hours, Remaining = hours, Predecessors = [.. predecessors] };

    // Two independent chains: A → B → C and X → Y, one day of work each.
    private static ImmutableArray<PlanRow> Chains() => [
        Task("A", "2026-10-05", "2026-10-05", 8), Task("B", "2026-10-06", "2026-10-06", 8, "A"), Task("C", "2026-10-07", "2026-10-07", 8, "B"),
        Task("X", "2026-10-05", "2026-10-05", 8), Task("Y", "2026-10-06", "2026-10-06", 8, "X")];

    [Test]
    public void ARemainingEditGroupsItsOwnEndAndEverySuccessorItMovedUnderThatEdit()
    {
        var edited = Apply(Document(Chains()), new EditPlanCells(PlanOperationKind.Cell, [new("A", PlanField.Remaining, 16m)]));
        var groups = Groups(edited);
        Assert.That(groups, Has.Length.EqualTo(1));
        Assert.That(groups[0].Cause, Is.EqualTo("A"));
        Assert.That(groups[0].CauseFields, Is.EqualTo(new[] { PlanField.Remaining }));
        Assert.That(groups[0].Tasks, Is.EqualTo(new[] { "A", "B", "C" }));
    }

    [Test]
    public void TwoEditsInIndependentChainsMakeTwoGroupsInPlanOrder()
    {
        var edited = Apply(Document(Chains()), new EditPlanCells(PlanOperationKind.Paste,
            [new("X", PlanField.Remaining, 16m), new("B", PlanField.Remaining, 16m)]));
        var groups = Groups(edited);
        Assert.That(groups.Select(g => (g.Cause, string.Join(",", g.Tasks))), Is.EqualTo(new[] { ((string?)"B", "B,C"), ("X", "X,Y") }));
    }

    [Test]
    public void ATaskDownstreamOfTwoEditsBelongsToTheNearerOne()
    {
        var edited = Apply(Document(Chains()), new EditPlanCells(PlanOperationKind.Paste,
            [new("A", PlanField.Remaining, 16m), new("B", PlanField.Remaining, 16m)]));
        var groups = Groups(edited);
        Assert.That(groups.Select(g => (g.Cause, string.Join(",", g.Tasks))), Is.EqualTo(new[] { ((string?)"A", "A"), ("B", "B,C") }));
    }

    [Test]
    public void AParentRollUpBelongsToTheEditOfItsChild()
    {
        ImmutableArray<PlanRow> rows = [
            new("R", "Requirement", "acme/repo") { Start = D("2026-10-05"), End = D("2026-10-06") },
            Task("A", "2026-10-05", "2026-10-05", 8) with { Parent = "R" }, Task("B", "2026-10-06", "2026-10-06", 8, "A") with { Parent = "R" }];
        var edited = Apply(Document(rows), new EditPlanCells(PlanOperationKind.Cell, [new("B", PlanField.Remaining, 16m)]));
        var groups = Groups(edited);
        Assert.That(groups, Has.Length.EqualTo(1));
        Assert.That(groups[0].Cause, Is.EqualTo("B"));
        Assert.That(groups[0].Tasks, Does.Contain("R").And.Contain("B"));
    }

    [Test]
    public void ATitleEditCausesNoRecalculationAndAMoveWithoutAnEditHasNoCause()
    {
        // GitHub holds an end the plan does not reach: the plan's date is recalculated with no task edit behind it.
        var rows = Chains().SetItem(1, Task("B", "2026-10-06", "2026-10-09", 8, "A"));
        var document = Document(rows);
        var stale = document with { Baseline = document.Baseline with { Rows = document.Baseline.Rows.SetItem(2, Task("C", "2026-10-08", "2026-10-08", 8, "B")) } };
        var edited = Apply(stale, new EditPlanCells(PlanOperationKind.Cell, [new("A", PlanField.Title, "renamed")]));
        var groups = Groups(edited);
        Assert.That(groups, Is.Not.Empty);
        Assert.That(groups.All(g => g.Cause is null), Is.True);
        Assert.That(PlanPublishGrouping.CauseFields(PlanOperations.Changes(edited, Monday), "A"), Is.Empty);
    }

    [Test]
    public void ANewTaskIsItsOwnCause()
    {
        var document = Document(Chains());
        var added = PlanRow.New("new", "acme/repo") with { Estimate = 8, Remaining = 8 };
        var inserted = Apply(document, new InsertPlanRows([added], "B"));
        var changes = PlanOperations.Changes(inserted, Monday);
        Assert.That(PlanPublishGrouping.CauseFields(changes, added.Identity), Is.EqualTo(new[] { PlanField.NewTask }));
    }

    [TestCase("a,b,c,d,e", "a,b,N,c,d,e", "N+", "b,N,c")]
    [TestCase("a,b,c,d,e", "a,N,b,c,d,e,M", "N+ M+", "a,N,b|e,M")]
    [TestCase("a,b,c,d,e", "a,c,d,b,e", "b", "d,b,e")]
    [TestCase("a,b,c,d,e", "N,a,b,c,d,e", "N+", "N,a")]
    [TestCase("a,b,c", "a,c", "b", "")]
    public void AChildOrderExcerptNamesTheChangedChildrenAndOnlyTheirNeighbours(string before, string after, string causes, string windows)
    {
        var document = Document(before.Split(',').Select(id => new PlanRow(id, id, "acme/repo")).ToImmutableArray());
        var excerpt = PlanPublishGrouping.ChildOrderExcerpt(document, [.. before.Split(',')], [.. after.Split(',')]);
        Assert.That(string.Join(" ", excerpt.Causes.Select(c => c.Identity + (c.Added ? "+" : ""))), Is.EqualTo(causes));
        Assert.That(string.Join("|", excerpt.Windows.Select(w => string.Join(",", w))), Is.EqualTo(windows));
    }
}
