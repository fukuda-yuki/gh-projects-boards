using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;

// #275 / #286: the gh.exe path is set once per PC; empty returns to the PATH lookup.
[TestFixture]
internal sealed class PlanConnectionSettingsTests
{
    private string root = "";
    [SetUp] public void Setup() { root = Path.Combine(Path.GetTempPath(), "ghpb-connection-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TearDown] public void Cleanup() { try { Directory.Delete(root, true); } catch (IOException) { } }
    private PlanWorkspace Workspace() => new(new PlanStore(root), () => new(2026, 10, 26));

    [Test]
    public async Task AConfiguredPathSurvivesRestartAndClearingItReturnsToThePathLookup()
    {
        var workspace = Workspace();
        Assert.That(workspace.GhPath, Is.Null);
        Assert.That(workspace.GhExecutable, Is.EqualTo(GhProjectsBoards.App.ConnectionViewModel.FindGh()), "Without a setting the PATH lookup decides.");

        await workspace.SaveGhPath("  \"" + GhProcessTests.FakeExecutable + "\"  ");
        Assert.That(workspace.GhExecutable, Is.EqualTo(GhProcessTests.FakeExecutable), "Quotes and spaces from a pasted path are dropped.");
        Assert.That(Workspace().GhPath, Is.EqualTo(GhProcessTests.FakeExecutable), "Another workspace on this PC reads the same path after restart.");

        await workspace.SaveGhPath("");
        Assert.That(Workspace().GhPath, Is.Null);
    }

    [Test]
    public async Task APathThatNamesNoFileIsRefusedAndKeepsTheAcceptedValue()
    {
        var workspace = Workspace();
        await workspace.SaveGhPath(GhProcessTests.FakeExecutable);
        var missing = Path.Combine(root, "missing", "gh.exe");
        Assert.That(async () => await workspace.SaveGhPath(missing), Throws.ArgumentException.With.Message.EqualTo("gh.exe が見つかりません: " + missing));
        Assert.That(Workspace().GhPath, Is.EqualTo(GhProcessTests.FakeExecutable));
    }

    [Test]
    public void AnUnreadableFileFallsBackToThePathLookup()
    {
        var path = new PlanConnectionSettingsStore(new PlanStore(root)).FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        Assert.That(Workspace().GhPath, Is.Null);
    }

    [Test]
    public void ConnectingWithAMissingExecutableNamesTheExecutableAsTheCause()
    {
        var workspace = Workspace();
        var missing = Path.Combine(root, "gh.exe");
        var failure = Assert.ThrowsAsync<PlanConnectionFailedException>(() => workspace.Connect(new GhConnectionService(missing, "github.com")));
        Assert.That(failure!.Executable, Is.True);
        Assert.That(failure.Message, Is.EqualTo(GhProjectsBoards.App.ConnectionViewModel.FailureText(FailureKind.MissingExecutable)));
    }
}
