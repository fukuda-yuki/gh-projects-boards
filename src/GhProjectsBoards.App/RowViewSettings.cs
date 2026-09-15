using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private readonly RowProjection projection;
    private readonly TextBlock viewNotice = new() { TextWrapping = TextWrapping.Wrap };
    internal string[] DisplayedRowIds => rows.Select(r => r.ItemId).ToArray();
    internal RowProjection RowProjection => projection;
    private void ReapplyRows(Button launcher)
    {
        if (!CanRefresh) { viewNotice.Text = "IME変換中です。自然に確定・取消してから再適用してください。"; return; }
        layout = session.Workspace.Columns(registration); projection.Reapply(session.Workspace, registration); RebuildRows(); Update();
        if (!active) launcher.Focus(FocusState.Programmatic);
    }
    private async Task ConfigureRowsAsync(Button launcher)
    {
        if (!CanRefresh) { viewNotice.Text = "IME変換中です。自然に確定・取消してから表示設定を開いてください。"; return; }
        var request = generation;
        if (!await prepareLocalRows() || request != generation || !IsLoaded) return;
        var candidate = session.Workspace.PrepareRowView(registration);
        var definition = candidate.Definition;
        var fields = session.Workspace.LocalColumns(registration);
        var choices = new[] { (Kind: "Source", Id: (string?)null, Name: "取得順"), (Kind: "Title", Id: (string?)null, Name: "タイトル") }
            .Concat(fields.Select(f => (Kind: "Field", Id: (string?)f.Id.NodeId, Name: $"{f.Name} [{f.Id.NodeId}]"))).ToList();
        if (definition.Sort == "Field" && !choices.Any(c => c.Id == definition.FieldId)) choices.Add(("Field", definition.FieldId, $"未確認 [{definition.FieldId}]"));
        var sort = new ComboBox { Header = "並べ替え", ItemsSource = choices.Select(c => c.Name).ToArray(), SelectedIndex = choices.FindIndex(c => c.Kind == definition.Sort && c.Id == definition.FieldId) };
        var descending = new CheckBox { Content = "降順", IsChecked = definition.Descending };
        var title = new TextBox { Header = "タイトルに含む文字（大文字・小文字を区別しない）", Text = definition.Title };
        AutomationProperties.SetAutomationId(sort, "RowSort"); AutomationProperties.SetAutomationId(descending, "RowDescending"); AutomationProperties.SetAutomationId(title, "RowTitleFilter");
        var filters = (definition.Filters ?? []).ToDictionary(f => f.FieldId, f => (Options: f.OptionIds.ToHashSet(), States: f.States.ToHashSet()));
        var entries = new StackPanel { Spacing = 8 }; var content = new StackPanel { Spacing = 8 };
        content.Children.Add(sort); content.Children.Add(descending); content.Children.Add(title);
        void RenderFilters()
        {
            entries.Children.Clear();
            foreach (var id in fields.Select(f => f.Id.NodeId).Concat(filters.Keys).Distinct().ToArray())
            {
                var field = fields.SingleOrDefault(f => f.Id.NodeId == id);
                entries.Children.Add(new TextBlock { Text = $"{field?.Name ?? "未確認"} [{id}]（選択なし: 条件なし）", TextWrapping = TextWrapping.Wrap });
                if (!filters.ContainsKey(id)) filters[id] = ([], []);
                var chosen = filters[id];
                void Choice(string value, string label, bool state)
                {
                    var set = state ? chosen.States : chosen.Options;
                    var check = new CheckBox { Content = label, IsChecked = set.Contains(value) };
                    AutomationProperties.SetAutomationId(check, $"RowFilter-{id}-{value}");
                    check.Checked += (_, _) => set.Add(value); check.Unchecked += (_, _) => set.Remove(value); entries.Children.Add(check);
                }
                foreach (var option in (field?.Options.Select(o => (o.Id, o.Name)) ?? []).Concat(chosen.Options.Where(id => field is null || !field.Options.Any(o => o.Id == id)).Select(id => (id, "未確認"))))
                    Choice(option.Item1, $"{option.Item2} [{option.Item1}]", false);
                Choice("Empty", "空値（明示的ローカルクリアを含む）", true); Choice("Unspecified", "新規行の未指定", true); Choice("Unknown", "不明・未取得", true);
            }
        }
        content.Children.Add(new ScrollViewer { MaxHeight = 240, Content = entries });
        var reset = new Button { Content = "行設定をリセット" }; AutomationProperties.SetAutomationId(reset, "RowsReset"); content.Children.Add(reset);
        var error = new TextBlock { Text = session.Workspace.ViewProblem(registration, definition) ?? "", TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(error, "RowSettingsStatus"); content.Children.Add(error);
        reset.Click += (_, _) => { sort.SelectedIndex = 0; descending.IsChecked = false; title.Text = ""; filters.Clear(); RenderFilters(); };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Projectの行表示設定", Content = content,
            PrimaryButtonText = "ローカル保存・適用", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(dialog, "RowSettingsDialog");
        var cancelled = false; dialog.Closed += (_, _) => cancelled = true; dialog.CloseButtonClick += (_, _) => cancelled = true;
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            e.Cancel = true;
            if (!CanRefresh) { error.Text = "IME変換中のため保存・再適用を保留します。"; return; }
            var selected = choices[sort.SelectedIndex];
            var saved = candidate with { Definition = new(selected.Kind, descending.IsChecked == true, selected.Id, title.Text,
                filters.Where(f => f.Value.Options.Count + f.Value.States.Count > 0).Select(f => new RowFilter(f.Key, f.Value.Options.ToArray(), f.Value.States.ToArray())).ToArray()) };
            var deferral = e.GetDeferral(); dialog.IsPrimaryButtonEnabled = false; sort.IsEnabled = descending.IsEnabled = title.IsEnabled = reset.IsEnabled = false; foreach (var check in entries.Children.OfType<CheckBox>()) check.IsEnabled = false;
            try {
                if (!await session.CommitAsync(w => { w.SaveRowView(saved); return w; }, () => !cancelled && IsLoaded && request == generation && CanRefresh))
                { error.Text = "保存できません。保存先・未確認の条件・定義変更を確認してください。候補は保持しています。設定を開き直すかリセットできます。"; return; }
                ReapplyRows(launcher); e.Cancel = false;
            }
            finally { dialog.IsPrimaryButtonEnabled = true; sort.IsEnabled = descending.IsEnabled = title.IsEnabled = reset.IsEnabled = true; foreach (var check in entries.Children.OfType<CheckBox>()) check.IsEnabled = true; deferral.Complete(); }
        };
        RenderFilters(); await dialog.ShowAsync();
    }
}
