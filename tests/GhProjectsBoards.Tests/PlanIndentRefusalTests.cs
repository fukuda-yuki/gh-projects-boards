using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #241: an indent refused because of an existing link must say which rows and which link block it.
[TestFixture]
internal sealed class PlanIndentRefusalTests
{
    private static readonly DateOnly Today = PlanCsvImportTests.Today;
    private static PlanRow Row(string id, string? parent = null, params string[] predecessors)
        => new(id, id, "acme/repo") { Parent = parent, Predecessors = [.. predecessors] };
    private static PlanDocument Document(params PlanRow[] rows)
        => PlanCsvImportTests.Empty() with { Baseline = new([.. rows], []), State = new([.. rows], new()) };
    private static string Refusal(PlanDocument document, params string[] targets)
        => Assert.Throws<PlanCycleException>(() => PlanOperations.Apply(document, new IndentPlanRows([.. targets]), Today))!.Message;

    [Test]
    public void SuccessorIndentedUnderItsPredecessorNamesBothRowsAndTheLink() =>
        Assert.That(Refusal(Document(Row("a"), Row("b", null, "a")), "b"),
            Is.EqualTo("ID 2 は ID 1 の後に始まるため、ID 1 の下にインデントできません。先にリンクを解除してください。"));

    [Test]
    public void NewParentLinkedAfterItsNewChildNamesItsOwnLink() =>
        Assert.That(Refusal(Document(Row("a", null, "b"), Row("b")), "b"),
            Is.EqualTo("ID 1 は ID 2 の後に始まるため、ID 1 の下にインデントできません。先にリンクを解除してください。"));

    [Test]
    public void DescendantLinkToTheNewParentIsNamedByItsOwnRowId() =>
        Assert.That(Refusal(Document(Row("a"), Row("b"), Row("c", "b", "a")), "b"),
            Is.EqualTo("ID 3 は ID 1 の後に始まるため、ID 1 の下にインデントできません。先にリンクを解除してください。"));

    [Test]
    public void LinkOutsideTheNewSubtreeDoesNotRefuseTheIndent()
    {
        var result = PlanOperations.Apply(Document(Row("a"), Row("b"), Row("c", null, "a")), new IndentPlanRows(["b"]), Today);
        Assert.That(result.State.Rows.Single(r => r.Identity == "b").Parent, Is.EqualTo("a"));
    }

    [Test]
    public void MissingPrecedingSiblingKeepsItsOwnMessage() =>
        Assert.That(Assert.Throws<ArgumentException>(() =>
                PlanOperations.Apply(Document(Row("a"), Row("b")), new IndentPlanRows(["a"]), Today))!.Message,
            Is.EqualTo("直前の同階層タスクがありません。"));

    [Test, Category("PlanIndentStorage")]
    public async Task RefusedIndentChangesNothingAndKeepsHistoryAndRedo()
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "indent-refusal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await PlanSession.CreateAsync(new(root), Document(Row("a"), Row("b", null, "a")), Today);
            Assert.That((await session.FlushAsync()).Succeeded, Is.True);
            await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("a", PlanField.Title, "edited")]), Today);
            await session.Undo(Today);
            var before = session.Document; var undo = session.UndoCount; var redo = session.RedoCount;
            Assert.That(() => session.Execute(new IndentPlanRows(["b"]), Today),
                Throws.TypeOf<PlanCycleException>().With.Message
                    .EqualTo("ID 2 は ID 1 の後に始まるため、ID 1 の下にインデントできません。先にリンクを解除してください。"));
            Assert.That(session.Document, Is.SameAs(before));
            Assert.That((session.UndoCount, session.RedoCount), Is.EqualTo((undo, redo)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
