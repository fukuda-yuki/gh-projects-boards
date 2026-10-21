using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanSheetHostedTests
{
    [Test, Category("CommandAvailability")]
    public async Task FilterExplainsDisabledFoldingInToolbarAndOverflowAndClearsTheReason()
    {
        var parent = new PlanRow("I1", "Requirement", "acme/repo");
        var child = new PlanRow("I2", "Task", "acme/repo") { Parent = "I1" };
        await MountPresentation([parent, child], [parent, child]);
        await Ui.Run(() => sheetHost.Width = 760);
        await Ui.Until(() => Ui.CommandButton("PlanSheetCollapseAll").IsInOverflow
            && Ui.CommandButton("PlanSheetExpandAll").IsInOverflow);
        foreach (var filtered in new[] { true, false }) {
            await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = filtered ? "Task" : "");
            await Ui.Until(() => sheet.RowIds.Count == (filtered ? 2 : 3));
            await Ui.Run(() => {
                foreach (var id in new[] { "PlanSheetCollapseAll", "PlanSheetExpandAll" })
                    AssertFolding(Ui.CommandButton(id), filtered);
                Ui.Click("PlanSheetMore");
            });
            await Ui.Ready<MenuFlyoutItem>("PlanSheetCollapseAll");
            await Ui.Run(() => {
                foreach (var id in new[] { "PlanSheetCollapseAll", "PlanSheetExpandAll" }) {
                    var item = Ui.Find<MenuFlyoutItem>(id);
                    AssertFolding(item, filtered);
                    if (filtered) Assert.That(item.Focus(FocusState.Keyboard), Is.True);
                }
                Ui.Find<AppBarButton>("PlanSheetMore").Flyout.Hide();
            });
            await Ui.Idle();
        }
        await Ui.Run(() => {
            sheet.SetRemoteBusy(true);
            Assert.That(Ui.Find<AppBarButton>("PlanSheetMore").IsEnabled, Is.False);
            foreach (var id in new[] { "PlanSheetCollapseAll", "PlanSheetExpandAll" }) {
                var button = Ui.CommandButton(id);
                Assert.That(AutomationProperties.GetHelpText(button), Is.Empty);
            }
            sheet.SetRemoteBusy(false);
        });

        static void AssertFolding(Control command, bool filtered)
        {
            Assert.That(command.IsEnabled, Is.EqualTo(!filtered));
            Assert.That(AutomationProperties.GetHelpText(command), Is.EqualTo(filtered ? "絞り込み中は折りたためません" : ""));
            Assert.That(ToolTipService.GetToolTip(command), Is.EqualTo(filtered ? "絞り込み中は折りたためません" : AutomationProperties.GetName(command)));
            if (filtered) Assert.That(command.AllowFocusWhenDisabled, Is.True);
        }
    }

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
        var burstEvents = 0;
        var burstSteps = Enumerable.Range(0, 8).Select(_ =>
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        Microsoft.UI.Xaml.Input.PointerEventHandler observe = (_, args) => {
            var wheel = args.GetCurrentPoint(sheet).Properties;
            Record($"wheel delta={wheel.MouseWheelDelta} horizontal={wheel.IsHorizontalMouseWheel} modifiers={args.KeyModifiers} handled={args.Handled} source={args.OriginalSource.GetType().Name}");
            if (phase == "eight-notch burst") {
                burstUnits += Math.Abs(wheel.MouseWheelDelta);
                if (burstEvents < burstSteps.Length) burstSteps[burstEvents++].TrySetResult(wheel.MouseWheelDelta);
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
            SheetNativeInput.Move(await SheetNativeInput.PointFor(row, x));
            await SheetNativeInput.ScrollWheel(shift ? -120 : 120, horizontal: !shift, shift: shift);
            await Ui.Until(() => sheet.ChartOffset > start);
            await Ui.Run(() => {
                step = sheet.ChartOffset - start;
                Assert.That(horizontal.ScrollableWidth - start, Is.GreaterThan(step * 8));
                horizontal.ChangeView(start, null, null, true);
            });
            await Ui.Until(() => Math.Abs(sheet.ChartOffset - start) < 1);
            phase = "eight-notch burst";
            if (shift) SheetNativeInput.Key(VirtualKey.Shift, true);
            try {
                // Acknowledge each native event, without a frame wait or UI dispatch
                // between inputs. Unacknowledged SendInput batches can be coalesced
                // by the desktop into a different delta than the injected total.
                for (var i = 0; i < burstSteps.Length; i++) {
                    var delta = shift ? -120 : 120;
                    SheetNativeInput.Wheel(delta, horizontal: !shift);
                    Assert.That(await burstSteps[i].Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(delta));
                }
                // Keep Shift held until the queued pointer event has been routed.
            }
            finally { if (shift) SheetNativeInput.Key(VirtualKey.Shift, false); }
            Assert.That(burstUnits, Is.EqualTo(8 * 120), "The independent native gesture must deliver exactly eight notches.");
            await Ui.Until(() => Math.Abs(sheet.ChartOffset - (start + step * 8)) < 1);
            // The scroll strip and app navigation may replace the wheel's last target.
            // The next gesture must continue from that observed viewport.
            phase = "external scroll then notch";
            await Ui.Run(() => horizontal.ChangeView(start + step * 2, null, null, true));
            await Ui.Until(() => Math.Abs(sheet.ChartOffset - (start + step * 2)) < 1);
            await SheetNativeInput.ScrollWheel(shift ? -120 : 120, horizontal: !shift, shift: shift);
            await Ui.Until(() => Math.Abs(sheet.ChartOffset - (start + step * 3)) < 1);
            phase = "boundary reversal";
            double nearEnd = 0;
            await Ui.Run(() => {
                nearEnd = horizontal.ScrollableWidth - step;
                horizontal.ChangeView(nearEnd, null, null, true);
            });
            await Ui.Until(() => Math.Abs(sheet.ChartOffset - nearEnd) < 1);
            var outwardSteps = Enumerable.Range(0, 4).Select(_ =>
                new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var reverseDelivered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var outwardEvents = 0;
            var outwardUnits = 0;
            var reverseUnits = 0;
            Microsoft.UI.Xaml.Input.PointerEventHandler observeBoundary = (_, args) => {
                var delta = args.GetCurrentPoint(sheet).Properties.MouseWheelDelta;
                if (Math.Sign(delta) == (shift ? -1 : 1)) {
                    outwardUnits += Math.Abs(delta);
                    if (outwardEvents < outwardSteps.Length) outwardSteps[outwardEvents++].TrySetResult(delta);
                }
                else {
                    reverseUnits += Math.Abs(delta);
                    reverseDelivered.TrySetResult(delta);
                }
            };
            await Ui.Run(() => Ui.Root.AddHandler(UIElement.PointerWheelChangedEvent, observeBoundary, true));
            try {
                if (shift) SheetNativeInput.Key(VirtualKey.Shift, true);
                try {
                    for (var i = 0; i < outwardSteps.Length; i++) {
                        var delta = shift ? -120 : 120;
                        SheetNativeInput.Wheel(delta, horizontal: !shift);
                        Assert.That(await outwardSteps[i].Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(delta));
                    }
                    // Reverse after routed delivery, without waiting for a frame.
                    var reverse = shift ? 120 : -120;
                    SheetNativeInput.Wheel(reverse, horizontal: !shift);
                    Assert.That(await reverseDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(reverse));
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
        await Ui.Until(() => Ui.Popup<MenuFlyoutItem>("PlanSheetCopy") is null
            && expand.ExpandCollapseState == ExpandCollapseState.Collapsed);
        await Ui.Run(() => toggle.Toggle());
        await Ui.Ready<MenuFlyoutItem>("PlanSheetCopy");
        await Ui.Run(() => {
            Assert.That(Ui.Find<AppBarButton>("PlanSheetMore").Flyout.AreOpenCloseAnimationsEnabled, Is.False);
            Assert.That(Ui.Find<CommandBar>("PlanSheetCommands").IsOpen, Is.False);
            toggle.Toggle();
        });
        await Ui.Until(() => Ui.Popup<MenuFlyoutItem>("PlanSheetCopy") is null
            && expand.ExpandCollapseState == ExpandCollapseState.Collapsed);
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

    [Test, Category("PlanSheetNative")]
    public async Task PageDownAndPageUpMoveTheSelectionOnePageAndKeepItsPlaceOnScreen()
    {
        // #256: like Excel, a page moves the selection by the rows the viewport shows and scrolls the rows by
        // the same amount; the native list moved focus to the last visible row and rebound three pages.
        await SelectFocusedCell(3, PlanField.Remaining);
        var scroll = ScrollMeasurementViewport(); var page = 0; var top = 0d;
        await Ui.Run(() => { page = (int)(scroll.ViewportHeight / sheet.RowHeight); top = scroll.VerticalOffset; });
        Assert.That(page, Is.GreaterThan(2));
        var target = 3 + page;
        await SheetNativeInput.Press(VirtualKey.PageDown);
        await Ui.Until(() => SelectProvider($"PlanCell{target}_{PlanField.Remaining}").IsSelected
            && Ui.Find<Control>($"PlanCell{target}_{PlanField.Remaining}").FocusState != FocusState.Unfocused);
        await Ui.Run(() => {
            Assert.That(scroll.VerticalOffset - top, Is.EqualTo(page * sheet.RowHeight).Within(1));
            Assert.That(SelectProvider($"PlanCell3_{PlanField.Remaining}").IsSelected, Is.False);
        });
        await SheetNativeInput.Press(VirtualKey.PageDown, VirtualKey.Shift);
        await Ui.Until(() => SelectProvider($"PlanCell{target + page}_{PlanField.Remaining}").IsSelected);
        await Ui.Run(() => Assert.That(SelectProvider($"PlanCell{target}_{PlanField.Remaining}").IsSelected, Is.True, "Shift+PageDown extends the selection."));
        await SheetNativeInput.Press(VirtualKey.PageUp);
        await SheetNativeInput.Press(VirtualKey.PageUp);
        await Ui.Until(() => SelectProvider($"PlanCell3_{PlanField.Remaining}").IsSelected
            && Ui.Find<Control>($"PlanCell3_{PlanField.Remaining}").FocusState != FocusState.Unfocused);
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(scroll.VerticalOffset, Is.EqualTo(top).Within(1));
            Assert.That(SelectProvider($"PlanCell{target}_{PlanField.Remaining}").IsSelected, Is.False);
        });
    }

    [Test, Category("PlanSheetNative")]
    public async Task OverflowColumnCommandHasOneNavigationEntryAndNoToggleFlyout()
    {
        await Select(2, PlanField.Remaining);
        await SheetNativeInput.Click("PlanSheetMore");
        await Ui.Ready<MenuFlyoutItem>("PlanSheetColumns");
        await Ui.Run(() => {
            Assert.That(Ui.Popup<MenuFlyoutItem>("PlanSheetColumns")!.IsEnabled,Is.True);
            Assert.That(Ui.CommandButton("PlanSheetColumns").Flyout,Is.Null);
        });
        await SheetNativeInput.Press(VirtualKey.Escape);
    }

    [Test, Category("PlanSheetNative")]
    public async Task OverflowKeyboardCopyUsesTheSelectedCellAndRestoresFocus()
    {
        clipboard = new("not copied", null);
        var undo = session.UndoCount;
        await Select(2, PlanField.Remaining);
        await SheetNativeInput.ActivateWindow();
        await Ui.Run(() => Assert.That(Ui.Find<AppBarButton>("PlanSheetMore").Focus(FocusState.Keyboard), Is.True));
        SheetNativeInput.Key(VirtualKey.Down, true); SheetNativeInput.Key(VirtualKey.Down, false);
        await Ui.Ready<MenuFlyoutItem>("PlanSheetCopy");
        await Ui.Run(() => Assert.That(Ui.Find<MenuFlyoutItem>("PlanSheetCopy").Focus(FocusState.Keyboard), Is.True));
        SheetNativeInput.Key(VirtualKey.Enter, true); SheetNativeInput.Key(VirtualKey.Enter, false);
        // Observe the command's public effect, independent of ClickCommand's
        // automation invocation and native menu-close synchronization.
        await Ui.Until(() => clipboard.Text == "8");
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Popup<MenuFlyoutItem>("PlanSheetCopy"), Is.Null);
            Assert.That(SelectProvider("PlanCell2_Remaining").IsSelected, Is.True);
            Assert.That(Ui.Find<AppBarButton>("PlanSheetMore").FocusState, Is.Not.EqualTo(FocusState.Unfocused));
            Assert.That(session.UndoCount, Is.EqualTo(undo));
        });
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
        });
        // Popup disappearance precedes the native Closed event that resets the
        // public expand/collapse state during same-flyout teardown.
        await Ui.Until(() => ((IExpandCollapseProvider)FrameworkElementAutomationPeer.CreatePeerForElement(
            Ui.Find<CommandBar>("PlanSheetCommands")).GetPattern(PatternInterface.ExpandCollapse)).ExpandCollapseState
            == ExpandCollapseState.Collapsed);
        await Ui.Run(() => sheet.SetRemoteBusy(false));
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
        await Ui.Until(() => Ui.Find<TextBox>("PlanPredecessorSearch").FocusState != FocusState.Unfocused);
        await Ui.Run(() => {
            Assert.That(Ui.Popup<MenuFlyoutItem>("PlanSheetCopy"), Is.Null);
            Assert.That(Ui.Find<TextBox>("PlanPredecessorSearch").FocusState, Is.Not.EqualTo(FocusState.Unfocused));
            Ui.Click("PlanPredecessorCancel");
        });
    }
}
