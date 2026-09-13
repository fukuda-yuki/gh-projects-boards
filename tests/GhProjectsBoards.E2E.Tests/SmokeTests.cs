using System.Diagnostics;
using System.IO;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using NUnit.Framework;
using Application = FlaUI.Core.Application;

namespace GhProjectsBoards.E2E.Tests;

[TestFixture]
[Category("E2E")]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class SmokeTests
{
    [Test]
    public void OrdinaryExecutable_OpensAndCloses()
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_E2E") != "1")
            Assert.Ignore("Desktop E2E is opt-in. Run scripts/Test-E2E.ps1 on an unlocked Windows desktop.");
        Assert.That(Environment.UserInteractive, Is.True,
            "An interactive desktop is required; this does not verify that it is unlocked.");
        using var dpi = new DesktopDpiScope();
        var executable = Environment.GetEnvironmentVariable("GHPB_E2E_APP_PATH");
        Assert.That(!string.IsNullOrWhiteSpace(executable) && File.Exists(executable), Is.True,
            "Build the app and set GHPB_E2E_APP_PATH, or use scripts/Test-E2E.ps1.");
        executable = Path.GetFullPath(executable!);
        using var automation = new UIA3Automation();
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!,
            Environment = { ["GHPB_DATA_ROOT"] = Path.Combine(Environment.GetEnvironmentVariable("GHPB_E2E_ARTIFACTS")!, "smoke-data-" + Guid.NewGuid().ToString("N")) }
        })!;
        // Preserve the original launch handle for the post-close exit-code assertion.
        using var application = Application.Attach(process.Id);
        Window? window = null;
        try
        {
            window = application.GetMainWindow(automation, TimeSpan.FromSeconds(20));
            Assert.That(window, Is.Not.Null, "The ordinary executable did not expose a main window.");
            WinUiProcess.AssertRuntime(process);
            Assert.That(window!.Title, Is.EqualTo("GitHub Projects Boards"));
            Assert.That(Retry.WhileFalse(() => !window.Properties.IsOffscreen.Value,
                TimeSpan.FromSeconds(5)).Result, Is.True, "The main window did not become visible.");
            Assert.That(window.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionScreen")), Is.Not.Null,
                "The WinUI content must expose the connection screen.");
            Assert.That(window.FindFirstDescendant(cf => cf.ByAutomationId("CheckConnectionButton")), Is.Not.Null,
                "The ordinary executable must expose the connection workflow.");
            window.Close();
            Assert.That(Retry.WhileFalse(() => process.HasExited, TimeSpan.FromSeconds(10)).Result,
                Is.True, "Closing the window did not terminate the application.");
            Assert.That(process.ExitCode, Is.Zero);
        }
        catch
        {
            TryCaptureFailure(window);
            throw;
        }
        finally
        {
            // Cleanup cannot substitute for the normal-close assertion.
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
        }
    }

    private static void TryCaptureFailure(Window? window)
    {
        var directory = Environment.GetEnvironmentVariable("GHPB_E2E_ARTIFACTS");
        if (window is null || string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"failure-{Guid.NewGuid():N}.png");
            using var image = Capture.Element(window);
            image.ToFile(path);
            TestContext.AddTestAttachment(path, "Application window at failure");
        }
        catch (Exception exception)
        {
            TestContext.Progress.WriteLine($"Window capture unavailable: {exception.GetType().Name}");
        }
    }
}
