using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private FrameworkElement CreateColumnHeader(PresentationColumn column, int index, TextBlock label, string name)
    {
        var header = new Grid { Style = (Style)Application.Current.Resources["SheetHeaderStyle"] };
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(8, 4, 12, 4), BorderThickness = new(0), CornerRadius = new(0) };
        AutomationProperties.SetAutomationId(button, $"GridHeaderMenu{index}");
        AutomationProperties.SetName(button, name + " の列操作");
        ToolTipService.SetToolTip(button, name + "\n" + (column.Id.FieldId ?? column.Id.Role));
        var menu = new MenuFlyout();
        void Command(string text, string id, Func<Task> action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += async (_, _) => await action(); menu.Items.Add(item);
        }
        if (column.Id.Role is "Title" or "Field")
        {
            Command("昇順で並べ替え", "HeaderSortAscending", () => SaveQuickRowViewAsync(d => d with { Sort = column.Id.Role, FieldId = column.Id.FieldId, Descending = false }));
            Command("降順で並べ替え", "HeaderSortDescending", () => SaveQuickRowViewAsync(d => d with { Sort = column.Id.Role, FieldId = column.Id.FieldId, Descending = true }));
            Command("この列で絞り込む…", "HeaderFilter", () => { ShowColumnFilter(button, column); return Task.CompletedTask; });
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        Command("幅を広げる", "HeaderWiden", () => ChangeColumnAsync(column.Id, widthDelta: 40));
        Command("幅を狭める", "HeaderNarrow", () => ChangeColumnAsync(column.Id, widthDelta: -40));
        if (column.Id.Role == "Field")
        {
            Command("左へ移動", "HeaderMoveLeft", () => ChangeColumnAsync(column.Id, move: -1), index > 1);
            Command("右へ移動", "HeaderMoveRight", () => ChangeColumnAsync(column.Id, move: 1), index < layout.Visible.Length - 2);
            Command("列を非表示", "HeaderHide", () => ChangeColumnAsync(column.Id, hide: true));
        }
        Command("列の設定…", "HeaderAllColumns", () => ConfigureColumnsAsync(button));
        button.Flyout = menu; header.Children.Add(button);

        var resize = new Thumb { Width = 6, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetAutomationId(resize, $"GridColumnResize{index}");
        AutomationProperties.SetName(resize, name + " の幅を変更");
        ToolTipService.SetToolTip(resize, "ドラッグして列幅を変更");
        ColumnCandidate? candidate = null; ColumnLayout? original = null; double width = 0; int request = 0;
        resize.DragStarted += (_, _) =>
        {
            diagnostics?.Record("column-resize-start", new { column.Id, generation });
            if (!CanRefresh) { ViewCommandProblem("IME変換中です。自然に確定・取消してから列幅を変更してください。"); return; }
            request = generation; candidate = session.Workspace.PrepareColumns(registration); original = layout;
            width = candidate.Columns.Single(c => c.Id == column.Id).Width;
        };
        resize.DragDelta += (_, args) =>
        {
            diagnostics?.Record("column-resize-delta", new { column.Id, args.HorizontalChange, width, generation, request });
            if (candidate is null || request != generation || !CanRefresh) return;
            width = Math.Clamp(width + args.HorizontalChange, EditingWorkspace.MinimumColumnWidth, EditingWorkspace.MaximumColumnWidth);
            layout = session.Workspace.Columns(registration, candidate.Columns.Select(c => c.Id == column.Id ? c with { Width = width } : c).ToArray());
            ResizeSheetColumns();
        };
        resize.DragCompleted += async (_, args) =>
        {
            diagnostics?.Record("column-resize-end", new { column.Id, args.HorizontalChange, args.Canceled, width, generation, request });
            if (candidate is null || original is null) return;
            var saved = candidate with { Columns = candidate.Columns.Select(c => c.Id == column.Id ? c with { Width = width } : c).ToArray() };
            candidate = null;
            // Preview changes layout only. Durable failure restores the accepted width.
            layout = original;
            if (!args.Canceled && request == generation && CanRefresh) await SaveQuickColumnsAsync(saved, request);
            ResizeSheetColumns();
        };
        header.Children.Add(resize);
        return header;
    }

    private void ViewCommandProblem(string message)
    {
        columnNotice.Text = message; columnNotice.Visibility = Visibility.Visible;
    }
    private async Task ChangeColumnAsync(ColumnIdentity id, int widthDelta = 0, int move = 0, bool hide = false)
    {
        if (!CanRefresh) { ViewCommandProblem("IME変換中です。自然に確定・取消してから列を変更してください。"); return; }
        var request = generation;
        var candidate = session.Workspace.PrepareColumns(registration);
        var columns = candidate.Columns.ToArray(); var index = Array.FindIndex(columns, c => c.Id == id);
        if (index < 0) return;
        if (move != 0)
        {
            var visible = layout.Visible; var visibleIndex = Array.FindIndex(visible, c => c.Id == id);
            var target = visibleIndex + move;
            if (target <= 0 || target >= visible.Length - 1) return;
            var other = Array.FindIndex(columns, c => c.Id == visible[target].Id);
            (columns[index], columns[other]) = (columns[other], columns[index]);
        }
        else columns[index] = columns[index] with { Visible = !hide,
            Width = Math.Clamp(columns[index].Width + widthDelta, EditingWorkspace.MinimumColumnWidth, EditingWorkspace.MaximumColumnWidth) };
        await SaveQuickColumnsAsync(candidate with { Columns = columns }, request);
    }
    private async Task SaveQuickColumnsAsync(ColumnCandidate candidate, int request)
    {
        if (!await prepareLocalRows() || !IsLoaded || request != generation || !CanRefresh) return;
        if (!await session.CommitAsync(w => { w.SaveColumns(candidate); return w; }, () => IsLoaded && generation == request && CanRefresh))
        { ViewCommandProblem("列の設定を保存できません。元の表示を保持しました。保存先と列定義を確認してください。"); return; }
        columnNotice.Visibility = Visibility.Collapsed;
        ApplyColumnLayout(session.Workspace.Columns(registration), reapplyButton);
        RestoreWorkspaceFocus();
    }
    private async Task<bool> SaveQuickRowViewAsync(Func<RowViewDefinition, RowViewDefinition> change, Func<bool>? stillOpen = null)
    {
        if (!CanRefresh) { ViewCommandProblem("IME変換中です。自然に確定・取消してから表示を変更してください。"); return false; }
        var request = generation;
        var candidate = session.Workspace.PrepareRowView(registration);
        if (!await prepareLocalRows() || !IsLoaded || request != generation || !CanRefresh) return false;
        candidate = candidate with { Definition = change(candidate.Definition) };
        if (!await session.CommitAsync(w => { w.SaveRowView(candidate); return w; }, () => IsLoaded && generation == request && CanRefresh && (stillOpen?.Invoke() ?? true)))
        { ViewCommandProblem("表示条件を保存できません。入力と元の表示を保持しました。保存先とフィールド定義を確認してください。"); return false; }
        columnNotice.Visibility = Visibility.Collapsed;
        ReapplyRows(reapplyButton); RestoreWorkspaceFocus();
        return true;
    }

    private TextBox quickTitleFilter = null!;
    private FrameworkElement CreateQuickFilter()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "タイトル", VerticalAlignment = VerticalAlignment.Center });
        quickTitleFilter = new TextBox { Width = 180, MinHeight = 28, Padding = new(6, 2, 6, 2), PlaceholderText = "含む文字で絞り込み",
            Text = session.Workspace.RowView(registration).Title };
        AutomationProperties.SetAutomationId(quickTitleFilter, "GridQuickTitleFilter");
        AutomationProperties.SetName(quickTitleFilter, "タイトルに含む文字");
        var composing = false;
        quickTitleFilter.TextCompositionStarted += (_, _) => composing = true;
        quickTitleFilter.TextCompositionEnded += (_, _) => composing = false;
        quickTitleFilter.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != VirtualKey.Enter || composing) return;
            e.Handled = true; var value = quickTitleFilter.Text; await SaveQuickRowViewAsync(d => d with { Title = value });
        };
        var apply = new Button { Content = "絞り込み", Padding = new(8, 2, 8, 2), MinHeight = 28 };
        AutomationProperties.SetAutomationId(apply, "GridQuickFilterApply");
        apply.Click += async (_, _) => { if (composing) return; var value = quickTitleFilter.Text; await SaveQuickRowViewAsync(d => d with { Title = value }); };
        var clear = new Button { Content = "解除", Padding = new(8, 2, 8, 2), MinHeight = 28 };
        AutomationProperties.SetAutomationId(clear, "GridQuickFilterClear"); AutomationProperties.SetName(clear, "タイトルの絞り込みを解除");
        clear.Click += async (_, _) => { if (!composing && await SaveQuickRowViewAsync(d => d with { Title = "" })) quickTitleFilter.Text = ""; };
        panel.Children.Add(quickTitleFilter); panel.Children.Add(apply); panel.Children.Add(clear);
        return panel;
    }
    private void ShowColumnFilter(Button launcher, PresentationColumn column)
    {
        if (column.Id.Role == "Title") { quickTitleFilter.Focus(FocusState.Keyboard); quickTitleFilter.SelectAll(); return; }
        if (column.Id.FieldId is not { } id) return;
        var field = registration.Snapshot.Fields.Single(f => f.Id.NodeId == id);
        var previous = session.Workspace.RowView(registration).Filters?.SingleOrDefault(f => f.FieldId == id);
        var options = (previous?.OptionIds ?? []).ToHashSet(); var states = (previous?.States ?? []).ToHashSet();
        var panel = new StackPanel { Spacing = 8, MinWidth = 240, MaxWidth = 420 };
        panel.Children.Add(new TextBlock { Text = column.Name + " [" + id + "]", TextWrapping = TextWrapping.Wrap });
        var choices = new StackPanel { Spacing = 4 };
        void Choice(string value, string label, HashSet<string> selected)
        {
            var check = new CheckBox { Content = label, IsChecked = selected.Contains(value) };
            AutomationProperties.SetAutomationId(check, "HeaderFilter-" + value);
            check.Checked += (_, _) => selected.Add(value); check.Unchecked += (_, _) => selected.Remove(value); choices.Children.Add(check);
        }
        foreach (var option in field.Options)
            Choice(option.Id, option.Name + (field.Options.Count(o => o.Name == option.Name) > 1 ? $" [{option.Id}]" : ""), options);
        foreach (var missing in options.Where(id => !field.Options.Any(o => o.Id == id)).ToArray()) Choice(missing, $"未確認 [{missing}]", options);
        Choice("Empty", "空値", states); Choice("Unspecified", "新規行の未指定", states); Choice("Unknown", "不明・未取得", states);
        panel.Children.Add(new ScrollViewer { Content = choices, MaxHeight = 300 });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var apply = new Button { Content = "保存・適用" }; AutomationProperties.SetAutomationId(apply, "HeaderFilterApply");
        var reset = new Button { Content = "この列の条件を解除" }; AutomationProperties.SetAutomationId(reset, "HeaderFilterClear");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        actions.Children.Add(apply); actions.Children.Add(reset); panel.Children.Add(actions); panel.Children.Add(error);
        var flyout = new Flyout { Content = panel };
        var open = true; flyout.Closed += (_, _) => open = false;
        async Task Save(bool clear)
        {
            apply.IsEnabled = reset.IsEnabled = false;
            try
            {
                var saved = await SaveQuickRowViewAsync(d => d with { Filters = (d.Filters ?? []).Where(f => f.FieldId != id)
                    .Concat(clear || options.Count + states.Count == 0 ? [] : new[] { new RowFilter(id, options.ToArray(), states.ToArray()) }).ToArray() }, () => open);
                if (saved) flyout.Hide(); else error.Text = columnNotice.Text;
            }
            finally { apply.IsEnabled = reset.IsEnabled = true; }
        }
        apply.Click += async (_, _) => await Save(false); reset.Click += async (_, _) => await Save(true);
        flyout.ShowAt(launcher);
    }
}
