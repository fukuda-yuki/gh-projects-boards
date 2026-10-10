using System.Globalization;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace GhProjectsBoards.App;

internal sealed partial class PlanWorkspaceView
{
    private readonly ContentControl surfaceHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly Grid busyOverlay = new() { Visibility = Visibility.Collapsed };
    private readonly Border busyBackdrop = new();
    private readonly ContentControl busyPanel = Id(new ContentControl {
        MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(24), IsTabStop = true,
        Visibility = Visibility.Collapsed }, "PlanBusyPanel");
    private readonly ProgressRing busyRing = Id(new ProgressRing { Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center, IsActive = false, IsIndeterminate = true }, "PlanBusyRing");
    private readonly TextBlock busyHeading = Id(new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, "PlanBusyHeading");
    private readonly TextBlock busyExplanation = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBlock busyNote = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly Button busyCancel = Id(new Button { Content = "中止", HorizontalAlignment = HorizontalAlignment.Center }, "PlanBusyCancel");
    private readonly StackPanel busySteps = new() { Spacing = 8 };
    private readonly Dictionary<string, (TextBlock Label, TextBlock Count)> busyStepRows = [];
    private readonly HashSet<string> completedBusySteps = [];
    private readonly DispatcherTimer busyDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Control? busyReturnFocus;
    private string? busyKind;
    private RemoteProgress? currentBusyProgress;
    private int busyGeneration;

