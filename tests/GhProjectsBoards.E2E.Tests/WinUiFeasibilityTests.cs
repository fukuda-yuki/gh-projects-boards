using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using NUnit.Framework;
using Application = FlaUI.Core.Application;

namespace GhProjectsBoards.E2E.Tests;

[TestFixture, Category("WinUiFeasibility")]
[NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class WinUiFeasibilityTests
{
    [TestCase("launch")]
    [TestCase("reference")]
    [TestCase("f2")]
    [TestCase("direct")]
    public void OrdinaryExecutableInputGate(string scenario)
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_WINUI_PROBE") != "1")
            Assert.Ignore("Use the opt-in WinUI feasibility script on an interactive Japanese IME desktop.");
        using var dpi = new DesktopDpiScope();
        var executable = Environment.GetEnvironmentVariable("GHPB_WINUI_PROBE_PATH")!;
        var artifacts = Environment.GetEnvironmentVariable("GHPB_WINUI_PROBE_ARTIFACTS")!;
        Assert.That(File.Exists(executable), Is.True);
        using var automation = new UIA3Automation();
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!
        })!;
        using var app = Application.Attach(process.Id);
        Window? window = null;
        var observations = new List<object>();
        try
        {
            window = app.GetMainWindow(automation, TimeSpan.FromSeconds(25));
            Assert.That(window, Is.Not.Null);
            window!.SetForeground();
            var grid = window!.FindFirstDescendant(cf => cf.ByAutomationId("CandidateGrid"));
            Assert.That(grid, Is.Not.Null);
            if (scenario != "launch")
            {
                var target = scenario == "reference"
                    ? window.FindFirstDescendant(cf => cf.ByAutomationId("ReferenceEditor"))
                    : grid!.FindFirstDescendant(cf => cf.ByName("試験データ 002"));
                Assert.That(target, Is.Not.Null, "Synthetic cell must be addressable through UIA.");
                Assert.That(Retry.WhileFalse(() => !target!.IsOffscreen && target.BoundingRectangle.Width > 0,
                    TimeSpan.FromSeconds(3)).Result, Is.True);
                target!.Click();
                FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                if (scenario == "reference")
                {
                    // WinUI includes the Header in the TextBox bounds; its center can hit the label.
                    var bounds = target.BoundingRectangle;
                    Mouse.Click(new System.Drawing.Point(bounds.Left + 30, bounds.Bottom - 15));
                    Assert.That(Retry.WhileFalse(() => target.Properties.HasKeyboardFocus.Value,
                        TimeSpan.FromSeconds(3)).Result, Is.True, "Click the editable area, not its header.");
                }
                AutomationElement? Editor() => scenario == "reference" ? target
                    : grid!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
                string Text() => Editor()?.Patterns.Text.Pattern.DocumentRange.GetText(-1) ?? "<no editor>";
                if (scenario != "reference") Assert.That(Editor(), Is.Null, "Selection must not start editing.");
                if (scenario == "f2")
                {
                    Keyboard.Type(VirtualKeyShort.F2);
                    Assert.That(Retry.WhileFalse(() => Editor() is not null, TimeSpan.FromSeconds(3)).Result, Is.True);
                }
                // Same public key-input stimulus as PR #21 ImeComparisonTests; no Unicode text injection.
                Keyboard.TypeVirtualKeyCode(0x16);
                FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                foreach (var key in new[] { VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_H,
                    VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_G, VirtualKeyShort.KEY_O })
                {
                    Keyboard.Type(key);
                    FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                    observations.Add(new { key = key.ToString(), text = Text(), focus = Editor()?.Properties.HasKeyboardFocus.Value });
                }
                using var capture = Capture.Element(window);
                var screenshot = Path.Combine(artifacts, scenario + ".png");
                capture.ToFile(screenshot);
                TestContext.AddTestAttachment(screenshot);
                Assert.That(Text(), Is.EqualTo("にほんご"), "The first syllable must survive direct physical-key IME input.");
            }
        }
        finally
        {
            if (window is not null && !process.HasExited)
            {
                try
                {
                    using var capture = Capture.Element(window);
                    capture.ToFile(Path.Combine(artifacts, scenario + "-final.png"));
                    File.WriteAllText(Path.Combine(artifacts, scenario + "-uia.txt"), string.Join(Environment.NewLine,
                        window.FindAllDescendants().Select(element => $"{element.Properties.ControlType.ValueOrDefault} | {element.Properties.Name.ValueOrDefault} | {element.Properties.AutomationId.ValueOrDefault} | {element.Properties.BoundingRectangle.ValueOrDefault} | focus={element.Properties.HasKeyboardFocus.ValueOrDefault}")));
                }
                catch (Exception error) { TestContext.Progress.WriteLine($"Optional diagnostics failed: {error.GetType().Name}"); }
            }
            try
            {
                var evidence = Path.Combine(artifacts, scenario + ".json");
                File.WriteAllText(evidence, JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
                TestContext.AddTestAttachment(evidence);
            }
            finally
            {
                try
                {
                    if (window is not null && !process.HasExited)
                    {
                        Keyboard.Type(VirtualKeyShort.ESCAPE);
                        FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                        Keyboard.TypeVirtualKeyCode(0x1A);
                        FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                        window.Close();
                        Assert.That(process.WaitForExit(10000), Is.True,
                            "Normal window close must terminate the actual UI process.");
                        Assert.That(process.ExitCode, Is.Zero);
                    }
                }
                finally
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
            }
        }
    }
}
