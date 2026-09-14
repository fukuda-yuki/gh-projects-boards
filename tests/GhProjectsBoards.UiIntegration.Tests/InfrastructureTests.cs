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
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Ui.Run(() => button = new Button { Content = "Infrastructure fault injection" });
        await Ui.Mount(button);
    }
    [TearDown]
    public async Task Cleanup()
    {
        release.TrySetResult();
        await Ui.Unmount(button, check: false);
        TestContext.Out.WriteLine("Failure-path visual root removed");
        await Ui.Idle();
    }
    [Test]
    public void AssertionFailure() => Assert.Fail("Intentional runner assertion failure");
    [Test]
    public async Task AsyncFailureAfterAssertion()
    {
        await Ui.Run(() =>
        {
            button.Click += async (_, _) => { await release.Task; throw new InvalidOperationException("Intentional asynchronous event failure after assertion"); };
            Ui.Click(button);
        });
        Assert.That(TrackedContext.Operations, Is.GreaterThan(0), "The event must remain owned until teardown releases it");
    }
    [Test]
    public async Task HungHost() => await Ui.Run(() => new TaskCompletionSource().Task);
    [Test]
    public async Task IncompleteTeardown()
    {
        await Ui.Run(() => { button.Click += async (_, _) => await new TaskCompletionSource().Task; Ui.Click(button); });
    }
}
