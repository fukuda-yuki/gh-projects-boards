using System.Drawing;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [Test, Category("BulkEditing")]
    public void BulkEditingThreeRoutesRestoreAndReviewWithoutAutomaticCommunication()
    {
        using var fixture = new Fixture();
        using var clipboard = new NativeClipboardScope();
        File.WriteAllText(Path.Combine(fixture.Root, "scenario.json"), JsonSerializer.Serialize(new {
            registration = true, bulk = true, apply = true, itemCount = 100
        }));
        fixture.Run(window => {
            window.Patterns.Transform.Pattern.Resize(1616, 947); window.Move(0, 0);
            Connect(window); Invoke(window, "ProjectsPageButton"); Register(window, 1);
            var calls = fixture.Calls().Length;
            foreach (var route in new[] { "fill", "paste", "down" })
            {
                using var video = RecordRoute(window, fixture.Root, route);
                if (video is not null) Thread.Sleep(600); // Walkthrough pacing only, never a performance sample.
                WorkspaceUi.SelectCombo(window, "GridCell0_1", "Ready");
                Wait(() => Text(window, "DraftStatus").Contains("GitHub未反映 1セル"));
                if (route == "fill")
                    NativePointer.Drag(window, Center(Element(window, "GridFillHandle0_1")), Center(Element(window, "GridCell9_1")), () => {
                        Wait(() => Text(window, "GridSelection").Contains("10行へコピー予定"));
                        Assert.That(Text(window, "DraftStatus"), Does.Contain("GitHub未反映 1セル"));
                        if (video is not null) Thread.Sleep(600);
                    });
                else
                {
                    Element(window, "GridCell0_1").Click();
                    if (route == "paste")
                    {
                        NativeClipboardScope.WriteTestFormats("copy must replace this sentinel");
                        Invoke(window, "GridCopy");
                        Wait(() => NativeClipboardScope.ReadText() == "Ready");
                        Assert.That(Text(window, "DraftStatus"), Does.Not.Contain("クリップボードを利用できません"));
                    }
                    using (Keyboard.Pressing(VirtualKeyShort.SHIFT)) Element(window, "GridCell9_1").Click();
                    Wait(() => Text(window, "GridSelection") == "Status：10行・10セル");
                    if (route == "paste") Invoke(window, "GridPaste");
                    else using (Keyboard.Pressing(VirtualKeyShort.CONTROL)) Keyboard.Type(VirtualKeyShort.KEY_D);
                }
                Wait(() => Text(window, "DraftStatus").Contains("GitHub未反映 10セル"));
                foreach (var index in Enumerable.Range(0, 10)) Assert.That(WorkspaceUi.ChoiceText(window, $"GridCell{index}_1"), Is.EqualTo("Ready"));
                Capture(window, fixture.Root, route + "-ten-ready");
                Assert.That(fixture.Calls(), Has.Length.EqualTo(calls), "Local source/bulk edits must perform no permission checks, queries or writes.");
                Invoke(window, "GridUndo"); Wait(() => Text(window, "DraftStatus").Contains("GitHub未反映 1セル"));
                Assert.That(WorkspaceUi.ChoiceText(window, "GridCell0_1"), Is.EqualTo("Ready"));
                foreach (var index in Enumerable.Range(1, 9)) Assert.That(WorkspaceUi.ChoiceText(window, $"GridCell{index}_1"), Is.EqualTo("Backlog"));
                Invoke(window, "GridUndo"); Wait(() => Text(window, "DraftStatus").Contains("GitHub未反映 0セル"));
                Assert.That(WorkspaceUi.ChoiceText(window, "GridCell0_1"), Is.EqualTo("Backlog"));
                if (video is not null) Thread.Sleep(600);
            }
            WorkspaceUi.SelectCombo(window, "GridCell0_1", "Ready");
            Element(window, "GridCell0_1").Click();
            using (Keyboard.Pressing(VirtualKeyShort.SHIFT)) Element(window, "GridCell9_1").Click();
            Invoke(window, "GridFillDown");
            Wait(() => Text(window, "DraftStatus").Contains("GitHub未反映 10セル") && Text(window, "DraftStatus").Contains("ローカル保存済み"));
            Assert.That(fixture.Calls(), Has.Length.EqualTo(calls));
        });
        Assert.That(NativeClipboardScope.ReadText(), Is.EqualTo("Ready"), "A copied value must remain available after normal process exit.");
        var previousCalls = fixture.Calls().Length;
        fixture.Run(window => {
            window.Patterns.Transform.Pattern.Resize(1616, 947); window.Move(0, 0);
            OpenSaved(window, profile: true);
            Wait(() => Text(window, "DraftStatus").Contains("GitHub未反映 10セル"));
            Assert.That(fixture.Calls(), Has.Length.EqualTo(previousCalls), "Opening the recovered Project must remain offline.");
            var changed = Durable(fixture).GetProperty("Fields").EnumerateArray().Where(f => f.GetProperty("Change").ValueKind != JsonValueKind.Null).ToArray();
            Assert.That(changed.Select(f => f.GetProperty("Key").GetProperty("NodeId").GetString()),
                Is.EquivalentTo(Enumerable.Range(1, 10).Select(i => "P1-T" + i)));
            Assert.That(changed.Select(f => f.GetProperty("Change").GetProperty("Value").GetString()), Is.All.EqualTo("done"));
            Assert.That(changed.Select(f => f.GetProperty("Key").GetProperty("FieldId").GetString()), Is.All.EqualTo("P1-status"));
            Capture(window, fixture.Root, "reopened-local-work");
            Connect(window); Invoke(window, "ProjectsPageButton"); OpenSaved(window);
            Invoke(window, "ReviewApplyButton");
            Wait(() => Element(window, "ApplyTargetRows").AsListBox().Items.Length >= 10);
            var items = Element(window, "ApplyTargetRows").AsListBox().Items;
            for (var i = 0; i < 10; i++) items[i].AddToSelection();
            Invoke(window, "PrimaryButton");
            Wait(() => window.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is not null);
            var review = Element(window, "ApplyReviewDialog");
            var text = string.Join("\n", review.FindAllDescendants().Select(e => e.Properties.Name.ValueOrDefault));
            Assert.That(text, Does.Contain("選択行 10 / 更新 10 / 作成 0").And.Contain("Ready"));
            Capture(window, fixture.Root, "review-ten-changes"); Invoke(window, "CloseButton");
        });
        Assert.That(fixture.Calls().Any(call => call.GetProperty("mutation").GetBoolean()), Is.False);
        TestContext.AddTestAttachment(Path.Combine(fixture.Root, "fill-ten-ready.png"));
        TestContext.AddTestAttachment(Path.Combine(fixture.Root, "paste-ten-ready.png"));
        TestContext.AddTestAttachment(Path.Combine(fixture.Root, "down-ten-ready.png"));
        static Point Center(AutomationElement element) { var r = element.BoundingRectangle; return new(r.Left + r.Width / 2, r.Top + r.Height / 2); }
    }
    private static VideoRecorder? RecordRoute(Window window, string output, string route)
    {
        if (Environment.GetEnvironmentVariable("GHPB_VIDEO_ENCODER") is not { } encoder) return null;
        Assert.That(Path.IsPathFullyQualified(encoder) && File.Exists(encoder), Is.True);
        var bounds = window.BoundingRectangle;
        bounds.Width -= bounds.Width % 2; bounds.Height -= bounds.Height % 2;
        var path = Path.Combine(output, route + ".mp4");
        TestContext.Out.WriteLine("Ordinary-app walkthrough video: " + path);
        return new VideoRecorder(new VideoRecorderSettings { ffmpegPath = encoder, TargetVideoPath = path,
            FrameRate = 15, VideoQuality = 20 }, _ => {
                var frame = FlaUI.Core.Capturing.Capture.Rectangle(bounds);
                frame.ApplyOverlays(new MouseOverlay(frame)); return frame;
            });
    }
}
