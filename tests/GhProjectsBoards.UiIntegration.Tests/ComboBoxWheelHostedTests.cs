using System.Text.Json;
using GhProjectsBoards.App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class PlanningHostedTests
{
    [Test, Category("ComboBoxWheel")]
    public async Task ClosedMappingWheelScrollsTheSettingsPageWithoutChangingMappingAndNativeKeysStillSelect()
    {
        var before = JsonSerializer.Serialize(session.Workspace.Snapshot());
        await Ui.ClickCommand("GridPlanningSettings"); await Ui.Ready<ComboBox>("PlanField-Remaining");
        ComboBox mapping = null!;
        await Ui.Run(() => mapping = Ui.Find<ComboBox>("PlanField-Remaining"));
        await ComboBoxWheelInput.ScrollParentWithoutSelection(mapping, focused: true);
        await ComboBoxWheelInput.ScrollParentWithoutSelection(mapping, focused: false);
        await Ui.Run(() => {
            Assert.That(((ComboBoxItem)mapping.SelectedItem).Tag, Is.EqualTo("F-Remaining"));
            Assert.That(JsonSerializer.Serialize(session.Workspace.Snapshot()), Is.EqualTo(before));
        });
        await ComboBoxWheelInput.Focus(mapping);
        await SheetNativeInput.Press(VirtualKey.Down);
        await Ui.Until(() => (string)((ComboBoxItem)mapping.SelectedItem).Tag == "F-Actual");
        await SheetNativeInput.Press(VirtualKey.Up);
        await Ui.Until(() => (string)((ComboBoxItem)mapping.SelectedItem).Tag == "F-Remaining");
        await SheetNativeInput.Press(VirtualKey.F4); await Ui.Until(() => mapping.IsDropDownOpen);
        await SheetNativeInput.Press(VirtualKey.Down); await SheetNativeInput.Press(VirtualKey.Enter);
        await Ui.Until(() => !mapping.IsDropDownOpen && (string)((ComboBoxItem)mapping.SelectedItem).Tag == "F-Actual");
        await SheetNativeInput.Press(VirtualKey.Down, VirtualKey.Menu); await Ui.Until(() => mapping.IsDropDownOpen);
        await SheetNativeInput.Press(VirtualKey.Up); await SheetNativeInput.Press(VirtualKey.Enter);
        await Ui.Until(() => !mapping.IsDropDownOpen && (string)((ComboBoxItem)mapping.SelectedItem).Tag == "F-Remaining");
        await Ui.Run(() => Ui.Click("PlanSettingsCancel")); await Ui.Until(() => !grid.PlanningSettingsOpen);
        await Ui.Run(() => Assert.That(JsonSerializer.Serialize(session.Workspace.Snapshot()), Is.EqualTo(before),
            "Mapping candidates and scrolling cannot commit planning or create a journal entry."));
    }

    [Test, Category("ComboBoxWheel")]
    public async Task DailyClosedWheelPreservesCandidatesAtScrollBoundaryWhileOpenWorkerListStillScrolls()
    {
        await PrepareDailyProgress([]);
        var before = JsonSerializer.Serialize(session.Workspace.Snapshot());
        Windows.Graphics.SizeInt32 originalSize = default;
        await Ui.Run(() => { originalSize = Ui.Window.AppWindow.Size; Ui.Window.AppWindow.Resize(new(1080, 760)); });
        try
        {
        await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
        ComboBox worker = null!, option = null!;
        await Ui.Run(() => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            worker = Ui.Find<ComboBox>("DailyActualWorker", dialog);
        });
        await ComboBoxWheelInput.ScrollParentWithoutSelection(worker, focused: true);
        await ComboBoxWheelInput.Focus(worker);
        await SheetNativeInput.Press(VirtualKey.F4); await Ui.Until(() => worker.IsDropDownOpen);
        await SheetNativeInput.Rendered();
        ScrollViewer popupScroll = null!; double oldPopupOffset = 0;
        await Ui.Run(() => {
            var item = worker.Items.Cast<object>().Select(worker.ContainerFromItem).OfType<ComboBoxItem>().First(i => i.IsLoaded);
            var scroll = ComboBoxWheelInput.ParentScroll(item);
            Console.WriteLine($"Open worker list viewport units={scroll.ViewportHeight}, rendered height={scroll.ActualHeight}, item height={item.ActualHeight}");
        });
        await Ui.Until(() => {
            var items = worker.Items.Cast<object>().Select(worker.ContainerFromItem).OfType<ComboBoxItem>().Where(i => i.IsLoaded).ToArray();
            if (items.Length == 0) return false;
            popupScroll = ComboBoxWheelInput.ParentScroll(items[0]);
            return popupScroll.ViewportHeight > 0 && items.Any(item => ComboBoxWheelInput.FullyVisible(item, popupScroll));
        });
        await Ui.Run(() => {
            var items = worker.Items.Cast<object>().Select(worker.ContainerFromItem).OfType<ComboBoxItem>().Where(i => i.IsLoaded).ToArray();
            popupScroll = ComboBoxWheelInput.ParentScroll(items.First());
            Assert.That(popupScroll.ScrollableHeight, Is.GreaterThan(0));
            Assert.That(items.Any(item => ComboBoxWheelInput.FullyVisible(item, popupScroll)), Is.True);
            oldPopupOffset = popupScroll.VerticalOffset;
        });
        await ComboBoxWheelInput.WheelOpenPopup(worker, popupScroll, -120);
        await Ui.Until(() => popupScroll.VerticalOffset > oldPopupOffset);
        await Ui.Run(() => Assert.That(worker.IsDropDownOpen, Is.True, "Wheel retains the native open list."));
        await SheetNativeInput.Press(VirtualKey.Escape); await Ui.Until(() => !worker.IsDropDownOpen);
        await Ui.Run(() => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            SelectDailyOption("DailyProjectField", "P1-status", dialog);
            option = Ui.Find<ComboBox>("DailyProjectOption", dialog);
        });
        await ComboBoxWheelInput.Focus(option);
        ScrollViewer parent = null!; object original = null!;
        await Ui.Run(() => {
            parent = ComboBoxWheelInput.ParentScroll(option); original = option.SelectedItem;
            parent.ChangeView(null, parent.ScrollableHeight, null, true);
        });
        await Ui.Until(() => Math.Abs(parent.VerticalOffset - parent.ScrollableHeight) < 1 && ComboBoxWheelInput.FullyVisible(option, parent));
        await ComboBoxWheelInput.Wheel(option, -120); await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(option.SelectedItem, Is.SameAs(original), "At the parent boundary the closed field still cannot change.");
            Assert.That(((ComboBoxItem)option.SelectedItem).Tag, Is.EqualTo("todo"));
            Assert.That(Ui.Find<TextBlock>("DailyProjectFieldHint", Ui.Dialog("DailyProgressDialog")!).Text, Does.Contain("確定済み").And.Not.Contain("変更候補"));
            Assert.That(JsonSerializer.Serialize(session.Workspace.Snapshot()), Is.EqualTo(before));
        });
        await CloseDailyAndWaitForSave("CloseButton");
        await Ui.Run(() => Assert.That(JsonSerializer.Serialize(session.Workspace.Snapshot()), Is.EqualTo(before),
            "Wheel, picker browsing and Cancel preserve all local work, Undo and remote journal."));
        }
        finally { await Ui.Run(() => Ui.Window.AppWindow.Resize(originalSize)); }
    }
}

