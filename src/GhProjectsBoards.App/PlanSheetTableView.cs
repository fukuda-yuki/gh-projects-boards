using System.Collections.ObjectModel;
using System.ComponentModel;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Tabular;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace GhProjectsBoards.App;

// Issue #202 Gate 1 only. The native control owns its editing presentation;
// the existing sheet owns pending text and PlanSession owns every accepted operation.
internal sealed partial class PlanSheetView
{
    private TableView? tableView;
    private readonly ObservableCollection<PlanTableViewItem> tableRows = [];
    internal PlanTableViewEditor? TableEditor;
    private bool finishingTableEdit;

    private void InitializeTableView(Grid controls)
    {
        tableView = Id(new TableView {
            ItemsSource = tableRows, IsReadOnly = false, CanUserSortColumns = false,
            CanUserResizeColumns = true, SelectionMode = TableViewSelectionMode.Single,
            Density = TableViewDensity.Compact
        }, "PlanTableView");
        AutomationProperties.SetName(tableView, "計画");
        TableViewTemplateColumn Column(string header, double width, string display, string? editor) => new() {
            Header = header, Width = new GridLength(width), CanSort = false, IsReadOnly = editor is null,
            CellTemplate = (DataTemplate)Application.Current.Resources[display],
            CellEditingTemplate = editor is null ? null : (DataTemplate)Application.Current.Resources[editor]
        };
        tableView.Columns.Add(Column("タスク名", 360, "PlanTableTitle", "PlanTableTitleEditor"));
        tableView.Columns.Add(Column("残 h", 100, "PlanTableRemaining", "PlanTableRemainingEditor"));
        tableView.Columns.Add(Column("終了日", 160, "PlanTableEnd", null));
        tableView.BeginningEdit += (_, args) => {
            var field = TableField(args.Column);
            if (args.Item is not PlanTableViewItem item || item.Identity.Length == 0
                || field is null || ReadOnly(item.Identity, field) || !tail.IsCompleted) {
                args.Cancel = true; return;
            }
            if (!Pending.ContainsKey((item.Identity, field))) {
                var original = PlanSheetEditing.EditBaseline(Rows.GetValueOrDefault(item.Identity), field, EditForm(item.Identity, field));
                SetInput(item.Identity, field, EditForm(item.Identity, field), original);
            }
        };
        tableView.CellEditEnding += TableCellEditEnding;
        tableView.SelectionChanged += (_, _) => {
            if (tableView.SelectedItem is PlanTableViewItem item) Select(item.Identity, PlanField.Title, false);
        };
        // The legacy ListView contains both sheet and chart in each row. Do not display
        // a stationary chart beside a new independently scrolling Gate 1 table.
        foreach (var element in new UIElement[] { List, headers, scrollbars, divider, chartBackground, chartStatus, controls })
            element.Visibility = Visibility.Collapsed;
        Children.Add(tableView); SetRow(tableView, 4);
    }

    private string? TableField(TableViewColumn column) => tableView is null ? null
        : column == tableView.Columns[0] ? PlanField.Title : column == tableView.Columns[1] ? PlanField.Remaining : null;

    private void TableCellEditEnding(TableView sender, TableViewCellEditEndingEventArgs args)
    {
        if (finishingTableEdit) return;
        if (args.Item is not PlanTableViewItem item || TableField(args.Column) is not { } field) { args.Cancel = true; return; }
        // Native events have no async deferral. Never let a focus change or an IME
        // confirmation outrun the pending-input/commit boundary.
        if (TableEditor is not { } editor || editor.Item != item || editor.Field != field || editor.BlocksCommit) {
            args.Cancel = true; return;
        }
        if (args.EditAction == TableViewEditAction.Cancel) {
            CancelEdit(item.Identity, field); return;
        }
        if (remoteBusy || !tail.IsCompleted) { args.Cancel = true; return; }
        if (!Pending.TryGetValue((item.Identity, field), out var input)) return;
        // Commit accepts/rejects the immutable operation before its first await,
        // exactly as in the current renderer. The same queue still owns persistence.
        var save = Commit(item.Identity, field, input.Text, input.Generation, input.OriginalText);
        args.Cancel = Pending.ContainsKey((item.Identity, field));
        _ = Run(() => save, "TableView cell commit");
        UpdateInputProblem();
    }

    private void RefreshTableViewRows()
    {
        if (tableView is null) return;
        var wanted = RowIds.ToHashSet();
        for (var i = tableRows.Count - 1; i >= 0; i--)
            if (!wanted.Contains(tableRows[i].Identity)) tableRows.RemoveAt(i);
        for (var i = 0; i < RowIds.Count; i++) {
            if (i < tableRows.Count && tableRows[i].Identity == RowIds[i]) continue;
            var existing = tableRows.FirstOrDefault(row => row.Identity == RowIds[i]);
            if (existing is null) tableRows.Insert(i, new(this, RowIds[i]));
            else tableRows.Move(tableRows.IndexOf(existing), i);
        }
        foreach (var row in tableRows) row.Refresh();
    }

    private void RequireTableViewCompositionResolved()
    {
        if (TableEditor is not { BlocksCommit: true, Item: { } item } editor) return;
        var message = "IME変換を確定または取消してください。";
        Problems[(item.Identity, editor.Field)] = message;
        editor.Focus(FocusState.Programmatic); UpdateInputProblem();
        throw new InputRejectedException(message);
    }

