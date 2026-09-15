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
        if (!await prepareLocalRows() || request != generation || !IsLoaded) return;
        var candidate = session.Workspace.PrepareColumns(registration);
        var values = candidate.Columns.ToList();
        var content = new StackPanel { Spacing = 8 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(error, "ColumnSettingsStatus");
        var entries = new StackPanel { Spacing = 8 };
        var reset = new Button { Content = "既定値に戻す" }; AutomationProperties.SetAutomationId(reset, "ColumnsReset");
        content.Children.Add(new TextBlock { Text = $"{registration.Snapshot.Title} / {projectId}\n幅: 80～1200 論理単位。変更はローカル保存後に反映します。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new ScrollViewer { MaxHeight = 360, Content = entries }); content.Children.Add(reset); content.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Projectの列設定", Content = content,
            PrimaryButtonText = "ローカル保存", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(dialog, "ColumnSettingsDialog");
        var cancelled = false;
        dialog.CloseButtonClick += (_, _) => cancelled = true;
        dialog.Closed += (_, _) => cancelled = true;
        void Render()
        {
            entries.Children.Clear();
            var reconciled = session.Workspace.Columns(registration, values.ToArray());
            foreach (var column in reconciled.Columns)
            {
                var id = column.Id; var index = values.FindIndex(v => v.Id == id);
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(new TextBlock { Text = $"{column.Name} [{id.FieldId ?? id.Role}]" + (column.Available ? "" : "（未確認・設定を保持）"), TextWrapping = TextWrapping.Wrap });
                var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var visible = new CheckBox { Content = "表示", IsChecked = values[index].Visible, IsEnabled = id.Role == "Field" && column.Available };
                AutomationProperties.SetAutomationId(visible, "ColumnVisible-" + (id.FieldId ?? id.Role));
                visible.Checked += (_, _) => values[index] = values[index] with { Visible = true };
                visible.Unchecked += (_, _) => values[index] = values[index] with { Visible = false };
                var width = new NumberBox { Value = values[index].Width, Width = 120, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                    ValidationMode = NumberBoxValidationMode.Disabled };
                AutomationProperties.SetAutomationId(width, "ColumnWidth-" + (id.FieldId ?? id.Role)); AutomationProperties.SetName(width, column.Name + " の幅（80～1200）");
                width.ValueChanged += (_, _) => values[index] = values[index] with { Width = width.Value };
                commands.Children.Add(visible); commands.Children.Add(width);
                foreach (var delta in new[] { -1, 1 })
                {
                    var move = new Button { Content = delta < 0 ? "上へ" : "下へ", IsEnabled = id.Role == "Field" && index + delta > 0 && index + delta < values.Count - 1 };
                    AutomationProperties.SetAutomationId(move, (delta < 0 ? "ColumnUp-" : "ColumnDown-") + (id.FieldId ?? id.Role));
                    move.Click += (_, _) => { (values[index], values[index + delta]) = (values[index + delta], values[index]); Render(); };
                    commands.Children.Add(move);
                }
                row.Children.Add(commands); entries.Children.Add(row);
            }
        }
        reset.Click += (_, _) => { values = session.Workspace.DefaultColumns(registration).ToList(); Render(); };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            e.Cancel = true;
            if (!CanRefresh) { error.Text = "IME変換が終わるまで設定を保存できません。未確定文字は保持します。"; return; }
            var deferral = e.GetDeferral(); dialog.IsPrimaryButtonEnabled = false; entries.IsHitTestVisible = false; reset.IsEnabled = false;
            try
            {
                var saved = candidate with { Columns = values.ToArray() };
                var ok = await session.CommitAsync(w => { w.SaveColumns(saved); return w; }, () => !cancelled && IsLoaded && generation == request && CanRefresh);
                if (!ok) { error.Text = "保存できません。幅（80～1200）、保存先、列定義の変更を確認してください。候補を保持しています。"; return; }
                ApplyColumnLayout(session.Workspace.Columns(registration), launcher);
                e.Cancel = false;
            }
            finally { dialog.IsPrimaryButtonEnabled = true; entries.IsHitTestVisible = true; reset.IsEnabled = true; deferral.Complete(); }
        };
        Render(); await dialog.ShowAsync();
    }
    private void ApplyColumnLayout(ColumnLayout next, Button launcher)
    {
        var sameOrder = layout.Visible.Select(c => c.Id).SequenceEqual(next.Visible.Select(c => c.Id));
        layout = next; generation++;
        if (sameOrder)
        {
            foreach (var grid in list.Items.Cast<ListViewItem>().Select(i => (Grid)i.Content).Prepend((Grid)list.Header))
                for (var c = 0; c < next.Visible.Length; c++) grid.ColumnDefinitions[c].Width = new(next.Visible[c].Preference.Width);
        }
        else
        {
            RebuildRows();
            if (!active) launcher.Focus(FocusState.Programmatic);
        }
        Update();
    }
}
