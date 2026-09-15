using System.Diagnostics;
using System.IO;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using NUnit.Framework;
using Application = FlaUI.Core.Application;

namespace GhProjectsBoards.E2E.Tests;

[TestFixture]
[Category("LiveGitHub")]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class LiveConnectionTests
{
    [Test]
    public void OrdinaryExecutableDiagnosesTheAuthorizedLiveSandbox()
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_LIVE_GITHUB") != "1")
            Assert.Ignore("Run scripts/Test-LiveGitHub.ps1 on an interactive Windows desktop.");
        Assert.That(Environment.UserInteractive, Is.True, "An interactive desktop is required.");
        using var dpi = new DesktopDpiScope();
        var executable = Environment.GetEnvironmentVariable("GHPB_E2E_APP_PATH")!;
        var gh = Environment.GetEnvironmentVariable("GHPB_LIVE_GH_PATH")!;
        var artifacts = Environment.GetEnvironmentVariable("GHPB_LIVE_ARTIFACTS")!;
        Assert.That(File.Exists(executable) && File.Exists(gh), Is.True);
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!,
            Environment = { ["GHPB_DATA_ROOT"] = Path.Combine(artifacts, "connection-data-" + Guid.NewGuid().ToString("N")) }
        })!;
        using var automation = new UIA3Automation();
        using var application = Application.Attach(process.Id);
        Window? window = null;
        try
        {
            window = application.GetMainWindow(automation, TimeSpan.FromSeconds(20));
            Assert.That(window, Is.Not.Null);
            WinUiProcess.AssertRuntime(process);
            Element("ExecutablePath").AsTextBox().Text = gh;
            Element("HostInput").AsTextBox().Text = "github.com";
            Element("IssueUrlInput").AsTextBox().Text = "https://github.com/fukuda-yuki/codex-sandbox/issues/1";
            Element("ProjectUrlInput").AsTextBox().Text = "https://github.com/users/fukuda-yuki/projects/3";
            Element("CheckConnectionButton").AsButton().Invoke();
            Assert.That(Retry.WhileFalse(() => Element("CheckConnectionButton").IsEnabled
                && Element("ConnectionStatus").Name.Contains("接続を確認しました", StringComparison.Ordinal),
                TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(200)).Result, Is.True);
            Assert.That(Element("AccountValue").Name, Does.Contain("fukuda-yuki").And.Contain("github.com"));
            Assert.That(Element("StorageValue").Name, Does.Contain("keyring"));
            Assert.That(Element("IssueResult").Name, Does.Contain("読み取り：あり").And.Contain("更新権限：あり").And.Contain("repo：あり"));
            Assert.That(Element("ProjectResult").Name, Does.Contain("読み取り：あり").And.Contain("更新権限：あり").And.Contain("project：あり"));
            Capture("live-connected");
            window!.Close();
            Assert.That(Retry.WhileFalse(() => process.HasExited, TimeSpan.FromSeconds(10)).Result, Is.True);
            Assert.That(process.ExitCode, Is.Zero);
        }
        catch
        {
            if (window is not null && !process.HasExited) Capture("live-failure");
            throw;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
        }

        AutomationElement Element(string id) => WorkspaceUi.Element(window!, id);
        void Capture(string name)
        {
            try
            {
                var path = Path.Combine(artifacts, $"{name}.png");
                using var capture = FlaUI.Core.Capturing.Capture.Element(window!);
                capture.ToFile(path);
                TestContext.AddTestAttachment(path, "Ordinary application using real gh and the authorized sandbox");
            }
            catch (Exception exception) { TestContext.Progress.WriteLine($"Capture unavailable: {exception.GetType().Name}"); }
        }
    }
}
