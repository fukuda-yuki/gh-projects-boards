using GhProjectsBoards.Core.Prototypes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
namespace GhProjectsBoards.App.Prototypes.WinUi;

public sealed class NativePrototype : Grid
{
    internal readonly PrototypePlan Plan = new();
    internal readonly HashSet<PrototypeNativeRow> Visible = [];
    private readonly PrototypeMetrics metrics = new();
    private readonly TextBlock error = new();
    private readonly Dictionary<(int Row, int Column), string> invalidInputs = [];
    private readonly ListView list;
    private int pending;
    public NativePrototype()
    {
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new());

        AutomationProperties.SetAutomationId(error, "PrototypeError");
        Grid.SetRow(error, 1); Children.Add(error);
        list = new ListView { SelectionMode = ListViewSelectionMode.None, Padding = new(0), ItemsSource = Enumerable.Range(0, 1000).ToArray(),
            ItemTemplate = (DataTemplate)Application.Current.Resources["PrototypeNativeRowTemplate"] };
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 32d));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 32d));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        list.ItemContainerStyle = style;
        var header = new Grid { Width = 1868, Height = 32, HorizontalAlignment = HorizontalAlignment.Left };
        var headings = new[] { "ID", "タイトル", "Remaining", "Start date", "Target date" };
        var widths = new[] { 48d, 230, 100, 145, 145, 1200 };
        foreach (var width in widths) header.ColumnDefinitions.Add(new() { Width = new(width) });
        for (var i = 0; i < headings.Length; i++) { var label = new TextBlock { Text = headings[i], Margin = new(4, 6, 0, 0) }; Grid.SetColumn(label, i); header.Children.Add(label); }
        var days = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < 50; i++) days.Children.Add(new TextBlock { Text = new DateOnly(2026, 10, 5).AddDays(i).ToString("dd"), Width = 24, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(days, 5); header.Children.Add(days); list.Header = header;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Enabled);
        Grid.SetRow(list, 2); Children.Add(list);
        Unloaded += (_, _) => { CompositionTarget.Rendered -= Rendered; metrics.End(pending, "unloaded-before-frame"); };
    }
    internal bool Commit(int index, int column, string text)
    {
        pending = metrics.Begin();
        try { Plan.Edit(index, column, text); invalidInputs.Remove((index, column)); UpdateError(); }
        catch (ArgumentException)
        {
            invalidInputs[(index, column)] = text;
            UpdateError(); metrics.End(pending, "invalid"); return false;
        }
        foreach (var row in Visible.ToArray()) row.Refresh();
        CompositionTarget.Rendered -= Rendered; CompositionTarget.Rendered += Rendered;
        return true;
    }
    // Pending invalid text belongs to the cell identity, not a recycled TextBox.
    internal bool HasInvalidInput(int row, int column) => invalidInputs.ContainsKey((row, column));
    internal string EditorText(int row, int column, string committed) =>
        invalidInputs.TryGetValue((row, column), out var text) ? text : committed;
    private void UpdateError() => error.Text = invalidInputs.Count == 0 ? "" : "入力値を確認してください";
    private void Rendered(object? sender, object args)
    {
        CompositionTarget.Rendered -= Rendered;
        metrics.End(pending, "winui-rendered-callback");
    }
    internal void Advance(int index, int column)
    {
        var next = Math.Min(999, index + 1); list.ScrollIntoView(next);
        DispatcherQueue.TryEnqueue(() => Visible.FirstOrDefault(r => r.Index == next)?.FocusCell(column));
    }
}
public sealed class PrototypeNativeRow : Grid
{
    private NativePrototype? owner;
    internal int Index => DataContext is int i ? i : -1;
    private readonly TextBlock id = new(), end = new();
    private readonly PrototypeCell[] cells;
    private readonly Canvas chart = new() { Width = 1200, Height = 32 };
    public PrototypeNativeRow()
    {
        Height = 32; Width = 1868;
        foreach (var width in new[] { 48d, 230, 100, 145, 145, 1200 }) ColumnDefinitions.Add(new() { Width = new(width) });
        cells = [new(this, 0), new(this, 1), new(this, 2)];
        Add(id, 0); for (var c = 0; c < 3; c++) Add(cells[c], c + 1); Add(end, 4); Add(chart, 5);
        Loaded += (_, _) => { for (DependencyObject? p = this; p != null; p = VisualTreeHelper.GetParent(p)) if (p is NativePrototype v) { owner = v; v.Visible.Add(this); break; } Refresh(); };
        Unloaded += (_, _) => { owner?.Visible.Remove(this); owner = null; ClearIds(); };
        DataContextChanged += (_, _) => { ClearIds(); Refresh(); };
    }
    private void ClearIds() { AutomationProperties.SetAutomationId(end, ""); foreach (var c in cells) AutomationProperties.SetAutomationId(c, ""); chart.Children.Clear(); }
    private void Add(FrameworkElement e, int column) { SetColumn(e, column); Children.Add(e); }
    internal void FocusCell(int column) { cells[column].Focus(FocusState.Keyboard); cells[column].SelectAll(); }
    internal bool Commit(int column, string value) => owner?.Commit(Index, column, value) == true;
    internal void Advance(int column) => owner?.Advance(Index, column);
    internal bool HasInvalidInput(int column) => owner?.HasInvalidInput(Index, column) == true;
    internal void Refresh()
    {
        if (owner is null || Index < 0) return;
        var r = owner.Plan.Rows[Index]; id.Text = r.Id.ToString(); end.Text = r.End;
        AutomationProperties.SetAutomationId(end, $"PrototypeEnd{Index}");
        var values = new[] { r.Title, r.Remaining.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Start };
        for (var c = 0; c < 3; c++) { cells[c].Refresh(values[c], owner.EditorText(Index, c, values[c])); AutomationProperties.SetAutomationId(cells[c], $"PrototypeCell{Index}_{c}"); AutomationProperties.SetName(cells[c], $"行 {r.Id} {new[] { "タイトル", "Remaining", "Start date" }[c]}"); }
        chart.Children.Clear();
        double X(string date) => (DateOnly.Parse(date).DayNumber - new DateOnly(2026, 10, 5).DayNumber) * 24;
        var brush = (Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"];
        if (r.Predecessor > 0)
        {
            var x = X(owner.Plan.Rows[Index - 1].End); var to = X(r.Start);
            var arrow = new Polyline { Stroke = brush, StrokeThickness = 1, Points = [new(x, -16), new(x + 5, -16), new(x + 5, 16), new(to, 16), new(to - 4, 12), new(to, 16), new(to - 4, 20)] };
            chart.Children.Add(arrow);
        }
        var bar = new Rectangle { Width = Math.Max(3, X(r.End) - X(r.Start)), Height = 14, Fill = brush };
        Canvas.SetLeft(bar, X(r.Start)); Canvas.SetTop(bar, 9); chart.Children.Add(bar);
        AutomationProperties.SetAutomationId(bar, $"PrototypeBar{Index}");
    }
}
internal sealed class PrototypeCell : TextBox
{
    private bool composing, endedThisTurn, committing;
    private string committed = "";
    private readonly PrototypeNativeRow row;
    private readonly int column;
    public PrototypeCell(PrototypeNativeRow row, int column)
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        this.row = row; this.column = column; MinHeight = 28; Height = 32; Padding = new(4, 2, 4, 2); FontSize = 13;
        LostFocus += (_, _) => { if (IsLoaded && !composing && !committing && (Text != committed || row.HasInvalidInput(column)) && FocusManager.GetFocusedElement(XamlRoot) is not null) CommitText(); };
        TextCompositionStarted += (_, _) => composing = true;
        TextCompositionEnded += (_, _) => { composing = false; endedThisTurn = true; DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => endedThisTurn = false); };
    }
    internal void Refresh(string text, string editorText) { committed = text; if (FocusState == FocusState.Unfocused && !composing) Text = editorText; }
    private bool CommitText()
    {
        committing = true;
        try
        {
            var accepted = row.Commit(column, Text);
            // The typed start can be earlier than its predecessor. Show the computed
            // value before moving focus, without creating a second focus-loss edit.
            if (accepted) Text = committed;
            return accepted;
        }
        finally { committing = false; }
    }
    protected override void OnPreviewKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !composing && !endedThisTurn) { e.Handled = true; if (CommitText()) row.Advance(column); }
        base.OnPreviewKeyDown(e);
    }
}







