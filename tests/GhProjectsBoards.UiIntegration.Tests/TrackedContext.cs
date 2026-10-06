using Microsoft.UI.Dispatching;

namespace GhProjectsBoards.UiIntegration.Tests;

// async-void events must settle before the owning NUnit case can finish.
internal sealed class TrackedContext(DispatcherQueue queue) : SynchronizationContext
{
    private int operations, posts;
    private long postSequence;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> pendingPosts = new();
    internal static string DescribePosts() => "continuations=[" + string.Join("; ", current.pendingPosts.Values) + "]";
    private static TrackedContext current = null!;
    internal static int Operations => Volatile.Read(ref current.operations);
    internal static int Posts => Volatile.Read(ref current.posts);
    internal bool IsCurrent => ReferenceEquals(this, current);
    internal static void Start(DispatcherQueue queue)
    {
        current = new(queue);
        SetSynchronizationContext(current);
    }
    internal static void Install() => SetSynchronizationContext(current);
    public override void OperationStarted() => Interlocked.Increment(ref operations);
    public override void OperationCompleted() => Interlocked.Decrement(ref operations);
    public override SynchronizationContext CreateCopy() => this;
    public override void Post(SendOrPostCallback callback, object? state)
    {
        var id = Interlocked.Increment(ref postSequence);
        var target = state is Delegate continuation ? continuation.Target?.GetType().FullName : state?.GetType().FullName;
        pendingPosts[id] = $"{DateTime.UtcNow:O} {callback.Method.DeclaringType?.FullName}.{callback.Method.Name} state={target}";
        Interlocked.Increment(ref posts);
        if (!queue.TryEnqueue(() =>
        {
            var previous = Current;
            try { SetSynchronizationContext(this); callback(state); }
            catch (Exception error) { Ui.RecordFailure(error, this); }
            finally { SetSynchronizationContext(previous); pendingPosts.TryRemove(id, out _); Interlocked.Decrement(ref posts); }
        }))
        {
            pendingPosts.TryRemove(id, out _); Interlocked.Decrement(ref posts);
            Ui.RecordFailure(new InvalidOperationException("Asynchronous continuation dispatch rejected"), this);
        }
    }
}
