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
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
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
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fake)!, "..", "..", "..", "..", ".."));
        var prepare = new ProcessStartInfo("pwsh") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(repo, "scripts", "Start-Evaluation.ps1"), "-NoBuild", "-Configuration", new DirectoryInfo(Path.GetDirectoryName(fake)!).Parent!.Name, "-PrepareOnly", "-DataRoot", root }) prepare.ArgumentList.Add(argument);
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN" }) prepare.Environment.Remove(name);
        using (var fixture = Process.Start(prepare)!) {
            Assert.That(fixture.WaitForExit(30000), Is.True, "Evaluation preparation must finish.");
            Assert.That(fixture.ExitCode, Is.Zero);
        }
        var isolatedGh = Path.Combine(root, "fake-gh", "bin", "gh.exe");
        Assert.That(File.ReadAllBytes(Path.Combine(root, "fake-gh", "bin", "GhProjectsBoards.Tests.dll")),
            Is.EqualTo(File.ReadAllBytes(Path.ChangeExtension(fake, ".dll"))), "Prepared data must use the selected fixture binary.");
        using var dpi = new DesktopDpiScope();
        using var automation = new UIA3Automation();
        for (var launch = 0; launch < 2; launch++)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, WindowStyle = ProcessWindowStyle.Hidden };
            start.Environment["GHPB_DATA_ROOT"] = root;
            start.Environment["GH_CONFIG_DIR"] = Path.Combine(root, "fake-gh");
            start.Environment["PATH"] = Path.GetDirectoryName(isolatedGh) + Path.PathSeparator + start.Environment["PATH"];
            foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN" }) start.Environment.Remove(name);
            using var process = Process.Start(start)!;
            using var app = Application.Attach(process.Id);
            Window? window = null;
            try
            {
                window = app.GetMainWindow(automation, TimeSpan.FromSeconds(20));
                Assert.That(window, Is.Not.Null);
                WinUiProcess.AssertRuntime(process);
                window!.Patterns.Transform.Pattern.Resize(1600, 960);
                Assert.That(Find(window!, "PlanGhPath").AsTextBox().Text, Is.EqualTo(isolatedGh));
                Find(window!, "PlanHost").AsTextBox().Text = "github.com";
                Find(window!, "PlanConnect").AsButton().Invoke();
                if (launch == 0)
                {
                    Wait(() => window!.FindFirstDescendant(c => c.ByAutomationId("AvailableProjects"))?.AsListBox().Items.Length == 2);
                    Find(window!, "AvailableProjects").AsListBox().Select(0);
                }
                Wait(() => Find(window!, "OpenProjectName").Properties.Name.ValueOrDefault == "第2027.04版");
                Wait(() => Find(window!, "PlanTasks").FindAllDescendants().Any(e => e.Properties.AutomationId.ValueOrDefault == "PlanCell1_Title" && e.AsTextBox().Text == (launch == 0 ? "R01 受注データの外部連携" : "日本語の計画") && !e.Properties.IsOffscreen.ValueOrDefault && !e.BoundingRectangle.IsEmpty));
                Wait(() => Find(window!, "PlanUnpublished").Properties.Name.ValueOrDefault == $"未発行 {launch} タスク");
                Assert.That(window!.FindAllDescendants().Any(e => e.Properties.Name.ValueOrDefault == "開始日"), Is.True);
                Find(window!, "PlanProjectPicker").AsButton().Invoke();
                Wait(() => Find(window!, "RegisteredProjects").AsListBox().Items.Length == 1 &&
                    Find(window!, "RegisteredProjects").FindAllDescendants().Any(e =>
                        (e.Properties.Name.ValueOrDefault ?? "").Contains("第2027.04版") && !e.Properties.IsOffscreen.ValueOrDefault));
                Assert.That(window.FindFirstDescendant(c => c.ByAutomationId("RegistrationUrl")), Is.Null);
                Find(window!, "RegisteredProjects").Focus();
                FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
                Wait(() => Find(window, "PlanCell1_Predecessors").BoundingRectangle.Right <= Find(window, "PlanGanttHorizontal").BoundingRectangle.Left);
                var chartBounds = Find(window, "PlanGanttHorizontal").BoundingRectangle;
                Assert.That(chartBounds.Width, Is.GreaterThanOrEqualTo(280));
                var firstRow = Find(window, "PlanCell1_Title").BoundingRectangle;
                var rowPitch = Find(window, "PlanRowId2").BoundingRectangle.Top - Find(window, "PlanRowId1").BoundingRectangle.Top;
                var scale = GetDpiForWindow(window.Properties.NativeWindowHandle.Value) / 96d;
                Assert.That(rowPitch / scale, Is.EqualTo(28).Within(1));
                File.WriteAllText(Path.Combine(root, $"layout-{launch}.json"), JsonSerializer.Serialize(new {
                    window = window.BoundingRectangle, chart = chartBounds, rowPitch, scale,
                    predecessorRight = Find(window, "PlanCell1_Predecessors").BoundingRectangle.Right,
                    title = firstRow, start = Find(window, "PlanCell1_Start").BoundingRectangle,
                    end = Find(window, "PlanCell1_End").BoundingRectangle,
                    calendar = Find(window, "PlanTimelineMonths").Properties.Name.ValueOrDefault }));
                using (var capture = Capture.Element(window)) capture.ToFile(Path.Combine(root, $"workspace-{launch}.png"));
                Find(window, "PlanShowSettings").AsButton().Invoke();
                Wait(() => window.FindFirstDescendant(c => c.ByAutomationId("PlanMapEstimate")) is not null);
                using (var capture = Capture.Element(window)) capture.ToFile(Path.Combine(root, $"settings-{launch}.png"));
                Find(window, "PlanShowTasks").Patterns.SelectionItem.Pattern.Select();
                Wait(() => window.FindFirstDescendant(c => c.ByAutomationId("PlanCell1_Title")) is not null);
                var editor = Find(window, "PlanCell1_Title").AsTextBox();
                editor.Focus();
                Wait(() => editor.Properties.HasKeyboardFocus.ValueOrDefault);
                if (launch == 0) editor.Text = "日本語の計画";
                // Public UIA Unicode input exercises focused-editor/normal-close
                // lifetime and durable reopen, not Microsoft IME composition.
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
        Assert.That(File.Exists(Path.Combine(root, "PlanningEditor", "v1", "workspace.json")), Is.True);
        Assert.That(File.ReadAllLines(Path.Combine(root, "fake-gh", "calls.jsonl"))
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
