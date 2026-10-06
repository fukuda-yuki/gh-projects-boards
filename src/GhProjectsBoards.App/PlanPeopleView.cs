using System.Collections.Immutable;
using System.Globalization;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace GhProjectsBoards.App;

internal sealed class PlanPeopleView : UserControl
{
    internal PlanSession Session { get; }
    internal event Action? Changed;
    private readonly Grid root = new() { RowSpacing = 4 };
    private readonly Grid table = new();
    private readonly Grid header = new();
    private readonly ListView rows = Id(new ListView { SelectionMode = ListViewSelectionMode.None, Padding = new(0) }, "PeopleRows");
    private readonly TextBlock error = Id(new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }, "PeopleError");
    private readonly TextBlock range = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly HashSet<string> expanded = [];
    // Edits outlive the controls rebuilt after an asynchronous save.
    private sealed class InputState(string text)
    {
        internal string Accepted = text;
        internal bool Composing;
        internal bool Dirty => Box is not null && Box.Text != Accepted;
        internal TextBox Box = null!;
        internal Func<Task> Commit = null!;
    }
    private readonly Dictionary<string, InputState> inputs = [];
    private DateOnly anchor;
    private PlanPeriodScale scale;
    private int periodIndex, generation;
    private Task operation = Task.CompletedTask;
    private bool rendering;
    private PeoplePlan report = null!;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    private static readonly double[] Widths = [144, 48, 64, 64, 64, 64, 64, 64];
    internal PlanPeopleView(PlanSession session)
    {
        Session = session; anchor = session.Document.State.Settings.StatusDate ?? Today;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var zoom = Id(new ComboBox { ItemsSource = new[] { "日", "週", "月" }, SelectedIndex = 0, Width = 70 }, "PeopleScale");
        AutomationProperties.SetName(zoom, "負荷の表示期間");
        zoom.SelectionChanged += async (_, _) => { if (!rendering && zoom.SelectedIndex >= 0) {
            var selected = zoom.SelectedIndex;
            await Run(() => { scale = (PlanPeriodScale)selected; periodIndex = 0; Refresh(); return Task.CompletedTask; });
            rendering = true; zoom.SelectedIndex = (int)scale; rendering = false;
        } };
        actions.Children.Add(zoom);
        actions.Children.Add(Button("前へ", "PeoplePrevious", () => { Shift(-1); return Task.CompletedTask; }));
        actions.Children.Add(Button("次へ", "PeopleNext", () => { Shift(1); return Task.CompletedTask; }));
        actions.Children.Add(range);
        root.Children.Add(actions); root.Children.Add(error); Grid.SetRow(error, 1);
        table.RowDefinitions.Add(new() { Height = new(28) });
        table.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        table.Children.Add(header); table.Children.Add(rows); Grid.SetRow(rows, 1);
        rows.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = {
            new Setter(Control.PaddingProperty, new Thickness(0)), new Setter(FrameworkElement.MinHeightProperty, 24d),
            new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } };
        ScrollViewer.SetHorizontalScrollMode(rows, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(rows, ScrollBarVisibility.Disabled);
        var scroll = new ScrollViewer { Content = table, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(scroll); Grid.SetRow(scroll, 2);
        Content = root; Refresh();
    }
    private static T Id<T>(T item, string id) where T : DependencyObject { AutomationProperties.SetAutomationId(item, id); return item; }
    private static string Number(decimal? value) => value?.ToString("0.##", CultureInfo.CurrentCulture) ?? "未入力";
    private static TextBlock Text(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private Button Button(string text, string id, Func<Task> action)
    {
        var button = Id(new Button { Content = text, Padding = new(4, 0, 4, 0), MinHeight = 0, Height = 24 }, id);
        button.Click += async (_, _) => await Run(action); return button;
    }
    private Task Run(Func<Task> action, bool commitPending = true)
    {
        var previous = operation;
        return operation = Execute();
        async Task Execute() {
            await Task.Yield(); await previous;
            try { if (commitPending) await CommitInput(); await action();
                if (!inputs.Values.Any(i => i.Dirty)) { error.Text = ""; error.Visibility = Visibility.Collapsed; } }
            catch (Exception ex) when (ex is not OutOfMemoryException) { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
        }
    }
    private async Task CommitInput() { foreach (var input in inputs.Values.ToArray()) await input.Commit(); }
    internal async Task FlushInput() { await operation; await CommitInput(); }
    private async Task Apply(PlanCommand command)
    {
        var result = await Session.Execute(command, Today);
        Changed?.Invoke();
        if (!result.Succeeded) throw new IOException(result.Error);
    }
    private TextBox Input(string text, string id, string name, Func<string, PlanCommand> command)
    {
        if (!inputs.TryGetValue(id, out var input)) inputs[id] = input = new(text);
        if (input.Dirty) text = input.Box.Text;
        else input.Accepted = text;
        var box = Id(new TextBox { Text = text, MinHeight = 0, Height = 24, Padding = new(3, 0, 3, 0), VerticalContentAlignment = VerticalAlignment.Center }, id);
        AutomationProperties.SetName(box, name);
        input.Box = box; var current = generation;
        var composing = false; var justComposed = false;
        box.TextCompositionStarted += (_, _) => input.Composing = composing = true;
        box.TextCompositionEnded += (_, _) => { input.Composing = composing = false; justComposed = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => justComposed = false); };
        async Task Commit() {
            if (input.Composing) throw new InvalidOperationException("文字の変換を確定してください。");
            if (!input.Dirty) return;
            var changed = input.Box.Text; await Apply(command(changed)); input.Accepted = changed;
        }
        input.Commit = Commit;
        box.LostFocus += async (_, _) => {
            if (!composing && current == generation && input.Dirty)
                await Run(async () => { await Commit(); Refresh(); }, commitPending: false);
        };
        box.KeyDown += async (_, e) => {
            if (composing || justComposed) return;
            if (e.Key == VirtualKey.Enter) { e.Handled = true; await Run(() => { Refresh(); return Task.CompletedTask; }); }
            else if (e.Key == VirtualKey.Escape) { box.Text = input.Accepted; e.Handled = true; }
        };
        return box;
    }
    private void Shift(int direction) {
        anchor = scale switch { PlanPeriodScale.Month => anchor.AddMonths(direction * 6), PlanPeriodScale.Week => anchor.AddDays(direction * 42), _ => anchor.AddDays(direction * 7) };
        periodIndex = 0; Refresh();
    }
    private void Columns(Grid grid) {
        grid.ColumnDefinitions.Clear();
        foreach (var width in Widths) grid.ColumnDefinitions.Add(new() { Width = new(width) });
        foreach (var _ in report.Periods) grid.ColumnDefinitions.Add(new() { Width = new(104) });
    }
    private static void Add(Grid grid, FrameworkElement control, int column) { grid.Children.Add(control); Grid.SetColumn(control, column); }
    internal void Refresh()
    {
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as TextBox;
        var focusedInput = inputs.FirstOrDefault(p => ReferenceEquals(p.Value.Box, focused));
        var focusId = focusedInput.Key;
        var selection = focused?.SelectionStart ?? 0; var selectionLength = focused?.SelectionLength ?? 0;
        var offset = Descendants(rows).OfType<ScrollViewer>().FirstOrDefault()?.VerticalOffset ?? 0;
        generation++; rows.Items.Clear(); header.Children.Clear();
        report = PlanPeople.Calculate(Session.Document, Today, anchor, scale, scale == PlanPeriodScale.Day ? 7 : 6);
        table.Width = Widths.Sum() + 104 * report.Periods.Count;
        range.Text = $"{report.Periods[0].Start:yyyy/M/d} – {report.Periods[^1].End:M/d}  人時";
        Columns(header);
        var names = new[] { "担当者", "稼働%", "許容量", "見積", "実績", "残", "予測", "差分" };
        for (var i = 0; i < names.Length; i++) Add(header, Text(names[i]), i);
        for (var i = 0; i < report.Periods.Count; i++) {
            var index = i; var period = report.Periods[i];
            var caption = scale switch { PlanPeriodScale.Week => $"{period.Start:M/d} 週", PlanPeriodScale.Month => $"{period.Start:yyyy/M}", _ => $"{period.Start:M/d (ddd)}" };
            Add(header, Button(caption, "PeoplePeriod_" + i, () => { periodIndex = index; Refresh(); return Task.CompletedTask; }), 8 + i);
        }
        foreach (var person in report.People) {
            var group = new StackPanel();
            var line = Id(new Grid { Height = 24 }, "PeopleRow_" + person.Identity); Columns(line);
            var expand = Button((expanded.Contains(person.Identity) ? "▾ " : "▸ ") + person.Name, "PeopleExpand_" + person.Identity,
                () => { if (!expanded.Remove(person.Identity)) expanded.Add(person.Identity); Refresh(); return Task.CompletedTask; });
            expand.HorizontalAlignment = HorizontalAlignment.Stretch; expand.HorizontalContentAlignment = HorizontalAlignment.Left;
            ToolTipService.SetToolTip(expand, person.Name + (person.Missing.Count > 0 ? " — " + string.Join("、", person.Missing) : ""));
            AutomationProperties.SetName(expand, person.Name + "のタスクを展開 " + string.Join("、", person.Missing));
            Add(line, expand, 0);
            if (person.Rate is not null) {
                Add(line, Input(Number(person.Rate), "PeopleRate_" + person.Identity, person.Name + " 稼働率", text => ResourceCommand(person, text, true)), 1);
                Add(line, Input(person.Allowance?.ToString(CultureInfo.CurrentCulture) ?? "", "PeopleAllowance_" + person.Identity, person.Name + " 許容量 人時", text => ResourceCommand(person, text, false)), 2);
            } else { Add(line, Text("—"), 1); Add(line, Text("—"), 2); }
            var totals = new[] { person.Estimate, person.Actual, person.Remaining, person.Forecast, person.Difference };
            for (var i = 0; i < totals.Length; i++) {
                var label = Id(Text(Number(totals[i])), $"PeopleTotal_{person.Identity}_{i}");
                AutomationProperties.SetName(label, person.Name + " " + names[i + 3] + " " + label.Text);
                ToolTipService.SetToolTip(label, label.Text + (person.Missing.Count > 0 ? " — " + string.Join("、", person.Missing) : ""));
                if (i == 4 && person.Difference < 0) { label.Text += " 超過"; label.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]; }
                Add(line, label, i + 3);
            }
            for (var i = 0; i < report.Periods.Count; i++) {
                var index = i; var load = person.Periods[i];
                var label = Id(Text(load.Planned is null ? "未算定" : load.Capacity is null ? Number(load.Planned) + "h" : load.Capacity == 0 ? "稼働日なし" : Number(load.Percent) + "%"), $"PeopleLoad_{person.Identity}_{i}");
                if (load.Overloaded) { label.Text += " 超過"; label.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]; }
                var button = Button("", $"PeopleLoadOpen_{person.Identity}_{i}", () => { periodIndex = index; expanded.Add(person.Identity); Refresh(); return Task.CompletedTask; });
                button.Content = label; button.HorizontalAlignment = HorizontalAlignment.Stretch;
                var description = $"{person.Name} {report.Periods[i].Start:M/d}–{report.Periods[i].End:M/d} 計画 {Number(load.Planned)}h / 稼働可能 {Number(load.Capacity)}h {label.Text}";
                AutomationProperties.SetName(button, description); ToolTipService.SetToolTip(button, description); Add(line, button, i + 8);
            }
            group.Children.Add(line);
            if (expanded.Contains(person.Identity)) group.Children.Add(Details(person));
            rows.Items.Add(group);
        }
        var refreshed = generation;
        if (focusId is not null && inputs.TryGetValue(focusId, out var next)) {
            next.Box.Loaded += RestoreFocus;
            void RestoreFocus(object sender, RoutedEventArgs args) {
                next.Box.Loaded -= RestoreFocus;
                if (generation != refreshed) return;
                next.Box.Focus(FocusState.Programmatic);
                var at = Math.Min(selection, next.Box.Text.Length);
                next.Box.Select(at, Math.Min(selectionLength, next.Box.Text.Length - at));
            }
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
            if (generation != refreshed || !IsLoaded) return;
            Descendants(rows).OfType<ScrollViewer>().FirstOrDefault()?.ChangeView(null, offset, null, true);
        });
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private PlanCommand ResourceCommand(PersonLoad person, string text, bool rate)
    {
        decimal? value = text.Length == 0 ? null : decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var number) ? number : throw new ArgumentException("数値を入力してください。");
        if (rate && (value is null || value <= 0 || value > 100) || !rate && value < 0) throw new ArgumentException(rate ? "稼働率は0より大きく100以下にしてください。" : "許容量は0以上の人時で入力してください。");
        var settings = Session.Document.State.Settings;
        var resource = settings.People.FirstOrDefault(p => p.Identity == person.Identity) ?? new(person.Identity, person.Name, 100, null, []);
        resource = rate ? resource with { Rate = value!.Value } : resource with { Allowance = value };
        var people = settings.People.Any(p => p.Identity == person.Identity) ? settings.People.Select(p => p.Identity == person.Identity ? resource : p).ToImmutableArray() : settings.People.Add(resource);
        return new ReplacePlanSettings(settings with { People = people });
    }
    private FrameworkElement Details(PersonLoad person)
    {
        var panel = new StackPanel { Margin = new(24, 4, 0, 8), Spacing = 4 };
        var period = report.Periods[periodIndex];
        panel.Children.Add(Text($"{person.Name}  {period.Start:M/d}–{period.End:M/d}" + (person.Missing.Count > 0 ? " — " + string.Join("、", person.Missing) : "")));
        var headings = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (name, width) in new[] { ("タスク", 250d), ("担当者", 160d), ("残", 90d), ("実績", 90d), ("日程固定", 90d) }) {
            var text = Text(name); text.Width = width - 8; headings.Children.Add(text);
        }
        panel.Children.Add(headings);
        var ids = person.Periods[periodIndex].Tasks.Concat(person.Unallocated).Distinct().ToHashSet();
        foreach (var row in Session.Document.State.Rows.Where(r => ids.Contains(r.Identity))) {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var title = Text($"{Session.Document.State.Rows.IndexOf(row) + 1} {row.Title}" + (person.Unallocated.Contains(row.Identity) ? "（未配分）" : ""));
            title.Width = 242; ToolTipService.SetToolTip(title, title.Text); line.Children.Add(title);
            line.Children.Add(AssigneeChoice(row));
            foreach (var field in new[] { PlanField.Remaining, PlanField.Actual }) {
                var text = (field == PlanField.Remaining ? row.Remaining : row.Actual)?.ToString(CultureInfo.CurrentCulture) ?? "";
                var input = Input(text, $"PeopleTask_{row.Identity}_{field}", row.Title + " " + (field == PlanField.Remaining ? "残" : "実績"),
                    value => new EditPlanCells(PlanOperationKind.Cell, [new(row.Identity, field, PlanSheetEditing.Parse(Session.Document, field, value))]));
                input.Width = 90; line.Children.Add(input);
            }
            var fixedBox = Id(new CheckBox { Content = "固定", IsChecked = row.Fixed, MinHeight = 24 }, "PeopleTask_" + row.Identity + "_Fixed");
            AutomationProperties.SetName(fixedBox, row.Title + " 日程固定");
            fixedBox.Click += async (_, _) => { var value = fixedBox.IsChecked == true; await Run(async () => {
                await Apply(new EditPlanCells(PlanOperationKind.Cell, [new(row.Identity, PlanField.Fixed, value)])); Refresh();
            }); };
            line.Children.Add(fixedBox); panel.Children.Add(line);
        }
        if (ids.Count == 0) panel.Children.Add(Text("この期間のタスクはありません"));
        return panel;
    }
    private ComboBox AssigneeChoice(PlanRow row)
    {
        var choice = Id(new ComboBox { Width = 160, MinHeight = 24, Padding = new(3, 0, 3, 0) }, $"PeopleTask_{row.Identity}_Assignees");
        AutomationProperties.SetName(choice, row.Title + " 担当者");
        var none = new ComboBoxItem { Content = "担当者なし", Tag = "" };
        choice.Items.Add(none);
        var document = Session.Document;
        var ids = document.State.Settings.People.Select(p => p.Identity).Concat(document.Sync.PeopleNames.Keys)
            .Concat(document.State.Rows.SelectMany(r => r.Assignees)).Distinct();
        foreach (var id in ids) choice.Items.Add(new ComboBoxItem { Tag = id,
            Content = document.Sync.PeopleNames.GetValueOrDefault(id, document.State.Settings.People.FirstOrDefault(p => p.Identity == id)?.Name ?? "担当者（未確認）") });
        if (row.Assignees.Length > 1) {
            var multiple = new ComboBoxItem { Content = "担当者が複数", IsEnabled = false };
            choice.Items.Add(multiple); choice.SelectedItem = multiple;
        } else choice.SelectedItem = row.Assignees.Length == 0 ? none : choice.Items.OfType<ComboBoxItem>().Single(i => (string?)i.Tag == row.Assignees[0]);
        var original = choice.SelectedItem;
        choice.SelectionChanged += async (_, _) => {
            if (ReferenceEquals(choice.SelectedItem, original) || choice.SelectedItem is not ComboBoxItem { Tag: string identity }) return;
            await Run(async () => {
                ImmutableArray<string> assignees = identity.Length == 0 ? [] : [identity];
                await Apply(new EditPlanCells(PlanOperationKind.Cell, [new(row.Identity, PlanField.Assignees, assignees)])); Refresh();
            });
            if (choice.IsLoaded) choice.SelectedItem = original;
        };
        return choice;
    }
}
