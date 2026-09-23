using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GhProjectsBoards.E2E.Tests;

// Passive observation through public DXGI desktop duplication. Only the declared
// app viewport is copied to CPU memory. No input, OS settings, GPU/ETW profiling,
// package dependency, or product renderer change. GDI remains a separate observer.
internal sealed class DesktopFrameObserver : IDisposable
{
    private readonly ManualResetEventSlim armed = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task capture;
    private readonly List<Frame> frames = [];
    private readonly string output;
    private readonly Rectangle bounds;
    private sealed record Frame(long Begin, long Acquired, long Copied, long Present, uint Accumulated, bool Masked, byte[] Pixels);
    public DesktopFrameObserver(Rectangle bounds, string output)
    {
        this.bounds = bounds; this.output = output;
        if (Directory.Exists(output)) throw new InvalidOperationException("Retain prior desktop observations.");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "environment.json"), JsonSerializer.Serialize(new { bounds.X, bounds.Y, bounds.Width, bounds.Height,
            frequency = Stopwatch.Frequency, boundary = "DXGI desktop LastPresentTime and independent CPU pixel-copy completion; not physical scanout" }));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture = Task.Run(() => Capture(ready));
        ready.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
    }
    public void Arm() => armed.Set();
    public void Dispose()
    {
        stop.Cancel(); armed.Set();
        Exception? failure = null;
        try { capture.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult(); } catch (Exception ex) { failure = ex; }
        try
        {
            var index = new List<object>();
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i]; var path = $"desktop-{i:D4}.png";
                using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                var pixels = bitmap.LockBits(new(0, 0, bounds.Width, bounds.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try { for (var y = 0; y < bounds.Height; y++) Marshal.Copy(frame.Pixels, y * bounds.Width * 4, pixels.Scan0 + y * pixels.Stride, bounds.Width * 4); }
                finally { bitmap.UnlockBits(pixels); }
                bitmap.Save(Path.Combine(output, path), ImageFormat.Png);
                index.Add(new { index = i, begin = frame.Begin, acquired = frame.Acquired, copied = frame.Copied,
                    present = frame.Present, accumulated = frame.Accumulated, masked = frame.Masked, path });
            }
            File.WriteAllText(Path.Combine(output, "frames.json"), JsonSerializer.Serialize(index));
            File.WriteAllText(Path.Combine(output, "lifetime.json"), JsonSerializer.Serialize(new { count = frames.Count, saturated = frames.Count >= 512,
                error = failure?.ToString(), complete = failure is null && frames.Count is > 0 and < 512 }));
        }
        finally { armed.Dispose(); stop.Dispose(); }
        if (failure is not null) throw new InvalidOperationException("Desktop observer failed; partial observations retained.", failure);
        if (frames.Count is 0 or >= 512) throw new InvalidOperationException("Missing or saturated desktop observer.");
    }
    private void Capture(TaskCompletionSource ready)
    {
        nint factory = 0, adapter = 0, output1 = 0, device = 0, context = 0, duplication = 0, staging = 0;
        try
        {
            Check(CreateDXGIFactory1(new("770aae78-f26f-4dba-a829-253c83d1b387"), out factory));
            OutputDescription display = default;
            for (uint a = 0; output1 == 0 && Method<Enumerate>(factory, 12)(factory, a, out adapter) != NotFound; a++)
            {
                for (uint m = 0; Method<Enumerate>(adapter, 7)(adapter, m, out var candidate) != NotFound; m++)
                {
                    try
                    {
                        Check(Method<GetDescription>(candidate, 7)(candidate, out var description));
                        if (description.Attached == 0 || bounds.Left < description.Left || bounds.Top < description.Top
                            || bounds.Right > description.Right || bounds.Bottom > description.Bottom) continue;
                        if (description.Rotation != 1) throw new InvalidOperationException("Rotated output is outside this observer.");
                        Check(Marshal.QueryInterface(candidate, in Output1Id, out output1)); display = description; break;
                    }
                    finally { Release(ref candidate); }
                }
                if (output1 == 0) Release(ref adapter);
            }
            if (output1 == 0) throw new InvalidOperationException("The complete viewport must be on one connected output.");
            Check(D3D11CreateDevice(adapter, 0, 0, 0x20, 0, 0, 7, out device, out _, out context));
            Check(Method<Duplicate>(output1, 22)(output1, device, out duplication));
            var description2D = new TextureDescription { Width = (uint)bounds.Width, Height = (uint)bounds.Height, Mips = 1, Array = 1,
                Format = 87, Samples = 1, Usage = 3, CpuAccess = 0x20000 };
            Check(Method<CreateTexture>(device, 5)(device, in description2D, 0, out staging));
            var crop = new Box { Left = (uint)(bounds.Left - display.Left), Top = (uint)(bounds.Top - display.Top),
                Right = (uint)(bounds.Right - display.Left), Bottom = (uint)(bounds.Bottom - display.Top), Back = 1 };
            ready.SetResult(); armed.Wait();
            while (!stop.IsCancellationRequested && frames.Count < 512)
            {
                var begin = Stopwatch.GetTimestamp();
                var hr = Method<Acquire>(duplication, 8)(duplication, 50, out var info, out var resource);
                if (hr == WaitTimeout) continue;
                Check(hr); var acquired = Stopwatch.GetTimestamp(); nint texture = 0;
                try
                {
                    if (info.Present == 0) continue;
                    Check(Marshal.QueryInterface(resource, in TextureId, out texture));
                    // Slot 46 is ID3D11DeviceContext::CopySubresourceRegion; slots
                    // 14/15 are Map/Unmap. These are the SDK's public COM layout.
                    Method<CopyRegion>(context, 46)(context, staging, 0, 0, 0, 0, texture, 0, in crop);
                    Check(Method<Map>(context, 14)(context, staging, 0, 1, 0, out var mapped));
                    var pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
                    try { for (var y = 0; y < bounds.Height; y++) Marshal.Copy(mapped.Data + checked(y * (int)mapped.RowPitch), pixels, y * bounds.Width * 4, bounds.Width * 4); }
                    finally { Method<Unmap>(context, 15)(context, staging, 0); }
                    frames.Add(new(begin, acquired, Stopwatch.GetTimestamp(), info.Present, info.Accumulated, info.Masked != 0, pixels));
                }
                finally { Release(ref texture); Release(ref resource); Check(Method<Finish>(duplication, 14)(duplication)); }
            }
        }
        catch (Exception ex) { ready.TrySetException(ex); throw; }
        finally { Release(ref staging); Release(ref duplication); Release(ref context); Release(ref device); Release(ref output1); Release(ref adapter); Release(ref factory); }
    }
    private const int NotFound = unchecked((int)0x887A0002), WaitTimeout = unchecked((int)0x887A0027);
    private static readonly Guid Output1Id = new("00cddea8-939b-4b83-a340-a685226666cc"), TextureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    private static void Release(ref nint value) { if (value != 0) { Marshal.Release(value); value = 0; } }
    private static T Method<T>(nint instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OutputDescription { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public int Left, Top, Right, Bottom, Attached, Rotation; public nint Monitor; }
    [StructLayout(LayoutKind.Sequential)] private struct TextureDescription { public uint Width, Height, Mips, Array, Format, Samples, Quality, Usage, Bind, CpuAccess, Misc; }
    [StructLayout(LayoutKind.Sequential)] private struct Box { public uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)] private struct FrameInfo { public long Present, Mouse; public uint Accumulated; public int Coalesced, Masked, PointerX, PointerY, PointerVisible; public uint Metadata, PointerShape; }
    [StructLayout(LayoutKind.Sequential)] private struct Mapped { public nint Data; public uint RowPitch, DepthPitch; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Enumerate(nint instance, uint index, out nint item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(nint instance, out OutputDescription description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Duplicate(nint instance, nint device, out nint duplication);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture(nint instance, in TextureDescription description, nint initialData, out nint texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Acquire(nint instance, uint timeout, out FrameInfo info, out nint resource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Finish(nint instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyRegion(nint instance, nint destination, uint subresource, uint x, uint y, uint z, nint source, uint sourceSubresource, in Box crop);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Map(nint instance, nint resource, uint subresource, uint type, uint flags, out Mapped mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Unmap(nint instance, nint resource, uint subresource);
    [DllImport("dxgi.dll", ExactSpelling = true)] private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);
    [DllImport("d3d11.dll", ExactSpelling = true)] private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, nint levels, uint levelCount, uint sdk, out nint device, out uint level, out nint context);
}
