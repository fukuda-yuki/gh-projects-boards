using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using NUnit.Framework;
using Application = FlaUI.Core.Application;

namespace GhProjectsBoards.E2E.Tests;

[TestFixture, Category("ReadyInput"), NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class ReadyInputTests
{
    [TestCase("selection")]
    [TestCase("draft")]
    [TestCase("reference")]
    [TestCase("f2")]
    [TestCase("mouse")]
    [TestCase("keyboard")]
    [TestCase("mouse-preenabled")]
    [TestCase("keyboard-preenabled")]
    [TestCase("cancel-direct")]
    [TestCase("cancel-f2")]
    [TestCase("reconvert-direct")]
    [TestCase("reconvert-f2")]
    public void NativeEditorKeepsCellAndImeBoundaries(string scenario)
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_READY_INPUT") != "1")
            Assert.Ignore("Use Test-ReadyInput.ps1 on an interactive Japanese IME desktop.");
        using var dpi = new DesktopDpiScope();
        var output = Environment.GetEnvironmentVariable("GHPB_READY_ARTIFACTS")!;
        var executable = Environment.GetEnvironmentVariable("GHPB_WINUI_PROBE_PATH")!;
        using var automation = new UIA3Automation();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.Environment["GHPB_IME_MODE"] = "ready";
        start.Environment["GHPB_IME_TRACE"] = Path.Combine(output, scenario + "-events.json");
        using var process = Process.Start(start)!;
        using var app = Application.Attach(process.Id);
        Window? window = null;
        try
        {
            window = app.GetMainWindow(automation, TimeSpan.FromSeconds(25));
            Assert.That(window, Is.Not.Null);
            window!.Focus();
            window!.SetForeground();
            FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            AutomationElement Element(string id) => window.FindFirstDescendant(cf => cf.ByAutomationId(id))
                ?? throw new AssertionException("Missing ordinary UI element: " + id);
            TextBox Cell(int index) => Element($"ReadyCell{index}").AsTextBox()!;
            string Model(int index) => Element($"Committed{index}").Name;
            string State() => Element("ReadySelection").Name;
            string Text(TextBox editor) => editor.Patterns.Text.Pattern.DocumentRange.GetText(-1);
            void Focused(TextBox editor) => Assert.That(Retry.WhileFalse(() => editor.Properties.HasKeyboardFocus.Value,
                TimeSpan.FromSeconds(3)).Result, Is.True, "Ordinary mouse/keyboard action must focus the selected cell.");
            var reference = Element("ReadyReference").AsTextBox()!;
            // Release the Windows foreground lock using a test-only modifier before activating
            // the ordinary window. No editor receives focus or an edit-start key through UIA.
            Keyboard.TypeVirtualKeyCode(0x12);
            Assert.That(Retry.WhileFalse(() =>
            {
                window.SetForeground();
                return GetForegroundWindow() == window.Properties.NativeWindowHandle.Value;
            }, TimeSpan.FromSeconds(5)).Result, Is.True, "The probe window must be foreground before mouse/key stimulus.");
            Assert.That(Retry.WhileFalse(() => Cell(2).BoundingRectangle.Width > 0 && !Cell(2).IsOffscreen,
                TimeSpan.FromSeconds(3)).Result, Is.True);
            if (scenario == "draft")
            {
                Cell(2).Click();
                Focused(Cell(2));
                Keyboard.TypeVirtualKeyCode(0x1A);
                using (Keyboard.Pressing(VirtualKeyShort.CONTROL)) Type(VirtualKeyShort.KEY_A);
                Assert.That(State(), Does.EndWith("state=Selected"), "A selection shortcut is not character input.");
                Type(VirtualKeyShort.KEY_1);
                Assert.That(Text(Cell(2)), Is.EqualTo("1"));
                Assert.That(State(), Does.EndWith("state=Editing"));
                Assert.That(Model(2), Is.EqualTo("既存値 2"));
                Type(VirtualKeyShort.ESCAPE);
                Assert.That(Text(Cell(2)), Is.EqualTo("既存値 2"));
                Assert.That(Model(2), Is.EqualTo("既存値 2"));
                Assert.That(State(), Does.EndWith("state=Selected"));
                File.WriteAllText(Path.Combine(output, scenario + "-body.txt"), "Digit edit startup and cell cancellation passed before close.");
                return;
            }
            if (scenario == "reference" || scenario.EndsWith("preenabled", StringComparison.Ordinal))
            {
                var bounds = reference.BoundingRectangle;
                Mouse.Click(new System.Drawing.Point(bounds.Left + 30, bounds.Bottom - 15));
                Focused(reference);
                Keyboard.TypeVirtualKeyCode(0x16);
                if (scenario != "reference")
                {
                    Type(VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I);
                    Assert.That(Text(reference), Is.EqualTo("に"), "Positive IME control before selecting the target.");
                    Type(VirtualKeyShort.ESCAPE);
                }
            }
            if (scenario == "selection")
            {
                Cell(0).Click();
                Focused(Cell(0));
                Type(VirtualKeyShort.DOWN);
                Focused(Cell(2));
                Assert.That(State(), Is.EqualTo("anchor=2;current=2;state=Selected"));
                using (Keyboard.Pressing(VirtualKeyShort.SHIFT)) Type(VirtualKeyShort.RIGHT);
                Focused(Cell(3));
                Assert.That(State(), Is.EqualTo("anchor=2;current=3;state=Selected"));
                Assert.That(Cell(3).Patterns.Text.Pattern.GetSelection().Single().GetText(-1), Is.EqualTo("既存値 3"),
                    "Shift navigation must select a cell, not move the native string caret.");
                for (var index = 0; index < 6; index++)
                {
                    Assert.That(Element($"Member{index}").Name, Is.EqualTo(index is 2 or 3 ? "選択" : "未選択"));
                    Assert.That(Text(Cell(index)), Is.EqualTo($"既存値 {index}"));
                    Assert.That(Model(index), Is.EqualTo($"既存値 {index}"));
                }
                using (Keyboard.Pressing(VirtualKeyShort.SHIFT)) Type(VirtualKeyShort.DOWN);
                Focused(Cell(5));
                for (var index = 0; index < 6; index++)
                    Assert.That(Element($"Member{index}").Name, Is.EqualTo(index >= 2 ? "選択" : "未選択"));
                using (Keyboard.Pressing(VirtualKeyShort.SHIFT)) Cell(0).Click();
                Focused(Cell(0));
                for (var index = 0; index < 6; index++)
                {
                    Assert.That(Element($"Member{index}").Name, Is.EqualTo(index is 0 or 2 ? "選択" : "未選択"));
                    Assert.That(Text(Cell(index)), Is.EqualTo($"既存値 {index}"));
                    Assert.That(Model(index), Is.EqualTo($"既存値 {index}"));
                }
                File.WriteAllText(Path.Combine(output, scenario + "-body.txt"), "Selection, range focus and all six editor/model values passed before close.");
                return;
            }
            var editor = scenario == "reference" ? reference : Cell(2);
            if (scenario != "reference")
            {
                if (scenario.StartsWith("keyboard", StringComparison.Ordinal))
                {
                    Cell(0).Click();
                    Focused(Cell(0));
                    Type(VirtualKeyShort.DOWN);
                }
                else editor.Click();
                Focused(editor);
                Assert.That(Text(editor), Is.EqualTo("既存値 2"));
                Assert.That(Model(2), Is.EqualTo("既存値 2"));
                Assert.That(State(), Is.EqualTo("anchor=2;current=2;state=Selected"));
                if (scenario == "f2" || scenario.EndsWith("-f2", StringComparison.Ordinal)) Type(VirtualKeyShort.F2);
                if (!scenario.EndsWith("preenabled", StringComparison.Ordinal)) Keyboard.TypeVirtualKeyCode(0x16);
            }
            foreach (var key in new[] { VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_H,
                VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_G, VirtualKeyShort.KEY_O })
            {
                Type(key);
                TestContext.Progress.WriteLine($"{scenario}: {key}: {Text(editor)}");
            }
            Assert.That(Text(editor), Is.EqualTo("にほんご"), "Keep every physical IME input exactly once.");
            if (scenario == "reference")
            {
                File.WriteAllText(Path.Combine(output, scenario + "-body.txt"), "Physical IME reference input passed before close.");
                return;
            }
            Assert.That(Model(2), Is.EqualTo("既存値 2"));
            Assert.That(State(), Does.EndWith("state=Editing"));
            Type(VirtualKeyShort.SPACE);
            Assert.That(Text(editor), Is.EqualTo("日本語"));
            if (scenario.StartsWith("cancel", StringComparison.Ordinal))
            {
                Type(VirtualKeyShort.ESCAPE);
                Assert.That(Text(editor), Is.EqualTo("にほんご"), "Candidate cancellation returns to composition.");
                Focused(editor);
                Assert.That(Model(2), Is.EqualTo("既存値 2"));
                Assert.That(State(), Does.EndWith("state=Editing"));
                Type(VirtualKeyShort.ESCAPE);
                Focused(editor);
                Assert.That(Model(2), Is.EqualTo("既存値 2"));
                Assert.That(State(), Does.EndWith("state=Editing"), "IME cancellation must not cancel the cell.");
                Type(VirtualKeyShort.ESCAPE);
                Assert.That(Text(editor), Is.EqualTo("既存値 2"));
                Assert.That(Model(2), Is.EqualTo("既存値 2"));
                Assert.That(State(), Does.EndWith("state=Selected"));
                File.WriteAllText(Path.Combine(output, scenario + "-body.txt"), "Candidate, composition and cell cancellation boundaries passed before close.");
                return;
            }
            Type(VirtualKeyShort.RETURN);
            Focused(editor);
            Assert.That(Model(2), Is.EqualTo("既存値 2"), "IME Enter cannot commit the model.");
            Type(VirtualKeyShort.RETURN);
            Focused(Cell(4));
            Assert.That(Model(2), Is.EqualTo("日本語"));
            Assert.That(Text(editor), Is.EqualTo("日本語"));
            Assert.That(State(), Is.EqualTo("anchor=4;current=4;state=Selected"));
            if (scenario.StartsWith("reconvert", StringComparison.Ordinal))
            {
                editor.Click();
                Focused(editor);
                if (scenario.EndsWith("-f2", StringComparison.Ordinal)) Type(VirtualKeyShort.F2);
                Keyboard.TypeVirtualKeyCode(0x1C);
                FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                // The observed list has two entries; Space then Down cycles back to the
                // original candidate. Select the next entry once and verify its real text.
                Type(VirtualKeyShort.SPACE);
                var alternative = Text(editor);
                Assert.That(alternative, Is.Not.Empty.And.Not.EqualTo("日本語"), "Physical reconversion/candidate keys must change the selected existing text.");
                Assert.That(Model(2), Is.EqualTo("日本語"));
                Type(VirtualKeyShort.RETURN);
                Focused(editor);
                Assert.That(Model(2), Is.EqualTo("日本語"), "Reconversion Enter must not commit the cell.");
                Type(VirtualKeyShort.RETURN);
                Focused(Cell(4));
                Assert.That(Model(2), Is.EqualTo(alternative));
                Assert.That(Text(editor), Is.EqualTo(alternative));
            }
            File.WriteAllText(Path.Combine(output, scenario + "-body.txt"), "Input, conversion, separate model, both Enter boundaries and next-cell focus passed before close.");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, scenario + "-body.txt"), error.ToString());
            throw;
        }
        finally
        {
            try
            {
                if (window is not null && !process.HasExited)
                {
                    try
                    {
                        using var capture = Capture.Element(window);
                        capture.ToFile(Path.Combine(output, scenario + ".png"));
                    }
                    catch (Exception error) { TestContext.Progress.WriteLine("Optional capture: " + error.GetType().Name); }
                    Type(VirtualKeyShort.ESCAPE);
                    Keyboard.TypeVirtualKeyCode(0x1A);
                    FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                    window.Close();
                }
                Assert.That(process.WaitForExit(10000), Is.True, "Ordinary close must exit the owned process.");
                Assert.That(process.ExitCode, Is.Zero);
                TestContext.Progress.WriteLine($"Owned process {process.Id} exited normally.");
                Assert.That(File.Exists(start.Environment["GHPB_IME_TRACE"]), Is.True, "Required event evidence must exist after normal close.");
                {
                    using var events = JsonDocument.Parse(File.ReadAllText(start.Environment["GHPB_IME_TRACE"]!));
                    var commitCount = events.RootElement.EnumerateArray().Count(item => item.GetProperty("phase").GetString() == "commit");
                    // Supplement the actual model/focus assertions with exact transaction count.
                    if (File.ReadAllText(Path.Combine(output, scenario + "-body.txt")).Contains("passed before close", StringComparison.Ordinal))
                    {
                        var expectedCommits = scenario is "selection" or "draft" or "reference" || scenario.StartsWith("cancel", StringComparison.Ordinal)
                            ? 0 : scenario.StartsWith("reconvert", StringComparison.Ordinal) ? 2 : 1;
                        Assert.That(commitCount, Is.EqualTo(expectedCommits));
                    }
                }
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
    }

    private static void Type(params VirtualKeyShort[] keys)
    {
        foreach (var key in keys)
        {
            Keyboard.Type(key);
            FlaUI.Core.Input.Wait.UntilInputIsProcessed();
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
