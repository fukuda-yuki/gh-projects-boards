using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
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

// Independently selected diagnostic scope; deliberately outside the default E2E category.
[TestFixture, NonParallelizable, Apartment(ApartmentState.STA), Category("LocalSheetDiagnostic")]
public sealed class LocalSheetDiagnosticTests
{
    [Test, Explicit("Requires an immutable ordinary app, isolated prepared seed and dedicated output directory.")]
    public void CachedSheetFocusedNativeScrollAndLocalActions()
    {
        string Required(string suffix) => Environment.GetEnvironmentVariable("GHPB_DIAGNOSTIC_" + suffix)
            ?? throw new InvalidOperationException("Set GHPB_DIAGNOSTIC_" + suffix + " for this opt-in diagnostic.");
        var executable = Path.GetFullPath(Required("APP"));
        var data = Path.GetFullPath(Required("DATA_ROOT"));
        var output = Path.GetFullPath(Required("OUTPUT"));
        var trace = Environment.GetEnvironmentVariable("GHPB_DIAGNOSTIC_TRACE");
        var physicalIme = Environment.GetEnvironmentVariable("GHPB_DIAGNOSTIC_IME") == "1";
        var seedFile = Path.Combine(data, "diagnostics", "editing-seed.json");
        Assert.That(Environment.UserInteractive && File.Exists(executable), Is.True);
        Assert.That(File.ReadAllText(Path.Combine(data, "synthetic-editing-check.txt")).Trim(),
            Is.EqualTo("Synthetic registered Projects; no live authentication."));
        using var seedDocument = JsonDocument.Parse(File.ReadAllText(seedFile));
        var seed = seedDocument.RootElement;
        Assert.That(seed.GetProperty("validatedReadback").GetBoolean(), Is.True);
        var rows = seed.GetProperty("count").GetInt32();
        var fields = seed.GetProperty("selectFieldCount").GetInt32();
        Assert.That(rows, Is.InRange(101, 1000));
        Assert.That(fields, Is.InRange(1, 12));
        if (physicalIme) Assert.That(rows, Is.EqualTo(101), "The optional physical IME probe is bounded to 101 rows.");
        foreach (var (name, expected) in new[] { ("ROWS", rows), ("FIELDS", fields) })
            if (Environment.GetEnvironmentVariable("GHPB_DIAGNOSTIC_" + name) is { } supplied)
                Assert.That(int.Parse(supplied), Is.EqualTo(expected), "Requested workload must match validated seed metadata.");
        Assert.That(seed.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("projectId").GetString()),
            Is.EquivalentTo(new[] { "P1", "P2" }));
        Assert.That(Directory.Exists(output), Is.False, "Never overwrite prior diagnostic evidence.");
        Assert.That(!output.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !data.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(output, data, StringComparison.OrdinalIgnoreCase), Is.True, "Output and seed roots must be separate.");
        if (!string.IsNullOrWhiteSpace(trace))
        {
            Assert.That(Path.IsPathFullyQualified(trace) && !File.Exists(trace), Is.True, "Trace must be a new absolute file.");
            Assert.That(!trace.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.True);
        }
        Directory.CreateDirectory(output);
        File.Copy(seedFile, Path.Combine(output, "seed-manifest.json"));
        string Hash(string file) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
        var appDll = Path.ChangeExtension(executable, ".dll");
        Write("plan.json", new
        {
            executable, executableSha256 = Hash(executable), appDll, appDllSha256 = Hash(appDll),
            testAssemblySha256 = Hash(typeof(LocalSheetDiagnosticTests).Assembly.Location), data, rows, fields,
            seedSha256 = Hash(seedFile), trace, physicalIme,
            repeatedActions = new { names = new[] { "select-visible-title", "arrows-up-down" }, warmup = 1, measured = 5 },
            singleObservationActions = new[] { "cached-project-ready", "commit-pending-title", "undo-title", "project-P2-P1" },
            driverRevision = "paced-selection-v2",
            pacing = "Each intentional click/arrow awaits the public selected row; clicks also await that editor's reported focus. No retry of input. Not the prior fast-queue workload.",
            frequency = Stopwatch.Frequency, startedUtc = DateTimeOffset.UtcNow, startTicks = Stopwatch.GetTimestamp(),
            os = Environment.OSVersion.ToString(), driver = "FlaUI UIA3 5.0.0",
            scope = "Ordinary cached local-sheet diagnosis. Physical input/public controls; no connection, refresh or Apply. No real gh fallback.",
            boundary = "Action records are DRIVER elapsed including UIA calls/input waits. App UI spans/render callbacks are separate trace records, not presented pixels or acceptance.",
            humanAcceptance = "PR42 rejected; diagnostic completion does not reverse rejection."
        });
        var events = Path.Combine(output, "driver.jsonl");
        void Record(string kind, object? details = null) => File.AppendAllText(events,
            JsonSerializer.Serialize(new { kind, utc = DateTimeOffset.UtcNow, ticks = Stopwatch.GetTimestamp(), details }) + Environment.NewLine);
        void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        void DriverWait(int milliseconds)
        {
            var start = Stopwatch.GetTimestamp(); Thread.Sleep(milliseconds);
            Record("driver-wait", new { requestedMilliseconds = milliseconds, elapsedTicks = Stopwatch.GetTimestamp() - start });
        }
        void Wait(Func<bool> condition, string reason, int seconds = 10) => Assert.That(
            Retry.WhileFalse(condition, TimeSpan.FromSeconds(seconds), TimeSpan.FromMilliseconds(100)).Result, Is.True, reason);
        using var dpi = new DesktopDpiScope();
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        startInfo.Environment["GHPB_DATA_ROOT"] = data;
        startInfo.Environment["GH_CONFIG_DIR"] = Path.Combine(output, "empty-gh-config");
        foreach (var secret in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN" }) startInfo.Environment.Remove(secret);
        if (string.IsNullOrWhiteSpace(trace)) startInfo.Environment.Remove("GHPB_SHEET_DIAGNOSTICS");
        else startInfo.Environment["GHPB_SHEET_DIAGNOSTICS"] = trace;
        using var process = Process.Start(startInfo)!;
        using var automation = new UIA3Automation();
        using var application = Application.Attach(process.Id);
        Window? window = null;
        var completed = false;
        var closeRequested = false;
        var normal = false;
        var captureNumber = 0;
        try
        {
            window = application.GetMainWindow(automation, TimeSpan.FromSeconds(20));
            Assert.That(window, Is.Not.Null); WinUiProcess.AssertRuntime(process);
            // Match the existing native-input fixture's foreground acquisition before any cell input.
            Keyboard.TypeVirtualKeyCode(0x12);
            window!.SetForeground(); Wait(() => GetForegroundWindow() == window.Properties.NativeWindowHandle.Value, "Diagnostic window must be foreground.");
            Resize(1080, 760);
            WorkspaceUi.OpenProjectNavigation(window);
            WorkspaceUi.SelectCombo(window, "SavedProfiles", 0);
            Measure("cached-project-ready", 0, () => OpenProject("P1"));
            Snapshot("cached-project-ready");
            for (var sample = -1; sample < 5; sample++)
            {
                Measure("select-visible-title", sample, () => ClickCell(1));
                Measure("arrows-up-down", sample, () =>
                {
                    NativeKey(VirtualKeyShort.UP); SelectedRow(0);
                    NativeKey(VirtualKeyShort.DOWN); SelectedRow(1);
                });
            }
            ClickCell(0);
            Record("native-ime-mode", new { virtualKey = "VK_IME_OFF", reason = "ASCII pending phase" });
            Keyboard.TypeVirtualKeyCode(0x1A);
            foreach (var key in new[] { VirtualKeyShort.KEY_D, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_A, VirtualKeyShort.KEY_G,
                VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_S, VirtualKeyShort.KEY_T, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_C })
                NativeKey(key);
            Snapshot("after-ascii-input-before-readiness", expectedIssue: "I1");
            Wait(() => Element("GridCell0_0").AsTextBox().Text == "diagnostic", "Physical ASCII input must be visible on I1 before the scroll probe.");
            SelectedRow(0);
            NativeKey(VirtualKeyShort.LEFT);
            Snapshot("pending-before-wheel", expectedIssue: "I1");
            Checkpoint("pending-before-wheel");
            Wheel("vertical-down", 100, false);
            Snapshot("bottom-after-wheel", expectedIssue: "I1");
            Wheel("vertical-up", 0, false);
            Snapshot("returned-pending", expectedIssue: "I1");
            if (fields > 1)
            {
                Wheel("horizontal-right", 100, true); Snapshot("horizontal-right", expectedIssue: "I1");
                Wheel("horizontal-left", 0, true); Snapshot("horizontal-return", expectedIssue: "I1");
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                Mouse.Position = SheetPoint();
                Mouse.Scroll(-24); Mouse.Scroll(24); FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                Record("native-rapid-roundtrip", new { repeat, input = "physical vertical wheel -24/+24; no focus command" });
            }
            DriverWait(150); Snapshot("rapid-roundtrip");
            Wheel("return-before-drag", 0, false);
            DragScrollbar();
            Wheel("return-after-drag", 0, false);
            Snapshot("drag-return");

            // A pointer selection is intentional here; wheel phases above never escape to a command.
            ClickCell(0);
            Measure("commit-pending-title", 0, () => NativeKey(VirtualKeyShort.RETURN));
            Wait(() => { var field = FirstTitle(ReadCheckpoint()); var change = field.GetProperty("Change");
                return change.ValueKind != JsonValueKind.Null && change.GetProperty("Value").GetString() == "diagnostic"
                    && field.GetProperty("Buffer").ValueKind == JsonValueKind.Null; }, "Commit must be durable on I1 before observing Undo.");
            Checkpoint("after-commit");
            Measure("undo-title", 0, () => WorkspaceUi.Invoke(window, "GridUndo"));
            Wait(() => { var field = FirstTitle(ReadCheckpoint()); return field.GetProperty("Change").ValueKind == JsonValueKind.Null
                && field.GetProperty("Buffer").GetString() == "diagnostic"; }, "Undo must restore I1's preceding buffer before the next phase.");
            Snapshot("after-undo"); Checkpoint("after-undo");

            Resize(1400, 900); Snapshot("wide-workspace");
            ClickCell(0); Wheel("wide-down", 100, false); Snapshot("wide-bottom", expectedIssue: "I1");
            Wheel("wide-up", 0, false);
            Measure("project-P2-P1", 0, () => { OpenProject("P2"); OpenProject("P1"); });
            Snapshot("returned-project"); Checkpoint("after-project-roundtrip");
            if (physicalIme) PhysicalImeRoundtrip();
            closeRequested = true; window.Close();
            Wait(() => process.HasExited, "Ordinary normal close must end the original process.", 15);
            normal = process.ExitCode == 0;
            Assert.That(normal, Is.True);
            Checkpoint("after-normal-close");
            completed = true;
        }
        catch (Exception error)
        {
            Record("diagnostic-failure", new { type = error.GetType().FullName, error.Message });
            if (window is not null && !process.HasExited)
                try { Snapshot("failure"); Checkpoint("failure"); } catch (Exception observer) { Record("failure-capture-error", new { observer.Message }); }
            throw;
        }
        finally
        {
            if (!process.HasExited && window is not null)
            {
                try { closeRequested = true; window.Close(); normal = process.WaitForExit(10000) && process.ExitCode == 0; }
                catch (Exception closeError) { Record("close-error", new { closeError.Message }); }
            }
            var forced = !process.HasExited;
            if (forced) { process.Kill(true); process.WaitForExit(5000); }
            Write("lifetime.json", new { pid = process.Id, completed, closeRequested, normal, forced, exitCode = process.HasExited ? process.ExitCode : (int?)null });
            TestContext.AddTestAttachment(events, "Driver observations, explicitly separate from application spans.");
            TestContext.Out.WriteLine("Local sheet diagnostic artifacts: " + output);
        }

        AutomationElement Element(string id) => WorkspaceUi.Element(window!, id);
        void SelectedRow(int row) => Wait(() => Element("GridSelection").Name == $"行 {row + 1} 列 1 ～ 行 {row + 1} 列 1",
            "The native input must reach the expected public selected row before the next input.");
        void ClickCell(int row)
        {
            var cell = VisibleCell(row);
            Record("native-cell-click-start", new { row, expectedIssue = "I" + (row + 1), bounds = cell.BoundingRectangle });
            cell.Click();
            SelectedRow(row);
            Wait(() => Element("GridCell" + row + "_0").Properties.HasKeyboardFocus.ValueOrDefault,
                "The intentionally clicked editor must report focus before keyboard input; a timeout is retained as a diagnostic failure.");
            Record("native-cell-click-observed", new { row, expectedIssue = "I" + (row + 1), bounds = cell.BoundingRectangle });
        }
        void NativeKey(VirtualKeyShort key, bool imePacing = false)
        {
            Record("native-key-start", new { key = key.ToString(), imePacing });
            Keyboard.Type(key); FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            if (imePacing) DriverWait(100);
            Record("native-key-input-drained", new { key = key.ToString(), note = "Driver input drain is not app/render completion." });
        }
        void PhysicalImeRoundtrip()
        {
            Record("physical-ime-phase-start", new { issue = "I1", item = "P1T1", input = "Physical NIHONGO; Microsoft IME must already be available. No Unicode insertion." });
            ClickCell(0);
            Assert.That(GetForegroundWindow(), Is.EqualTo(window!.Properties.NativeWindowHandle.Value));
            Keyboard.TypeVirtualKeyCode(0x1A);
            NativeKey(VirtualKeyShort.ESCAPE, imePacing: true);
            Wait(() => Element("GridCell0_0").AsTextBox().Text == "Issue 1", "Cancel the restored ASCII pending buffer before direct IME input.");
            Wait(() => FirstTitle(ReadCheckpoint()).GetProperty("Buffer").ValueKind == JsonValueKind.Null,
                "The cancellation must be durable before the IME probe.");
            Snapshot("ime-selected-first-title", "I1");
            try
            {
                Record("native-ime-mode", new { virtualKey = "VK_IME_ON", reason = "Physical composition probe" });
                Keyboard.TypeVirtualKeyCode(0x16);
                DriverWait(100);
                foreach (var key in new[] { VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_H,
                    VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_G, VirtualKeyShort.KEY_O }) NativeKey(key, imePacing: true);
                Assert.That(Element("GridCell0_0").AsTextBox().Text, Is.EqualTo("にほんご"), "Every physical direct IME syllable must appear exactly once on I1.");
                Snapshot("ime-composing-hiragana", "I1");
                NativeKey(VirtualKeyShort.SPACE, imePacing: true);
                Assert.That(Element("GridCell0_0").AsTextBox().Text, Is.EqualTo("日本語"));
                Snapshot("ime-unconfirmed-candidate-before-wheel", "I1");
                Assert.That(FirstTitle(Checkpoint("ime-before-wheel")).GetProperty("Change").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Wheel("ime-composing-down", 100, false); Snapshot("ime-composing-bottom", "I1");
                Wheel("ime-composing-up", 0, false); Snapshot("ime-composing-return", "I1");
                Assert.That(Element("GridCell0_0").AsTextBox().Text, Is.EqualTo("日本語"), "Native wheel roundtrip must retain the same I1 candidate text.");
                SelectedRow(0);
                // Reported focus is captured above. Enter's actual text/commit effect and durable key
                // establish the input target without treating a UIA focus error as pixel truth.
                NativeKey(VirtualKeyShort.RETURN, imePacing: true);
                Snapshot("ime-first-enter-confirmed", "I1");
                Wait(() =>
                {
                    var title = FirstTitle(ReadCheckpoint());
                    return title.GetProperty("Buffer").GetString() == "日本語" && title.GetProperty("Change").ValueKind == JsonValueKind.Null;
                }, "The first Enter must confirm IME text without committing the cell.");
                var confirmed = Checkpoint("ime-first-enter-confirmed");
                Assert.That(WorkKeys(confirmed), Is.EquivalentTo(new[] { "Title/I1" }), "Composition must not retarget work to another row/field.");
                NativeKey(VirtualKeyShort.RETURN, imePacing: true);
                Wait(() =>
                {
                    var title = FirstTitle(ReadCheckpoint()); var change = title.GetProperty("Change");
                    return change.ValueKind != JsonValueKind.Null && change.GetProperty("Value").GetString() == "日本語"
                        && title.GetProperty("Buffer").ValueKind == JsonValueKind.Null;
                }, "The second Enter must commit 日本語 to Title/I1.");
                Snapshot("ime-second-enter-committed", "I1");
                Assert.That(WorkKeys(Checkpoint("ime-second-enter-committed")), Is.EquivalentTo(new[] { "Title/I1" }));
                Record("physical-ime-phase-completed", new { confirmed = "日本語", committedKey = "Title/I1" });
            }
            catch
            {
                try { Snapshot("ime-failure-before-mode-restore", "I1"); }
                catch (Exception observer) { Record("ime-failure-capture-error", new { observer.Message }); }
                throw;
            }
            finally
            {
                Keyboard.TypeVirtualKeyCode(0x1A);
                Record("native-ime-mode", new { virtualKey = "VK_IME_OFF", reason = "End optional probe" });
            }
        }
        JsonElement FirstTitle(JsonElement checkpoint) => checkpoint.GetProperty("Fields").EnumerateArray().Single(field =>
            field.GetProperty("Key").GetProperty("Kind").GetString() == "Title" && field.GetProperty("Key").GetProperty("NodeId").GetString() == "I1");
        string[] WorkKeys(JsonElement checkpoint) => checkpoint.GetProperty("Fields").EnumerateArray().Where(field =>
            field.GetProperty("Buffer").ValueKind != JsonValueKind.Null || field.GetProperty("Change").ValueKind != JsonValueKind.Null)
            .Select(field => field.GetProperty("Key").GetProperty("Kind").GetString() + "/" + field.GetProperty("Key").GetProperty("NodeId").GetString()).ToArray();
        AutomationElement VisibleCell(int row)
        {
            var cell = Element("GridCell" + row + "_0");
            var bounds = cell.BoundingRectangle;
            var top = Element("GridHeader0").BoundingRectangle.Bottom;
            var bottom = Element("GridSelection").BoundingRectangle.Top;
            Assert.That(!cell.Properties.IsOffscreen.Value && bounds.Top >= top - 1 && bounds.Bottom <= bottom + 1, Is.True,
                "Do not redirect native input into an offscreen/ambiguous cell.");
            return cell;
        }
        Point SheetPoint()
        {
            var top = Element("GridHeader0").BoundingRectangle.Bottom;
            var footer = Element("GridSelection").BoundingRectangle;
            return new Point(footer.Left + 12, (top + footer.Top) / 2);
        }
        void OpenProject(string name)
        {
            // Use the toggle's public pane-state label. Offscreen peers can be stale after a resize.
            var toggle = Element("ToggleProjectNavigation");
            if (toggle.Name == "Project一覧を表示") toggle.AsButton().Invoke();
            Wait(() => Element("ToggleProjectNavigation").Name == "Project一覧を折りたたむ", "The Project pane must be open before selection.");
            var entry = window!.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.TreeItem)));
            Assert.That(entry, Is.Not.Null, "Seeded Project must be reachable through navigation.");
            entry!.Patterns.Invoke.Pattern.Invoke();
            Wait(() => Element("ProjectSummary").Name.StartsWith(name, StringComparison.Ordinal), "Selected Project context must update.");
            _ = Element("GridCell0_0");
        }
        void Resize(int width, int height)
        {
            window!.Patterns.Transform.Pattern.Resize(width, height); window.Move(24, 24);
            Wait(() => Math.Abs(window.BoundingRectangle.Width - width) <= 2 && Math.Abs(window.BoundingRectangle.Height - height) <= 2,
                "Requested diagnostic size must settle.");
            DriverWait(200);
        }
        void Measure(string action, int sample, Action invoke)
        {
            var begin = Stopwatch.GetTimestamp(); Record("driver-action-start", new { action, sample, warmup = sample < 0 });
            invoke();
            Record("driver-action-end", new { action, sample, elapsedTicks = Stopwatch.GetTimestamp() - begin,
                boundary = "Driver invocation/input drain only; not isolated app latency." });
        }
        void Wheel(string phase, double endpoint, bool horizontal)
        {
            var pattern = Element("ProjectItems").Patterns.Scroll.Pattern;
            if (!(horizontal ? pattern.HorizontallyScrollable.Value : pattern.VerticallyScrollable.Value))
            { Record("wheel-not-executed", new { phase, reason = "axis reports non-scrollable" }); return; }
            var point = SheetPoint(); Mouse.Position = point;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var begin = Stopwatch.GetTimestamp();
                if (horizontal) Mouse.HorizontalScroll(endpoint == 100 ? 120 : -120);
                else Mouse.Scroll(endpoint == 100 ? -120 : 120);
                FlaUI.Core.Input.Wait.UntilInputIsProcessed(); DriverWait(75);
                var reported = horizontal ? pattern.HorizontalScrollPercent.Value : pattern.VerticalScrollPercent.Value;
                Record("wheel-observation", new { phase, attempt, point, endpoint, reported, driverTicks = Stopwatch.GetTimestamp() - begin });
                if (Math.Abs(reported - endpoint) < 1) break;
            }
            DriverWait(150);
            Record("wheel-settled-observation", new { phase, horizontal = pattern.HorizontalScrollPercent.Value, vertical = pattern.VerticalScrollPercent.Value });
            var attained = horizontal ? pattern.HorizontalScrollPercent.Value : pattern.VerticalScrollPercent.Value;
            if (Math.Abs(attained - endpoint) >= 1)
                Record("wheel-endpoint-not-reached", new { phase, requested = endpoint, attained, note = "The following screenshot is the attained viewport, not proof of the requested endpoint." });
        }
        void DragScrollbar()
        {
            var viewport = Element("ProjectItems");
            var headerBottom = Element("GridHeader0").BoundingRectangle.Bottom;
            var footerTop = Element("GridSelection").BoundingRectangle.Top;
            var bar = viewport.FindAllDescendants(cf => cf.ByControlType(ControlType.ScrollBar))
                .FirstOrDefault(item => !item.Properties.IsOffscreen.Value && item.BoundingRectangle.Height > item.BoundingRectangle.Width);
            var thumb = bar?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Thumb));
            if (thumb is null || thumb.Properties.IsOffscreen.Value)
            {
                Record("drag-not-executed", new { reason = "No visible public scrollbar thumb; do not guess a drag target." });
                Snapshot("scrollbar-thumb-unavailable"); return;
            }
            var rect = thumb.BoundingRectangle;
            var from = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            Assert.That(window!.BoundingRectangle.Contains(from) && from.Y >= headerBottom && from.Y <= footerTop, Is.True);
            var to = new Point(from.X, footerTop - Math.Max(20, rect.Height / 2));
            Record("native-drag-start", new { from, to, note = "Native scrollbar press may change focus; no forced editor focus." });
            Mouse.Drag(from, to, MouseButton.Left); FlaUI.Core.Input.Wait.UntilInputIsProcessed(); DriverWait(150); Snapshot("scrollbar-dragged");
        }
        void Snapshot(string phase, string? expectedIssue = null)
        {
            var prefix = (++captureNumber).ToString("D2") + "-" + phase;
            var captureTicks = Stopwatch.GetTimestamp();
            using (var capture = Capture.Element(window!)) capture.ToFile(Path.Combine(output, prefix + ".png"));
            var observationStart = Stopwatch.GetTimestamp();
            object observed;
            try
            {
                var focus = automation.FocusedElement();
                var scroll = Element("ProjectItems").Patterns.Scroll.Pattern;
                observed = new
                {
                    reportedFocusId = focus?.Properties.AutomationId.ValueOrDefault, reportedFocusName = focus?.Properties.Name.ValueOrDefault,
                    reportedFocusIsObservationOnly = true, selection = Element("GridSelection").Name,
                    horizontal = scroll.HorizontalScrollPercent.Value, vertical = scroll.VerticalScrollPercent.Value,
                    firstCell = window!.FindFirstDescendant(cf => cf.ByAutomationId("GridCell0_0"))?.Name,
                    lastCell = window.FindFirstDescendant(cf => cf.ByAutomationId("GridCell" + (rows - 1) + "_0"))?.Name
                };
            }
            catch (COMException error) { observed = new { uiaError = error.HResult, error.Message }; }
            catch (TimeoutException error) { observed = new { uiaObservationUnavailable = error.GetType().Name, error.Message }; }
            process.Refresh();
            Write(prefix + ".json", new { phase, expectedIssue, expectedItem = expectedIssue is null ? null : "P1T" + expectedIssue[1..],
                captureTicks, observationStart, observationEnd = Stopwatch.GetTimestamp(), utc = DateTimeOffset.UtcNow,
                processWorkingSetBytes = process.WorkingSet64, processPrivateBytes = process.PrivateMemorySize64,
                bounds = window!.BoundingRectangle, dpi = GetDpiForWindow(window.Properties.NativeWindowHandle.Value), observed });
            Record("snapshot", new { phase, prefix });
        }
        JsonElement ReadCheckpoint()
        {
            var directory = Path.Combine(data, "Drafts");
            using var document = Retry.WhileNull(() =>
            {
                try
                {
                    var file = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").SingleOrDefault() : null;
                    if (file is null) return null;
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    return JsonDocument.Parse(stream);
                }
                catch (FileNotFoundException) { return null; }
            }, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50)).Result;
            Assert.That(document, Is.Not.Null, "Capture an actual coherent checkpoint, not an absent-file assumption.");
            return document!.RootElement.Clone();
        }
        JsonElement Checkpoint(string phase)
        {
            var checkpoint = ReadCheckpoint();
            File.WriteAllText(Path.Combine(output, "checkpoint-" + phase + ".json"), checkpoint.GetRawText());
            Record("checkpoint", new { phase, revision = checkpoint.GetProperty("Revision").GetInt64(),
                fields = checkpoint.GetProperty("Fields").EnumerateArray().Where(field =>
                    field.GetProperty("Buffer").ValueKind != JsonValueKind.Null || field.GetProperty("Change").ValueKind != JsonValueKind.Null).Select(field => field.Clone()).ToArray() });
            return checkpoint;
        }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
