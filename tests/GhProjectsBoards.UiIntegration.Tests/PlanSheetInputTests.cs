using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanSheetHostedTests
{
    [TestCase(false, false), TestCase(true, false), TestCase(false, true), TestCase(true, true), Category("PlanSheetNative")]
    public async Task HorizontalWheelOverGanttMovesDatesWithoutMovingRowsOrSheet(bool shift, bool header)
    {
        var before = session.Document.State;
        var undo = session.UndoCount;
        ScrollViewer vertical = null!;
        double offset = 0, rowOffset = 0, sheetOffset = 0, barX = 0;
        FrameworkElement target = null!;
        double x = 0;
        await Ui.Run(() => {
            vertical = ScrollMeasurementViewport();
            target = header ? Ui.Find<FrameworkElement>("PlanGanttHeader") : sheet.Realized.Single(r => r.Identity == "I1");
            x = header ? .5 : (sheet.SheetViewport + sheet.ChartViewport / 2) / target.ActualWidth;
            offset = sheet.ChartOffset; rowOffset = vertical.VerticalOffset; sheetOffset = sheet.SheetOffset;
            barX = Ui.Find<FrameworkElement>("PlanBar1").TransformToVisual(sheet).TransformPoint(new()).X;
            Assert.That(Ui.Find<ScrollViewer>("PlanGanttHorizontal").ScrollableWidth - offset, Is.GreaterThan(100));
        });
        SheetNativeInput.Move(await SheetNativeInput.PointFor(target, x));
        await SheetNativeInput.ScrollWheel(shift ? -120 : 120, horizontal: !shift, shift: shift);
        await Ui.Until(() => sheet.ChartOffset > offset);
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(vertical.VerticalOffset, Is.EqualTo(rowOffset));
            Assert.That(sheet.SheetOffset, Is.EqualTo(sheetOffset));
            Assert.That(Ui.Find<FrameworkElement>("PlanBar1").TransformToVisual(sheet).TransformPoint(new()).X,
                Is.EqualTo(barX - (sheet.ChartOffset - offset)).Within(1));
            Assert.That(Ui.Find<Microsoft.UI.Xaml.Shapes.Line>("PlanStatusLine").X1, Is.EqualTo(sheet.X(Today)).Within(1));
        });
        await SheetNativeInput.ScrollWheel(shift ? 120 : -120, horizontal: !shift, shift: shift);
        await Ui.Until(() => Math.Abs(sheet.ChartOffset - offset) < 1);
        await Ui.Run(() => Assert.That(vertical.VerticalOffset, Is.EqualTo(rowOffset)));
        Assert.That(session.Document.State, Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
    }

    [Test, Category("PlanSheetNative")]
    public async Task VerticalWheelOverGanttStillMovesRowsWithoutMovingDates()
    {
        ScrollViewer vertical = null!;
        FrameworkElement row = null!;
        double x = 0, offset = 0;
        await Ui.Run(() => {
            vertical = ScrollMeasurementViewport();
            row = sheet.Realized.Single(r => r.Identity == "I1");
            x = (sheet.SheetViewport + sheet.ChartViewport / 2) / row.ActualWidth;
            offset = sheet.ChartOffset;
        });
        SheetNativeInput.Move(await SheetNativeInput.PointFor(row, x));
        await SheetNativeInput.ScrollWheel(-120);
        await Ui.Until(() => vertical.VerticalOffset > 0);
        await Ui.Run(() => Assert.That(sheet.ChartOffset, Is.EqualTo(offset)));
    }

    [TestCase(false), TestCase(true), Category("PlanSheetNative")]
    public async Task HorizontalWheelAtTimelineEdgesNeverFallsThroughToRowScrolling(bool shift)
    {
        ScrollViewer horizontal = null!, vertical = null!;
        FrameworkElement row = null!;
        double x = 0;
        await Ui.Run(() => {
            horizontal = Ui.Find<ScrollViewer>("PlanGanttHorizontal");
            vertical = ScrollMeasurementViewport();
            row = sheet.Realized.Single(r => r.Identity == "I1");
            x = (sheet.SheetViewport + sheet.ChartViewport / 2) / row.ActualWidth;
            horizontal.ChangeView(0, null, null, true);
        });
        await Ui.Until(() => horizontal.HorizontalOffset == 0);
        SheetNativeInput.Move(await SheetNativeInput.PointFor(row, x));
        await SheetNativeInput.ScrollWheel(shift ? 120 : -120, horizontal: !shift, shift: shift);
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(horizontal.HorizontalOffset, Is.Zero);
            Assert.That(vertical.VerticalOffset, Is.Zero);
            horizontal.ChangeView(horizontal.ScrollableWidth, null, null, true);
        });
        await Ui.Until(() => Math.Abs(horizontal.HorizontalOffset - horizontal.ScrollableWidth) < 1);
        await SheetNativeInput.ScrollWheel(shift ? -120 : 120, horizontal: !shift, shift: shift);
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(horizontal.HorizontalOffset, Is.EqualTo(horizontal.ScrollableWidth).Within(1));
            Assert.That(vertical.VerticalOffset, Is.Zero);
        });
    }

    [TestCase(false), TestCase(true), Category("PlanSheetNative")]
    public async Task RapidHorizontalWheelEventsRetainEveryNotch(bool shift)
    {
        ScrollViewer horizontal = null!, vertical = null!;
        FrameworkElement row = null!;
        double x = 0, start = 0, step = 0, sheetOffset = 0;
        var trace = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var phase = "single-notch calibration";
        var burstUnits = 0;
        var burstDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Microsoft.UI.Xaml.Input.PointerEventHandler observe = (_, args) => {
            var wheel = args.GetCurrentPoint(sheet).Properties;
            Record($"wheel delta={wheel.MouseWheelDelta} horizontal={wheel.IsHorizontalMouseWheel} modifiers={args.KeyModifiers} handled={args.Handled} source={args.OriginalSource.GetType().Name}");
            if (phase == "eight-notch burst") {
                burstUnits += Math.Abs(wheel.MouseWheelDelta);
                if (burstUnits >= 8 * 120) burstDelivered.TrySetResult();
            }
        };
        EventHandler<ScrollViewerViewChangedEventArgs> viewChanged = (_, args) => Record($"view intermediate={args.IsIntermediate}");
        EventHandler<ScrollViewerViewChangingEventArgs> viewChanging = (_, args) => Record($"changing next={args.NextView.HorizontalOffset:F2} final={args.FinalView.HorizontalOffset:F2}");
        try {
            await Ui.Run(() => {
                horizontal = Ui.Find<ScrollViewer>("PlanGanttHorizontal");
                vertical = ScrollMeasurementViewport();
                row = sheet.Realized.Single(r => r.Identity == "I1");
                x = (sheet.SheetViewport + sheet.ChartViewport / 2) / row.ActualWidth;
                start = sheet.ChartOffset;
                sheetOffset = sheet.SheetOffset;
                Ui.Root.AddHandler(UIElement.PointerWheelChangedEvent, observe, true);
                horizontal.ViewChanged += viewChanged;
                horizontal.ViewChanging += viewChanging;
            });
            var pointer = await SheetNativeInput.PointFor(row, x);
            SheetNativeInput.Move(pointer);
            await SheetNativeInput.ScrollWheel(shift ? -120 : 120, horizontal: !shift, shift: shift);
            await Ui.Until(() => sheet.ChartOffset > start);
            await Ui.Run(() => {
                step = sheet.ChartOffset - start;
                Assert.That(horizontal.ScrollableWidth - start, Is.GreaterThan(step * 8));
                horizontal.ChangeView(start, null, null, true);
            });
            await Ui.Until(() => Math.Abs(sheet.ChartOffset - start) < 1);
            // A native pointer move separates the calibration gesture from this
            // batch: Windows can otherwise coalesce its prior delta into the burst.
            await SheetNativeInput.MoveAndWait(new(pointer.X + 2, pointer.Y));
            await SheetNativeInput.Rendered();
            phase = "eight-notch burst";
            if (shift) SheetNativeInput.Key(VirtualKey.Shift, true);
            try {
                for (var i = 0; i < 8; i++) SheetNativeInput.Wheel(shift ? -120 : 120, horizontal: !shift);
                // WinUI reads modifiers when the queued pointer event is routed.
                // Keep Shift held through delivery, without spacing the wheel inputs.
                await burstDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { if (shift) SheetNativeInput.Key(VirtualKey.Shift, false); }
            Assert.That(burstUnits, Is.EqualTo(8 * 120), "The independent native gesture must deliver exactly eight notches.");
            await Ui.Until(() => Math.Abs(sheet.ChartOffset - (start + step * 8)) < 1);
            // The scroll strip and app navigation may replace the wheel's last target.
            // The next gesture must continue from that observed viewport.
            phase = "external scroll then notch";
            await Ui.Run(() => horizontal.ChangeView(start + step * 2, null, null, true));
            await Ui.Until(() => Math.Abs(sheet.ChartOffset - (start + step * 2)) < 1);
            await SheetNativeInput.MoveAndWait(new(pointer.X + 4, pointer.Y));
            await SheetNativeInput.Rendered();
            await SheetNativeInput.ScrollWheel(shift ? -120 : 120, horizontal: !shift, shift: shift);
            await Ui.Until(() => Math.Abs(sheet.ChartOffset - (start + step * 3)) < 1);
            phase = "boundary reversal";
            double nearEnd = 0;
            await Ui.Run(() => {
                nearEnd = horizontal.ScrollableWidth - step;
                horizontal.ChangeView(nearEnd, null, null, true);
            });
            await Ui.Until(() => Math.Abs(sheet.ChartOffset - nearEnd) < 1);
            await SheetNativeInput.MoveAndWait(new(pointer.X + 6, pointer.Y));
            await SheetNativeInput.Rendered();
            var outwardDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reverseDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var outwardUnits = 0;
            var reverseUnits = 0;
            Microsoft.UI.Xaml.Input.PointerEventHandler observeBoundary = (_, args) => {
                var delta = args.GetCurrentPoint(sheet).Properties.MouseWheelDelta;
                if (Math.Sign(delta) == (shift ? -1 : 1)) {
                    outwardUnits += Math.Abs(delta);
                    if (outwardUnits >= 4 * 120) outwardDelivered.TrySetResult();
                }
                else {
                    reverseUnits += Math.Abs(delta);
                    if (reverseUnits >= 120) reverseDelivered.TrySetResult();
                }
            };
            await Ui.Run(() => Ui.Root.AddHandler(UIElement.PointerWheelChangedEvent, observeBoundary, true));
            try {
                if (shift) SheetNativeInput.Key(VirtualKey.Shift, true);
                try {
                    for (var i = 0; i < 4; i++) SheetNativeInput.Wheel(shift ? -120 : 120, horizontal: !shift);
                    // Establish the direction boundary in the native route; do not
                    // wait for a frame before reversing the pending scroll target.
                    await outwardDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    SheetNativeInput.Wheel(shift ? 120 : -120, horizontal: !shift);
                    await reverseDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { if (shift) SheetNativeInput.Key(VirtualKey.Shift, false); }
            }
            finally { await Ui.Run(() => Ui.Root.RemoveHandler(UIElement.PointerWheelChangedEvent, observeBoundary)); }
            Assert.That(outwardUnits, Is.EqualTo(4 * 120));
            Assert.That(reverseUnits, Is.EqualTo(120));
            await SheetNativeInput.Rendered();
            await Ui.Until(() => Math.Abs(sheet.ChartOffset - nearEnd) < 1);
            await Ui.Run(() => {
                Assert.That(ScrollMeasurementViewport().VerticalOffset, Is.Zero);
                Assert.That(sheet.SheetOffset, Is.EqualTo(sheetOffset));
            });
        }
        finally {
            await Ui.Run(() => {
                Ui.Root.RemoveHandler(UIElement.PointerWheelChangedEvent, observe);
                if (horizontal is not null) {
                    horizontal.ViewChanged -= viewChanged;
                    horizontal.ViewChanging -= viewChanging;
                }
                Record($"final start={start:F2} step={step:F2} expectedBurst={start + step * 8:F2} expectedBurstUnits=960 deliveredBurstUnits={burstUnits}");
            });
            TestContext.Out.WriteLine(string.Join(Environment.NewLine, trace));
        }

        void Record(string detail)
        {
            if (trace.Count < 100) trace.Add($"{clock.Elapsed.TotalMilliseconds:F1}ms {phase}: {detail} chart={sheet.ChartOffset:F2} rows={vertical?.VerticalOffset:F2}");
        }
    }

    [TestCase(false), TestCase(true), Category("PlanSheetNative")]
    public async Task HorizontalWheelBelowFilteredRowsStillMovesDates(bool shift)
    {
        double x = 0, start = 0;
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "Task 100");
        await Ui.Until(() => sheet.List.Items.Count == 2);
        await Ui.Run(() => {
            x = (sheet.SheetViewport + sheet.ChartViewport / 2) / sheet.List.ActualWidth;
            start = sheet.ChartOffset;
        });
        SheetNativeInput.Move(await SheetNativeInput.PointFor(sheet.List, x, .7));
        await SheetNativeInput.ScrollWheel(shift ? -120 : 120, horizontal: !shift, shift: shift);
        await Ui.Until(() => sheet.ChartOffset > start);
        await Ui.Run(() => Assert.That(ScrollMeasurementViewport().VerticalOffset, Is.Zero));
    }

    [Test]
    public async Task OverflowAutomationExpansionAndToggleUseTheOwnedMenu()
    {
        IExpandCollapseProvider expand = null!;
        IToggleProvider toggle = null!;
        await Ui.Run(() => {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<CommandBar>("PlanSheetCommands"));
            expand = (IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse);
            toggle = (IToggleProvider)peer.GetPattern(PatternInterface.Toggle);
            Assert.That(expand.ExpandCollapseState, Is.EqualTo(ExpandCollapseState.Collapsed));
            expand.Expand();
        });
        await Ui.Ready<MenuFlyoutItem>("PlanSheetCopy");
        await Ui.Run(() => {
            Assert.That(expand.ExpandCollapseState, Is.EqualTo(ExpandCollapseState.Expanded));
            Assert.That(toggle.ToggleState, Is.EqualTo(ToggleState.On));
            Assert.That(Ui.Find<CommandBar>("PlanSheetCommands").IsOpen, Is.False);
            expand.Collapse();
        });
        await Ui.Until(() => Ui.Popup<MenuFlyoutItem>("PlanSheetCopy") is null);
        await Ui.Run(() => toggle.Toggle());
        await Ui.Ready<MenuFlyoutItem>("PlanSheetCopy");
        await Ui.Run(() => {
            Assert.That(Ui.Find<AppBarButton>("PlanSheetMore").Flyout.AreOpenCloseAnimationsEnabled, Is.False);
            Assert.That(Ui.Find<CommandBar>("PlanSheetCommands").IsOpen, Is.False);
            toggle.Toggle();
        });
        await Ui.Until(() => Ui.Popup<MenuFlyoutItem>("PlanSheetCopy") is null);
    }

    [Test, Category("PlanSheetNative")]
    public async Task OverflowKeyboardOpeningAndEscapeUseInstantMenuAndRestoreFocus()
    {
        await SheetNativeInput.ActivateWindow();
        await Ui.Run(() => Assert.That(Ui.Find<AppBarButton>("PlanSheetMore").Focus(FocusState.Keyboard), Is.True));
        SheetNativeInput.Key(VirtualKey.Menu, true);
        try { SheetNativeInput.Key(VirtualKey.Down, true); SheetNativeInput.Key(VirtualKey.Down, false); }
        finally { SheetNativeInput.Key(VirtualKey.Menu, false); }
        await Ui.Ready<MenuFlyoutItem>("PlanSheetCopy");
        await Ui.Run(() => {
            Assert.That(Ui.Find<AppBarButton>("PlanSheetMore").Flyout.AreOpenCloseAnimationsEnabled, Is.False);
            Assert.That(Ui.Find<CommandBar>("PlanSheetCommands").IsOpen, Is.False);
            Assert.That(Ui.Find<MenuFlyoutItem>("PlanSheetCopy").KeyboardAcceleratorTextOverride, Is.EqualTo("Ctrl+C"));
        });
        SheetNativeInput.Key(VirtualKey.Escape, true); SheetNativeInput.Key(VirtualKey.Escape, false);
        await Ui.Until(() => Ui.Popup<MenuFlyoutItem>("PlanSheetCopy") is null);
        await Ui.Run(() => Assert.That(Ui.Find<AppBarButton>("PlanSheetMore").FocusState, Is.Not.EqualTo(FocusState.Unfocused)));
    }

    [Test]
    public async Task OverflowReflectsCommandAvailabilityAndClosesForRemoteWork()
    {
        await Ui.Run(() => sheetHost.Width = 760);
        await Ui.Until(() => Ui.CommandButton("PlanSheetCollapseAll").IsInOverflow);
        await Ui.Run(() => Ui.Click("PlanSheetMore"));
        await Ui.Ready<MenuFlyoutItem>("PlanSheetCollapseAll");
        await Ui.Run(() => {
            Assert.That(Ui.Find<MenuFlyoutItem>("PlanSheetCollapseAll").IsEnabled, Is.False);
            Assert.That(Ui.Find<MenuFlyoutItem>("PlanSheetGoToDate").IsEnabled, Is.False);
            sheet.SetRemoteBusy(true);
        });
        await Ui.Until(() => Ui.Popup<MenuFlyoutItem>("PlanSheetCopy") is null);
        await Ui.Run(() => {
            Assert.That(Ui.Find<AppBarButton>("PlanSheetMore").IsEnabled, Is.False);
            sheet.SetRemoteBusy(false);
        });
        await Select(1, PlanField.Title);
        await Ui.Run(() => Ui.Click("PlanSheetMore"));
        await Ui.Ready<MenuFlyoutItem>("PlanSheetGoToDate");
        await Ui.Run(() => Assert.That(Ui.Find<MenuFlyoutItem>("PlanSheetGoToDate").IsEnabled, Is.True));
    }

    [Test]
    public async Task OverflowPredecessorCommandOpensItsSearchWithKeyboardFocus()
    {
        await Ui.Run(() => sheetHost.Width = 760);
        await Ui.Until(() => Ui.CommandButton("PlanSheetPredecessorAdd").IsInOverflow);
        await Select(2, PlanField.Title);
        await Ui.ClickCommand("PlanSheetPredecessorAdd");
        await Ui.Ready<TextBox>("PlanPredecessorSearch");
        await Ui.Run(() => {
            Assert.That(Ui.Popup<MenuFlyoutItem>("PlanSheetCopy"), Is.Null);
            Assert.That(Ui.Find<TextBox>("PlanPredecessorSearch").FocusState, Is.Not.EqualTo(FocusState.Unfocused));
            Ui.Click("PlanPredecessorCancel");
        });
    }
}
