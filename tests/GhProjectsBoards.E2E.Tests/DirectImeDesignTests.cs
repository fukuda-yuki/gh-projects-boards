using System.Diagnostics;
using System.IO;
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

[TestFixture, Category("DirectImeDesign"), NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class DirectImeDesignTests
{
    [TestCase("column", false, false)]
    [TestCase("column", true, false)]
    [TestCase("standard", false, false)]
    [TestCase("standard", true, false)]
    [TestCase("column", false, true)]
    [TestCase("standard", false, true)]
    public void SelectionEntryAndConfirmationKeepTheirBoundaries(string mode, bool f2, bool preenabled)
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_DIRECT_IME") != "1")
            Assert.Ignore("Use the opt-in direct IME design script on an interactive Japanese IME desktop.");
        using var dpi = new DesktopDpiScope();
        var output = Environment.GetEnvironmentVariable("GHPB_DIRECT_IME_ARTIFACTS")!;
        var executable = Environment.GetEnvironmentVariable("GHPB_WINUI_PROBE_PATH")!;
        var name = $"{mode}-{(f2 ? "f2" : "direct")}{(preenabled ? "-preenabled" : "")}";
        using var automation = new UIA3Automation();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.Environment["GHPB_IME_MODE"] = mode;
        start.Environment["GHPB_IME_TRACE"] = Path.Combine(output, name + "-events.json");
        using var process = Process.Start(start)!;
        using var app = Application.Attach(process.Id);
        Window? window = null;
        try
        {
            window = app.GetMainWindow(automation, TimeSpan.FromSeconds(25));
            Assert.That(window, Is.Not.Null);
            window!.SetForeground();
            if (preenabled)
            {
                var reference = window.FindFirstDescendant(cf => cf.ByAutomationId("ProbeReference"))?.AsTextBox()
                    ?? throw new AssertionException("The ordinary reference TextBox must be exposed through UIA.");
                reference.Focus();
                Keyboard.TypeVirtualKeyCode(0x16);
                Keyboard.Type(VirtualKeyShort.KEY_N);
                Keyboard.Type(VirtualKeyShort.KEY_I);
                FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                Assert.That(reference.Patterns.Text.Pattern.DocumentRange.GetText(-1), Is.EqualTo("に"),
                    "Verify Japanese composition before selecting the read-only cell.");
                Keyboard.Type(VirtualKeyShort.ESCAPE);
                FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            }
            var editor = window.FindFirstDescendant(cf => cf.ByAutomationId("Entry2"))?.AsTextBox()
                ?? throw new AssertionException("The second read-only entry cell must be exposed through UIA.");
            editor.Click();
            // UIA focus is part of selecting the read-only native control, not entering edit mode.
            editor.Focus();
            Assert.That(Retry.WhileFalse(() => editor.Properties.HasKeyboardFocus.Value, TimeSpan.FromSeconds(3)).Result, Is.True);
            string State() => window.FindFirstDescendant(cf => cf.ByAutomationId("ProbeState")).Name;
            string Text() => editor.Patterns.Text.Pattern.DocumentRange.GetText(-1);
            Assert.That(editor.Patterns.Value.Pattern.IsReadOnly.Value, Is.True, "Selection must remain read-only.");
            Assert.That(State(), Does.Contain("state=Selected;commits=0;value=試験データ 002"));
            if (f2) Keyboard.Type(VirtualKeyShort.F2);
            if (!preenabled) Keyboard.TypeVirtualKeyCode(0x16);
            FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            foreach (var key in new[] { VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_H,
                VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_G, VirtualKeyShort.KEY_O })
            {
                Keyboard.Type(key);
                FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                TestContext.Progress.WriteLine($"{name}: {key}: {Text()}");
            }
            Assert.That(Text(), Is.EqualTo("にほんご"), "Physical direct entry must retain the first syllable exactly once.");
            Assert.That(State(), Does.Contain("state=Editing;commits=0;value=試験データ 002"));
            Keyboard.Type(VirtualKeyShort.SPACE);
            FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            Assert.That(Text(), Is.EqualTo("日本語"));
            Keyboard.Type(VirtualKeyShort.RETURN);
            FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            Assert.That(editor.Properties.HasKeyboardFocus.Value, Is.True, "IME Enter must not move the cell.");
            Assert.That(State(), Does.Contain("commits=0;value=試験データ 002"), "IME Enter must not commit the cell.");
            Keyboard.Type(VirtualKeyShort.RETURN);
            FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            Assert.That(State(), Does.Contain("row=3;state=Selected;commits=1"));
            Assert.That(Text(), Is.EqualTo("日本語"));
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
                        var screenshot = Path.Combine(output, name + ".png");
                        capture.ToFile(screenshot);
                        TestContext.AddTestAttachment(screenshot);
                    }
                    catch (Exception error) { TestContext.Progress.WriteLine("Capture unavailable: " + error.GetType().Name); }
                    Keyboard.Type(VirtualKeyShort.ESCAPE);
                    FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                    Keyboard.TypeVirtualKeyCode(0x1A);
                    FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                    window.Close();
                    Assert.That(process.WaitForExit(10000), Is.True, "Normal close must terminate the UI process.");
                    Assert.That(process.ExitCode, Is.Zero);
                    TestContext.AddTestAttachment(start.Environment["GHPB_IME_TRACE"]!);
                }
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
    }
}
