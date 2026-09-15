using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    private static void SaveRows(Window w)
    {
        Invoke(w, "PrimaryButton"); Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("RowSettingsDialog")) is null);
    }
    [Test]
    public void RowViewClipboardEditingAndRealRestart()
    {
        using var f = new Fixture(); using var clipboard = new NativeClipboardScope();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, columns = true, itemCount = 3 }));
        f.Run(w =>
        {
            w.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Register(w, 2); OpenSaved(w);
            // The ordinary editor prepares R1/R3 for a noncontiguous filtered view.
            Edit(w, 0, "keep Z");
            if (Element(w, "ProjectItems").Patterns.Scroll.Pattern.VerticallyScrollable.Value) Scroll(w, 100);
            Edit(w, 2, "keep A");
            if (Element(w, "ProjectItems").Patterns.Scroll.Pattern.VerticallyScrollable.Value) Scroll(w, 0);
            ReorderColumns(w); Invoke(w, "GridRowSettings");
            WorkspaceUi.SelectCombo(w, "RowSort", 1); Element(w, "RowTitleFilter").AsTextBox().Text = "keep"; SaveRows(w);
            Assert.That(CellText(w, 0), Is.EqualTo("keep A")); Assert.That(CellText(w, 1), Is.EqualTo("keep Z"));
            Element(w, "GridCell0_1").Click(); Key(VirtualKeyShort.ESCAPE);
            NativeClipboardScope.WriteTestFormats("Done\tDone\nDone\tDone"); Invoke(w, "GridPaste");
            Wait(() => Durable(f).GetProperty("Fields").EnumerateArray().Count(x => x.GetProperty("Key").GetProperty("Kind").GetString() == "Select" && x.GetProperty("Change").ValueKind != JsonValueKind.Null) == 4);
            var keys = Durable(f).GetProperty("Fields").EnumerateArray().Where(x => x.GetProperty("Key").GetProperty("Kind").GetString() == "Select" && x.GetProperty("Change").ValueKind != JsonValueKind.Null).Select(x => x.GetProperty("Key"));
            Assert.That(keys.Select(k => k.GetProperty("NodeId").GetString() + "/" + k.GetProperty("FieldId").GetString()), Is.EquivalentTo(new[] { "P1-T3/P1C", "P1-T3/P1A", "P1-T1/P1C", "P1-T1/P1A" }));
            Assert.That(CellText(w, 0), Is.EqualTo("keep A"));
            Invoke(w, "GridRowSettings"); Invoke(w, "RowsReset"); SaveRows(w); Invoke(w, "GridUndo");
            Wait(() => Durable(f).GetProperty("Fields").EnumerateArray().Where(x => x.GetProperty("Key").GetProperty("Kind").GetString() == "Select").All(x => x.GetProperty("Change").ValueKind == JsonValueKind.Null));
            Invoke(w, "GridRowSettings"); Element(w, "RowTitleFilter").AsTextBox().Text = "keep"; SaveRows(w);
            OpenSaved(w, "Project 2"); Assert.That(Text(w, "RowViewStatus"), Does.Contain("表示 3")); OpenSaved(w);
            Assert.That(Text(w, "RowViewStatus"), Does.Contain("表示 2"));
            Invoke(w, "ReviewApplyButton");
            Assert.That(Text(w, "ApplyTargetCounts"), Does.Contain("表示 2")); Assert.That(Element(w, "ApplyIncludeHidden").AsCheckBox().IsChecked, Is.False); Invoke(w, "CloseButton");
        });
        var calls = f.Calls().Length;
        f.Run(w => { OpenSaved(w, profile: true); Assert.That(Text(w, "RowViewStatus"), Does.Contain("表示 2")); Assert.That(Text(w, "RowViewStatus"), Does.Contain("keep")); });
        Assert.That(f.Calls().Length, Is.EqualTo(calls)); Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }
    [TestCase(false), TestCase(true), Category("GridIme")]
    public void RowViewPhysicalDirectAndF2PendingTransition(bool f2)
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, columns = true, itemCount = 3 }));
        f.Run(w =>
        {
            w.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1);
            var cell = Element(w, "GridCell0_0").AsTextBox(); cell.Click(); Wait(() => cell.Properties.HasKeyboardFocus.Value);
            if (f2) Key(VirtualKeyShort.F2); Keyboard.TypeVirtualKeyCode(0x16);
            Key(VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_H, VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_G, VirtualKeyShort.KEY_O);
            Assert.That(cell.Text, Is.EqualTo("にほんご")); Key(VirtualKeyShort.SPACE); Assert.That(cell.Text, Is.EqualTo("日本語"));
            Invoke(w, "GridRowSettings");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("RowViewStatus"))?.Name.Contains("IME変換中") == true
                || w.FindFirstDescendant(cf => cf.ByAutomationId("RowSettingsDialog")) is not null);
            if (w.FindFirstDescendant(cf => cf.ByAutomationId("RowSettingsDialog")) is null) Invoke(w, "GridRowSettings");
            Element(w, "RowTitleFilter").AsTextBox().Text = "Issue 2"; SaveRows(w);
            Wait(() => Durable(f).GetProperty("Fields").EnumerateArray().Any(x => x.GetProperty("Key").GetProperty("Kind").GetString() == "Title" && x.GetProperty("Buffer").GetString() == "日本語"));
            Assert.That(Durable(f).GetProperty("Fields").EnumerateArray().Where(x => x.GetProperty("Key").GetProperty("Kind").GetString() == "Title").All(x => x.GetProperty("Change").ValueKind == JsonValueKind.Null), Is.True);
            Invoke(w, "GridRowSettings"); Invoke(w, "RowsReset"); SaveRows(w); Assert.That(CellText(w, 0), Is.EqualTo("日本語"));
        });
        Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }
}
