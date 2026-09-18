using System.Collections.ObjectModel;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace GhProjectsBoards.App;

public sealed class ApplyConfirmationGroup(string name) : ObservableCollection<ApplyConfirmationRow>
{
    public string Name { get; } = name;
}

public sealed class ApplyConfirmationRow
{
    private ApplyConfirmationRowContent? content;
    internal ConfirmationIssue Data { get; private set; }
    internal bool Narrow { get; private set; }
    private readonly Action<ConfirmationCell, bool> resolve;
    internal ApplyConfirmationRow(ConfirmationIssue data, bool narrow, Action<ConfirmationCell, bool> resolve)
        => (Data, Narrow, this.resolve) = (data, narrow, resolve);
    public FrameworkElement Content => content ??= new(Data, Narrow, resolve);
    internal void Update(ConfirmationIssue data, bool narrow)
    {
        Data = data; Narrow = narrow; content?.Update(data, narrow);
    }
    public override string ToString() => Data.Identity;
}

public sealed partial class ApplyConfirmationTable : UserControl
{
    private readonly ObservableCollection<ApplyConfirmationGroup> groups = [];
    private readonly Dictionary<string, ApplyConfirmationRow> rows = [];
    private readonly UISettings settings = new();
    private bool populating, narrow;
    private ConfirmationColumn[] columns = [];
    private IReadOnlySet<string> selection = new HashSet<string>();
    internal Action? SelectionUpdated { get; set; }
    internal Action<ConfirmationCell, bool>? Resolve { get; set; }
    internal HashSet<string> SelectedIds => Rows.SelectedItems.Cast<ApplyConfirmationRow>().Select(r => r.Data.Id).ToHashSet();
    internal ListView List => Rows;

