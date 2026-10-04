using System.Globalization;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [Test]
    public void OrdinaryWeeklyPasteHundredOfThousandRowsUndoesRestartsAndApprovesOnlyVisibleEffort()
    {
        using var f = new Fixture(); using var clipboard = new NativeClipboardScope();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new {
            registration = true, apply = true, planning = true, planningAssignee = "U1", itemCount = 1000 }));
        var reportingDay = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(9)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var expected = Enumerable.Range(1, 100).SelectMany(i => new[] {
            ($"P1-T{i}/F-Remaining", ((i - 1) % 5).ToString(CultureInfo.InvariantCulture)),
            ($"P1-T{i}/F-Actual", i.ToString(CultureInfo.InvariantCulture)) }).ToDictionary(pair => pair.Item1, pair => pair.Item2);
        var tsv = string.Join("\r\n", Enumerable.Range(1, 100).Select(i => $"{(i - 1) % 5}\t{i}"));
        JsonElement[] WeeklyChanges(JsonElement checkpoint) => checkpoint.GetProperty("Fields").EnumerateArray()
            .Where(field => field.GetProperty("Key").GetProperty("FieldId").GetString() is "F-Actual" or "F-Remaining"
                && field.GetProperty("Change").ValueKind != JsonValueKind.Null).ToArray();
        void AssertAllReports(JsonElement checkpoint)
        {
            var tasks = checkpoint.GetProperty("Planning")[0].GetProperty("Tasks").EnumerateArray().ToArray();
            var reported = tasks.Where(task => task.GetProperty("Actuals").ValueKind == JsonValueKind.Array
                && task.GetProperty("Actuals").GetArrayLength() > 0).ToArray();
            Assert.That(reported.Select(task => task.GetProperty("Id").GetString()),
                Is.EquivalentTo(Enumerable.Range(1, 100).Select(i => "I" + i)));
            foreach (var task in reported)
            {
                var report = task.GetProperty("Actuals").EnumerateArray().Single();
                Assert.That(report.GetProperty("PersonId").GetString(), Is.EqualTo("U1"));
                Assert.That(report.GetProperty("Hours").GetDecimal(), Is.EqualTo(int.Parse(task.GetProperty("Id").GetString()![1..], CultureInfo.InvariantCulture)));
                Assert.That(report.GetProperty("ReportedThrough").GetString(), Is.EqualTo(reportingDay));
            }
        }
        void Paste(Window w)
        {
            Element(w, "GridCell0_3").Focus(); Wait(() => Element(w, "GridCell0_3").Properties.HasKeyboardFocus.Value);
            NativeClipboardScope.WriteTestFormats(tsv);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
            Wait(() => WeeklyChanges(Durable(f)).Length == 200);
            var checkpoint = Durable(f); AssertAllReports(checkpoint);
            Assert.That(WeeklyChanges(checkpoint).ToDictionary(field => field.GetProperty("Key").GetProperty("NodeId").GetString()
                + "/" + field.GetProperty("Key").GetProperty("FieldId").GetString(), field => field.GetProperty("Change").GetProperty("Value").GetString()),
                Is.EquivalentTo(expected), "The 100-row native paste must change exactly its 200 destinations; the other 900 rows stay untouched.");
            Assert.That(CellText(w, 0, 3), Is.EqualTo("0")); Assert.That(CellText(w, 0, 4), Is.EqualTo("1"));
            Assert.That(CellText(w, 1, 3), Is.EqualTo("1")); Assert.That(CellText(w, 1, 4), Is.EqualTo("2"));
        }
        f.Run(w =>
        {
            w.Patterns.Transform.Pattern.Resize(2200, 1100);
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1);
            Assert.That(WorkspaceUi.ProjectInformation(w), Does.Contain("項目 1000 / Issue 1000"));
            ConfigureWeeklyPlanning(w);
            EditWeeklyTitle(w, 0, "Weekly target 1"); EditWeeklyTitle(w, 1, "Weekly target 2");
            Element(w, "GridCell0_2").Focus(); Wait(() => Element(w, "GridCell0_2").Properties.HasKeyboardFocus.Value);
            Key(VirtualKeyShort.F2); Keyboard.Type("16"); Key(VirtualKeyShort.RETURN);
            Wait(() => CellText(w, 0, 2) == "16" && CellText(w, 0, 6) == "2026-10-06");
            Paste(w); Capture(w, f.Root, "weekly-100-pasted");
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
            Wait(() => WeeklyChanges(Durable(f)).Length == 0);
            Assert.That(Durable(f).GetProperty("Planning")[0].GetProperty("Tasks").EnumerateArray()
                .All(task => task.GetProperty("Actuals").ValueKind == JsonValueKind.Null || task.GetProperty("Actuals").GetArrayLength() == 0), Is.True);
            Assert.That(CellText(w, 0, 2), Is.EqualTo("16")); Assert.That(CellText(w, 0), Is.EqualTo("Weekly target 1"));
            Paste(w);
            Invoke(w, "GridRowSettings"); Set(w, "RowTitleFilter", "Weekly target"); SaveRows(w);
            Wait(() => Text(w, "RowViewStatus").Contains("2/1000行"));
            Capture(w, f.Root, "weekly-filtered-for-approval");
            Assert.That(f.Calls().Any(call => call.GetProperty("mutation").GetBoolean()), Is.False);
        });
        var calls = f.Calls().Length;
        f.Run(w =>
        {
            w.Patterns.Transform.Pattern.Resize(2200, 1100); OpenSaved(w, profile: true);
            Wait(() => CellText(w, 0, 4) == "1" && CellText(w, 1, 4) == "2");
            Assert.That(Text(w, "RowViewStatus"), Does.Contain("2/1000行"));
            AssertAllReports(Durable(f)); Assert.That(WeeklyChanges(Durable(f)), Has.Length.EqualTo(200));
            Assert.That(f.Calls(), Has.Length.EqualTo(calls), "Normal close/restart must restore local weekly work without GitHub access.");
            Invoke(w, "ConnectionPageButton"); Connect(w); Invoke(w, "ProjectsPageButton");
            Invoke(w, "ReviewWeeklyApplyButton"); WorkspaceUi.WaitForApplyReady(w);
            Assert.That(WorkspaceUi.ApplyRows(w), Has.Length.EqualTo(2));
            Assert.That(Element(w, "ApplyIncludeHidden").AsCheckBox().IsChecked, Is.False);
            Assert.That(Element(w, "ApplySelectAll").AsCheckBox().IsChecked, Is.True);
            var values = Element(w, "ApplyReviewDialog").FindAllDescendants()
                .Select(element => element.Properties.AutomationId.ValueOrDefault).Where(id => id?.StartsWith("ApplyValue-", StringComparison.Ordinal) == true).ToArray();
            Assert.That(values, Is.EquivalentTo(new[] {
                "ApplyValue-P1-T1-Field-F-Actual", "ApplyValue-P1-T1-Field-F-Remaining",
                "ApplyValue-P1-T2-Field-F-Actual", "ApplyValue-P1-T2-Field-F-Remaining" }));
            Assert.That(f.Calls().Any(call => call.GetProperty("mutation").GetBoolean()), Is.False);
            Capture(w, f.Root, "weekly-final-review"); Invoke(w, "PrimaryButton");
            Wait(() => WorkspaceUi.RegistrationStatusText(w).Contains("反映完了"));
            var checkpoint = Durable(f); var batch = checkpoint.GetProperty("Journal").EnumerateArray().Single();
            Assert.That(batch.GetProperty("WeeklyEffort").GetBoolean(), Is.True);
            var operations = batch.GetProperty("Operations").EnumerateArray().ToArray();
            Assert.That(operations, Has.Length.EqualTo(4));
            Assert.That(operations.All(operation => operation.GetProperty("State").GetInt32() == 2
                && operation.GetProperty("Verification").ValueKind == JsonValueKind.Object), Is.True,
                "Each ordinary Apply operation must retain independent readback, not only a mutation acknowledgement.");
            var writes = File.ReadAllLines(Path.Combine(f.Root, "apply-requests.jsonl"))
                .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            var sent = writes.ToDictionary(write => write.GetProperty("itemId").GetString() + "/" + write.GetProperty("fieldId").GetString(),
                write => write.GetProperty("value").GetProperty("number").GetDecimal().ToString(CultureInfo.InvariantCulture));
            Assert.That(sent, Is.EquivalentTo(expected.Where(pair => pair.Key.StartsWith("P1-T1/", StringComparison.Ordinal)
                || pair.Key.StartsWith("P1-T2/", StringComparison.Ordinal))));
            Assert.That(WeeklyChanges(checkpoint).ToDictionary(field => field.GetProperty("Key").GetProperty("NodeId").GetString()
                + "/" + field.GetProperty("Key").GetProperty("FieldId").GetString(), field => field.GetProperty("Change").GetProperty("Value").GetString()),
                Is.EquivalentTo(expected.Where(pair => !pair.Key.StartsWith("P1-T1/", StringComparison.Ordinal)
                    && !pair.Key.StartsWith("P1-T2/", StringComparison.Ordinal))), "The exact values of all 98 hidden changed rows remain local.");
            AssertAllReports(checkpoint);
            var local = checkpoint.GetProperty("Fields").EnumerateArray().Where(field => field.GetProperty("Change").ValueKind != JsonValueKind.Null).ToArray();
            Assert.That(local.Count(field => field.GetProperty("Key").GetProperty("Kind").GetString() == "Title"), Is.EqualTo(2));
            Assert.That(local.Where(field => field.GetProperty("Key").GetProperty("FieldId").GetString() is "F-Estimate" or "F-Start" or "F-Finish")
                .Select(field => field.GetProperty("Key").GetProperty("FieldId").GetString()), Is.EquivalentTo(new[] { "F-Estimate", "F-Start", "F-Finish" }));
            Assert.That(CellText(w, 0), Is.EqualTo("Weekly target 1")); Assert.That(CellText(w, 0, 2), Is.EqualTo("16"));
            Capture(w, f.Root, "weekly-published-effort-local-other-changes");
        });
        File.WriteAllText(Path.Combine(f.Root, "weekly-journey.json"), JsonSerializer.Serialize(new {
            rows = 1000, pastedRows = 100, pastedCells = 200, confirmedRows = 2, confirmedFields = 4, hiddenLocalRows = 98, reportingDay,
            endpoint = "Ordinary WinUI executable, native clipboard Ctrl+V/Undo, local checkpoint and real restart, explicit approval, isolated fake-gh dispatch and independent readback.",
            liveGitHub = false, humanAcceptance = "not run", performance = "not measured" }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var artifact in Directory.GetFiles(f.Root, "weekly-*")) TestContext.AddTestAttachment(artifact);
    }

    private static void ConfigureWeeklyPlanning(Window w)
    {
        Invoke(w, "GridPlanningSettings"); Wait(() => WorkspaceUi.HasVisibleElement(w, "PlanSettingsSave"));
        Invoke(w, "PlanProjectStart-Direct"); Set(w, "PlanProjectStart", "2026-10-05 09:00");
        Invoke(w, "PlanCutoff-Direct"); Set(w, "PlanCutoff", "2026-10-05 09:00");
        foreach (var (role, index) in new[] { ("Estimate", 1), ("Remaining", 2), ("Actual", 3), ("Start", 1), ("Finish", 2) })
        {
            Element(w, "PlanField-" + role).Focus(); Key(VirtualKeyShort.HOME);
            for (var i = 0; i < index; i++) Key(VirtualKeyShort.DOWN);
            Key(VirtualKeyShort.TAB);
        }
        Element(w, "PlanSection-担当者・配賦").Patterns.ExpandCollapse.Pattern.Expand();
        Element(w, "PlanPerson-U1").Patterns.Toggle.Pattern.Toggle(); Set(w, "PlanWeight-U1", "100");
        Invoke(w, "PlanSettingsSave"); Wait(() => !WorkspaceUi.HasVisibleElement(w, "PlanSettingsSave"));
        Wait(() => WorkspaceUi.HasVisibleElement(w, "GridCell0_2"));
    }

    private static void EditWeeklyTitle(Window w, int row, string text)
    {
        // Focus the public cell identity after the settings page is removed;
        // the disappearing setup hint can still move its screen coordinates.
        var id = $"GridCell{row}_0";
        Element(w, id).Focus(); Wait(() => Element(w, id).Properties.HasKeyboardFocus.Value);
        Key(VirtualKeyShort.F2); Set(w, id, text); Key(VirtualKeyShort.RETURN);
        Wait(() => CellText(w, row) == text);
    }
}
