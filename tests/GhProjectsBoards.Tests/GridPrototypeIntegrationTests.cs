using GhProjectsBoards.App.GridPrototype;
using NUnit.Framework;
using System.Threading;

namespace GhProjectsBoards.Tests;

[TestFixture]
[Category("Integration")]
internal sealed class GridPrototypeIntegrationTests
{
    [Test]
    [Apartment(ApartmentState.STA)]
    public void PrototypeWindowCanLoadItsXamlAndSyntheticBindings()
    {
        var window = new GridPrototypeWindow();
        Assert.That(window.DataContext, Is.TypeOf<GridPrototypeViewModel>());
        var grid = (System.Windows.Controls.DataGrid)window.FindName("EditorGrid");
        Assert.That(grid.Columns.Select(column => column.IsReadOnly), Is.All.False,
            "Separating the committed values from editors must not make the grid read-only.");
        window.Close();
    }

    [Test]
    public void RejectedPasteCanBeCorrectedAndUndoneWithoutLosingEarlierEdits()
    {
        var model = new GridPrototypeViewModel();
        model.Edit(new(1, GridField.Title), "先の編集");
        var committed = model.Rows.Select(row => row.Values).ToArray();
        var notifications = new List<string?>();
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        Assert.That(model.Paste(new(1, GridField.Number), "12\t2026-02-30\tLow\n21\t2026-02-02\tHigh").Succeeded, Is.False);
        Assert.That(model.StatusText, Does.Contain("適用していません").And.Contain("日付"));
        Assert.That(model.Rows[0].DateError, Is.Not.Empty);
        Assert.That(model.UndoCount, Is.EqualTo(1));
        Assert.That(model.Rows.Select(row => row.Values), Is.EqualTo(committed));

        Assert.That(model.Paste(new(1, GridField.Number), "12\t2026-02-28\tLow\n21\t2026-02-02\tHigh").Succeeded, Is.True);
        Assert.That(model.Rows[0].DateError, Is.Empty);
        Assert.That(model.UndoText, Does.Contain("範囲貼り付け"));
        model.Undo();
        Assert.That(model.Rows.Select(row => row.Values), Is.EqualTo(committed));
        model.Edit(new(1, GridField.Title), "取り消し後の再編集");
        model.Undo();
        Assert.That(model.Rows[0].Title, Is.EqualTo("先の編集"));
        Assert.That(notifications, Is.Not.Empty);
    }

    [Test]
    public void NewRowReportsRequiredInputAndRetainsStableIdentityThroughCorrectionAndUndo()
    {
        var model = new GridPrototypeViewModel();
        var row = model.AddRow();
        Assert.That(model.RowCountText, Is.EqualTo("101 行"));
        Assert.That(row.TitleError, Does.Contain("必須"));
        Assert.That(model.Edit(new(row.Id, GridField.Date), "bad").Succeeded, Is.False);
        Assert.That(row.DateError, Is.Not.Empty);
        Assert.That(model.Edit(new(row.Id, GridField.Title), "新規課題").Succeeded, Is.True);
        Assert.That(row.TitleError, Is.Empty);
        Assert.That(model.Edit(new(row.Id, GridField.Date), "2026-12-31").Succeeded, Is.True);
        Assert.That(row.DateError, Is.Empty);
        model.Undo();
        Assert.That(row.Id, Is.EqualTo(101));
        Assert.That(row.Values, Is.EqualTo(new GridValues("新規課題", "Open", null, null, null)));
        model.Undo();
        Assert.That(row.TitleError, Is.Not.Empty);
        model.Undo();
        Assert.That(model.Rows, Has.Count.EqualTo(100));
        Assert.That(model.CanUndo, Is.False);
    }
}
