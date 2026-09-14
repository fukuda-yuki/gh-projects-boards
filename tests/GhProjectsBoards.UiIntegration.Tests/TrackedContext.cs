using Microsoft.UI.Dispatching;

namespace GhProjectsBoards.UiIntegration.Tests;

// async-void events must settle before the owning NUnit case can finish.
internal sealed class TrackedContext(DispatcherQueue queue) : SynchronizationContext
{
    internal static int Operations;
    internal static int Posts;
    public override void OperationStarted() => Interlocked.Increment(ref Operations);
    public override void OperationCompleted() => Interlocked.Decrement(ref Operations);
    public override SynchronizationContext CreateCopy() => this;
    public override void Post(SendOrPostCallback callback, object? state)
    {
        Interlocked.Increment(ref Posts);
        if (!queue.TryEnqueue(() =>
        {
            try { SetSynchronizationContext(this); callback(state); }
            catch (Exception error) { Ui.RecordFailure(error); }
            finally { Interlocked.Decrement(ref Posts); }
        }))
        {
            Interlocked.Decrement(ref Posts);
            Ui.RecordFailure(new InvalidOperationException("Asynchronous continuation dispatch rejected"));
        }
    }
}
