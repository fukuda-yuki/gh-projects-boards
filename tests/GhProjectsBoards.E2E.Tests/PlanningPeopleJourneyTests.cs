using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;
using NUnit.Framework;
using Application = FlaUI.Core.Application;
namespace GhProjectsBoards.E2E.Tests;

[TestFixture, Category("E2E"), NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class PlanningPeopleJourneyTests
{
    [Test]
    public void PeopleReviewShowsTwentyPeopleAtClientSizeAndRetainsAllowanceAfterRestart()
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_E2E") != "1") Assert.Ignore("Run scripts/Test-E2E.ps1.");
        var executable = Environment.GetEnvironmentVariable("GHPB_E2E_APP_PATH")!;
        var fake = Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!;
        var root = Path.Combine(Environment.GetEnvironmentVariable("GHPB_E2E_ARTIFACTS")!, "people-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        File.WriteAllText(Path.Combine(root, "plan-state.json"), JsonSerializer.Serialize(new {
            issues = Enumerable.Range(1, 1000).Select(i => new { row = new { identity = "I" + i, title = "計画レビュー " + i,
                repository = "acme/repo", estimate = 4, remaining = 4, actual = 0,
                assignees = new[] { "U" + ((i - 1) % 20 + 1) },
                predecessors = i % 10 == 1 ? Array.Empty<string>() : new[] { "I" + (i - 1) } }, body = "", added = true }), nextId = 1001 }));
        using var dpi = new DesktopDpiScope(); using var automation = new UIA3Automation();
        for (var launch = 0; launch < 2; launch++) {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, WindowStyle = ProcessWindowStyle.Hidden };
            start.Environment["GHPB_DATA_ROOT"] = Path.Combine(root, "data"); start.Environment["GH_CONFIG_DIR"] = root;
            using var process = Process.Start(start)!; using var app = Application.Attach(process.Id);
            Window? window = null;
            try {
                window = app.GetMainWindow(automation, TimeSpan.FromSeconds(20)); WinUiProcess.AssertRuntime(process);
                var handle = window!.Properties.NativeWindowHandle.Value; var factor = GetDpiForWindow(handle) / 96d;
                GetClientRect(handle, out var client); var outer = window.BoundingRectangle;
                window.Patterns.Transform.Pattern.Resize(1280 * factor + outer.Width - client.Right, 720 * factor + outer.Height - client.Bottom);
                Find("PlanGhPath").AsTextBox().Text = fake; Find("PlanConnect").AsButton().Invoke();
                if (launch == 0) { Wait(() => Find("AvailableProjects").AsListBox().Items.Length == 2); Find("AvailableProjects").AsListBox().Select(0); }
                Wait(() => Find("PlanCell1_Title").AsTextBox().Text == "計画レビュー 1");
                Find("PlanShowPeople").Patterns.SelectionItem.Pattern.Select();
                Wait(() => !Find("PeopleAllowance_U20").Properties.IsOffscreen.ValueOrDefault);
                GetClientRect(handle, out client);
                Assert.That(client.Right / factor, Is.EqualTo(1280).Within(1)); Assert.That(client.Bottom / factor, Is.EqualTo(720).Within(1));
                var viewport = Find("PeopleRows").BoundingRectangle;
                foreach (var i in Enumerable.Range(1, 20)) {
                    var bounds = Find("PeopleExpand_U" + i).BoundingRectangle;
                    Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(viewport.Top)); Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(viewport.Bottom));
                }
                using (var capture = Capture.Element(window)) capture.ToFile(Path.Combine(root, $"day-{launch}.png"));
                if (launch == 0) { var allowance = Find("PeopleAllowance_U1").AsTextBox(); allowance.Focus(); allowance.Text = "80"; }
                else Assert.That(Find("PeopleAllowance_U1").AsTextBox().Text, Is.EqualTo("80"));
                Find("PeopleScale").AsComboBox().Select(1);
                Wait(() => Find("PeoplePeriod_0").Properties.Name.ValueOrDefault?.Contains("週") == true);
                using (var capture = Capture.Element(window)) capture.ToFile(Path.Combine(root, $"week-{launch}.png"));
                File.WriteAllText(Path.Combine(root, $"layout-{launch}.json"), JsonSerializer.Serialize(new { clientWidth = client.Right / factor, clientHeight = client.Bottom / factor, rows = 20, viewport }));
                window.Close(); Assert.That(process.WaitForExit(15000), Is.True); Assert.That(process.ExitCode, Is.Zero);
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
            AutomationElement Find(string id) => window!.FindFirstDescendant(c => c.ByAutomationId(id)) ?? throw new AssertionException("Missing control: " + id);
        }
        Assert.That(File.ReadAllLines(Path.Combine(root, "calls.jsonl")).Select(line => JsonDocument.Parse(line).RootElement.Clone()).Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }
    private static void Wait(Func<bool> ready) {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(30)) { try { if (ready()) return; } catch (AssertionException) { } Thread.Sleep(50); }
        Assert.Fail("Observable application state did not arrive.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct ClientRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint handle, out ClientRect rect);
}
