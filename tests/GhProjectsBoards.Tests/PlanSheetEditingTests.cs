using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class PlanSheetEditingTests
{
    private static readonly DateOnly Day = new(2026, 10, 6);
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
            Assert.That(PlanSheetEditing.Parse(document, field, input, Day), Is.EqualTo(DateOnly.Parse(expected)));
    }
    [TestCase("2/30"), TestCase("garbage"), TestCase("10-14"), TestCase("2026/2/30")]
    public void InvalidDateExplainsAllAcceptedForms(string input)
    {
        Assert.That(() => PlanSheetEditing.Parse(Document, PlanField.Start, input, Day), Throws.ArgumentException
            .With.Message.EqualTo("日付は 2026-10-14、2026/10/14 または 10/14 の形で入力してください。"));
    }
    [Test]
    public void UndatedDocumentUsesSuppliedDayForTypedAndPastedShortDates()
    {
        var today = new DateOnly(2030, 12, 30);
        Assert.That(PlanSheetEditing.Parse(Document, PlanField.Start, "1/2", today), Is.EqualTo(new DateOnly(2031, 1, 2)));
        var paste = PlanSheetEditing.Paste(Document, ["I1"], [PlanField.Start], new(0, 0), "1/2", today);
        Assert.That(paste.Cells.Single().Value, Is.EqualTo(new DateOnly(2031, 1, 2)));
    }

    [Test]
    public void DisplayedPredecessorNumbersResolveAgainstTheWholePlan()
    {
        Assert.That(PlanSheetEditing.Parse(Document, PlanField.Predecessors, "1, 3", Day), Is.EqualTo(new[] { "I1", "I3" }));
        Assert.That(PlanSheetEditing.Parse(Document, PlanField.Assignees, "alice", Day), Is.EqualTo(new[] { "U1" }));
        Assert.Throws<ArgumentException>(() => PlanSheetEditing.Parse(Document, PlanField.Predecessors, "4", Day));
        Assert.Throws<ArgumentException>(() => PlanSheetEditing.Parse(Document, PlanField.Assignees, "unknown", Day));
    }
    [Test]
    public void FilteredRowsKeepTheirIdentityAndEmptyPasteFieldsLeaveThemUnchanged()
    {
        var command = PlanSheetEditing.Paste(Document, ["I3", "I1"], [PlanField.Title, PlanField.Remaining],
            new(0, 0), "third edited\t16\n\t8", Day);
        Assert.That(command.Kind, Is.EqualTo(PlanOperationKind.Paste));
        Assert.That(command.Cells, Is.EqualTo(new PlanCellChange[] {
            new("I3", PlanField.Title, "third edited"), new("I3", PlanField.Remaining, 16m),
            new("I1", PlanField.Remaining, 8m) }));
    }
    [TestCase("changed\nvalue")]
    [TestCase("changed\n\n")]
    [TestCase("")]
    public void PasteRejectsEveryMissingDestinationIncludingEmptyCells(string text)
    {
        Assert.That(() => PlanSheetEditing.Paste(Document, ["I1", "removed"], [PlanField.Title],
            new(0, 0, 2, 1), text, Day), Throws.ArgumentException.With.Message.EqualTo("空行を除いて範囲を選択してください。"));
    }
    [Test]
    public void PasteIgnoresMissingRowsOutsideItsDestination()
    {
        var command = PlanSheetEditing.Paste(Document, ["I1", "removed"], [PlanField.Title], new(0, 0), "changed", Day);
        Assert.That(command.Cells, Is.EqualTo(new[] { new PlanCellChange("I1", PlanField.Title, "changed") }));
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
                [PlanField.Remaining, PlanField.Actual], new(0, 0, height, width), text, Day);
            Assert.That(result.Cells.Length, Is.EqualTo(text == "" ? 0 : height));
        }
        else Assert.Throws<ArgumentException>(() => PlanSheetEditing.Paste(Document, ["I1", "I2", "I3"],
            [PlanField.Remaining, PlanField.Actual], new(0, 0, height, width), text, Day));
    }
}
