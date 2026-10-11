using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, Category("WorkspaceShell")]
internal sealed class PlanWorkspaceLifetimeHostedTests
{
    private string root = null!;
    private bool previousTitleBarMode;
    [SetUp]
    public async Task Setup()
    {
        await Ui.BeginTest();
        root = Path.Combine(Path.GetTempPath(), "ghpb-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await Ui.Run(() => {
            previousTitleBarMode = Ui.Window.ExtendsContentIntoTitleBar;
            Ui.Window.ExtendsContentIntoTitleBar = true;
        });
    }
    [TearDown]
    public async Task Cleanup()
    {
        try {
            await Ui.Run(() => Ui.Window.ExtendsContentIntoTitleBar = previousTitleBarMode);
            Directory.Delete(root, true);
        }
        finally { Ui.EndTest(); }
    }

    [Test]
    public async Task UnloadedWorkspaceReleasesItsVisualTree()
    {
        var released = await MountAndRelease();
        for (var attempt = 0; attempt < 5 && released.IsAlive; attempt++)
        {
            await Task.Run(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            await Ui.Run(() => { });
        }
        Assert.That(released.IsAlive, Is.False, "An unloaded workspace must not stay reachable through its title bar.");
    }

    // Only a weak reference leaves this method, so the caller's state machine cannot keep the view.
    private async Task<WeakReference> MountAndRelease()
    {
        PlanWorkspaceView view = null!;
        await Ui.Run(() => view = new PlanWorkspaceView(new PlanWorkspace(new(root)),
            (_, host) => new("gh.exe", host, new GhProcessRunner(new Dictionary<string, string?>()))));
        try {
            await Ui.Mount(view);
            await Ui.Run(() => Ui.Window.SetTitleBar(view.WorkspaceTitleBar));
            await Ui.Ready<Microsoft.UI.Xaml.Controls.Button>("PlanProjectPicker");
        } finally {
            await Ui.Run(() => Ui.Window.SetTitleBar(null));
            await Ui.Unmount(view, check: false);
        }
        return new WeakReference(view);
    }
}
