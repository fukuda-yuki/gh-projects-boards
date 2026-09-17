using FlaUI.Core.AutomationElements;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [Test, Category("GridIme")]
    public void RefreshDuringPhysicalJapaneseInputPreservesPendingText()
    {
        using var f = new Fixture();
        f.Run(w => {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1);
            w.SetForeground(); var cell = Element(w, "GridCell0_0").AsTextBox(); cell.Click();
            Wait(() => cell.Properties.HasKeyboardFocus.Value);
            FlaUI.Core.Input.Keyboard.TypeVirtualKeyCode(0x16);
            Key(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_N, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_I);
            Assert.That(cell.Text, Is.EqualTo("に")); var calls = f.Calls().Length;
            Invoke(w, "RefreshProjectButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("IME変換中") || Text(w, "RegistrationStatus").Contains("照合をローカル保存"));
            // Native button focus can end composition before Click. The cell must still remain pending;
            // if composition remains active, the explicit deferral must dispatch no query.
            if (Text(w, "RegistrationStatus").Contains("IME変換中")) Assert.That(f.Calls(), Has.Length.EqualTo(calls));
            Assert.That(CellText(w, 0), Is.EqualTo("に"));
            Assert.That(Text(w, "DraftStatus"), Does.Contain("変更フィールド 0"));
            var pending = Durable(f).GetProperty("Fields").EnumerateArray().Single(f => f.GetProperty("Key").GetProperty("NodeId").GetString() == "I1");
            Assert.That(pending.GetProperty("Buffer").GetString(), Is.EqualTo("に")); Assert.That(pending.GetProperty("Change").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Null));
            Element(w, "GridCell0_0").Click();
            Key(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN, FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
            Wait(() => Text(w, "DraftStatus").Contains("変更フィールド 1"));
        });
    }

    [Test]
    public void DeliberateRefreshInterruptionRecoversCoherentCheckpoint()
    {
        using var f = new Fixture();
        f.Run(w => {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Edit(w, 0, "Local B");
            f.Write(remoteTitle: "External C"); Invoke(w, "RefreshProjectButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("照合をローカル保存"));
            Assert.That(Text(w, "DraftStatus"), Does.Contain("競合 1"));
        }, interrupt: true);
        f.Run(w => {
            OpenSaved(w, profile: true); Invoke(w, "GridConflicts");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ConflictComparison")) is not null);
            Assert.That(Text(w, "ConflictComparison"), Does.Contain("B 基準: Issue 1").And.Contain("L ローカル: Local B").And.Contain("R GitHub: External C"));
            Invoke(w, "CloseButton");
        });
    }
    [TestCase("remote"), TestCase("local"), TestCase("other")]
    public void RefreshConflictComparisonResolutionAndRestart(string choice)
    {
        using var f = new Fixture();
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Edit(w, 0, "Local B");
            f.Write(remoteTitle: "External C"); Invoke(w, "RefreshProjectButton");
            Wait(() => Text(w, "DraftStatus").Contains("競合 1"));
            Assert.That(CellText(w, 0), Is.EqualTo("Local B"));
            Invoke(w, "RefreshProjectButton"); Wait(() => Text(w, "RegistrationStatus").Contains("照合をローカル保存"));
            Invoke(w, "GridConflicts"); Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ConflictDialog")) is not null);
            Assert.That(Text(w, "ConflictComparison"), Does.Contain("B 基準: Issue 1").And.Contain("L ローカル: Local B").And.Contain("R GitHub: External C"));
            Capture(w, f.Root, "conflict-comparison");
            if (choice == "other") { Set(w, "ConflictAlternativeTitle", "Alternative D"); Invoke(w, "ConflictUseAlternative"); }
            else Invoke(w, choice == "remote" ? "PrimaryButton" : "SecondaryButton");
            Wait(() => Text(w, "DraftStatus").Contains("競合 0"));
            Assert.That(CellText(w, 0), Is.EqualTo(choice == "remote" ? "External C" : choice == "local" ? "Local B" : "Alternative D"));
        });
        f.Run(w => {
            OpenSaved(w, profile: true);
            Assert.That(CellText(w, 0), Is.EqualTo(choice == "remote" ? "External C" : choice == "local" ? "Local B" : "Alternative D"));
            Assert.That(Text(w, "DraftStatus"), Does.Contain("競合 0"));
            Invoke(w, "GridUndo"); Wait(() => Text(w, "DraftStatus").Contains("競合 1"));
        });
        Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }

    [Test]
    public void RefreshIndependentFieldsAndPartialFailureKeepCompleteCheckpoint()
    {
        using var f = new Fixture();
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Edit(w, 0, "Local title");
            f.Write(remoteOption: "done"); Invoke(w, "RefreshProjectButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("照合をローカル保存"));
            Assert.That(CellText(w, 0), Is.EqualTo("Local title"));
            Assert.That(WorkspaceUi.ChoiceText(w, "GridCell0_1"), Is.EqualTo("Done"));
            var saved = Durable(f).GetProperty("Registrations").GetRawText();
            f.Write(remoteTitle: "Partial must not win", partial: true); Invoke(w, "RefreshProjectButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("一部取得"));
            Assert.That(CellText(w, 0), Is.EqualTo("Local title")); Assert.That(Durable(f).GetProperty("Registrations").GetRawText(), Is.EqualTo(saved));
            Assert.That(Text(w, "ProjectSummary"), Does.Contain("未採用観測"));
        });
        f.Run(w => { OpenSaved(w, profile: true); Assert.That(CellText(w, 0), Is.EqualTo("Local title")); Assert.That(Text(w, "DraftStatus"), Does.Contain("競合 0")); });
        Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }

    [Test]
    public void RefreshCancellationAndCloseRetainExistingConflict()
    {
        using var f = new Fixture();
        f.Run(w => {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Edit(w, 0, "Local B");
            f.Write(remoteTitle: "External C"); Invoke(w, "RefreshProjectButton"); Wait(() => Text(w, "DraftStatus").Contains("競合 1"));
            f.Write(delay: 5000, remoteTitle: "Must not arrive"); Invoke(w, "RefreshProjectButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("取得中")); Invoke(w, "CancelProjectButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("キャンセルしました"));
            Assert.That(Text(w, "DraftStatus"), Does.Contain("競合 1"));
            Invoke(w, "RefreshProjectButton"); Wait(() => Text(w, "RegistrationStatus").Contains("取得中"));
        });
        f.Run(w => { OpenSaved(w, profile: true); Assert.That(CellText(w, 0), Is.EqualTo("Local B")); Assert.That(Text(w, "DraftStatus"), Does.Contain("競合 1")); });
    }
}