    private void InitializeBusy(Grid card)
    {
        surfaceHost.Content = surfaces;
        busyBackdrop.Background = busyPanel.Background = PlanSheetView.Brush("WorkspaceCardBrush");
        busyExplanation.Foreground = busyNote.Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush");
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(busyRing); content.Children.Add(busyHeading); content.Children.Add(busyExplanation);
        content.Children.Add(busySteps); content.Children.Add(busyCancel); content.Children.Add(busyNote);
        busyPanel.Content = new ScrollViewer { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        busyOverlay.Children.Add(busyBackdrop); busyOverlay.Children.Add(busyPanel);
        card.Children.Add(busyOverlay); Grid.SetRowSpan(busyOverlay, 2);
        AutomationProperties.SetLiveSetting(busyPanel, AutomationLiveSetting.Polite);
        busyDelay.Tick += (_, _) => { busyDelay.Stop(); if (busyKind is not null && !closing) RevealBusy(); };
        busyCancel.Click += (_, _) => operationCancellation?.Cancel();
    }
    private IProgress<RemoteProgress> BeginBusy(string kind, string heading, bool resolvingUrl = false)
    {
        busyReturnFocus = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        busyKind = kind; var generation = ++busyGeneration;
        completedBusySteps.Clear(); currentBusyProgress = null; busyStepRows.Clear(); busySteps.Children.Clear();
        busyHeading.Text = heading;
        busyExplanation.Text = kind == "publish"
            ? "GitHub に書き込んでいます。終わるまで計画は操作できません。"
            : "GitHub から プロジェクトの内容を読み込んでいます。終わるまで計画は操作できません。";
        busyCancel.IsEnabled = true;
        busyCancel.Visibility = kind == "publish" ? Visibility.Collapsed : Visibility.Visible;
        busyNote.Text = kind == "publish"
            ? "ウィンドウを閉じると発行は中断されます。次に 発行 すると残りを再開します。"
            : kind == "refresh" ? "中止すると、操作の前の状態に戻ります。作成済みと確認した Issue と行の対応は残ります。"
            : "中止すると、操作の前の状態に戻ります。";
        var steps = kind == "publish"
            ? new[] { "最新情報の確認", "新規 Issue", "フィールド・担当者", "親子関係", "先行タスク", "表示順", "検証" }
            : resolvingUrl ? new[] { "Project を確認", "プロジェクトの項目を取得", "日程を計算" }
            : new[] { "プロジェクトの項目を取得", "日程を計算" };
        foreach (var step in steps) {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var label = new TextBlock { Text = step, TextWrapping = TextWrapping.Wrap };
            var count = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
            row.Children.Add(label); row.Children.Add(count); Grid.SetColumn(count, 1);
            busySteps.Children.Add(row); busyStepRows.Add(step, (label, count));
        }
        busyBackdrop.Opacity = kind == "open" ? 1 : 0.85;
        busyOverlay.Visibility = Visibility.Visible;
        SetRemotePresentation();
        UpdateBusyProgress(new(steps[0]));
        AnnounceRemote(heading);
        if (kind == "open") busyDelay.Start(); else RevealBusy();
        // Progress<T> marshals to this view's dispatcher. Ignore queued reports from an ended operation.
        return new Progress<RemoteProgress>(value => {
            if (!closing && generation == busyGeneration && busyKind is not null) UpdateBusyProgress(value);
        });
    }
    private void RevealBusy()
    {
        busyPanel.Visibility = Visibility.Visible; busyRing.IsActive = true;
        var generation = busyGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
            if (closing || generation != busyGeneration || busyKind is null) return;
            if (busyCancel.Visibility == Visibility.Visible && busyCancel.IsEnabled) busyCancel.Focus(FocusState.Programmatic);
            else busyPanel.Focus(FocusState.Programmatic);
            (FrameworkElementAutomationPeer.FromElement(busyPanel) ?? FrameworkElementAutomationPeer.CreatePeerForElement(busyPanel))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });
    }
    private void UpdateBusyProgress(RemoteProgress value)
    {
        var announce = currentBusyProgress?.Stage != value.Stage || value.Total is not null && value.Completed == value.Total;
        if (currentBusyProgress is { } previous && previous.Stage != value.Stage &&
            (busyKind != "publish" || previous.Total is null || previous.Completed == previous.Total))
            completedBusySteps.Add(previous.Stage);
        completedBusySteps.Remove(value.Stage);
        // Creation verification precedes another write phase and its final verification.
        if (busyKind == "publish" && value.Stage != "検証") completedBusySteps.Remove("検証");
        currentBusyProgress = value;
        if (busyKind != "publish" && value.Stage == "日程を計算") {
            busyCancel.IsEnabled = false;
            busyNote.Text = "取得した内容を反映しています。完了までお待ちください。";
        }
        foreach (var (stage, row) in busyStepRows) {
            var current = stage == value.Stage;
            row.Label.Text = (completedBusySteps.Contains(stage) ? "✓ " : "") + stage;
            row.Count.FontWeight = row.Label.FontWeight = current ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            row.Count.Foreground = row.Label.Foreground = PlanSheetView.Brush(current || completedBusySteps.Contains(stage) ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            if (current) row.Count.Text = value.Completed is { } count && value.Total is { } total
                ? count.ToString("N0", CultureInfo.GetCultureInfo("ja-JP")) + " / " + total.ToString("N0", CultureInfo.GetCultureInfo("ja-JP")) : "";
        }
        var countText = busyStepRows.TryGetValue(value.Stage, out var currentRow) ? currentRow.Count.Text : "";
        AutomationProperties.SetName(busyPanel, busyHeading.Text + "、" + value.Stage + (countText.Length > 0 ? " " + countText : ""));
        if (announce && busyPanel.Visibility == Visibility.Visible)
            FrameworkElementAutomationPeer.FromElement(busyPanel)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private void EndBusy(string announcement)
    {
        busyDelay.Stop(); busyGeneration++; busyKind = null;
        busyRing.IsActive = false; busyPanel.Visibility = busyOverlay.Visibility = Visibility.Collapsed;
        SetRemotePresentation();
        if (!closing && busyReturnFocus?.Focus(FocusState.Programmatic) != true) projectPicker.Focus(FocusState.Programmatic);
        busyReturnFocus = null;
        AnnounceRemote(announcement);
    }
    private void AnnounceRemote(string message)
    {
        if (closing) return;
        var peer = FrameworkElementAutomationPeer.FromElement(statusCounts) ?? FrameworkElementAutomationPeer.CreatePeerForElement(statusCounts);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, message, "PlanRemoteOperation");
    }
    private async Task OpenProject(ProjectChoice? choice, string? address = null)
    {
        var previousPage = currentPage;
        title.Text = choice?.Title ?? "プロジェクトを開いています";
        projectMetadata.Text = choice is null ? "" : $"{choice.OwnerLogin} · Project {choice.Number}";
        statusBar.Visibility = Visibility.Visible;
        statusCounts.Text = choice is null ? "プロジェクトを確認中" : choice.Title + " を読み込み中";
        var reporter = BeginBusy("open", choice is null ? "プロジェクトを開いています" : choice.Title + " を開いています", resolvingUrl: choice is null);
        var outcome = "読み込みを中止しました";
        try {
            if (choice is null) {
                choice = await new ProjectDiscovery(workspace.Service!).ResolveAsync(workspace.Context!, address!, OperationToken);
                title.Text = choice.Title; projectMetadata.Text = $"{choice.OwnerLogin} · Project {choice.Number}";
                statusCounts.Text = choice.Title + " を読み込み中";
                busyHeading.Text = choice.Title + " を開いています";
                UpdateBusyProgress(new("プロジェクトの項目を取得"));
            }
            await workspace.Open(choice, OperationToken, reporter);
            Opened(); outcome = choice.Title + " を開きました";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            if (ex is not OperationCanceledException || !OperationToken.IsCancellationRequested)
                outcome = "プロジェクトを開けませんでした。" + ex.Message;
            RefreshLists(); Show(previousPage); UpdateStatus();
            throw;
        }
        finally { EndBusy(outcome); }
    }
}
