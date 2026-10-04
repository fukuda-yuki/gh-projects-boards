using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [Test]
    public void OrdinaryDependencyDragAndTwoWorkerAllocationCancelUndoAndRestartKeepOneLocalPlan()
    {
        using var f = new Fixture();
        var seed = new ProcessStartInfo(Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!) { UseShellExecute = false, CreateNoWindow = true };
        seed.ArgumentList.Add("--seed-planning-check"); seed.ArgumentList.Add(f.Data); seed.ArgumentList.Add("Weekly");
        using (var process = Process.Start(seed)!) { Assert.That(process.WaitForExit(30000), Is.True); Assert.That(process.ExitCode, Is.Zero); }
        var initial = Durable(f); var originalTask = TaskRecord(initial, "I1");
        var originalStart = Scalar(initial, "P1T1", "F-Start"); var originalFinish = Scalar(initial, "P1T1", "F-Finish");
        var reportDay = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(9)).ToString("yyyy-MM-dd");
        Assert.That(originalTask.GetProperty("Actuals")[0].GetProperty("Hours").GetDecimal(), Is.EqualTo(5));
        Assert.That(Scalar(initial, "P1T1", "F-Remaining"), Is.EqualTo("4"));
        Assert.That(DependencyPresent(initial, "I3", "I1"), Is.False);

        f.Run(w => {
            w.Patterns.Transform.Pattern.Resize(1600, 1000); OpenSaved(w, "P1", profile: true); WorkspaceUi.CloseProjectNavigation(w);
            ChooseView(w, "ProjectViewGantt"); Wait(() => WorkspaceUi.HasVisibleElement(w, "GanttRow-P1T3"));
            // A Rectangle bar has no required UIA peer. Its visible endpoint grip
            // and row expose public geometry without a product test hook.
            Element(w, "GanttRow-P1T3").Patterns.SelectionItem.Pattern.Select();
            Wait(() => WorkspaceUi.HasVisibleElement(w, "GanttDependencyStart-P1T3"));
            var targetEndpoint = Element(w, "GanttDependencyStart-P1T3").BoundingRectangle;
            Element(w, "GanttRow-P1T1").Patterns.SelectionItem.Pattern.Select();
            Wait(() => WorkspaceUi.HasVisibleElement(w, "GanttDependencyStart-P1T1"));
            var source = Element(w, "GanttDependencyStart-P1T1").BoundingRectangle;
            var targetRow = Element(w, "GanttRow-P1T3").BoundingRectangle;
            var destination = new Point(targetEndpoint.Left - Math.Max(2, targetEndpoint.Width / 4), targetRow.Top + targetRow.Height / 2);
            Assert.That(w.BoundingRectangle.Contains(destination), Is.True, "The native drop must reach the visible successor bar.");
            NativePointer.Drag(w, new Point(source.Left + source.Width / 2, source.Top + source.Height / 2), destination, () => {
                Wait(() => Text(w, "GanttSelected").Contains("依存プレビュー"));
                Assert.That(Text(w, "GanttSelected"), Does.Contain("#1").And.Contain("#3"));
                Assert.That(DependencyPresent(Durable(f), "I3", "I1"), Is.False, "A native drag preview must not persist an edge.");
                Capture(w, f.Root, "next-roadmap-dependency-preview");
            });
            Wait(() => DependencyPresent(Durable(f), "I3", "I1"));
            Assert.That(Scalar(Durable(f), "P1T1", "F-Start"), Is.EqualTo(originalStart));
            Assert.That(Scalar(Durable(f), "P1T1", "F-Finish"), Is.EqualTo(originalFinish));
            Wait(() => WorkspaceUi.HasVisibleElement(w, "GanttLinkSelect-I1-I3"));
            Element(w, "GanttLinkSelect-I1-I3").Click();
            Wait(() => Text(w, "GanttSelected").Contains("依存を選択"));
            Capture(w, f.Root, "next-roadmap-dependency-selected"); Invoke(w, "GanttRemoveDependency");
            Wait(() => !DependencyPresent(Durable(f), "I3", "I1"));
            Invoke(w, "GanttUndo"); Wait(() => DependencyPresent(Durable(f), "I3", "I1"));

            ChooseView(w, "ProjectViewBoards"); Wait(() => WorkspaceUi.HasVisibleElement(w, "GridCell0_4"));
            OpenAllocation(w); var beforeCancel = Durable(f);
            EnterAllocation(w, addWorker: true);
            Capture(w, f.Root, "next-roadmap-allocation-candidate");
            Invoke(w, "ActualReportsCancel"); Wait(() => !WorkspaceUi.HasVisibleElement(w, "ActualReportsUpdate"));
            Assert.That(TaskRecord(Durable(f), "I1").GetRawText(), Is.EqualTo(TaskRecord(beforeCancel, "I1").GetRawText()));
            Assert.That(Durable(f).GetProperty("History").GetArrayLength(), Is.EqualTo(beforeCancel.GetProperty("History").GetArrayLength()));
            Assert.That(CellText(w, 0, 4), Is.EqualTo("5")); Assert.That(CellText(w, 0, 3), Is.EqualTo("4"));

            OpenAllocation(w); EnterAllocation(w, addWorker: true); Invoke(w, "ActualReportsUpdate");
            Wait(() => !WorkspaceUi.HasVisibleElement(w, "ActualReportsUpdate") && Scalar(Durable(f), "P1T1", "F-Actual") == "7");
            AssertAllocation(Durable(f));
            Assert.That(CellText(w, 0, 4), Is.EqualTo("7")); Assert.That(CellText(w, 0, 3), Is.EqualTo("3"));
            Invoke(w, "GridUndo"); Wait(() => Scalar(Durable(f), "P1T1", "F-Actual") == "5");
            Assert.That(Scalar(Durable(f), "P1T1", "F-Remaining"), Is.EqualTo("4"));
            Assert.That(TaskRecord(Durable(f), "I1").GetRawText(), Is.EqualTo(originalTask.GetRawText()));
            Assert.That(DependencyPresent(Durable(f), "I3", "I1"), Is.True, "Allocation Undo must not remove the preceding dependency operation.");
            OpenAllocation(w); EnterAllocation(w, addWorker: true); Invoke(w, "ActualReportsUpdate");
            Wait(() => !WorkspaceUi.HasVisibleElement(w, "ActualReportsUpdate") && Scalar(Durable(f), "P1T1", "F-Actual") == "7");
            AssertAllocation(Durable(f)); Capture(w, f.Root, "next-roadmap-allocation-saved");
            Assert.That(f.Calls(), Is.Empty, "All preparation and Undo are local, including after cached startup.");
        });
        AssertAllocation(Durable(f));
        f.Run(w => {
            w.Patterns.Transform.Pattern.Resize(1600, 1000); OpenSaved(w, "P1", profile: true); WorkspaceUi.CloseProjectNavigation(w);
            Wait(() => CellText(w, 0, 4) == "7" && CellText(w, 0, 3) == "3");
            OpenAllocation(w);
            Assert.That(Element(w, "ActualReportHours-U1").AsTextBox().Text, Is.EqualTo("5"));
            Assert.That(Element(w, "ActualReportHours-U2").AsTextBox().Text, Is.EqualTo("2"));
            Assert.That(Element(w, "ActualReportRemaining-U1").AsTextBox().Text, Is.EqualTo("1"));
            Assert.That(Element(w, "ActualReportRemaining-U2").AsTextBox().Text, Is.EqualTo("1.5"));
            Assert.That(Text(w, "ActualReportsTotal"), Does.Contain("未配分 0.5"));
            Capture(w, f.Root, "next-roadmap-allocation-restarted"); Invoke(w, "ActualReportsCancel");
            Wait(() => !WorkspaceUi.HasVisibleElement(w, "ActualReportsUpdate"));
            ChooseView(w, "ProjectViewGantt"); Element(w, "GanttRow-P1T1").Patterns.SelectionItem.Pattern.Select();
            Wait(() => WorkspaceUi.HasVisibleElement(w, "GanttLinkSelect-I1-I3"));
            AssertAllocation(Durable(f)); Assert.That(DependencyPresent(Durable(f), "I3", "I1"), Is.True);
            Capture(w, f.Root, "next-roadmap-dependency-restarted"); Assert.That(f.Calls(), Is.Empty);
        });
        var evidence = Path.Combine(f.Root, "next-roadmap-journey.json");
        File.WriteAllText(evidence, JsonSerializer.Serialize(new {
            tasks = 20, fixture = "existing Weekly planning-check seed", project = "P1", sourceTask = "I1", successorTask = "I3",
            dependency = "Native connector preview, release, selection, explicit delete and Undo; source interval unchanged; edge restored after normal restart.",
            allocation = "Explicitly added U2; Actual U1=5/U2=2, Remaining total3 with U1=1/U2=1.5 and unassigned0.5; Cancel unchanged; one Undo restores Actual5/Remaining4 and preceding edge; saved values reread after normal restart.",
            endpoint = "Ordinary WinUI executable, public UIA and native mouse, real isolated checkpoint and normal close/restart. External fake-gh boundary receives zero requests.",
            reportDay, liveGitHub = false, humanAcceptance = "not run", performance = "not measured"
        }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(evidence);
        foreach (var file in Directory.GetFiles(f.Root, "next-roadmap-*.png")) TestContext.AddTestAttachment(file);

        void AssertAllocation(JsonElement saved)
        {
            var task = TaskRecord(saved, "I1");
            Assert.That(Scalar(saved, "P1T1", "F-Actual"), Is.EqualTo("7"));
            Assert.That(Scalar(saved, "P1T1", "F-Remaining"), Is.EqualTo("3"));
            var reports = task.GetProperty("Actuals").EnumerateArray().ToDictionary(r => r.GetProperty("PersonId").GetString()!);
            Assert.That(reports.Keys, Is.EquivalentTo(new[] { "U1", "U2" }));
            Assert.That(reports["U1"].GetProperty("Hours").GetDecimal(), Is.EqualTo(5));
            Assert.That(reports["U1"].GetProperty("ReportedThrough").GetString(), Is.EqualTo("2026-10-05"));
            Assert.That(reports["U2"].GetProperty("Hours").GetDecimal(), Is.EqualTo(2));
            Assert.That(reports["U2"].GetProperty("ReportedThrough").GetString(), Is.EqualTo(reportDay));
            var shares = task.GetProperty("Contributions").EnumerateArray().ToDictionary(c => c.GetProperty("PersonId").GetString()!);
            Assert.That(shares.Keys, Is.EquivalentTo(new[] { "U1", "U2" }));
            Assert.That(shares["U1"].GetProperty("RemainingHours").GetDecimal(), Is.EqualTo(1));
            Assert.That(shares["U2"].GetProperty("RemainingHours").GetDecimal(), Is.EqualTo(1.5m));
            Assert.That(shares.Values.All(s => s.GetProperty("EstimateHours").ValueKind == JsonValueKind.Null), Is.True);
            foreach (var property in new[] { "Mode", "OwnerId", "Progress", "ActualStart", "ActualFinish", "Assignment" })
                Assert.That(task.GetProperty(property).GetRawText(), Is.EqualTo(originalTask.GetProperty(property).GetRawText()));
            Assert.That(saved.GetProperty("Journal").GetArrayLength(), Is.Zero);
        }
        static JsonElement TaskRecord(JsonElement saved, string taskId) => saved.GetProperty("Planning").EnumerateArray()
            .Single(p => p.GetProperty("ProjectId").GetString() == "P1").GetProperty("Tasks").EnumerateArray().Single(t => t.GetProperty("Id").GetString() == taskId).Clone();
        static string? Scalar(JsonElement saved, string rowId, string fieldId)
        {
            var field = saved.GetProperty("Fields").EnumerateArray().Single(f => f.GetProperty("Key").GetProperty("NodeId").GetString() == rowId
                && f.GetProperty("Key").GetProperty("FieldId").GetString() == fieldId);
            return field.GetProperty("Change").ValueKind == JsonValueKind.Null ? field.GetProperty("Baseline").GetString() : field.GetProperty("Change").GetProperty("Value").GetString();
        }
        static bool DependencyPresent(JsonElement saved, string taskId, string predecessor) => saved.GetProperty("Fields").EnumerateArray().Any(f => {
            var key = f.GetProperty("Key");
            return key.GetProperty("Kind").GetString() == "Dependency" && key.GetProperty("NodeId").GetString() == taskId
                && key.GetProperty("FieldId").GetString() == predecessor
                && (f.GetProperty("Change").ValueKind == JsonValueKind.Null ? f.GetProperty("Baseline").GetString() : f.GetProperty("Change").GetProperty("Value").GetString()) == "present";
        });
        static void OpenAllocation(Window window)
        {
            Element(window, "GridCell0_4").Focus();
            Wait(() => Element(window, "GridCell0_4").Properties.HasKeyboardFocus.Value);
            if (!WorkspaceUi.HasVisibleElement(window, "ActualDetails")) Invoke(window, "ActualContext");
            Invoke(window, "ActualDetails"); Wait(() => WorkspaceUi.HasVisibleElement(window, "ActualReportsUpdate"));
        }
        static void EnterAllocation(Window window, bool addWorker)
        {
            if (addWorker)
            {
                WorkspaceUi.SelectCombo(window, "ActualReportAddWorker", "Owner2"); Invoke(window, "ActualReportAdd");
                Wait(() => WorkspaceUi.HasVisibleElement(window, "ActualReportHours-U2"));
            }
            Set(window, "ActualReportHours-U2", "2"); Set(window, "ActualReportRemaining-U2", "1.5");
            Element(window, "ActualReportHours-U1").Focus();
            Set(window, "ActualReportHours-U1", "5"); Set(window, "ActualReportRemaining-U1", "1");
            Set(window, "ActualReportsRemainingTotal", "3");
            Wait(() => Text(window, "ActualReportsTotal").Contains("未配分 0.5"));
        }
    }
}
