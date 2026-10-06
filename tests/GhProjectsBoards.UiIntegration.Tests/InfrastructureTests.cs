using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, Category("Infrastructure")]
public sealed class InfrastructureTests
{
    private Button button = null!;
    private TaskCompletionSource release = null!;
    [SetUp]
    public async Task Mount()
    {
        await Ui.BeginTest();
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Ui.Run(() => button = new Button { Content = "Infrastructure fault injection" });
        await Ui.Mount(button);
    }
    [TearDown]
    public async Task Cleanup()
    {
        try
        {
        release.TrySetResult();
        await Ui.Unmount(button, check: false);
        TestContext.Out.WriteLine("Failure-path visual root removed");
        await Ui.Idle();
            }
        finally { Ui.EndTest(); }
    }
    [Test]
    public void AssertionFailure() => Assert.Fail("Intentional runner assertion failure");
    [Test, Order(1)]
    public async Task AsyncFailureAfterAssertion()
    {
        await Ui.Run(() =>
        {
            button.Click += async (_, _) => { await release.Task; throw new InvalidOperationException("Intentional asynchronous event failure after assertion"); };
            Ui.Click(button);
        });
        Assert.That(TrackedContext.Operations, Is.GreaterThan(0), "The event must remain owned until teardown releases it");
    }
    [Test, Order(2)]
    public async Task FollowingCaseHasIndependentFailureTracking()
    {
        await Ui.Run(() => { button.Click += async (_, _) => await release.Task; Ui.Click(button); });
        Assert.That(TrackedContext.Operations, Is.GreaterThan(0));
        release.TrySetResult();
        await Ui.Idle();
    }
    [Test]
    public async Task HungHost() => await Ui.Run(() => new TaskCompletionSource().Task);
    [Test, Order(0)]
    public async Task LateFaultAfterDispatchDeadline()
    {
        Assert.ThrowsAsync<TimeoutException>(() => Ui.Run(async () => {
            await release.Task;
            throw new ArgumentException("Intentional late capture-like failure");
        }, timeout: TimeSpan.FromMilliseconds(100)));
        var failures = Ui.FailureCount;
        release.TrySetResult();
        await Ui.Run(() => Task.CompletedTask, check: false);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        await Task.Delay(100);
        Assert.That(Ui.FailureCount, Is.EqualTo(failures), "A late fault must not be counted again as an unrelated unobserved failure.");
        // Teardown deliberately reports the originating dispatcher timeout.
    }
    [Test]
    public async Task UnloadedCaptureFailsAtItsCaller()
    {
        await Ui.Unmount(button);
        Assert.ThrowsAsync<InvalidOperationException>(() => Ui.Run(() => RenderedEvidence.Capture(button, "unloaded")));
    }
    [Test]
    public async Task IncompleteTeardown()
    {
        await Ui.Run(() => { button.Click += async (_, _) => await new TaskCompletionSource().Task; Ui.Click(button); });
    }
}
