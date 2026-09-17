using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    private static void LocalCount(Fixture f, int count) => Wait(() => Directory.Exists(Path.Combine(f.Data, "Drafts"))
        && Directory.GetFiles(Path.Combine(f.Data, "Drafts"), "*.json").Length == 1
        && Durable(f).TryGetProperty("LocalRows", out var rows) && rows.GetArrayLength() == count);
    [Test]
    public void LocalRowsSaveFailureAndInterruptionRecoverIdentitiesAndUndo()
    {
        using var f = new Fixture(); string saved = "";
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Register(w, 2); OpenSaved(w);
            Invoke(w, "GridAddRow"); LocalCount(f, 1); Scroll(w, 100);
            using (var gate = new FileStream(Path.Combine(f.Data, "Drafts", ".writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Element(w, "GridCell101_0").Click(); Set(w, "GridCell101_0", "Recover local pending");
                Wait(() => Text(w, "DraftStatus").Contains("保存失敗"));
                w.FindFirstDescendant(cf => cf.ByName("Project 2"))!.Click(); Wait(() => Text(w, "RegistrationStatus").Contains("保存失敗"));
                Assert.That(CellText(w, 101), Is.EqualTo("Recover local pending"));
            }
            Invoke(w, "GridSave"); Wait(() => Durable(f).GetProperty("LocalRows")[0].GetProperty("TitleBuffer").GetString() == "Recover local pending");
            saved = Durable(f).GetProperty("LocalRows").GetRawText(); Element(w, "GridCell101_0").Click(); Invoke(w, "GridRemoveRows"); LocalCount(f, 0);
        }, interrupt: true);
        f.Run(w => { OpenSaved(w, profile: true); Invoke(w, "GridUndo"); LocalCount(f, 1); Scroll(w, 100);
            Assert.That(CellText(w, 101), Is.EqualTo("Recover local pending")); Assert.That(Durable(f).GetProperty("LocalRows").GetRawText(), Is.EqualTo(saved)); });
        Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }
    [Test]
    public void LocalRowsContinuousEditingDuplicateRemoveUndoSwitchRestartAndRefresh()
    {
        using var f = new Fixture(); string saved = "";
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Register(w, 2); OpenSaved(w);
            Edit(w, 0, "Committed original"); WorkspaceUi.SelectCombo(w, "GridCell0_1", "Done");
            Element(w, "GridCell0_0").Click(); Invoke(w, "GridDuplicateRows"); LocalCount(f, 1);
            Scroll(w, 100); Assert.That(CellText(w, 101), Is.EqualTo("Committed original"));
            Assert.That(WorkspaceUi.ChoiceText(w, "GridCell101_1"), Is.EqualTo("Done"));
            Element(w, "GridCell101_0").Click(); Key(VirtualKeyShort.TAB, VirtualKeyShort.TAB);
            Wait(() => Element(w, "GridCell101_2").Properties.HasKeyboardFocus.Value);
            Set(w, "GridCell101_2", "chosen/repo"); Key(VirtualKeyShort.RETURN);
            var scroll = Element(w, "ProjectItems").Patterns.Scroll.Pattern;
            if (scroll.HorizontallyScrollable.Value) scroll.SetScrollPercent(0, -1);
            Invoke(w, "GridAddRow"); LocalCount(f, 2); Scroll(w, 100); Edit(w, 102, "Second local");
            Element(w, "GridCell101_0").Click(); Set(w, "GridCell101_0", "Pending local");
            Wait(() => Durable(f).GetProperty("LocalRows")[0].GetProperty("TitleBuffer").GetString() == "Pending local");
            saved = Durable(f).GetProperty("LocalRows").GetRawText();
            Invoke(w, "GridRemoveRows"); LocalCount(f, 1); Invoke(w, "GridUndo"); LocalCount(f, 2);
            Assert.That(Durable(f).GetProperty("LocalRows").GetRawText(), Is.EqualTo(saved));
            OpenSaved(w, "Project 2"); Assert.That(CellText(w, 0), Is.EqualTo("Committed original"));
            OpenSaved(w); Scroll(w, 100); Assert.That(CellText(w, 101), Is.EqualTo("Pending local"));
            Invoke(w, "RefreshProjectButton"); Wait(() => Text(w, "RegistrationStatus").Contains("照合をローカル保存"));
            Assert.That(Durable(f).GetProperty("LocalRows").GetRawText(), Is.EqualTo(saved));
            Capture(w, f.Root, "local-rows-retained");
        });
        var calls = f.Calls().Length;
        f.Run(w => { OpenSaved(w, profile: true); Scroll(w, 100); Assert.That(CellText(w, 101), Is.EqualTo("Pending local"));
            Assert.That(CellText(w, 101, 2), Is.EqualTo("chosen/repo")); Assert.That(Text(w, "DraftStatus"), Does.Contain("ローカル行 2").And.Contain("GitHub未反映")); });
        Assert.That(Durable(f).GetProperty("LocalRows").GetRawText(), Is.EqualTo(saved));
        Assert.That(f.Calls().Length, Is.EqualTo(calls)); Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }
    [Test]
    public void LocalRowsAppendAtomicCopyNamesUndoAndOfflinePreparation()
    {
        using var f = new Fixture(); using var clipboard = new NativeClipboardScope();
        f.Run(w => { Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); });
        var calls = f.Calls().Length;
        f.Run(w =>
        {
            OpenSaved(w, profile: true); Edit(w, 0, "Existing pending work");
            NativeClipboardScope.WriteTestFormats("One\tDone\nTwo\tMissing"); Invoke(w, "GridAppendRows");
            Invoke(w, "PrimaryButton");
            Wait(() => Text(w, "DraftStatus").Contains("追加していません")); LocalCount(f, 0);
            NativeClipboardScope.WriteTestFormats("One\tDone\nTwo\t"); Invoke(w, "GridAppendRows"); Invoke(w, "PrimaryButton"); LocalCount(f, 2);
            Scroll(w, 100); Element(w, "GridCell101_0").Click(); using (Keyboard.Pressing(VirtualKeyShort.SHIFT)) Key(VirtualKeyShort.RIGHT);
            Invoke(w, "GridCopy"); Assert.That(NativeClipboardScope.ReadText(), Is.EqualTo("One\tDone"));
            Invoke(w, "GridUndo"); LocalCount(f, 0); Scroll(w, 0); Assert.That(CellText(w, 0), Is.EqualTo("Existing pending work"));
            Invoke(w, "GridAddRow"); LocalCount(f, 1); Scroll(w, 100); Assert.That(CellText(w, 101), Is.Empty);
        });
        Assert.That(f.Calls().Length, Is.EqualTo(calls)); Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }
    [Test]
    public void LocalRowsSurviveOrdinaryExistingApplyAndExplicitSelection()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, apply = true }));
        string saved = "";
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Edit(w, 0, "Applied existing");
            Invoke(w, "GridAddRow"); LocalCount(f, 1); Scroll(w, 100); Set(w, "GridCell101_0", "Pending new");
            Wait(() => Durable(f).GetProperty("LocalRows")[0].GetProperty("TitleBuffer").GetString() == "Pending new");
            saved = Durable(f).GetProperty("LocalRows").GetRawText(); Invoke(w, "ReviewApplyButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyTargetRows")) is not null);
            var targets = Element(w, "ApplyTargetRows").AsListBox(); targets.Items[0].Select(); Invoke(w, "PrimaryButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is not null);
            Invoke(w, "PrimaryButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("Apply処理を停止"));
            Assert.That(Durable(f).GetProperty("LocalRows").GetRawText(), Is.EqualTo(saved));
            var writes = File.ReadAllLines(Path.Combine(f.Root, "apply-requests.jsonl")); Assert.That(writes, Has.Length.EqualTo(1));
            Assert.That(JsonDocument.Parse(writes[0]).RootElement.GetProperty("id").GetString(), Is.EqualTo("I1"));
        });
        f.Run(w => { OpenSaved(w, profile: true); Scroll(w, 100); Assert.That(CellText(w, 101), Is.EqualTo("Pending new")); });
        Assert.That(Durable(f).GetProperty("LocalRows").GetRawText(), Is.EqualTo(saved));
    }
    [TestCase(false, false), TestCase(false, true), TestCase(true, false), TestCase(true, true), Category("GridIme")]
    public void LocalRowsPhysicalJapaneseDirectAndF2RetainPendingAfterRefreshRestart(bool duplicate, bool f2)
    {
        using var f = new Fixture();
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1);
            if (duplicate) { Element(w, "GridCell0_0").Click(); Invoke(w, "GridDuplicateRows"); } else Invoke(w, "GridAddRow");
            LocalCount(f, 1); Scroll(w, 100); var cell = Element(w, "GridCell101_0").AsTextBox(); cell.Click(); Wait(() => cell.Properties.HasKeyboardFocus.Value);
            if (f2) Key(VirtualKeyShort.F2); Keyboard.TypeVirtualKeyCode(0x16);
            Key(VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_H, VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_G, VirtualKeyShort.KEY_O);
            Assert.That(cell.Text, Is.EqualTo("にほんご")); Key(VirtualKeyShort.SPACE); Assert.That(cell.Text, Is.EqualTo("日本語"));
            Key(VirtualKeyShort.RETURN);
            Wait(() => Text(w, "DraftStatus").Contains("保存済み") && Durable(f).GetProperty("LocalRows")[0].GetProperty("TitleBuffer").GetString() == "日本語");
            Assert.That(w.FindFirstDescendant(cf => cf.ByName("編集中・未確定")), Is.Not.Null);
            Assert.That(Durable(f).GetProperty("LocalRows")[0].GetProperty("Title").GetString(), Is.EqualTo(duplicate ? "Issue 1" : ""));
            Key(VirtualKeyShort.RETURN); Wait(() => Durable(f).GetProperty("LocalRows")[0].GetProperty("Title").GetString() == "日本語");
            cell.Click(); if (f2) Key(VirtualKeyShort.F2);
            Key(VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I); Assert.That(cell.Text, Is.EqualTo("に"));
            Invoke(w, "RefreshProjectButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("照合をローカル保存") || Text(w, "RegistrationStatus").Contains("IME"));
            Wait(() => Durable(f).GetProperty("LocalRows")[0].GetProperty("TitleBuffer").GetString() == "に");
        });
        f.Run(w => { OpenSaved(w, profile: true); Scroll(w, 100); Assert.That(CellText(w, 101), Is.EqualTo("に")); });
        Assert.That(Durable(f).GetProperty("LocalRows")[0].GetProperty("Title").GetString(), Is.EqualTo("日本語"));
        Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }
}
