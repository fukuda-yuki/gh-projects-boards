using System.Text.Json;
using FlaUI.Core.AutomationElements;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [TestCase("cancel"), TestCase("close"), TestCase("interrupt")]
    public void InterruptedApplyRetainsJournalAndNeverRestartsWrites(string action)
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, apply = true, applyDelayMs = 10000 }));
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Edit(w, 0, "B");
            Invoke(w, "ReviewApplyButton"); Element(w, "ApplyTargetRows").AsListBox().Items[0].Select(); Invoke(w, "PrimaryButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is not null); Invoke(w, "PrimaryButton");
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
            Element(w, "ApplyTargetRows").AsListBox().Items[0].Select(); Invoke(w, "PrimaryButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is not null || Text(w, "RegistrationStatus").Contains("IME変換中"));
            if (w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is not null) Invoke(w, "CloseButton");
            Assert.That(CellText(w, 0), Is.EqualTo("に")); Assert.That(Text(w, "DraftStatus"), Does.Contain("GitHub未反映 0セル"));
            Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
        });
    }
    [Test]
    public void OrdinaryApplyReviewsTitleThenVerifiesAndRestoresHistoryWithoutReplay()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, apply = true }));
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Edit(w, 0, "Applied B");
            Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
            Invoke(w, "ReviewApplyButton");
            var targets = Element(w, "ApplyTargetRows").AsListBox(); targets.Items[0].Select();
            Invoke(w, "PrimaryButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is not null);
            Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
            Capture(w, f.Root, "apply-review"); Invoke(w, "PrimaryButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("Apply処理を停止"));
            Assert.That(Text(w, "DraftStatus"), Does.Contain("GitHub未反映 0セル"));
            var writes = File.ReadAllLines(Path.Combine(f.Root, "apply-requests.jsonl"));
            Assert.That(writes.Length, Is.EqualTo(1));
            using var request = JsonDocument.Parse(writes[0]);
            Assert.That(request.RootElement.GetProperty("id").GetString(), Is.EqualTo("I1"));
            Assert.That(request.RootElement.GetProperty("title").GetString(), Is.EqualTo("Applied B"));
            Invoke(w, "ApplyHistoryButton"); Wait(() => Element(w, "ApplyHistoryDialog").IsEnabled);
            Capture(w, f.Root, "apply-history"); Invoke(w, "CloseButton");
        });
        var count = f.Calls().Length;
        f.Run(w => { OpenSaved(w, profile: true); Assert.That(CellText(w, 0), Is.EqualTo("Applied B"));
            Invoke(w, "ApplyHistoryButton"); Wait(() => Element(w, "ApplyHistoryDialog").IsEnabled); Invoke(w, "CloseButton"); });
        Assert.That(f.Calls().Length, Is.EqualTo(count));
    }
}
