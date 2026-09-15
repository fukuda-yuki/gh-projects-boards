using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [Test, Category("GridIme")]
    public void CreationPromotionDefersDuringPhysicalImeAndRetainsLaterCommitAndBuffer()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, apply = true, creation = true, creationResponseDelayMs = 5000 }));
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); AddCreationRow(f, w, 101, "A");
            Invoke(w, "ReviewApplyButton"); var list = Element(w, "ApplyTargetRows").AsListBox(); list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
            Wait(() => list.Items.Any(i => i.Name.Contains("新規作成 / sample-user/first / A")));
            list.Items.Single(i => i.Name.Contains("新規作成 / sample-user/first / A")).Select(); Invoke(w, "PrimaryButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is not null); Invoke(w, "PrimaryButton");
            Wait(() => File.Exists(Path.Combine(f.Root, "creation-requests.jsonl"))); Scroll(w, 100); Edit(w, 101, "B");
            var cell = Element(w, "GridCell101_0").AsTextBox(); cell.Click(); Wait(() => cell.Properties.HasKeyboardFocus.Value);
            Keyboard.TypeVirtualKeyCode(0x16); Key(VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I);
            Assert.That(cell.Text, Is.EqualTo("に")); Wait(() => Text(w, "RegistrationStatus").Contains("Apply処理を停止"));
            Assert.That(cell.Text, Is.EqualTo("に")); Assert.That(Durable(f).GetProperty("LocalRows")[0].GetProperty("Title").GetString(), Is.EqualTo("B"));
            Key(VirtualKeyShort.RETURN); Invoke(w, "RefreshProjectButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("照合をローカル保存"));
            var draft = Durable(f).GetProperty("Fields").EnumerateArray().Single(x => x.GetProperty("Key").GetProperty("NodeId").GetString() == "created1" && x.GetProperty("Key").GetProperty("Kind").GetString() == "Title");
            Assert.That(draft.GetProperty("Baseline").GetString(), Is.EqualTo("A")); Assert.That(draft.GetProperty("Change").GetProperty("Value").GetString(), Is.EqualTo("B"));
            Assert.That(draft.GetProperty("Buffer").GetString(), Is.EqualTo("に"));
        });
        f.Run(w => { OpenSaved(w, profile: true); Scroll(w, 100); Assert.That(CellText(w, 101), Is.EqualTo("に")); });
        Assert.That(File.ReadAllLines(Path.Combine(f.Root, "creation-requests.jsonl")).Count(l => l.Contains("CreateWorkspaceIssue")), Is.EqualTo(1));
    }
    private static void AddCreationRow(Fixture f, Window w, int index, string title, int? expectedLocalCount = null)
    {
        Invoke(w, "GridAddRow"); LocalCount(f, expectedLocalCount ?? index - 100); Scroll(w, 100); Edit(w, index, title);
        // Enter commits the last row and keeps its title selected. Continue from
        // that native focus after layout settles instead of clicking stale bounds.
        Wait(() => Element(w, $"GridCell{index}_0").Properties.HasKeyboardFocus.Value);
        Key(VirtualKeyShort.TAB, VirtualKeyShort.TAB);
        Wait(() => Element(w, $"GridCell{index}_2").Properties.HasKeyboardFocus.Value);
        Set(w, $"GridCell{index}_2", "sample-user/first"); Key(VirtualKeyShort.RETURN);
        var scroll = Element(w, "ProjectItems").Patterns.Scroll.Pattern;
        if (scroll.HorizontallyScrollable.Value) scroll.SetScrollPercent(0, -1);
    }
    [Test]
    public void OrdinaryCreationMixedApplyCreatesTwoAndReopensWithoutReplay()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, apply = true, creation = true }));
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); Edit(w, 0, "Existing update");
            AddCreationRow(f, w, 101, "Same title"); WorkspaceUi.SelectCombo(w, "GridCell101_1", "Done");
            AddCreationRow(f, w, 102, "Same title");
            Invoke(w, "GridAddRow"); LocalCount(f, 3);
            Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
            Invoke(w, "ReviewApplyButton"); var list = Element(w, "ApplyTargetRows").AsListBox();
            list.Items[0].Select(); list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
            Wait(() => list.Items.Count(i => i.Name.Contains("新規作成 / sample-user/first / Same title")) == 2);
            foreach (var target in list.Items.Where(i => i.Name.Contains("新規作成 / sample-user/first / Same title"))) target.AddToSelection();
            Invoke(w, "PrimaryButton"); Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is not null);
            Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
            Capture(w, f.Root, "creation-mixed-review");
            // Two creations, initial setup and an existing update each require
            // paced dispatches, independent read-back and the final UI handoff.
            var completionClock = System.Diagnostics.Stopwatch.StartNew();
            Invoke(w, "PrimaryButton");
            var completed = FlaUI.Core.Tools.Retry.WhileFalse(
                () => Text(w, "RegistrationStatus").Contains("Apply処理を停止"),
                TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100)).Result;
            completionClock.Stop();
            TestContext.WriteLine($"Mixed Apply approval-to-visible-stop: observed={completed}, elapsedMs={completionClock.Elapsed.TotalMilliseconds:F1}");
            Assert.That(completed, Is.True, "The mixed batch must finish and publish its result in the ordinary workspace.");
            var creations = Durable(f).GetProperty("Journal")[0].GetProperty("Creations");
            Assert.That(creations.GetArrayLength(), Is.EqualTo(2));
            Assert.That(creations.EnumerateArray().All(c => c.GetProperty("Completed").GetBoolean()), Is.True, Text(w, "RegistrationStatus"));
            LocalCount(f, 1); Capture(w, f.Root, "creation-promoted");
        });
        var requests = File.ReadAllLines(Path.Combine(f.Root, "creation-requests.jsonl"));
        Assert.That(requests.Count(l => l.Contains("CreateWorkspaceIssue")), Is.EqualTo(2));
        var calls = f.Calls().Length;
        f.Run(w => { OpenSaved(w, profile: true); Invoke(w, "ApplyHistoryButton"); Capture(w, f.Root, "creation-restored-history"); Invoke(w, "CloseButton"); });
        Assert.That(f.Calls().Length, Is.EqualTo(calls));
    }
    [TestCase(false), TestCase(true)]
    public void OrdinaryCreationInterruptedResponseResolvesThroughPublicUi(bool retry)
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, apply = true, creation = true, loseCreationResponse = true }));
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); AddCreationRow(f, w, 101, "Ambiguous title");
            Invoke(w, "ReviewApplyButton"); var list = Element(w, "ApplyTargetRows").AsListBox(); list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
            Wait(() => list.Items.Any(i => i.Name.Contains("新規作成 / sample-user/first / Ambiguous title")));
            list.Items.Single(i => i.Name.Contains("新規作成 / sample-user/first / Ambiguous title")).Select(); Invoke(w, "PrimaryButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is not null); Invoke(w, "PrimaryButton");
            Wait(() => File.Exists(Path.Combine(f.Root, "creation-requests.jsonl")));
        }, interrupt: true);
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, apply = true, creation = true }));
        var id = Durable(f).GetProperty("Journal")[0].GetProperty("Creations")[0].GetProperty("Id").GetString();
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); OpenSaved(w);
            Invoke(w, "ApplyHistoryButton"); Invoke(w, "ResolveCreation-" + id);
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("CreationResolutionDialog")) is not null);
            Capture(w, f.Root, "creation-ambiguity");
            if (retry)
            {
                Invoke(w, "SecondaryButton"); Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("CreationRetryConfirmDialog")) is not null);
                Assert.That(Element(w, "PrimaryButton").IsEnabled, Is.False);
                Element(w, "CreationDuplicateAcknowledgement").AsCheckBox().IsChecked = true;
                Invoke(w, "PrimaryButton");
            }
            else
            {
                foreach (var rejected in new[] { "https://wrong.example/sample-user/first/issues/1001", "https://example.test/sample-user/second/issues/1001" })
                {
                    var before = File.ReadAllLines(Path.Combine(f.Root, "creation-requests.jsonl")).Length;
                    Set(w, "CreationBindUrl", rejected); Invoke(w, "PrimaryButton");
                    Wait(() => Text(w, "RegistrationStatus").Contains("検証できない"));
                    Assert.That(File.ReadAllLines(Path.Combine(f.Root, "creation-requests.jsonl")).Length, Is.EqualTo(before));
                    Invoke(w, "ApplyHistoryButton"); Invoke(w, "ResolveCreation-" + id);
                    Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("CreationResolutionDialog")) is not null);
                }
                Set(w, "CreationBindUrl", "https://example.test/sample-user/first/issues/1001"); Invoke(w, "PrimaryButton");
                Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("CreationBindingConfirmDialog")) is not null);
                Capture(w, f.Root, "creation-binding-confirm"); Invoke(w, "PrimaryButton");
                Wait(() => Text(w, "RegistrationStatus").Contains("関連付けを保存"));
                Invoke(w, "ApplyHistoryButton"); Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyHistoryDialog")) is not null); Invoke(w, "PrimaryButton");
            }
            Wait(() => Text(w, "RegistrationStatus").Contains("Apply処理を停止"));
            var batches = Durable(f).GetProperty("Journal"); var current = batches[batches.GetArrayLength() - 1].GetProperty("Creations")[0];
            Assert.That(current.GetProperty("Completed").GetBoolean(), Is.True, Text(w, "RegistrationStatus"));
            Assert.That(current.GetProperty("EarlierUncertain").GetBoolean(), Is.True);
            Capture(w, f.Root, "creation-reconciled");
        });
        var writes = File.ReadAllLines(Path.Combine(f.Root, "creation-requests.jsonl"));
        Assert.That(writes.Count(l => l.Contains("CreateWorkspaceIssue")), Is.EqualTo(retry ? 2 : 1));
    }

    [Test]
    public void OrdinaryCreationRejectsIssueClaimedByAnotherRow()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, apply = true, creation = true }));
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); AddCreationRow(f, w, 101, "Known"); Approve(w, "Known");
            Wait(() => Text(w, "RegistrationStatus").Contains("Apply処理を停止")); LocalCount(f, 0);
            File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, apply = true, creation = true, loseCreationResponse = true }));
            AddCreationRow(f, w, 102, "Ambiguous second", 1); Approve(w, "Ambiguous second");
            Wait(() => File.ReadAllLines(Path.Combine(f.Root, "creation-requests.jsonl")).Count(l => l.Contains("CreateWorkspaceIssue")) == 2);
        }, interrupt: true);
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, apply = true, creation = true }));
        var id = Durable(f).GetProperty("Journal")[1].GetProperty("Creations")[0].GetProperty("Id").GetString();
        var before = File.ReadAllLines(Path.Combine(f.Root, "creation-requests.jsonl")).Length;
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); OpenSaved(w); Invoke(w, "ApplyHistoryButton"); Invoke(w, "ResolveCreation-" + id);
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("CreationResolutionDialog")) is not null);
            Set(w, "CreationBindUrl", "https://example.test/sample-user/first/issues/1001"); Invoke(w, "PrimaryButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("別の作成行に関連付け済み"));
            Assert.That(Durable(f).GetProperty("Journal")[1].GetProperty("Creations")[0].GetProperty("Verified").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
        Assert.That(File.ReadAllLines(Path.Combine(f.Root, "creation-requests.jsonl")).Length, Is.EqualTo(before));
        void Approve(Window w, string title)
        {
            Invoke(w, "ReviewApplyButton"); var list = Element(w, "ApplyTargetRows").AsListBox(); list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
            Wait(() => list.Items.Any(i => i.Name.Contains("新規作成 / sample-user/first / " + title)));
            list.Items.Single(i => i.Name.Contains("新規作成 / sample-user/first / " + title)).Select(); Invoke(w, "PrimaryButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is not null); Invoke(w, "PrimaryButton");
        }
    }
}
