using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using NUnit.Framework;
using Application = FlaUI.Core.Application;

namespace GhProjectsBoards.E2E.Tests;

[TestFixture, Category("E2E"), Category("Performance"), Category("PlanScreenPerformance"), NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class PlanScreenPerformanceTests
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool GetSystemParameter(uint action, uint parameter, ref int value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SetSystemParameter(uint action, uint parameter, nint value, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint x, uint y, uint data, nuint extraInfo);

    [Test]
    public void VersionEditsRecordNativeEnterToChangedDatePixelsInTheOrdinaryApp()
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_E2E") != "1")
            Assert.Ignore("Run Test-E2E.ps1 -Filter TestCategory=PlanScreenPerformance on an idle Windows desktop.");
        var executable = Environment.GetEnvironmentVariable("GHPB_E2E_APP_PATH")!;
        var fake = Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!;
        var root = Path.Combine(Environment.GetEnvironmentVariable("GHPB_E2E_ARTIFACTS")!, "plan-screen");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(root);
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fake)!, "..", "..", "..", "..", ".."));
        var prepare = new ProcessStartInfo("pwsh") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(repo, "scripts", "Start-Evaluation.ps1"), "-NoBuild", "-Configuration", new DirectoryInfo(Path.GetDirectoryName(fake)!).Parent!.Name, "-PrepareOnly", "-DataRoot", data })
            prepare.ArgumentList.Add(argument);
        RemoveCredentials(prepare);
        using (var fixture = Process.Start(prepare)!) {
            Assert.That(fixture.WaitForExit(30000), Is.True, "Evaluation preparation must finish.");
            Assert.That(fixture.ExitCode, Is.Zero);
        }
        using var dpi = new DesktopDpiScope();
        using var automation = new UIA3Automation();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, WindowStyle = ProcessWindowStyle.Hidden };
        start.Environment["GHPB_DATA_ROOT"] = data;
        start.Environment["GH_CONFIG_DIR"] = Path.Combine(data, "fake-gh");
        start.Environment["PATH"] = Path.Combine(data, "fake-gh", "bin") + Path.PathSeparator + start.Environment["PATH"];
        start.Environment["GHPB_PLAN_METRICS"] = Path.Combine(root, "product-stages.jsonl");
        RemoveCredentials(start);
        using var process = Process.Start(start)!;
        using var app = Application.Attach(process.Id);
        Window? window = null;
        var samples = new List<object>();
        var wheelRecordings = new List<object>();
        var flyoutRecordings = new List<object>();
        string outcome = "incomplete";
        string? failure = null;
        object? environment = null;
        var originalAnimation = 0;
        Assert.That(GetSystemParameter(0x1042, 0, ref originalAnimation, 0), Is.True); // SPI_GETCLIENTAREAANIMATION
        var changedAnimation = originalAnimation == 0 && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        if (changedAnimation) Assert.That(SetSystemParameter(0x1043, 0, new nint(1), 0), Is.True);
        try
        {
            window = AttachMainWindow(app, automation, process, root);
            Assert.That(window, Is.Not.Null);
            WinUiProcess.AssertRuntime(process);
            window!.Patterns.Transform.Pattern.Move(0, 0);
            window.Patterns.Transform.Pattern.Resize(1920, 1080);
            window.SetForeground();
            AutomationElement Find(string id) => window.FindFirstDescendant(c => c.ByAutomationId(id)) ?? throw new AssertionException("Missing control: " + id);
            Find("PlanConnect").AsButton().Invoke();
            Wait(() => Find("AvailableProjects").AsListBox().Items.Length == 2);
            Find("AvailableProjects").AsListBox().Select(0);
            Wait(() => Find("OpenProjectName").Properties.Name.ValueOrDefault == "第2027.04版");
            Wait(() => Find("PlanCell1_Title").IsOffscreen == false && Find("PlanUnpublished").Properties.Name.ValueOrDefault == "未発行 0 タスク");
            var list = Find("PlanTasks");
            environment = new {
                window = window.BoundingRectangle, scale = GetDpiForWindow(window.Properties.NativeWindowHandle.Value) / 96d,
                os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(),
                processors = Environment.ProcessorCount, initialVerticalScrollPercent = list.Patterns.Scroll.Pattern.VerticalScrollPercent.Value,
                clientAreaAnimationsEnabled = originalAnimation != 0 || changedAnimation,
                fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "evaluation.json"))).RootElement.Clone()
            };
            list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 0);
            Wait(() => list.Patterns.Scroll.Pattern.VerticalScrollPercent.Value == 0);
            var rowBounds = Rectangle.Round(list.BoundingRectangle);
            var rowRegion = new Rectangle(rowBounds.Left + 2, rowBounds.Top + 2,
                Math.Min(600, rowBounds.Width - 4), Math.Min(650, rowBounds.Height - 4));
            Assert.That(rowRegion.Width > 100 && rowRegion.Height > 100, Is.True);
            Assert.That(SetCursorPos(rowRegion.Left + 200, rowRegion.Top + 100), Is.True);
            using (var capture = Capture.Element(window)) capture.ToFile(Path.Combine(root, "before-scroll.png"));
            foreach (var down in new[] { true, false })
            {
                var direction = down ? "down" : "up";
                var beforeOffset = list.Patterns.Scroll.Pattern.VerticalScrollPercent.Value;
                var recording = RecordPixels(Path.Combine(root, "scroll-" + direction), rowRegion, mark => {
                    for (var notch = 1; notch <= 40; notch++) {
                        mark($"wheel-{direction}:{notch}");
                        MouseEvent(0x0800, 0, 0, unchecked((uint)(down ? -120 : 120)), 0);
                        Thread.Sleep(50);
                    }
                }, TimeSpan.FromSeconds(1));
                var afterOffset = list.Patterns.Scroll.Pattern.VerticalScrollPercent.Value;
                wheelRecordings.Add(new { direction, notches = 40, requestedIntervalMs = 50,
                    beforeVerticalPercent = beforeOffset, afterVerticalPercent = afterOffset, region = rowRegion,
                    recording.ScreenChanged, recording.FinalStateStable, recording.MaxCaptureIntervalMs,
                    boundary = "Native wheel input and raw desktop pixels over the visible row IDs/titles; inspect images for stale/blank content. Hash changes do not classify population or physical display flicker." });
                WriteSummary();
                using (var capture = Capture.Element(window)) capture.ToFile(Path.Combine(root, "after-scroll-" + direction + ".png"));
                Assert.That(down ? afterOffset > beforeOffset : afterOffset < beforeOffset, Is.True,
                    "The ordinary app must receive the recorded wheel input.");
            }
            // Record the native return position above, then reset outside the edit measurement.
            list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 0);
            Wait(() => list.Patterns.Scroll.Pattern.VerticalScrollPercent.Value == 0);
            list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 13);
            Wait(() => Find("PlanCell144_Remaining").IsOffscreen == false);
            Thread.Sleep(500);
            using (var capture = Capture.Element(window)) capture.ToFile(Path.Combine(root, "before-edits.png"));
            var original = Find("PlanCell144_Remaining").AsTextBox().Text;
            Assert.That(double.TryParse(original, out var remaining), Is.True, "ID 144 must be an editable version task.");
            for (var index = 0; index < 6; index++)
            {
                var editor = Find("PlanCell144_Remaining").AsTextBox();
                editor.Focus();
                Wait(() => editor.Properties.HasKeyboardFocus.ValueOrDefault);
                editor.Text = index % 2 == 0 ? (remaining + 24).ToString(System.Globalization.CultureInfo.CurrentCulture) : original;
                var endCell = Find("PlanCell144_End").AsTextBox();
                var before = endCell.Text;
                var bounds = endCell.BoundingRectangle;
                // Exclude selection borders/changed markers. The date text is read independently through UIA.
                var region = Rectangle.Inflate(Rectangle.Round(bounds), -5, -5);
                Assert.That(region.Width > 20 && region.Height > 5, Is.True, "The date must be on screen.");
                Thread.Sleep(200);
                var recording = RecordPixels(Path.Combine(root, $"edit-{index + 1}"), region,
                    mark => { mark("Enter"); Keyboard.Type(VirtualKeyShort.RETURN); }, TimeSpan.FromSeconds(3));
                var finalEndCell = Find("PlanCell144_End").AsTextBox();
                var after = finalEndCell.Text;
                var unchangedBounds = finalEndCell.BoundingRectangle == bounds;
                var valid = before != after && unchangedBounds && recording.ScreenChanged && recording.FinalStateStable;
                samples.Add(new { sample = index + 1, row = 144, title = Find("PlanCell144_Title").AsTextBox().Text,
                    remaining = editor.Text, before, after, unchangedBounds, valid,
                    recording.FirstFinalPixelsMs, recording.FirstFinalFrameStartMs, recording.PreviousFrameMs,
                    recording.PreviousFrameStartMs, recording.MaxCaptureIntervalMs,
                    within200Ms = valid && recording.FirstFinalPixelsMs <= 200 });
                WriteSummary();
                Assert.That(valid, Is.True, "A timing sample needs changed dates, unchanged screen coordinates and stable changed pixels; raw frames are preserved.");
            }
            using (var capture = Capture.Element(window)) capture.ToFile(Path.Combine(root, "after-edits.png"));
            // Retain native character delivery and filtering separately at both typing rhythms.
            var filter = Find("PlanSheetFilter").AsTextBox();
            var filterRegion = Rectangle.Round(window.BoundingRectangle);
            filterRegion.Height = Math.Min(450, filterRegion.Height);
            var filterResults = new List<object>();
            foreach (var pauseMs in new[] { 200, 700 })
            {
                filter.Focus();
                Wait(() => filter.Properties.HasKeyboardFocus.ValueOrDefault);
                var recordingName = pauseMs == 200 ? "filter-fast" : "filter-slow";
                RecordPixels(Path.Combine(root, recordingName), filterRegion, mark => {
                    var cadence = Stopwatch.StartNew();
                    var index = 0;
                    foreach (var character in "PS-00") {
                        // FlaUI includes input-processing time. Schedule key starts
                        // absolutely so that overhead does not lengthen each pause.
                        var remaining = TimeSpan.FromMilliseconds(index++ * pauseMs) - cadence.Elapsed;
                        if (remaining > TimeSpan.Zero) Thread.Sleep(remaining);
                        mark("key:" + character);
                        Keyboard.Type(character.ToString());
                    }
                }, TimeSpan.FromSeconds(2));
                var count = Find("PlanSheetFilterCount").Properties.Name.ValueOrDefault;
                filterResults.Add(new { pauseMs, recordingName, text = filter.Text, count });
                File.WriteAllText(Path.Combine(root, "filter-result.json"), JsonSerializer.Serialize(new {
                    results = filterResults,
                    boundary = "Native typing at 200 and 700 ms intervals and raw desktop pixel frames; inspect character delivery and result replacement separately."
                }));
                Assert.That(filter.Text, Is.EqualTo("PS-00"));
                Assert.That(count, Is.EqualTo("120 件"));
                filter.Text = "";
                Keyboard.Type(VirtualKeyShort.RETURN);
                Wait(() => window.FindFirstDescendant(c => c.ByAutomationId("PlanSheetFilterCount")) is null or { IsOffscreen: true });
                // Settle row realization outside the next typing/menu recording.
                Thread.Sleep(1000);
            }
            Assert.That(originalAnimation != 0 || changedAnimation, Is.True, "Menu recording requires Windows client-area animations enabled.");
            var more = window.FindFirstDescendant(c => c.ByAutomationId("PlanSheetMoreCommands"))
                ?? window.FindFirstDescendant(c => c.ByAutomationId("PlanSheetMore"))
                ?? Find("MoreButton");
            var menuBounds = more.BoundingRectangle;
            var menuRegion = Rectangle.FromLTRB(Math.Max(0, (int)menuBounds.Left - 300), (int)menuBounds.Bottom + 2,
                Math.Min(1920, (int)menuBounds.Right + 300), Math.Min(1080, (int)menuBounds.Bottom + 550));
            var menuOpen = RecordPixels(Path.Combine(root, "menu-open"), menuRegion,
                mark => { mark("More click"); more.Click(); }, TimeSpan.FromSeconds(2));
            process.Refresh();
            Assert.That(process.HasExited, Is.False,
                process.HasExited ? $"The app exited while opening the menu: 0x{process.ExitCode:X8}. Desktop pixels are not an opened menu." : "");
            Assert.That(window.FindAllDescendants(c => c.ByAutomationId("PlanSheetCopy")).Any(item => !item.IsOffscreen), Is.True,
                "The captured state must contain the application's opened command menu.");
            Assert.That(menuOpen.ScreenChanged && menuOpen.FinalStateStable, Is.True, "The opened menu must appear in the recorded pixels.");
            var menuClose = RecordPixels(Path.Combine(root, "menu-close"), menuRegion,
                mark => { mark("Escape"); Keyboard.Type(VirtualKeyShort.ESCAPE); }, TimeSpan.FromSeconds(2));
            process.Refresh();
            Assert.That(process.HasExited, Is.False,
                process.HasExited ? $"The app exited while closing the menu: 0x{process.ExitCode:X8}." : "");
            Assert.That(window.FindAllDescendants(c => c.ByAutomationId("PlanSheetCopy")).Any(item => !item.IsOffscreen), Is.False);
            Assert.That(menuClose.ScreenChanged && menuClose.FinalStateStable, Is.True, "Escape must remove the recorded menu.");
            RecordFlyout("columns", "PlanSheetColumns", "PlanColumnRemaining", [
                "PlanColumnId", "PlanColumnIndicator", "PlanColumnTitle", "PlanColumnAssignees", "PlanColumnEstimate",
                "PlanColumnRemaining", "PlanColumnActual", "PlanColumnStart", "PlanColumnEnd", "PlanColumnPredecessors",
                "PlanColumnStartNoEarlierThan", "PlanColumnFixed", "PlanColumnStatus"]);
            RecordFlyout("predecessor", "PlanSheetPredecessorAdd", "PlanPredecessorSearch",
                ["PlanPredecessorSearch", "PlanPredecessorCandidates", "PlanPredecessorConfirm", "PlanPredecessorCancel"]);
            RecordFlyout("project", "PlanProjectPicker", "PlanChooseProject",
                ["RegisteredProjects", "PlanChooseProject", "PlanConnection"]);
            window.Close();
            Assert.That(process.WaitForExit(10000), Is.True, "Normal close must finish local storage.");
            Assert.That(process.ExitCode, Is.Zero);
            outcome = "completed";

            void RecordFlyout(string name, string commandId, string markerId, string[] contentIds)
            {
                AutomationElement? Visible(string id) => window.FindAllDescendants(c => c.ByAutomationId(id)).FirstOrDefault(item => !item.IsOffscreen);
                AutomationElement PrepareCommand()
                {
                    if (Visible(commandId) is { } command) return command;
                    more.Click();
                    Wait(() => Visible(commandId) is not null);
                    // The baseline uses CommandBar's animated native overflow. Finish
                    // that prerequisite outside the measured child-flyout opening.
                    Thread.Sleep(350);
                    return Visible(commandId)!;
                }
                void CloseProbe()
                {
                    Keyboard.Type(VirtualKeyShort.ESCAPE);
                    Wait(() => Visible(markerId) is null);
                    // The baseline can leave its parent CommandBar overflow open.
                    if (Visible("PlanSheetCopy") is not null) {
                        Keyboard.Type(VirtualKeyShort.ESCAPE);
                        Wait(() => Visible("PlanSheetCopy") is null);
                        Thread.Sleep(350);
                    }
                }

                // Discover the actual popup's content bounds before recording; do not
                // poll UIA or retain full-window bitmaps during the measured transition.
                PrepareCommand().Click();
                Wait(() => Visible(markerId) is not null);
                Thread.Sleep(350);
                var popupBounds = contentIds.Select(id => Rectangle.Round(Visible(id)!.BoundingRectangle)).Aggregate(Rectangle.Union);
                var region = Rectangle.Intersect(Rectangle.Inflate(popupBounds, 24, 24), Rectangle.Round(window.BoundingRectangle));
                Assert.That(region.Width > 100 && region.Height > 50 && region.Width * region.Height <= 900 * 700, Is.True,
                    "Flyout capture must have bounded, on-screen content bounds.");
                CloseProbe();
                var trigger = PrepareCommand();
                var triggerBounds = trigger.BoundingRectangle;
                var point = trigger.GetClickablePoint();
                File.WriteAllText(Path.Combine(root, name + "-input.json"), JsonSerializer.Serialize(new {
                    commandId, markerId, popupBounds, region, triggerBounds, point, preparedUtc = DateTime.UtcNow,
                    input = "FlaUI native SendInput down/up at the prepared point; no UIA calls during capture."
                }, new JsonSerializerOptions { WriteIndented = true }));
                Assert.That(SetCursorPos(point.X, point.Y), Is.True);
                FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                var opened = RecordPixels(Path.Combine(root, name + "-open"), region, mark => {
                    mark(name + " click");
                    Mouse.Down(); Mouse.Up();
                    // Match the successful probe's native input boundary. Moving the
                    // cursor away before queued press/release is processed can leave
                    // only hover feedback instead of invoking the menu item.
                    FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                    Assert.That(SetCursorPos(1850, 1030), Is.True);
                }, TimeSpan.FromSeconds(2));
                process.Refresh();
                Assert.That(process.HasExited, Is.False, name + " opening must leave the app running.");
                Assert.That(Visible(markerId), Is.Not.Null, name + " content must be present in the recorded state.");
                Assert.That(opened.ScreenChanged, Is.True, name + " must appear in changed pixels.");
                // The search owns a focused native caret. Preserve its full frames and
                // stability measurement, but do not confuse a blink with flyout motion.
                if (name != "predecessor") Assert.That(opened.FinalStateStable, Is.True, name + " must finish in stable pixels.");
                var closed = RecordPixels(Path.Combine(root, name + "-close"), region,
                    mark => { mark("Escape"); Keyboard.Type(VirtualKeyShort.ESCAPE); }, TimeSpan.FromSeconds(2));
                process.Refresh();
                Assert.That(process.HasExited, Is.False, name + " closing must leave the app running.");
                Assert.That(Visible(markerId), Is.Null, name + " content must be dismissed by Escape.");
                Assert.That(closed.ScreenChanged && closed.FinalStateStable, Is.True, name + " must disappear from the recorded pixels.");
                flyoutRecordings.Add(new { name, commandId, markerId, popupBounds, region, triggerBounds, opened, closed,
                    boundary = "Ordinary-app native click/Escape with Windows animations enabled. Raw frames and copy timestamps define the observation cadence; inspect complete popup content separately from native caret/hover feedback. No physical one-frame assertion is inferred from sampling." });
                WriteSummary();
                if (Visible("PlanSheetCopy") is not null) {
                    Keyboard.Type(VirtualKeyShort.ESCAPE);
                    Wait(() => Visible("PlanSheetCopy") is null);
                    Thread.Sleep(350);
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            if (window is not null && !process.HasExited)
                try { using var capture = Capture.Element(window); capture.ToFile(Path.Combine(root, "failure.png")); }
                catch (Exception captureError) { TestContext.Error.WriteLine(captureError); }
            throw;
        }
        finally
        {
            WriteSummary();
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
            if (changedAnimation) SetSystemParameter(0x1043, 0, nint.Zero, 0);
        }
        void WriteSummary() => File.WriteAllText(Path.Combine(root, "plan-screen.json"), JsonSerializer.Serialize(new {
            schemaVersion = 1, outcome, failure, rows = 1040, version = "2027.04", environment, wheelRecordings, flyoutRecordings, samples,
            processExited = process.HasExited, processExitCode = process.HasExited ? (int?)process.ExitCode : null,
            targetMs = 200, boundary = "Native Enter injection start to first desktop copy matching the final stable changed End-date pixels. CopyFromScreen is desktop-compositor evidence, not monitor photon latency. The observed upper bound is final-copy end; the conservative lower bound is previous-copy start. CI runner timings are reported, not asserted as PMO hardware acceptance."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record PixelFrame(double StartMs, double EndMs, string Hash);
    private sealed record PixelResult(bool ScreenChanged, bool FinalStateStable, double? FirstFinalPixelsMs,
        double? FirstFinalFrameStartMs, double? PreviousFrameMs, double? PreviousFrameStartMs, double MaxCaptureIntervalMs);

    private static Window AttachMainWindow(Application app, UIA3Automation automation, Process process, string evidence)
    {
        const int uiaTimeout = unchecked((int)0x80131505);
        var timer = Stopwatch.StartNew();
        var attempts = new List<object>();
        Exception? lastTimeout = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            process.Refresh();
            if (process.HasExited) throw new InvalidOperationException($"The ordinary app exited during startup: 0x{process.ExitCode:X8}.");
            try
            {
                var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(10));
                Record(attempt, window is null ? "window-not-ready" : "attached", null);
                if (window is not null) return window;
            }
            // UIA can time out while the just-created HWND is registering its provider. Retry only
            // that attachment failure; product crashes and other UIA errors remain failures.
            catch (TimeoutException ex) when (ex.InnerException is COMException { HResult: uiaTimeout })
            {
                lastTimeout = ex; Record(attempt, "transient-uia-timeout", ex);
            }
            catch (COMException ex) when (ex.HResult == uiaTimeout)
            {
                lastTimeout = ex; Record(attempt, "transient-uia-timeout", ex);
            }
            catch (Exception ex) { Record(attempt, "failed", ex); throw; }
            if (attempt < 3) Thread.Sleep(250);
        }
        throw new TimeoutException("The ordinary app's window could not be attached in three bounded startup attempts; see startup-attachment.json.", lastTimeout);

        void Record(int attempt, string outcome, Exception? error)
        {
            attempts.Add(new { attempt, elapsedMs = timer.Elapsed.TotalMilliseconds, outcome, error = error?.ToString() });
            File.WriteAllText(Path.Combine(evidence, "startup-attachment.json"), JsonSerializer.Serialize(new {
                processId = process.Id, maxAttempts = 3, windowWaitPerAttemptSeconds = 10, attempts
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static PixelResult RecordPixels(string directory, Rectangle region, Action<Action<string>> input, TimeSpan settle)
    {
        Directory.CreateDirectory(directory);
        var frames = new List<PixelFrame>();
        var images = new Dictionary<string, Bitmap>();
        var inputs = new List<object>();
        using var ready = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var timer = Stopwatch.StartNew();
        double? inputStart = null, inputEnd = null;
        string? failure = null;
        var capture = Task.Run(() => {
            using var dpi = new DesktopDpiScope();
            while (!stop.IsCancellationRequested)
            {
                var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
                var before = timer.Elapsed.TotalMilliseconds;
                using (var graphics = Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(region.Location, Point.Empty, region.Size, CopyPixelOperation.SourceCopy);
                var after = timer.Elapsed.TotalMilliseconds;
                var locked = bitmap.LockBits(new Rectangle(Point.Empty, region.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                string hash;
                try {
                    var pixels = new byte[Math.Abs(locked.Stride) * locked.Height];
                    Marshal.Copy(locked.Scan0, pixels, 0, pixels.Length);
                    hash = Convert.ToHexString(SHA256.HashData(pixels));
                } finally { bitmap.UnlockBits(locked); }
                frames.Add(new(before, after, hash));
                if (!images.TryAdd(hash, bitmap)) bitmap.Dispose();
                ready.Set();
                Thread.Sleep(15);
            }
        });
        try
        {
            if (!ready.Wait(TimeSpan.FromSeconds(5))) {
                if (capture.IsFaulted) capture.GetAwaiter().GetResult();
                throw new TimeoutException("Desktop capture did not start.");
            }
            inputStart = timer.Elapsed.TotalMilliseconds;
            input(label => inputs.Add(new { label, startMs = timer.Elapsed.TotalMilliseconds }));
            inputEnd = timer.Elapsed.TotalMilliseconds;
            Thread.Sleep(settle);
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            stop.Cancel();
            try { capture.GetAwaiter().GetResult(); }
            catch (Exception ex) { failure = (failure ?? "") + ex; }
            try {
                foreach (var (hash, bitmap) in images) bitmap.Save(Path.Combine(directory, hash + ".png"), ImageFormat.Png);
                File.WriteAllText(Path.Combine(directory, "frames.json"), JsonSerializer.Serialize(new {
                    region, inputStartMs = inputStart, inputEndMs = inputEnd, inputs, failure, frames,
                    boundary = "Graphics.CopyFromScreen start/end timestamps; hashes name raw PNGs. Images are encoded only after capture stops."
                }, new JsonSerializerOptions { WriteIndented = true }));
            } finally { foreach (var bitmap in images.Values) bitmap.Dispose(); }
        }
        Assert.That(failure, Is.Null, "Capture failures are not timing samples.");
        var finalHash = frames[^1].Hash;
        var changed = frames[0].Hash != finalHash;
        var stable = frames.Count >= 5 && frames.TakeLast(5).All(frame => frame.Hash == finalHash);
        var first = frames.FindIndex(frame => frame.StartMs >= inputStart && frame.Hash == finalHash);
        var intervals = frames.Zip(frames.Skip(1), (a, b) => b.EndMs - a.EndMs).ToArray();
        return new(changed, stable, first < 0 ? null : frames[first].EndMs - inputStart,
            first < 0 ? null : frames[first].StartMs - inputStart,
            first <= 0 ? null : frames[first - 1].EndMs - inputStart,
            first <= 0 ? null : frames[first - 1].StartMs - inputStart, intervals.Length == 0 ? 0 : intervals.Max());
    }

    private static void RemoveCredentials(ProcessStartInfo start)
    {
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN" }) start.Environment.Remove(name);
    }
    private static void Wait(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(30)) {
            try { if (predicate()) return; } catch (AssertionException) { }
            Thread.Sleep(50);
        }
        Assert.Fail("The ordinary version sheet did not reach the expected state.");
    }
}
