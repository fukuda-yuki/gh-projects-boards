using System.Globalization;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace GhProjectsBoards.App;

internal sealed partial class PlanWorkspaceView
{
    private readonly Button detailEdit = CommandButton("本文を編集", "PlanDetailEdit", Symbol.Edit);
    private readonly TextBox detailEditor = Id(new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }, "PlanDetailEditor");
    private readonly Grid detailEditPanel = new() { Visibility = Visibility.Collapsed, RowSpacing = 8 };
    private readonly TextBlock detailEditing = Id(Label("編集中"), "PlanDetailEditing"),
        detailPinnedNote = Id(Label("編集を終えると、選択中のタスクを表示します"), "PlanDetailPinnedNote"),
        detailPublishingText = Id(Label("本文を発行しています…"), "PlanDetailPublishing"),
        detailPublished = Id(Label(""), "PlanDetailPublished");
    private readonly Button detailPublish = Id(new Button { Content = "本文を発行" }, "PlanDetailPublishBody"),
        detailCancelEdit = Id(new Button { Content = "キャンセル" }, "PlanDetailCancelEdit"),
        detailOverwrite = Id(new Button { Content = "上書きして発行" }, "PlanDetailOverwrite"),
        detailRevert = Id(new Button { Content = "GitHub の本文に戻す" }, "PlanDetailRevert"),
        detailPublishRetry = Id(new Button { Content = "再試行" }, "PlanDetailPublishRetry");
    private readonly InfoBar detailConflict = FailureBar("PlanDetailConflict", "編集を始めた後に、GitHub で本文が変更されました"),
        detailPublishFailure = FailureBar("PlanDetailPublishFailure", "本文を発行できませんでした");
    private IssueDetail? detailLoadedIssue;
    private string? detailEditIdentity, detailConflictBody, detailPublishedIdentity;
    private string detailStartingBody = "";
    private GhConnectionService? detailEditService;
    private ConnectionContext? detailEditContext;
    private Task detailPublishWork = Task.CompletedTask;
    private bool detailPublishingBody, detailComposing, detailCompositionEnded, settingDetailToggles, detailDiscardPending, detailFocusEditAfterRead;
    private bool DetailEditing => detailEditIdentity is not null;
    private bool DetailChanged => DetailEditing && IssueBodyText.Changed(detailStartingBody, detailEditor.Text);

    private void InitializeDetailEditing(StackPanel header, StackPanel actions)
    {
        detailConflict.Severity = InfoBarSeverity.Warning;
        detailEdit.Padding = new(6, 4, 6, 4); detailEdit.AllowFocusWhenDisabled = true;
        actions.Spacing = 4; actions.Children.Add(detailEdit);
        foreach (var text in new[] { detailEditing, detailPublished, detailPinnedNote }) {
            text.Style = (Style)Application.Current.Resources["WorkspaceMetadataStyle"]; text.Visibility = Visibility.Collapsed;
        }
        detailStateLine.Children.Add(detailEditing); Grid.SetColumn(detailEditing, 2);
        detailStateLine.Children.Add(detailPublished); Grid.SetColumn(detailPublished, 2);
        header.Children.Add(detailPinnedNote);
        AutomationProperties.SetName(detailEditor, "Issue の本文");
        ScrollViewer.SetVerticalScrollBarVisibility(detailEditor, ScrollBarVisibility.Auto);
        detailEditPanel.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        detailEditPanel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        detailEditPanel.Children.Add(detailEditor);
        var footer = new StackPanel { Spacing = 8 }; Grid.SetRow(footer, 1); detailEditPanel.Children.Add(footer);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        detailPublish.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        buttons.Children.Add(detailPublish); buttons.Children.Add(detailCancelEdit);
        footer.Children.Add(buttons); footer.Children.Add(detailPublishingText);
        footer.Children.Add(detailConflict); footer.Children.Add(detailPublishFailure);
        var conflictActions = new StackPanel { Spacing = 4 };
        conflictActions.Children.Add(detailOverwrite); conflictActions.Children.Add(detailRevert); detailConflict.Content = conflictActions;
        detailPublishFailure.ActionButton = detailPublishRetry;
        detailBody.Children.Add(detailEditPanel);
        foreach (var button in new[] { detailPublish, detailCancelEdit, detailOverwrite, detailRevert, detailPublishRetry }) {
            AutomationProperties.SetName(button, (string)button.Content); ToolTipService.SetToolTip(button, button.Content);
        }
        foreach (var text in new[] { detailPublishingText, detailPublished }) AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        detailEdit.Click += (_, _) => BeginDetailEdit();
        detailEditor.TextChanged += (_, _) => UpdateDetailEditCommands();
        detailEditor.TextCompositionStarted += (_, _) => detailComposing = true;
        detailEditor.TextCompositionEnded += (_, _) => {
            detailComposing = false; detailCompositionEnded = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => detailCompositionEnded = false);
        };
        detailEditor.PreviewKeyDown += async (_, args) => {
            if (detailComposing || detailCompositionEnded || PlanSheetView.Down(VirtualKey.Menu)) return;
            if (args.Key == VirtualKey.Enter && PlanSheetView.Down(VirtualKey.Control)) {
                args.Handled = true; if (detailPublish.IsEnabled) await PublishDetailBody();
            } else if (args.Key == VirtualKey.Escape) { args.Handled = true; await DiscardDetailEdit(); }
        };
        detailPublish.Click += async (_, _) => await PublishDetailBody();
        detailCancelEdit.Click += async (_, _) => await DiscardDetailEdit();
        detailOverwrite.Click += async (_, _) => {
            if (detailConflictBody is null || detailPublishingBody || busyKind is not null) return;
            detailStartingBody = detailConflictBody; await PublishDetailBody();
        };
        detailRevert.Click += (_, _) => { if (!detailPublishingBody) LeaveDetailEdit(reload: true); };
        detailPublishRetry.Click += async (_, _) => await PublishDetailBody();
        UpdateDetailEditCommands();
    }
    private void UpdateDetailEditCommands()
    {
        var reason = detailLoadedIssue is not null ? detailLoadedIssue.ViewerCanUpdate ? "" : "この Issue の本文を編集する権限がありません"
            : detailUrl is null ? "GitHub に作成してから編集できます" : "読み込みが終わると編集できます";
        detailEdit.IsEnabled = !DetailEditing && reason.Length == 0;
        AutomationProperties.SetHelpText(detailEdit, reason); ToolTipService.SetToolTip(detailEdit, reason.Length == 0 ? "本文を編集" : reason);
        detailEdit.Visibility = DetailEditing ? Visibility.Collapsed : Visibility.Visible;
        detailReload.IsEnabled = !DetailEditing && detailUrl is not null;
        detailPublish.IsEnabled = DetailChanged && !detailPublishingBody && busyKind is null;
        detailCancelEdit.IsEnabled = !detailPublishingBody;
        detailOverwrite.IsEnabled = detailPublishRetry.IsEnabled = !detailPublishingBody && busyKind is null;
        detailRevert.IsEnabled = !detailPublishingBody;
        detailEditor.IsReadOnly = detailPublishingBody || busyKind is not null;
        detailPublishingText.Visibility = detailPublishingBody ? Visibility.Visible : Visibility.Collapsed;
        detailEditing.Visibility = DetailEditing ? Visibility.Visible : Visibility.Collapsed;
        detailPublished.Visibility = !DetailEditing && detailIdentity is not null && detailIdentity == detailPublishedIdentity ? Visibility.Visible : Visibility.Collapsed;
        detailUpdated.Visibility = DetailEditing || detailPublished.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }
    private void BeginDetailEdit()
    {
        if (!detailEdit.IsEnabled || detailLoadedIssue is not { ViewerCanUpdate: true } issue || detailIdentity is null) return;
        detailEditIdentity = detailIdentity; detailStartingBody = issue.Body;
        detailEditService = workspace.Service; detailEditContext = workspace.Context;
        CancelDetail(); detailIdentity = detailEditIdentity;
        detailPublishedIdentity = null;
        foreach (var child in detailBody.Children.OfType<FrameworkElement>()) child.Visibility = Visibility.Collapsed;
        detailEditor.Text = IssueBodyText.EditorText(issue.Body);
        detailEditPanel.Visibility = detailEditor.Visibility = Visibility.Visible;
        detailConflict.Visibility = detailPublishFailure.Visibility = Visibility.Collapsed;
        detailConflict.IsOpen = detailPublishFailure.IsOpen = false;
        UpdateDetailEditCommands(); UpdateDetailPinnedNote(); detailEditor.Focus(FocusState.Programmatic);
    }
    private void UpdateDetailPinnedNote() => detailPinnedNote.Visibility = DetailEditing && sheet?.IsSelectedRow(detailEditIdentity!) != true ? Visibility.Visible : Visibility.Collapsed;
    private void LeaveDetailEdit(bool reload = false)
    {
        detailEditIdentity = null; detailStartingBody = ""; detailConflictBody = null;
        detailEditService = null; detailEditContext = null;
        detailEditor.Text = ""; detailEditPanel.Visibility = detailEditor.Visibility = detailPinnedNote.Visibility = Visibility.Collapsed;
        detailConflict.IsOpen = detailPublishFailure.IsOpen = false;
        detailFocusEditAfterRead = true;
        CancelDetail(); UpdateDetailEditCommands(); UpdateDetailSelection(reload);
        detailFocusEditAfterRead = DetailVisible && !detailEdit.Focus(FocusState.Programmatic);
    }
    private Task PublishDetailBody()
    {
        if (!DetailEditing || detailPublishingBody || busyKind is not null || detailEditService is null || detailEditContext is null) return detailPublishWork;
        detailPublishingBody = true; UpdateDetailEditCommands(); AnnounceDetail(detailPublishingText);
        detailPublishWork = PublishDetailBodyCore(detailEditService, detailEditContext, detailEditIdentity!, detailStartingBody, detailEditor.Text);
        return detailPublishWork;
    }
    private async Task PublishDetailBodyCore(GhConnectionService service, ConnectionContext context, string identity, string startingBody, string draft)
    {
        detailConflict.IsOpen = detailPublishFailure.IsOpen = false;
        detailConflict.Visibility = detailPublishFailure.Visibility = Visibility.Collapsed;
        try {
            var result = await new IssueBodyPublisher(service).Publish(context, identity, startingBody, draft);
            if (result.Outcome == IssueBodyPublishOutcome.Conflict) {
                detailConflictBody = result.RemoteBody; detailConflict.Visibility = Visibility.Visible; detailConflict.IsOpen = true; return;
            }
            if (result.Outcome == IssueBodyPublishOutcome.Failed) { ShowDetailPublishFailure(result.FailureReason); return; }
            var fresh = await new IssueDetailReader(service).Read(context, identity);
            if (fresh.Issue is null) { detailCache.Remove(identity); ShowDetailPublishFailure(fresh.FailureReason); return; }
            detailCache[identity] = fresh.Issue;
            detailLoadedIssue = fresh.Issue;
            detailPublished.Text = "本文を発行しました（" + DateTimeOffset.Now.ToString("H:mm", CultureInfo.GetCultureInfo("ja-JP")) + "）";
            LeaveDetailEdit();
            detailPublishedIdentity = identity;
            // The full read is already fresh; display it without the selection debounce before restoring focus.
            if (DetailVisible && sheet?.IsSelectedRow(identity) == true && fresh.Issue is not null) {
                CancelDetail(); detailIdentity = identity;
                await DisplayDetail(fresh.Issue, detailGeneration, CancellationToken.None);
                detailFocusEditAfterRead = false; detailEdit.Focus(FocusState.Programmatic);
            }
            AnnounceDetail(detailPublished);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowDetailPublishFailure("本文を発行できません。再試行してください。"); }
        finally { detailPublishingBody = false; UpdateDetailEditCommands(); }
    }
    private void ShowDetailPublishFailure(string reason)
    {
        detailPublishFailure.Message = reason; detailPublishFailure.Visibility = Visibility.Visible; detailPublishFailure.IsOpen = true;
    }
    private async Task<bool> DiscardDetailEdit()
    {
        if (detailDiscardPending) return false;
        detailDiscardPending = true;
        try {
            await detailPublishWork;
            if (!DetailEditing) return true;
            if (DetailChanged) {
                if (VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(p => p.Child is ContentDialog)) return false;
                var dialog = Id(new ContentDialog { XamlRoot = XamlRoot, Title = "本文の変更を破棄しますか？",
                    PrimaryButtonText = "破棄", CloseButtonText = "編集に戻る", DefaultButton = ContentDialogButton.Close }, "PlanDetailDiscard");
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) {
                    if (currentPage != "tasks") Show("tasks");
                    SyncDetailToggles(true); UpdateDetailVisibility(); detailEditor.Focus(FocusState.Programmatic); return false;
                }
            }
            LeaveDetailEdit(); return true;
        }
        finally { detailDiscardPending = false; }
    }
    private void SyncDetailToggles(bool value)
    {
        settingDetailToggles = true;
        try { detailToggle.IsChecked = value; sheet?.SetDetailChecked(value); }
        finally { settingDetailToggles = false; }
    }
    private async Task RequestDetailToggle(bool value)
    {
        if (settingDetailToggles) return;
        if (!value && DetailEditing) {
            SyncDetailToggles(true);
            if (!await DiscardDetailEdit()) return;
        }
        SyncDetailToggles(value);
        if (value) assistToggle.IsChecked = false;
        UpdateDetailVisibility();
    }
    private async Task RequestAssistToggle()
    {
        if (DetailEditing) {
            assistToggle.IsChecked = false;
            if (!await DiscardDetailEdit()) return;
            assistToggle.IsChecked = true; return;
        }
        await RequestDetailToggle(false); await assistLoaded; UpdateAssistVisibility();
    }
}
