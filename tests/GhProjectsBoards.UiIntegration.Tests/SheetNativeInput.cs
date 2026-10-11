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
    internal static async Task CaptureDesktop(string name)
    {
        // GDI captures the actual composed popup HWNDs; RenderTargetBitmap can omit
        // native popup islands. This bounded diagnostic runs only after failure.
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        Assert.That(width > 0 && height > 0 && (long)width * height <= 16000000, Is.True);
        var screen = GetDC(nint.Zero);
        Assert.That(screen, Is.Not.EqualTo(nint.Zero));
        var memory = CreateCompatibleDC(screen);
        nint bitmap = nint.Zero, previous = nint.Zero;
        var pixels = new byte[checked(width * height * 4)];
        try {
            Assert.That(memory, Is.Not.EqualTo(nint.Zero));
            var header = new BitmapHeader { Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = width,
                Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref header, 0, out var bits, nint.Zero, 0);
            Assert.That(bitmap, Is.Not.EqualTo(nint.Zero));
            previous = SelectObject(memory, bitmap);
            Assert.That(previous != nint.Zero && previous != new nint(-1), Is.True);
            Assert.That(BitBlt(memory, 0, 0, width, height, screen, left, top, 0x40CC0020), Is.True);
            Assert.That(GdiFlush(), Is.True);
            Marshal.Copy(bits, pixels, 0, pixels.Length);
        }
        finally {
            if (previous != nint.Zero && previous != new nint(-1)) SelectObject(memory, previous);
            if (bitmap != nint.Zero) DeleteObject(bitmap);
            if (memory != nint.Zero) DeleteDC(memory);
            ReleaseDC(nint.Zero, screen);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "screenshots",
            TestContext.CurrentContext.Test.ID + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(folder).AsTask(deadline.Token);
        var file = await destination.CreateFileAsync(name + ".png").AsTask(deadline.Token);
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite).AsTask(deadline.Token);
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream).AsTask(deadline.Token);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync().AsTask(deadline.Token);
        TestContext.Out.WriteLine($"Native desktop evidence: {file.Path}; origin={left},{top}; size={width}x{height}");
    }
    internal static async Task<string> DesktopState()
    {
        var state = "";
        await Ui.Run(() => {
            var hwnd = Win32Interop.GetWindowFromWindowId(Ui.Window.AppWindow.Id);
            var origin = new NativePoint(); Assert.That(ClientToScreen(hwnd, ref origin), Is.True);
            Assert.That(GetCursorPos(out var cursor), Is.True);
            var windowAtCursor = WindowFromPoint(cursor);
            state = $"hwnd={hwnd:X} foreground={GetForegroundWindow():X} cursor={cursor.X},{cursor.Y} "
                + $"clientOrigin={origin.X},{origin.Y} scale={Ui.Root.XamlRoot.RasterizationScale} "
                + $"host=[{DescribeWindow(hwnd)}] cursorWindow=[{DescribeWindow(windowAtCursor)}]";
        });
        return state;

        static string DescribeWindow(nint hwnd)
        {
            var name = new System.Text.StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            GetWindowRect(hwnd, out var bounds);
            GetWindowThreadProcessId(hwnd, out var processId);
            return $"hwnd={hwnd:X} class={name} rect={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom} "
                + $"owner={GetWindow(hwnd, 4):X} parent={GetParent(hwnd):X} root={GetAncestor(hwnd, 2):X} process={processId}";
        }
    }
    internal static Task ActivateWindow() => Ui.Run(() => {
        var hwnd = Win32Interop.GetWindowFromWindowId(Ui.Window.AppWindow.Id);
        Ui.Window.Activate();
        if (GetForegroundWindow() != hwnd) { Key(VirtualKey.Menu, true); Key(VirtualKey.Menu, false); SetForegroundWindow(hwnd); }
        Assert.That(GetForegroundWindow(), Is.EqualTo(hwnd));
    });
    internal static Task<Point> PointFor(string id, double x = .5, double y = .5) => PointFor(() => Ui.Find<FrameworkElement>(id), x, y);
    internal static Task<Point> PointFor(FrameworkElement control, double x = .5, double y = .5) => PointFor(() => control, x, y);
    private static async Task<Point> PointFor(Func<FrameworkElement> find, double x, double y)
    {
        await Rendered();
        await ActivateWindow();
        Point result = default;
        await Ui.Run(() => result = ScreenPoint(find(), x, y));
        return result;
    }
    // Call on the UI thread to convert XAML root coordinates to physical screen coordinates.
    internal static Point ScreenPoint(FrameworkElement control, double x = .5, double y = .5)
    {
        Assert.That(control.IsLoaded && control.ActualWidth > 0 && control.ActualHeight > 0, Is.True);
        var bounds = control.TransformToVisual(Ui.Root).TransformBounds(new(0, 0, control.ActualWidth, control.ActualHeight));
        var hwnd = Win32Interop.GetWindowFromWindowId(Ui.Window.AppWindow.Id);
        Assert.That(GetForegroundWindow(), Is.EqualTo(hwnd));
        var origin = new NativePoint(); Assert.That(ClientToScreen(hwnd, ref origin), Is.True);
        var scale = Ui.Root.XamlRoot.RasterizationScale;
        return new(origin.X + (bounds.X + bounds.Width * x) * scale, origin.Y + (bounds.Y + bounds.Height * y) * scale);
    }
    internal static Task Rendered() => Ui.Run(async () => {
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frames = 0;
            EventHandler<object> rendered = (_, _) => { if (++frames == 2) settled.TrySetResult(); };
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += rendered;
            try { await settled.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= rendered; }
        });
    internal static void Move(Point point, bool noCoalesce = false) => Send(new() { Mouse = new() {
        X = (int)Math.Round((point.X - GetSystemMetrics(76)) * 65535d / (GetSystemMetrics(78) - 1)),
        Y = (int)Math.Round((point.Y - GetSystemMetrics(77)) * 65535d / (GetSystemMetrics(79) - 1)), Flags = 0x0001u | 0x4000 | 0x8000 | (noCoalesce ? 0x2000u : 0u) } });
    internal static void Button(bool down) => Send(new() { Mouse = new() { Flags = down ? 0x0002u : 0x0004u } });
    internal static void Wheel(int delta, bool horizontal = false) => Send(new() { Mouse = new() { Data = unchecked((uint)delta), Flags = horizontal ? 0x1000u : 0x0800u } });
    internal static async Task ScrollWheel(int delta, bool horizontal = false, bool shift = false)
    {
        if (shift) Key(VirtualKey.Shift, true);
        try { await PointerStep(UIElement.PointerWheelChangedEvent, () => Wheel(delta, horizontal)); }
        finally { if (shift) Key(VirtualKey.Shift, false); }
    }
    internal static uint WheelLines()
    {
        Assert.That(SystemParametersInfo(0x0068, 0, out var lines, 0), Is.True);
        return lines;
    }
    internal static void Key(VirtualKey key, bool down) => Send(new() { Type = 1, Key = new() { VirtualKey = (ushort)key,
        Flags = (down ? 0u : 2u) | (key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down ? 1u : 0u) } });
    internal static async Task Press(VirtualKey key, params VirtualKey[] modifiers)
    {
        await Ui.Run(() => Assert.That(GetForegroundWindow(), Is.EqualTo(Win32Interop.GetWindowFromWindowId(Ui.Window.AppWindow.Id)),
            "The UI host must own foreground before physical keys are sent."));
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyEventHandler observed = (_, args) => { if (args.OriginalKey == key) delivered.TrySetResult(); };
        await Ui.Run(() => Ui.Root.AddHandler(UIElement.KeyUpEvent, observed, true));
        try {
            foreach (var modifier in modifiers) Key(modifier, true);
            try { Key(key, true); Key(key, false); }
            finally { foreach (var modifier in modifiers.Reverse()) Key(modifier, false); }
            // Dispatcher work can run before injected native keys. Key-up establishes
            // that the preceding key-down has finished traversing the actual UI route.
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await Ui.Run(() => Ui.Root.RemoveHandler(UIElement.KeyUpEvent, observed)); }
    }
    internal static async Task Click(string id, params VirtualKey[] modifiers)
    {
        var point = await PointFor(id, .3); Move(point);
        foreach (var modifier in modifiers) Key(modifier, true);
        var pressed = false;
        try
        {
            await PointerStep(UIElement.PointerPressedEvent, () => { Button(true); pressed = true; });
            await PointerStep(UIElement.PointerReleasedEvent, () => { Button(false); pressed = false; });
        }
        finally
        {
            // A missing routed event can time out after SendInput succeeded.
            // Release the owned press before another test uses the desktop.
            if (pressed) Button(false);
            foreach (var modifier in modifiers.Reverse()) Key(modifier, false);
        }
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
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRectangle bounds);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapHeader info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);
}
