using System.Security.Cryptography;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [Test]
    public void OrdinaryHolidayPickerPreviewsBeforeExplicitAdoptionAndRetainsCurrentWorkAfterRestart()
    {
        using var f = new Fixture(); using var clipboard = new NativeClipboardScope();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new {
            registration = true, planning = true, planningAssignee = "U1", itemCount = 2 }));
        var selectedFile = Path.Combine(f.Root, "syukujitsu.csv");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "JapanHolidays.csv"), selectedFile);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(selectedFile)));
        string? beforeVersion = null;
        JsonElement beforePlan = default;
        void AssertProtectedPlanning(JsonElement checkpoint)
        {
            var current = checkpoint.GetProperty("Planning")[0];
            foreach (var property in new[] { "Fields", "People", "Tasks", "Start", "Cutoff" })
                Assert.That(current.GetProperty(property).GetRawText(), Is.EqualTo(beforePlan.GetProperty(property).GetRawText()), property + " must be retained.");
            Assert.That(current.GetProperty("Calendar").GetProperty("Exceptions").GetRawText(),
                Is.EqualTo(beforePlan.GetProperty("Calendar").GetProperty("Exceptions").GetRawText()));
            Assert.That(checkpoint.GetProperty("Journal").GetArrayLength(), Is.Zero);
        }
        f.Run(w =>
        {
            w.Patterns.Transform.Pattern.Resize(2200, 1100);
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1); ConfigureWeeklyPlanning(w);
            EditWeeklyTitle(w, 0, "Local holiday work");
            Element(w, "GridCell0_2").Focus(); Wait(() => Element(w, "GridCell0_2").Properties.HasKeyboardFocus.Value);
            Key(VirtualKeyShort.F2); Keyboard.Type("16"); Key(VirtualKeyShort.RETURN);
            Wait(() => CellText(w, 0, 6) == "2026-10-06");
            Element(w, "GridCell0_3").Focus(); Wait(() => Element(w, "GridCell0_3").Properties.HasKeyboardFocus.Value);
            NativeClipboardScope.WriteTestFormats("4\t5\r\n3\t6");
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
            Wait(() => CellText(w, 0, 4) == "5" && CellText(w, 1, 4) == "6");
            Wait(() => Durable(f).GetProperty("Planning")[0].GetProperty("Tasks").EnumerateArray()
                .Count(task => task.GetProperty("Actuals").ValueKind == JsonValueKind.Array && task.GetProperty("Actuals").GetArrayLength() == 1) == 2);
            beforePlan = Durable(f).GetProperty("Planning")[0].Clone();
            beforeVersion = beforePlan.GetProperty("Calendar").GetProperty("Holidays").GetProperty("Version").GetString();
            Invoke(w, "GridPlanningSettings"); Wait(() => WorkspaceUi.HasVisibleElement(w, "PlanSettingsSave"));
            Element(w, "PlanSection-カレンダー・祝日").Patterns.ExpandCollapse.Pattern.Expand();
            ScrollCalendarIntoView(w);
            Element(w, "PlanImportHolidays").Focus(); Invoke(w, "PlanImportHolidays");
            var picker = ConnectionTests.WaitForPicker(w);
            var fileName = picker.FindFirstDescendant(cf => cf.ByAutomationId("1148").And(cf.ByClassName("Edit")))!.AsTextBox();
            fileName.Text = selectedFile; Assert.That(fileName.Text, Is.EqualTo(selectedFile));
            Capture(picker, f.Root, "holiday-native-selected-file");
            picker.FindFirstDescendant(cf => cf.ByAutomationId("1").And(cf.ByClassName("Button")))!.AsButton().Invoke();
            Wait(() => {
                // PickerHost closes asynchronously; the owner's UIA provider can
                // time out transiently before the returned file is presented.
                try { return w.FindFirstDescendant(cf => cf.ByAutomationId("PlanAdoptImportedHolidays")) is not null; }
                catch (System.Runtime.InteropServices.COMException error) when (error.HResult == unchecked((int)0x80131505)) { return false; }
            });
            Assert.That(Text(w, "HolidayImportStatus"), Does.Contain("syukujitsu.csv").And.Contain("2025–2027年").And.Contain("変更0日"));
            Assert.That(Element(w, "PlanAdoptImportedHolidays").AsCheckBox().IsChecked, Is.False);
            Assert.That(Durable(f).GetProperty("Planning")[0].GetProperty("Calendar").GetProperty("Holidays").GetProperty("Version").GetString(), Is.EqualTo(beforeVersion));
            Element(w, "HolidayImportDetails").Patterns.ExpandCollapse.Pattern.Expand();
            ScrollCalendarIntoView(w);
            Assert.That(Element(w, "HolidayImportDetails").FindAllDescendants().Select(element => element.Name),
                Has.Some.Contains(hash), "The preview must expose provenance of the actual selected bytes.");
            Capture(w, f.Root, "holiday-import-preview");
            Invoke(w, "PlanImportHolidays"); picker = ConnectionTests.WaitForPicker(w);
            picker.FindFirstDescendant(cf => cf.ByAutomationId("2").And(cf.ByClassName("Button")))!.AsButton().Invoke();
            Wait(() => Element(w, "PlanImportHolidays").IsEnabled);
            Assert.That(Text(w, "HolidayImportStatus"), Does.Contain("syukujitsu.csv"));
            Assert.That(Element(w, "PlanAdoptImportedHolidays").AsCheckBox().IsChecked, Is.False);
            Element(w, "PlanAdoptImportedHolidays").AsCheckBox().IsChecked = true;
            Assert.That(Durable(f).GetProperty("Planning")[0].GetProperty("Calendar").GetProperty("Holidays").GetProperty("Version").GetString(), Is.EqualTo(beforeVersion));
            Invoke(w, "PlanSettingsSave"); Wait(() => !WorkspaceUi.HasVisibleElement(w, "PlanSettingsSave"));
            var saved = Durable(f); AssertProtectedPlanning(saved);
            var holidays = saved.GetProperty("Planning")[0].GetProperty("Calendar").GetProperty("Holidays");
            Assert.That(holidays.GetProperty("Version").GetString(), Does.StartWith("csv-2025-2027-"));
            Assert.That(holidays.GetProperty("SourceSha256").GetString(), Is.EqualTo(hash));
            Assert.That(holidays.GetProperty("Source").GetString(), Does.Contain("Local CSV import: syukujitsu.csv"));
            Assert.That(CellText(w, 0), Is.EqualTo("Local holiday work")); Assert.That(CellText(w, 0, 2), Is.EqualTo("16"));
            Assert.That(CellText(w, 0, 3), Is.EqualTo("4")); Assert.That(CellText(w, 0, 4), Is.EqualTo("5"));
            Assert.That(CellText(w, 0, 6), Is.EqualTo("2026-10-06"));
            Capture(w, f.Root, "holiday-adopted-local-work-retained");
            Assert.That(f.Calls().Any(call => call.GetProperty("mutation").GetBoolean()), Is.False);
        });
        var calls = f.Calls().Length;
        f.Run(w =>
        {
            w.Patterns.Transform.Pattern.Resize(2200, 1100); OpenSaved(w, profile: true);
            AssertProtectedPlanning(Durable(f));
            Assert.That(Durable(f).GetProperty("Planning")[0].GetProperty("Calendar").GetProperty("Holidays").GetProperty("SourceSha256").GetString(), Is.EqualTo(hash));
            Assert.That(CellText(w, 0), Is.EqualTo("Local holiday work")); Assert.That(CellText(w, 1, 4), Is.EqualTo("6"));
            Assert.That(f.Calls(), Has.Length.EqualTo(calls)); Capture(w, f.Root, "holiday-restarted");
        });
        File.WriteAllText(Path.Combine(f.Root, "holiday-picker-journey.json"), JsonSerializer.Serialize(new {
            selectedFile, sourceSha256 = hash, beforeVersion, changedDates = 0,
            endpoint = "Ordinary WinUI executable, native Windows FileOpenPicker selection and cancellation, preview, explicit adoption and save, normal close/restart.",
            liveGitHub = false, humanAcceptance = "not run" }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var artifact in Directory.GetFiles(f.Root, "holiday-*")) TestContext.AddTestAttachment(artifact);
    }

    private static void ScrollCalendarIntoView(Window w)
    {
        // The layout Grid has no UIA peer. Follow the actual import control's
        // public ancestry to the settings ScrollViewer instead.
        var scroll = Element(w, "PlanImportHolidays").Parent;
        while (scroll is not null && !(scroll.Patterns.Scroll.IsSupported && scroll.Patterns.Scroll.Pattern.VerticallyScrollable.Value))
            scroll = scroll.Parent;
        Assert.That(scroll, Is.Not.Null, "The calendar must have a public scroll route.");
        scroll!.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
        Wait(() => !Element(w, "PlanImportHolidays").IsOffscreen);
    }
}
