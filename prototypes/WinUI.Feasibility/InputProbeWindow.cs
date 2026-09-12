using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinUI.TableView;

namespace WinUI.Feasibility;

// A few-row public-API experiment. The original candidate window remains the control.
public sealed class InputProbeWindow : Window
{
    private readonly List<object> trace = [];
    private readonly List<EntryProbe> editors = [];
    private readonly TextBlock state = new();
    private int commits;

    public InputProbeWindow(string mode)
    {
        Title = "Issue 24 — " + mode;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 650));
        AutomationProperties.SetAutomationId(state, "ProbeState");
        var panel = new StackPanel { Padding = new Thickness(24), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "入力方式の検証 — 採用未承認" });
        panel.Children.Add(state);
        if (mode == "column")
        {
            var table = new global::WinUI.TableView.TableView { AutoGenerateColumns = false, SelectionUnit = TableViewSelectionUnit.Cell,
                SelectionMode = ListViewSelectionMode.Extended, Height = 350 };
            table.Columns.Add(new EntryColumn(this) { Header = "Title", Width = new GridLength(650),
                Binding = new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath("Value") } });
            table.ItemsSource = new ObservableCollection<ProbeItem>(Enumerable.Range(1, 3).Select(id => new ProbeItem(id)));
            AutomationProperties.SetAutomationId(table, "EntryTable");
            panel.Children.Add(table);
        }
        else
        {
            foreach (var id in Enumerable.Range(1, 3)) panel.Children.Add(CreateEditor(new ProbeItem(id)));
        }
        var reference = new TextBox { Header = "標準 TextBox" };
        AutomationProperties.SetAutomationId(reference, "ProbeReference");
        panel.Children.Add(reference);
        Content = panel;
        Closed += (_, _) =>
        {
            var path = Environment.GetEnvironmentVariable("GHPB_IME_TRACE");
            if (!string.IsNullOrEmpty(path)) System.IO.File.WriteAllText(path,
                JsonSerializer.Serialize(trace, new JsonSerializerOptions { WriteIndented = true }));
        };
    }

    private EntryProbe CreateEditor(ProbeItem item)
    {
        var editor = new EntryProbe(item, Observe, CommitAndMove);
        editors.Add(editor);
        // Element generation runs during layout: do not mutate the status layout from here.
        trace.Add(new { phase = "created", row = item.Id });
        return editor;
    }

    private void Observe(EntryProbe editor, string phase)
    {
        state.Text = $"row={editor.Item.Id};state={(editor.IsReadOnly ? "Selected" : "Editing")};commits={commits};value={editor.Item.Value}";
        trace.Add(new { phase, row = editor.Item.Id, readOnly = editor.IsReadOnly, editor.Text,
            committed = editor.Item.Value, editor.FocusState, commits });
    }

    private void CommitAndMove(EntryProbe editor)
    {
        editor.Item.Value = editor.Text;
        commits++;
        editor.IsReadOnly = true;
        Observe(editor, "commit");
        var next = editors.FirstOrDefault(candidate => candidate.IsLoaded && candidate.Item.Id == editor.Item.Id + 1);
        next?.Focus(FocusState.Keyboard);
    }

    public sealed class EntryColumn(InputProbeWindow owner) : TableViewTextColumn
    {
        public override FrameworkElement GenerateElement(TableViewCell cell, object? dataItem)
        {
            UseSingleElement = true;
            // TableView creates cells before row content is assigned, then calls RefreshElement.
            return dataItem is ProbeItem item ? owner.CreateEditor(item) : new TextBlock();
        }
        public override void RefreshElement(TableViewCell cell, object? dataItem)
        {
            if (dataItem is ProbeItem item && (cell.Content is not EntryProbe editor || editor.Item != item))
                cell.Content = owner.CreateEditor(item);
        }
    }

    public sealed class ProbeItem(int id)
    {
        public int Id { get; } = id;
        public string Value { get; set; } = $"試験データ {id:000}";
    }

    public sealed class EntryProbe : TextBox
    {
        private bool composing;
        private readonly Action<EntryProbe, string> observe;
        private readonly Action<EntryProbe> commit;
        public ProbeItem Item { get; }

        public EntryProbe(ProbeItem item, Action<EntryProbe, string> observe, Action<EntryProbe> commit)
        {
            Item = item;
            this.observe = observe;
            this.commit = commit;
            Text = item.Value;
            IsReadOnly = true;
            AutomationProperties.SetAutomationId(this, $"Entry{item.Id}");
            GotFocus += (_, _) => observe(this, "focus");
            LostFocus += (_, _) => observe(this, "blur");
            TextCompositionStarted += (_, _) => { composing = true; observe(this, "composition-start"); };
            TextCompositionEnded += (_, _) => { composing = false; observe(this, "composition-end"); };
            TextChanged += (_, _) => observe(this, "text-changed");
        }

        protected override void OnPreviewKeyDown(KeyRoutedEventArgs e)
        {
            observe(this, "preview-" + e.Key);
            if (IsReadOnly && (e.Key == VirtualKey.F2 || e.Key >= VirtualKey.A && e.Key <= VirtualKey.Z || (int)e.Key == 229))
            {
                // Toggle the already focused public editor; never capture or replay the key/text.
                IsReadOnly = false;
                SelectAll();
                observe(this, "begin-edit");
                if (e.Key == VirtualKey.F2) e.Handled = true;
            }
            else if (!IsReadOnly && e.Key == VirtualKey.Enter && !composing)
            {
                e.Handled = true;
                commit(this);
            }
            else if (!IsReadOnly && e.Key == VirtualKey.Escape && !composing)
            {
                Text = Item.Value;
                IsReadOnly = true;
                e.Handled = true;
                observe(this, "cancel-edit");
            }
            base.OnPreviewKeyDown(e);
        }
    }
}
