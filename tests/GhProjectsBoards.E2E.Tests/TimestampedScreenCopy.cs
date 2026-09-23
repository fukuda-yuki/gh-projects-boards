using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.Capturing;
using FlaUI.Core.WindowsAPI;

namespace GhProjectsBoards.E2E.Tests;

// Same full-rectangle desktop GDI route as FlaUI 5 Capture.Rectangle. Retain
// both the whole-call interval and the completed screen-copy interval so bitmap
// conversion/cleanup cannot obscure a threshold observation. Neither is scanout.
internal static class TimestampedScreenCopy
{
    internal sealed record Frame(long Begin, long End, long CopyBegin, long CopyEnd, CaptureImage Image);

    internal static Frame Rectangle(Rectangle bounds)
    {
        var begin = Stopwatch.GetTimestamp();
        var desktop = User32.GetDesktopWindow();
        var source = User32.GetWindowDC(desktop);
        if (source == nint.Zero) throw new Win32Exception();
        nint destination = 0, bitmap = 0, previous = 0;
        Bitmap? image = null;
        long copyBegin, copyEnd;
        try
        {
            destination = Gdi32.CreateCompatibleDC(source);
            if (destination == nint.Zero) throw new Win32Exception();
            bitmap = Gdi32.CreateCompatibleBitmap(source, bounds.Width, bounds.Height);
            if (bitmap == nint.Zero) throw new Win32Exception();
            previous = Gdi32.SelectObject(destination, bitmap);
            if (previous == nint.Zero || previous == new nint(-1)) throw new Win32Exception();
            copyBegin = Stopwatch.GetTimestamp();
            if (!Gdi32.BitBlt(destination, 0, 0, bounds.Width, bounds.Height, source, bounds.X, bounds.Y,
                CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt) || !GdiFlush()) throw new Win32Exception();
            copyEnd = Stopwatch.GetTimestamp();
            image = System.Drawing.Image.FromHbitmap(bitmap);
        }
        finally
        {
            if (previous != nint.Zero && previous != new nint(-1)) Gdi32.SelectObject(destination, previous);
            if (bitmap != nint.Zero) Gdi32.DeleteObject(bitmap);
            if (destination != nint.Zero) Gdi32.DeleteDC(destination);
            User32.ReleaseDC(desktop, source);
        }
        return new(begin, Stopwatch.GetTimestamp(), copyBegin, copyEnd, new CaptureImage(image, bounds, new CaptureSettings()));
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
}
