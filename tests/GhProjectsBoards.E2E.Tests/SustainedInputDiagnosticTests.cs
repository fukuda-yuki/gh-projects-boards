using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using NUnit.Framework;
using Application = FlaUI.Core.Application;

namespace GhProjectsBoards.E2E.Tests;

// Native input/scroll measurement on an ordinary app; not a timing assertion in CI.
[TestFixture, NonParallelizable, Apartment(ApartmentState.STA), Category("LocalSheetDiagnostic")]
public sealed class SustainedInputDiagnosticTests
{
    [Test, Explicit("Requires a frozen executable and a fresh isolated 1,000-task planning fixture.")]
    public void SustainedPlanningSheetInputAndScroll()
    {
        string Required(string name) => Environment.GetEnvironmentVariable("GHPB_SUSTAINED_" + name)
            ?? throw new InvalidOperationException("Set GHPB_SUSTAINED_" + name);
        var app = Path.GetFullPath(Required("APP"));
        var data = Path.GetFullPath(Required("DATA"));
        var output = Path.GetFullPath(Required("OUTPUT"));
        var condition = Required("CONDITION");
        var mode = Environment.GetEnvironmentVariable("GHPB_SUSTAINED_MODE") ?? "standard";
        var traceDetail = Environment.GetEnvironmentVariable("GHPB_SUSTAINED_TRACE_DETAIL") ?? "full";
        var earlyScroll = Environment.GetEnvironmentVariable("GHPB_SUSTAINED_EARLY_SCROLL") ?? "none";
        var desktopObserver = Environment.GetEnvironmentVariable("GHPB_SUSTAINED_DESKTOP_OBSERVER") == "1";
        var scrollProfile = Environment.GetEnvironmentVariable("GHPB_SUSTAINED_SCROLL_PROFILE") ?? "stress";
        var readinessKeyDelayMs = int.Parse(Environment.GetEnvironmentVariable("GHPB_SUSTAINED_READINESS_KEY_DELAY_MS") ?? "20");
        var readinessSurface = Environment.GetEnvironmentVariable("GHPB_SUSTAINED_READINESS_SURFACE") ?? "sheet";
        var readinessDispatch = Environment.GetEnvironmentVariable("GHPB_SUSTAINED_READINESS_DISPATCH") ?? "separate";
        Assert.That(readinessDispatch, Is.AnyOf("separate", "batch"));
        Assert.That(readinessDispatch != "batch" || readinessKeyDelayMs == 0, Is.True);
        Assert.That(readinessSurface, Is.AnyOf("sheet", "filter-control"));
        Assert.That(scrollProfile, Is.AnyOf("stress", "near1", "near3", "continuous"));
        Assert.That(scrollProfile == "stress" || mode == "standard" && earlyScroll == "immediate" && desktopObserver, Is.True,
            "Ordinary profiles retain immediate post-input prehistory and require the independent desktop observer.");
        Assert.That(earlyScroll, Is.AnyOf("none", "immediate", "settled"));
        Assert.That(earlyScroll == "none" || mode == "standard", Is.True);
        Assert.That(traceDetail, Is.AnyOf("full", "light", "off"));
        Assert.That(mode, Is.AnyOf("standard", "ime", "scroll", "readiness"));
        Assert.That(mode != "readiness" || desktopObserver, Is.True, "Selection readiness requires independent retained pixels.");
        Assert.That(condition, Is.AnyOf("cold", "warm"));
        Assert.That(Directory.Exists(output), Is.False, "Retain earlier attempts.");
        using var seed = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "diagnostics", "gantt-fixture.json")));
        Assert.That(seed.RootElement.GetProperty("validatedReadback").GetBoolean(), Is.True);
        Assert.That(seed.RootElement.GetProperty("tasks").GetInt32(), Is.EqualTo(1000));
        Assert.That(seed.RootElement.GetProperty("people").GetInt32(), Is.EqualTo(20));
        Directory.CreateDirectory(output);
        var checkpoint = Directory.GetFiles(Path.Combine(data, "Drafts"), "*.json").Single();
        using var before = JsonDocument.Parse(File.ReadAllText(checkpoint));
        Write("plan.json", new {
            app, source = Required("SOURCE"), condition, mode, traceDetail, earlyScroll, desktopObserver, scrollProfile,
            recycledPresentation = Environment.GetEnvironmentVariable("GHPB_RECYCLED_PRESENTATION") == "1", data, tasks = 1000, people = 20,
            scrollSchedule = scrollProfile == "stress" ? null : OrdinaryScrollSchedule(scrollProfile),
            readinessSchedule = mode == "readiness" ? ReadinessSchedule() : null, readinessKeyDelayMs, readinessSurface, readinessDispatch,
            selectionObservation = "Passive DXGI throughout selection; native-value/focus readback is delayed 350 ms and cannot supply selection latency.",
            fields = 6, checkpointBytes = new FileInfo(checkpoint).Length,
            pending = before.RootElement.GetProperty("Fields").EnumerateArray().Count(f => f.GetProperty("Buffer").ValueKind != JsonValueKind.Null),
            undoOperations = before.RootElement.GetProperty("History").GetArrayLength(),
            executableSha256 = Hash(app), appSha256 = Hash(Path.ChangeExtension(app, ".dll")),
            coreSha256 = Hash(Path.Combine(Path.GetDirectoryName(app)!, "GhProjectsBoards.Core.dll")),
            driverSha256 = Hash(typeof(SustainedInputDiagnosticTests).Assembly.Location),
            frequency = Stopwatch.Frequency, startedUtc = DateTimeOffset.UtcNow,
            schedule = mode == "readiness" ? "20 predeclared native selections and first characters across new, revisited and last-row targets. Declared click-to-key interval is included in every selection boundary. No focus wait or replay. Then 20 seconds each of sustained Title/NUMBER input."
                : mode == "scroll" ? "Native vertical scrollbar-thumb drag to the last row, immediate physical-key title edit, horizontal roundtrip and wheel return. Independent timestamped GDI frames cover the entire probe."
                : mode == "ime" ? "60 seconds of physical Japanese IME composition, conversion and confirmation, including a bounded writer-lock failure and explicit save recovery."
                : "60 seconds per condition: 20 title input, 20 NUMBER input, 20 alternating wheel/horizontal motion. Warm runs have an additional unmeasured 10 second title phase. Each condition starts a fresh process and fixture. A separate scrollbar-thumb probe remains required.",
            boundary = "Input start immediately before native key dispatch to first exact UIA native TextBox value readback, including UIA observer cost; not composited pixels. Independent best-effort GDI samples cover scrolling, with capture gaps reported; app rendering callbacks are separate pre-presentation signals. No physical scanout claim.",
            target = "Typed native-value readback p95 <=100 ms. Report all samples, capture gaps and app rendering gaps >=100 ms. No threshold asserted by this diagnostic test.",
            humanAcceptance = "Failed prior evaluation; not re-evaluated by this diagnostic.",
            earlyScrollBoundary = "When selected: unchanged title/NUMBER prehistory; camera armed during last NUMBER second, buffered PNG encoding after the replay, 1 second of original scroll commands. Settled adds an explicit wait for saved status plus 250 ms and is a diagnostic control, never product acceptance."
        });
        var events = new List<object>();
        var expectedBuffers = new Dictionary<(int Row, int Column), string>();
        void Record(string kind, object detail) => events.Add(new { kind, ticks = Stopwatch.GetTimestamp(), detail });
        void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        static void Wait(Func<bool> predicate, string reason) => Assert.That(Retry.WhileFalse(predicate,
            TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(50)).Result, Is.True, reason);
        var startInfo = new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app)! };
        startInfo.Environment["GHPB_DATA_ROOT"] = data;
        if (traceDetail != "off") startInfo.Environment["GHPB_SHEET_DIAGNOSTICS"] = Path.Combine(output, "app-trace.jsonl");
        else startInfo.Environment.Remove("GHPB_SHEET_DIAGNOSTICS");
        startInfo.Environment["GHPB_SHEET_VISUAL_WALK"] = traceDetail == "full" ? "1" : "0";
        startInfo.Environment["GH_CONFIG_DIR"] = Path.Combine(output, "empty-gh-config");
        foreach (var key in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN" }) startInfo.Environment.Remove(key);
        using var dpi = new DesktopDpiScope();
        using var process = Process.Start(startInfo)!;
        using var automation = new UIA3Automation();
        using var application = Application.Attach(process.Id);
        Window? window = null;
        var normal = false;
        try
        {
            window = application.GetMainWindow(automation, TimeSpan.FromSeconds(20)) ?? throw new InvalidOperationException("No application window.");
            WinUiProcess.AssertRuntime(process);
            window.Patterns.Transform.Pattern.Resize(1080, 760); window.Move(0, 0);
            Keyboard.TypeVirtualKeyCode(0x12); window.SetForeground();
            WorkspaceUi.OpenProjectNavigation(window);
            WorkspaceUi.SelectCombo(window, "SavedProfiles", 0);
            (WorkspaceUi.ProjectNavigation(window).FindFirstDescendant(cf => cf.ByName("P1")) ?? throw new InvalidOperationException("No P1 node.")).Patterns.Invoke.Pattern.Invoke();
            Wait(() => WorkspaceUi.HasVisibleElement(window, "GridCell0_0"), "P1 sheet must be visible.");
            // This narrow layout closes the overlay when the Project is invoked.
            // Wait for its public state instead of toggling during the closing animation.
            Wait(() => Element("ToggleProjectNavigation").Name == "Project一覧を表示", "Project navigation must finish closing.");
            // The overlay remains hit-testable during its closing animation even
            // after its public label changes. This untimed setup wait is not warmup.
            Thread.Sleep(400);
            Keyboard.TypeVirtualKeyCode(0x1A);
            Capture("ready");
            if (condition == "warm" && mode is "standard" or "readiness") Input("warmup", 0, 10, false);
            var start = Stopwatch.GetTimestamp();
            Record("clock-sync", new { before = Stopwatch.GetTimestamp(), utc = DateTimeOffset.UtcNow, after = Stopwatch.GetTimestamp() });
            Record("workload-start", new { start });
            if (mode == "readiness") { Readiness(); if (readinessSurface == "sheet") { Input("title", 0, 20, true); Input("number", 2, 20, true); } }
            else if (mode == "scroll") ScrollbarAndDistantEdit();
            else if (mode == "ime") Ime(60);
            else { Input("title", 0, 20, true); if (earlyScroll == "none") { Input("number", 2, 20, true); Scroll(20); } else EarlyScroll(); }
            Record("workload-end", new { elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds });
            Capture("after-workload");
            window.Close(); normal = process.WaitForExit(20000) && process.ExitCode == 0;
            Assert.That(normal, Is.True, "Ordinary close must flush and exit.");
            using var after = JsonDocument.Parse(File.ReadAllText(checkpoint));
            foreach (var ((row, column), expected) in expectedBuffers)
            {
                var field = after.RootElement.GetProperty("Fields").EnumerateArray().Single(f => column == 0
                    ? f.GetProperty("Key").GetProperty("Kind").GetString() == "Title" && f.GetProperty("Key").GetProperty("NodeId").GetString() == "I" + (row + 1)
                    : f.GetProperty("Key").GetProperty("NodeId").GetString() == "P1T" + (row + 1) && f.GetProperty("Key").GetProperty("FieldId").GetString() == "F-Estimate");
                Assert.That(field.GetProperty("Buffer").GetString(), Is.EqualTo(expected), "Close must retain the last observed native input.");
            }
            Assert.That(after.RootElement.GetProperty("History").GetArrayLength(), Is.EqualTo(before.RootElement.GetProperty("History").GetArrayLength()), "Buffer typing must not create cell commits.");
            foreach (var pending in before.RootElement.GetProperty("Fields").EnumerateArray().Where(f => f.GetProperty("Buffer").ValueKind != JsonValueKind.Null))
                Assert.That(after.RootElement.GetProperty("Fields").EnumerateArray().Single(f => f.GetProperty("Key").GetRawText() == pending.GetProperty("Key").GetRawText()).GetProperty("Buffer").GetString(),
                    Is.EqualTo(pending.GetProperty("Buffer").GetString()), "Offscreen pending input must survive.");
            var oldTasks = before.RootElement.GetProperty("Planning")[0].GetProperty("Tasks").EnumerateArray().ToDictionary(t => t.GetProperty("Id").GetString()!);
            foreach (var task in after.RootElement.GetProperty("Planning")[0].GetProperty("Tasks").EnumerateArray())
                foreach (var property in new[] { "Mode", "OwnerId", "ManualStart", "ManualFinish", "Actuals", "Contributions" })
                    Assert.That(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(task.GetProperty(property).GetRawText()),
                        System.Text.Json.Nodes.JsonNode.Parse(oldTasks[task.GetProperty("Id").GetString()!].GetProperty(property).GetRawText())), Is.True, property);
            Write("durable-readback.json", new { passed = true, expectedBuffers = expectedBuffers.Select(p => new { p.Key.Row, p.Key.Column, p.Value }).ToArray(), historyOperations = after.RootElement.GetProperty("History").GetArrayLength(), planningTasksUnchanged = oldTasks.Count,
                endpoint = "Normal close and independent checkpoint content; ordinary restart is verified in the separate Gantt journey." });
        }
        catch (Exception e)
        {
            Record("failure", new { type = e.GetType().Name, e.Message });
            if (window is not null && !process.HasExited) try { Capture("failure"); } catch { /* Preserve the original failure. */ }
            throw;
        }
        finally
        {
            if (!process.HasExited && window is not null) try { window.Close(); normal = process.WaitForExit(20000) && process.ExitCode == 0; } catch { }
            var forced = !process.HasExited;
            if (forced) { process.Kill(true); process.WaitForExit(5000); }
            File.WriteAllLines(Path.Combine(output, "driver.jsonl"), events.Select(e => JsonSerializer.Serialize(e)));
            Write("lifetime.json", new { normal, forced, pid = process.Id });
            if (File.Exists(checkpoint)) File.Copy(checkpoint, Path.Combine(output, "synthetic-final-checkpoint.json"));
            TestContext.AddTestAttachment(Path.Combine(output, "driver.jsonl"));
        }
        AutomationElement Element(string id) => WorkspaceUi.Element(window!, id);
        TextBox AcquireNativeEditor(string id)
        {
            var begin = Stopwatch.GetTimestamp();
            Element(id).Click();
            // A recyclable presentation can hand focus to an identity-owned native
            // editor. Reacquire that public target; an old peer is not its identity.
            Wait(() => Element(id).Properties.HasKeyboardFocus.Value, "The intended native editor must own focus.");
            var editor = Element(id).AsTextBox();
            Record("selection-readiness", new { id, begin, end = Stopwatch.GetTimestamp(),
                boundary = "Click dispatch through fresh native-focus readback; includes UIA observer overhead, before the retained input phase." });
            return editor;
        }
        void Capture(string name) { using var image = FlaUI.Core.Capturing.Capture.Rectangle(window!.BoundingRectangle); image.ToFile(Path.Combine(output, name + ".png")); }
        void Readiness()
        {
            var list = Element("ProjectItems");
            var control = readinessSurface == "filter-control";
            var bounds = control ? Element("GridQuickTitleFilter").BoundingRectangle : list.BoundingRectangle;
            using (var desktop = new DesktopFrameObserver(bounds, Path.Combine(output, "readiness-desktop")))
            {
                desktop.Arm();
                foreach (var trial in ReadinessSchedule())
                {
                    // Navigation is separate from selection readiness. It must not
                    // activate the target, warm its editor or supply its first key.
                    if (control) Element("GridQuickTitleFilter").Patterns.Value.Pattern.SetValue("");
                    else list.Patterns.Scroll.Pattern.SetScrollPercent(0, Math.Min(100, Math.Max(0, trial.Row - 2) * 100d / 987));
                    var id = control ? "GridQuickTitleFilter" : $"GridCell{trial.Row}_{trial.Column}";
                    Wait(() => WorkspaceUi.HasVisibleElement(window!, id), "The declared target must be displayed before selection.");
                    var target = Element(id); var rectangle = target.BoundingRectangle;
                    Assert.That(bounds.Contains(rectangle), Is.True, "The whole measured cell must be visible.");
                    var classBefore = target.ClassName;
                    var textBefore = target.Patterns.Value.Pattern.Value.Value;
                    var expected = (control ? "" : expectedBuffers.GetValueOrDefault((trial.Row, trial.Column), "")) + trial.Text;
                    if (control) Element("GridQuickFilterApply").Focus();
                    NativePointer.Position(window!, new Point(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2));
                    Thread.Sleep(200); // Retained old pixels; selection has not begun.
                    var (begin, keyBegin, sent) = NativePointer.SelectAndType((ushort)(trial.Text[0]), readinessKeyDelayMs, readinessDispatch == "batch");
                    // E1 selection ends at independent visible pixels. Tree/value
                    // polling in that interval would itself contend with rendering.
                    // Validate identity once afterwards; sustained input below owns
                    // the separately required native-value latency distribution.
                    Thread.Sleep(350);
                    var current = Element(id);
                    var actual = current.Patterns.Value.Pattern.Value.Value;
                    var classAfter = current.ClassName;
                    var matched = classAfter == "TextBox" && current.Properties.HasKeyboardFocus.Value && actual == expected;
                    var native = Stopwatch.GetTimestamp();
                    Thread.Sleep(70); // Retain the independently checked final reference.
                    Record("editor-readiness", new { trial.Index, trial.Row, trial.Column, trial.Text, id, readinessSurface, readinessDispatch, begin, keyBegin, sent, native, readinessKeyDelayMs,
                        end = Stopwatch.GetTimestamp(), matched, expected, actual, textBefore, classBefore, classAfter,
                        viewport = bounds, rectangle, delayedNativeReadbackMs = Stopwatch.GetElapsedTime(begin, native).TotalMilliseconds,
                        boundary = "Native mouse-down dispatch through independent visible pixels. Fixed key interval and editor activation are included. Native identity/value readback is deliberately delayed and is not a latency sample." });
                    Assert.That(matched, Is.True, "The first physical character must reach the selected task without replay.");
                    if (!control) expectedBuffers[(trial.Row, trial.Column)] = expected;
                }
            }
            list.Patterns.Scroll.Pattern.SetScrollPercent(0, 0);
            Wait(() => WorkspaceUi.HasVisibleElement(window!, "GridCell0_0"), "Return for sustained input.");
        }
        void Input(string phase, int column, int seconds, bool measured, Action? nearEnd = null)
        {
            var cell = AcquireNativeEditor("GridCell0_" + column);
            var start = Stopwatch.GetTimestamp(); var index = 0;
            Record("input-phase-start", new { phase, measured });
            while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds)
            {
                if (nearEnd is not null && Stopwatch.GetElapsedTime(start).TotalSeconds >= seconds - 1)
                { nearEnd(); nearEnd = null; }
                // Replace the pending buffer repeatedly without invalid NUMBER overflow.
                using (Keyboard.Pressing(VirtualKeyShort.CONTROL)) Keyboard.Type(VirtualKeyShort.KEY_A);
                var key = index % 2 == 0 ? VirtualKeyShort.KEY_7 : VirtualKeyShort.KEY_8;
                var expected = index % 2 == 0 ? "7" : "8";
                var begin = Stopwatch.GetTimestamp(); Keyboard.Type(key); var sent = Stopwatch.GetTimestamp();
                var matched = false;
                while (Stopwatch.GetElapsedTime(begin).TotalSeconds < 5)
                {
                    if (cell.Text == expected) { matched = true; break; }
                    Thread.Sleep(2);
                }
                var end = Stopwatch.GetTimestamp();
                Record("typed-value", new { phase, measured, index, begin, sent, end, matched, milliseconds = Stopwatch.GetElapsedTime(begin, end).TotalMilliseconds });
                Assert.That(matched, Is.True, "Native key must update the selected cell.");
                expectedBuffers[(0, column)] = expected;
                index++;
                // 5 updates/second is declared pacing, outside each measured latency.
                var rest = 200 - Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                if (rest > 0) Thread.Sleep((int)rest);
            }
            Record("input-phase-end", new { phase, samples = index });
        }
        void Ime(int seconds)
        {
            var cell = AcquireNativeEditor("GridCell0_0");
            var start = Stopwatch.GetTimestamp(); var index = 0; var testedFailure = false;
            try
            {
                while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds)
                {
                    using (Keyboard.Pressing(VirtualKeyShort.CONTROL)) Keyboard.Type(VirtualKeyShort.KEY_A);
                    using var locked = !testedFailure && Stopwatch.GetElapsedTime(start).TotalSeconds >= 20
                        ? new FileStream(Path.Combine(data, "Drafts", ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None) : null;
                    Keyboard.TypeVirtualKeyCode(0x16);
                    FlaUI.Core.Input.Wait.UntilInputIsProcessed(); Thread.Sleep(100);
                    var begin = Stopwatch.GetTimestamp();
                    foreach (var key in new[] { VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_H, VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_G, VirtualKeyShort.KEY_O })
                    { Keyboard.Type(key); FlaUI.Core.Input.Wait.UntilInputIsProcessed(); Thread.Sleep(100); }
                    Wait(() => cell.Text == "にほんご", "Physical romaji must start native composition.");
                    Keyboard.Type(VirtualKeyShort.SPACE); Wait(() => cell.Text == "日本語", "Native conversion must produce the expected candidate.");
                    Thread.Sleep(1200); // Allow in-flight durable completion while the IME still owns composition.
                    Assert.That(cell.Properties.HasKeyboardFocus.Value, Is.True);
                    if (index == 0 || locked is not null) Capture(locked is null ? "ime-composing" : "ime-composing-save-failure");
                    Keyboard.Type(VirtualKeyShort.RETURN); Wait(() => cell.Text == "日本語", "Composition confirmation must retain the pending text.");
                    expectedBuffers[(0, 0)] = "日本語";
                    Record("physical-ime", new { index, begin, end = Stopwatch.GetTimestamp(), writerLocked = locked is not null, nativeText = "日本語" });
                    if (locked is not null)
                    {
                        Wait(() => Element("DraftStatus").Name.Contains("保存失敗"), "Save failure must become visible after composition confirmation.");
                        Thread.Sleep(250); // Observe settled pixels separately from the native text readback.
                        Capture("ime-confirmed-save-failure"); locked.Dispose(); testedFailure = true;
                        WorkspaceUi.Element(window!, "GridSave").AsButton().Invoke();
                        Wait(() => Element("DraftStatus").Name.Contains("保存済み"), "Explicit retry must clear the save failure.");
                        Thread.Sleep(250);
                        Capture("ime-save-recovered"); cell = AcquireNativeEditor("GridCell0_0");
                    }
                    index++;
                }
                Assert.That(testedFailure, Is.True);
            }
            finally { Keyboard.TypeVirtualKeyCode(0x1A); }
        }
        void WithScrollFrames(Action action)
        {
            var list = Element("ProjectItems"); var bounds = list.BoundingRectangle;
            var captures = new List<object>();
            using var stop = new CancellationTokenSource();
            var camera = Task.Run(() => {
                var index = 0;
                while (!stop.IsCancellationRequested)
                {
                    var begin = Stopwatch.GetTimestamp();
                    using var image = FlaUI.Core.Capturing.Capture.Rectangle(bounds);
                    var end = Stopwatch.GetTimestamp();
                    var path = $"scroll-{index:D4}.png";
                    image.ToFile(Path.Combine(output, path));
                    captures.Add(new { index, begin, end, path }); index++; Thread.Sleep(33);
                }
            });
            try { action(); }
            finally { stop.Cancel(); camera.GetAwaiter().GetResult(); Write("scroll-captures.json", captures); }
        }
        void ScrollbarAndDistantEdit() => WithScrollFrames(() =>
        {
            var firstTitle = Element("GridCell0_0").AsTextBox().Text;
            var bar = Element("SheetVerticalScroll"); var bounds = bar.BoundingRectangle;
            Mouse.MoveTo(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
            var thumb = Retry.WhileNull(() => bar.FindFirstDescendant(cf => cf.ByControlType(ControlType.Thumb)),
                TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(50)).Result;
            var rect = thumb is not null && !thumb.Properties.IsOffscreen.Value ? thumb.BoundingRectangle : Rectangle.Empty;
            var targetSource = "public Thumb";
            Rectangle Bounds(string id) => bar.FindFirstDescendant(cf => cf.ByAutomationId(id))?.BoundingRectangle ?? Rectangle.Empty;
            var smallAfter = Bounds("VerticalSmallIncrease");
            if (rect.IsEmpty && bar.ClassName == "ScrollBar")
            {
                // WinUI's standard peer exposes the track buttons but omits Thumb.
                // Their rendered rectangles bound the real thumb without a guessed size.
                var before = Bounds("VerticalLargeDecrease"); var after = Bounds("VerticalLargeIncrease");
                var smallBefore = Bounds("VerticalSmallDecrease");
                if (!smallBefore.IsEmpty && !smallAfter.IsEmpty)
                {
                    var top = before.Height > 0 ? before.Bottom : smallBefore.Bottom;
                    var bottom = after.Height > 0 ? after.Top : smallAfter.Top;
                    if (bottom > top && top >= bounds.Top && bottom <= bounds.Bottom)
                        rect = Rectangle.FromLTRB(bounds.Left, top, bounds.Right, bottom);
                }
                targetSource = "gap between observed standard native track buttons";
            }
            Assert.That(rect.IsEmpty, Is.False, "A visible native thumb is required; no guessed drag target.");
            var from = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            var to = new Point(from.X, smallAfter.IsEmpty ? bounds.Bottom - rect.Height / 2 : smallAfter.Top - rect.Height / 2);
            var start = Stopwatch.GetTimestamp();
            Record("thumb-drag-start", new { from, to, rect, targetSource });
            NativePointer.Drag(window!, from, to);
            var scroll = Element("ProjectItems").Patterns.Scroll.Pattern;
            Wait(() => scroll.VerticalScrollPercent.Value >= 99 && WorkspaceUi.HasVisibleElement(window!, "GridCell999_0"), "Thumb must reach the last task.");
            var reached = Stopwatch.GetTimestamp();
            var far = AcquireNativeEditor("GridCell999_0");
            Keyboard.Type(VirtualKeyShort.F2);
            using (Keyboard.Pressing(VirtualKeyShort.CONTROL)) Keyboard.Type(VirtualKeyShort.KEY_A);
            var begin = Stopwatch.GetTimestamp(); Keyboard.Type(VirtualKeyShort.KEY_9);
            Wait(() => far.Text == "9", "Immediate distant edit must reach the last task.");
            var observed = Stopwatch.GetTimestamp(); expectedBuffers[(999, 0)] = "9";
            Record("distant-edit", new { start, reached, begin, observed, nativeText = far.Text,
                verticalPercent = scroll.VerticalScrollPercent.Value, canonicalIssue = "I1000", canonicalItem = "P1T1000",
                keyToNativeMilliseconds = Stopwatch.GetElapsedTime(begin, observed).TotalMilliseconds });
            Thread.Sleep(250); Capture("distant-edit");
            var area = Element("ProjectItems").BoundingRectangle; Mouse.MoveTo(new Point(area.Left + area.Width / 2, area.Top + 70));
            Mouse.HorizontalScroll(120);
            Wait(() => scroll.HorizontalScrollPercent.Value > 0, "Physical horizontal input must move the viewport.");
            Record("horizontal-end", new { percent = scroll.HorizontalScrollPercent.Value });
            Capture("horizontal-end"); Mouse.HorizontalScroll(-120);
            Wait(() => scroll.HorizontalScrollPercent.Value == 0, "Horizontal return must restore the left edge.");
            for (var i = 0; i < 12 && scroll.VerticalScrollPercent.Value > 0; i++)
            {
                Mouse.Scroll(120); Thread.Sleep(200);
                Record("wheel-return", new { index = i, verticalPercent = scroll.VerticalScrollPercent.Value });
            }
            Wait(() => scroll.VerticalScrollPercent.Value == 0, "Wheel return must restore the first task.");
            Assert.That(Element("GridCell0_0").AsTextBox().Text, Is.EqualTo(firstTitle), "Far edit must not write to the first task.");
            Record("scroll-roundtrip-end", new { verticalPercent = scroll.VerticalScrollPercent.Value, horizontalPercent = scroll.HorizontalScrollPercent.Value });
            Thread.Sleep(250); Capture("scroll-return");
        });
        void EarlyScroll()
        {
            var bounds = Element("ProjectItems").BoundingRectangle;
            using var desktop = desktopObserver ? new DesktopFrameObserver(bounds, Path.Combine(output, "desktop")) : null;
            using var armed = new ManualResetEventSlim();
            using var stop = new CancellationTokenSource();
            var frames = new List<TimestampedScreenCopy.Frame>();
            var camera = scrollProfile != "stress" ? Task.CompletedTask : Task.Run(() =>
            {
                armed.Wait(stop.Token);
                while (!stop.IsCancellationRequested && frames.Count < 512)
                {
                    frames.Add(TimestampedScreenCopy.Rectangle(bounds));
                    Thread.Sleep(1);
                }
            });
            try
            {
                Input("number", 2, 20, true, () => { Record("early-camera-armed", new { bounds,
                    sampling = "FlaUI-equivalent full viewport GDI copy with nested BitBlt/GdiFlush timestamps; retained 1 ms sleep; PNG encoding after replay" }); desktop?.Arm(); armed.Set(); });
                if (earlyScroll == "settled")
                {
                    Record("diagnostic-save-wait-start", new { diagnosticOnly = true });
                    Wait(() => Element("DraftStatus").Name.Contains("保存済み"), "Diagnostic control requires settled saving.");
                    Thread.Sleep(250);
                    Record("diagnostic-save-wait-end", new { diagnosticOnly = true });
                }
                if (scrollProfile == "stress") ScrollInputs(1, bounds);
                else OrdinaryScrollInputs(bounds);
            }
            finally
            {
                stop.Cancel(); armed.Set();
                try
                {
                    try { camera.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                }
                finally
                {
                    try
                    {
                        Write("scroll-captures.json", frames.Select((f, i) => new { index = i, begin = f.Begin, end = f.End,
                            copyBegin = f.CopyBegin, copyEnd = f.CopyEnd, path = $"scroll-{i:D4}.png" }).ToArray());
                        for (var i = 0; i < frames.Count; i++) frames[i].Image.ToFile(Path.Combine(output, $"scroll-{i:D4}.png"));
                    }
                    finally { foreach (var frame in frames) frame.Image.Dispose(); }
                }
            }
            Assert.That(frames.Count, Is.LessThan(512), "A saturated camera is incomplete evidence.");
        }
        void Scroll(int seconds) => WithScrollFrames(() => ScrollInputs(seconds, Element("ProjectItems").BoundingRectangle));
        void OrdinaryScrollInputs(Rectangle bounds)
        {
            Mouse.Position = new Point(bounds.Left + bounds.Width / 2, bounds.Top + 70);
            var schedule = OrdinaryScrollSchedule(scrollProfile);
            var start = Stopwatch.GetTimestamp();
            Record("scroll-phase-start", new { bounds, scrollProfile, start, schedule });
            foreach (var command in schedule)
            {
                while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < command.DueMs) Thread.Sleep(1);
                var begin = Stopwatch.GetTimestamp();
                if (command.Axis == "horizontal") Mouse.HorizontalScroll(command.ApiArgument);
                else Mouse.Scroll(command.ApiArgument);
                Record("scroll-input", new { index = command.Index, begin, sent = Stopwatch.GetTimestamp(),
                    axis = command.Axis, detents = command.ApiArgument, apiArgument = command.ApiArgument,
                    driverWheelDelta = command.ApiArgument * 120, dueMs = command.DueMs,
                    actualMs = Stopwatch.GetElapsedTime(start, begin).TotalMilliseconds, scrollProfile });
            }
            // Keep the final response observable; this is after input, not a pre-input save wait.
            Thread.Sleep(350);
            Record("scroll-phase-end", new { samples = schedule.Length });
        }
        void ScrollInputs(int seconds, Rectangle bounds)
        {
                Mouse.Position = new Point(bounds.Left + bounds.Width / 2, bounds.Top + 70);
                var start = Stopwatch.GetTimestamp(); var index = 0;
                Record("scroll-phase-start", new { bounds });
                while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds)
                {
                    var begin = Stopwatch.GetTimestamp();
                    if (index % 6 == 4) Mouse.HorizontalScroll(120);
                    else if (index % 6 == 5) Mouse.HorizontalScroll(-120);
                    else Mouse.Scroll(index % 4 < 2 ? -80 : 80);
                    Record("scroll-input", new { index, begin, sent = Stopwatch.GetTimestamp(),
                        axis = index % 6 is 4 or 5 ? "horizontal" : "vertical",
                        detents = index % 6 == 4 ? 120 : index % 6 == 5 ? -120 : index % 4 < 2 ? -80 : 80,
                        apiArgument = index % 6 == 4 ? 120 : index % 6 == 5 ? -120 : index % 4 < 2 ? -80 : 80,
                        driverWheelDelta = (index % 6 == 4 ? 120 : index % 6 == 5 ? -120 : index % 4 < 2 ? -80 : 80) * 120,
                        scrollProfile = "stress" });
                    Thread.Sleep(200); index++;
                }
                Record("scroll-phase-end", new { samples = index });
        }
    }

    private sealed record ReadinessTrial(int Index, int Row, int Column, string Text);
    private static ReadinessTrial[] ReadinessSchedule()
    {
        (int Row, int Column)[] targets = [(5, 0), (25, 2), (5, 0), (999, 0), (50, 0), (25, 2), (100, 2), (999, 0),
            (150, 0), (50, 0), (200, 2), (100, 2), (250, 0), (150, 0), (500, 2), (200, 2), (750, 0), (250, 0), (999, 0), (750, 0)];
        return targets.Select((target, index) => new ReadinessTrial(index, target.Row, target.Column, index % 2 == 0 ? "7" : "8")).ToArray();
    }
    private sealed record ScrollCommand(int Index, string Axis, int ApiArgument, int DueMs);
    private static ScrollCommand[] OrdinaryScrollSchedule(string profile)
    {
        var continuous = profile == "continuous";
        var magnitude = profile == "near3" ? 3 : 1;
        return Enumerable.Range(0, continuous ? 200 : 24).Select(i =>
        {
            var step = i % 8;
            var horizontal = step >= 4;
            var sign = step is 0 or 1 or 6 or 7 ? -1 : 1;
            return new ScrollCommand(i, horizontal ? "horizontal" : "vertical", sign * magnitude, i * (continuous ? 100 : 250));
        }).ToArray();
    }
}
