using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace GhProjectsBoards.E2E.Tests;

// OLE preserves the available clipboard formats without a UI-framework dependency.
internal sealed class NativeClipboardScope : IDisposable
{
    private IDataObject? previous;
    private bool disposed;

    public NativeClipboardScope()
    {
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        try { Retry(() => OleGetClipboard(out previous)); }
        catch { OleUninitialize(); throw; }
    }

    public static string? ReadText()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var handle = GetClipboardData(13); // CF_UNICODETEXT
            if (handle == IntPtr.Zero) return null;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return Marshal.PtrToStringUni(pointer); }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            Retry(() => OleSetClipboard(previous));
            if (previous is not null) Retry(OleFlushClipboard);
        }
        finally
        {
            if (previous is not null && Marshal.IsComObject(previous)) Marshal.ReleaseComObject(previous);
            previous = null;
            OleUninitialize();
        }
    }

    private static void Retry(Func<int> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = action();
            if (result >= 0) return;
            if (attempt == 9) Marshal.ThrowExceptionForHR(result);
            Thread.Sleep(50);
        }
    }

    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int OleGetClipboard(out IDataObject? data);
    [DllImport("ole32.dll")] private static extern int OleSetClipboard(IDataObject? data);
    [DllImport("ole32.dll")] private static extern int OleFlushClipboard();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);
}
