using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace GhProjectsBoards.App;

internal sealed partial class PlanSheetView
{
    private double? pendingWheelOffset;

    internal void ScrollTimeline(PointerRoutedEventArgs args)
    {
        var wheel = args.GetCurrentPoint(this).Properties;
        var horizontal = wheel.IsHorizontalMouseWheel;
        if (!horizontal && (args.KeyModifiers & VirtualKeyModifiers.Shift) == 0) return;
        // Handle before the ListView's ScrollViewer, including at the timeline edges:
        // a horizontal gesture must never fall through and move the shared row viewport.
        args.Handled = true;
        if (remoteBusy || wheel.MouseWheelDelta == 0) return;
        // Respect the Windows wheel amount, retaining fractional/high-resolution deltas.
        // Horizontal wheels count characters; Shift+wheel retains the vertical setting.
        if (!SystemParametersInfo(horizontal ? 0x006Cu : 0x0068u, 0, out var amount, 0)) amount = 3;
        var distance = amount == uint.MaxValue ? ChartViewport : amount * (horizontal ? 16 : RowHeight);
        var pixels = wheel.MouseWheelDelta / 120d * distance * (horizontal ? 1 : -1);
        // ChangeView applies asynchronously. Several native wheel messages can
        // arrive before HorizontalOffset advances, so continue from the requested
        // position until the final ViewChanged acknowledges the new viewport.
        pendingWheelOffset = Math.Clamp((pendingWheelOffset ?? chartHorizontal.HorizontalOffset) + pixels, 0, chartHorizontal.ScrollableWidth);
        if (!chartHorizontal.ChangeView(pendingWheelOffset, null, null, true)) pendingWheelOffset = null;
    }

    private void ChangeTimelineOffset(double offset)
    {
        pendingWheelOffset = null;
        chartHorizontal.ChangeView(offset, null, null, true);
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);
}
