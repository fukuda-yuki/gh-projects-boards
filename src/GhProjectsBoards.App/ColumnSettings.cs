using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private async Task ConfigureColumnsAsync(Button launcher)
    {
        if (!CanRefresh) { columnNotice.Text = "IME変換中のため列設定を保留しています。自然な操作で確定・取消してから列の設定を開いてください。"; columnNotice.Visibility = Visibility.Visible; return; }
        columnNotice.Visibility = Visibility.Collapsed;
        var request = generation;
        var returnGeneration = request;
        if (!await prepareLocalRows() || request != generation || !IsLoaded) return;
        var candidate = session.Workspace.PrepareColumns(registration);
        var values = candidate.Columns.ToList();
        var content = new StackPanel { Spacing = 12 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(error, "ColumnSettingsStatus");
        var entries = new StackPanel { Spacing = 4 };
        var inputs = new ContentControl { Content = entries, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(preview, "ColumnLayoutPreview");
        var reset = new Button { Content = "既定値に戻す" }; AutomationProperties.SetAutomationId(reset, "ColumnsReset");
        content.Children.Add(new TextBlock { Text = registration.Snapshot.Title + " の列", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "表の左からの順序と幅を調整します。タイトルと参照・宛先は常に表示します。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new ScrollViewer { MaxHeight = 52, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = preview });
        Grid SettingsRow()
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new(48) });
            row.ColumnDefinitions.Add(new() { Width = new(112) });
            row.ColumnDefinitions.Add(new() { Width = new(80) });
            return row;
        }
        var headings = SettingsRow();
        foreach (var (text, index) in new[] { ("列", 0), ("表示", 1), ("幅", 2), ("順序", 3) })
        {
            var label = new TextBlock { Text = text }; SetColumn(label, index); headings.Children.Add(label);
        }
        content.Children.Add(headings);
        content.Children.Add(new ScrollViewer { MaxHeight = 320, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = inputs });
        content.Children.Add(new TextBlock { Text = "幅は 80～1200。変更はローカル保存後に反映されます。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(reset); content.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "列の設定", Content = content,
            PrimaryButtonText = "ローカル保存", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
        dialog.Resources["ContentDialogMaxWidth"] = 720d;
        AutomationProperties.SetAutomationId(dialog, "ColumnSettingsDialog");
        var cancelled = false;
        dialog.CloseButtonClick += (_, _) => cancelled = true;
        dialog.Closed += (_, _) => cancelled = true;
        string ColumnName(PresentationColumn column)
        {
            var repeated = session.Workspace.Columns(registration, values.ToArray()).Columns.Count(c => c.Name == column.Name) > 1;
            return column.Name + (repeated || !column.Available ? $" [{column.Id.FieldId}]" : "");
        }
        void Preview()
        {
            var visible = session.Workspace.Columns(registration, values.ToArray()).Visible;
            preview.Text = $"表示 {visible.Length} 列: " + string.Join(" → ", visible.Select(ColumnName));
        }
        void Render()
        {
            entries.Children.Clear();
            var reconciled = session.Workspace.Columns(registration, values.ToArray());
            foreach (var column in reconciled.Columns)
            {
                var id = column.Id; var index = values.FindIndex(v => v.Id == id);
                var row = SettingsRow();
                var name = new TextBlock { Text = ColumnName(column) + (column.Available ? "" : "（未確認・設定を保持）"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(name, $"Project: {projectId}\n列: {id.FieldId ?? id.Role}");
                AutomationProperties.SetHelpText(name, $"列の識別子: {id.FieldId ?? id.Role}");
                row.Children.Add(name);
                var visible = new CheckBox { IsChecked = values[index].Visible, IsEnabled = id.Role == "Field" && column.Available,
                    MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetAutomationId(visible, "ColumnVisible-" + (id.FieldId ?? id.Role));
                AutomationProperties.SetName(visible, ColumnName(column) + " を表示");
                visible.Checked += (_, _) => { values[index] = values[index] with { Visible = true }; Preview(); };
                visible.Unchecked += (_, _) => { values[index] = values[index] with { Visible = false }; Preview(); };
                var width = new NumberBox { Value = values[index].Width, Width = 112, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                    ValidationMode = NumberBoxValidationMode.Disabled };
                AutomationProperties.SetAutomationId(width, "ColumnWidth-" + (id.FieldId ?? id.Role)); AutomationProperties.SetName(width, column.Name + " の幅（80～1200）");
                width.ValueChanged += (_, _) => values[index] = values[index] with { Width = width.Value };
                SetColumn(visible, 1); row.Children.Add(visible); SetColumn(width, 2); row.Children.Add(width);
                var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                foreach (var delta in new[] { -1, 1 })
                {
                    var move = new Button { Content = delta < 0 ? "↑" : "↓", Width = 36, MinWidth = 0, Padding = new(4),
                        IsEnabled = id.Role == "Field" && index + delta > 0 && index + delta < values.Count - 1 };
                    AutomationProperties.SetAutomationId(move, (delta < 0 ? "ColumnUp-" : "ColumnDown-") + (id.FieldId ?? id.Role));
                    var help = ColumnName(column) + (delta < 0 ? " を左へ移動" : " を右へ移動");
                    AutomationProperties.SetName(move, help); ToolTipService.SetToolTip(move, help);
                    move.Click += (_, _) => { (values[index], values[index + delta]) = (values[index + delta], values[index]); Render(); };
                    commands.Children.Add(move);
                }
                SetColumn(commands, 3); row.Children.Add(commands); entries.Children.Add(row);
            }
            Preview();
        }
        reset.Click += (_, _) => { values = session.Workspace.DefaultColumns(registration).ToList(); Render(); };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            e.Cancel = true;
            if (!CanRefresh) { error.Text = "IME変換が終わるまで設定を保存できません。未確定文字は保持します。"; return; }
            var deferral = e.GetDeferral(); dialog.IsPrimaryButtonEnabled = false; inputs.IsEnabled = false; reset.IsEnabled = false;
            try
            {
                var saved = candidate with { Columns = values.ToArray() };
                var ok = await session.CommitAsync(w => { w.SaveColumns(saved); return w; }, () => !cancelled && IsLoaded && generation == request && CanRefresh);
                if (!ok) { error.Text = "保存できません。幅（80～1200）、保存先、列定義の変更を確認してください。候補を保持しています。"; return; }
                ApplyColumnLayout(session.Workspace.Columns(registration), launcher);
                returnGeneration = generation;
                e.Cancel = false;
            }
            finally { dialog.IsPrimaryButtonEnabled = true; inputs.IsEnabled = true; reset.IsEnabled = true; deferral.Complete(); }
        };
        Render(); await dialog.ShowAsync();
        if (IsLoaded && generation == returnGeneration && CanRefresh) RestoreWorkspaceFocus();
    }
    private void ApplyColumnLayout(ColumnLayout next, Button launcher)
    {
        temporaryApplyColumns.Clear();
        var sameOrder = layout.Visible.Select(c => c.Id).SequenceEqual(next.Visible.Select(c => c.Id));
        layout = next; generation++;
        if (sameOrder)
        {
            ResizeSheetColumns();
        }
        else
        {
            RebuildRows();
            if (!active) FocusViewCommand();
        }
        Update();
    }
}
