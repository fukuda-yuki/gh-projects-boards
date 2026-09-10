using GhProjectsBoards.App.GridPrototype;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
[Category("Unit")]
internal sealed class GridPrototypeTests
{
    [Test]
    public void CommittingACellUpdatesOnlyThatCell()
    {
        var model = new GridPrototypeViewModel();
        var before = model.Rows[0].Values;
        var untouched = model.Rows[1].Values;

        var result = model.Edit(new(1, GridField.Title), "日本語の変更");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(model.Rows[0].Values, Is.EqualTo(before with { Title = "日本語の変更" }));
        Assert.That(model.Rows[1].Values, Is.EqualTo(untouched));
    }

    [TestCase(GridField.State, "Closed", "Closed")]
    [TestCase(GridField.Number, "-12.5", "-12.5")]
    [TestCase(GridField.Number, "0", "0")]
    [TestCase(GridField.Number, "", "")]
    [TestCase(GridField.Date, "2028-02-29", "2028-02-29")]
    [TestCase(GridField.Date, "", "")]
    [TestCase(GridField.Choice, "Low", "Low")]
    [TestCase(GridField.Choice, "", "")]
    public void CommittingAFieldUsesItsDeclaredType(GridField field, string input, string expected)
    {
        var model = new GridPrototypeViewModel();
        var title = model.Rows[0].Title;
        Assert.That(model.Edit(new(1, field), input).Succeeded, Is.True);
        Assert.That(model.Rows[0].Text(field), Is.EqualTo(expected));
        Assert.That(model.Rows[0].Title, Is.EqualTo(title));
    }

