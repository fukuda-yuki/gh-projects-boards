using FlaUI.Core.Input;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;
using System.Text.Json;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [Test]
    public void OrdinaryBoardsManualDependencyWeeklyReplanRestartsAndPublishesReviewedProjections()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, apply = true, planning = true, itemCount = 2 }));
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1);
            Invoke(w, "GridPlanning"); Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("PlanningDialog")) is not null);
            Element(w, "PlanProjectStart").AsTextBox().Text = "2026-10-05 09:00";
            Element(w, "PlanCutoff").AsTextBox().Text = "2026-10-05 09:00";
            foreach (var (role, index) in new[] { ("Estimate", 1), ("Remaining", 2), ("Actual", 3), ("Start", 1), ("Finish", 2) })
            {
                var combo = Element(w, "PlanField-" + role); combo.Focus(); Key(VirtualKeyShort.HOME);
                for (var i = 0; i < index; i++) Key(VirtualKeyShort.DOWN);
                Key(VirtualKeyShort.TAB);
            }
            Capture(w, f.Root, "planning-setup"); Invoke(w, "PrimaryButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("PlanningDialog")) is null);
            Wait(() => Element(w, "GridCell0_2").Name.StartsWith("行 1 列 3 Estimate") && Element(w, "GridCell0_2").IsEnabled
                && !Element(w, "GridCell0_2").Patterns.Value.Pattern.IsReadOnly.Value);
            var effort = Element(w, "GridCell0_2").AsTextBox();
            Assert.That(effort.Patterns.Value.Pattern.IsReadOnly.Value, Is.False, effort.Name);
            effort.Focus(); Wait(() => Element(w, "GridCell0_2").Properties.HasKeyboardFocus.Value);
            Key(VirtualKeyShort.F2); Keyboard.Type("16"); Key(VirtualKeyShort.RETURN);
            Wait(() => CellText(w, 0, 2) == "16");
            Element(w, "GridCell0_2").Focus(); Wait(() => Element(w, "GridCell0_2").Properties.HasKeyboardFocus.Value);
            Invoke(w, "GridPlanning"); Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("PlanMode")) is not null);
            Element(w, "PlanMode").Focus(); Key(VirtualKeyShort.HOME, VirtualKeyShort.DOWN, VirtualKeyShort.TAB);
            Invoke(w, "PrimaryButton"); Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("PlanningDialog")) is null);
            Invoke(w, "GridPlanning"); Wait(() => Element(w, "PlanTaskFinish").AsTextBox().Text == "2026-10-06 18:00");
            Capture(w, f.Root, "planning-auto"); Invoke(w, "CloseButton");
            Invoke(w, "GridPlanning"); Wait(() => Element(w, "PlanTaskFinish").AsTextBox().Text == "2026-10-06 18:00");
            Set(w, "PlanTaskFinish", "2026-10-06 16:19"); Invoke(w, "PrimaryButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("PlanningDialog")) is null);
            Element(w, "GridCell1_2").Focus(); Key(VirtualKeyShort.F2); Keyboard.Type("4"); Key(VirtualKeyShort.RETURN);
            Wait(() => CellText(w, 1, 2) == "4"); Element(w, "GridCell1_2").Focus();
            Invoke(w, "GridPlanning"); Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("PlanMode")) is not null);
            Element(w, "PlanMode").Focus(); Key(VirtualKeyShort.HOME, VirtualKeyShort.DOWN, VirtualKeyShort.TAB);
            Element(w, "PlanSection-先行Issue（終了→開始）").Patterns.ExpandCollapse.Pattern.Expand();
            Wait(() => Element(w, "PlanPredecessors").AsListBox().Items.Length == 1);
            Element(w, "PlanPredecessors").AsListBox().Items[0].AddToSelection(); Invoke(w, "PrimaryButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("PlanningDialog")) is null);
            Invoke(w, "GridPlanning"); Wait(() => Element(w, "PlanTaskStart").AsTextBox().Text == "2026-10-06 16:19");
            Assert.That(Element(w, "PlanTaskFinish").AsTextBox().Text, Is.EqualTo("2026-10-07 11:19"));
            Capture(w, f.Root, "planning-manual-successor"); Invoke(w, "CloseButton");
            Element(w, "GridCell0_2").Focus(); Invoke(w, "GridPlanning");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("PlanMode")) is not null);
            Element(w, "PlanSection-工数・進捗・実績").Patterns.ExpandCollapse.Pattern.Expand();
            Element(w, "PlanProgress").Focus(); Key(VirtualKeyShort.HOME, VirtualKeyShort.DOWN, VirtualKeyShort.TAB);
            Set(w, "PlanWork-Remaining", "3"); Set(w, "PlanActualStart", "2026-10-05 09:00");
            Element(w, "PlanSection-累積実績・担当者別内訳").Patterns.ExpandCollapse.Pattern.Expand();
            Element(w, "PlanReportsEnabled").Patterns.Toggle.Pattern.Toggle();
            Set(w, "PlanActualHours-Unattributed", "5"); Set(w, "PlanReportedThrough-Unattributed", "2026-10-06");
            Capture(w, f.Root, "planning-weekly-report"); Invoke(w, "PrimaryButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("PlanningDialog")) is null);
            Invoke(w, "GridPlanningSettings"); Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("PlanCutoff")) is not null);
            Set(w, "PlanCutoff", "2026-10-07 09:00"); Invoke(w, "PrimaryButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("PlanningDialog")) is null);
            Assert.That(CellText(w, 0, 2), Is.EqualTo("16")); Assert.That(CellText(w, 0, 3), Is.EqualTo("3")); Assert.That(CellText(w, 0, 4), Is.EqualTo("5"));
            Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
        });
        f.Run(w =>
        {
            OpenSaved(w, profile: true); Element(w, "GridCell0_0").Focus(); Invoke(w, "GridPlanning");
            Wait(() => Element(w, "PlanTaskFinish").AsTextBox().Text == "2026-10-06 16:19");
            Assert.That(Element(w, "PlanMode").AsComboBox().SelectedItem?.Text, Is.EqualTo("Manual")); Capture(w, f.Root, "planning-restored-manual"); Invoke(w, "CloseButton");
            // Reconnect only for the explicit publication path.
            Invoke(w, "ConnectionPageButton"); Connect(w); Invoke(w, "ProjectsPageButton");
            Invoke(w, "ReviewApplyButton"); Wait(() => WorkspaceUi.ApplyRows(w).Length > 0);
            foreach (var row in WorkspaceUi.ApplyRows(w)) { row.AddToSelection(); WorkspaceUi.WaitForApplyReady(w); }
            Capture(w, f.Root, "planning-apply-review"); Invoke(w, "PrimaryButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("反映完了"));
            var payloads = File.ReadAllLines(Path.Combine(f.Root, "apply-requests.jsonl")).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToArray();
            Assert.That(payloads.Length, Is.EqualTo(9));
            Assert.That(payloads.Single(p => p.TryGetProperty("blockingIssueId", out _)).GetProperty("blockingIssueId").GetString(), Is.EqualTo("I1"));
            Assert.That(payloads.Where(p => p.TryGetProperty("fieldId", out var field) && field.GetString() == "F-Actual").Single().GetProperty("value").GetProperty("number").GetDecimal(), Is.EqualTo(5));
            Capture(w, f.Root, "planning-published");
        });
        foreach (var screenshot in Directory.GetFiles(f.Root, "*.png")) TestContext.AddTestAttachment(screenshot);
    }
}
