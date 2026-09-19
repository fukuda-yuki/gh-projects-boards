using FlaUI.Core.Input;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;
using System.Text.Json;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [Test]
    public void OrdinaryBoardsPlanningSavesRestartsAndPublishesReviewedProjections()
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
            Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
        });
        f.Run(w =>
        {
            OpenSaved(w, profile: true); Element(w, "GridCell0_0").Focus(); Invoke(w, "GridPlanning");
            Wait(() => Element(w, "PlanTaskFinish").AsTextBox().Text == "2026-10-06 18:00"); Invoke(w, "CloseButton");
            // Reconnect only for the explicit publication path.
            Invoke(w, "ConnectionPageButton"); Connect(w); Invoke(w, "ProjectsPageButton");
            Invoke(w, "ReviewApplyButton"); Wait(() => WorkspaceUi.ApplyRows(w).Length > 0);
            WorkspaceUi.ApplyRows(w)[0].Select(); WorkspaceUi.WaitForApplyReady(w);
            Capture(w, f.Root, "planning-apply-review"); Invoke(w, "PrimaryButton");
            Wait(() => Text(w, "RegistrationStatus").Contains("反映完了"));
            var payloads = File.ReadAllLines(Path.Combine(f.Root, "apply-requests.jsonl")).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToArray();
            Assert.That(payloads.Length, Is.EqualTo(3));
            Assert.That(payloads.Select(p => p.GetProperty("fieldId").GetString()), Is.EquivalentTo(new[] { "F-Estimate", "F-Start", "F-Finish" }));
            Capture(w, f.Root, "planning-published");
        });
        foreach (var screenshot in Directory.GetFiles(f.Root, "*.png")) TestContext.AddTestAttachment(screenshot);
    }
}
