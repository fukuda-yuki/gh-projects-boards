using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    private static void SaveColumns(Window w)
    {
        Invoke(w, "PrimaryButton");
        Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ColumnSettingsDialog")) is null);
    }
    private static void ReorderColumns(Window w)
    {
        Invoke(w, "GridColumns");
        Element(w, "ColumnVisible-P1B").AsCheckBox().IsChecked = false;
        Invoke(w, "ColumnUp-P1C"); Invoke(w, "ColumnUp-P1C"); SaveColumns(w);
    }
    private static JsonElement[] Preferences(Fixture f, string project) => Durable(f).GetProperty("ColumnPreferences").EnumerateArray()
        .Single(p => p.GetProperty("ProjectId").GetString() == project).GetProperty("Columns").EnumerateArray().ToArray();
    [Test]
    public void ProjectColumnsClipboardOriginalUndoAndActualRestart()
    {
        using var f = new Fixture(); using var clipboard = new NativeClipboardScope();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, columns = true, itemCount = 3 }));
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Register(w, 2); OpenSaved(w);
            ReorderColumns(w);
            Wait(() => Preferences(f, "P1")[1].GetProperty("Id").GetProperty("FieldId").GetString() == "P1C");
            Element(w, "GridCell0_1").Click(); Key(VirtualKeyShort.ESCAPE);
            NativeClipboardScope.WriteTestFormats("Done\tDone"); Invoke(w, "GridPaste");
            Wait(() => Durable(f).GetProperty("Fields").EnumerateArray().Count(x => x.GetProperty("Change").ValueKind != JsonValueKind.Null) == 2);
            var changed = Durable(f).GetProperty("Fields").EnumerateArray().Where(x => x.GetProperty("Change").ValueKind != JsonValueKind.Null);
            Assert.That(changed.Select(x => x.GetProperty("Key").GetProperty("FieldId").GetString()), Is.EquivalentTo(new[] { "P1C", "P1A" }));
            Element(w, "GridCell0_0").Click(); using (Keyboard.Pressing(VirtualKeyShort.SHIFT)) Key(VirtualKeyShort.RIGHT, VirtualKeyShort.RIGHT);
            Invoke(w, "GridCopy"); Assert.That(NativeClipboardScope.ReadText(), Is.EqualTo("Issue 1\tDone\tDone"));
            var range = Text(w, "GridSelection"); Invoke(w, "GridColumns");
            (Element(w, "ColumnWidth-Title").FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)) ?? throw new AssertionException("Missing native width input")).AsTextBox().Text = "380";
            SaveColumns(w); Assert.That(Text(w, "GridSelection"), Is.EqualTo(range));
            Invoke(w, "GridColumns"); Invoke(w, "ColumnsReset"); SaveColumns(w); Invoke(w, "GridUndo");
            Wait(() => Durable(f).GetProperty("Fields").EnumerateArray().All(x => x.GetProperty("Change").ValueKind == JsonValueKind.Null));
            ReorderColumns(w);
            OpenSaved(w, "Project 2"); Invoke(w, "GridColumns"); Element(w, "ColumnVisible-P2A").AsCheckBox().IsChecked = false; SaveColumns(w);
            OpenSaved(w); Assert.That(Preferences(f, "P1")[1].GetProperty("Id").GetProperty("FieldId").GetString(), Is.EqualTo("P1C"));
            Capture(w, f.Root, "project-columns");
        });
        var calls = f.Calls().Length;
        f.Run(w =>
        {
            OpenSaved(w, profile: true); Invoke(w, "GridColumns"); Assert.That(Element(w, "ColumnVisible-P1B").AsCheckBox().IsChecked, Is.False); Invoke(w, "CloseButton");
            OpenSaved(w, "Project 2"); Invoke(w, "GridColumns"); Assert.That(Element(w, "ColumnVisible-P2A").AsCheckBox().IsChecked, Is.False); Invoke(w, "CloseButton");
        });
        Assert.That(f.Calls().Length, Is.EqualTo(calls)); Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }
    [TestCase(false), TestCase(true), Category("GridIme")]
    public void ProjectColumnsPhysicalDirectAndF2PendingTransition(bool f2)
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, columns = true, itemCount = 3 }));
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1);
            var cell = Element(w, "GridCell0_0").AsTextBox(); cell.Click(); Wait(() => cell.Properties.HasKeyboardFocus.Value);
            if (f2) Key(VirtualKeyShort.F2); Keyboard.TypeVirtualKeyCode(0x16);
            Key(VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_H, VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_G, VirtualKeyShort.KEY_O);
            Assert.That(cell.Text, Is.EqualTo("にほんご")); Key(VirtualKeyShort.SPACE); Assert.That(cell.Text, Is.EqualTo("日本語"));
            Invoke(w, "GridColumns");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ColumnTransitionStatus"))?.Name.Contains("IME変換中") == true
                || w.FindFirstDescendant(cf => cf.ByAutomationId("ColumnSettingsDialog")) is not null);
            var held = w.FindFirstDescendant(cf => cf.ByAutomationId("ColumnSettingsDialog")) is null;
            TestContext.Out.WriteLine($"Native composition transition: f2={f2}; held before natural focus completion={held}");
            // Invoking the neutral control naturally ends composition through the native focus path.
            // Do not send an extra Enter: it could commit the pending cell after composition ended.
            if (held) Invoke(w, "GridColumns");
            Element(w, "ColumnVisible-P1B").AsCheckBox().IsChecked = false;
            Invoke(w, "ColumnUp-P1C"); Invoke(w, "ColumnUp-P1C"); SaveColumns(w);
            Assert.That(CellText(w, 0), Is.EqualTo("日本語"));
            Wait(() => Durable(f).GetProperty("Fields").EnumerateArray().Any(x => x.GetProperty("Key").GetProperty("Kind").GetString() == "Title" && x.GetProperty("Buffer").GetString() == "日本語"));
            Assert.That(Durable(f).GetProperty("Fields").EnumerateArray().Where(x => x.GetProperty("Key").GetProperty("Kind").GetString() == "Title").All(x => x.GetProperty("Change").ValueKind == JsonValueKind.Null), Is.True);
        });
        f.Run(w => { OpenSaved(w, profile: true); Assert.That(CellText(w, 0), Is.EqualTo("日本語")); });
        Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }
}
