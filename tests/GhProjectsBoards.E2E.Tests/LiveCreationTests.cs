using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

[TestFixture, Category("LiveGitHub"), NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class LiveCreationTests
{
    private const string Project = "PVT_kwHOBGPKL84BjFYc", Repository = "R_kgDOUVKgAw";
    private const string SnapshotQuery = "query{repository(owner:\"fukuda-yuki\",name:\"codex-sandbox\"){id issues(first:100){totalCount pageInfo{hasNextPage} nodes{id number url title body state}}} user(login:\"fukuda-yuki\"){projectV2(number:3){id fields(first:100){pageInfo{hasNextPage} nodes{... on ProjectV2FieldCommon{id name dataType} ... on ProjectV2SingleSelectField{options{id name}}}} items(first:100){pageInfo{hasNextPage} nodes{id isArchived content{... on Issue{id title state}} fieldValues(first:100){pageInfo{hasNextPage} nodes{__typename ... on ProjectV2ItemFieldSingleSelectValue{optionId field{... on ProjectV2FieldCommon{id}}}}}}}}}}";
    private static JsonNode Graph(string query, object? variables = null)
    {
        var start = new ProcessStartInfo("C:\\Program Files\\GitHub CLI\\gh.exe") { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "GH_DEBUG" }) start.Environment.Remove(name);
        foreach (var arg in new[] { "api", "graphql", "--hostname", "github.com", "--input", "-" }) start.ArgumentList.Add(arg);
        using var p = Process.Start(start)!; var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
        p.StandardInput.Write(JsonSerializer.Serialize(new { query, variables = variables ?? new { } })); p.StandardInput.Close();
        Assert.That(p.WaitForExit(60000), Is.True); Assert.That(p.ExitCode, Is.Zero, "Independent gh call failed; inspect fixture before retry.");
        var result = JsonNode.Parse(output.GetAwaiter().GetResult())!; Assert.That(result["errors"], Is.Null); return result["data"]!;
    }
    private static JsonNode Snapshot()
    {
        var data = Graph(SnapshotQuery); var p = data["user"]!["projectV2"]!;
        Assert.That(data["repository"]!["id"]!.ToString(), Is.EqualTo(Repository)); Assert.That(p["id"]!.ToString(), Is.EqualTo(Project));
        Assert.That(data["repository"]!["issues"]!["pageInfo"]!["hasNextPage"]!.GetValue<bool>(), Is.False);
        Assert.That(p["items"]!["pageInfo"]!["hasNextPage"]!.GetValue<bool>(), Is.False);
        Assert.That(p["fields"]!["pageInfo"]!["hasNextPage"]!.GetValue<bool>(), Is.False);
        Assert.That(p["items"]!["nodes"]!.AsArray().All(i => !i!["fieldValues"]!["pageInfo"]!["hasNextPage"]!.GetValue<bool>()), Is.True);
        return data;
    }
    [Test]
    public void OrdinaryProductCreatesTwoAndReconcilesInterruptedThirdWithoutReplay()
    {
        var root = Environment.GetEnvironmentVariable("GHPB_CREATION_LIVE_ROOT"); if (root is null) Assert.Ignore("Run Test-CreationLive.ps1.");
        var marker = File.ReadAllText(Path.Combine(root!, "marker.txt")); var data = Path.Combine(root!, "product-data");
        var baseline = Snapshot(); File.WriteAllText(Path.Combine(root!, "baseline.json"), baseline.ToJsonString());
        using var dpi = new DesktopDpiScope(); using var automation = new UIA3Automation();
        Process? process = null; Application? app = null; Window? window = null; var cleanupVerified = false;
        try
        {
            Launch(); var w = window!;
            E("ExecutablePath").AsTextBox().Text = Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!;
            E("HostInput").AsTextBox().Text = "github.com"; Click("CheckConnectionButton");
            Wait(() => E("ConnectionStatus").Name.Contains("接続を確認しました")); Click("ProjectsPageButton"); Click("AddProjectButton");
            E("RegistrationUrl").AsTextBox().Text = "https://github.com/users/fukuda-yuki/projects/3";
            Click("ResolveProjectButton"); Wait(() => E("RegisterProjectButton").IsEnabled);
            E("InitialDefaultRepository").AsTextBox().Text = "fukuda-yuki/codex-sandbox"; Click("RegisterProjectButton");
            Wait(() => WorkspaceUi.ProjectInformation(w).Contains(Project));
            var initialCount = baseline["user"]!["projectV2"]!["items"]!["nodes"]!.AsArray().Count;
            Add(initialCount, marker + " First", false); Add(initialCount + 1, marker + " Second", true);
            RunApply();
            var creation = Checkpoint().GetProperty("Journal")[0].GetProperty("Creations");
            Assert.That(creation.GetArrayLength(), Is.EqualTo(2)); Assert.That(creation.EnumerateArray().All(c => c.GetProperty("Completed").GetBoolean()), Is.True);
            VerifySetup();
            Add(initialCount + 2, marker + " Interrupted", false); RunApply();
            var interrupted = Checkpoint().GetProperty("Journal")[1].GetProperty("Creations")[0];
            Assert.That(interrupted.GetProperty("Dispatched").GetBoolean(), Is.True); Assert.That(interrupted.GetProperty("ReceivedId").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Close(); Launch(); w = window!;
            E("ExecutablePath").AsTextBox().Text = Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!;
            E("HostInput").AsTextBox().Text = "github.com"; Click("CheckConnectionButton"); Wait(() => E("ConnectionStatus").Name.Contains("接続を確認しました"));
            Click("ProjectsPageButton"); var projectTitle = baseline["user"]!["projectV2"]!["id"]!.ToString();
            var savedTitle = Checkpoint().GetProperty("Registrations")[0].GetProperty("Snapshot").GetProperty("Title").GetString()!;
            Wait(() => w.FindFirstDescendant(cf => cf.ByName(savedTitle)) is not null); w.FindFirstDescendant(cf => cf.ByName(savedTitle))!.Click();
            Wait(() => WorkspaceUi.ProjectInformation(w).Contains(Project));
            var independent = Snapshot()["repository"]!["issues"]!["nodes"]!.AsArray().Single(i => i!["title"]!.ToString() == marker + " Interrupted")!;
            Click("ApplyHistoryButton"); Click("ResolveCreation-" + interrupted.GetProperty("Id").GetString());
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("CreationResolutionDialog")) is not null);
            E("CreationBindUrl").AsTextBox().Text = independent["url"]!.ToString(); Click("PrimaryButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("CreationBindingConfirmDialog")) is not null); Click("PrimaryButton");
            Wait(() => E("RegistrationStatus").Name.Contains("関連付けを保存")); Click("ApplyHistoryButton");
            Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyHistoryDialog")) is not null); Click("PrimaryButton");
            Wait(() => E("RegistrationStatus").Name.Contains("Apply処理を停止")); ReconcileMembership(); VerifySetup();
            Assert.That(Checkpoint().GetProperty("Journal")[1].GetProperty("Creations")[0].GetProperty("EarlierUncertain").GetBoolean(), Is.True);
            var completed = Checkpoint().GetProperty("Journal").EnumerateArray().SelectMany(b => b.GetProperty("Creations").EnumerateArray()).ToArray();
            Assert.That(completed.Length, Is.EqualTo(3)); Assert.That(completed.All(c => c.GetProperty("Completed").GetBoolean()), Is.True);
            Assert.That(completed.Select(c => c.GetProperty("Verified").GetProperty("Id").GetString()).Distinct().Count(), Is.EqualTo(3));
            Assert.That(Snapshot()["repository"]!["issues"]!["nodes"]!.AsArray().Count(i => i!["title"]!.ToString().StartsWith(marker, StringComparison.Ordinal)), Is.EqualTo(3));
            var mutationLines = File.ReadAllLines(Path.Combine(root!, "product-intents.jsonl"));
            Assert.That(mutationLines.Count(line => line.Contains("CreateWorkspaceIssue")), Is.EqualTo(3));
            var projectAdds = mutationLines.Count(line => line.Contains("AddWorkspaceIssue"));
            Assert.That(projectAdds, Is.EqualTo(completed.Count(c => c.GetProperty("MembershipDispatched").GetBoolean())));
            var fieldWrites = completed.SelectMany(c => c.GetProperty("Fields").EnumerateArray()).Sum(f => f.GetProperty("Attempts").GetArrayLength());
            Assert.That(mutationLines.Length, Is.EqualTo(3 + projectAdds + fieldWrites));
            Close(); var intentsBefore = File.ReadAllLines(Path.Combine(root!, "product-intents.jsonl")).Length;
            Launch(); Click("ProjectsPageButton"); WorkspaceUi.OpenProjectNavigation(window!);
            WorkspaceUi.SelectCombo(window!, "SavedProfiles", 0);
            WorkspaceUi.OpenProjectNavigation(window!);
            Wait(() => WorkspaceUi.ProjectNavigation(window!).FindFirstDescendant(cf => cf.ByName(savedTitle)) is not null);
            WorkspaceUi.ProjectNavigation(window!).FindFirstDescendant(cf => cf.ByName(savedTitle))!.Click();
            Wait(() => WorkspaceUi.ProjectInformation(w).Contains(Project)); Click("ApplyHistoryButton");
            Wait(() => window!.FindFirstDescendant(cf => cf.ByAutomationId("ApplyHistoryDialog")) is not null);
            Click("CloseButton"); Close();
            Assert.That(File.ReadAllLines(Path.Combine(root!, "product-intents.jsonl")).Length, Is.EqualTo(intentsBefore));
            File.WriteAllText(Path.Combine(root!, "product-evidence.json"), JsonSerializer.Serialize(new { creations = 3, projectAdds, membershipsObservedWithoutProductAdd = 3 - projectAdds, fieldWrites,
                productMutations = mutationLines.Length, verified = true, interruptedRecoveredByPublicUrlBinding = true, reopenedWithoutReplay = true }));
        }
        finally
        {
            if (process is not null && !process.HasExited) { process.Kill(true); process.WaitForExit(10000); }
            app?.Dispose(); process?.Dispose();
            var now = Snapshot(); var owned = now["repository"]!["issues"]!["nodes"]!.AsArray().Where(i => i!["title"]!.ToString().StartsWith(marker, StringComparison.Ordinal)).ToArray();
            foreach (var issue in owned)
            {
                var id = issue!["id"]!.ToString();
                Assert.That(baseline["repository"]!["issues"]!["nodes"]!.AsArray().Any(i => i!["id"]!.ToString() == id), Is.False);
                Graph("mutation($input:DeleteIssueInput!){deleteIssue(input:$input){clientMutationId}}", new { input = new { issueId = id } });
            }
            var after = Snapshot(); Assert.That(JsonNode.DeepEquals(baseline, after), Is.True, "Pre-existing sandbox data must be preserved after cleanup.");
            cleanupVerified = true; File.WriteAllText(Path.Combine(root!, "cleanup.json"), JsonSerializer.Serialize(new { cleanupVerified, removed = owned.Length, preservedBaseline = true }));
        }
        void Launch()
        {
            var exe = Environment.GetEnvironmentVariable("GHPB_E2E_APP_PATH")!;
            process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)!,
                Environment = { ["GHPB_DATA_ROOT"] = data, ["GHPB_CREATION_PROXY"] = root! } })!;
            app = Application.Attach(process.Id); window = app.GetMainWindow(automation, TimeSpan.FromSeconds(20))!;
            WinUiProcess.AssertRuntime(process); Keyboard.TypeVirtualKeyCode(0x12); window.SetForeground();
        }
        void Close() { window!.Close(); Wait(() => process!.HasExited); Assert.That(process!.ExitCode, Is.Zero); app!.Dispose(); process.Dispose(); process = null; app = null; }
        AutomationElement E(string id) => WorkspaceUi.Element(window!, id);
        void Click(string id) => WorkspaceUi.Invoke(window!, id, TimeSpan.FromSeconds(120));
        void Wait(Func<bool> condition) => Assert.That(Retry.WhileFalse(condition, TimeSpan.FromSeconds(120), TimeSpan.FromMilliseconds(200)).Result, Is.True);
        JsonElement Checkpoint() { using var doc = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(Path.Combine(data, "Drafts"), "*.json").Single())); return doc.RootElement.Clone(); }
        void Add(int row, string title, bool clear)
        {
            Click("GridAddRow"); Wait(() => window!.FindFirstDescendant(cf => cf.ByAutomationId($"GridCell{row}_0")) is not null);
            var cell = E($"GridCell{row}_0").AsTextBox(); cell.Click(); cell.Text = title; Keyboard.Type(VirtualKeyShort.RETURN);
            if (clear) { E($"GridCell{row}_1").Focus(); Click("GridClear"); } else WorkspaceUi.SelectLastChoice(window!, $"GridCell{row}_1");
        }
        void RunApply()
        {
            Click("ReviewApplyButton"); var list = E("ApplyTargetRows").AsListBox();
            foreach (var target in list.Items.Where(i => i.Name.StartsWith("新規作成") && i.Name.Contains(marker))) target.AddToSelection();
            Click("PrimaryButton"); Wait(() => window!.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is not null);
            Assert.That(E("PrimaryButton").IsEnabled, Is.True); Click("PrimaryButton"); Wait(() => E("RegistrationStatus").Name.Contains("Apply処理を停止"));
            ReconcileMembership();
        }
        void ReconcileMembership()
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var pending = Checkpoint().GetProperty("Journal").EnumerateArray().Last().GetProperty("Creations").EnumerateArray().Where(c => !c.GetProperty("Completed").GetBoolean()).ToArray();
                if (pending.Length == 0 || pending.Any(c => c.GetProperty("Verified").ValueKind == JsonValueKind.Null || c.GetProperty("Fields").ValueKind != JsonValueKind.Null)) return;
                Click("ApplyHistoryButton"); Wait(() => window!.FindFirstDescendant(cf => cf.ByAutomationId("ApplyHistoryDialog")) is not null);
                Click("PrimaryButton"); Wait(() => E("RegistrationStatus").Name.Contains("Apply処理を停止"));
            }
        }
        void VerifySetup()
        {
            var remote = Snapshot();
            foreach (var c in Checkpoint().GetProperty("Journal").EnumerateArray().SelectMany(b => b.GetProperty("Creations").EnumerateArray()))
            {
                if (!c.GetProperty("Completed").GetBoolean()) continue;
                var id = c.GetProperty("Verified").GetProperty("Id").GetString();
                var issue = remote["repository"]!["issues"]!["nodes"]!.AsArray().Single(i => i!["id"]!.ToString() == id)!;
                Assert.That(issue["title"]!.ToString(), Is.EqualTo(c.GetProperty("Title").GetString())); Assert.That(issue["body"]!.ToString(), Is.Empty);
                var item = remote["user"]!["projectV2"]!["items"]!["nodes"]!.AsArray().Single(i => i!["content"]?["id"]?.ToString() == id)!;
                Assert.That(item["id"]!.ToString(), Is.EqualTo(c.GetProperty("ItemId").GetString()));
                foreach (var field in c.GetProperty("Fields").EnumerateArray())
                {
                    var fieldId = field.GetProperty("Key").GetProperty("FieldId").GetString();
                    var actual = item["fieldValues"]!["nodes"]!.AsArray().SingleOrDefault(v => v!["field"]?["id"]?.ToString() == fieldId);
                    Assert.That(actual?["optionId"]?.ToString(), Is.EqualTo(field.GetProperty("Intended").GetProperty("Value").GetString()));
                }
            }
        }
    }
}
