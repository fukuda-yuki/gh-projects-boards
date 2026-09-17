using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private async Task CompareAsync()
    {
        if (!CanRefresh) { status.Text = "IME変換を自然な操作で確定・取消してから比較してください。"; return; }
        CancelPending();
        var request = generation;
        try { await ShowComparisonAsync(); }
        finally { if (IsLoaded && generation == request && CanRefresh) RestoreWorkspaceFocus(); }
    }
    private async Task ShowComparisonAsync()
    {
        var fields = session.Workspace.Fields.Where(f => f.Conflict || f.Observation?.Reason is not null).ToArray();
        var diagnostics = string.Join("\n", session.Workspace.StructuralChanges.Concat(session.Workspace.UndoWarnings)
            .Concat(layout.Columns.Where(c => !c.Available).Select(c => $"未確認の列設定を保持: {c.Id.FieldId}")));
        if (fields.Length == 0)
        {
            var summary = new ContentDialog { XamlRoot = XamlRoot, Title = "構成変更とUndo", CloseButtonText = "閉じる",
                Content = new ScrollViewer { MaxHeight = 340, Content = ComparisonText("競合・未確認の保存フィールドはありません。\n" + diagnostics) } };
            await summary.ShowAsync(); return;
        }
        var projects = session.Workspace.CheckpointRegistrations.ToArray();
        string Label(DraftField field)
        {
            var observedProject = field.Observation?.Project;
            var project = projects.SingleOrDefault(p => p.Snapshot.Id == observedProject)?.Snapshot;
            var item = project?.Items.SingleOrDefault(i => i.Id.NodeId == field.Key.NodeId);
            var issue = project?.Issues.Values.SingleOrDefault(i => i.Id.NodeId == (field.Key.Kind == "Title" ? field.Key.NodeId : item?.ContentId?.NodeId));
            var identity = issue is null ? field.Key.NodeId : $"{issue.Repository.NameWithOwner} #{issue.Number}";
            var name = field.Key.Kind == "Title" ? "タイトル" : project?.Fields.SingleOrDefault(f => f.Id.NodeId == field.Key.FieldId)?.Name ?? "単一選択";
            return $"{identity} / {name} [{field.Key.FieldId ?? "Title"}] / {project?.Title ?? observedProject?.NodeId ?? "未確認のProject"} [{observedProject?.NodeId ?? "?"}]";
        }
        var picker = new ComboBox { Header = "比較するフィールド", ItemsSource = fields.Select(Label).ToArray(), SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(picker, "ConflictField");
        var context = ComparisonText("");
        var notice = new InfoBar { IsOpen = true, IsClosable = false };
        var values = new Grid { ColumnSpacing = 20 };
        values.ColumnDefinitions.Add(new()); values.ColumnDefinitions.Add(new()); values.ColumnDefinitions.Add(new());
        TextBlock ValueColumn(string heading, string id, int column)
        {
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(ComparisonText(heading));
            var value = ComparisonText("", emphasis: true); AutomationProperties.SetAutomationId(value, id);
            panel.Children.Add(value); Grid.SetColumn(panel, column); values.Children.Add(panel); return value;
        }
        var baseline = ValueColumn("B 基準", "ConflictBaselineValue", 0);
        var localValue = ValueColumn("L ローカル", "ConflictLocalValue", 1);
        var remoteValue = ValueColumn("R GitHub", "ConflictRemoteValue", 2);
        var comparison = new ContentControl { Content = values, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(comparison, "ConflictComparison");
        var pending = ComparisonText("");
        var text = new TextBox { Header = "別のタイトル" }; AutomationProperties.SetAutomationId(text, "ConflictAlternativeTitle");
        var options = new ComboBox { Header = "別の選択肢", DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(options, "ConflictAlternativeOption");
        var clear = new CheckBox { Content = "明示的にクリア" }; AutomationProperties.SetAutomationId(clear, "ConflictAlternativeClear");
        var other = new Button { Content = "別の値をローカル採用" }; AutomationProperties.SetAutomationId(other, "ConflictUseAlternative");
        var identityDetails = ComparisonText("");
        var details = new Expander { Header = "所有範囲・識別情報・構成変更", Content = identityDetails,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var content = new StackPanel { Spacing = 12 };
        foreach (var element in new FrameworkElement[] { picker, context, notice, comparison, pending, text, options, clear, other, details }) content.Children.Add(element);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "競合・未確認の比較（ローカルのみ）",
            Content = new ScrollViewer { MaxHeight = 460, Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = "GitHub値を採用", SecondaryButtonText = "ローカル値を保持", CloseButtonText = "閉じる", DefaultButton = ContentDialogButton.Close };
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        AutomationProperties.SetAutomationId(dialog, "ConflictDialog");
        DraftField selected = fields[0]; ResolutionDecision? decision = null; LocalValue? alternative = null;
        void Show()
        {
            selected = session.Workspace.Fields.Single(f => f.Key == fields[picker.SelectedIndex].Key);
            var remote = selected.Observation!;
            string Display(string? value) => value is null ? "（明示的な空値）" : selected.Key.Kind == "Title" ? value
                : $"{remote.Options.SingleOrDefault(o => o.Id == value)?.Name ?? "選択肢不明"} [ID: {value}]";
            baseline.Text = Display(selected.Baseline);
            localValue.Text = selected.Change is { Clear: true } ? "明示的にクリア" : Display(selected.Change is { } local ? local.Value : selected.Baseline);
            remoteValue.Text = remote.Availability is ValueAvailability.Present or ValueAvailability.Empty ? Display(remote.Value)
                : $"未確認（{EditingWorkspace.AvailabilityText(remote.Availability)}）";
            // Expose the same labelled comparison as one accessible group while keeping the visible values side by side.
            AutomationProperties.SetName(comparison, $"B 基準: {baseline.Text}\nL ローカル: {localValue.Text}\nR GitHub: {remoteValue.Text}");
            context.Text = $"Project: {projects.SingleOrDefault(p => p.Snapshot.Id == remote.Project)?.Snapshot.Title ?? remote.Project.NodeId}\n観測: {remote.At.LocalDateTime:yyyy-MM-dd HH:mm:ss}";
            decision = selected.Conflict && remote.Reason is null ? session.Workspace.Decision(selected.Key) : null;
            notice.Title = decision is null ? "値を確認できるまで解消を保留します" : "採用する値を選んでください";
            notice.Message = remote.Reason ?? "この選択はローカル保存のみです。GitHubへ送信するには、別途Applyで差分を確認します。";
            notice.Severity = decision is null ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
            pending.Text = selected.Buffer is null ? "" : "未確定文字（採用値には含みません）: " + selected.Buffer;
            pending.Visibility = selected.Buffer is null ? Visibility.Collapsed : Visibility.Visible;
            identityDetails.Text = $"所有: {(selected.Key.Kind == "Title" ? "Issue（同じアカウント内で共有）" : "Project項目")}\nProject ID: {remote.Project.NodeId}\n対象 ID: {selected.Key.NodeId}\nフィールド ID: {selected.Key.FieldId ?? "Issue title"}";
            if (diagnostics.Length > 0) identityDetails.Text += "\n\n構成変更・Undo:\n" + diagnostics;
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = other.IsEnabled = decision is not null;
            text.IsEnabled = options.IsEnabled = clear.IsEnabled = decision is not null;
            text.Visibility = selected.Key.Kind == "Title" ? Visibility.Visible : Visibility.Collapsed;
            options.Visibility = clear.Visibility = selected.Key.Kind == "Select" ? Visibility.Visible : Visibility.Collapsed;
            text.Text = selected.Change?.Value ?? selected.Baseline ?? ""; options.ItemsSource = remote.Options; options.SelectedIndex = -1; clear.IsChecked = false;
        }
        picker.SelectionChanged += (_, _) => Show();
        other.Click += (_, _) =>
        {
            alternative = selected.Key.Kind == "Title" ? new(text.Text) : clear.IsChecked == true ? new(null, true)
                : options.SelectedItem is SelectOption option ? new(option.Id) : null;
            if (alternative is not null) dialog.Hide();
        };
        Show(); var result = await dialog.ShowAsync();
        if (decision is null || result == ContentDialogResult.None && alternative is null) return;
        var chosenValue = alternative ?? (result == ContentDialogResult.Primary ? selected.Observation!.Value : selected.Change is { } local ? local.Value : selected.Baseline) switch
        { null => new LocalValue(null, true), var chosen => new LocalValue(chosen) };
        await session.CommitAsync(candidate => { candidate.Resolve(selected.Observation!.Project.NodeId, decision, chosenValue); return candidate; }, () => IsLoaded && CanRefresh);
        Update();
    }
    private static TextBlock ComparisonText(string text, bool emphasis = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
        FontWeight = emphasis ? FontWeights.SemiBold : FontWeights.Normal
    };
}
