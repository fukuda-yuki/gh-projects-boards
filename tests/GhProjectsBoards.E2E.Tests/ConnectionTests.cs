using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using NUnit.Framework;
using Application = FlaUI.Core.Application;
using Window = FlaUI.Core.AutomationElements.Window;

namespace GhProjectsBoards.E2E.Tests;

[TestFixture]
[Category("E2E")]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class ConnectionTests
{
    [Test]
    public void DiagnosesTargetsAndRequiresExplicitAccountRebinding()
    {
        WithApplication((window, process, fixture) =>
        {
            SetText(window, "IssueUrlInput", "https://example.test/example/sandbox/issues/1");
            SetText(window, "ProjectUrlInput", "https://example.test/users/example/projects/3");
            Assert.That(Element(window, "ProjectUrlInput").AsTextBox().Text,
                Is.EqualTo("https://example.test/users/example/projects/3"));
            Button(window, "CheckConnectionButton").Invoke();
            WaitFor(() => Text(window, "ConnectionStatus").Contains("接続を確認しました", StringComparison.Ordinal));
            Assert.That(Text(window, "AccountValue"), Does.Contain("fixture-user").And.Contain("42").And.Contain("example.test"));
            Assert.That(Text(window, "IssueResult"), Does.Contain("読み取り：あり").And.Contain("更新権限：あり"));
            Assert.That(Text(window, "ProjectResult"), Does.Contain("読み取り：あり").And.Contain("更新権限：あり"));
            CaptureWindow(window, "connected");

            fixture.Write(id: 99);
            Button(window, "CheckConnectionButton").Invoke();
            WaitFor(() => Text(window, "ConnectionStatus").Contains("変更を検出", StringComparison.Ordinal));
            Assert.That(Text(window, "IssueResult"), Does.Contain("読み取り：不明"));
            Button(window, "NewConnectionButton").Invoke();
            WaitFor(() => Text(window, "ConnectionStatus").Contains("接続を確認しました", StringComparison.Ordinal)
                && Text(window, "AccountValue").Contains("99", StringComparison.Ordinal));
            Assert.That(fixture.Calls().Any(call => call.GetProperty("mutation").GetBoolean()), Is.False);

            var help = Element(window, "LoginHelpExpander");
            help.Patterns.ExpandCollapse.Pattern.Expand();
            var previousText = NativeClipboardScope.ReadText();
            using (var userClipboard = new NativeClipboardScope())
            {
                NativeClipboardScope.WriteTestFormats();
                using (var syntheticClipboard = new NativeClipboardScope())
                {
                    Button(window, "CopyLoginButton").Invoke();
                    WaitFor(() => NativeClipboardScope.ReadText()?.Contains("auth login --web", StringComparison.Ordinal) == true);
                    Assert.That(NativeClipboardScope.ReadText(), Does.Contain("--hostname 'example.test'"));
                    Button(window, "CopyRefreshButton").Invoke();
                    WaitFor(() => NativeClipboardScope.ReadText()?.Contains("auth refresh", StringComparison.Ordinal) == true);
                    Assert.That(NativeClipboardScope.ReadText(), Does.Contain("--hostname 'example.test'"));
                }
                Assert.That(NativeClipboardScope.ReadText(), Is.EqualTo("Clipboard regression — 日本語"));
                Assert.That(NativeClipboardScope.ReadTestFormat(), Is.EqualTo("Synthetic custom-format payload"));
            }
            // Compare privately: an assertion failure must never print prior clipboard text.
            Assert.That(NativeClipboardScope.ReadText() == previousText, Is.True, "Original clipboard text was not restored.");
        });
    }

    [TestCase("HostInput", false)]
    [TestCase("HostInput", true)]
    [TestCase("LoginCommand", false)]
    [TestCase("LoginCommand", true)]
    public void CloseWithTextBoxFocusedExitsNormally(string input, bool altF4)
    {
        WithApplication((window, process, fixture) =>
        {
            if (input == "LoginCommand")
            {
                Element(window, "LoginHelpExpander").Patterns.ExpandCollapse.Pattern.Expand();
                WaitFor(() => window.FindFirstDescendant(cf => cf.ByAutomationId(input)) is not null);
            }
            var editor = Element(window, input);
            Foreground(window);
            editor.Focus();
            WaitFor(() => editor.Properties.HasKeyboardFocus.Value);
            if (input == "HostInput")
            {
                Keyboard.Type(VirtualKeyShort.KEY_A);
                WaitFor(() => editor.AsTextBox().Text != "example.test");
                Assert.That(editor.Properties.HasKeyboardFocus.Value, Is.True);
            }
            CaptureWindow(window, "focused-before-close");
            CloseFromChrome(window, altF4);
            WaitFor(() => process.HasExited);
            Assert.That(process.ExitCode, Is.Zero, "Ordinary close with a focused TextBox must not crash.");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ChromeCloseDuringGhStopsOwnedProcess(bool altF4)
    {
        WithApplication((window, process, fixture) =>
        {
            fixture.Write(delayMs: 10000);
            Button(window, "CheckConnectionButton").Invoke();
            WaitFor(() => fixture.LastUserProcessId() > 0);
            var child = fixture.LastUserProcessId();
            CloseFromChrome(window, altF4);
            WaitFor(() => process.HasExited);
            Assert.That(process.ExitCode, Is.Zero);
            WaitFor(() => !Running(child));
        });
    }

    [Test]
    public void NativePickerSelectsExecutableAndCancelPreservesIt()
    {
        WithApplication((window, process, fixture) =>
        {
            var fake = Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!;
            SetText(window, "ExecutablePath", Path.Combine(fixture.Directory, "old.exe"));
            Button(window, "BrowseGhButton").Invoke();
            var picker = WaitForPicker(window);
            CaptureWindow(picker, "native-picker");
            var fileName = picker.FindFirstDescendant(cf => cf.ByAutomationId("1148").And(cf.ByClassName("Edit")))!.AsTextBox();
            fileName.Text = fake;
            Assert.That(fileName.Text, Is.EqualTo(fake));
            picker.FindFirstDescendant(cf => cf.ByAutomationId("1").And(cf.ByClassName("Button")))!.AsButton().Invoke();
            WaitFor(() => Element(window, "ExecutablePath").AsTextBox().Text == fake && Button(window, "BrowseGhButton").IsEnabled);
            Button(window, "BrowseGhButton").Invoke();
            picker = WaitForPicker(window);
            picker.FindFirstDescendant(cf => cf.ByAutomationId("2").And(cf.ByClassName("Button")))!.AsButton().Invoke();
            WaitFor(() => Button(window, "BrowseGhButton").IsEnabled);
            Assert.That(Element(window, "ExecutablePath").AsTextBox().Text, Is.EqualTo(fake));
            Button(window, "CheckConnectionButton").Invoke();
            WaitFor(() => Text(window, "ConnectionStatus").Contains("接続を確認しました", StringComparison.Ordinal));
        });
    }

    private static Window WaitForPicker(Window window)
    {
        Window? picker = null;
        WaitFor(() => (picker = window.FindFirstDescendant(cf => cf.ByClassName("#32770"))?.AsWindow()) is not null);
        return picker!;
    }

    private static void CloseFromChrome(Window window, bool altF4)
    {
        if (altF4)
        {
            Foreground(window);
            Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.F4);
        }
        else
        {
            var close = window.FindFirstDescendant(cf => cf.ByAutomationId("Close"));
            Assert.That(close, Is.Not.Null, "The native title-bar Close button must be present.");
            close!.Click();
        }
    }

    private static void Foreground(Window window)
    {
        window.SetForeground();
        WaitFor(() => GetForegroundWindow() == window.Properties.NativeWindowHandle.Value);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    [Test]
    public void CancelAndWindowCloseStopTheOwnedGhProcess()
    {
        WithApplication((window, process, fixture) =>
        {
            fixture.Write(delayMs: 5000);
            Button(window, "CheckConnectionButton").Invoke();
            WaitFor(() => fixture.LastUserProcessId() > 0);
            var firstChild = fixture.LastUserProcessId();
            Assert.That(Button(window, "CancelConnectionButton").IsEnabled, Is.True);
            Button(window, "CancelConnectionButton").Invoke();
            WaitFor(() => Text(window, "ConnectionStatus").Contains("キャンセル", StringComparison.Ordinal));
            WaitFor(() => !Running(firstChild));
            Assert.That(Button(window, "CheckConnectionButton").IsEnabled, Is.True);

            Button(window, "CheckConnectionButton").Invoke();
            WaitFor(() => fixture.LastUserProcessId() != firstChild);
            var secondChild = fixture.LastUserProcessId();
            window.Close();
            WaitFor(() => process.HasExited);
            Assert.That(process.ExitCode, Is.Zero);
            WaitFor(() => !Running(secondChild));
        });
    }

    [Test]
    public void MissingGhAndMissingLoginAreActionableInTheOrdinaryScreen()
    {
        WithApplication((window, process, fixture) =>
        {
            var fake = Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!;
            SetText(window, "ExecutablePath", Path.Combine(fixture.Directory, "missing-gh.exe"));
            Button(window, "CheckConnectionButton").Invoke();
            WaitFor(() => Text(window, "ConnectionStatus").Contains("gh.exeが見つかりません", StringComparison.Ordinal));
            fixture.Write(state: "notLoggedIn");
            SetText(window, "ExecutablePath", fake);
            Button(window, "CheckConnectionButton").Invoke();
            WaitFor(() => Text(window, "ConnectionStatus").Contains("未ログイン", StringComparison.Ordinal));
            Assert.That(Text(window, "AccountValue"), Is.EqualTo("不明"));
            Assert.That(Text(window, "IssueResult"), Does.Contain("読み取り：不明"));
        });
    }

    private static void WithApplication(Action<Window, Process, Fixture> journey)
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_E2E") != "1") Assert.Ignore("Run scripts/Test-E2E.ps1 on an interactive desktop.");
        Assert.That(Environment.UserInteractive, Is.True, "An interactive desktop is required.");
        using var dpi = new DesktopDpiScope();
        var executable = Environment.GetEnvironmentVariable("GHPB_E2E_APP_PATH")!;
        var fake = Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!;
        Assert.That(File.Exists(executable) && File.Exists(fake), Is.True, "Build the ordinary app and fake gh test executable.");
        var fixture = new Fixture();
        fixture.Write();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.Environment["GH_CONFIG_DIR"] = fixture.Directory;
        start.Environment["GH_TOKEN"] = "synthetic-ignored-token";
        using var process = Process.Start(start)!;
        using var automation = new UIA3Automation();
        using var application = Application.Attach(process.Id);
        Window? window = null;
        try
        {
            window = application.GetMainWindow(automation, TimeSpan.FromSeconds(20));
            Assert.That(window, Is.Not.Null);
            WinUiProcess.AssertRuntime(process);
            SetText(window!, "ExecutablePath", fake);
            SetText(window!, "HostInput", "example.test");
            journey(window!, process, fixture);
            if (!process.HasExited) window!.Close();
            WaitFor(() => process.HasExited);
            Assert.That(process.ExitCode, Is.Zero);
            Assert.That(fixture.Calls().Select(call => call.GetProperty("pid").GetInt32()).Distinct().Any(Running), Is.False,
                "No recorded owned gh process may remain after a successful journey.");
        }
        catch
        {
            if (window is not null && !process.HasExited) CaptureWindow(window, "connection-failure");
            throw;
        }
        finally
        {
            var exitedBeforeCleanup = process.HasExited;
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
            var childrenBeforeCleanup = fixture.Calls().Select(call => call.GetProperty("pid").GetInt32()).Distinct().Where(Running).ToArray();
            foreach (var childId in childrenBeforeCleanup)
            {
                try
                {
                    using var child = Process.GetProcessById(childId);
                    if (child.StartTime >= process.StartTime && string.Equals(child.MainModule?.FileName, fake, StringComparison.OrdinalIgnoreCase))
                    {
                        child.Kill(entireProcessTree: true);
                        child.WaitForExit(5000);
                    }
                }
                catch (ArgumentException) { } // The recorded child already exited.
            }
            try
            {
                File.WriteAllText(Path.Combine(fixture.Directory, "lifetime.json"), JsonSerializer.Serialize(new
                {
                    test = TestContext.CurrentContext.Test.FullName, appPid = process.Id,
                    exitedBeforeCleanup, appExitCode = process.HasExited ? (int?)process.ExitCode : null,
                    childrenBeforeCleanup, remainingRecordedChildren = childrenBeforeCleanup.Where(Running).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (IOException) { TestContext.Progress.WriteLine("Lifetime artifact could not be written."); }
        }
    }

    private static AutomationElement Element(Window window, string id)
        => Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByAutomationId(id)),
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100)).Result
            ?? throw new AssertionException($"Missing control: {id}");
    private static Button Button(Window window, string id) => Element(window, id).AsButton();
    private static string Text(Window window, string id) => Element(window, id).Name;
    private static void SetText(Window window, string id, string value) => Element(window, id).AsTextBox().Text = value;
    private static void WaitFor(Func<bool> predicate)
        => Assert.That(Retry.WhileFalse(predicate, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(100)).Result, Is.True, "The UI did not reach the expected state.");
    private static bool Running(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static void CaptureWindow(Window window, string name)
    {
        try
        {
            var path = Path.Combine(Environment.GetEnvironmentVariable("GHPB_E2E_ARTIFACTS")!, $"{name}-{Guid.NewGuid():N}.png");
            using var capture = Capture.Element(window);
            capture.ToFile(path);
            TestContext.AddTestAttachment(path, "Ordinary application with synthetic data");
        }
        catch (Exception exception) { TestContext.Progress.WriteLine($"Capture unavailable: {exception.GetType().Name}"); }
    }

    private sealed class Fixture
    {
        public string Directory { get; } = Path.Combine(Environment.GetEnvironmentVariable("GHPB_E2E_ARTIFACTS")!, "fixtures", Guid.NewGuid().ToString("N"));
        public void Write(int id = 42, int delayMs = 0, string state = "success")
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, "scenario.json"), JsonSerializer.Serialize(new { id, delayMs, state }));
        }
        public JsonElement[] Calls()
        {
            try
            {
                var path = Path.Combine(Directory, "calls.jsonl");
                return !File.Exists(path) ? [] : File.ReadAllLines(path).Select(line =>
                {
                    using var json = JsonDocument.Parse(line);
                    return json.RootElement.Clone();
                }).ToArray();
            }
            catch (IOException) { return []; }
        }
        public int LastUserProcessId() => Calls().Where(call => call.GetProperty("endpoint").GetString() == "user")
            .Select(call => call.GetProperty("pid").GetInt32()).LastOrDefault();
    }
}
