using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace GhProjectsBoards.App;

// Keep the accepted input lifecycle executable independently of unfinished table features.
public sealed class InputCheckWindow : Window
{
    private readonly List<ReadyCell> cells = [];
    private readonly List<TextBlock> membership = [];
    private readonly List<object> trace = [];
    private readonly string? tracePath = Environment.GetEnvironmentVariable("GHPB_IME_TRACE");
    private readonly TextBlock selection = new();
    private int anchor;
    private int current;

    public InputCheckWindow()
    {
        Title = "GH Projects Boards — 日本語入力の確認";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 800));
        var panel = new StackPanel { Padding = new Thickness(24), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "日本語入力の確認（テストデータ・終了時に破棄）" });
        AutomationProperties.SetAutomationId(selection, "ReadySelection");
        panel.Children.Add(selection);
        for (var row = 0; row < 3; row++)
        {
            var line = new Grid { ColumnSpacing = 12 };
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition());
            for (var column = 0; column < 2; column++)
            {
                var index = row * 2 + column;
                var model = new TextBlock();
                AutomationProperties.SetAutomationId(model, $"Committed{index}");
                var cell = new ReadyCell(this, index, model);
                cells.Add(cell);
                var container = new StackPanel { Spacing = 4 };
                container.Children.Add(new TextBlock { Text = $"行 {row + 1} / 列 {column + 1}" });
                var selected = new TextBlock { Text = "未選択" };
                AutomationProperties.SetAutomationId(selected, $"Member{index}");
                membership.Add(selected);
                container.Children.Add(selected);
                container.Children.Add(cell);
                container.Children.Add(model);
                Grid.SetColumn(container, column);
                line.Children.Add(container);
            }
            panel.Children.Add(line);
        }
        var reference = new TextBox { Header = "標準 TextBox" };
        AutomationProperties.SetAutomationId(reference, "ReadyReference");
        panel.Children.Add(reference);
        var close = new Button { Content = "閉じる" };
        AutomationProperties.SetAutomationId(close, "ReadyClose");
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        // WinUI issue #11653 describes this pinned SDK crashing while tearing down a focused
        // TextBox. Release text focus on the ordinary close path, never during input startup.
        AppWindow.Closing += (_, _) => close.Focus(FocusState.Programmatic);
        Content = new ScrollViewer { Content = panel };
        Closed += (_, _) =>
        {
            if (!string.IsNullOrEmpty(tracePath)) System.IO.File.WriteAllText(tracePath,
                JsonSerializer.Serialize(trace, new JsonSerializerOptions { WriteIndented = true }));
        };
    }

    private void Select(int index, bool extend)
    {
        if (cells[current].Editing && current != index) cells[current].Cancel();
        current = index;
        if (!extend) anchor = index;
        for (var candidate = 0; candidate < cells.Count; candidate++)
        {
            var inRange = candidate / 2 >= Math.Min(anchor / 2, current / 2)
                && candidate / 2 <= Math.Max(anchor / 2, current / 2)
                && candidate % 2 >= Math.Min(anchor % 2, current % 2)
                && candidate % 2 <= Math.Max(anchor % 2, current % 2);
            membership[candidate].Text = inRange ? "選択" : "未選択";
        }
        cells[index].Focus(FocusState.Keyboard);
        cells[index].SelectAll();
        Observe(cells[index], "select");
    }

    private void Observe(ReadyCell cell, string phase)
    {
        selection.Text = $"anchor={anchor};current={current};state={(cell.Editing ? "Editing" : "Selected")}";
        if (!string.IsNullOrEmpty(tracePath))
            trace.Add(new { phase, cell.Index, anchor, current, cell.Text, cell.Committed,
                cell.Editing, cell.Composing, cell.FocusState, cell.SelectionStart, cell.SelectionLength });
    }

    private sealed class ReadyCell : TextBox
    {
        private readonly InputCheckWindow owner;
        private readonly TextBlock model;
        private bool restoring;
        public int Index { get; }
        public string Committed { get; private set; }
        public bool Editing { get; private set; }
        public bool Composing { get; private set; }

        public ReadyCell(InputCheckWindow owner, int index, TextBlock model)
        {
            this.owner = owner;
            this.model = model;
            Index = index;
            Committed = $"既存値 {index}";
            Text = Committed;
            model.Text = Committed;
            AutomationProperties.SetAutomationId(this, $"ReadyCell{index}");
            AutomationProperties.SetName(this, $"行 {index / 2 + 1} 列 {index % 2 + 1}");
            TextCompositionStarted += (_, _) => { Composing = true; Begin(); owner.Observe(this, "composition-start"); };
            TextCompositionEnded += (_, _) => { Composing = false; owner.Observe(this, "composition-end"); };
            // Observe native changes instead of guessing characters from virtual keys or modifiers.
            TextChanging += (_, _) => { if (!restoring && Text != Committed) Begin(); };
            TextChanged += (_, _) => owner.Observe(this, "text-changed");
        }

        protected override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            if (!Editing)
            {
                // Focus and prepare the selection before any typing; no first-key focus/read-only transition.
                owner.Select(Index, (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0);
                e.Handled = true;
                return;
            }
            base.OnPointerPressed(e);
        }

        private void Begin()
        {
            if (Editing) return;
            Editing = true;
            owner.Observe(this, "begin");
        }

        public void Cancel()
        {
            restoring = true;
            Text = Committed;
            restoring = false;
            Editing = false;
            SelectAll();
            owner.Observe(this, "cancel");
        }

        protected override void OnPreviewKeyDown(KeyRoutedEventArgs e)
        {
            owner.Observe(this, "preview-" + e.Key);
            if (!Editing && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
            {
                var row = Index / 2;
                var column = Index % 2;
                if (e.Key == VirtualKey.Left) column = Math.Max(0, column - 1);
                if (e.Key == VirtualKey.Right) column = Math.Min(1, column + 1);
                if (e.Key == VirtualKey.Up) row = Math.Max(0, row - 1);
                if (e.Key == VirtualKey.Down) row = Math.Min(2, row + 1);
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                owner.Select(row * 2 + column, shift);
                e.Handled = true;
            }
            else if (!Editing && e.Key == VirtualKey.F2)
            {
                Begin();
                e.Handled = true;
            }
            else if (Editing && !Composing && e.Key == VirtualKey.Escape)
            {
                Cancel();
                e.Handled = true;
            }
            else if (Editing && !Composing && e.Key == VirtualKey.Enter)
            {
                Committed = Text;
                model.Text = Committed;
                Editing = false;
                owner.Observe(this, "commit");
                owner.Select(Math.Min(Index + 2, 5), false);
                e.Handled = true;
            }
            else if (!Editing && e.Key is VirtualKey.Back or VirtualKey.Delete) e.Handled = true;
            base.OnPreviewKeyDown(e);
        }
    }
}
