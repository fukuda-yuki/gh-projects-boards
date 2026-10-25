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
        foreach (var field in new[] { PlanField.Start, PlanField.End })
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
    private static PlanDocument Linked(params (string Identity, string Caption)[] links)
        => Document with { Sync = Document.Sync with {
            IssueLinks = links.ToImmutableDictionary(l => l.Identity, l => new PlanIssueLink(l.Caption, "https://github.com/" + l.Caption)) } };
    private static readonly PlanDocument Issues = Linked(("I1", "acme/repo#18"), ("I2", "acme/repo#19"), ("I3", "acme/repo#20"));
    // #263: a bare number and #N both name the Issue; neither is ever a row position.
    [TestCase("19"), TestCase("#19"), TestCase("repo#19"), TestCase("REPO#19"), TestCase("acme/repo#19")]
    [TestCase("１９"), TestCase("＃１９"), TestCase("ｒｅｐｏ＃１９")]
    public void PredecessorNamesTheRowCarryingThatIssue(string input)
        => Assert.That(PlanSheetEditing.Parse(Issues, PlanField.Predecessors, input, Day), Is.EqualTo(new[] { "I2" }));
    [Test]
    public void PredecessorListsResolveEachEntryAndLoginsStayAscii()
    {
        Assert.That(PlanSheetEditing.Parse(Issues, PlanField.Predecessors, "２０，＃１８", Day), Is.EqualTo(new[] { "I3", "I1" }));
        Assert.That(PlanSheetEditing.Parse(Issues, PlanField.Predecessors, "#18、 20", Day), Is.EqualTo(new[] { "I1", "I3" }));
        Assert.That(() => PlanSheetEditing.Parse(Document, PlanField.Assignees, "ａｌｉｃｅ", Day),
            Throws.ArgumentException.With.Message.EqualTo("担当者を確認してください: ａｌｉｃｅ"));
    }
    [Test]
    public void RowsWithoutAnIssueAreNamedNewInPlanOrder()
    {
        var rows = ImmutableArray.Create(new PlanRow("local:a", "draft a", "acme/repo"), new PlanRow("I2", "second", "acme/repo"),
            new PlanRow("local:b", "draft b", "acme/repo"));
        var document = Issues with { State = Issues.State with { Rows = rows } };
        var names = PlanTaskNames.Of(document);
        Assert.That(rows.Select(r => names[r.Identity]), Is.EqualTo(new[] { "新1", "#19", "新2" }));
        Assert.That(PlanSheetEditing.Parse(document, PlanField.Predecessors, "新２, 19", Day), Is.EqualTo(new[] { "local:b", "I2" }));
        Assert.That(() => PlanSheetEditing.Parse(document, PlanField.Predecessors, "新3", Day),
            Throws.ArgumentException.With.Message.EqualTo("新3 はこの版にありません。"));
    }
    [Test]
    public void IssuesOfSeveralRepositoriesCarryTheRepositoryNameAndAnAmbiguousNumberIsRefused()
    {
        var document = Linked(("I1", "acme/api#19"), ("I2", "acme/web#19"), ("I3", "acme/web#20"));
        var names = PlanTaskNames.Of(document);
        Assert.That(new[] { "I1", "I2", "I3" }.Select(id => names[id]), Is.EqualTo(new[] { "api#19", "web#19", "web#20" }));
        Assert.That(PlanSheetEditing.Parse(document, PlanField.Predecessors, "web#19, 20", Day), Is.EqualTo(new[] { "I2", "I3" }));
        foreach (var input in new[] { "19", "#19", "＃１９" })
            Assert.That(() => PlanSheetEditing.Parse(document, PlanField.Predecessors, input, Day),
                Throws.ArgumentException.With.Message.EqualTo("#19 は複数のリポジトリにあります: api#19, web#19"), input);
    }
    [TestCase("#99", "#99 はこの版にありません。")]
    [TestCase("99", "#99 はこの版にありません。")]
    [TestCase("other/repo#19", "other/repo#19 はこの版にありません。")]
    [TestCase("web#19", "web#19 はこの版にありません。")]
    [TestCase("新1", "新1 はこの版にありません。")]
    [TestCase("#abc", "先行タスクは #524、repo#524 または 新2 の形で指定してください: #abc")]
    [TestCase("#", "先行タスクは #524、repo#524 または 新2 の形で指定してください: #")]
    [TestCase("#0", "先行タスクは #524、repo#524 または 新2 の形で指定してください: #0")]
    [TestCase("新", "先行タスクは #524、repo#524 または 新2 の形で指定してください: 新")]
    [TestCase("abc", "先行タスクは #524、repo#524 または 新2 の形で指定してください: abc")]
    [TestCase("ＩＤ２", "先行タスクは #524、repo#524 または 新2 の形で指定してください: ID2")]
    public void UnresolvableReferencesAreRefusedWithTheirOwnMessage(string input, string expected)
        => Assert.That(() => PlanSheetEditing.Parse(Issues, PlanField.Predecessors, input, Day),
            Throws.ArgumentException.With.Message.EqualTo(expected));
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
    public void CopiedPredecessorTextKeepsItsMeaningAfterRowsAreInsertedAbove()
    {
        var rows = Issues.State.Rows.Select(r => r.Identity == "I3" ? r with { Predecessors = ["I1", "I2"] } : r).ToImmutableArray();
        var document = Issues with { State = Issues.State with { Rows = rows } };
        var copied = PlanTaskNames.Of(document).List(rows[2].Predecessors);
        Assert.That(copied, Is.EqualTo("#18, #19"));
        var inserted = document with { State = document.State with { Rows = rows.Insert(0, new PlanRow("local:x", "inserted", "acme/repo")) } };
        Assert.That(PlanTaskNames.Of(inserted).List(rows[2].Predecessors), Is.EqualTo(copied), "Inserting a row renames no published task.");
        Assert.That(PlanSheetEditing.Parse(inserted, PlanField.Predecessors, copied, Day), Is.EqualTo(new[] { "I1", "I2" }));
        Assert.That(PlanSheetEditing.Parse(Document, PlanField.Assignees, "alice", Day), Is.EqualTo(new[] { "U1" }));
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
    [Test]
    public void UnresolvedAssigneesKeepsOnlyValuesWithoutAUniqueLocalMatch()
    {
        Assert.That(PlanSheetEditing.UnresolvedAssignees(Document, "alice"), Is.Empty, "A stored person resolves locally.");
        Assert.That(PlanSheetEditing.UnresolvedAssignees(Document, "ALICE"), Is.Empty, "Name matching is case-insensitive.");
        Assert.That(PlanSheetEditing.UnresolvedAssignees(Document, "late-user"), Is.EqualTo(new[] { "late-user" }));
        Assert.That(PlanSheetEditing.UnresolvedAssignees(Document, "alice, late-user"), Is.EqualTo(new[] { "late-user" }));
        var resolved = ImmutableArray.Create(new PlanResource("U101", "late-user", 100, null));
        Assert.That(PlanSheetEditing.UnresolvedAssignees(Document, "late-user", resolved), Is.Empty, "A verified login needs no second lookup.");
    }
    [Test]
    public void ResolvedPeopleParseOnlyFromTheirVerifiedLogin()
    {
        Assert.Throws<ArgumentException>(() => PlanSheetEditing.Parse(Document, PlanField.Assignees, "late-user", Day));
        var resolved = ImmutableArray.Create(new PlanResource("U101", "late-user", 100, null));
        Assert.That(PlanSheetEditing.Parse(Document, PlanField.Assignees, "LATE-USER", Day, "I1", resolved), Is.EqualTo(new[] { "U101" }));
        // A resolved person and a stored name that both match stay ambiguous rather than guessing.
        var colliding = ImmutableArray.Create(new PlanResource("U101", "alice", 100, null));
        Assert.Throws<ArgumentException>(() => PlanSheetEditing.Parse(Document, PlanField.Assignees, "alice", Day, "I1", colliding));
    }
    [Test]
    public void PasteCollectsPendingAssigneesAndCarriesResolvedPeople()
    {
        string[] rows = ["I1", "I2"], columns = [PlanField.Assignees];
        var selection = new CellRange(0, 0, 2, 1);
        var pending = PlanSheetEditing.PendingAssignees(Document, rows, columns, selection, "alice\nlate-user");
        Assert.That(pending, Is.EqualTo(new[] { ("acme/repo", "late-user") }), "Only the login without a local match is verified.");
        Assert.Throws<ArgumentException>(() => PlanSheetEditing.Paste(Document, rows, columns, selection, "alice\nlate-user", Day));
        var resolved = ImmutableArray.Create(new PlanResource("U101", "late-user", 100, null));
        var command = PlanSheetEditing.Paste(Document, rows, columns, selection, "alice\nlate-user", Day, resolved);
        Assert.That(command.Cells.Single(c => c.Identity == "I1").Value, Is.EqualTo(new[] { "U1" }));
        Assert.That(command.Cells.Single(c => c.Identity == "I2").Value, Is.EqualTo(new[] { "U101" }));
    }
    // #275: Ctrl+Enter writes the typed text into every selected cell as one operation.
    [Test]
    public void FillSelectionWritesTheTypedTextIntoEverySelectedCellOfEveryColumn()
    {
        var command = PlanSheetEditing.FillSelection(Document, ["I1", "I2", "I3"], [PlanField.Title, PlanField.Estimate, PlanField.Remaining],
            new(1, 1, 2, 2), "8h", Day);
        Assert.That(command.Kind, Is.EqualTo(PlanOperationKind.Fill));
        Assert.That(command.Cells, Is.EqualTo(new PlanCellChange[] {
            new("I2", PlanField.Estimate, 8m), new("I2", PlanField.Remaining, 8m),
            new("I3", PlanField.Estimate, 8m), new("I3", PlanField.Remaining, 8m) }));
        var published = Document with { Baseline = Document.Baseline with { Rows = Document.State.Rows } };
        var applied = PlanOperations.Apply(published, command, Day).State.Rows;
        Assert.That(applied.Select(r => (r.Identity, r.Estimate, r.Remaining)), Is.EqualTo(new (string, decimal?, decimal?)[] {
            ("I1", null, null), ("I2", 8m, 8m), ("I3", 8m, 8m) }), "Cells outside the selection stay as they were.");
    }
    [Test]
    public void FillSelectionRefusesTheWholeRangeWhenOneCellCannotTakeTheText()
    {
        Assert.That(() => PlanSheetEditing.FillSelection(Document, ["I1", "I2"], [PlanField.Title, PlanField.Estimate],
            new(0, 0, 2, 2), "設計", Day), Throws.ArgumentException.With.Message.EqualTo("工数は0以上の数値で入力してください。"));
        Assert.That(() => PlanSheetEditing.FillSelection(Document, ["I1", ""], [PlanField.Estimate],
            new(0, 0, 2, 1), "8", Day), Throws.ArgumentException.With.Message.EqualTo("空行を除いて範囲を選択してください。"));
    }
    [Test]
    public void FillSelectionVerifiesUnknownLoginsOncePerRepository()
    {
        string[] rows = ["I1", "I2"], columns = [PlanField.Assignees];
        var selection = new CellRange(0, 0, 2, 1);
        Assert.That(PlanSheetEditing.FillSelectionAssignees(Document, rows, columns, selection, "alice"), Is.Empty);
        Assert.That(PlanSheetEditing.FillSelectionAssignees(Document, rows, columns, selection, "alice, late-user"),
            Is.EqualTo(new[] { ("acme/repo", "late-user") }));
        Assert.That(PlanSheetEditing.FillSelectionAssignees(Document, rows, [PlanField.Title], new(0, 0, 2, 1), "late-user"), Is.Empty,
            "Only a selection that reaches 担当者 needs a lookup.");
        var resolved = ImmutableArray.Create(new PlanResource("U101", "late-user", 100, null));
        var command = PlanSheetEditing.FillSelection(Document, rows, columns, selection, "late-user", Day, resolved);
        Assert.That(command.Cells.Select(c => c.Value), Is.All.EqualTo(new[] { "U101" }));
    }
}
