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
    [TestCase("10月14日", "2026-10-05", "2026-10-14")]
    [TestCase("１０月１４日", "2026-10-05", "2026-10-14")]
    [TestCase("２・１０", "2026-10-05", "2027-02-10")]
    [TestCase("２０２６・１０・１４", "2026-10-05", "2026-10-14")]
    public void DateInputUsesExplicitFormsAndNearestStatusYear(string input, string status, string expected)
    {
        var document = Document with { State = Document.State with { Settings = Document.State.Settings with { StatusDate = DateOnly.Parse(status) } } };
        foreach (var field in new[] { PlanField.Start, PlanField.End, PlanField.StartNoEarlierThan })
            Assert.That(PlanSheetEditing.Parse(document, field, input, Day), Is.EqualTo(DateOnly.Parse(expected)));
    }
    [TestCase("2/30"), TestCase("garbage"), TestCase("10-14"), TestCase("2026/2/30")]
    [TestCase("2026年10月14日"), TestCase("10月14"), TestCase("10月"), TestCase("10/14日")]
    public void InvalidDateExplainsAllAcceptedForms(string input)
    {
        Assert.That(() => PlanSheetEditing.Parse(Document, PlanField.Start, input, Day), Throws.ArgumentException
            .With.Message.EqualTo("日付は 2026-10-14、2026/10/14 または 10/14 の形で入力してください。"));
    }
    [TestCase("8", 8)]
    [TestCase("8h", 8)]
    [TestCase("8H", 8)]
    [TestCase("1d", 8)]
    [TestCase("1.5h", 1.5)]
    [TestCase("２４", 24)]
    [TestCase("８", 8)]
    [TestCase("８ｈ", 8)]
    [TestCase("１ｄ", 8)]
    public void EffortAcceptsTheDisplayedUnitTheDayUnitAndFullWidthDigits(string input, decimal expected)
    {
        foreach (var field in new[] { PlanField.Estimate, PlanField.Remaining, PlanField.Actual })
            Assert.That(PlanSheetEditing.Parse(Document, field, input, Day), Is.EqualTo(expected), field);
    }
    [TestCase("8x"), TestCase("1w"), TestCase("8hh"), TestCase("h"), TestCase("abc"), TestCase("８x")]
    [TestCase("-8"), TestCase("-1d")]
    [TestCase("79228162514264337593543950335d")]
    public void RefusedEffortKeepsItsMessage(string input)
        => Assert.That(() => PlanSheetEditing.Parse(Document, PlanField.Estimate, input, Day),
            Throws.ArgumentException.With.Message.EqualTo("工数は0以上の数値で入力してください。"));
    [Test]
    public void FullWidthPredecessorNumbersResolveWhileLoginsStayAscii()
    {
        Assert.That(PlanSheetEditing.Parse(Document, PlanField.Predecessors, "２，３", Day), Is.EqualTo(new[] { "I2", "I3" }));
        Assert.That(PlanSheetEditing.Parse(Document, PlanField.Predecessors, "１", Day), Is.EqualTo(new[] { "I1" }));
        Assert.That(() => PlanSheetEditing.Parse(Document, PlanField.Predecessors, "４", Day),
            Throws.ArgumentException.With.Message.EqualTo("先行タスクは計画内のIDで指定してください: 4"));
        Assert.That(() => PlanSheetEditing.Parse(Document, PlanField.Assignees, "ａｌｉｃｅ", Day),
            Throws.ArgumentException.With.Message.EqualTo("担当者を確認してください: ａｌｉｃｅ"));
    }
    private static PlanDocument Linked(params (string Identity, string Caption)[] links)
        => Document with { Sync = Document.Sync with {
            IssueLinks = links.ToImmutableDictionary(l => l.Identity, l => new PlanIssueLink(l.Caption, "https://github.com/" + l.Caption)) } };
    [Test]
    public void IssueReferenceLinksTheRowCarryingThatIssueAndABareNumberStaysAPlanRow()
    {
        var document = Linked(("I1", "acme/repo#18"), ("I2", "acme/repo#19"), ("I3", "acme/repo#20"));
        Assert.That(PlanSheetEditing.Parse(document, PlanField.Predecessors, "#19", Day), Is.EqualTo(new[] { "I2" }));
        Assert.That(PlanSheetEditing.Parse(document, PlanField.Predecessors, "1", Day), Is.EqualTo(new[] { "I1" }));
        Assert.That(PlanSheetEditing.Parse(document, PlanField.Predecessors, "３, #19", Day), Is.EqualTo(new[] { "I3", "I2" }));
        Assert.That(PlanSheetEditing.Parse(document, PlanField.Predecessors, "＃１９", Day), Is.EqualTo(new[] { "I2" }));
    }
    [TestCase("#99", "Issue #99 はこの版にありません。")]
    [TestCase("#abc", "先行タスクは計画内のIDか Issue 番号（#19）で指定してください: #abc")]
    [TestCase("#", "先行タスクは計画内のIDか Issue 番号（#19）で指定してください: #")]
    [TestCase("#0", "先行タスクは計画内のIDか Issue 番号（#19）で指定してください: #0")]
    [TestCase("other/repo#99", "先行タスクは計画内のIDで指定してください: other/repo#99")]
    [TestCase("abc", "先行タスクは計画内のIDで指定してください: abc")]
    [TestCase("9", "先行タスクは計画内のIDで指定してください: 9")]
    public void UnresolvableReferencesAreRefusedWithTheirOwnMessage(string input, string expected)
        => Assert.That(() => PlanSheetEditing.Parse(
                Linked(("I1", "acme/repo#18"), ("I2", "acme/repo#19"), ("I3", "acme/repo#20")),
                PlanField.Predecessors, input, Day),
            Throws.ArgumentException.With.Message.EqualTo(expected));
    [Test]
    public void IssueNumberCarriedByTwoRowsIsRefusedNamingBothCandidates()
        => Assert.That(() => PlanSheetEditing.Parse(Linked(("I1", "acme/repo#19"), ("I2", "other/repo#19")),
                PlanField.Predecessors, "#19", Day),
            Throws.ArgumentException.With.Message.EqualTo("Issue #19 は複数の行にあります: acme/repo#19, other/repo#19"));
    [Test]
    public void IssueReferenceEditLeavesEveryOtherRowsPredecessorsUntouched()
    {
        var rows = ImmutableArray.Create(
            new PlanRow("I1", "first", "acme/repo"),
            new PlanRow("I2", "second", "acme/repo") { Predecessors = ["other/repo#99"] },
            new PlanRow("I3", "third", "acme/repo"));
        var document = PlanCsvImportTests.Empty() with {
            Baseline = new(rows, []), State = new(rows, new()),
            Sync = new() { IssueLinks = ImmutableDictionary<string, PlanIssueLink>.Empty.Add("I3", new("acme/repo#20", "https://github.com/acme/repo/issues/20")) } };
        var resolved = PlanSheetEditing.Parse(document, PlanField.Predecessors, "#20", Day);
        var after = PlanOperations.Apply(document,
            new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Predecessors, resolved)]), Day).State;
        Assert.That(after.Rows.Single(r => r.Identity == "I1").Predecessors, Is.EqualTo(new[] { "I3" }));
        Assert.That(after.Rows.Single(r => r.Identity == "I2").Predecessors, Is.EqualTo(new[] { "other/repo#99" }));
    }
    [Test]
    public void PastedFullWidthEffortAndDateUseTheSameNormalisation()
    {
        var command = PlanSheetEditing.Paste(Document, ["I1"], [PlanField.Remaining, PlanField.Start], new(0, 0), "８ｈ\t１０月１４日", Day);
        Assert.That(command.Cells, Is.EqualTo(new PlanCellChange[] {
            new("I1", PlanField.Remaining, 8m), new("I1", PlanField.Start, new DateOnly(2026, 10, 14)) }));
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
