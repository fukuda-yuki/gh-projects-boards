using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using NUnit.Framework;
using Application = FlaUI.Core.Application;
namespace GhProjectsBoards.E2E.Tests;

// #202 Gate 1 probe. TableView 2.5.4-experimental exposes rows but no cell or editor UIA content
// (cell peers have no properties and the owned editor is absent from the UIA tree), so cells are
// reached by physical pointer input at the Title column and the editor text is recorded as screen
// evidence. Behavioral assertions use the saved document, the unpublished count and row focus.
[TestFixture, Category("E2E"), Category("GridImeTableView"), NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class PlanSheetTableViewImeTests
{
    [TestCase("direct"), TestCase("f2"), TestCase("cancel"), TestCase("cancel-f2"), TestCase("reconvert"), TestCase("reconvert-f2")]
    public void PhysicalJapaneseImeKeepsConversionSeparateFromPlanSessionCommit(string scenario)
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_E2E") != "1")
            Assert.Ignore("Run Test-E2E.ps1 -Filter TestCategory=GridImeTableView on the PMO Japanese IME desktop.");
        var root = Path.Combine(Environment.GetEnvironmentVariable("GHPB_E2E_ARTIFACTS")!, "tableview-ime-" + scenario + "-" + Guid.NewGuid().ToString("N"));
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
        start.Environment["GHPB_DATA_ROOT"] = Path.Combine(root, "data");
        start.Environment["GH_CONFIG_DIR"] = root;
        start.Environment["GHPB_PLAN_RENDERER"] = "tableview";
        using var process = Process.Start(start)!;
        using var app = Application.Attach(process.Id);
        Window? window = null;
        var step = 0;
        var log = new List<string>();
        try
        {
            window = app.GetMainWindow(automation, TimeSpan.FromSeconds(20));
            Assert.That(window, Is.Not.Null); WinUiProcess.AssertRuntime(process);
            AutomationElement Find(string id) => window!.FindFirstDescendant(c => c.ByAutomationId(id)) ?? throw new AssertionException("Missing control: " + id);
            string Count() => Find("PlanUnpublished").Properties.Name.ValueOrDefault ?? "";
            AutomationElement[] Rows() => Find("PlanTableView").FindAllDescendants(c => c.ByClassName("Microsoft.UI.Xaml.Controls.Tabular.TableViewRow"))
                .OrderBy(r => r.BoundingRectangle.Top).ToArray();
            Point Title(int row)
            {
                var header = Find("PlanTableView").FindAllDescendants(c => c.ByName("タスク名")).First(e => e.Properties.ClassName.ValueOrDefault == "NamedContainerAutomationPeer");
                var bounds = Rows()[row].BoundingRectangle;
                return new Point(header.BoundingRectangle.Left + 40, bounds.Top + bounds.Height / 2);
            }
            bool FocusInRow(int row) => Rows()[row].Properties.HasKeyboardFocus.ValueOrDefault;
            // The owned editor is invisible to UIA; the realized editing template makes the row taller.
            var baseHeight = 0;
            bool Editing() => Rows()[0].BoundingRectangle.Height > baseHeight;
            void Shot(string what)
            {
                var rows = Rows();
                log.Add($"{++step:00} {what}: count='{Count()}' saved='{SavedTitle(root)}' focusRow={Array.FindIndex(rows, r => r.Properties.HasKeyboardFocus.ValueOrDefault)} row0Height={rows[0].BoundingRectangle.Height}");
                using var capture = Capture.Element(window!); capture.ToFile(Path.Combine(root, $"step-{step:00}-{what}.png"));
            }
            Find("PlanGhPath").AsTextBox().Text = Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!;
            Find("PlanConnect").AsButton().Invoke();
            Wait(() => Find("AvailableProjects").AsListBox().Items.Length == 2);
            Find("AvailableProjects").AsListBox().Select(0);
            Wait(() => Rows().Length >= 3 && !Rows()[0].IsOffscreen);
            Keyboard.TypeVirtualKeyCode(0x12); window!.SetForeground();
            var original = SavedTitle(root);
            Assert.That(original, Is.EqualTo("Issue 1"));
            Mouse.Click(Title(0));
            Wait(() => FocusInRow(0));
            baseHeight = Rows()[0].BoundingRectangle.Height;
            Shot("selected");
            if (scenario.EndsWith("f2", StringComparison.Ordinal)) { Key(VirtualKeyShort.F2); Shot("f2"); }
            Keyboard.TypeVirtualKeyCode(0x16);
            Key(VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_H, VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_G, VirtualKeyShort.KEY_O);
            Shot("composing");
            Assert.That(Editing(), Is.True, "Physical typing must reach an editor (direct entry starts editing; F2 opens it).");
            Assert.That(Count(), Is.EqualTo("未発行 0 タスク"));
            Assert.That(SavedTitle(root), Is.EqualTo(original));
            Key(VirtualKeyShort.SPACE);
            Shot("converted");
            if (scenario.StartsWith("cancel", StringComparison.Ordinal))
            {
                Key(VirtualKeyShort.ESCAPE); Shot("conversion-cancelled");
                Assert.That(Editing(), Is.True, "Escape during conversion must cancel only the conversion.");
                Key(VirtualKeyShort.ESCAPE, VirtualKeyShort.ESCAPE);
                Thread.Sleep(500); Shot("edit-cancelled");
                Assert.That(Count(), Is.EqualTo("未発行 0 タスク"));
                Assert.That(SavedTitle(root), Is.EqualTo(original));
                Assert.That(FocusInRow(0), Is.True, "Cancelling the edit must keep the selected row.");
            }
            else
            {
                Key(VirtualKeyShort.RETURN);
                Thread.Sleep(300); Shot("ime-confirmed");
                Assert.That(Count(), Is.EqualTo("未発行 0 タスク"), "IME confirmation must not commit the cell.");
                Assert.That(SavedTitle(root), Is.EqualTo(original), "IME confirmation must not save the cell.");
                Assert.That(Editing() && !FocusInRow(1), Is.True, "IME confirmation must not end the edit or move to the next row.");
                Key(VirtualKeyShort.RETURN);
                Wait(() => Count() == "1 未発行のタスク" && SavedTitle(root) == "日本語");
                Shot("committed");
                Assert.That(FocusInRow(1), Is.True, "The second Enter must move to the next row.");
                if (scenario.StartsWith("reconvert", StringComparison.Ordinal))
                {
                    Mouse.Click(Title(0)); Wait(() => FocusInRow(0));
                    if (scenario.EndsWith("f2", StringComparison.Ordinal)) Key(VirtualKeyShort.F2);
                    else Mouse.DoubleClick(Title(0));
                    Thread.Sleep(300); Shot("reconvert-edit");
                    Keyboard.TypeVirtualKeyCode(0x1C); FlaUI.Core.Input.Wait.UntilInputIsProcessed(); Key(VirtualKeyShort.SPACE);
                    Shot("reconverted");
                    Key(VirtualKeyShort.RETURN);
                    Thread.Sleep(300); Shot("reconvert-confirmed");
                    Assert.That(Count(), Is.EqualTo("1 未発行のタスク"));
                    Assert.That(SavedTitle(root), Is.EqualTo("日本語"), "Reconversion confirmation must not save the cell.");
                    Assert.That(Editing() && !FocusInRow(1), Is.True, "Reconversion confirmation must not end the edit.");
                    Key(VirtualKeyShort.RETURN);
                    Wait(() => SavedTitle(root) != "日本語");
                    Shot("reconvert-committed");
                    var alternative = SavedTitle(root);
                    Assert.That(alternative, Is.Not.Empty.And.Not.Contains("日本語日本語"));
                    Assert.That(FocusInRow(1), Is.True);
                    Assert.That(Count(), Is.EqualTo("1 未発行のタスク"));
                }
            }
            window.Close();
            Assert.That(process.WaitForExit(10000), Is.True, "Normal close must preserve confirmed input.");
            Assert.That(process.ExitCode, Is.Zero);
            Assert.That(File.ReadAllLines(Path.Combine(root, "calls.jsonl")).Any(line => {
                using var call = JsonDocument.Parse(line);
                return call.RootElement.GetProperty("mutation").GetBoolean();
            }), Is.False);
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
            File.WriteAllLines(Path.Combine(root, "steps.txt"), log);
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
        Assert.Fail("The ordinary TableView plan sheet did not reach the expected state.");
    }
}
