using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using NUnit.Framework;
using Application = FlaUI.Core.Application;
namespace GhProjectsBoards.E2E.Tests;

[TestFixture, Category("E2E"), Category("GridIme"), NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class PlanSheetImeTests
{
    [TestCase("direct"), TestCase("f2"), TestCase("cancel"), TestCase("cancel-f2"), TestCase("reconvert"), TestCase("reconvert-f2")]
    public void PlanSheetPhysicalJapaneseImeKeepsConversionSeparateFromCellCommit(string scenario)
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_E2E") != "1")
            Assert.Ignore("Run Test-E2E.ps1 -Filter TestCategory=GridIme on the PMO Japanese IME desktop.");
        var root = Path.Combine(Environment.GetEnvironmentVariable("GHPB_E2E_ARTIFACTS")!, "plan-ime-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "scenario.json"), """{"planEditor":true,"workspace":true}""");
        File.WriteAllText(Path.Combine(root, "plan-state.json"), JsonSerializer.Serialize(new {
            issues = Enumerable.Range(1, 3).Select(i => new { row = new { identity = "I" + i, title = "Issue " + i, repository = "acme/repo" }, body = "", added = true }),
            nextId = 4
        }));
        using var dpi = new DesktopDpiScope();
        using var automation = new UIA3Automation();
        var executable = Environment.GetEnvironmentVariable("GHPB_E2E_APP_PATH")!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, WindowStyle = ProcessWindowStyle.Hidden };
        start.Environment["GHPB_DATA_ROOT"] = Path.Combine(root, "data"); start.Environment["GH_CONFIG_DIR"] = root;
        using var process = Process.Start(start)!;
        using var app = Application.Attach(process.Id);
        Window? window = null;
        try
        {
            window = app.GetMainWindow(automation, TimeSpan.FromSeconds(20));
            Assert.That(window, Is.Not.Null); WinUiProcess.AssertRuntime(process);
            AutomationElement Find(string id) => window!.FindFirstDescendant(c => c.ByAutomationId(id)) ?? throw new AssertionException("Missing control: " + id);
            string Count() => Find("PlanUnpublished").Properties.Name.ValueOrDefault ?? "";
            Find("PlanGhPath").AsTextBox().Text = Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!;
            Find("PlanConnect").AsButton().Invoke();
            Wait(() => Find("AvailableProjects").AsListBox().Items.Length == 2);
            Find("AvailableProjects").AsListBox().Select(0);
            Wait(() => window!.FindFirstDescendant(c => c.ByAutomationId("PlanCell1_Title")) is { IsOffscreen: false });
            Keyboard.TypeVirtualKeyCode(0x12); window!.SetForeground();
            var cell = Find("PlanCell1_Title").AsTextBox(); cell.Click();
            Wait(() => cell.Properties.HasKeyboardFocus.ValueOrDefault);
            if (scenario.EndsWith("f2", StringComparison.Ordinal)) Key(VirtualKeyShort.F2);
            Keyboard.TypeVirtualKeyCode(0x16);
            Key(VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_H, VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_G, VirtualKeyShort.KEY_O);
            Assert.That(cell.Text, Is.EqualTo("にほんご"), "Physical input must appear exactly once.");
            Assert.That(Count(), Is.EqualTo("未発行 0 タスク"));
            Assert.That(SavedTitle(root), Is.EqualTo("Issue 1"));
            Key(VirtualKeyShort.SPACE); Assert.That(cell.Text, Is.EqualTo("日本語"));
            if (scenario.StartsWith("cancel", StringComparison.Ordinal))
            {
                Key(VirtualKeyShort.ESCAPE); Assert.That(cell.Text, Is.EqualTo("にほんご"));
                Key(VirtualKeyShort.ESCAPE, VirtualKeyShort.ESCAPE);
                Wait(() => cell.Text == "Issue 1");
                Assert.That(Count(), Is.EqualTo("未発行 0 タスク"));
                Assert.That(SavedTitle(root), Is.EqualTo("Issue 1"));
            }
            else
            {
                Key(VirtualKeyShort.RETURN);
                Assert.That(cell.Properties.HasKeyboardFocus.ValueOrDefault, Is.True);
                Assert.That(Count(), Is.EqualTo("未発行 0 タスク"));
                Assert.That(SavedTitle(root), Is.EqualTo("Issue 1"));
                Key(VirtualKeyShort.RETURN);
                Wait(() => Count() == "1 未発行のタスク" && Find("PlanCell2_Title").Properties.HasKeyboardFocus.ValueOrDefault);
                Assert.That(SavedTitle(root), Is.EqualTo("日本語"));
                if (scenario.StartsWith("reconvert", StringComparison.Ordinal))
                {
                    cell.Click(); Wait(() => cell.Properties.HasKeyboardFocus.ValueOrDefault);
                    if (scenario.EndsWith("f2", StringComparison.Ordinal)) Key(VirtualKeyShort.F2);
                    Keyboard.TypeVirtualKeyCode(0x1C); FlaUI.Core.Input.Wait.UntilInputIsProcessed(); Key(VirtualKeyShort.SPACE);
                    var alternative = cell.Text; Assert.That(alternative, Is.Not.Empty.And.Not.EqualTo("日本語"));
                    Key(VirtualKeyShort.RETURN);
                    Assert.That(cell.Properties.HasKeyboardFocus.ValueOrDefault, Is.True);
                    Assert.That(SavedTitle(root), Is.EqualTo("日本語"));
                    Key(VirtualKeyShort.RETURN);
                    Wait(() => Find("PlanCell2_Title").Properties.HasKeyboardFocus.ValueOrDefault && SavedTitle(root) == alternative);
                }
            }
            using (var capture = Capture.Element(window)) capture.ToFile(Path.Combine(root, "ime-" + scenario + ".png"));
            window.Close();
            Assert.That(process.WaitForExit(10000), Is.True, "Normal close must preserve confirmed input.");
            Assert.That(process.ExitCode, Is.Zero);
            Assert.That(File.ReadAllLines(Path.Combine(root, "calls.jsonl")).Any(line => JsonDocument.Parse(line).RootElement.GetProperty("mutation").GetBoolean()), Is.False);
        }
        catch (Exception ex)
        {
            TestContext.Error.WriteLine(ex.ToString());
            if (process.HasExited) TestContext.Error.WriteLine($"Product process exit: {process.ExitCode} (0x{process.ExitCode:X8})");
            if (window is not null && !process.HasExited)
                try { using var capture = Capture.Element(window); capture.ToFile(Path.Combine(root, "failure.png")); }
                catch (Exception captureError) { TestContext.Error.WriteLine("Failure capture unavailable: " + captureError.Message); }
            throw;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
        }
    }
    private static string SavedTitle(string root)
    {
        var path = Directory.GetFiles(Path.Combine(root, "data", "PlanningEditor", "v1"), "*.json").Single(p => Path.GetFileName(p) != "workspace.json");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("document").GetProperty("state").GetProperty("rows").EnumerateArray()
            .Single(row => row.GetProperty("identity").GetString() == "I1").GetProperty("title").GetString()!;
    }
    private static void Key(params VirtualKeyShort[] keys)
    {
        foreach (var key in keys) { Keyboard.Type(key); FlaUI.Core.Input.Wait.UntilInputIsProcessed(); Thread.Sleep(60); }
    }
    private static void Wait(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            try { if (predicate()) return; } catch (AssertionException) { }
            Thread.Sleep(40);
        }
        Assert.Fail("The ordinary plan sheet did not reach the expected state.");
    }
}
