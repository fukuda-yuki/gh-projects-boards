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
        // A plain dotnet test must not unexpectedly take over the user's desktop.
        if (Environment.GetEnvironmentVariable("GHPB_RUN_E2E") != "1")
        {
            Assert.Ignore("Desktop E2E is opt-in. Run scripts/Test-E2E.ps1 on an unlocked Windows desktop.");
        }

        Assert.That(Environment.UserInteractive, Is.True,
            "An interactive Windows desktop is required; this does not verify that it is unlocked.");
        var executable = Environment.GetEnvironmentVariable("GHPB_E2E_APP_PATH");
        Assert.That(!string.IsNullOrWhiteSpace(executable) && File.Exists(executable), Is.True,
            "Build the app and set GHPB_E2E_APP_PATH, or use scripts/Test-E2E.ps1.");
        executable = Path.GetFullPath(executable!);

        using var automation = new UIA3Automation();
        using var application = Application.Launch(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        });
        Window? window = null;
        try
        {
            window = application.GetMainWindow(automation, TimeSpan.FromSeconds(20));
            Assert.That(window, Is.Not.Null, "The ordinary executable did not expose a main window.");
            Assert.That(window!.Properties.AutomationId.Value, Is.EqualTo("MainWindow"));
            Assert.That(window.Title, Is.EqualTo("GitHub Projects Boards"));
            var visible = Retry.WhileFalse(
                () => !window.Properties.IsOffscreen.Value,
                timeout: TimeSpan.FromSeconds(5));
            Assert.That(visible.Result, Is.True, "The main window did not become visible.");

            // Use the actual UI close action, not process termination as the assertion.
            window.Close();
            var exited = Retry.WhileFalse(
                () => application.HasExited,
                timeout: TimeSpan.FromSeconds(10));
            Assert.That(exited.Result, Is.True, "Closing the window did not terminate the application.");
            Assert.That(application.ExitCode, Is.Zero);
        }
        catch
        {
            TryCaptureFailure(window);
            throw;
        }
        finally
        {
            // Cleanup happens after assertions and only targets the process we launched.
            try
            {
                if (!application.HasExited)
                {
                    application.Close();
                }
            }
            catch (Exception exception)
            {
                TestContext.Progress.WriteLine($"Cleanup required forced termination: {exception.GetType().Name}");
                application.Kill();
            }
        }
    }

    private static void TryCaptureFailure(Window? window)
    {
        var directory = Environment.GetEnvironmentVariable("GHPB_E2E_ARTIFACTS");
        if (window is null || string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"failure-{Guid.NewGuid():N}.png");
            // Capture only the app's rectangle; never fall back to a whole-desktop image.
            using var image = Capture.Element(window);
            image.ToFile(path);
            TestContext.AddTestAttachment(path, "Application window at failure");
        }
        catch (Exception exception)
        {
            // Diagnostics must not hide the original test failure.
            TestContext.Progress.WriteLine($"Window capture unavailable: {exception.GetType().Name}");
        }
    }
}
