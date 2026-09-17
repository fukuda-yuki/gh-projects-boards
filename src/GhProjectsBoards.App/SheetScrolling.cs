using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private UIElement? wheelSurface;
    private double? wheelHorizontal, wheelVertical;
    private readonly ScrollBar verticalScroll = new() { Orientation = Orientation.Vertical, Width = 16,
        IndicatorMode = ScrollingIndicatorMode.MouseIndicator, IsTabStop = false };
    private bool syncingScrollbar;

    private Grid CreateSheetViewport()
    {
        var viewport = new Grid(); viewport.ColumnDefinitions.Add(new()); viewport.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        viewport.Children.Add(list);
        AutomationProperties.SetAutomationId(verticalScroll, "SheetVerticalScroll");
        AutomationProperties.SetName(verticalScroll, "表の縦スクロール");
        SetColumn(verticalScroll, 1); viewport.Children.Add(verticalScroll);
        verticalScroll.ValueChanged += (_, args) =>
        {
            if (syncingScrollbar || listScroll is null) return;
            wheelVertical = null;
            listScroll.ChangeView(null, args.NewValue, null, true);
        };
        return viewport;
    }
    private void SyncScrollbar()
    {
        if (listScroll is null) return;
        syncingScrollbar = true;
        try
        {
            verticalScroll.Maximum = listScroll.ScrollableHeight;
            verticalScroll.ViewportSize = listScroll.ViewportHeight;
            verticalScroll.SmallChange = rowLines.FirstOrDefault(line => line.IsLoaded && line.ActualHeight > 0)?.ActualHeight ?? 30;
            verticalScroll.LargeChange = listScroll.ViewportHeight;
            verticalScroll.Value = listScroll.VerticalOffset;
            verticalScroll.Visibility = listScroll.ScrollableHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { syncingScrollbar = false; }
    }

    private void AttachWheel()
    {
        DetachWheel();
        wheelSurface = listScroll?.Content as UIElement;
        wheelSurface?.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(ScrollWheel), true);
    }
    private void DetachWheel()
    {
        wheelSurface?.RemoveHandler(PointerWheelChangedEvent, new PointerEventHandler(ScrollWheel));
        wheelSurface = null; wheelHorizontal = wheelVertical = null;
    }
    private void ScrollWheel(object sender, PointerRoutedEventArgs args)
    {
        if (listScroll is null || (args.KeyModifiers & VirtualKeyModifiers.Control) != 0) return;
        var pointer = args.GetCurrentPoint(wheelSurface).Properties;
        var horizontal = pointer.IsHorizontalMouseWheel || (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        if (!(horizontal ? listScroll.ScrollableWidth > 0 : listScroll.ScrollableHeight > 0)) return;
        if (!SystemParametersInfo(horizontal ? 0x006Cu : 0x0068u, 0, out var units, 0)) units = 3;
        var viewport = horizontal ? listScroll.ViewportWidth : listScroll.ViewportHeight;
        var unit = horizontal ? 16 : rowLines.FirstOrDefault(line => line.IsLoaded && line.ActualHeight > 0)?.ActualHeight ?? 30;
        var distance = pointer.MouseWheelDelta / 120d * (units == uint.MaxValue ? viewport : units * unit);
        if (!pointer.IsHorizontalMouseWheel) distance = -distance;
        // A data sheet changes its viewport and realizes the destination together.
        // Native wheel animation can outrun the UI thread and expose empty row slots.
        // Keep Windows' wheel amount, partial deltas and the original native editor.
        args.Handled = true;
        if (horizontal)
        {
            wheelHorizontal = Math.Clamp((wheelHorizontal ?? listScroll.HorizontalOffset) + distance, 0, listScroll.ScrollableWidth);
            listScroll.ChangeView(wheelHorizontal, null, null, true);
        }
        else
        {
            wheelVertical = Math.Clamp((wheelVertical ?? listScroll.VerticalOffset) + distance, 0, listScroll.ScrollableHeight);
            listScroll.ChangeView(null, wheelVertical, null, true);
        }
        diagnostics?.Record("sheet-wheel", new { horizontal, pointer.MouseWheelDelta, units, wheelHorizontal, wheelVertical });
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);
}
