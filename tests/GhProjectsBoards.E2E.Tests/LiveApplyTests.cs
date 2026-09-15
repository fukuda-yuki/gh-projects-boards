using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

[TestFixture, Category("LiveGitHub"), NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class LiveApplyTests
{
    [Test]
    public void OrdinaryProductAppliesDisposableTitleSelectAndClear()
    {
        var manifest = Environment.GetEnvironmentVariable("GHPB_APPLY_FIXTURE");
        if (manifest is null) Assert.Ignore("Run Test-ApplyLive.ps1 with a run-owned fixture.");
        using var fixture = JsonDocument.Parse(File.ReadAllText(manifest!)); var f = fixture.RootElement;
        var issueId = f.GetProperty("issue").GetString()!; var itemId = f.GetProperty("item").GetString()!;
        var marker = f.GetProperty("marker").GetString()!;
        Assert.That(f.GetProperty("project").GetString(), Is.EqualTo("PVT_kwHOBGPKL84BjFYc"));
        Assert.That(f.GetProperty("repository").GetString(), Is.EqualTo("R_kgDOUVKgAw"));
        var root = Path.GetDirectoryName(manifest!)!; var data = Path.Combine(root, "product-data");
        using var dpi = new DesktopDpiScope();
        var exe = Environment.GetEnvironmentVariable("GHPB_E2E_APP_PATH")!;
        using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)!, Environment = { ["GHPB_DATA_ROOT"] = data } })!;
        using var automation = new UIA3Automation(); using var app = Application.Attach(process.Id);
        var w = app.GetMainWindow(automation, TimeSpan.FromSeconds(20)) ?? throw new AssertionException("Missing main window"); WinUiProcess.AssertRuntime(process);
        try
        {
            Keyboard.TypeVirtualKeyCode(0x12); w.SetForeground();
            E("ExecutablePath").AsTextBox().Text = "C:\\Program Files\\GitHub CLI\\gh.exe";
            E("HostInput").AsTextBox().Text = "github.com"; Click("CheckConnectionButton");
            Wait(() => E("ConnectionStatus").Name.Contains("接続を確認しました"));
            Click("ProjectsPageButton"); Click("AddProjectButton");
            E("RegistrationUrl").AsTextBox().Text = "https://github.com/users/fukuda-yuki/projects/3";
            Click("ResolveProjectButton"); Wait(() => E("RegisterProjectButton").IsEnabled); Click("RegisterProjectButton");
            Wait(() => WorkspaceUi.ProjectInformation(w).Contains("PVT_kwHOBGPKL84BjFYc"));
            var titleCell = w.FindAllDescendants().Single(e => Regex.IsMatch(e.Properties.AutomationId.ValueOrDefault ?? "", "^GridCell[0-9]+_0$") && e.AsTextBox().Text == marker + " A");
            var row = int.Parse(Regex.Match(titleCell.AutomationId, "[0-9]+").Value);
            var initialOption = VerifyRemote(marker + " A", null, false);
            titleCell.Click(); titleCell.AsTextBox().Text = marker + " B"; Keyboard.Type(VirtualKeyShort.RETURN);
            Wait(() => E("DraftStatus").Name.Contains("変更フィールド 1"));
            VerifyRemote(marker + " A", initialOption);
            Click("RefreshProjectButton"); Wait(() => E("RegistrationStatus").Name.Contains("照合をローカル保存"));
            VerifyRemote(marker + " A", initialOption);
            Assert.That(Checkpoint().GetProperty("Journal").GetArrayLength(), Is.Zero);
            RunApply();
            VerifyRemote(marker + " B", initialOption);
            var combo = E($"GridCell{row}_1").AsComboBox(); combo.Select(combo.SelectedItem?.Text == combo.Items[0].Text ? 1 : 0);
            Wait(() => E("DraftStatus").Name.Contains("変更フィールド 1")); RunApply();
            var journal = Checkpoint().GetProperty("Journal").EnumerateArray().Last().GetProperty("Operations")[0];
            var option = journal.GetProperty("Intended").GetProperty("Value").GetString();
            VerifyRemote(marker + " B", option);
            E($"GridCell{row}_1").Focus(); Click("GridClear");
            Wait(() => E("DraftStatus").Name.Contains("変更フィールド 1")); RunApply(); VerifyRemote(marker + " B", null);
            var operations = Checkpoint().GetProperty("Journal").EnumerateArray().SelectMany(b => b.GetProperty("Operations").EnumerateArray()).ToArray();
            Assert.That(operations.Length, Is.EqualTo(3));
            Assert.That(operations.All(o => o.GetProperty("Attempts").GetArrayLength() == 1 && o.GetProperty("State").GetInt32() == 2), Is.True);
            File.WriteAllText(Path.Combine(root, "product-evidence.json"), JsonSerializer.Serialize(new { issueId, itemId, productMutations = 3, fixtureSetupMutations = 2, verified = true }));
            w.Close(); Wait(() => process.HasExited); Assert.That(process.ExitCode, Is.Zero);
            using var reopened = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)!, Environment = { ["GHPB_DATA_ROOT"] = data } })!;
            using var reopenedApp = Application.Attach(reopened.Id);
            try
            {
                w = reopenedApp.GetMainWindow(automation, TimeSpan.FromSeconds(20)) ?? throw new AssertionException("Missing reopened window");
                WinUiProcess.AssertRuntime(reopened); Keyboard.TypeVirtualKeyCode(0x12); w.SetForeground(); Click("ProjectsPageButton");
                WorkspaceUi.OpenProjectNavigation(w);
                WorkspaceUi.SelectCombo(w, "SavedProfiles", 0);
                var projectTitle = Checkpoint().GetProperty("Registrations")[0].GetProperty("Snapshot").GetProperty("Title").GetString()!;
                WorkspaceUi.OpenProjectNavigation(w);
                Wait(() => WorkspaceUi.ProjectNavigation(w).FindFirstDescendant(cf => cf.ByName(projectTitle)) is not null);
                WorkspaceUi.ProjectNavigation(w).FindFirstDescendant(cf => cf.ByName(projectTitle))!.Click();
                Wait(() => E($"GridCell{row}_0").AsTextBox().Text == marker + " B"); VerifyRemote(marker + " B", null);
                Assert.That(Checkpoint().GetProperty("Journal").GetArrayLength(), Is.EqualTo(3));
                w.Close(); Wait(() => reopened.HasExited); Assert.That(reopened.ExitCode, Is.Zero);
            }
            finally { if (!reopened.HasExited) { reopened.Kill(true); reopened.WaitForExit(10000); } }
        }
        finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(10000); } }
        AutomationElement E(string id) => WorkspaceUi.Element(w, id);
        void Click(string id) => WorkspaceUi.Invoke(w, id);
        void Wait(Func<bool> check) => Assert.That(Retry.WhileFalse(check, TimeSpan.FromSeconds(90), TimeSpan.FromMilliseconds(200)).Result, Is.True);
        JsonElement Checkpoint() => JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(Path.Combine(data, "Drafts"), "*.json").Single())).RootElement.Clone();
        void RunApply()
        {
            Click("ReviewApplyButton"); var targets = E("ApplyTargetRows").AsListBox();
            targets.Items.Single(i => i.Name.Contains(itemId)).Select(); Click("PrimaryButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is not null);
            Click("PrimaryButton"); Wait(() => E("RegistrationStatus").Name.Contains("Apply処理を停止"));
            Assert.That(E("DraftStatus").Name, Does.Contain("変更フィールド 0"));
        }
        string? VerifyRemote(string title, string? option, bool compare = true)
        {
            var start = new ProcessStartInfo("C:\\Program Files\\GitHub CLI\\gh.exe") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "GH_DEBUG" }) start.Environment.Remove(name);
            foreach (var arg in new[] { "api", "graphql", "--hostname", "github.com", "--input", "-" }) start.ArgumentList.Add(arg);
            using var read = Process.Start(start)!;
            read.StandardInput.Write(JsonSerializer.Serialize(new { query = "query($i:ID!,$t:ID!){issue:node(id:$i){... on Issue{id title}} item:node(id:$t){... on ProjectV2Item{id fieldValues(first:100){nodes{... on ProjectV2ItemFieldSingleSelectValue{optionId}} pageInfo{hasNextPage}}}}}", variables = new { i = issueId, t = itemId } })); read.StandardInput.Close();
            var output = read.StandardOutput.ReadToEndAsync(); var error = read.StandardError.ReadToEndAsync(); read.WaitForExit();
            Assert.That(read.ExitCode, Is.Zero); using var response = JsonDocument.Parse(output.GetAwaiter().GetResult());
            Assert.That(response.RootElement.TryGetProperty("errors", out _), Is.False);
            var result = response.RootElement.GetProperty("data"); Assert.That(result.GetProperty("issue").GetProperty("title").GetString(), Is.EqualTo(title));
            var values = result.GetProperty("item").GetProperty("fieldValues"); Assert.That(values.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean(), Is.False);
            var options = values.GetProperty("nodes").EnumerateArray().Where(n => n.TryGetProperty("optionId", out _)).Select(n => n.GetProperty("optionId").GetString()).ToArray();
            if (compare) Assert.That(options, Is.EqualTo(option is null ? Array.Empty<string>() : new[] { option }));
            return options.SingleOrDefault();
        }
    }
}
