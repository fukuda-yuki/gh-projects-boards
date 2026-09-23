using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;

namespace GhProjectsBoards.App;

// Opt-in investigation only. Records are content-free and file work stays off the UI thread.
internal sealed class SheetDiagnostics
{
    private static readonly DiagnosticSink? sink = DiagnosticSink.Create();
    private static readonly ConcurrentBag<Task> observers = [];
    private static long nextGrid, nextSpan;
    [ThreadStatic] private static long currentSpan;
    private readonly long grid = Interlocked.Increment(ref nextGrid);
    private readonly List<RenderRequest> rendering = [];
    private Func<bool, object>? snapshot;
    private bool attached, visualCounts;
    private readonly bool visualWalk = Environment.GetEnvironmentVariable("GHPB_SHEET_VISUAL_WALK") != "0";
    private long previousRendering;
    private long? previousThreadCpu;
    private readonly bool threadTiming = Environment.GetEnvironmentVariable("GHPB_SHEET_THREAD_TIMING") == "1";
    private readonly bool renderingCallbacks = Environment.GetEnvironmentVariable("GHPB_SHEET_RENDER_CALLBACKS") != "0";
    private sealed record RenderRequest(long Span, long End);

    internal static SheetDiagnostics? Create() => sink is null ? null : new();
    internal static void Observe(Task task) { if (sink is not null) observers.Add(task); }
    internal static async Task CompleteAsync()
    {
        if (sink is null) return;
        await Task.WhenAll(observers.ToArray());
        await sink.CompleteAsync();
    }
    internal void Record(string kind, object? data = null) => sink!.Add(grid, kind, data);
    internal IDisposable Span(string kind, string? reason = null) => new MeasuredSpan(this, kind, reason);
    internal void RequestVisualCounts() => visualCounts = visualWalk;
    internal void Attach(Func<bool, object> state)
    {
        snapshot = state;
        if (attached) return;
        attached = true; visualCounts = visualWalk; previousRendering = 0; previousThreadCpu = null;
        if (renderingCallbacks) { CompositionTarget.Rendering += Rendering; CompositionTarget.Rendered += Rendered; }
        Record("grid-loaded", state(false));
    }
    internal void Detach()
    {
        if (attached) { CompositionTarget.Rendering -= Rendering; CompositionTarget.Rendered -= Rendered; }
        attached = false;
        Record("grid-unloaded", new { pendingRenderingBoundaries = rendering.Count });
        rendering.Clear(); snapshot = null;
        sink!.FlushInBackground();
    }
    private void Rendering(object? sender, object args)
    {
        var now = Stopwatch.GetTimestamp();
        long? cpu = threadTiming && GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user) ? kernel + user : null;
        // This callback precedes presentation; it is not evidence that pixels were displayed.
        // Optional OS thread CPU separates running from the unaccounted wall
        // interval. The remainder includes waits/preemption; it is not a cause.
        Record("rendering-callback", new { previousGapTicks = previousRendering == 0 ? (long?)null : now - previousRendering,
            threadCpu100ns = cpu, previousThreadCpu100ns = cpu - previousThreadCpu });
        previousThreadCpu = cpu;
        previousRendering = now;
        if (rendering.Count == 0 && !visualCounts) return;
        var pending = rendering.Select(r => new { span = r.Span, afterSpanTicks = now - r.End }).ToArray();
        rendering.Clear();
        var countVisuals = visualCounts; visualCounts = false;
        Record("rendering-boundary", new { spans = pending, state = snapshot?.Invoke(countVisuals), includesVisualWalk = countVisuals });
    }
    private void Rendered(object? sender, RenderedEventArgs args) => Record("rendered-callback", new {
        frameDurationTicks = args.FrameDuration.Ticks,
        boundary = "XAML reports its completed frame work; not desktop presentation or physical scanout." });

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
    private sealed class MeasuredSpan : IDisposable
    {
        private readonly SheetDiagnostics owner;
        private readonly string kind;
        private readonly string? reason;
        private readonly long id = Interlocked.Increment(ref nextSpan), parent = currentSpan;
        private readonly long start = Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread();
        private readonly int thread = Environment.CurrentManagedThreadId;
        private readonly int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        internal MeasuredSpan(SheetDiagnostics owner, string kind, string? reason)
        {
            this.owner = owner; this.kind = kind; this.reason = reason; currentSpan = id;
        }
        public void Dispose()
        {
            var end = Stopwatch.GetTimestamp();
            var sameThread = thread == Environment.CurrentManagedThreadId;
            var bytes = sameThread ? GC.GetAllocatedBytesForCurrentThread() - allocated : (long?)null;
            if (currentSpan == id) currentSpan = parent;
            owner.Record("ui-span", new { id, parent, kind, reason, start, end, elapsedTicks = end - start, thread,
                endThread = Environment.CurrentManagedThreadId, managedAllocatedBytesOnThread = bytes,
                gc0 = GC.CollectionCount(0) - gc0, gc1 = GC.CollectionCount(1) - gc1, gc2 = GC.CollectionCount(2) - gc2 });
            if (owner.renderingCallbacks && kind is ("build" or "rebuild" or "select" or "run" or "update"))
            {
                if (owner.rendering.Count < 256) owner.rendering.Add(new(id, end));
                else owner.Record("rendering-request-dropped");
            }
        }
    }

    private sealed class DiagnosticSink
    {
        private const int Capacity = 25000;
        private static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        private readonly string path;
        private readonly ConcurrentQueue<object> queue = new();
        private readonly Timer timer;
        private StreamWriter? writer;
        private int count, flushing, failed;
        private long sequence, dropped, totalDropped;
        private readonly object sync = new();
        private bool closing;
        private Task currentFlush = Task.CompletedTask;
        private DiagnosticSink(string path)
        {
            this.path = path;
            timer = new Timer(_ => FlushInBackground(), null, 250, 250);
            Add(0, "trace-start", new { frequency = Stopwatch.Frequency, process = Environment.ProcessId,
                runtime = Environment.Version.ToString(), boundary = "Synchronous method work and rendering callbacks; not pixel presentation. Managed allocations exclude native XAML allocations." });
        }
        internal static DiagnosticSink? Create()
        {
            var path = Environment.GetEnvironmentVariable("GHPB_SHEET_DIAGNOSTICS");
            return string.IsNullOrWhiteSpace(path) ? null : new(path);
        }
        internal void Add(long grid, string kind, object? data)
        {
            lock (sync)
            {
            if (closing) return;
            if (Volatile.Read(ref failed) != 0) return;
            if (Interlocked.Increment(ref count) > Capacity)
            {
                Interlocked.Decrement(ref count); Interlocked.Increment(ref dropped); Interlocked.Increment(ref totalDropped); return;
            }
            queue.Enqueue(new { sequence = Interlocked.Increment(ref sequence), ticks = Stopwatch.GetTimestamp(),
                thread = Environment.CurrentManagedThreadId, grid, kind, data });
            }
        }
        internal void FlushInBackground()
        {
            lock (sync)
            {
            if (closing) return;
            if (Volatile.Read(ref failed) != 0 || Interlocked.CompareExchange(ref flushing, 1, 0) != 0) return;
            currentFlush = Task.Run(Flush);
            }
        }
        internal async Task CompleteAsync()
        {
            Task pending;
            lock (sync) { closing = true; timer.Dispose(); pending = currentFlush; }
            await pending;
            await Task.Run(() =>
            {
                Flush();
                if (writer is null) return;
                try
                {
                    writer.WriteLine(JsonSerializer.Serialize(new { sequence = ++sequence, ticks = Stopwatch.GetTimestamp(), grid = 0,
                        kind = "trace-end", data = new { queued = count, dropped = totalDropped, failed,
                            boundary = "All accepted diagnostic records drained after draft flush, before native close." } }, Json));
                    writer.Flush(); writer.Dispose(); writer = null;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Interlocked.Exchange(ref failed, 1); Debug.WriteLine($"Sheet diagnostics completion failed: {error.GetType().Name}"); }
            });
        }
        private void Flush()
        {
                try
                {
                    if (writer is null)
                    {
                        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                        if (directory is not null) Directory.CreateDirectory(directory);
                        writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
                    }
                    while (queue.TryDequeue(out var sample))
                    {
                        Interlocked.Decrement(ref count);
                        writer.WriteLine(JsonSerializer.Serialize(sample, Json));
                    }
                    var lost = Interlocked.Exchange(ref dropped, 0);
                    if (lost > 0) writer.WriteLine(JsonSerializer.Serialize(new { kind = "records-dropped", count = lost }, Json));
                    writer.Flush();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    Interlocked.Exchange(ref failed, 1); timer.Dispose();
                    writer?.Dispose(); writer = null;
                    while (queue.TryDequeue(out _)) Interlocked.Decrement(ref count);
                    Debug.WriteLine($"Sheet diagnostics stopped: {error.GetType().Name}");
                }
                finally { Interlocked.Exchange(ref flushing, 0); }
        }
    }
}
