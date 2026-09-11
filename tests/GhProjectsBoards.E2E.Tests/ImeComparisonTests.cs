using System.IO;
using System.Text.Json;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

[TestFixture, Category("ImeComparison"), Explicit("Diagnostic comparison; a failing control is retained as evidence.")]
[NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class ImeComparisonTests
{
    public static IEnumerable<TestCaseData> StartupCases()
    {
        foreach (var scenario in new[] { "prototype", "standard", "textbox" })
            foreach (var f2 in scenario == "textbox" ? new[] { false } : new[] { false, true })
                for (var sample = 1; sample <= 3; sample++)
                    yield return new TestCaseData(scenario, f2, sample);
    }

    [TestCaseSource(nameof(StartupCases))]
    public void StartupRetainsFirstSyllable(string scenario, bool f2, int sample)
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_REAL_IME") != "1")
            Assert.Ignore("Requires Microsoft Japanese IME selected in alphanumeric mode.");
        using var app = new GridAppDriver();
        AutomationElement cell;
        if (scenario == "prototype")
        {
            app.SelectCell(2, 0);
            cell = app.Cell(2, 0);
        }
        else
        {
            app.OpenStandardComparison();
            cell = scenario == "textbox" ? app.Element("ReferenceTextBox") : app.Element("StandardGrid").AsGrid().Rows[1].Cells[0];
            cell.Click();
        }
        AutomationElement? Editor() => scenario == "textbox" ? cell
            : cell.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        string Text() => Editor()?.Patterns.Text.Pattern.DocumentRange.GetText(-1) ?? "<no editor>";
        var observations = new List<object>();
        try
        {
            if (scenario != "textbox") Assert.That(Editor(), Is.Null, "The direct-start cell must not already be editing.");
            if (f2)
            {
                Keyboard.Type(VirtualKeyShort.F2);
                GridAppDriver.Wait(() => Editor() is not null);
            }
            Keyboard.TypeVirtualKeyCode(0x16);
            FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            observations.Add(new { step = "IME enabled", text = Text() });
            foreach (var key in new[] { VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_H,
                VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_G, VirtualKeyShort.KEY_O })
            {
                Keyboard.Type(key);
                FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                observations.Add(new { step = key.ToString(), text = Text() });
            }
            app.Capture($"comparison-{scenario}-f2-{f2}-{sample}");
            Assert.That(Text(), Is.EqualTo("にほんご"), "Physical n+i must retain the first syllable.");
            app.AssertNoGhCalls();
        }
        finally
        {
            var path = Path.Combine(app.Artifacts, $"comparison-{scenario}-f2-{f2}-{sample}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { scenario, f2, sample, observations }, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.AddTestAttachment(path);
            for (var cancel = 0; cancel < 3 && Editor() is not null; cancel++)
            {
                Keyboard.Type(VirtualKeyShort.ESCAPE);
                FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            }
            Keyboard.TypeVirtualKeyCode(0x1A);
            FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            app.CloseNormally();
        }
    }
}