    public ApplyConfirmationTable()
    {
        InitializeComponent(); GroupedRows.Source = groups;
        Rows.ContainerContentChanging += (_, args) =>
        {
            if (!args.InRecycleQueue && args.Item is ApplyConfirmationRow row)
            {
                AutomationProperties.SetName(args.ItemContainer, row.Data.Identity);
                AutomationProperties.SetAutomationId(args.ItemContainer, "ApplyTarget-" + row.Data.Id);
            }
        };
        SizeChanged += (_, _) => LayoutColumns();
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => LayoutColumns());
        Loaded += (_, _) => { settings.TextScaleFactorChanged += TextScaleChanged; LayoutColumns(); };
        Unloaded += (_, _) => settings.TextScaleFactorChanged -= TextScaleChanged;
    }
    private void TextScaleChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(LayoutColumns);

    internal void Update(ApplyConfirmationPresentation presentation, IReadOnlySet<string> selected)
    {
        var anchor = CaptureAnchor();
        populating = true;
        try
        {
            selection = selected.ToHashSet();
            columns = presentation.Columns;
            var wanted = presentation.Rows.Select(r => r.Id).ToHashSet();
            foreach (var key in rows.Keys.Where(id => !wanted.Contains(id)).ToArray())
            {
                foreach (var group in groups) group.Remove(rows[key]);
                rows.Remove(key);
            }
            foreach (var repository in presentation.Rows.GroupBy(r => r.Repository))
            {
                var group = groups.SingleOrDefault(g => g.Name == repository.Key);
                if (group is null) { group = new(repository.Key); groups.Add(group); }
                var index = 0;
                foreach (var data in repository)
                {
                    if (!rows.TryGetValue(data.Id, out var row)) rows[data.Id] = row = new(data, narrow, (cell, remote) => Resolve?.Invoke(cell, remote));
                    row.Update(data, narrow);
                    if (Rows.ContainerFromItem(row) is ListViewItem container) AutomationProperties.SetName(container, data.Identity);
                    if (!group.Contains(row))
                    {
                        foreach (var other in groups.Where(g => g != group)) other.Remove(row);
                        group.Insert(index, row);
                    }
                    else if (group.IndexOf(row) != index) group.Move(group.IndexOf(row), index);
                    index++;
                }
            }
            foreach (var empty in groups.Where(g => g.Count == 0).ToArray()) groups.Remove(empty);
            foreach (var row in rows.Values)
            {
                if (selected.Contains(row.Data.Id) && !Rows.SelectedItems.Contains(row)) Rows.SelectedItems.Add(row);
                if (!selected.Contains(row.Data.Id) && Rows.SelectedItems.Contains(row)) Rows.SelectedItems.Remove(row);
            }
            SelectAll.IsChecked = selected.Count == 0 ? false : selected.Count == rows.Count ? true : null;
            SelectAll.IsEnabled = rows.Count > 0;
            LayoutColumns();
        }
        finally { populating = false; }
        RestoreAnchor(anchor);
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!populating) SelectionUpdated?.Invoke();
    }
    private void SelectAllClicked(object sender, RoutedEventArgs e)
    {
        populating = true;
        try { if (selection.Count == rows.Count) Rows.SelectedItems.Clear(); else Rows.SelectAll(); }
        finally { populating = false; }
        SelectionUpdated?.Invoke();
    }
    private void LayoutColumns()
    {
        var next = ActualWidth < (400 + columns.Length * 160) * Math.Max(1, FontSize / 14 * settings.TextScaleFactor);
        var signature = string.Join("|", columns.Select(c => c.Name + c.Hidden)) + next;
        if (ColumnHeader.Tag as string != signature)
        {
            narrow = next; ColumnHeader.Tag = signature;
            ColumnHeader.Children.Clear(); ColumnHeader.ColumnDefinitions.Clear();
            AddHeader("Issue", new(1, GridUnitType.Star));
            if (!narrow) foreach (var column in columns) AddHeader(column.Name + (column.Hidden ? "\n表では非表示" : ""), new(160));
            AddHeader(narrow ? "変更前 → 変更後" : "", new(100));
            foreach (var row in rows.Values) row.Update(row.Data, narrow);
        }
        void AddHeader(string text, GridLength width)
        {
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            Grid.SetColumn(label, ColumnHeader.ColumnDefinitions.Count); ColumnHeader.ColumnDefinitions.Add(new() { Width = width }); ColumnHeader.Children.Add(label);
        }
    }
    internal void GoToProblem(string id)
    {
        if (!rows.TryGetValue(id, out var row)) return;
        Rows.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
        Rows.UpdateLayout();
        if (Rows.ContainerFromItem(row) is Control control) control.Focus(FocusState.Keyboard);
    }
    private (ApplyConfirmationRow? Row, double Y) CaptureAnchor()
    {
        foreach (var row in groups.SelectMany(g => g))
            if (Rows.ContainerFromItem(row) is FrameworkElement element)
            {
                var y = element.TransformToVisual(Rows).TransformPoint(new Point()).Y;
                if (y + element.ActualHeight > 32 && y < Rows.ActualHeight) return (row, y);
            }
        return (null, 0);
    }
    private void RestoreAnchor((ApplyConfirmationRow? Row, double Y) anchor)
    {
        if (anchor.Row is null || !rows.ContainsKey(anchor.Row.Data.Id)) return;
        Rows.UpdateLayout();
        if (Rows.ContainerFromItem(anchor.Row) is not FrameworkElement element) return;
        var scroll = Descendants(Rows).OfType<ScrollViewer>().FirstOrDefault();
        var y = element.TransformToVisual(Rows).TransformPoint(new Point()).Y;
        if (scroll is not null && Math.Abs(y - anchor.Y) > 1) scroll.ChangeView(null, scroll.VerticalOffset + y - anchor.Y, null, true);
    }
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Descendants(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
}

