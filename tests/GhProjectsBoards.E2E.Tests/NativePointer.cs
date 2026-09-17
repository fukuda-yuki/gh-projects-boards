using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

internal static class NativePointer
{
    internal static void Drag(Window window, Point from, Point to)
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
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
