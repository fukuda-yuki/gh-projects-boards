using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using NUnit.Framework;
using Application = FlaUI.Core.Application;

namespace GhProjectsBoards.E2E.Tests;

[TestFixture, Explicit("Retained grid routes require adaptation to the Phase 6 sheet."), Category("RetainedGrid"), NonParallelizable, Apartment(ApartmentState.STA)]
public sealed partial class RegistrationTests
{
    private static void AddUrl(Window w, int number)
    {
        Invoke(w, "AddProjectButton"); Set(w, "RegistrationUrl", $"https://example.test/users/sample-user/projects/{number}");
        Invoke(w, "ResolveProjectButton"); Wait(() => Element(w, "RegisterProjectButton").IsEnabled);
    }
    private static void Connect(Window w)
    {
        Set(w, "ExecutablePath", Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!);
        Set(w, "HostInput", "example.test"); Invoke(w, "CheckConnectionButton");
        Wait(() => Text(w, "ConnectionStatus").Contains("接続を確認しました"));
    }
    private static AutomationElement Element(Window w, string id) => WorkspaceUi.Element(w, id);
    private static string Text(Window w, string id) => id == "RegistrationStatus" ? WorkspaceUi.RegistrationStatusText(w) : Element(w, id).Name;
    private static void Invoke(Window w, string id) => WorkspaceUi.Invoke(w, id);
    private static void Set(Window w, string id, string value) => Element(w, id).AsTextBox().Text = value;
    private static void Wait(Func<bool> condition) => Assert.That(Retry.WhileFalse(condition, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(100)).Result, Is.True);
    private static void Capture(Window w, string root, string name) { Thread.Sleep(350); using var capture = FlaUI.Core.Capturing.Capture.Element(w); capture.ToFile(Path.Combine(root, name + ".png")); }
    private sealed class Fixture : IDisposable
    {
        private readonly DesktopDpiScope dpi = new();
        public string Root { get; }
        public string Data => Path.Combine(Root, "data");
        public Fixture()
        {
            if (Environment.GetEnvironmentVariable("GHPB_RUN_E2E") != "1") Assert.Ignore("Run Test-E2E.ps1.");
            Root = Path.Combine(Environment.GetEnvironmentVariable("GHPB_E2E_ARTIFACTS")!, "registration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root); Write();
        }
        public void Write(int delay = 0, long id = 42, string? remoteTitle = null, string? remoteOption = null, bool partial = false)
            => File.WriteAllText(Path.Combine(Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, readDelayMs = delay, id, remoteTitle, remoteOption, partial }));
        public JsonElement[] Calls() => File.Exists(Path.Combine(Root, "calls.jsonl")) ? File.ReadAllLines(Path.Combine(Root, "calls.jsonl")).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray() : [];
        public void Run(Action<Window> action, bool alreadyClosed = false, bool interrupt = false)
        {
            var exe = Environment.GetEnvironmentVariable("GHPB_E2E_APP_PATH")!;
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            start.Environment["GH_CONFIG_DIR"] = Root; start.Environment["GHPB_DATA_ROOT"] = Data;
            using var process = Process.Start(start)!; using var automation = new UIA3Automation(); using var app = Application.Attach(process.Id);
            Window? window = null;
            try
            {
                window = app.GetMainWindow(automation, TimeSpan.FromSeconds(20)); Assert.That(window, Is.Not.Null); WinUiProcess.AssertRuntime(process);
                FlaUI.Core.Input.Keyboard.TypeVirtualKeyCode(0x12);
                window!.SetForeground();
                Wait(() => GetForegroundWindow() == window.Properties.NativeWindowHandle.Value);
                action(window!);
                if (interrupt) process.Kill(true); else if (!alreadyClosed) window!.Close();
                Wait(() => process.HasExited);
                if (!interrupt) Assert.That(process.ExitCode, Is.Zero);
                Assert.That(Calls().Select(c => c.GetProperty("pid").GetInt32()).Distinct().Any(Running), Is.False);
            }
            catch { if (window is not null && !process.HasExited) Capture(window, Root, "failure"); throw; }
            finally
            {
                var normal = process.HasExited && !interrupt;
                if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
                File.WriteAllText(Path.Combine(Root, "lifetime-" + process.Id + ".json"), JsonSerializer.Serialize(new { normal, deliberateInterruption = interrupt, code = process.ExitCode, pid = process.Id }));
            }
        }
        private static bool Running(int pid)
        {
            // Recorded short-lived gh PIDs can be reused by unrelated/protected processes on restart.
            var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!));
            try { return candidates.Any(p => p.Id == pid && !p.HasExited); }
            finally { foreach (var p in candidates) p.Dispose(); }
        }
        public void Dispose() => dpi.Dispose();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
