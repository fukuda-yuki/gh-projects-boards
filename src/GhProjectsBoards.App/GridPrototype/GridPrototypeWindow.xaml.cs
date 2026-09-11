using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace GhProjectsBoards.App.GridPrototype;

internal partial class GridPrototypeWindow : Window
{
    private readonly GridPrototypeViewModel model = new();
    private FrameworkElement? editor;
    public GridPrototypeWindow()
    {
        InitializeComponent();
        DataContext = model;
        GridInputTrace.Attach(this, EditorGrid, "prototype", () => model.UndoCount);
    }
    private void OpenStandardGrid_Click(object sender, RoutedEventArgs e)
        => new StandardGridWindow { Owner = this }.ShowDialog();
    private void Grid_Loaded(object sender, RoutedEventArgs e)
    {
        FocusCell(model.Rows[0], GridField.Title);
    }

    private void PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
    {
        editor = e.EditingElement;
        editor.ApplyTemplate();
        editor.UpdateLayout();
        // Text columns prepare the caret and IME themselves, including type-to-edit.
        Descendants<ComboBox>(editor).FirstOrDefault()?.Focus();
    }

    private void CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Cancel)
        {
            editor = null;
            model.ClearFeedback();
            model.ShowInteractionError("入力を取り消しました。確定済みの変更は保持しています。");
            return;
        }
        // Native IME reconversion can update the text document without refreshing Text.
        // Read the editor's document so committing cannot silently restore the old value.
        var text = Descendants<TextBox>(e.EditingElement).FirstOrDefault() is { } textBox
            ? string.Concat(Enumerable.Range(0, textBox.LineCount).Select(textBox.GetLineText))
            : Descendants<ComboBox>(e.EditingElement).FirstOrDefault()?.SelectedItem as string ?? "";
        var row = (GridPrototypeRow)e.Row.Item;
        var result = model.Edit(new(row.Id, (GridField)e.Column.DisplayIndex), text);
        e.Cancel = !result.Succeeded;
        if (!e.Cancel) editor = null;
    }

    private bool FinishEditing() => EditorGrid.CommitEdit(DataGridEditingUnit.Cell, true)
        && EditorGrid.CommitEdit(DataGridEditingUnit.Row, true);

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        if (!FinishEditing()) return;
        FocusCell(model.AddRow(), GridField.Title);
    }

    private void Paste_Click(object sender, RoutedEventArgs e) => Paste();
    private void Paste(string? clipboardText = null)
    {
        if (!FinishEditing()) return;
        var selection = SelectedAddresses();
        if (selection.Length == 0) { model.ShowInteractionError("貼り付け先のセルを選択してください。"); return; }
        var rowIndexes = selection.Select(address => model.Rows.ToList().FindIndex(row => row.Id == address.RowId)).ToArray();
        var firstRow = rowIndexes.Min();
        var firstColumn = selection.Min(address => (int)address.Field);
        if (selection.Length != (rowIndexes.Max() - firstRow + 1) * (selection.Max(address => (int)address.Field) - firstColumn + 1))
        {
            model.ShowInteractionError("貼り付け先は連続した矩形で選択してください。");
            return;
        }
        try
        {
            if (clipboardText is null && !Clipboard.ContainsText())
            {
                model.ShowInteractionError("クリップボードにテキストがありません。");
                return;
            }
            model.Paste(new(model.Rows[firstRow].Id, (GridField)firstColumn), clipboardText ?? Clipboard.GetText());
            EditorGrid.Focus();
        }
        catch (ExternalException) { model.ShowInteractionError("クリップボードを読み取れませんでした。値は変更していません。"); }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Clear();
    private void Clear()
    {
        if (!FinishEditing()) return;
        model.Clear(SelectedAddresses());
        EditorGrid.Focus();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Undo()
    {
        if (!FinishEditing())
        {
            EditorGrid.CancelEdit(DataGridEditingUnit.Cell);
            return;
        }
        var row = EditorGrid.CurrentCell.Item as GridPrototypeRow;
        var column = EditorGrid.CurrentColumn?.DisplayIndex ?? 0;
        if (!model.Undo()) return;
        FocusCell(row is not null && model.Rows.Contains(row) ? row : model.Rows[^1], (GridField)column);
    }

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Let WPF/IME own composition and confirmation. Do not reinterpret ImeProcessedKey as Enter.
        if (e.Key == Key.ImeProcessed) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            try
            {
                var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                if (editor is null || text?.IndexOfAny(['\t', '\r', '\n']) >= 0)
                {
                    e.Handled = true;
                    Paste(text);
                }
            }
            catch (ExternalException) { e.Handled = true; model.ShowInteractionError("クリップボードを読み取れませんでした。"); }
        }
        else if (editor is null && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            e.Handled = true;
            Undo();
        }
        else if (editor is null && Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Delete)
        {
            e.Handled = true;
            Clear();
        }
    }

    private GridAddress[] SelectedAddresses() => EditorGrid.SelectedCells
        .Where(cell => cell.Item is GridPrototypeRow && cell.Column is not null)
        .Select(cell => new GridAddress(((GridPrototypeRow)cell.Item).Id, (GridField)cell.Column.DisplayIndex)).Distinct().ToArray();

    private void FocusCell(GridPrototypeRow row, GridField field)
    {
        var column = EditorGrid.Columns[(int)field];
        EditorGrid.ScrollIntoView(row, column);
        EditorGrid.UpdateLayout();
        EditorGrid.CurrentCell = new DataGridCellInfo(row, column);
        EditorGrid.SelectedCells.Clear();
        EditorGrid.SelectedCells.Add(EditorGrid.CurrentCell);
        if (EditorGrid.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow container)
            Descendants<DataGridCell>(container).FirstOrDefault(cell => cell.Column == column)?.Focus();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }

}

internal sealed class PrototypeTextColumn : DataGridTextColumn
{
    // The window validates and commits whole operations. The binding only initializes
    // the editor; making it read-only would disable DataGridTextColumn's native IME path.
    protected override bool OnCoerceIsReadOnly(bool baseValue) => baseValue || (DataGridOwner?.IsReadOnly ?? false);
}

internal sealed class NonEmptyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string { Length: > 0 };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
