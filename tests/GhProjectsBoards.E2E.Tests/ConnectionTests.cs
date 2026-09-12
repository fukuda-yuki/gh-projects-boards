using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
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
            using var clipboard = new NativeClipboardScope();
            Button(window, "CopyLoginButton").Invoke();
            WaitFor(() => NativeClipboardScope.ReadText()?.Contains("auth login --web", StringComparison.Ordinal) == true);
            Assert.That(NativeClipboardScope.ReadText(), Does.Contain("--hostname 'example.test'"));
        });
    }

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
        }
        catch
        {
            if (window is not null && !process.HasExited) CaptureWindow(window, "connection-failure");
            throw;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
        }
    }

    private static AutomationElement Element(Window window, string id)
        => window.FindFirstDescendant(cf => cf.ByAutomationId(id)) ?? throw new AssertionException($"Missing control: {id}");
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
