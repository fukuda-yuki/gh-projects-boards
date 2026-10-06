using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;
using NUnit.Framework;
using Application = FlaUI.Core.Application;

namespace GhProjectsBoards.E2E.Tests;

[TestFixture, Category("E2E"), NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class PlanningPublishJourneyTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void ExplicitPublishSurvivesRestartWithoutResendingOrDuplicating(bool interrupted)
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_E2E") != "1") Assert.Ignore("Run scripts/Test-E2E.ps1.");
        var executable = Environment.GetEnvironmentVariable("GHPB_E2E_APP_PATH")!;
        var fake = Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!;
        var root = Path.Combine(Environment.GetEnvironmentVariable("GHPB_E2E_ARTIFACTS")!, "publish-" + interrupted + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Scenario(interrupted);
        File.WriteAllText(Path.Combine(root, "plan-state.json"), JsonSerializer.Serialize(new {
            issues = new[] { new { row = new { identity = "I1", title = "設計", repository = "acme/repo" }, body = "", added = true } }, nextId = 2, mutationBatches = 0 }));
        using var dpi = new DesktopDpiScope();
        using var automation = new UIA3Automation();
        int completedBatches = -1;
        for (var launch = 0; launch < 3; launch++)
        {
            if (launch > 0) Scenario(false);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, WindowStyle = ProcessWindowStyle.Hidden };
            start.Environment["GHPB_DATA_ROOT"] = Path.Combine(root, "data"); start.Environment["GH_CONFIG_DIR"] = root;
            using var process = Process.Start(start)!;
            using var app = Application.Attach(process.Id);
            Window? window = null;
            try
            {
                window = app.GetMainWindow(automation, TimeSpan.FromSeconds(20));
                WinUiProcess.AssertRuntime(process);
                window!.Patterns.Transform.Pattern.Resize(1600, 960);
                Find("PlanGhPath").AsTextBox().Text = fake;
                Find("PlanConnect").AsButton().Invoke();
                if (launch == 0)
                {
                    Wait(() => Find("AvailableProjects").AsListBox().Items.Length == 2);
                    Find("AvailableProjects").AsListBox().Select(0);
                }
                Wait(() => Find("PlanCell1_Title").AsTextBox().Text == (launch == 0 || interrupted ? "設計" : "設計の更新"));
                if (launch == 0)
                {
                    var input = Find(interrupted ? "PlanCell0_Title" : "PlanCell1_Title").AsTextBox();
                    input.Focus(); input.Text = interrupted ? "追加した計画" : "設計の更新";
                }
                Find("PlanPublish").AsButton().Invoke();
                Wait(() => !Find("PlanPublishConfirm").Properties.IsOffscreen.ValueOrDefault);
                if (launch == 0) Assert.That(State().GetProperty("mutationBatches").GetInt32(), Is.Zero);
                using (var capture = Capture.Element(window)) capture.ToFile(Path.Combine(root, $"review-{launch}.png"));
                Find("PlanPublishConfirm").AsButton().Invoke();
                if (launch == 0 && interrupted)
                    Wait(() => !string.IsNullOrEmpty(Find("PlanError").Properties.Name.ValueOrDefault) && Find("PlanPublishConfirm").IsEnabled);
                else
                {
                    Wait(() => Find("PlanUnpublished").Properties.Name.ValueOrDefault == "未発行 0 タスク" && Find("PlanPublishConfirm").IsEnabled);
                    var state = State();
                    Assert.That(state.GetProperty("issues").GetArrayLength(), Is.EqualTo(interrupted ? 2 : 1));
                    if (completedBatches >= 0) Assert.That(state.GetProperty("mutationBatches").GetInt32(), Is.EqualTo(completedBatches), "Verified writes must not be sent on restart.");
                    completedBatches = state.GetProperty("mutationBatches").GetInt32();
                }
                window.Close();
                Assert.That(process.WaitForExit(10000), Is.True, "Normal close must persist outcomes.");
                Assert.That(process.ExitCode, Is.Zero);
            }
            catch
            {
                if (window is not null) try { using var capture = Capture.Element(window); capture.ToFile(Path.Combine(root, $"failure-{launch}.png")); } catch { }
                throw;
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
            AutomationElement Find(string id) => window!.FindFirstDescendant(c => c.ByAutomationId(id)) ?? throw new AssertionException("Missing: " + id);
        }
        void Scenario(bool fail) => File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true, workspace = true, planFault = fail ? "after-and-read" : "" }));
        JsonElement State()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "plan-state.json")));
            return document.RootElement.Clone();
        }
    }
    private static void Wait(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            try { if (predicate()) return; } catch (AssertionException) { }
            Thread.Sleep(50);
        }
        Assert.Fail("The ordinary app did not reach the expected state.");
    }
}
