using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture, NonParallelizable]
internal sealed class PlanStartupTests
{
    private string? previous;
    [SetUp] public void SaveRoot() => previous = Environment.GetEnvironmentVariable("GHPB_DATA_ROOT");
    [TearDown] public void RestoreRoot()
    {
        Environment.SetEnvironmentVariable("GHPB_DATA_ROOT", previous);
        PlanStartup.ViewProbe = null;
    }

    [Test]
    public void AnUnsetRootNamesTheRegistrationsFolder()
    {
        Environment.SetEnvironmentVariable("GHPB_DATA_ROOT", null);
        Assert.That(PlanDataRoot.Resolve(), Is.EqualTo(PlanDataRoot.DefaultFolder));
        Assert.That(PlanDataRoot.DefaultFolder, Does.EndWith(Path.Combine("GhProjectsBoards", "Registrations")));
    }

    [Test]
    public void ARelativeRootIsADataRootFailureAndNamesThatFolder()
    {
        Environment.SetEnvironmentVariable("GHPB_DATA_ROOT", "plans");
        var called = false;
        var notice = PlanStartup.Try(() => called = true)!.Value;
        Assert.That(called, Is.False, "The workspace is not built when the data root cannot be used.");
        Assert.That(notice.Kind, Is.EqualTo(PlanStartupKind.DataRoot));
        Assert.That(notice.Text, Does.Contain("plans"));
        Assert.That(notice.Text, Does.Contain("保存先を利用できません。"));
        Assert.That(notice.Text, Does.Contain("GHPB_DATA_ROOT は絶対パスを指定してください。"));
    }

    [Test]
    public void AnUnusableAbsoluteRootNamesThatFolder()
    {
        var file = Path.Combine(Path.GetTempPath(), "ghpb-startup-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "not a directory");
        var blocked = Path.Combine(file, "child");
        try
        {
            Environment.SetEnvironmentVariable("GHPB_DATA_ROOT", blocked);
            var notice = PlanStartup.Try(() => { })!.Value;
            Assert.That(notice.Kind, Is.EqualTo(PlanStartupKind.DataRoot));
            Assert.That(notice.Text, Does.Contain(blocked));
        }
        finally { File.Delete(file); }
    }

    [Test]
    public void AWorkspaceFailureNamesTheExceptionAndNotTheDataRoot()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ghpb-startup-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("GHPB_DATA_ROOT", folder);
        PlanStartup.ViewProbe = new InvalidOperationException("forced workspace failure");
        var called = false;
        var notice = PlanStartup.Try(() => called = true)!.Value;
        try
        {
            Assert.That(called, Is.False, "The probe stands in for a failure while building the workspace.");
            Assert.That(Directory.Exists(folder), Is.True);
            Assert.That(notice.Kind, Is.EqualTo(PlanStartupKind.General));
            Assert.That(notice.Text, Does.Contain("起動できませんでした。"));
            Assert.That(notice.Text, Does.Contain(typeof(InvalidOperationException).FullName));
            Assert.That(notice.Text, Does.Contain("forced workspace failure"));
            Assert.That(notice.Text, Does.Not.Contain("保存先を利用できません"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    public void AUsableRootStartsTheWorkspace()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ghpb-startup-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("GHPB_DATA_ROOT", folder);
        var called = false;
        try
        {
            Assert.That(PlanStartup.Try(() => called = true), Is.Null);
            Assert.That(called, Is.True);
            Assert.That(PlanDataRoot.Prepare(), Is.EqualTo(folder));
        }
        finally { Directory.Delete(folder, true); }
    }
}
