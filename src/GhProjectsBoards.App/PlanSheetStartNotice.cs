using System.Globalization;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GhProjectsBoards.App;

// #229: a typed 開始日 that a predecessor moved later is reported once, at the commit that produced it. It
// sits in the existing selection line — the place that already explains the start reason — so it moves no
// rows and stays one small, replaceable surface when the work card is redesigned (#233).
internal sealed partial class PlanSheetView
{
    private readonly StackPanel startNoticePanel = new()
    { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock startNotice = Id(new TextBlock
    { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 560, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, "PlanStartNotice");
    private readonly HyperlinkButton startNoticeAction = Id(new HyperlinkButton { Padding = new(0), MinHeight = 0 }, "PlanStartNoticeAction");
    private readonly Button startNoticeDismiss = Id(new Button
    {
        Padding = new(4, 0, 4, 0), MinWidth = 0, MinHeight = 0, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        BorderThickness = new(0)
    }, "PlanStartNoticeDismiss");
    // The document the message was computed from: another operation makes the message stale, and Refresh
    // dismisses it then instead of leaving an explanation of an older plan on screen.
    private PlanStartNotice? shownStartNotice;
    private PlanDocument? startNoticeDocument;

    private void InitializeStartNotice(Grid explanation)
    {
        // The line is announced, not focused: the PMO keeps working in the cell and reaches both controls in
        // the line's keyboard order.
        AutomationProperties.SetLiveSetting(startNotice, AutomationLiveSetting.Polite);
        startNoticeAction.Click += async (_, _) => await Run(UnlinkStartNotice, "Unlink start notice");
        startNoticeDismiss.Content = new FontIcon { Glyph = "\uE711", FontSize = 10 };
        AutomationProperties.SetName(startNoticeDismiss, "開始日の通知を閉じる");
        ToolTipService.SetToolTip(startNoticeDismiss, "開始日の通知を閉じる");
        startNoticeDismiss.Click += (_, _) => DismissStartNotice(returnFocus: true);
        startNoticePanel.Children.Add(startNotice);
        startNoticePanel.Children.Add(startNoticeAction);
        startNoticePanel.Children.Add(startNoticeDismiss);
        explanation.Children.Add(startNoticePanel);
        SetColumn(startNoticePanel, 1);
    }

    // The message names what the PMO typed, the predecessor that won and the date the schedule holds, using
    // the same task names and date text as the rest of the sheet (#263).
    private void PresentStartNotice(PlanStartNotice notice)
    {
        var message = $"開始日 {notice.Typed.ToString("M/d", CultureInfo.InvariantCulture)} は "
            + $"{notice.PredecessorName} の終了後のため {DateText(notice.Scheduled)} になりました";
        var announce = startNotice.Text != message;
        shownStartNotice = notice; startNoticeDocument = Session.Document;
        startNotice.Text = message;
        AutomationProperties.SetName(startNotice, message);
        ToolTipService.SetToolTip(startNotice, message);
        startNoticeAction.Content = notice.PredecessorName + " とのリンクを解除";
        AutomationProperties.SetName(startNoticeAction, startNoticeAction.Content?.ToString() ?? "");
        ToolTipService.SetToolTip(startNoticeAction, startNoticeAction.Content);
        startNoticePanel.Visibility = Visibility.Visible; slipPill.Margin = new(0);
        if (announce && startNotice.IsLoaded)
            FrameworkElementAutomationPeer.CreatePeerForElement(startNotice)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    // `returnFocus` is the PMO closing the notice themselves: the cell it explains takes selection and
    // keyboard focus back. An operation on the plan dismisses it without moving the PMO's place.
    internal void DismissStartNotice(bool returnFocus = false)
    {
        var notice = shownStartNotice;
        shownStartNotice = null; startNoticeDocument = null;
        startNoticePanel.Visibility = Visibility.Collapsed; slipPill.Margin = new(-10, 0, 0, 0);
        startNotice.Text = "";
        AutomationProperties.SetName(startNotice, "");
        ToolTipService.SetToolTip(startNotice, null);
        if (returnFocus && notice is not null && Rows.ContainsKey(notice.Identity))
            SelectAndFocus(notice.Identity, PlanField.Start, false);
    }

    // One ordinary cell operation: it removes exactly the link the notice names, so one Undo restores it and
    // the typed start holds again if nothing else constrains it.
    private async Task UnlinkStartNotice()
    {
        if (shownStartNotice is not { } notice) return;
        var command = PlanStartNotice.Unlink(Session.Document, notice);
        if (command is null) { DismissStartNotice(returnFocus: true); return; }
        Check(await Session.Execute(command, Today));
        DismissStartNotice();
        Refresh();
        if (Rows.ContainsKey(notice.Identity)) SelectAndFocus(notice.Identity, PlanField.Start, false);
    }
}
