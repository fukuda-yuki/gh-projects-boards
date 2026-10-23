using System.Globalization;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class PlanWorkspaceView
{
    private void InitializeRefreshCommands()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = char.ConvertFromUtf32((int)Symbol.Refresh), FontSize = 16 });
        content.Children.Add(new TextBlock { Text = "最新の情報に更新" });
        refreshButton.Content = content;
        AutomationProperties.SetName(refreshButton, "最新の情報に更新");
        refreshButton.Click += async (_, _) => { if (refreshButton.IsLoaded) await Run(RefreshRemote); };
        var menu = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
        menu.Items.Add(resetItem);
        menu.Opening += (_, _) => UpdateResetAvailability();
        resetItem.Click += async (_, _) => await Run(ConfirmReset);
        refreshButton.Flyout = menu;
    }
    private void UpdateResetAvailability()
    {
        var reason = busyKind is not null ? "更新または発行が進行中です。" : workspace.Session?.ResetUnavailableReason;
        resetItem.IsEnabled = workspace.Session is not null && reason is null;
        AutomationProperties.SetHelpText(resetItem, reason ?? "");
        ToolTipService.SetToolTip(resetItem, reason ?? "GitHub の現在の状態を読み直します");
    }
    private async Task ConfirmReset()
    {
        if (workspace.Session is not { } session) return;
        if (session.ResetUnavailableReason is { } reason) throw new InvalidOperationException(reason);
        var culture = CultureInfo.GetCultureInfo("ja-JP");
        var changes = session.Changes(Today);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(Label($"{workspace.Selected?.Title} の未発行の変更をすべて破棄します。"));
        if (changes.TaskCount == 0) content.Children.Add(Label("未発行の変更はありません。"));
        else {
            content.Children.Add(Label($"破棄する未発行のタスク {changes.TaskCount.ToString("N0", culture)}"));
            if (changes.EnteredTaskCount > 0) content.Children.Add(Label($"入力した変更 {changes.EnteredTaskCount.ToString("N0", culture)}"
                + (changes.NewTaskCount > 0 ? $"（新しく追加した {changes.NewTaskCount.ToString("N0", culture)} タスクを含む。これらは作られません）" : "")));
            if (changes.RecalculatedTaskCount > 0) content.Children.Add(Label($"再計算で変わった日程 {changes.RecalculatedTaskCount.ToString("N0", culture)}"));
        }
        // Only a change is worth a line: the settings stay and the dates are GitHub's, as the manual says.
        if (session.Document.State.Settings.StatusDate != Today)
            content.Children.Add(Label($"基準日は {Today.ToString("M/d (ddd)", culture)} になります。"));
        content.Children.Add(Label("「元に戻す」で取り消せます。"));
        var dialog = Id(new ContentDialog {
            XamlRoot = XamlRoot, Title = "GitHub の状態に戻しますか？",
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "GitHub の状態に戻す", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close
        }, "PlanResetConfirm");
        using var registration = OperationToken.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        var result = await dialog.ShowAsync();
        OperationToken.ThrowIfCancellationRequested();
        if (result == ContentDialogResult.Primary) await ReadRemote(true);
    }
    private void ShowResetResult(DateOnly day)
    {
        var culture = CultureInfo.GetCultureInfo("ja-JP");
        refreshResult.Title = "GitHub の状態に戻しました（" + DateTimeOffset.Now.ToString("M/d H:mm", culture) + "）";
        refreshResult.Message = $"基準日を {day.ToString("M/d (ddd)", culture)} にしました。日程は GitHub の値のままです。";
        refreshResult.ActionButton = null; refreshResult.Content = null;
        refreshResult.IsOpen = true;
    }
}
