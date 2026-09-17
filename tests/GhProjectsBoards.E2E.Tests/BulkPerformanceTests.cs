using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

// Dedicated opt-in acceptance run. Never selected by ordinary CI/default E2E.
[TestFixture, NonParallelizable, Apartment(ApartmentState.STA), Category("Performance")]
public sealed class BulkPerformanceTests
{
    [Test, Explicit("Fixed-machine composited-pixel measurements; use Test-SheetDiagnostic.ps1 -BulkPerformance.")]
    public void CachedSheetPixelMeasurements()
    {
        string Required(string name) => Environment.GetEnvironmentVariable("GHPB_DIAGNOSTIC_" + name)
            ?? throw new InvalidOperationException("Missing diagnostic " + name);
        var executable = Required("APP"); var data = Required("DATA_ROOT"); var output = Required("OUTPUT");
        Assert.That(Directory.Exists(output), Is.False); Directory.CreateDirectory(output);
        using var seed = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "diagnostics", "editing-seed.json")));
        Assert.That(seed.RootElement.GetProperty("selectFieldCount").GetInt32(), Is.EqualTo(12));
        Assert.That(seed.RootElement.GetProperty("count").GetInt32(), Is.AnyOf(100, 1000));
        using var dpiScope = new DesktopDpiScope(); using var clipboard = new NativeClipboardScope();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.Environment["GHPB_DATA_ROOT"] = data; start.Environment["GH_CONFIG_DIR"] = Path.Combine(output, "empty-gh-config");
        var trace = Environment.GetEnvironmentVariable("GHPB_DIAGNOSTIC_TRACE");
        if (string.IsNullOrWhiteSpace(trace)) start.Environment.Remove("GHPB_SHEET_DIAGNOSTICS");
        else start.Environment["GHPB_SHEET_DIAGNOSTICS"] = trace;
        var diagnosticFrames = Environment.GetEnvironmentVariable("GHPB_DIAGNOSTIC_FRAMES") == "1";
        foreach (var secret in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN" }) start.Environment.Remove(secret);
        using var process = Process.Start(start)!; using var automation = new UIA3Automation(); using var app = Application.Attach(process.Id);
        var samples = new List<PixelSample>(); Window? window = null; bool normalExit = false;
        try
        {
            window = app.GetMainWindow(automation, TimeSpan.FromSeconds(30)) ?? throw new InvalidOperationException("The app window did not open."); WinUiProcess.AssertRuntime(process);
            var hwnd = window.Properties.NativeWindowHandle.Value;
            Keyboard.TypeVirtualKeyCode(0x12); window.SetForeground();
            Wait(() => GetForegroundWindow() == hwnd);
            // Set the client to 1280x720 logical units, independently of window chrome.
            var scale = GetDpiForWindow(hwnd) / 96d; var size = window.BoundingRectangle;
            GetClientRect(hwnd, out var client);
            window.Patterns.Transform.Pattern.Resize((int)Math.Round(1280 * scale) + size.Width - client.Right,
                (int)Math.Round(720 * scale) + size.Height - client.Bottom); window.Move(0, 0);
            WorkspaceUi.OpenProjectNavigation(window); WorkspaceUi.SelectCombo(window, "SavedProfiles", 0);
            WorkspaceUi.OpenProjectNavigation(window);
            AutomationElement? project = null;
            Wait(() => (project = WorkspaceUi.ProjectNavigation(window).FindFirstDescendant(cf => cf.ByName("P1").And(cf.ByControlType(ControlType.TreeItem)))) is not null);
            project!.Patterns.Invoke.Pattern.Invoke(); Wait(() => WorkspaceUi.HasVisibleElement(window, "GridCell0_1"));
            var handle = window.FindFirstDescendant(cf => cf.ByAutomationId("GridFillDown"));
            var candidate = handle is not null;
            GetClientRect(hwnd, out client);
            Write("conditions.json", new { executable, appHash = HashFile(Path.Combine(Path.GetDirectoryName(executable)!, "GhProjectsBoards.App.dll")),
                os = RuntimeInformation.OSDescription, scale, client = new { width = client.Right, height = client.Bottom }, window = window.BoundingRectangle,
                rows = seed.RootElement.GetProperty("count").GetInt32(), selectFields = 12, candidate,
                frequency = Stopwatch.Frequency, warmupPerAction = 5, measuredSelection = 50, measuredMenu = 50, measuredBulk = 10,
                exploratoryTrace = trace, diagnosticFrames,
                boundary = "SendInput call start to completed GDI readback of the first exact matching composited desktop ROI. Not a dispatcher/rendering callback or UIA wait. Physical monitor scanout is unmeasured. Raw capture start/end timestamps expose readback cost. The conservative sampling interval starts at the preceding nonmatching capture START, not its END. UIA validates the target outside the timed interval. Checkpoint time separately observes the atomic file through shared reads and includes observer parsing/polling cost. Optional trace/frame collection is exploratory, not a comparable acceptance run." });
            using (var before = Capture.Rectangle(window.BoundingRectangle)) before.ToFile(Path.Combine(output, "before.png"));
            var first = E("GridCell0_1"); var second = E("GridCell1_1");
            first.Focus(); Mouse.MoveTo(window.BoundingRectangle.Left + 50, window.BoundingRectangle.Top + 15);
            var selectionBounds = Rectangle.Union(first.BoundingRectangle, second.BoundingRectangle); selectionBounds.Inflate(2, 1);
            var selectedFirst = Stable(selectionBounds, "selection-first");
            Key(VirtualKeyShort.DOWN); Wait(() => E("GridCell1_1").Properties.HasKeyboardFocus.Value);
            var selectedSecond = Stable(selectionBounds, "selection-second", selectedFirst);
            Assert.That(selectedFirst, Is.Not.EqualTo(selectedSecond), "The pixel oracle must distinguish the two selected cells.");
            for (var i = -5; i < 50; i++)
            {
                var up = (i + 5) % 2 == 0;
                samples.Add(Measure("selection", i, selectionBounds, up ? selectedFirst : selectedSecond,
                    () => Key(up ? VirtualKeyShort.UP : VirtualKeyShort.DOWN)));
                Assert.That(E(up ? "GridCell0_1" : "GridCell1_1").Properties.HasKeyboardFocus.Value, Is.True);
            }
            Key(VirtualKeyShort.F4);
            Wait(() => window.FindFirstDescendant(cf => cf.ByAutomationId("ChoiceOption-done")) is not null);
            Key(VirtualKeyShort.ESCAPE);
            Wait(() => window.FindFirstDescendant(cf => cf.ByAutomationId("ChoiceOption-done")) is null);
            Key(VirtualKeyShort.F4);
            Wait(() => window.FindFirstDescendant(cf => cf.ByAutomationId("ChoiceOption-done")) is not null);
            var menuBounds = StableBounds(() => Rectangle.Union(E("ChoiceOption-todo").BoundingRectangle, E("ChoiceOption-done").BoundingRectangle));
            // Reopening the native popup changes antialiasing at the outer focus
            // frame by up to ten RGB levels. Measure every label/checkmark pixel
            // exactly, excluding only that three-pixel left/right frame perimeter.
            menuBounds.Inflate(-3, 0);
            Write("menu-bounds.json", menuBounds);
            var openMenu = Stable(menuBounds, "menu-open"); Key(VirtualKeyShort.ESCAPE);
            Wait(() => window.FindFirstDescendant(cf => cf.ByAutomationId("ChoiceOption-done")) is null);
            var closedMenu = Stable(menuBounds, "menu-closed"); Assert.That(openMenu, Is.Not.EqualTo(closedMenu));
            for (var i = -5; i < 50; i++)
            {
                Wait(() => E("GridCell0_1").Properties.HasKeyboardFocus.Value);
                samples.Add(Measure("menu", i, menuBounds, openMenu, () => Key(VirtualKeyShort.F4)));
                Key(VirtualKeyShort.ESCAPE); AwaitPixels(menuBounds, closedMenu);
            }
            // The same 100x1 paste is available on both main and the correction.
            // A separate copy-down measurement establishes the new bulk command.
            E("GridCell0_1").Focus(); NativeClipboardScope.WriteTestFormats(string.Join('\n', Enumerable.Repeat("Ready", 100)));
            var lastVisible = Enumerable.Range(0, 100).Select(i => window.FindFirstDescendant(cf => cf.ByAutomationId($"GridCell{i}_1")))
                .Where(e => e is not null && !e.Properties.IsOffscreen.Value).Last()!;
            var bulkBounds = Rectangle.Union(E("GridCell0_1").BoundingRectangle, lastVisible.BoundingRectangle); bulkBounds.Inflate(1, 0);
            Chord(VirtualKeyShort.KEY_V); Wait(() => Changes() == 100); var pasted = Stable(bulkBounds, "bulk-pasted");
            Chord(VirtualKeyShort.KEY_Z); Wait(() => Changes() == 0); var original = Stable(bulkBounds, "bulk-original");
            Assert.That(pasted, Is.Not.EqualTo(original));
            for (var i = -5; i < 10; i++)
            {
                samples.Add(Bulk("paste-100", i, bulkBounds, pasted, () => Chord(VirtualKeyShort.KEY_V)));
                Chord(VirtualKeyShort.KEY_Z); AwaitPixels(bulkBounds, original); Wait(() => Changes() == 0);
            }
            if (candidate)
            {
                Key(VirtualKeyShort.F4); Wait(() => window.FindFirstDescendant(cf => cf.ByAutomationId("ChoiceOption-done")) is not null);
                E("ChoiceOption-done").Patterns.Invoke.Pattern.Invoke(); Wait(() => Changes() == 1);
                Wait(() => E("GridCell0_1").Properties.HasKeyboardFocus.Value);
                using (Keyboard.Pressing(VirtualKeyShort.SHIFT))
                    for (var i = 1; i < 100; i++) { Key(VirtualKeyShort.DOWN); var count = i + 1; Wait(() => E("GridSelection").Name.Contains($"{count}行・{count}セル")); }
                Wait(() => E("GridSelection").Name.Contains("100行・100セル"));
                // Prepare the changed state before recording the pixel oracle.
                Chord(VirtualKeyShort.KEY_D); Wait(() => Changes() == 100);
                Wait(() => WorkspaceUi.ChoiceText(window, "GridCell99_1") == "Ready");
                Wait(() => E("GridCell99_1").Properties.HasKeyboardFocus.Value);
                // The viewport and column stay fixed while row containers move.
                // Reuse the established column region, excluding its bottom
                // quarter where the active-row focus frame and auto-hiding
                // horizontal scrollbar can change independently of the values.
                // This observes multiple visible destinations; the checkpoint
                // observer independently verifies all 100 changes.
                var fillBounds = bulkBounds; fillBounds.Height = (int)(fillBounds.Height * .75);
                Write("copy-down-bounds.json", fillBounds);
                Assert.That(fillBounds.Width, Is.InRange(100, 300)); Assert.That(fillBounds.Height, Is.InRange(200, 700));
                var filled = Stable(fillBounds, "copy-down-filled");
                // Keyboard Undo keeps the existing native focus and scroll
                // position. Repeated UIA SetFocus can request a new viewport.
                Chord(VirtualKeyShort.KEY_Z); Wait(() => Changes() == 1);
                Wait(() => WorkspaceUi.ChoiceText(window, "GridCell99_1") == "Backlog");
                var unfilled = Stable(fillBounds, "copy-down-unfilled", filled);
                Assert.That(filled, Is.Not.EqualTo(unfilled));
                for (var i = -5; i < 10; i++)
                {
                    samples.Add(Bulk("copy-down-100", i, fillBounds, filled, () => Chord(VirtualKeyShort.KEY_D)));
                    Chord(VirtualKeyShort.KEY_Z); Wait(() => Changes() == 1); AwaitPixels(fillBounds, unfilled);
                }
            }
            using (var after = Capture.Rectangle(window.BoundingRectangle)) after.ToFile(Path.Combine(output, "after.png"));
            Write("summary.json", samples.Where(s => s.Index >= 0).GroupBy(s => s.Action).Select(g => new {
                action = g.Key, count = g.Count(), p95Milliseconds = g.OrderBy(s => s.Milliseconds).ElementAt((int)Math.Ceiling(g.Count() * .95) - 1).Milliseconds,
                maximumMilliseconds = g.Max(s => s.Milliseconds), targetMilliseconds = g.Key is "selection" or "menu" ? 100 : 500,
                passes = g.Key is "selection" or "menu" ? g.OrderBy(s => s.Milliseconds).ElementAt((int)Math.Ceiling(g.Count() * .95) - 1).Milliseconds <= 100 : g.All(s => s.Milliseconds <= 500)
            }).ToArray());
            window.Close(); Wait(() => process.HasExited); Assert.That(process.ExitCode, Is.Zero); normalExit = true;

            AutomationElement E(string id) => WorkspaceUi.Element(window, id);
            int Changes()
            {
                var folder = Path.Combine(data, "Drafts");
                var file = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").SingleOrDefault() : null;
                if (file is null) return -1;
                try { using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var doc = JsonDocument.Parse(stream);
                    return doc.RootElement.GetProperty("Fields").EnumerateArray().Count(f => f.GetProperty("Change").ValueKind != JsonValueKind.Null); }
                catch (IOException) { return -1; } // Atomic replacement/sharing races are observer retries, not zero changes.
            }
            PixelSample Bulk(string action, int index, Rectangle bounds, string expected, Action input)
            {
                using var begin = new ManualResetEventSlim();
                var observed = Task.Run(() => { begin.Wait(); var limit = Stopwatch.StartNew();
                    while (limit.Elapsed < TimeSpan.FromSeconds(30)) { if (Changes() == 100) return Stopwatch.GetTimestamp(); Thread.Sleep(5); }
                    throw new TimeoutException("The expected durable 100-cell checkpoint was not observed."); });
                var sample = Measure(action, index, bounds, expected, () => { begin.Set(); input(); });
                return sample with { DurableMilliseconds = (observed.GetAwaiter().GetResult() - sample.InputStart) * 1000d / Stopwatch.Frequency };
            }
        }
        finally
        {
            Write("samples.json", samples);
            Write("lifetime.json", new { pid = process.Id, normalExit, exited = process.HasExited, exitCode = process.HasExited ? process.ExitCode : (int?)null });
            if (!process.HasExited) { if (window is not null) { using var image = Capture.Rectangle(window.BoundingRectangle); image.ToFile(Path.Combine(output, "failed.png")); } process.Kill(true); process.WaitForExit(5000); }
        }

        void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        Rectangle StableBounds(Func<Rectangle> read)
        {
            var timer = Stopwatch.StartNew(); var previous = Rectangle.Empty; var unchangedSince = Stopwatch.GetTimestamp();
            while (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                var next = read(); if (next != previous) unchangedSince = Stopwatch.GetTimestamp(); previous = next;
                if (Stopwatch.GetElapsedTime(unchangedSince).TotalMilliseconds >= 200) return next;
                Thread.Sleep(10);
            }
            throw new TimeoutException("The popup geometry did not settle before measurement.");
        }
        string Stable(Rectangle bounds, string name, string? differentFrom = null)
        {
            // Oracle preparation is outside every measured input interval. A
            // short plateau can still contain the pre-input frame. Require an
            // unchanged sequence beyond the observed presentation delay,
            // distinct states, and retain the oracle PNGs.
            var timer = Stopwatch.StartNew(); string? previous = null; var unchangedSince = Stopwatch.GetTimestamp();
            while (timer.Elapsed < TimeSpan.FromSeconds(10)) { using var image = Capture.Rectangle(bounds); var hash = Pixels(image.Bitmap);
                if (hash != previous || hash == differentFrom) unchangedSince = Stopwatch.GetTimestamp(); previous = hash;
                if (Stopwatch.GetElapsedTime(unchangedSince).TotalMilliseconds >= 500) { image.ToFile(Path.Combine(output, name + ".png")); return hash; } }
            throw new TimeoutException("Stable composited ROI was not observed: " + name);
        }
        PixelSample Measure(string action, int index, Rectangle bounds, string expected, Action input)
        {
            var startTicks = Stopwatch.GetTimestamp(); input(); var sent = Stopwatch.GetTimestamp();
            var captures = new List<Frame>(); long previous = startTicks;
            var frames = new List<Bitmap>();
            try
            {
            while (Stopwatch.GetElapsedTime(startTicks) < TimeSpan.FromSeconds(10))
            {
                var captureStart = Stopwatch.GetTimestamp(); using var image = Capture.Rectangle(bounds); var end = Stopwatch.GetTimestamp();
                var match = Pixels(image.Bitmap) == expected; captures.Add(new(captureStart, end, match));
                if (diagnosticFrames && index == 0 && action is "selection" or "menu") frames.Add(new Bitmap(image.Bitmap));
                if (match) { if (index is 0 or 49 or 9) image.ToFile(Path.Combine(output, action + "-" + index + ".png"));
                    return new(action, index, startTicks, sent, end, (end - startTicks) * 1000d / Stopwatch.Frequency,
                        (previous - startTicks) * 1000d / Stopwatch.Frequency, bounds, captures.ToArray(), null); }
                previous = end;
            }
            Write(action + "-" + index + "-failed-captures.json", captures);
            Write(action + "-" + index + "-failed-bounds.json", bounds);
            using (var failed = Capture.Rectangle(bounds)) failed.ToFile(Path.Combine(output, action + "-" + index + "-failed.png"));
            throw new TimeoutException("Matching composited ROI not observed: " + action);
            }
            finally
            {
                for (var i = 0; i < frames.Count; i++) { frames[i].Save(Path.Combine(output, $"{action}-{index}-frame-{i:000}.png"), ImageFormat.Png); frames[i].Dispose(); }
            }
        }
        void AwaitPixels(Rectangle bounds, string hash) => Measure("reset", -1, bounds, hash, () => { });
    }
    private sealed record Frame(long Start, long End, bool Match);
    private sealed record PixelSample(string Action, int Index, long InputStart, long InputSent, long MatchReadback, double Milliseconds,
        double LastNonmatchingMilliseconds, Rectangle Bounds, Frame[] Captures, double? DurableMilliseconds);
    private static string Pixels(Bitmap bitmap)
    {
        var pixels = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { var bytes = new byte[Math.Abs(pixels.Stride) * pixels.Height]; Marshal.Copy(pixels.Scan0, bytes, 0, bytes.Length); return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { bitmap.UnlockBits(pixels); }
    }
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Key(VirtualKeyShort key)
    {
        // The extended scan codes select the dedicated arrow cluster. Nonextended
        // VK_DOWN can be interpreted as a numpad key and alter Shift under NumLock.
        if (key == VirtualKeyShort.DOWN) Keyboard.TypeScanCode(0x50, true);
        else if (key == VirtualKeyShort.UP) Keyboard.TypeScanCode(0x48, true);
        else Keyboard.Type(key);
    }
    private static void Chord(VirtualKeyShort key) { using (Keyboard.Pressing(VirtualKeyShort.CONTROL)) Key(key); }
    private static void Wait(Func<bool> condition) => Assert.That(Retry.WhileFalse(condition, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20), ignoreException: true).Result, Is.True);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rectangle);
}