internal sealed class ApplyConfirmationRowContent : Grid
{
    private ConfirmationIssue data;
    private readonly Action<ConfirmationCell, bool> resolve;
    private readonly TextBlock identity = Text("");
    private readonly TextBlock note = Text("");
    private readonly Button details = new() { Content = new TextBlock { Text = "全文・詳細", TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel detailContent = new() { Spacing = 8 };
    private readonly StackPanel warnings = new() { Spacing = 4 };
    private sealed class CellView
    {
        public StackPanel Panel { get; } = new() { Spacing = 4 };
        public TextBlock Name { get; } = Text("");
        public TextBlock Value { get; } = Text("");
        public TextBlock Pending { get; } = Text("");
        public TextBlock Problem { get; } = Text("");
        public StackPanel Actions { get; } = new() { Spacing = 4 };
    }
    private readonly Dictionary<ColumnIdentity, CellView> cells = [];
    private string layout = "", problemSignature = "";
    internal ApplyConfirmationRowContent(ConfirmationIssue value, bool narrow, Action<ConfirmationCell, bool> resolve)
    {
        data = value; this.resolve = resolve; ColumnSpacing = 12; RowSpacing = 4; MinHeight = 60; Padding = new(0, 6, 0, 6);
        identity.MaxLines = 2; identity.TextTrimming = TextTrimming.CharacterEllipsis;
        var issue = new StackPanel { Spacing = 2 }; issue.Children.Add(identity); issue.Children.Add(note);
        Children.Add(issue); Children.Add(details); Children.Add(warnings);
        var full = new ScrollViewer { Content = detailContent, Padding = new(8), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsTabStop = true };
        AutomationProperties.SetAutomationId(full, "ApplyDetailScroll-" + value.Id);
        AutomationProperties.SetName(full, "全文と完全な差分");
        details.Flyout = new Flyout { Content = full };
        details.Flyout.Opening += (_, _) => RefreshDetails();
        details.Flyout.Closed += (_, _) => { if (details.IsLoaded && details.IsEnabled) details.Focus(FocusState.Keyboard); };
        AutomationProperties.SetAutomationId(details, "ApplyDetails-" + value.Id);
        AutomationProperties.SetAutomationId(this, "ApplyRow-" + value.Id);
        Update(value, narrow);
    }
    internal void Update(ConfirmationIssue value, bool narrow)
    {
        data = value;
        identity.Text = data.Number + "  " + data.Title;
        AutomationProperties.SetName(this, data.Identity);
        var notices = new List<string>();
        if (!data.IsCreation && data.Cells.Any(c => c.Column.Id == ColumnIdentity.Title && c.After.Kind != ConfirmationValueKind.Unchanged)) notices.Add("タイトル変更");
        if (data.Hidden) notices.Add("表では非表示");
        if (data.Cells.Any(c => c.Pending is not null) || data.RepositoryBuffer is not null) notices.Add("未確定入力は送信対象外");
        note.Text = string.Join(" · ", notices); note.Visibility = notices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var cell in data.Cells)
        {
            if (!cells.TryGetValue(cell.Column.Id, out var view))
            {
                view = new();
                view.Value.MaxLines = 2; view.Value.TextTrimming = TextTrimming.CharacterEllipsis;
                view.Panel.Children.Add(view.Name); view.Panel.Children.Add(view.Value); view.Panel.Children.Add(view.Pending);
                view.Panel.Children.Add(view.Problem); view.Panel.Children.Add(view.Actions);
                foreach (var remote in new[] { true, false })
                {
                    var button = new Button { Content = new TextBlock { Text = remote ? "GitHubの値を使う" : "自分の変更を使う", TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch };
                    var field = cell.Column.Id == ColumnIdentity.Title ? "Title" : "Select-" + cell.Column.Id.FieldId;
                    AutomationProperties.SetAutomationId(button, $"ApplyResolve-{data.Id}-{field}-{(remote ? "Remote" : "Local")}");
                    button.Click += (_, _) => { var current = data.Cells.Single(c => c.Column.Id == cell.Column.Id); if (current.CanResolve) resolve(current, remote); };
                    view.Actions.Children.Add(button);
                }
                cells[cell.Column.Id] = view; Children.Add(view.Panel);
                AutomationProperties.SetAutomationId(view.Value, "ApplyValue-" + data.Id + "-" + cell.Column.Id.Role + "-" + cell.Column.Id.FieldId);
            }
            view.Name.Text = cell.Column.Name + (cell.Column.Hidden ? "（表では非表示）" : "");
            view.Name.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
            view.Value.Text = cell.Difference;
            view.Pending.Text = cell.Pending is null ? "" : "送らない未確定入力: " + Short(cell.Pending);
            view.Pending.Visibility = cell.Pending is null ? Visibility.Collapsed : Visibility.Visible;
            view.Problem.Text = cell.Problem ?? ""; view.Problem.Visibility = cell.Problem is null ? Visibility.Collapsed : Visibility.Visible;
            // Keep the controls stable during a check so a focused resolution action is not recreated.
            view.Actions.Visibility = cell.Draft?.Conflict == true ? Visibility.Visible : Visibility.Collapsed;
            foreach (var button in view.Actions.Children.OfType<Button>()) button.IsEnabled = cell.CanResolve;
        }
        var signature = string.Join("|", data.Cells.Select(c => c.Column.Id)) + narrow;
        if (layout != signature)
        {
            layout = signature; ColumnDefinitions.Clear(); RowDefinitions.Clear();
            ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            if (!narrow) foreach (var _ in data.Cells) ColumnDefinitions.Add(new() { Width = new(160) });
            ColumnDefinitions.Add(new() { Width = new(100) });
            RowDefinitions.Add(new() { Height = GridLength.Auto });
            Grid.SetColumn(details, ColumnDefinitions.Count - 1);
            var i = 0;
            foreach (var cell in data.Cells)
            {
                var panel = cells[cell.Column.Id].Panel;
                Grid.SetColumn(panel, narrow ? 0 : i + 1); Grid.SetColumnSpan(panel, narrow ? 2 : 1);
                Grid.SetRow(panel, narrow ? i + 1 : 0);
                if (narrow) RowDefinitions.Add(new() { Height = GridLength.Auto });
                i++;
            }
            RowDefinitions.Add(new() { Height = GridLength.Auto }); Grid.SetRow(warnings, RowDefinitions.Count - 1); Grid.SetColumnSpan(warnings, ColumnDefinitions.Count);
        }
        var problemKey = string.Join("|", data.RowProblems) + data.RepositoryBuffer;
        if (problemSignature != problemKey)
        {
            problemSignature = problemKey; warnings.Children.Clear();
            foreach (var problem in data.RowProblems) warnings.Children.Add(Text(problem));
            if (data.RepositoryBuffer is not null) warnings.Children.Add(Text("送らない未確定入力（Repository）: " + Short(data.RepositoryBuffer)));
        }
        warnings.Visibility = warnings.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (details.Flyout.IsOpen) RefreshDetails();
    }
    private void RefreshDetails()
    {
        var scroll = (ScrollViewer)((Flyout)details.Flyout).Content;
        scroll.Width = Math.Max(240, Math.Min(600, XamlRoot.Size.Width - 120)); scroll.MaxHeight = Math.Max(160, XamlRoot.Size.Height - 180);
        var text = new List<string> { data.Identity, data.IdentityDetails };
        foreach (var cell in data.Cells)
        {
            text.Add(cell.Column.Name + (cell.Column.Hidden ? "（表では非表示）" : ""));
            text.Add("GitHubの値: " + cell.Before.Text + "\n反映する値: " + cell.After.Text);
            text.Add($"{(cell.Column.Id == ColumnIdentity.Title ? "Issue共通" : "Projectフィールド " + cell.Column.Id.FieldId)} / {(cell.Latest ? "確認済み" : "保存済み・最新未確認")} {cell.RetrievedAt.LocalDateTime:g}");
            if (cell.Column.Id.Role == "Field") text.Add("値のID: " + (cell.Before.Id ?? cell.Before.Text) + " → " + (cell.After.Id ?? cell.After.Text));
            if (cell.Pending is not null) text.Add("送らない未確定入力: " + cell.Pending);
        }
        if (data.RepositoryBuffer is not null) text.Add("送らない未確定入力（Repository）: " + data.RepositoryBuffer);
        while (detailContent.Children.Count < text.Count) detailContent.Children.Add(Text(""));
        while (detailContent.Children.Count > text.Count) detailContent.Children.RemoveAt(detailContent.Children.Count - 1);
        for (var i = 0; i < text.Count; i++) ((TextBlock)detailContent.Children[i]).Text = text[i];
        AutomationProperties.SetAutomationId(detailContent.Children[1], "ApplyFullDetails-" + data.Id);
    }
    private static string Short(string value) => value.Length > 100 ? value[..100] + "…（全文・詳細）" : value;
    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
}
