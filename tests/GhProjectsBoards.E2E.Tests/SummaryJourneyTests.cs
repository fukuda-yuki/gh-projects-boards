using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [Test]
    public void OrdinarySummaryThousandTaskEditRoundtripProjectIsolationAndRestart()
    {
        using var f = new Fixture();
        var seed = new ProcessStartInfo(Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!) { UseShellExecute = false, CreateNoWindow = true };
        seed.ArgumentList.Add("--seed-summary"); seed.ArgumentList.Add(f.Data);
        using (var process = Process.Start(seed)!) { Assert.That(process.WaitForExit(30000), Is.True); Assert.That(process.ExitCode, Is.Zero); }
        var baselineId = Durable(f).GetProperty("Planning")[0].GetProperty("Summary").GetProperty("Baseline").GetProperty("Id").GetString();
        f.Run(w => {
            w.Patterns.Transform.Pattern.Resize(1600, 1000); OpenSaved(w, "P1", profile: true); WorkspaceUi.CloseProjectNavigation(w);
            Element(w, "GridCell0_0").Focus(); Key(VirtualKeyShort.F2); Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A); Keyboard.Type("pending Summary title");
            ChooseView(w, "ProjectViewSummary"); Wait(() => Text(w, "SummaryPersonDetail").Contains("9 人日 / 72 人時"));
            Capture(w, f.Root, "summary-ordinary-initial");
            Invoke(w, "SummaryGantt"); Wait(() => Text(w, "GanttSelected").Contains("#1"));
            ChooseView(w, "ProjectViewSummary"); Invoke(w, "SummaryBoards"); Wait(() => CellText(w, 0) == "pending Summary title");
            ChooseView(w, "ProjectViewSummary"); Invoke(w, "SummaryEdit"); Wait(() => WorkspaceUi.HasVisibleElement(w, "PlanMode"));
            Element(w, "PlanSection-工数・進捗・実績").Patterns.ExpandCollapse.Pattern.Expand();
            Set(w, "PlanWork-Remaining", "96"); Invoke(w, "PrimaryButton");
            Wait(() => Text(w, "SummaryPersonDetail").Contains("12 人日 / 96 人時"));
            Capture(w, f.Root, "summary-ordinary-reestimated");
            OpenGanttProject(w, "P2"); ChooseView(w, "ProjectViewSummary"); Wait(() => Text(w, "SummaryContext").StartsWith("P2"));
            Invoke(w, "SummaryAllowance"); Wait(() => WorkspaceUi.HasVisibleElement(w, "SummaryAllowanceHours"));
            Assert.That(Element(w, "SummaryAllowanceHours").AsTextBox().Text, Is.EqualTo("8")); Invoke(w, "CloseButton");
            OpenGanttProject(w, "P1"); Wait(() => Text(w, "SummaryPersonDetail").Contains("12 人日 / 96 人時"));
            w.Patterns.Transform.Pattern.Resize(1200, 750);
            Wait(() => Element(w, "ToggleProjectNavigation").Name == "Project一覧を表示" && !WorkspaceUi.HasVisibleElement(w, "SavedProfiles"));
            Capture(w, f.Root, "summary-ordinary-narrow"); Assert.That(f.Calls(), Is.Empty);
        });
        var saved = Durable(f);
        Assert.That(saved.GetProperty("Planning")[0].GetProperty("Summary").GetProperty("Baseline").GetProperty("Id").GetString(), Is.EqualTo(baselineId));
        f.Run(w => {
            w.Patterns.Transform.Pattern.Resize(1600, 1000); OpenSaved(w, "P1", profile: true); WorkspaceUi.CloseProjectNavigation(w);
            Assert.That(CellText(w, 0), Is.EqualTo("pending Summary title")); ChooseView(w, "ProjectViewSummary");
            Wait(() => Text(w, "SummaryPersonDetail").Contains("12 人日 / 96 人時"));
            Capture(w, f.Root, "summary-ordinary-restarted"); Assert.That(f.Calls(), Is.Empty);
        });
        File.WriteAllText(Path.Combine(f.Root, "summary-journey.json"), JsonSerializer.Serialize(new {
            tasks = 1000, fixturePeople = 20, changedSet = 1, projects = 2, baselineId,
            endpoint = "ordinary app, native controls, real checkpoint, normal close and real restart; isolated gh boundary receives zero requests",
            expected = "A remaining72->96h; forecast120->144h; estimate144h/actual48h/baseline unchanged; P2 allowance8h; pending title retained",
            visible = "normal/narrow GDI window captures after independent public UI state; no physical presentation latency claimed", humanAcceptance = "not run" }));
    }
}
