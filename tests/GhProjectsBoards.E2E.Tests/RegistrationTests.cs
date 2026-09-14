using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using NUnit.Framework;
using Application = FlaUI.Core.Application;

namespace GhProjectsBoards.E2E.Tests;

[TestFixture, Category("E2E"), NonParallelizable, Apartment(ApartmentState.STA)]
public sealed partial class RegistrationTests
{
    [Test]
    public void ChangingConnectionInputsClearsPrivateDiscoveryAndDisablesReads()
    {
        using var f = new Fixture();
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); AddUrl(w, 1);
            Assert.That(Text(w, "ProjectConfirmation"), Does.Contain("Project 1"));
            Invoke(w, "ConnectionPageButton"); Set(w, "HostInput", "other.test");
            Invoke(w, "ProjectsPageButton");
            Assert.That(Element(w, "AddProjectButton").IsEnabled, Is.False);
            Assert.That(Text(w, "WorkspaceIdentity"), Does.Contain("未選択"));
            Invoke(w, "ConnectionPageButton");
            f.Write(id: 99); Invoke(w, "NewConnectionButton");
            Wait(() => Text(w, "AccountValue").Contains("99"));
            Invoke(w, "ProjectsPageButton"); Invoke(w, "AddProjectButton");
            Assert.That(Element(w, "ProjectCandidates").AsListBox().Items, Is.Empty);
            Assert.That(Element(w, "ProjectConfirmation").Properties.Name.ValueOrDefault, Is.Null.Or.Empty);
            Assert.That(Element(w, "RegistrationUrl").AsTextBox().Text, Is.Empty);
        });
    }

    [Test]
    public void RegisterTwoProjectsRestartRestoreAndUnregisterLocally()
    {
        using var f = new Fixture();
        f.Run(w =>
        {
            Connect(w);
            Invoke(w, "ProjectsPageButton"); Invoke(w, "AddProjectButton");
            Set(w, "DiscoveryOwner", "sample-user"); Invoke(w, "LoadRepositoriesButton");
            Wait(() => Element(w, "DiscoveryRepositories").AsComboBox().Items.Length == 3);
            Element(w, "DiscoveryRepositories").AsComboBox().Select(1);
            Invoke(w, "SearchProjectsButton");
            Wait(() => Element(w, "ProjectCandidates").AsListBox().Items.Length == 1);
            Element(w, "ProjectCandidates").AsListBox().Select(0);
            Wait(() => Element(w, "RegisterProjectButton").IsEnabled);
            Set(w, "InitialDefaultRepository", "sample-user/first"); Invoke(w, "RegisterProjectButton");
            Wait(() => Text(w, "RegistrationStatus").StartsWith("登録完了"));
            Assert.That(Text(w, "ProjectSummary"), Does.Contain("項目 101").And.Contain("Issue 101"));
            var rows = Element(w, "ProjectItems").AsListBox().Items;
            Assert.That(rows[0].Name, Does.Contain("sample-user/first"));
            Assert.That(rows[1].Name, Does.Contain("sample-user/second"));
            Element(w, "ProjectItems").Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
            Wait(() => Element(w, "ProjectItems").AsListBox().Items.Any(i => i.Name.Contains("#101")));
            Capture(w, f.Root, "registered");
            AddUrl(w, 1);
            Assert.That(Text(w, "ProjectConfirmation"), Does.Contain("既登録"));
            Invoke(w, "RegisterProjectButton"); Wait(() => Text(w, "RegistrationStatus").Contains("既に登録"));
            AddUrl(w, 2); Invoke(w, "RegisterProjectButton"); Wait(() => Text(w, "RegistrationStatus").StartsWith("登録完了"));
            Assert.That(Directory.GetFiles(f.Data, "*.json"), Has.Length.EqualTo(2));
        });
        var calls = f.Calls().Length;
        f.Run(w =>
        {
            Invoke(w, "ProjectsPageButton");
            Wait(() => Element(w, "SavedProfiles").AsComboBox().Items.Length == 1);
            Element(w, "SavedProfiles").AsComboBox().Select(0);
            var entry = Retry.WhileNull(() => w.FindFirstDescendant(cf => cf.ByName("Project 1")), TimeSpan.FromSeconds(5)).Result;
            Assert.That(entry, Is.Not.Null); entry!.Click();
            Wait(() => Text(w, "ProjectSummary").Contains("項目 101"));
            Assert.That(Element(w, "DefaultRepository").AsTextBox().Text, Is.EqualTo("sample-user/first"));
            Assert.That(Text(w, "WorkspaceIdentity"), Does.Contain("未認証"));
            Assert.That(Element(w, "RefreshProjectButton").IsEnabled, Is.False);
            Assert.That(f.Calls(), Has.Length.EqualTo(calls), "Startup/profile/navigation must use only the cache.");
            Invoke(w, "UnregisterProjectButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("LocalUnregisterConfirmation")) is not null);
            var dialog = Element(w, "LocalUnregisterConfirmation");
            var confirm = Retry.WhileNull(() => w.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton")), TimeSpan.FromSeconds(5)).Result;
            Assert.That(confirm, Is.Not.Null); confirm!.AsButton().Invoke();
            Wait(() => Text(w, "RegistrationStatus").Contains("解除しました"));
            Assert.That(Element(w, "DefaultRepository").AsTextBox().Text, Is.Empty);
            Assert.That(Element(w, "DefaultRepository").IsEnabled, Is.False);
            Assert.That(Directory.GetFiles(f.Data, "*.json"), Has.Length.EqualTo(1));
            Assert.That(f.Calls(), Has.Length.EqualTo(calls));
            Capture(w, f.Root, "local-removal");
        });
        Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }

    [Test]
    public void CancelFirstRetrievalDoesNotRegister()
    {
        using var f = new Fixture();
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); AddUrl(w, 2); f.Write(10000);
            Invoke(w, "RegisterProjectButton"); Wait(() => Text(w, "RegistrationStatus").Contains("fields"));
            Invoke(w, "CancelProjectButton"); Wait(() => Text(w, "RegistrationStatus").Contains("キャンセル"));
            Assert.That(Directory.Exists(f.Data) ? Directory.GetFiles(f.Data, "*.json").Length : 0, Is.Zero);
        });
    }

    [Test]
    public void NormalCloseDuringProjectRetrievalStopsOwnedWork()
    {
        using var f = new Fixture();
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); AddUrl(w, 2); f.Write(10000);
            Invoke(w, "RegisterProjectButton"); Wait(() => Text(w, "RegistrationStatus").Contains("fields"));
            w.Close();
        }, alreadyClosed: true);
        Assert.That(Directory.Exists(f.Data) ? Directory.GetFiles(f.Data, "*.json").Length : 0, Is.Zero);
    }
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
    private static AutomationElement Element(Window w, string id) => Retry.WhileNull(() => w.FindFirstDescendant(cf => cf.ByAutomationId(id)), TimeSpan.FromSeconds(5)).Result
        ?? throw new AssertionException("Missing " + id);
    private static string Text(Window w, string id) => Element(w, id).Name;
    private static void Invoke(Window w, string id) { Wait(() => Element(w, id).IsEnabled); Element(w, id).AsButton().Invoke(); }
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
