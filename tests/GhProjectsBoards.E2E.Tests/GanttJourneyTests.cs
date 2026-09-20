using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [TestCase(false), TestCase(true), Category("GridIme")]
    public void GanttViewSwitchDoesNotEndNativeCompositionOrCommitTheCell(bool f2)
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, columns = true, itemCount = 3 }));
        f.Run(w => {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); WorkspaceUi.CloseProjectNavigation(w);
            var cell = Element(w, "GridCell0_0").AsTextBox(); cell.Click(); Wait(() => cell.Properties.HasKeyboardFocus.Value);
            if (f2) Key(VirtualKeyShort.F2);
            try
            {
                Keyboard.TypeVirtualKeyCode(0x16);
                Key(VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_H, VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_G, VirtualKeyShort.KEY_O);
                Assert.That(cell.Text, Is.EqualTo("にほんご")); Key(VirtualKeyShort.SPACE); Assert.That(cell.Text, Is.EqualTo("日本語"));
                Element(w, "ProjectViewGantt").Click();
                Assert.That(WorkspaceUi.HasVisibleElement(w, "GridCell0_0"), Is.True);
                Assert.That(cell.Properties.HasKeyboardFocus.Value, Is.True);
                Assert.That(cell.Text, Is.EqualTo("日本語"));
                Key(VirtualKeyShort.RETURN); // Native composition confirmation only.
                ChooseView(w, "ProjectViewGantt"); Wait(() => WorkspaceUi.HasVisibleElement(w, "GanttTasks"));
                ChooseView(w, "ProjectViewBoards"); Wait(() => WorkspaceUi.HasVisibleElement(w, "GridCell0_0"));
                Assert.That(CellText(w, 0), Is.EqualTo("日本語"));
                Capture(w, f.Root, "gantt-physical-ime-roundtrip");
            }
            finally { Keyboard.TypeVirtualKeyCode(0x1A); }
        });
        var title = Durable(f).GetProperty("Fields").EnumerateArray().Single(x => x.GetProperty("Key").GetProperty("Kind").GetString() == "Title" && x.GetProperty("Buffer").GetString() == "日本語");
        Assert.That(title.GetProperty("Buffer").GetString(), Is.EqualTo("日本語"));
        Assert.That(title.GetProperty("Change").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }

    [Test]
    public void OrdinaryGanttThousandTaskEditProjectSwitchAndRestartRetainOnePlan()
    {
        using var f = new Fixture();
        var seed = new ProcessStartInfo(Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!) { UseShellExecute = false, CreateNoWindow = true };
        seed.ArgumentList.Add("--seed-gantt"); seed.ArgumentList.Add(f.Data);
        using (var process = Process.Start(seed)!) { Assert.That(process.WaitForExit(30000), Is.True); Assert.That(process.ExitCode, Is.Zero); }
        f.Run(w => {
            w.Patterns.Transform.Pattern.Resize(1600, 1000); OpenSaved(w, "P1", profile: true); WorkspaceUi.CloseProjectNavigation(w);
            Element(w, "GridCell0_0").Focus(); Key(VirtualKeyShort.F2);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A); Keyboard.Type("pending gantt title");
            Wait(() => CellText(w, 0) == "pending gantt title");
            ChooseView(w, "ProjectViewGantt"); Wait(() => Text(w, "GanttSelected").Contains("#1"));
            Capture(w, f.Root, "gantt-ordinary-initial");
            Set(w, "GanttSearch", "#1000");
            Wait(() => Element(w, "GanttRow-P1T1000").IsAvailable);
            Element(w, "GanttRow-P1T1000").Patterns.SelectionItem.Pattern.Select();
            Invoke(w, "GanttReveal"); Wait(() => Text(w, "GanttSelected").Contains("2027-03-15 12:07"));
            Invoke(w, "GanttEdit"); Wait(() => WorkspaceUi.HasVisibleElement(w, "ScheduleFinish"));
            Assert.That(Element(w, "ScheduleStart").AsTextBox().Text, Is.EqualTo("2027-03-15 12:07"));
            Set(w, "ScheduleFinish", "2027-03-15 16:19"); Invoke(w, "ScheduleApply");
            Wait(() => Text(w, "GanttSelected").Contains("2027-03-15 16:19"));
            Capture(w, f.Root, "gantt-ordinary-manual-edited");
            OpenGanttProject(w, "P2"); Wait(() => WorkspaceUi.HasVisibleElement(w, "GridCell0_0"));
            Assert.That(CellText(w, 0), Is.EqualTo("pending gantt title"));
            OpenGanttProject(w, "P1"); Wait(() => WorkspaceUi.HasVisibleElement(w, "GanttSelected"));
            Assert.That(Text(w, "GanttSelected"), Does.Contain("#1000").And.Contain("16:19"));
            w.Patterns.Transform.Pattern.Resize(1200, 750);
            // The responsive transition closes the inline pane. Invoking the
            // toggle while its closing content still reports visible reopens it.
            Wait(() => Element(w, "ToggleProjectNavigation").Name == "Project一覧を表示" && !WorkspaceUi.HasVisibleElement(w, "SavedProfiles"));
            Invoke(w, "GanttReveal"); Capture(w, f.Root, "gantt-ordinary-narrow");
            Invoke(w, "GanttBoards"); Wait(() => WorkspaceUi.HasVisibleElement(w, "GridCell999_2"));
            Assert.That(CellText(w, 999, 2), Is.EqualTo("24未確定"));
            ChooseView(w, "ProjectViewGantt");
            Assert.That(f.Calls(), Is.Empty, "View/edit/replan/navigation is entirely local before reviewed Apply.");
        });
        var saved = Durable(f);
        Assert.That(saved.GetProperty("Planning")[0].GetProperty("Tasks").EnumerateArray().Single(t => t.GetProperty("Id").GetString() == "I1000").GetProperty("ManualFinish").GetString(), Is.EqualTo("2027-03-15T16:19:00"));
        f.Run(w => {
            w.Patterns.Transform.Pattern.Resize(1600, 1000); OpenSaved(w, "P1", profile: true); WorkspaceUi.CloseProjectNavigation(w);
            Assert.That(CellText(w, 0), Is.EqualTo("pending gantt title"));
            ChooseView(w, "ProjectViewGantt"); Set(w, "GanttSearch", "#1000");
            Wait(() => Element(w, "GanttRow-P1T1000").IsAvailable);
            Element(w, "GanttRow-P1T1000").Patterns.SelectionItem.Pattern.Select(); Invoke(w, "GanttReveal");
            Wait(() => Text(w, "GanttSelected").Contains("2027-03-15 16:19"));
            Assert.That(Text(w, "GanttSelected"), Does.Contain("Manual"));
            Capture(w, f.Root, "gantt-ordinary-restarted");
            Assert.That(f.Calls(), Is.Empty);
        });
        File.WriteAllText(Path.Combine(f.Root, "gantt-journey.json"), JsonSerializer.Serialize(new {
            tasks = 1000, projects = 2, input = "UIA plus native ASCII keys; Japanese pending effort was seeded, not physical IME evidence",
            endpoint = "real checkpoint and ordinary process restart; no GitHub requests", assertions = "same stable task, Manual override, shared pending title, Project-specific plan, pending effort, normal/narrow controls", humanAcceptance = "not run" }));
    }
    private static void ChooseView(Window w, string id)
    {
        var element = Element(w, id);
        if (element.Patterns.SelectionItem.IsSupported) element.Patterns.SelectionItem.Pattern.Select(); else element.Click();
    }
    private static void OpenGanttProject(Window w, string name)
    {
        WorkspaceUi.OpenProjectNavigation(w);
        var node = WorkspaceUi.ProjectNavigation(w).FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.TreeItem)));
        Assert.That(node, Is.Not.Null); node!.Patterns.Invoke.Pattern.Invoke();
        Wait(() => Text(w, "ProjectSummary") == name);
    }
}
