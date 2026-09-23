using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

internal static class NativePointer
{
    internal static void Position(Window window, Point point)
    {
        Assert.That(GetForegroundWindow(), Is.EqualTo(window.Properties.NativeWindowHandle.Value));
        Move(point); FlaUI.Core.Input.Wait.UntilInputIsProcessed();
    }
    internal static (long Begin, long KeyBegin, long Sent) SelectAndType(ushort key, int keyDelayMs)
    {
        // A declared physical key interval is inside the selection boundary.
        // No focus polling, extra click or character replay hides activation.
        var pointer = new[] {
            new Input { Mouse = new() { Flags = 0x0002 } },
            new Input { Mouse = new() { Flags = 0x0004 } }
        };
        var keyboard = new[] {
            new Input { Type = 1, Keyboard = new() { Key = key } },
            new Input { Type = 1, Keyboard = new() { Key = key, Flags = 0x0002 } }
        };
        var begin = Stopwatch.GetTimestamp();
        Assert.That(SendInput((uint)pointer.Length, pointer, Marshal.SizeOf<Input>()), Is.EqualTo(pointer.Length));
        while (Stopwatch.GetElapsedTime(begin).TotalMilliseconds < keyDelayMs) Thread.Sleep(1);
        var keyBegin = Stopwatch.GetTimestamp();
        var sent = SendInput((uint)keyboard.Length, keyboard, Marshal.SizeOf<Input>());
        var end = Stopwatch.GetTimestamp();
        Assert.That(sent, Is.EqualTo(keyboard.Length), "Selection and first character must be delivered once.");
        return (begin, keyBegin, end);
    }
    internal static void Drag(Window window, Point from, Point to, Action? beforeRelease = null)
    {
        Assert.That(GetForegroundWindow(), Is.EqualTo(window.Properties.NativeWindowHandle.Value));
        Move(from); FlaUI.Core.Input.Wait.UntilInputIsProcessed();
        Mouse.Down(MouseButton.Left);
        try
        {
            FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            // SetCursorPos relocates the cursor but did not deliver WinUI Thumb's
            // drag delta in this host. SendInput supplies an actual mouse move.
            Move(to); FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            beforeRelease?.Invoke();
        }
        finally { Mouse.Up(MouseButton.Left); }
        FlaUI.Core.Input.Wait.UntilInputIsProcessed();
    }
    private static void Move(Point point)
    {
        var input = new Input { Mouse = new MouseInput {
            X = (int)Math.Round((point.X - GetSystemMetrics(76)) * 65535d / (GetSystemMetrics(78) - 1)),
            Y = (int)Math.Round((point.Y - GetSystemMetrics(77)) * 65535d / (GetSystemMetrics(79) - 1)),
            Flags = 0x0001 | 0x4000 | 0x8000 } };
        Assert.That(SendInput(1, [input], Marshal.SizeOf<Input>()), Is.EqualTo(1), "Native mouse move must be delivered.");
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
