using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class PlanSheetEditingTests
{
    private static readonly PlanDocument Document = new(new(new("github.com", 1), "P1"),
        new([], []), new([
            new("I1", "first", "acme/repo"),
            new("I2", "second", "acme/repo"),
            new("I3", "third", "acme/repo")],
            new() { People = [new("U1", "alice", 100, null)] }));
    [TestCase("2026-10-14", "2026-10-05", "2026-10-14")]
    [TestCase("2026/10/14", "2026-10-05", "2026-10-14")]
    [TestCase("10/14", "2026-10-05", "2026-10-14")]
    [TestCase("1/2", "2026-12-30", "2027-01-02")]
    [TestCase("12/30", "2027-01-02", "2026-12-30")]
    [TestCase("1/1", "2024-07-02", "2025-01-01")]
    public void DateInputUsesExplicitFormsAndNearestStatusYear(string input, string status, string expected)
    {
        var document = Document with { State = Document.State with { Settings = Document.State.Settings with { StatusDate = DateOnly.Parse(status) } } };
        foreach (var field in new[] { PlanField.Start, PlanField.End, PlanField.StartNoEarlierThan })
            Assert.That(PlanSheetEditing.Parse(document, field, input), Is.EqualTo(DateOnly.Parse(expected)));
    }
    [TestCase("2/30"), TestCase("garbage"), TestCase("10-14"), TestCase("2026/2/30")]
    public void InvalidDateExplainsAllAcceptedForms(string input)
    {
        Assert.That(() => PlanSheetEditing.Parse(Document, PlanField.Start, input), Throws.ArgumentException
            .With.Message.EqualTo("日付は 2026-10-14、2026/10/14 または 10/14 の形で入力してください。"));
    }
    [Test]
    public void DisplayedPredecessorNumbersResolveAgainstTheWholePlan()
    {
        Assert.That(PlanSheetEditing.Parse(Document, PlanField.Predecessors, "1, 3"), Is.EqualTo(new[] { "I1", "I3" }));
        Assert.That(PlanSheetEditing.Parse(Document, PlanField.Assignees, "alice"), Is.EqualTo(new[] { "U1" }));
        Assert.Throws<ArgumentException>(() => PlanSheetEditing.Parse(Document, PlanField.Predecessors, "4"));
        Assert.Throws<ArgumentException>(() => PlanSheetEditing.Parse(Document, PlanField.Assignees, "unknown"));
    }
    [Test]
    public void FilteredRowsKeepTheirIdentityAndEmptyPasteFieldsLeaveThemUnchanged()
    {
        var command = PlanSheetEditing.Paste(Document, ["I3", "I1"], [PlanField.Title, PlanField.Remaining],
            new(0, 0), "third edited\t16\n\t8");
        Assert.That(command.Kind, Is.EqualTo(PlanOperationKind.Paste));
        Assert.That(command.Cells, Is.EqualTo(new PlanCellChange[] {
            new("I3", PlanField.Title, "third edited"), new("I3", PlanField.Remaining, 16m),
            new("I1", PlanField.Remaining, 8m) }));
    }
    [TestCase(3, 1, "16\n8", false)]
    [TestCase(2, 2, "16", false)]
    [TestCase(2, 1, "16\t8\n8\t16", false)]
    [TestCase(2, 1, "16\n8", true)]
    [TestCase(3, 1, "", true)]
    [TestCase(3, 1, "16", true)]
    public void PasteRequiresMatchingRectangleOrSingleColumnBroadcast(int height, int width, string text, bool accepted)
    {
        if (accepted)
        {
            var result = PlanSheetEditing.Paste(Document, ["I1", "I2", "I3"],
                [PlanField.Remaining, PlanField.Actual], new(0, 0, height, width), text);
            Assert.That(result.Cells.Length, Is.EqualTo(text == "" ? 0 : height));
        }
        else Assert.Throws<ArgumentException>(() => PlanSheetEditing.Paste(Document, ["I1", "I2", "I3"],
            [PlanField.Remaining, PlanField.Actual], new(0, 0, height, width), text));
    }
}
