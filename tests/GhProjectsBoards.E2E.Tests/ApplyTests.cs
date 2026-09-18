using System.Text.Json;
using FlaUI.Core.AutomationElements;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [TestCase(false, 960, 600), TestCase(true, 1280, 800, Category = "GridIme")]
    public void MixedApplyReturnsToProblemWithoutTakingNativeCompositionFocus(bool ime, int width, int height)
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new {
            registration = true, apply = true, applyDelayMs = 2000, rejectApplyId = "I2" }));
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1);
            var scale = WorkspaceWindowDpi(w.Properties.NativeWindowHandle.Value) / 96d;
            w.Patterns.Transform.Pattern.Resize(width * scale, height * scale);
            Edit(w, 0, "verified first"); Edit(w, 1, "failed second");
            Invoke(w, "ReviewApplyButton");
            Element(w, "ApplySelectAll").AsCheckBox().IsChecked = true; WorkspaceUi.WaitForApplyReady(w);
            Invoke(w, "PrimaryButton");
            Wait(() => File.Exists(Path.Combine(f.Root, "apply-requests.jsonl")));
            var cell = Element(w, "GridCell0_0").AsTextBox(); cell.Click();
            Wait(() => cell.Properties.HasKeyboardFocus.Value);
            if (ime) { FlaUI.Core.Input.Keyboard.TypeVirtualKeyCode(0x16); Key(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_N, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_I); }
            else cell.Text = "unsent buffer";
            var pending = ime ? "に" : "unsent buffer";
            Wait(() => Durable(f).GetProperty("Journal")[0].GetProperty("Operations")[1].GetProperty("State").GetInt32() == 3);
            if (ime)
            {
                Assert.That(cell.Text, Is.EqualTo(pending));
                Assert.That(cell.Properties.HasKeyboardFocus.Value, Is.True);
                Assert.That(w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyOutcomeWarning")), Is.Null);
                Capture(w, f.Root, "composition-retained-after-failure");
                Key(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
            }
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyOutcomeWarning")) is not null);
            Capture(w, f.Root, "short-failure-warning"); Invoke(w, "CloseButton");
            Wait(() => Element(w, "GridCell1_0").Properties.HasKeyboardFocus.Value);
            Assert.That(CellText(w, 0), Is.EqualTo(pending));
            Assert.That(Text(w, "ApplyProblemStatus"), Does.Contain("失敗"));
            Capture(w, f.Root, "problem-cell-and-pending-input");
            Assert.That(File.ReadAllLines(Path.Combine(f.Root, "apply-requests.jsonl")), Has.Length.EqualTo(2));
            Assert.That(Durable(f).GetProperty("Journal")[0].GetProperty("Operations")[0].GetProperty("State").GetInt32(), Is.EqualTo(2));
        });
    }
    [TestCase("cancel"), TestCase("close"), TestCase("interrupt")]
    public void InterruptedApplyRetainsJournalAndNeverRestartsWrites(string action)
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, apply = true, applyDelayMs = 10000 }));
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Edit(w, 0, "B");
            Invoke(w, "ReviewApplyButton"); Wait(() => WorkspaceUi.ApplyRows(w).Length > 0); WorkspaceUi.ApplyRows(w)[0].Select();
            WorkspaceUi.WaitForApplyReady(w); Invoke(w, "PrimaryButton");
            Wait(() => File.Exists(Path.Combine(f.Root, "apply-requests.jsonl")));
            if (action == "cancel") { Invoke(w, "CancelProjectButton"); Wait(() => Element(w, "ReviewApplyButton").IsEnabled); }
            if (action == "close") w.Close();
        }, alreadyClosed: action == "close", interrupt: action == "interrupt");
        var count = f.Calls().Length;
        f.Run(w => { OpenSaved(w, profile: true); Assert.That(CellText(w, 0), Is.EqualTo("B"));
            Invoke(w, "ApplyHistoryButton"); Wait(() => Element(w, "ApplyHistoryDialog").IsEnabled); Invoke(w, "CloseButton"); });
        Assert.That(f.Calls().Length, Is.EqualTo(count));
        Assert.That(File.ReadAllLines(Path.Combine(f.Root, "apply-requests.jsonl")).Length, Is.EqualTo(1));
        var operation = Durable(f).GetProperty("Journal")[0].GetProperty("Operations")[0];
        Assert.That(operation.GetProperty("State").GetInt32(), Is.AnyOf(1, 4));
    }
    [Test, Category("GridIme")]
    public void ApplySelectionDoesNotCommitPhysicalJapanesePendingText()
    {
        using var f = new Fixture();
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1);
            var cell = Element(w, "GridCell0_0").AsTextBox(); cell.Click(); Wait(() => cell.Properties.HasKeyboardFocus.Value);
            FlaUI.Core.Input.Keyboard.TypeVirtualKeyCode(0x16);
            Key(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_N, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_I);
            Assert.That(cell.Text, Is.EqualTo("に")); Invoke(w, "ReviewApplyButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is not null || Text(w, "RegistrationStatus").Contains("IME変換中"));
            if (w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is null)
            {
                Assert.That(cell.Properties.HasKeyboardFocus.Value, Is.True);
                Key(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN); // Natural IME confirmation leaves the cell uncommitted.
                Invoke(w, "ReviewApplyButton");
            }
            Wait(() => WorkspaceUi.ApplyRows(w).Length > 0); WorkspaceUi.ApplyRows(w)[0].Select();
            Wait(() => Element(w, "ApplyCheckStatus").Name.Contains("最新確認済み"));
            Assert.That(Element(w, "PrimaryButton").IsEnabled, Is.False);
            Assert.That(Element(w, "ApplyReviewDialog").FindAllDescendants().Select(e => e.Properties.Name.ValueOrDefault ?? ""), Has.Some.Contains("送らない未確定入力: に"));
            Capture(w, f.Root, "physical-ime-pending-review"); Invoke(w, "CloseButton");
            Assert.That(CellText(w, 0), Is.EqualTo("に")); Assert.That(Text(w, "DraftStatus"), Does.Contain("GitHub未反映 0セル"));
            Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
        });
    }
    [TestCase(1280, 720), TestCase(960, 600)]
    public void OrdinaryApplyReviewsTitleThenVerifiesAndRestoresHistoryWithoutReplay(int width, int height)
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, apply = true }));
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Edit(w, 0, "Applied B");
            var scale = WorkspaceWindowDpi(w.Properties.NativeWindowHandle.Value) / 96d;
            w.Patterns.Transform.Pattern.Resize(width * scale, height * scale);
            w.Move(12, 12);
            Capture(w, f.Root, "ia-workspace-before-settings");
            Invoke(w, "ConnectionPageButton");
            Wait(() => WorkspaceUi.HasVisibleElement(w, "ConnectionScreen"));
            Assert.That(w.FindFirstDescendant(cf => cf.ByAutomationId("RegistrationUrl")), Is.Null);
            Assert.That(w.FindFirstDescendant(cf => cf.ByAutomationId("IssueUrlInput")), Is.Null);
            Assert.That(w.FindFirstDescendant(cf => cf.ByAutomationId("ProjectUrlInput")), Is.Null);
            Element(w, "ConnectionDetails").Patterns.ExpandCollapse.Pattern.Collapse();
            Capture(w, f.Root, "ia-connection-only");
            Invoke(w, "ProjectsPageButton");
            Wait(() => CellText(w, 0) == "Applied B");
            Assert.That(Text(w, "ProjectSummary"), Does.Contain("Project 1"));
            Capture(w, f.Root, "ia-workspace-returned");
            Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
            Invoke(w, "ReviewApplyButton");
            Wait(() => WorkspaceUi.ApplyRows(w).Length > 0); WorkspaceUi.ApplyRows(w)[0].Select();
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is not null && Element(w, "PrimaryButton").IsEnabled);
            Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
            Assert.That(WorkspaceUi.ApplyRows(w).Length, Is.EqualTo(1), "Unchanged Issues must not be ordinary candidates.");
            Assert.That(Element(w, "PrimaryButton").Name, Is.EqualTo("GitHubに反映（1件）"));
            Capture(w, f.Root, "apply-review"); Invoke(w, "PrimaryButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("反映完了"));
            Assert.That(w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyHistoryDialog")), Is.Null);
            Assert.That(Element(w, "ApplyHistoryButton").IsEnabled, Is.True);
            Assert.That(Text(w, "DraftStatus"), Does.Contain("GitHub未反映 0セル"));
            var writes = File.ReadAllLines(Path.Combine(f.Root, "apply-requests.jsonl"));
            Assert.That(writes.Length, Is.EqualTo(1));
            using var request = JsonDocument.Parse(writes[0]);
            Assert.That(request.RootElement.GetProperty("id").GetString(), Is.EqualTo("I1"));
            Assert.That(request.RootElement.GetProperty("title").GetString(), Is.EqualTo("Applied B"));
            Invoke(w, "ApplyHistoryButton"); Wait(() => Element(w, "ApplyHistoryDialog").IsEnabled);
            Capture(w, f.Root, "apply-history"); Invoke(w, "CloseButton");
            foreach (var screenshot in Directory.GetFiles(f.Root, "*.png")) TestContext.AddTestAttachment(screenshot);
        });
        var count = f.Calls().Length;
        f.Run(w => { OpenSaved(w, profile: true); Assert.That(CellText(w, 0), Is.EqualTo("Applied B"));
            Invoke(w, "ApplyHistoryButton"); Wait(() => Element(w, "ApplyHistoryDialog").IsEnabled); Invoke(w, "CloseButton"); });
        Assert.That(f.Calls().Length, Is.EqualTo(count));
    }
}
