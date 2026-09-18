using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private readonly RowProjection projection;
    private readonly TextBlock viewNotice = new() { TextWrapping = TextWrapping.Wrap };
    private string? deferredViewNotice;
    internal string[] DisplayedRowIds => rows.Select(r => r.ItemId).ToArray();
    internal RowProjection RowProjection => projection;
    private void ReapplyRows(Button launcher)
    {
        if (!CanRefresh) { viewNotice.Text = deferredViewNotice = "IME変換中です。自然に確定・取消してから再適用してください。"; return; }
        deferredViewNotice = null;
        temporaryApplyColumns.Clear();
        quickTitleFilter.Text = session.Workspace.RowView(registration).Title;
        layout = session.Workspace.Columns(registration); projection.Reapply(session.Workspace, registration); RebuildRows(); Update();
        if (!active) FocusViewCommand();
    }
    private async Task ConfigureRowsAsync(Button launcher)
    {
        if (!CanRefresh) { viewNotice.Text = deferredViewNotice = "IME変換中です。自然に確定・取消してから表示設定を開いてください。"; return; }
        deferredViewNotice = null;
        var request = generation;
        var returnGeneration = request;
        if (!await prepareLocalRows() || request != generation || !IsLoaded) return;
        var candidate = session.Workspace.PrepareRowView(registration);
        var definition = candidate.Definition;
        var fields = session.Workspace.LocalColumns(registration);
        string FieldName(string id)
        {
            var field = fields.SingleOrDefault(f => f.Id.NodeId == id);
            return field is null ? $"未確認フィールド [{id}]"
                : field.Name + (fields.Count(f => f.Name == field.Name) > 1 ? $" [{id}]" : "");
        }
        var choices = new[] { (Kind: "Source", Id: (string?)null, Name: "取得順"), (Kind: "Title", Id: (string?)null, Name: "タイトル") }
            .Concat(fields.Select(f => (Kind: "Field", Id: (string?)f.Id.NodeId, Name: FieldName(f.Id.NodeId)))).ToList();
        if (definition.Sort == "Field" && !choices.Any(c => c.Id == definition.FieldId)) choices.Add(("Field", definition.FieldId, $"未確認 [{definition.FieldId}]"));
        var sort = new ComboBox { Header = "並べ替え", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = choices.Select(c => c.Name).ToArray(), SelectedIndex = choices.FindIndex(c => c.Kind == definition.Sort && c.Id == definition.FieldId) };
        var descending = new CheckBox { Content = "降順", IsChecked = definition.Descending, VerticalAlignment = VerticalAlignment.Bottom };
        var title = new TextBox { Header = "タイトルに含む文字", PlaceholderText = "すべてのタイトル", Text = definition.Title };
        AutomationProperties.SetHelpText(title, "入力した文字を含むタイトルを表示します。大文字・小文字を区別しません。");
        AutomationProperties.SetAutomationId(sort, "RowSort"); AutomationProperties.SetAutomationId(descending, "RowDescending"); AutomationProperties.SetAutomationId(title, "RowTitleFilter");
        var filters = (definition.Filters ?? []).ToDictionary(f => f.FieldId, f => (Options: f.OptionIds.ToHashSet(), States: f.States.ToHashSet()));
        var entries = new StackPanel { Spacing = 4 }; var content = new StackPanel { Spacing = 12 };
        var editor = new StackPanel { Spacing = 12 };
        var inputs = new ContentControl { Content = editor, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(preview, "RowCriteriaPreview");
        content.Children.Add(new TextBlock { Text = registration.Snapshot.Title + " の表示", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new ScrollViewer { MaxHeight = 64, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = preview });
        var sortLine = new Grid { ColumnSpacing = 16 };
        sortLine.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); sortLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        sortLine.Children.Add(sort); SetColumn(descending, 1); sortLine.Children.Add(descending);
        editor.Children.Add(sortLine); editor.Children.Add(title);
        editor.Children.Add(new TextBlock { Text = "フィールドで絞り込む", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        editor.Children.Add(new TextBlock { Text = "同じフィールドの選択肢はいずれかに一致、異なるフィールドはすべてに一致する行を表示します。未選択は条件なしです。", TextWrapping = TextWrapping.Wrap });
        string StateName(string state) => state switch { "Empty" => "空値", "Unspecified" => "新規行の未指定", _ => "不明・未取得" };
        string OptionName(string fieldId, string optionId)
        {
            var field = fields.SingleOrDefault(f => f.Id.NodeId == fieldId);
            var option = field?.Options.SingleOrDefault(o => o.Id == optionId);
            return option is null ? $"未確認 [{optionId}]"
                : option.Name + (field!.Options.Count(o => o.Name == option.Name) > 1 ? $" [{optionId}]" : "");
        }
        void Preview()
        {
            var selected = choices[Math.Max(sort.SelectedIndex, 0)];
            descending.IsEnabled = selected.Kind != "Source";
            AutomationProperties.SetHelpText(sort, selected.Id is null ? selected.Name : "フィールドの識別子: " + selected.Id);
            ToolTipService.SetToolTip(sort, selected.Id is null ? selected.Name : "フィールド: " + selected.Id);
            var clauses = filters.Where(f => f.Value.Options.Count + f.Value.States.Count > 0)
                .Select(f => FieldName(f.Key) + ": " + string.Join(" または ", f.Value.Options.Select(id => OptionName(f.Key, id)).Concat(f.Value.States.Select(StateName)))).ToList();
            if (title.Text.Length > 0) clauses.Insert(0, $"タイトルに「{title.Text}」を含む");
            preview.Text = selected.Name + (selected.Kind == "Source" ? "" : descending.IsChecked == true ? " / 降順" : " / 昇順")
                + "\n" + (clauses.Count == 0 ? "絞り込みなし" : string.Join(" ＋ ", clauses));
        }
        void RenderFilters()
        {
            entries.Children.Clear();
            foreach (var id in fields.Select(f => f.Id.NodeId).Concat(filters.Keys).Distinct().ToArray())
            {
                var field = fields.SingleOrDefault(f => f.Id.NodeId == id);
                if (!filters.ContainsKey(id)) filters[id] = ([], []);
                var chosen = filters[id];
                var header = new TextBlock { TextWrapping = TextWrapping.Wrap };
                var group = new Expander { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, IsExpanded = chosen.Options.Count + chosen.States.Count > 0 };
                AutomationProperties.SetAutomationId(group, "RowFilterGroup-" + id);
                AutomationProperties.SetName(group, FieldName(id) + " のフィルター");
                AutomationProperties.SetHelpText(group, "フィールドの識別子: " + id); ToolTipService.SetToolTip(header, "フィールド: " + id);
                var options = new Grid { ColumnSpacing = 12, RowSpacing = 0 };
                options.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); options.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                var optionIndex = 0;
                void Summary() { header.Text = FieldName(id) + (chosen.Options.Count + chosen.States.Count == 0 ? " · 条件なし" : $" · {chosen.Options.Count + chosen.States.Count} 件選択"); Preview(); }
                void Choice(string value, string label, bool state)
                {
                    var set = state ? chosen.States : chosen.Options;
                    var check = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, IsChecked = set.Contains(value), MinWidth = 0,
                        HorizontalAlignment = HorizontalAlignment.Stretch };
                    AutomationProperties.SetAutomationId(check, $"RowFilter-{id}-{value}");
                    AutomationProperties.SetName(check, FieldName(id) + ": " + label);
                    AutomationProperties.SetHelpText(check, state ? label : $"フィールド: {id} / 選択肢: {value}");
                    ToolTipService.SetToolTip(check, state && value == "Empty" ? "GitHubの空値と明示的にローカルクリアした値を含みます。"
                        : state ? label : $"フィールド: {id}\n選択肢: {value}");
                    check.Checked += (_, _) => { set.Add(value); Summary(); }; check.Unchecked += (_, _) => { set.Remove(value); Summary(); };
                    if (optionIndex % 2 == 0) options.RowDefinitions.Add(new() { Height = GridLength.Auto });
                    SetRow(check, optionIndex / 2); SetColumn(check, optionIndex % 2); options.Children.Add(check); optionIndex++;
                }
                foreach (var option in (field?.Options.Select(o => (o.Id, o.Name)) ?? []).Concat(chosen.Options.Where(id => field is null || !field.Options.Any(o => o.Id == id)).Select(id => (id, "未確認"))))
                    Choice(option.Item1, OptionName(id, option.Item1), false);
                Choice("Empty", StateName("Empty"), true); Choice("Unspecified", StateName("Unspecified"), true); Choice("Unknown", StateName("Unknown"), true);
                group.Content = options; entries.Children.Add(group); Summary();
            }
            Preview();
        }
        editor.Children.Add(new ScrollViewer { MaxHeight = 220, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = entries });
        content.Children.Add(inputs);
        content.Children.Add(new TextBlock { Text = "保存時に表示を更新します。その後のセル編集では行を移動せず、必要なときに再適用できます。未確定文字は条件に含めません。", TextWrapping = TextWrapping.Wrap });
        var reset = new Button { Content = "並べ替え・絞り込みをリセット" }; AutomationProperties.SetAutomationId(reset, "RowsReset"); content.Children.Add(reset);
        var error = new TextBlock { Text = session.Workspace.ViewProblem(registration, definition) ?? "", TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(error, "RowSettingsStatus"); content.Children.Add(error);
        reset.Click += (_, _) => { sort.SelectedIndex = 0; descending.IsChecked = false; title.Text = ""; filters.Clear(); RenderFilters(); };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "並べ替え・絞り込み", Content = content,
            PrimaryButtonText = "ローカル保存・適用", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
        dialog.Resources["ContentDialogMaxWidth"] = 720d;
        AutomationProperties.SetAutomationId(dialog, "RowSettingsDialog");
        var cancelled = false; dialog.Closed += (_, _) => cancelled = true; dialog.CloseButtonClick += (_, _) => cancelled = true;
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            e.Cancel = true;
            if (!CanRefresh) { error.Text = "IME変換中のため保存・再適用を保留します。"; return; }
            var selected = choices[sort.SelectedIndex];
            var saved = candidate with { Definition = new(selected.Kind, descending.IsChecked == true, selected.Id, title.Text,
                filters.Where(f => f.Value.Options.Count + f.Value.States.Count > 0).Select(f => new RowFilter(f.Key, f.Value.Options.ToArray(), f.Value.States.ToArray())).ToArray()) };
            var deferral = e.GetDeferral(); dialog.IsPrimaryButtonEnabled = false; inputs.IsEnabled = false; reset.IsEnabled = false;
            try {
                if (!await session.CommitAsync(w => { w.SaveRowView(saved); return w; }, () => !cancelled && IsLoaded && request == generation && CanRefresh))
                { error.Text = "保存できません。保存先・未確認の条件・定義変更を確認してください。候補は保持しています。設定を開き直すかリセットできます。"; return; }
                ReapplyRows(launcher); returnGeneration = generation; e.Cancel = false;
            }
            finally { dialog.IsPrimaryButtonEnabled = true; inputs.IsEnabled = true; reset.IsEnabled = true; deferral.Complete(); }
        };
        sort.SelectionChanged += (_, _) => Preview(); descending.Checked += (_, _) => Preview(); descending.Unchecked += (_, _) => Preview(); title.TextChanged += (_, _) => Preview();
        RenderFilters(); await dialog.ShowAsync();
        if (IsLoaded && generation == returnGeneration && CanRefresh) RestoreWorkspaceFocus();
    }
}
