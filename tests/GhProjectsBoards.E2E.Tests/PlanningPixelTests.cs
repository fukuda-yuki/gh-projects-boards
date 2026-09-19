using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [Test, Category("Performance")]
    public void ThousandTaskPlanningCommandIsReadBackFromCompositedPixels()
    {
        var seed = Environment.GetEnvironmentVariable("GHPB_PLANNING_PIXEL_SEED");
        if (string.IsNullOrWhiteSpace(seed)) { Assert.Ignore("Supply an isolated 1,000-task planning checkpoint from the scoped planning workload."); return; }
        using var clipboard = new NativeClipboardScope(); using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.Data, "Drafts"));
        foreach (var file in Directory.GetFiles(Path.Combine(seed, "Drafts"), "*.json")) File.Copy(file, Path.Combine(f.Data, "Drafts", Path.GetFileName(file)));
        var samples = new List<object>(); var elapsed = new List<double>();
        f.Run(w => {
            w.Patterns.Transform.Pattern.Resize(1600, 900); w.Move(0, 0);
            OpenSaved(w, "P1", profile: true);
            WorkspaceUi.CloseProjectNavigation(w);
            Wait(() => CellText(w, 0, 2) == "16"); Element(w, "GridCell0_2").Focus();
            Wait(() => Element(w, "GridCell0_2").Properties.HasKeyboardFocus.Value);
            var bounds = Element(w, "GridCell0_6").BoundingRectangle;
            Assert.That(Element(w, "GridCell0_6").Properties.IsOffscreen.Value, Is.False);
            void Paste(string value) { NativeClipboardScope.WriteTestFormats(value); using (Keyboard.Pressing(VirtualKeyShort.CONTROL)) Key(VirtualKeyShort.KEY_V); }
            Paste("24"); Wait(() => CellText(w, 0, 6) == "2026-10-07"); var changed = Stable("24");
            Paste("16"); Wait(() => CellText(w, 0, 6) == "2026-10-06"); var original = Stable("16");
            Assert.That(changed, Is.Not.EqualTo(original));
            Capture(w, f.Root, "planning-1000-before");
            for (var sample = -2; sample < 10; sample++)
            {
                var value = sample % 2 == 0 ? "24" : "16"; var expected = value == "24" ? changed : original;
                NativeClipboardScope.WriteTestFormats(value);
                var frames = new List<object>(); var start = Stopwatch.GetTimestamp(); long previousCaptureStart = start; bool matched = false;
                using (Keyboard.Pressing(VirtualKeyShort.CONTROL)) Key(VirtualKeyShort.KEY_V);
                var sent = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(10))
                {
                    var begin = Stopwatch.GetTimestamp(); using var capture = FlaUI.Core.Capturing.Capture.Rectangle(bounds);
                    var end = Stopwatch.GetTimestamp(); var match = PixelHash(capture.Bitmap) == expected; frames.Add(new { begin, end, match });
                    if (match)
                    {
                        var milliseconds = (end - start) * 1000d / Stopwatch.Frequency;
                        samples.Add(new { sample, value, start, sent, end, lastNonmatchingStart = previousCaptureStart, milliseconds, frames });
                        if (sample >= 0) elapsed.Add(milliseconds);
                        if (sample is 0 or 9) capture.ToFile(Path.Combine(f.Root, "planning-pixels-" + sample + ".png"));
                        matched = true; break;
                    }
                    previousCaptureStart = begin;
                }
                File.WriteAllText(Path.Combine(f.Root, "planning-pixel-samples.json"), JsonSerializer.Serialize(samples));
                Assert.That(matched, Is.True, "Expected DATE pixels were not read back.");
                Wait(() => CellText(w, 0, 6) == (value == "24" ? "2026-10-07" : "2026-10-06"));
                Wait(() => Text(w, "DraftStatus").Contains("ローカル保存済み"));
            }
            Capture(w, f.Root, "planning-1000-after");
            Assert.That(f.Calls(), Is.Empty, "Offline calculations cannot request GitHub access.");
            File.WriteAllText(Path.Combine(f.Root, "planning-pixel-summary.json"), JsonSerializer.Serialize(new {
                seed, checkpointSha256 = Directory.GetFiles(Path.Combine(seed, "Drafts"), "*.json").Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray(),
                rows = 1000, people = 20, warmups = 2, samples = 10, window = w.BoundingRectangle, bounds,
                frequency = Stopwatch.Frequency, source = Environment.GetEnvironmentVariable("GHPB_PLANNING_SOURCE"),
                boundary = "SendInput Ctrl+V start through first exact matching composited GDI DATE-cell readback; physical scanout unmeasured. Clipboard preparation and durable-save wait outside interval.",
                p95Ms = elapsed.Order().Last(), maximumMs = elapsed.Max(), passed = elapsed.All(t => t <= 1000)
            }, new JsonSerializerOptions { WriteIndented = true }));
            string Stable(string name)
            {
                var timer = Stopwatch.StartNew(); string? previous = null; var since = Stopwatch.GetTimestamp();
                while (timer.Elapsed < TimeSpan.FromSeconds(10))
                {
                    using var capture = FlaUI.Core.Capturing.Capture.Rectangle(bounds); var hash = PixelHash(capture.Bitmap);
                    if (hash != previous) since = Stopwatch.GetTimestamp(); previous = hash;
                    if (Stopwatch.GetElapsedTime(since).TotalMilliseconds >= 500)
                    { capture.ToFile(Path.Combine(f.Root, "planning-oracle-" + name + ".png")); return hash; }
                }
                throw new TimeoutException("The planning DATE pixel oracle did not settle.");
            }
        });
        Assert.That(elapsed, Has.Count.EqualTo(10));
        Assert.That(elapsed.Max(), Is.LessThanOrEqualTo(1000));
        TestContext.AddTestAttachment(Path.Combine(f.Root, "planning-pixel-summary.json"));
    }
    private static string PixelHash(Bitmap bitmap)
    {
        var pixels = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { var bytes = new byte[Math.Abs(pixels.Stride) * pixels.Height]; Marshal.Copy(pixels.Scan0, bytes, 0, bytes.Length); return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { bitmap.UnlockBits(pixels); }
    }
}