    private void FinishTableViewEdit()
    {
        if (tableView is not { IsEditing: true }) return;
        finishingTableEdit = true;
        try { tableView.CommitEdit(); }
        finally { finishingTableEdit = false; }
    }

    internal void CancelTableViewEdit() => tableView?.CancelEdit();

    private void FocusTableViewProblem(string identity, string field)
    {
        if (TableEditor is { Item: { } item } editor && item.Identity == identity && editor.Field == field)
            editor.Focus(FocusState.Programmatic);
        UpdateInputProblem();
    }

    private void UpdateTableViewProblem()
    {
        if (TableEditor is not { IsLoaded: true, Item: { } item } editor
            || !Problems.TryGetValue((item.Identity, editor.Field), out var problem)) { inputProblem.Close(); return; }
        var bounds = editor.TransformToVisual(tableView).TransformBounds(new(0, 0, editor.ActualWidth, editor.ActualHeight));
        if (bounds.Bottom <= 0 || bounds.Top >= tableView!.ActualHeight || bounds.Right <= 0 || bounds.Left >= tableView.ActualWidth) {
            inputProblem.Close(); return;
        }
        inputProblem.Show(editor, problem);
    }
}

// Bindable identity projection only: no writable values, document or history copy.
[Microsoft.UI.Xaml.Data.Bindable]
public sealed class PlanTableViewItem : INotifyPropertyChanged
{
    internal PlanSheetView Owner { get; }
    internal string Identity { get; }
    internal PlanTableViewItem(PlanSheetView owner, string identity) { Owner = owner; Identity = identity; }
    public string Title => Owner.Display(Identity, PlanField.Title);
    public string Remaining => Owner.Display(Identity, PlanField.Remaining);
    public string End => Owner.Display(Identity, PlanField.End);
    public string TitleDisplayId => $"PlanTableDisplay{Owner.PlanIds.GetValueOrDefault(Identity)}_Title";
    public string RemainingDisplayId => $"PlanTableDisplay{Owner.PlanIds.GetValueOrDefault(Identity)}_Remaining";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

public sealed class PlanTableViewEditor : TextBox
{
    private string editorField = PlanField.Title, boundField = "";
    public string Field { get => editorField; set { editorField = value; BindItem(); } }
    internal PlanTableViewItem? Item { get; private set; }
    private bool composing, compositionEnded, refreshing;
    private string shownText = "";
    internal bool BlocksCommit => composing || compositionEnded;

    public PlanTableViewEditor()
    {
        MinWidth = 0;
        // Gate 1 run 1 showed native F2 realizes this template but leaves keyboard focus on the row,
        // so physical input never reached the editor. Focus it through the public API on realization.
        Loaded += (_, _) => { BindItem(); Focus(FocusState.Keyboard); };
        DataContextChanged += (_, _) => BindItem();
        Unloaded += (_, _) => {
            if (Item?.Owner.TableEditor == this) { Item.Owner.TableEditor = null; Item.Owner.UpdateInputProblem(); }
            Item = null;
            AutomationProperties.SetAutomationId(this, "");
        };
        GotFocus += (_, _) => {
            if (FocusState != FocusState.Unfocused && Item is { } item) item.Owner.Select(item.Identity, Field, false);
        };
        TextChanging += (_, _) => {
            if (refreshing || Text == shownText || Item is not { } item) return;
            var original = item.Owner.Pending.GetValueOrDefault((item.Identity, Field))?.OriginalText
                ?? PlanSheetEditing.EditBaseline(item.Owner.Rows.GetValueOrDefault(item.Identity), Field, item.Owner.EditForm(item.Identity, Field));
            item.Owner.SetInput(item.Identity, Field, Text, original);
            shownText = Text;
        };
        TextCompositionStarted += (_, _) => composing = true;
        TextCompositionEnded += (_, _) => {
            composing = false; compositionEnded = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => compositionEnded = false);
        };
    }

    private void BindItem()
    {
        var item = DataContext as PlanTableViewItem;
        if (ReferenceEquals(Item, item) && boundField == Field) return;
        if (Item?.Owner.TableEditor == this) Item.Owner.TableEditor = null;
        Item = item; boundField = Field;
        composing = compositionEnded = false;
        AutomationProperties.SetAutomationId(this, "");
        if (item is null) return;
        item.Owner.TableEditor = this;
        AutomationProperties.SetAutomationId(this, $"PlanCell{item.Owner.PlanIds.GetValueOrDefault(item.Identity)}_{Field}");
        refreshing = true;
        try {
            shownText = item.Owner.Pending.GetValueOrDefault((item.Identity, Field))?.Text ?? item.Owner.EditForm(item.Identity, Field);
            Text = shownText;
        }
        finally { refreshing = false; }
        SelectAll();
    }

    protected override void OnPreviewKeyDown(KeyRoutedEventArgs args)
    {
        if (!BlocksCommit && args.Key == VirtualKey.Escape && Item is { } item) {
            args.Handled = true;
            item.Owner.CancelTableViewEdit();
        }
        // Enter stays native: Gate 1 must expose whether native confirmation and
        // next-row navigation work. Do not emulate keys or traverse internal parts.
        base.OnPreviewKeyDown(args);
    }
}
