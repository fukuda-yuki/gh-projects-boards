using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace GhProjectsBoards.App;

internal sealed partial class PlanSheetView
{
    private AppBarButton moveUp = null!, moveDown = null!, arrangeOutline = null!;
    private readonly Border moveIndicator = Id(new Border { Height = 2, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed }, "PlanSheetMoveIndicator");
    private PlanState? outlineState;
    private PlanOutline? outline;
    private PlanOutline Outline {
        get {
            if (!ReferenceEquals(outlineState, Session.Document.State)) {
                outlineState = Session.Document.State; outline = new(outlineState.Rows);
            }
            return outline!;
        }
    }
    private ImmutableArray<string> MoveTargets => [.. RowIds.Skip(Range.Row).Take(Range.RowCount)];
    private string? MoveUnavailable => remoteBusy ? "更新または発行中は移動できません" : FilterActive ? "絞り込み中は移動できません" : null;
    private string? StepReason(bool down)
    {
        if (MoveUnavailable is { } reason) return reason;
        try { _ = Outline.Step(MoveTargets, down); return null; }
        catch (ArgumentException ex) { return ex.Message; }
    }
    private void RefreshOutlineCommands()
    {
        if (moveUp is null) return;
        Set(moveUp, StepReason(false)); Set(moveDown, StepReason(true));
        Set(arrangeOutline, MoveUnavailable ?? (Outline.IsContiguous ? "すでに階層順です" : null));
        static void Set(AppBarButton button, string? reason) {
            button.IsEnabled = reason is null; button.AllowFocusWhenDisabled = true;
            AutomationProperties.SetHelpText(button, reason ?? "");
            ToolTipService.SetToolTip(button, reason ?? button.Label);
        }
    }
    // A far edge jump places the viewport itself and then focuses without a second bring-into-view, so
    // it takes the whole-row state without SelectWholeRow's own focus request (#228).
    internal void SelectWholeRow(string identity, bool extend, bool focus = true)
    {
        if (!Rows.ContainsKey(identity) || Fields.Length == 0) return;
        var previous = Range;
        wholeRowSelection = true;
        selected = identity; selectedField = Fields[0];
        if (!extend) anchor = identity;
        anchorField = Fields[^1];
        // A refusal explains the operation it refused; choosing another row dismisses it (#241, #240).
        error.Text = "";
        UpdateReason(); RefreshOverviewCommands(); RefreshRealized(previous); if (focus) FocusSelected();
    }
    internal bool HandleOutlineShortcut(VirtualKey key)
    {
        if (!Down(VirtualKey.Menu) || !Down(VirtualKey.Shift) || key is not (VirtualKey.Up or VirtualKey.Down)) return false;
        _ = Run(() => MoveOneSibling(key == VirtualKey.Down), "Move outline"); return true;
    }
    private async Task MoveOneSibling(bool down)
    {
        await CommitPending();
        if (StepReason(down) is { } reason) throw new ArgumentException(reason);
        await ExecuteOutline(Outline.Step(MoveTargets, down));
    }
    private async Task ArrangeOutline()
    {
        await CommitPending();
        if (MoveUnavailable is { } reason) throw new ArgumentException(reason);
        await ExecuteOutline(new ArrangePlanOutline());
    }
    private async Task ExecuteOutline(PlanCommand command)
    {
        var selectionIds = MoveTargets;
        var active = selected; var activeField = selectedField;
        var reversed = RowIndexes.GetValueOrDefault(selected) < RowIndexes.GetValueOrDefault(anchor);
        var save = Session.Execute(command, Today);
        RefreshOrderKeepingViewport(() => {
            Refresh();
            // Preserve the same selected identities, even when subtree gathering changes endpoints.
            var visible = selectionIds.Where(RowIndexes.ContainsKey).OrderBy(id => RowIndexes[id]).ToArray();
            if (visible.Length > 0) {
                anchor = reversed ? visible[^1] : visible[0]; selected = reversed ? visible[0] : visible[^1];
                UpdateReason(); RefreshOverviewCommands(); RefreshRealized();
                if (RowIndexes.ContainsKey(active)) FocusCell(active, activeField, false);
            }
        }, active);
        Check(await save);
    }
    private void RefreshOrderKeepingViewport(Action refresh, string active)
    {
        var scroll = rowScroll ?? Descendants(List).OfType<ScrollViewer>().FirstOrDefault();
        var panel = List.ItemsPanelRoot as ItemsStackPanel;
        var offset = scroll?.VerticalOffset ?? 0;
        var mode = panel?.ItemsUpdatingScrollMode;
        // Collection moves must retain coordinates rather than anchor the old first item.
        if (panel is not null) panel.ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset;
        try {
            refresh();
            List.UpdateLayout();
            if (scroll is null) return;
            if (RowIndexes.TryGetValue(active, out var index)) {
                var top = index * RowHeight;
                if (top < offset) offset = top;
                else if (top + RowHeight > offset + scroll.ViewportHeight)
                    offset = top + RowHeight - scroll.ViewportHeight;
            }
            scroll.ChangeView(null, Math.Max(0, offset), null, true);
            List.UpdateLayout();
        } finally {
            if (panel is not null && mode is { } original) panel.ItemsUpdatingScrollMode = original;
        }
    }
    internal void BeginRowMove(string identity, UIElement target, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !Rows.ContainsKey(identity)) return;
        var extend = (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        var current = MoveTargets;
        if (extend || !current.Contains(identity)) SelectWholeRow(identity, extend);
        else { selectedField = Fields[0]; anchorField = Fields[^1]; wholeRowSelection = true; RefreshRealized(); FocusSelected(); }
        StartDrag(false, identity, selectedField, target, args);
        if (drag is not { } operation) return;
        operation.Move = true; operation.Targets = MoveTargets; operation.Narrow = !extend && current.Contains(identity);
        _ = Run(async () => {
            await CommitPending();
            if (!ReferenceEquals(drag, operation)) return;
            operation.State = Session.Document.State;
            try {
                if (MoveUnavailable is { } unavailable) throw new ArgumentException(unavailable);
                var roots = Outline.Roots(operation.Targets);
                operation.MoveCount = roots.Sum(id => Outline.Subtree(id).Length);
                var parent = Rows[roots[0]].Parent;
                operation.MoveScope = parent is not null && Rows.TryGetValue(parent, out var parentRow)
                    ? $"「{parentRow.Title}」の中でだけ移動できます（親を変えるにはインデント／アウトデント）"
                    : "最上位の行の間でだけ移動できます";
                operation.Gaps = Outline.Gaps(operation.Targets, RowIds.Where(Rows.ContainsKey).ToArray());
            } catch (ArgumentException ex) { operation.MoveReason = ex.Message; }
            operation.Ready = true;
        }, "Begin row move");
    }
    private void UpdateMoveDrag(Drag operation, double gap, double scrollOffset)
    {
        operation.Destination = operation.Gaps?.Snap(gap);
        moveIndicator.Visibility = operation.Destination is null ? Visibility.Collapsed : Visibility.Visible;
        if (operation.Destination is { } destination) {
            moveIndicator.Width = SheetViewport;
            moveIndicator.Margin = new(0, Math.Clamp(destination.Index * RowHeight - scrollOffset, 0, Math.Max(0, List.ActualHeight - 2)), 0, 0);
        }
        selection.Text = operation.Destination is null ? operation.MoveReason ?? operation.MoveScope : $"{operation.MoveCount}行を移動";
        selection.MaxWidth = Math.Max(0, (selection.Parent as FrameworkElement)?.ActualWidth
            - selectedTitle.ActualWidth - (issueLink.Visibility == Visibility.Visible ? issueLink.ActualWidth : 0) - 52 ?? 0);
        ToolTipService.SetToolTip(selection, selection.Text);
        AutomationProperties.SetName(selection, selection.Text);
    }
}