    [TestCase(GridField.Title, "")]
    [TestCase(GridField.Title, "  ")]
    [TestCase(GridField.Title, "two\nlines")]
    [TestCase(GridField.State, "")]
    [TestCase(GridField.State, "Done")]
    [TestCase(GridField.Number, "NaN")]
    [TestCase(GridField.Number, "1,234")]
    [TestCase(GridField.Number, " ")]
    [TestCase(GridField.Number, "79228162514264337593543950336")]
    [TestCase(GridField.Date, "2026-02-29")]
    [TestCase(GridField.Date, "01/02/2026")]
    [TestCase(GridField.Choice, "Unknown")]
    public void InvalidCellInputRetainsTheCommittedValueAndIdentifiesTheCell(GridField field, string input)
    {
        var model = new GridPrototypeViewModel();
        var before = model.Rows[0].Values;
        var result = model.Edit(new(1, field), input);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Errors.Single().Address, Is.EqualTo(new GridAddress(1, field)));
        Assert.That(result.Errors[0].Message, Is.Not.Empty);
        Assert.That(model.Rows[0].Values, Is.EqualTo(before));
    }

    [Test]
    public void UndoRestoresOneCommittedCellAtATime()
    {
        var model = new GridPrototypeViewModel();
        var before = model.Rows[0].Values;
        model.Edit(new(1, GridField.Title), "first change");
        model.Edit(new(1, GridField.Number), "42");
        Assert.That(model.UndoCount, Is.EqualTo(2));
        Assert.That(model.Undo(), Is.True);
        Assert.That(model.Rows[0].Values, Is.EqualTo(before with { Title = "first change" }));
        Assert.That(model.Undo(), Is.True);
        Assert.That(model.Rows[0].Values, Is.EqualTo(before));
        Assert.That(model.Undo(), Is.False);
    }

    [TestCase("\n")]
    [TestCase("\r\n")]
    public void RectangularPastePreservesEmptyCellsAndIsOneUndo(string newline)
    {
        var model = new GridPrototypeViewModel();
        var before = model.Rows.Select(row => row.Values).ToArray();
        var result = model.Paste(new(1, GridField.Title), $"貼付1\tClosed\t0\t2028-02-29\t{newline}貼付2\t\t-2.5\t\tLow{newline}");
        Assert.That(result.Succeeded, Is.True);
        Assert.That(model.Rows[0].Values, Is.EqualTo(new GridValues("貼付1", "Closed", 0, new DateOnly(2028, 2, 29), "High")));
        Assert.That(model.Rows[1].Values, Is.EqualTo(before[1] with { Title = "貼付2", Number = -2.5m, Choice = "Low" }));
        Assert.That(model.Rows.Skip(2).Select(row => row.Values), Is.EqualTo(before.Skip(2)));
        Assert.That(model.UndoCount, Is.EqualTo(1));
        Assert.That(model.Undo(), Is.True);
        Assert.That(model.Rows.Select(row => row.Values), Is.EqualTo(before));
    }

    [TestCase("valid\tOpen\t1\nother\tClosed\tbad", 2, GridField.Number)]
    [TestCase("valid\tOpen\nshort", 2, GridField.Title)]
    [TestCase("a\tOpen\t1\t2026-01-01\tHigh\textra", 1, GridField.Choice)]
    public void InvalidPasteChangesNothingAndIdentifiesTheFailure(string text, int rowId, GridField field)
    {
        var model = new GridPrototypeViewModel();
        var before = model.Rows.Select(row => row.Values).ToArray();
        var result = model.Paste(new(1, GridField.Title), text);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Errors, Is.Not.Empty);
        Assert.That(result.Errors[0].Address, Is.EqualTo(new GridAddress(rowId, field)));
        Assert.That(model.Rows.Select(row => row.Values), Is.EqualTo(before));
        Assert.That(model.UndoCount, Is.Zero);
    }

    [Test]
    public void PastePastLastRowIsRejectedInsteadOfAppendingOrPartiallyApplying()
    {
        var model = new GridPrototypeViewModel();
        var before = model.Rows[^1].Values;
        var result = model.Paste(new(100, GridField.Title), "last\nextra");
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Errors[0].Message, Does.Contain("範囲"));
        Assert.That(model.Rows, Has.Count.EqualTo(100));
        Assert.That(model.Rows[^1].Values, Is.EqualTo(before));
    }

    [Test]
    public void ExplicitClearIsAtomicAndUndoable()
    {
        var model = new GridPrototypeViewModel();
        var before = model.Rows[0].Values;
        Assert.That(model.Clear([new(1, GridField.Number), new(1, GridField.Date), new(1, GridField.Choice)]).Succeeded, Is.True);
        Assert.That(model.Rows[0].Values, Is.EqualTo(before with { Number = null, Date = null, Choice = null }));
        Assert.That(model.UndoCount, Is.EqualTo(1));
        model.Undo();
        Assert.That(model.Rows[0].Values, Is.EqualTo(before));
        Assert.That(model.Clear([new(1, GridField.Number), new(1, GridField.Title)]).Succeeded, Is.False);
        Assert.That(model.Rows[0].Values, Is.EqualTo(before));
        Assert.That(model.UndoCount, Is.Zero);
    }

    [Test]
    public void FailedAndNoOpCommandsDoNotConsumeUndo()
    {
        var model = new GridPrototypeViewModel();
        var before = model.Rows[0].Title;
        model.Edit(new(1, GridField.Title), "changed");
        model.Edit(new(1, GridField.Title), "changed");
        model.Edit(new(1, GridField.State), "invalid");
        Assert.That(model.Paste(new(1, GridField.Title), "\t\t\t\t").Succeeded, Is.True);
        Assert.That(model.UndoCount, Is.EqualTo(1));
        model.Undo();
        Assert.That(model.Rows[0].Title, Is.EqualTo(before));
    }

    [Test]
    public void NewRowAndSubsequentEditsUndoSeparatelyWithoutReusingIdentity()
    {
        var model = new GridPrototypeViewModel();
        var added = model.AddRow();
        Assert.That(model.Rows, Has.Count.EqualTo(101));
        Assert.That(added.IsNew, Is.True);
        Assert.That(added.Values, Is.EqualTo(new GridValues("", "Open", null, null, null)));
        model.Edit(new(added.Id, GridField.Title), "新規");
        model.Undo();
        Assert.That(model.Rows[^1].Title, Is.Empty);
        Assert.That(model.Rows, Has.Count.EqualTo(101));
        model.Undo();
        Assert.That(model.Rows, Has.Count.EqualTo(100));
        Assert.That(model.AddRow().Id, Is.GreaterThan(added.Id));
    }
}