public sealed partial class HostedTests
{
    [Test, Category("ComboBoxWheel")]
    public async Task ClosedNavigationRepositoryWheelDoesNotSwitchFilterOrSelectedProject()
    {
        await OpenNavigation(SplitViewDisplayMode.Inline);
        var selected = Workspace.Selected!.Snapshot.Id;
        var before = JsonSerializer.Serialize(Work.Snapshot());
        ComboBox filter = null!; object original = null!;
        await Ui.Run(() => {
            filter = Ui.Find<ComboBox>("NavigationRepositoryFilter"); original = filter.SelectedItem;
            Assert.That(filter.Items.Count, Is.GreaterThan(1));
        });
        await ComboBoxWheelInput.Focus(filter);
        await ComboBoxWheelInput.Wheel(filter, -120); await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(filter.SelectedItem, Is.SameAs(original));
            Assert.That(Workspace.Selected!.Snapshot.Id, Is.EqualTo(selected));
            Assert.That(JsonSerializer.Serialize(Work.Snapshot()), Is.EqualTo(before));
        });
    }
}

internal static class ComboBoxWheelInput
{
    internal static ScrollViewer ParentScroll(DependencyObject control)
    {
        for (var ancestor = VisualTreeHelper.GetParent(control); ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
            if (ancestor is ScrollViewer scroll) return scroll;
        throw new AssertionException("Expected a real containing ScrollViewer.");
    }
    internal static bool FullyVisible(FrameworkElement control, ScrollViewer scroll)
    {
        var viewport = Viewport(scroll);
        var bounds = control.TransformToVisual(viewport).TransformBounds(new(0, 0, control.ActualWidth, control.ActualHeight));
        return viewport.ActualHeight > 0 && bounds.Y >= 0 && bounds.Bottom <= viewport.ActualHeight + 1;
    }
    private static ScrollContentPresenter Viewport(ScrollViewer scroll) => Ui.Tree(scroll).OfType<ScrollContentPresenter>()
        .First(presenter => ReferenceEquals(ParentScroll(presenter), scroll));
    internal static async Task Focus(ComboBox box)
    {
        await SheetNativeInput.ActivateWindow();
        await Ui.Run(() => { box.StartBringIntoView(new() { AnimationDesired = false, VerticalAlignmentRatio = .5 });
            FrameworkElementAutomationPeer.CreatePeerForElement(box).SetFocus(); });
        await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(box.XamlRoot), box));
        await SheetNativeInput.Rendered();
    }
    internal static async Task Wheel(FrameworkElement control, int delta)
    {
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PointerEventHandler observed = (_, _) => delivered.TrySetResult();
        var point = await SheetNativeInput.PointFor(control, .5, .8);
        await Ui.Run(() => control.AddHandler(UIElement.PointerWheelChangedEvent, observed, true));
        try { SheetNativeInput.Move(point); SheetNativeInput.Wheel(delta); await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await Ui.Run(() => control.RemoveHandler(UIElement.PointerWheelChangedEvent, observed)); }
        await SheetNativeInput.Rendered();
    }
    internal static async Task WheelOpenPopup(ComboBox anchor, ScrollViewer scroll, int delta)
    {
        // The ComboBox stays in the main visual root. A popup item's transform
        // must not be combined with that root's HWND origin. Prove that the
        // native popup covers this anchor point before delivering its wheel.
        var point = await SheetNativeInput.PointFor(anchor);
        ScrollContentPresenter viewport = null!;
        await Ui.Run(() => {
            Assert.That(anchor.IsDropDownOpen && scroll.IsLoaded, Is.True);
            viewport = Viewport(scroll);
            Assert.That(viewport.ActualWidth > 0 && viewport.ActualHeight > 0, Is.True);
        });
        var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wheeled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool InsidePopup(PointerRoutedEventArgs args)
        {
            var source = args.OriginalSource as DependencyObject;
            while (source is not null && !ReferenceEquals(source, scroll)) source = VisualTreeHelper.GetParent(source);
            if (source is null || !anchor.IsDropDownOpen) return false;
            var position = args.GetCurrentPoint(viewport).Position;
            return position.X >= 0 && position.X < viewport.ActualWidth && position.Y >= 0 && position.Y < viewport.ActualHeight;
        }
        PointerEventHandler pointerMoved = (_, args) => {
            if (InsidePopup(args)) moved.TrySetResult();
        };
        PointerEventHandler pointerWheeled = (_, args) => {
            if (!InsidePopup(args)) return;
            var current = args.GetCurrentPoint(viewport);
            if (current.Properties.MouseWheelDelta != delta) return;
            Console.WriteLine($"Native worker popup wheel: screen={point}, viewport position={current.Position}, rendered viewport={viewport.ActualWidth}x{viewport.ActualHeight}, delta={current.Properties.MouseWheelDelta}, source={args.OriginalSource.GetType().Name}");
            wheeled.TrySetResult();
        };
        await Ui.Run(() => {
            scroll.AddHandler(UIElement.PointerMovedEvent, pointerMoved, true);
            scroll.AddHandler(UIElement.PointerWheelChangedEvent, pointerWheeled, true);
        });
        try
        {
            SheetNativeInput.Move(point);
            try { await moved.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { throw new AssertionException("Native pointer did not reach the open worker popup's rendered viewport at its ComboBox anchor."); }
            SheetNativeInput.Wheel(delta);
            try { await wheeled.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { throw new AssertionException("The open worker popup did not receive the requested native wheel delta inside its rendered viewport."); }
        }
        finally
        {
            await Ui.Run(() => {
                scroll.RemoveHandler(UIElement.PointerMovedEvent, pointerMoved);
                scroll.RemoveHandler(UIElement.PointerWheelChangedEvent, pointerWheeled);
            });
        }
        await SheetNativeInput.Rendered();
    }
    internal static async Task ScrollParentWithoutSelection(ComboBox box, bool focused)
    {
        await Focus(box);
        ScrollViewer scroll = null!; object original = null!; double offset = 0; int delta = 0;
        await Ui.Run(() => scroll = ParentScroll(box));
        await Ui.Until(() => FullyVisible(box, scroll));
        await Ui.Run(() => {
            if (!focused) Assert.That(scroll.Focus(FocusState.Programmatic), Is.True);
            Assert.That(ReferenceEquals(FocusManager.GetFocusedElement(box.XamlRoot), box), Is.EqualTo(focused));
            Assert.That(box.IsDropDownOpen, Is.False);
            Assert.That(scroll.ScrollableHeight, Is.GreaterThan(0));
            original = box.SelectedItem; offset = scroll.VerticalOffset;
            delta = scroll.ScrollableHeight - offset > 2 ? -120 : 120;
        });
        await Wheel(box, delta);
        await Ui.Run(() => Assert.That(box.SelectedItem, Is.SameAs(original), "The closed selected ID must survive native wheel input."));
        await Ui.Until(() => Math.Abs(scroll.VerticalOffset - offset) > 1);
    }
}
