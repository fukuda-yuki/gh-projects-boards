using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using NUnit.Framework;
using Windows.Foundation;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

// Native input into the existing bounded WinUI host. No handlers or product commands
// are called by this driver; coordinates come from currently rendered controls.
internal static class SheetNativeInput
{
    internal static async Task<Point> PointFor(string id, double x = .5, double y = .5)
    {
        await Rendered();
        Point result = default;
        await Ui.Run(() => {
            var control = Ui.Find<FrameworkElement>(id);
            Assert.That(control.IsLoaded && control.ActualWidth > 0 && control.ActualHeight > 0, Is.True);
            var bounds = control.TransformToVisual(Ui.Root).TransformBounds(new(0, 0, control.ActualWidth, control.ActualHeight));
            var hwnd = Win32Interop.GetWindowFromWindowId(Ui.Window.AppWindow.Id);
            Ui.Window.Activate();
            if (GetForegroundWindow() != hwnd) { Key(VirtualKey.Menu, true); Key(VirtualKey.Menu, false); SetForegroundWindow(hwnd); }
            Assert.That(GetForegroundWindow(), Is.EqualTo(hwnd));
            var origin = new NativePoint(); Assert.That(ClientToScreen(hwnd, ref origin), Is.True);
            var scale = Ui.Root.XamlRoot.RasterizationScale;
            result = new(origin.X + (bounds.X + bounds.Width * x) * scale, origin.Y + (bounds.Y + bounds.Height * y) * scale);
        });
        return result;
    }
    internal static Task Rendered() => Ui.Run(async () => {
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frames = 0;
            EventHandler<object> rendered = (_, _) => { if (++frames == 2) settled.TrySetResult(); };
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += rendered;
            try { await settled.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= rendered; }
        });
    internal static void Move(Point point) => Send(new() { Mouse = new() {
        X = (int)Math.Round((point.X - GetSystemMetrics(76)) * 65535d / (GetSystemMetrics(78) - 1)),
        Y = (int)Math.Round((point.Y - GetSystemMetrics(77)) * 65535d / (GetSystemMetrics(79) - 1)), Flags = 0x0001 | 0x4000 | 0x8000 } });
    internal static void Button(bool down) => Send(new() { Mouse = new() { Flags = down ? 0x0002u : 0x0004u } });
    internal static void Key(VirtualKey key, bool down) => Send(new() { Type = 1, Key = new() { VirtualKey = (ushort)key,
        Flags = (down ? 0u : 2u) | (key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down ? 1u : 0u) } });
    internal static async Task Press(VirtualKey key, params VirtualKey[] modifiers)
    {
        foreach (var modifier in modifiers) Key(modifier, true);
        try { Key(key, true); Key(key, false); }
        finally { foreach (var modifier in modifiers.Reverse()) Key(modifier, false); }
        await Ui.Run(() => { });
    }
    internal static async Task Click(string id, params VirtualKey[] modifiers)
    {
        var point = await PointFor(id, .3); Move(point);
        foreach (var modifier in modifiers) Key(modifier, true);
        try
        {
            await PointerStep(UIElement.PointerPressedEvent, () => Button(true));
            await PointerStep(UIElement.PointerReleasedEvent, () => Button(false));
        }
        finally { foreach (var modifier in modifiers.Reverse()) Key(modifier, false); }
        await Ui.Run(() => { });
    }
    internal static async Task Drag(string from, string to, Func<Task>? beforeRelease = null)
    {
        var first = await PointFor(from); var last = await PointFor(to);
        await Drag(first, last, beforeRelease);
    }
    internal static async Task Drag(Point first, Point last, Func<Task>? beforeRelease = null)
    {
        Move(first); await PointerStep(UIElement.PointerPressedEvent, () => Button(true));
        try { await PointerStep(UIElement.PointerMovedEvent, () => Move(last)); if (beforeRelease is not null) await beforeRelease(); }
        finally { await PointerStep(UIElement.PointerReleasedEvent, () => Button(false)); }
        await Ui.Run(() => { });
    }
    private static async Task PointerStep(RoutedEvent routedEvent, Action send)
    {
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PointerEventHandler observed = (_, _) => delivered.TrySetResult();
        await Ui.Run(() => Ui.Root.AddHandler(routedEvent, observed, true));
        try { send(); await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await Ui.Run(() => Ui.Root.RemoveHandler(routedEvent, observed)); }
    }
    private static void Send(Input input) => Assert.That(SendInput(1, [input], Marshal.SizeOf<Input>()), Is.EqualTo(1));
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MouseInput Mouse; [FieldOffset(8)] public KeyInput Key; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
}
