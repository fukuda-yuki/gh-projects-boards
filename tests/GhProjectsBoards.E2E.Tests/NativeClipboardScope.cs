using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace GhProjectsBoards.E2E.Tests;

// Materialize OLE formats before the app replaces the clipboard: OleGetClipboard
// can return a live proxy, not an immutable snapshot of delayed-rendered data.
internal sealed class NativeClipboardScope : IDisposable
{
    private IDataObject? previous;
    private bool disposed;

    public NativeClipboardScope()
    {
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        IDataObject? source = null;
        try
        {
            Retry(() => OleGetClipboard(out source));
            var iid = typeof(IDataObject).GUID;
            Marshal.ThrowExceptionForHR(SHCreateDataObject(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, ref iid, out previous));
            if (source is null) return;
            var formats = source.EnumFormatEtc(DATADIR.DATADIR_GET);
            try
            {
                var next = new FORMATETC[1];
                var fetched = new int[1];
                int result;
                while ((result = formats.Next(1, next, fetched)) == 0)
                {
                    var format = next[0];
                    try
                    {
                        source.GetData(ref format, out var medium);
                        try
                        {
                            format.tymed = medium.tymed;
                            previous.SetData(ref format, ref medium, true);
                            medium = default; // Ownership transferred to the independent data object.
                        }
                        finally { if (medium.tymed != TYMED.TYMED_NULL) ReleaseStgMedium(ref medium); }
                    }
                    finally { if (format.ptd != IntPtr.Zero) Marshal.FreeCoTaskMem(format.ptd); }
                }
                Marshal.ThrowExceptionForHR(result);
            }
            finally { Marshal.ReleaseComObject(formats); }
        }
        catch
        {
            if (previous is not null) Marshal.ReleaseComObject(previous);
            OleUninitialize();
            throw; // Abort before copying if any offered format cannot be preserved.
        }
        finally { if (source is not null) Marshal.ReleaseComObject(source); }
    }

    public static string? ReadText()
        => ReadStringFormat(13); // CF_UNICODETEXT

    internal static string? ReadTestFormat() => ReadStringFormat(TestFormat());

    // Synthetic formats exercise preservation independently of the user's clipboard.
    internal static void WriteTestFormats(string text = "Clipboard regression — 日本語")
    {
        var iid = typeof(IDataObject).GUID;
        Marshal.ThrowExceptionForHR(SHCreateDataObject(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, ref iid, out var data));
        try
        {
            Add(13, text);
            Add(TestFormat(), "Synthetic custom-format payload");
            Retry(() => OleSetClipboard(data));
            Retry(OleFlushClipboard);
        }
        finally { Marshal.ReleaseComObject(data); }

        void Add(uint id, string text)
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(text + '\0');
            var handle = GlobalAlloc(0x42, (nuint)bytes.Length); // movable, zero initialized
            if (handle == IntPtr.Zero) throw new OutOfMemoryException();
            var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle };
            try
            {
                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
                finally { GlobalUnlock(handle); }
                var format = new FORMATETC { cfFormat = unchecked((short)id), dwAspect = DVASPECT.DVASPECT_CONTENT,
                    lindex = -1, tymed = medium.tymed };
                data.SetData(ref format, ref medium, true);
                medium = default;
            }
            finally { if (medium.tymed != TYMED.TYMED_NULL) ReleaseStgMedium(ref medium); }
        }
    }

    private static uint TestFormat() => RegisterClipboardFormat("GhProjectsBoards.E2E.Synthetic");

    private static string? ReadStringFormat(uint format)
    {
        for (var attempt = 0; !OpenClipboard(IntPtr.Zero); attempt++)
        {
            if (attempt == 9) throw new Win32Exception(Marshal.GetLastWin32Error());
            Thread.Sleep(50);
        }
        try
        {
            var handle = GetClipboardData(format);
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
    [DllImport("shell32.dll")] private static extern int SHCreateDataObject(IntPtr folder, uint count,
        IntPtr items, IntPtr inner, ref Guid iid, out IDataObject data);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int OleGetClipboard(out IDataObject? data);
    [DllImport("ole32.dll")] private static extern int OleSetClipboard(IDataObject? data);
    [DllImport("ole32.dll")] private static extern int OleFlushClipboard();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);
}
