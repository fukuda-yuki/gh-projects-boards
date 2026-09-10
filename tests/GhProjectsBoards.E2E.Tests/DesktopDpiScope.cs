using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GhProjectsBoards.E2E.Tests;

internal sealed class DesktopDpiScope : IDisposable
{
    // UI Automation exposes physical coordinates. An unaware test host would
    // capture a scaled/offset rectangle instead of the complete app window.
    private readonly nint previous = SetThreadDpiAwarenessContext(new nint(-4)); // Per-monitor aware V2

    public DesktopDpiScope()
    {
        if (previous == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose() => SetThreadDpiAwarenessContext(previous);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
}
