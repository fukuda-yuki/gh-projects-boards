using System.Diagnostics;
using System.Runtime.InteropServices;
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
    public void WorkspaceReconstructionSession()
    {
        using var fixture = new Fixture();
        using var clipboard = new NativeClipboardScope();
        File.WriteAllText(Path.Combine(fixture.Root, "scenario.json"), JsonSerializer.Serialize(new {
            registration = true, columns = true, apply = true, itemCount = 101
        }));
        var artifacts = Environment.GetEnvironmentVariable("GHPB_E2E_ARTIFACTS")!;
        var executable = Environment.GetEnvironmentVariable("GHPB_E2E_APP_PATH")!;
        var assembly = Path.Combine(Path.GetDirectoryName(executable)!, "GhProjectsBoards.App.dll");
        var source = Path.Combine(artifacts, "source-environment.json");
        var assemblyHash = Hash(assembly);
        var sampleFile = Path.Combine(fixture.Root, "workspace-action-samples.jsonl");
        File.WriteAllText(Path.Combine(fixture.Root, "workspace-session-plan.json"), JsonSerializer.Serialize(new {
            test = TestContext.CurrentContext.Test.FullName,
            scope = "Ordinary-application E2E through real views, orchestration, isolated durable storage and external fake gh.",
            sourceRecord = source, sourceRecordSha256 = File.Exists(source) ? Hash(source) : null,
            executable, assembly, assemblySha256 = assemblyHash,
            os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.OSArchitecture.ToString(),
            driver = "FlaUI.UIA3 5.0.0", items = 101, projects = 2,
            windowSizesPhysicalPixels = new[] { new[] { 1400, 900 }, new[] { 1080, 760 } },
            warmup = 1, measuredSamples = 3, stopwatchFrequency = Stopwatch.Frequency,
            timingBoundary = "Public UIA action through the declared visible result, including driver polling, fixed keyboard/input waits and any command overflow/navigation. Fixture setup, screenshots and assertion preparation are excluded. These are bounded interaction observations, not isolated rendering or durable-write latency and not a speed comparison.",
            separateEvidence = new[] { "Physical Japanese IME", "Mixed creation and uncertain recovery", "High contrast and other DPI settings", "Human visual and usability acceptance" }
        }, new JsonSerializerOptions { WriteIndented = true }));

        fixture.Run(window =>
        {
            Resize(window, 1400, 900);
            Screenshot(window, "01-workspace-start");
            Connect(window); Invoke(window, "ProjectsPageButton");
            Register(window, 1); Register(window, 2); OpenSaved(window);
            Assert.That(Text(window, "ProjectSummary"), Does.Contain("項目 101"));
            Set(window, "DefaultRepository", "sample-user/first"); Invoke(window, "SaveProjectSettingButton");
            WorkspaceUi.CloseProjectSettings(window);
            Screenshot(window, "02-project-context-101-items");

            // Physical ASCII keys establish selection/editing and F6 focus routes;
            // the existing GridIme cases separately establish native composition.
            var first = Element(window, "GridCell0_0"); first.Click();
            Wait(() => first.Properties.HasKeyboardFocus.Value);
            Key(VirtualKeyShort.F6); Wait(() => Element(window, "GridReapply").Properties.HasKeyboardFocus.Value);
            Key(VirtualKeyShort.F6); Wait(() => Element(window, "GridDetails").Properties.HasKeyboardFocus.Value);
            Key(VirtualKeyShort.F6); Wait(() => Element(window, "GridCell0_0").Properties.HasKeyboardFocus.Value);
            using (Keyboard.Pressing(VirtualKeyShort.SHIFT)) Key(VirtualKeyShort.RIGHT, VirtualKeyShort.DOWN);
            Key(VirtualKeyShort.F6, VirtualKeyShort.F6, VirtualKeyShort.F6);
            Invoke(window, "GridCopy");
            Assert.That(NativeClipboardScope.ReadText(), Is.EqualTo("Issue 1\tTodo\r\nIssue 2\tTodo"),
                "Moving focus between workspace regions must preserve the selected rectangle.");
            Element(window, "GridCell0_0").Click();
            Keyboard.TypeVirtualKeyCode(0x1A); // VK_IME_OFF: this journey measures ASCII, not composition.
            Key(VirtualKeyShort.KEY_S, VirtualKeyShort.KEY_E, VirtualKeyShort.KEY_S, VirtualKeyShort.KEY_S, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N);
            Assert.That(CellText(window, 0), Is.EqualTo("session")); Key(VirtualKeyShort.RETURN);
            Element(window, "GridCell0_0").Click(); Key(VirtualKeyShort.F2);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Key(VirtualKeyShort.KEY_P, VirtualKeyShort.KEY_L, VirtualKeyShort.KEY_A, VirtualKeyShort.KEY_N, VirtualKeyShort.RETURN);
            Assert.That(CellText(window, 0), Is.EqualTo("plan"));
            var initialWidth = 320d; // The specified default; no preference record exists before the first save.
            var resize = Element(window, "GridColumnResize0").BoundingRectangle;
            NativePointer.Drag(window, new System.Drawing.Point(resize.Left + resize.Width / 2, resize.Top + resize.Height / 2),
                new System.Drawing.Point(resize.Left + resize.Width / 2 + 40, resize.Top + resize.Height / 2));
            Wait(() => Durable(fixture).GetProperty("ColumnPreferences").EnumerateArray().Any(p => p.GetProperty("ProjectId").GetString() == "P1"
                && p.GetProperty("Columns")[0].GetProperty("Width").GetDouble() > initialWidth + 10));
            WorkspaceUi.SelectCombo(window, "GridCell0_1", "Done");
            Invoke(window, "GridUndo"); Wait(() => WorkspaceUi.ChoiceText(window, "GridCell0_1") == "Todo");
            Screenshot(window, "03-direct-and-f2-editing");

            for (var sample = -1; sample < 3; sample++)
            {
                Measure("scroll-to-last-row", sample, () => {
                    Scroll(window, 100);
                    Assert.That(Element(window, "GridCell100_0").Properties.IsOffscreen.Value, Is.False);
                });
                Scroll(window, 0);
                var changed = "measurement-" + sample;
                Measure("edit-title", sample, () => { Edit(window, 2, changed); Wait(() => CellText(window, 2) == changed); });
                Measure("undo-title", sample, () => {
                    Invoke(window, "GridUndo");
                    Wait(() => Text(window, "DraftStatus").Contains("このProject 1 /", StringComparison.Ordinal)
                        && CellText(window, 2) == changed);
                });
                // Undo restores the preceding committed state and its pending buffer.
                // Explicit Escape cancels that buffer before the next independent sample.
                Wait(() => TitleDraft("I3").GetProperty("Change").ValueKind == JsonValueKind.Null
                    && TitleDraft("I3").GetProperty("Buffer").GetString() == changed);
                var restored = TitleDraft("I3");
                Assert.That(restored.GetProperty("Change").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(restored.GetProperty("Buffer").GetString(), Is.EqualTo(changed));
                Element(window, "GridCell2_0").Click(); Key(VirtualKeyShort.ESCAPE);
                Wait(() => CellText(window, 2) == "Issue 3");
                Wait(() => TitleDraft("I3").GetProperty("Buffer").ValueKind == JsonValueKind.Null);
                Element(window, "GridCell3_0").Click();
                var pasted = "Batch " + sample;
                NativeClipboardScope.WriteTestFormats(pasted + " A\tDone\r\n" + pasted + " B\tDone");
                Measure("paste-two-by-two", sample, () => {
                    Invoke(window, "GridPaste");
                    Wait(() => CellText(window, 3) == pasted + " A" && CellText(window, 4) == pasted + " B"
                        && WorkspaceUi.ChoiceText(window, "GridCell4_1") == "Done");
                });
                Measure("undo-paste", sample, () => {
                    Invoke(window, "GridUndo");
                    Wait(() => CellText(window, 3) == "Issue 4" && CellText(window, 4) == "Issue 5"
                        && WorkspaceUi.ChoiceText(window, "GridCell4_1") == "Todo");
                });
                Measure("switch-project-roundtrip", sample, () => {
                    OpenSaved(window, "Project 2"); Assert.That(CellText(window, 0), Is.EqualTo("plan"));
                    OpenSaved(window); Assert.That(CellText(window, 0), Is.EqualTo("plan"));
                });
            }

            // Compare observed alignment before and after scrolling; no density or
            // aesthetic threshold is substituted for review of the screenshots.
            Resize(window, 1080, 760);
            var header = Element(window, "GridHeader3").BoundingRectangle;
            var headerOffset = header.Left - Element(window, "GridCell0_3").BoundingRectangle.Left;
            Assert.That(Element(window, "GridHeader0").BoundingRectangle.Bottom,
                Is.LessThanOrEqualTo(Element(window, "GridCell0_0").BoundingRectangle.Top), "The header must not overlap the first row.");
            Element(window, "GridReapply").Focus();
            Wait(() => Element(window, "GridReapply").Properties.HasKeyboardFocus.Value);
            Scroll(window, 100);
            var scroller = Element(window, "ProjectItems").Patterns.Scroll.Pattern;
            Assert.That(scroller.HorizontallyScrollable.Value, Is.True, "This small-window workload must exercise horizontal scrolling.");
            ScrollEndpoint(window, 100, horizontal: true);
            Wait(() => Math.Abs(scroller.HorizontalScrollPercent.Value - 100) < 1
                    && Math.Abs(scroller.VerticalScrollPercent.Value - 100) < 1
                    && window.FindFirstDescendant(cf => cf.ByAutomationId("GridCell100_3")) is { } lastCell
                    && !lastCell.Properties.IsOffscreen.Value);
            Wait(() => Math.Abs(Element(window, "GridHeader3").BoundingRectangle.Left
                - Element(window, "GridCell100_3").BoundingRectangle.Left - headerOffset) < 2);
            Assert.That(Element(window, "GridHeader3").BoundingRectangle.Top, Is.EqualTo(header.Top).Within(1));
            Screenshot(window, "04-small-window-scrolled-header");
            ScrollEndpoint(window, 0, horizontal: true); Scroll(window, 0);

            WorkspaceUi.HeaderCommand(window, 2, "HeaderHide");
            Wait(() => Preferences(fixture, "P1").Single(c => c.GetProperty("Id").GetProperty("FieldId").GetString() == "P1B").GetProperty("Visible").GetBoolean() == false);
            WorkspaceUi.HeaderCommand(window, 2, "HeaderMoveLeft");
            Wait(() => Preferences(fixture, "P1")[1].GetProperty("Id").GetProperty("FieldId").GetString() == "P1C");
            Assert.That(Preferences(fixture, "P1")[1].GetProperty("Id").GetProperty("FieldId").GetString(), Is.EqualTo("P1C"));
            Invoke(window, "GridAddRow"); LocalCount(fixture, 1); Scroll(window, 100);
            Edit(window, 101, "plan follow-up");
            Assert.That(CellText(window, 101, 3), Is.EqualTo("sample-user/first"));
            Screenshot(window, "05-local-row-and-column-order");
            WorkspaceUi.HeaderCommand(window, 0, "HeaderSortAscending");
            Set(window, "GridQuickTitleFilter", "plan"); Invoke(window, "GridQuickFilterApply");
            Wait(() => Text(window, "RowViewStatus").Contains("表示 2"));
            Screenshot(window, "06-header-and-inline-view-configuration");
            Assert.That(CellText(window, 0), Is.EqualTo("plan")); Assert.That(CellText(window, 1), Is.EqualTo("plan follow-up"));
            Edit(window, 1, "plan follow-up revised"); Invoke(window, "GridReapply");
            Wait(() => CellText(window, 1) == "plan follow-up revised");
            Element(window, "GridCell0_0").Click(); Set(window, "GridCell0_0", "plan pending");
            Wait(() => TitleDraft("I1").GetProperty("Buffer").GetString() == "plan pending");
            Invoke(window, "GridDetails"); Wait(() => Text(window, "SelectedCellDetails").Contains("plan pending"));
            Screenshot(window, "07-filtered-pending-work");
            OpenSaved(window, "Project 2"); Assert.That(CellText(window, 0), Is.EqualTo("plan pending"));
            OpenSaved(window); Assert.That(CellText(window, 0), Is.EqualTo("plan pending"));
            Assert.That(Text(window, "RowViewStatus"), Does.Contain("表示 2"));
            Assert.That(fixture.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);

            Invoke(window, "ReviewApplyButton");
            var targets = Element(window, "ApplyTargetRows").AsListBox();
            targets.Items.Single(i => i.Name.Contains("sample-user/first #1 /", StringComparison.Ordinal)).Select();
            Assert.That(Element(window, "ApplyIncludeHidden").AsCheckBox().IsChecked, Is.False);
            Screenshot(window, "08-explicit-existing-target"); WorkspaceUi.WaitForApplyReady(window);
            Screenshot(window, "09-existing-title-review"); Invoke(window, "PrimaryButton");
            Wait(() => Text(window, "RegistrationStatus").Contains("反映完了"));
            var operation = Durable(fixture).GetProperty("Journal")[0].GetProperty("Operations")[0];
            Assert.That(operation.GetProperty("Intended").GetProperty("Value").GetString(), Is.EqualTo("plan"));
            Assert.That(operation.GetProperty("State").GetInt32(), Is.EqualTo(2));
            Assert.That(TitleDraft("I1").GetProperty("Buffer").GetString(), Is.EqualTo("plan pending"));
            LocalCount(fixture, 1);
            Invoke(window, "ApplyHistoryButton"); Screenshot(window, "10-verified-history"); Invoke(window, "CloseButton");
        });

        var callsBeforeRestart = fixture.Calls().Length;
        fixture.Run(window =>
        {
            Resize(window, 1080, 760); OpenSaved(window, profile: true);
            Assert.That(CellText(window, 0), Is.EqualTo("plan pending"));
            Assert.That(CellText(window, 1), Is.EqualTo("plan follow-up revised"));
            Assert.That(Text(window, "RowViewStatus"), Does.Contain("表示 2").And.Contain("plan"));
            Assert.That(Preferences(fixture, "P1")[1].GetProperty("Id").GetProperty("FieldId").GetString(), Is.EqualTo("P1C"));
            Assert.That(Durable(fixture).GetProperty("LocalRows")[0].GetProperty("Repository").GetString(), Is.EqualTo("sample-user/first"));
            Invoke(window, "GridDetails"); Screenshot(window, "11-restored-session");
            Invoke(window, "ApplyHistoryButton"); Screenshot(window, "12-restored-history-offline"); Invoke(window, "CloseButton");
        });
        Assert.That(fixture.Calls().Length, Is.EqualTo(callsBeforeRestart), "Opening saved work and history must not replay reads or writes.");
        var writes = File.ReadAllLines(Path.Combine(fixture.Root, "apply-requests.jsonl"));
        Assert.That(writes, Has.Length.EqualTo(1));
        using var applied = JsonDocument.Parse(writes[0]);
        Assert.That(applied.RootElement.GetProperty("id").GetString(), Is.EqualTo("I1"));
        Assert.That(applied.RootElement.GetProperty("title").GetString(), Is.EqualTo("plan"));
        TestContext.AddTestAttachment(sampleFile, "One warmup and three measured UIA interaction samples per action");

        JsonElement TitleDraft(string id) => Durable(fixture).GetProperty("Fields").EnumerateArray().Single(f =>
            f.GetProperty("Key").GetProperty("Kind").GetString() == "Title" && f.GetProperty("Key").GetProperty("NodeId").GetString() == id);
        void Measure(string action, int sample, Action run)
        {
            var started = DateTimeOffset.UtcNow; var clock = Stopwatch.StartNew(); var succeeded = false;
            try { run(); succeeded = true; }
            finally
            {
                clock.Stop();
                File.AppendAllText(sampleFile, JsonSerializer.Serialize(new {
                    action, sample, warmup = sample < 0, succeeded, startedUtc = started,
                    elapsedTicks = clock.ElapsedTicks, elapsedMilliseconds = clock.Elapsed.TotalMilliseconds
                }) + Environment.NewLine);
            }
        }
        void Screenshot(Window window, string phase)
        {
            var name = "workspace-" + phase + "-" + assemblyHash[..12];
            var titleBar = window.BoundingRectangle;
            Mouse.Position = new System.Drawing.Point((int)titleBar.Left + 120, (int)titleBar.Top + 16);
            // Let native help popups fade after leaving the table. Captures are
            // outside the measured interaction boundary.
            FlaUI.Core.Input.Wait.UntilInputIsProcessed(); Thread.Sleep(500);
            Capture(window, fixture.Root, name);
            var bounds = window.BoundingRectangle;
            File.WriteAllText(Path.Combine(fixture.Root, name + ".json"), JsonSerializer.Serialize(new {
                phase, image = name + ".png", capturedUtc = DateTimeOffset.UtcNow,
                sourceRecord = source, assemblySha256 = assemblyHash,
                windowPhysicalPixels = new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
                dpi = WorkspaceWindowDpi(window.Properties.NativeWindowHandle.Value),
                scope = "Ordinary application with isolated synthetic Project data; human acceptance pending"
            }, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.AddTestAttachment(Path.Combine(fixture.Root, name + ".png"), phase);
        }
        static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
        static void Resize(Window window, int width, int height)
        {
            window.Patterns.Transform.Pattern.Resize(width, height); window.Move(24, 24);
            Wait(() => Math.Abs(window.BoundingRectangle.Width - width) <= 2 && Math.Abs(window.BoundingRectangle.Height - height) <= 2);
            // The native window rectangle precedes the XAML responsive-pane update.
            FlaUI.Core.Input.Wait.UntilInputIsProcessed(); Thread.Sleep(350);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint WorkspaceWindowDpi(nint window);
}
