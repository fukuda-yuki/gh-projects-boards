using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [Test]
    public void KeyboardDetailsDisambiguateRepositoriesFieldsAndLongChangedTitles()
    {
        using var fixture = new Fixture(); using var clipboard = new NativeClipboardScope();
        File.WriteAllText(Path.Combine(fixture.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, reviewInformation = true, reviewDetails = true, itemCount = 2 }));
        fixture.Run(window =>
        {
            Connect(window); Invoke(window, "ProjectsPageButton"); Register(window, 1);
            var title = new string('題', 100) + "変更した末尾"; var longValue = new string('長', 100) + "末尾の値";
            NativeClipboardScope.WriteTestFormats(string.Join("\r\n", Enumerable.Repeat(title + "\tReady\t" + longValue + "\tM", 2)));
            Element(window, "GridCell0_0").Click(); Invoke(window, "GridPaste");
            Wait(() => Text(window, "DraftStatus").Contains("GitHub未反映 8セル") && Text(window, "DraftStatus").Contains("ローカル保存済み"));
            Invoke(window, "ReviewApplyButton");
            Wait(() => Element(window, "ApplyCheckStatus").Name.Contains("最新確認済み"));
            var list = Element(window, "ApplyTargetRows").AsListBox();
            foreach (var row in new[] { 1, 2 })
            {
                if (list.Patterns.Scroll.Pattern.VerticallyScrollable.Value) list.Patterns.Scroll.Pattern.SetScrollPercent(-1, row == 1 ? 0 : 100);
                var details = Element(window, "ApplyDetails-P1-T" + row); details.Focus(); Keyboard.Type(VirtualKeyShort.SPACE);
                Wait(() => WorkspaceUi.HasVisibleElement(window, "ApplyFullDetails-P1-T" + row));
                var full = Element(window, "ApplyDetailScroll-P1-T" + row);
                var text = string.Join("\n", full.FindAllDescendants().Select(e => e.Name));
                Assert.That(text, Does.Contain(row == 1 ? "sample-user/first #1" : "sample-user/second #1")
                    .And.Contain("同名 [P1-field-1]").And.Contain("同名 [P1-field-2]")
                    .And.Contain("元の末尾 " + row).And.Contain(title).And.Contain(longValue));
                Capture(window, fixture.Root, "long-details-" + row + "-top");
                full.Focus(); Keyboard.Type(VirtualKeyShort.END);
                Wait(() => full.Patterns.Scroll.Pattern.VerticalScrollPercent.Value >= 99);
                Capture(window, fixture.Root, "long-details-" + row + "-bottom");
                Keyboard.Type(VirtualKeyShort.ESCAPE);
                Wait(() => details.Properties.HasKeyboardFocus.Value);
                Assert.That(Element(window, "ApplyTargetCounts").Name, Is.EqualTo("2件中0件を選択"));
            }
            Capture(window, fixture.Root, "long-identities-comparison");
            Assert.That(fixture.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False); Invoke(window, "CloseButton");
        });
        foreach (var file in Directory.GetFiles(fixture.Root, "*.png")) TestContext.AddTestAttachment(file);
    }

    [TestCase(false), TestCase(true)]
    public void ApplyInformationReviewTwentyThreeRows(bool exceptionalLastRow)
    {
        using var fixture = new Fixture();
        using var clipboard = new NativeClipboardScope();
        File.WriteAllText(Path.Combine(fixture.Root, "scenario.json"), JsonSerializer.Serialize(new {
            registration = true, reviewInformation = true, itemCount = 23
        }));
        fixture.Run(window =>
        {
            Connect(window); Invoke(window, "ProjectsPageButton"); Register(window, 1);
            NativeClipboardScope.WriteTestFormats(string.Join("\r\n", Enumerable.Range(1, 23).Select(i => exceptionalLastRow && i == 23 ? "Ready\tP0\tM" : "Ready\tP1\tM")));
            Element(window, "GridCell0_1").Click(); Invoke(window, "GridPaste");
            Wait(() => Text(window, "DraftStatus").Contains("GitHub未反映 69セル") && Text(window, "DraftStatus").Contains("ローカル保存済み"));
            Capture(window, fixture.Root, "01-edited-23");
            Invoke(window, "ReviewApplyButton");
            Wait(() => window.FindFirstDescendant(cf => cf.ByAutomationId("ApplySelectAll")) is not null);
            var all = Element(window, "ApplySelectAll");
            if (all.Patterns.Toggle.IsSupported) all.Patterns.Toggle.Pattern.Toggle(); else all.AsButton().Invoke();
            WorkspaceUi.WaitForApplyReady(window);
            Assert.That(Element(window, "PrimaryButton").Name, Is.EqualTo("GitHubに反映（23件）"));
            Capture(window, fixture.Root, "02-review-23-top");
            File.WriteAllText(Path.Combine(fixture.Root, "review-conditions.json"), JsonSerializer.Serialize(new {
                rows = 23, fields = new[] { "Status", "Priority", "Size" }, selected = 23,
                window = window.BoundingRectangle, dpi = WorkspaceWindowDpi(window.Properties.NativeWindowHandle.Value),
                baseline = Environment.GetEnvironmentVariable("GHPB_IR_BASELINE") == "1"
            }, new JsonSerializerOptions { WriteIndented = true }));
            var list = Element(window, "ApplyTargetRows").AsListBox();
            var seen = new HashSet<int>(); var observations = new List<object>();
            int ObserveRows()
            {
                var viewport = list.BoundingRectangle;
                var lineHeight = 16 * WorkspaceWindowDpi(window.Properties.NativeWindowHandle.Value) / 96d;
                var complete = new List<int>();
                for (var i = 1; i <= 23; i++)
                {
                    var values = new[] { "status", "field-1", "field-2" }.Select(field => list.FindFirstDescendant(cf => cf.ByAutomationId($"ApplyValue-P1-T{i}-Field-P1-{field}"))).ToArray();
                    if (values.Any(value => value is null || value.BoundingRectangle.Width <= 0 || value.BoundingRectangle.Height < lineHeight
                        || value.BoundingRectangle.Top < viewport.Top || value.BoundingRectangle.Bottom > viewport.Bottom)) continue;
                    Assert.That(values.Select(v => v!.Name), Is.EqualTo(new[] { "Backlog → Ready", exceptionalLastRow && i == 23 ? "P2 → P0" : "P2 → P1", "S → M" }));
                    complete.Add(i); seen.Add(i);
                }
                observations.Add(new { scroll = list.Patterns.Scroll.Pattern.VerticalScrollPercent.Value, completeRows = complete.ToArray() });
                return complete.Count;
            }
            if (Environment.GetEnvironmentVariable("GHPB_IR_BASELINE") != "1")
            {
                Assert.That(ObserveRows(), Is.GreaterThanOrEqualTo(5), "The unchanged ordinary default window must expose five complete short comparisons.");
                for (var percent = 10; percent <= 100; percent += 10)
                {
                    list.Patterns.Scroll.Pattern.SetScrollPercent(-1, percent);
                    Wait(() => Math.Abs(list.Patterns.Scroll.Pattern.VerticalScrollPercent.Value - percent) < 2);
                    ObserveRows();
                    if (percent == 50) Capture(window, fixture.Root, "03-review-23-middle");
                }
                Assert.That(seen, Is.EquivalentTo(Enumerable.Range(1, 23)), "Every Issue's three old/new values must be reachable through the one list.");
            }
            else if (list.Patterns.Scroll.Pattern.VerticallyScrollable.Value) list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
            Capture(window, fixture.Root, "03-review-23-bottom");
            if (Environment.GetEnvironmentVariable("GHPB_IR_BASELINE") != "1")
            {
                var details = Element(window, "ApplyDetails-P1-T23"); details.Focus(); Keyboard.Type(VirtualKeyShort.SPACE);
                Wait(() => WorkspaceUi.HasVisibleElement(window, "ApplyFullDetails-P1-T23"));
                var full = Element(window, "ApplyFullDetails-P1-T23");
                Assert.That(full.Name, Does.Contain("Issue ID I23").And.Contain("項目 ID P1-T23"));
                Capture(window, fixture.Root, "04-keyboard-full-details");
                Keyboard.Type(VirtualKeyShort.ESCAPE);
                Wait(() => !WorkspaceUi.HasVisibleElement(window, "ApplyFullDetails-P1-T23") && details.Properties.HasKeyboardFocus.Value);
                Assert.That(Element(window, "PrimaryButton").Name, Is.EqualTo("GitHubに反映（23件）"));
                var original = window.BoundingRectangle; var scale = WorkspaceWindowDpi(window.Properties.NativeWindowHandle.Value) / 96d;
                window.Patterns.Transform.Pattern.Resize(760 * scale, 640 * scale);
                Wait(() => window.BoundingRectangle.Width < original.Width);
                list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
                Wait(() => list.Patterns.Scroll.Pattern.VerticalScrollPercent.Value >= 99
                    && Element(window, "ApplyValue-P1-T23-Field-P1-field-2").BoundingRectangle.Bottom <= list.BoundingRectangle.Bottom);
                Capture(window, fixture.Root, "05-narrow-last-row");
                Assert.That(WorkspaceUi.HasVisibleElement(window, "PrimaryButton") && WorkspaceUi.HasVisibleElement(window, "CloseButton") && WorkspaceUi.HasVisibleElement(window, "ApplySelectAll"), Is.True);
                window.Patterns.Transform.Pattern.Resize(original.Width, original.Height);
                File.WriteAllText(Path.Combine(fixture.Root, "reachable-rows.json"), JsonSerializer.Serialize(new { exceptionalLastRow, seen = seen.Order().ToArray(), observations }, new JsonSerializerOptions { WriteIndented = true }));
            }
            Assert.That(fixture.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
            Invoke(window, "CloseButton");
        });
        foreach (var file in Directory.GetFiles(fixture.Root, "*.png")) TestContext.AddTestAttachment(file);
    }
}
