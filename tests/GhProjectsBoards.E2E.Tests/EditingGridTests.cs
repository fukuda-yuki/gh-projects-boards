using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    private static void OpenSaved(Window w, string project = "Project 1", bool profile = false)
    {
        Invoke(w, "ProjectsPageButton");
        if (profile) { Wait(() => Element(w, "SavedProfiles").AsComboBox().Items.Length > 0); Element(w, "SavedProfiles").AsComboBox().Select(0); }
        Wait(() => w.FindFirstDescendant(cf => cf.ByName(project)) is not null);
        w.FindFirstDescendant(cf => cf.ByName(project))!.Click();
        Wait(() => Text(w, "ProjectSummary").StartsWith(project));
        Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("GridCell0_0")) is not null);
    }
    private static void Register(Window w, int number)
    {
        AddUrl(w, number); Invoke(w, "RegisterProjectButton");
        Wait(() => Text(w, "ProjectSummary").StartsWith("Project " + number));
    }
    private static void Key(params VirtualKeyShort[] keys)
    { foreach (var key in keys) { Keyboard.Type(key); FlaUI.Core.Input.Wait.UntilInputIsProcessed(); Thread.Sleep(100); } }
    private static string CellText(Window w, int row, int col = 0) => Element(w, $"GridCell{row}_{col}").AsTextBox().Text;
    private static void Edit(Window w, int row, string text)
    { var c = Element(w, $"GridCell{row}_0").AsTextBox(); c.Click(); c.Text = text; Key(VirtualKeyShort.RETURN); }
    private static void Scroll(Window w, double percent) { Element(w, "ProjectItems").Patterns.Scroll.Pattern.SetScrollPercent(-1, percent); Thread.Sleep(200); }
    private static JsonElement Durable(Fixture f)
    {
        var file = Directory.GetFiles(Path.Combine(f.Data, "Drafts"), "*.json").Single();
        return JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
    }
    [Test]
    public void GridEditsScrolledRowsSharedTitlesRestartBuffersAndUndo()
    {
        using var f = new Fixture();
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Register(w, 2); OpenSaved(w);
            Edit(w, 0, "Shared local"); Element(w, "GridCell0_1").AsComboBox().Select("Done");
            Scroll(w, 100); Edit(w, 100, "Last row");
            Element(w, "GridCell100_1").AsComboBox().Select("Done");
            Wait(() => Text(w, "DraftStatus").Contains("変更フィールド 4"));
            OpenSaved(w, "Project 2");
            Assert.That(CellText(w, 0), Is.EqualTo("Shared local"));
            Assert.That(Element(w, "GridCell0_1").AsComboBox().SelectedItem!.Text, Is.EqualTo("Todo"));
            Scroll(w, 100); Assert.That(CellText(w, 100), Is.EqualTo("Last row"));
            Assert.That(Element(w, "GridCell100_1").AsComboBox().SelectedItem!.Text, Is.EqualTo("Todo"));
            Element(w, "GridCell100_0").Click(); Set(w, "GridCell100_0", "");
            Wait(() => Text(w, "DraftStatus").Contains("保存済み"));
        });
        var calls = f.Calls().Length;
        f.Run(w =>
        {
            OpenSaved(w, profile: true); Assert.That(CellText(w, 0), Is.EqualTo("Shared local"));
            Scroll(w, 100); Assert.That(CellText(w, 100), Is.Empty);
            Assert.That(Text(w, "DraftStatus"), Does.Contain("変更フィールド 4"));
            Element(w, "GridCell100_0").Click(); Key(VirtualKeyShort.ESCAPE);
            Assert.That(CellText(w, 100), Is.EqualTo("Last row"));
            Invoke(w, "GridUndo"); Wait(() => Text(w, "DraftStatus").Contains("変更フィールド 3"));
            Assert.That(f.Calls().Length, Is.EqualTo(calls));
            Capture(w, f.Root, "editing-restart");
        });
        Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }
    [Test]
    public void GridRectangleCopyPasteValidationClearAndOperationUndo()
    {
        using var f = new Fixture(); using var clipboard = new NativeClipboardScope();
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1);
            Element(w, "GridCell0_0").Click();
            NativeClipboardScope.WriteTestFormats("First\tDone\r\nSecond\t\r\n"); Invoke(w, "GridPaste");
            Wait(() => Text(w, "DraftStatus").Contains("変更フィールド 3"));
            Assert.That(CellText(w, 0), Is.EqualTo("First")); Assert.That(CellText(w, 1), Is.EqualTo("Second"));
            Invoke(w, "GridUndo"); Wait(() => Text(w, "DraftStatus").Contains("変更フィールド 0"));
            Element(w, "GridCell0_0").Click();
            using (Keyboard.Pressing(VirtualKeyShort.SHIFT)) Key(VirtualKeyShort.RIGHT, VirtualKeyShort.DOWN);
            Assert.That(Text(w, "GridSelection"), Does.Contain("行 1 列 1 ～ 行 2 列 2"));
            Invoke(w, "GridCopy"); Assert.That(NativeClipboardScope.ReadText(), Is.EqualTo("Issue 1\tTodo\r\nIssue 2\tTodo"));
            NativeClipboardScope.WriteTestFormats("Changed\tMissing"); Invoke(w, "GridPaste");
            Wait(() => Text(w, "DraftStatus").Contains("行 1 列 2")); Assert.That(CellText(w, 0), Is.EqualTo("Issue 1"));
            Element(w, "GridCell0_1").Click(); Key(VirtualKeyShort.ESCAPE); Invoke(w, "GridClear");
            Wait(() => Text(w, "DraftStatus").Contains("変更フィールド 1"));
            Invoke(w, "GridUndo"); Wait(() => Text(w, "DraftStatus").Contains("変更フィールド 0"));
        });
        Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }
    [Test]
    public void FailedDraftSaveCancelsNavigationAndCloseUntilRetry()
    {
        using var f = new Fixture();
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Register(w, 2); OpenSaved(w);
            Wait(() => Text(w, "DraftStatus").Contains("保存済み"));
            using (var gate = new FileStream(Path.Combine(f.Data, "Drafts", ".writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Element(w, "GridCell0_0").Click(); Set(w, "GridCell0_0", "Recover after failed save");
                Wait(() => Text(w, "DraftStatus").Contains("保存失敗"));
                w.FindFirstDescendant(cf => cf.ByName("Project 2"))!.Click();
                Wait(() => Text(w, "RegistrationStatus").Contains("保存失敗"));
                Assert.That(Text(w, "ProjectSummary"), Does.StartWith("Project 1"));
                w.Close(); Thread.Sleep(400);
                Assert.That(CellText(w, 0), Is.EqualTo("Recover after failed save"));
            }
            Invoke(w, "GridSave"); Wait(() => Text(w, "DraftStatus").Contains("保存済み"));
            OpenSaved(w, "Project 2"); Assert.That(CellText(w, 0), Is.EqualTo("Recover after failed save"));
        });
        f.Run(w => { OpenSaved(w, profile: true); Assert.That(CellText(w, 0), Is.EqualTo("Recover after failed save")); });
    }
    [Test]
    public void RefreshCannotReplaceCacheWhenEditingStartsDuringRetrieval()
    {
        using var f = new Fixture();
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1);
            var file = Directory.GetFiles(f.Data, "*.json").Single(); var baseline = File.ReadAllBytes(file);
            f.Write(delay: 500); Invoke(w, "RefreshProjectButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("取得中"));
            Element(w, "GridCell0_0").Click(); Set(w, "GridCell0_0", "During refresh");
            Wait(() => Text(w, "RegistrationStatus").Contains("置換していません"));
            Assert.That(File.ReadAllBytes(file), Is.EqualTo(baseline));
            Assert.That(CellText(w, 0), Is.EqualTo("During refresh"));
            var calls = f.Calls().Length; Invoke(w, "RefreshProjectButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("更新は停止")); Assert.That(f.Calls().Length, Is.EqualTo(calls));
        });
    }
    [Test]
    public void UnregistrationRequiresDecisionAndPreservesSurvivingSharedDraft()
    {
        using var f = new Fixture();
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Register(w, 2); OpenSaved(w);
            Edit(w, 0, "Shared survives"); Element(w, "GridCell0_1").AsComboBox().Select("Done");
            Invoke(w, "UnregisterProjectButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("LocalUnregisterConfirmation")) is not null);
            Element(w, "CloseButton").AsButton().Invoke();
            Assert.That(Directory.GetFiles(f.Data, "*.json"), Has.Length.EqualTo(2));
            Invoke(w, "UnregisterProjectButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("SecondaryButton")) is not null);
            Element(w, "SecondaryButton").AsButton().Invoke();
            Wait(() => Directory.GetFiles(f.Data, "*.json").Length == 1);
            OpenSaved(w, "Project 2"); Assert.That(CellText(w, 0), Is.EqualTo("Shared survives"));
            Assert.That(Element(w, "GridCell0_1").AsComboBox().SelectedItem!.Text, Is.EqualTo("Todo"));
        });
        f.Run(w => { OpenSaved(w, "Project 2", profile: true); Assert.That(CellText(w, 0), Is.EqualTo("Shared survives")); });
        Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }
    [Test]
    public void DeliberateProcessInterruptionRecoversAcknowledgedTransactionAndUndo()
    {
        using var f = new Fixture(); using var clipboard = new NativeClipboardScope();
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Element(w, "GridCell0_0").Click();
            NativeClipboardScope.WriteTestFormats("Atomic A\tDone\nAtomic B\tDone"); Invoke(w, "GridPaste");
            Wait(() => Text(w, "DraftStatus").Contains("変更フィールド 4") && Text(w, "DraftStatus").Contains("保存済み"));
        }, interrupt: true);
        f.Run(w =>
        {
            OpenSaved(w, profile: true); Assert.That(CellText(w, 0), Is.EqualTo("Atomic A")); Assert.That(CellText(w, 1), Is.EqualTo("Atomic B"));
            Assert.That(Text(w, "DraftStatus"), Does.Contain("変更フィールド 4"));
            Invoke(w, "GridUndo"); Wait(() => Text(w, "DraftStatus").Contains("変更フィールド 0"));
        });
    }
    [TestCase("direct"), TestCase("f2"), TestCase("cancel"), TestCase("cancel-f2"), TestCase("reconvert"), TestCase("reconvert-f2"), Category("GridIme")]
    public void RegisteredGridPhysicalJapaneseIme(string scenario)
    {
        using var f = new Fixture();
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1);
            w.SetForeground(); var cell = Element(w, "GridCell0_0").AsTextBox(); cell.Click();
            Wait(() => cell.Properties.HasKeyboardFocus.Value);
            if (scenario.EndsWith("f2", StringComparison.Ordinal)) Key(VirtualKeyShort.F2);
            Keyboard.TypeVirtualKeyCode(0x16);
            Key(VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_H, VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_G, VirtualKeyShort.KEY_O);
            Assert.That(cell.Text, Is.EqualTo("にほんご"), "Physical direct input must appear exactly once.");
            Assert.That(Text(w, "DraftStatus"), Does.Contain("変更フィールド 0"));
            Key(VirtualKeyShort.SPACE); Assert.That(cell.Text, Is.EqualTo("日本語"));
            if (scenario.StartsWith("cancel", StringComparison.Ordinal))
            {
                Key(VirtualKeyShort.ESCAPE); Assert.That(cell.Text, Is.EqualTo("にほんご"));
                Key(VirtualKeyShort.ESCAPE, VirtualKeyShort.ESCAPE); Assert.That(cell.Text, Is.EqualTo("Issue 1"));
                Assert.That(Text(w, "DraftStatus"), Does.Contain("変更フィールド 0")); return;
            }
            Key(VirtualKeyShort.RETURN); Assert.That(cell.Properties.HasKeyboardFocus.Value, Is.True);
            Assert.That(Text(w, "DraftStatus"), Does.Contain("変更フィールド 0"));
            Key(VirtualKeyShort.RETURN); Assert.That(Text(w, "DraftStatus"), Does.Contain("変更フィールド 1"));
            Assert.That(Element(w, "GridCell1_0").Properties.HasKeyboardFocus.Value, Is.True);
            if (scenario.StartsWith("reconvert", StringComparison.Ordinal))
            {
                cell.Click(); Keyboard.TypeVirtualKeyCode(0x1C); Key(VirtualKeyShort.SPACE);
                var alternative = cell.Text; Assert.That(alternative, Is.Not.Empty.And.Not.EqualTo("日本語"));
                Key(VirtualKeyShort.RETURN); Assert.That(cell.Properties.HasKeyboardFocus.Value, Is.True);
                Key(VirtualKeyShort.RETURN); Assert.That(cell.Text, Is.EqualTo(alternative));
                Assert.That(Element(w, "GridCell1_0").Properties.HasKeyboardFocus.Value, Is.True);
            }
            Capture(w, f.Root, "grid-ime-" + scenario);
        });
        Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }
}
