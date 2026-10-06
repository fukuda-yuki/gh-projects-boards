using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;
using NUnit.Framework;
using Application = FlaUI.Core.Application;

namespace GhProjectsBoards.E2E.Tests;

[TestFixture, Category("E2E"), NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class PlanningWorkspaceJourneyTests
{
    [Test]
    public void FreshWorkspaceConnectsOpensMappedTasksAndReopensAfterRestart()
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_E2E") != "1")
            Assert.Ignore("Run scripts/Test-E2E.ps1 for the ordinary executable journey.");
        var executable = Environment.GetEnvironmentVariable("GHPB_E2E_APP_PATH")!;
        var fake = Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!;
        Assert.That(File.Exists(executable) && File.Exists(fake), Is.True);
        var artifacts = Environment.GetEnvironmentVariable("GHPB_E2E_ARTIFACTS")!;
        var root = Path.Combine(artifacts, "planning-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true, workspace = true }));
        File.WriteAllText(Path.Combine(root, "plan-state.json"), JsonSerializer.Serialize(new {
            issues = new[] { new { row = new { identity = "I1", title = "設計", repository = "acme/repo",
                estimate = 8, remaining = 8, assignees = new[] { "U1" } }, body = "", added = true } }, nextId = 2
        }));
        using var dpi = new DesktopDpiScope();
        using var automation = new UIA3Automation();
        for (var launch = 0; launch < 2; launch++)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, WindowStyle = ProcessWindowStyle.Hidden };
            start.Environment["GHPB_DATA_ROOT"] = Path.Combine(root, "data");
            start.Environment["GH_CONFIG_DIR"] = root;
            using var process = Process.Start(start)!;
            using var app = Application.Attach(process.Id);
            Window? window = null;
            try
            {
                window = app.GetMainWindow(automation, TimeSpan.FromSeconds(20));
                Assert.That(window, Is.Not.Null);
                WinUiProcess.AssertRuntime(process);
                Find(window!, "PlanGhPath").AsTextBox().Text = fake;
                Find(window!, "PlanHost").AsTextBox().Text = "github.com";
                Find(window!, "PlanConnect").AsButton().Invoke();
                if (launch == 0)
                {
                    Wait(() => window!.FindFirstDescendant(c => c.ByAutomationId("AvailableProjects"))?.AsListBox().Items.Length == 2);
                    Find(window!, "AvailableProjects").AsListBox().Select(0);
                }
                Wait(() => Find(window!, "OpenProjectName").Properties.Name.ValueOrDefault == "開発計画");
                Wait(() => Find(window!, "PlanTasks").FindAllDescendants().Any(e => e.Properties.Name.ValueOrDefault == "設計" && !e.Properties.IsOffscreen.ValueOrDefault && !e.BoundingRectangle.IsEmpty));
                Assert.That(window!.FindAllDescendants().Any(e => e.Properties.Name.ValueOrDefault == "Start date"), Is.True);
                Wait(() => Find(window!, "RegisteredProjects").AsListBox().Items.Length == 1 &&
                    Find(window!, "RegisteredProjects").FindAllDescendants().Any(e =>
                        (e.Properties.Name.ValueOrDefault ?? "").Contains("開発計画") && !e.Properties.IsOffscreen.ValueOrDefault));
                Assert.That(window.FindFirstDescendant(c => c.ByAutomationId("RegistrationUrl")), Is.Null);
                using (var capture = Capture.Element(window)) capture.ToFile(Path.Combine(root, $"workspace-{launch}.png"));
                Find(window, "PlanShowSettings").AsButton().Invoke();
                Wait(() => window.FindFirstDescendant(c => c.ByAutomationId("PlanMapEstimate")) is not null);
                using (var capture = Capture.Element(window)) capture.ToFile(Path.Combine(root, $"settings-{launch}.png"));
                window.Close();
                Assert.That(process.WaitForExit(10000), Is.True, "Normal close must finish local storage.");
                Assert.That(process.ExitCode, Is.Zero);
            }
            catch (Exception ex)
            {
                TestContext.Error.WriteLine(ex.ToString());
                if (window is not null)
                    try { using var capture = Capture.Element(window); capture.ToFile(Path.Combine(root, $"failure-{launch}.png")); } catch { }
                throw;
            }
            finally
            {
                try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
                catch (InvalidOperationException) { /* Automation may already have released the process. */ }
            }
        }
        Assert.That(File.Exists(Path.Combine(root, "data", "PlanningEditor", "v1", "workspace.json")), Is.True);
        Assert.That(File.ReadAllLines(Path.Combine(root, "calls.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }
    private static AutomationElement Find(Window window, string id)
        => window.FindFirstDescendant(c => c.ByAutomationId(id)) ?? throw new AssertionException("Missing control: " + id);
    private static void Wait(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(25))
        {
            try { if (ready()) return; } catch (AssertionException) { }
            Thread.Sleep(50);
        }
        Assert.Fail("The ordinary UI did not reach the expected state within 25 seconds.");
    }
}
